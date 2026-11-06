using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OneDragonLauncherWpf.Services
{
    /// <summary>
    /// 启动 Python 引导流程，收集双路输出并发布阶段和错误通知。
    /// </summary>
    public sealed class PythonRunner : IDisposable
    {
        private readonly LauncherLog log;
        private Process process;
        private int reportedError;
        /// <summary>
        /// 输出中识别到启动阶段时触发；订阅者需自行切换到界面线程。
        /// </summary>
        public event Action<LaunchStage> StageChanged;
        /// <summary>
        /// 发布已清理控制字符的后端输出；订阅者需自行切换到界面线程。
        /// </summary>
        public event Action<string> OutputReceived;
        /// <summary>
        /// 首次识别到错误日志时触发，后续错误不重复通知。
        /// </summary>
        public event Action ErrorDetected;
        /// <summary>
        /// 启动后等待进程退出及双路输出排空，并返回退出码的任务。
        /// </summary>
        public Task<int> Completion { get; private set; }
        /// <summary>
        /// 已启动的 Python 或 uv 进程标识，用于监视其后代窗口。
        /// </summary>
        public int ProcessId { get { return process.Id; } }
        /// <summary>
        /// 表示输出中是否曾识别到错误，支持跨线程读取。
        /// </summary>
        public bool HasError { get { return Volatile.Read(ref reportedError) != 0; } }
        private static readonly Regex ErrorPattern = new Regex(
            @"\bERROR\b|\bCRITICAL\b|Traceback \(most recent call last\)|\b\w*(?:Error|Exception):", RegexOptions.Compiled);

        // 在现有 Python 进程内读取管道，所有后代进程继承这些本地管道，
        // 不直接继承启动器的管道；启动器退出后继续读取并丢弃输出。
        /// <summary>
        /// 在 Python 进程内转发标准输出和错误输出；WPF 退出后继续排空并丢弃输出，避免后端阻塞。
        /// </summary>
        public static readonly string GuiOutputBridge = EmbeddedPythonScript.Read("GuiOutputBridge");

        // 两种启动入口都需在导入 Python 启动模块前执行此兼容处理。
        // 仅在确实缺少 colorama 时注入替代模块。
        /// <summary>
        /// 仅在缺少 colorama 时注入最小兼容模块，不覆盖已安装模块或掩盖其内部依赖错误。
        /// </summary>
        public static readonly string ColoramaBootstrap = EmbeddedPythonScript.Read("ColoramaBootstrap");

        /// <summary>
        /// 在当前 Python 进程内设置启动器版本，与 --version 保持一致。
        /// </summary>
        public static readonly string VersionBootstrap = EmbeddedPythonScript.Read("VersionBootstrap")
            .Replace("__LAUNCHER_VERSION__", LauncherVersion.Value);

        /// <summary>
        /// 配置输出转发，尝试清理 Codex tree 引用并兼容 colorama，再调用 GUI 业务入口。
        /// </summary>
        public static readonly string GuiBootstrap = GuiOutputBridge + GitCheckpointCleanup.Script + ColoramaBootstrap + VersionBootstrap +
            EmbeddedPythonScript.Read("GuiBootstrap");

        // 启动器已完成参数校验和提权，因此跳过 pyuac 的可执行文件包装入口。
        // GetPythonArguments 始终将启动器专用的 --onedragon 参数放在首位。
        /// <summary>
        /// 执行命令行引导并透传后端参数；提权由 WPF 完成，不再经过 pyuac 包装入口。
        /// </summary>
        public static readonly string CliBootstrap = GitCheckpointCleanup.Script + ColoramaBootstrap + VersionBootstrap +
            EmbeddedPythonScript.Read("CliBootstrap");

        /// <summary>
        /// 创建使用指定日志缓冲的进程执行器。
        /// </summary>
        /// <param name="log">用于记录启动信息及进程输出的共享日志。</param>
        public PythonRunner(LauncherLog log) { this.log = log; }

        /// <summary>
        /// 构造 Python 或 uv 的引导命令、工作目录及 UTF-8 输出环境。
        /// </summary>
        /// <param name="environment">已解析并检查的目标项目环境。</param>
        /// <param name="options">决定 GUI 或命令行模式的启动选项。</param>
        /// <returns>启用输入输出重定向且不创建控制台窗口的进程配置。</returns>
        /// <remarks>只设置子进程环境，不改写安装器维护的后端配置。</remarks>
        public static ProcessStartInfo CreateStartInfo(LaunchEnvironment environment, LauncherOptions options)
        {
            var args = new List<string>();
            if (environment.UseUv) args.AddRange(new[] { "run", "--frozen", "python" });
            args.Add("-u");
            if (options.OneDragon)
            {
                args.AddRange(new[] { "-c", CliBootstrap });
                args.AddRange(options.GetPythonArguments());
            }
            else args.AddRange(new[] { "-c", GuiBootstrap });
            var info = new ProcessStartInfo(environment.Executable, CommandLine.Join(args))
            {
                WorkingDirectory = environment.ProjectDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            string inheritedPath = Environment.GetEnvironmentVariable("PYTHONPATH");
            info.EnvironmentVariables["PYTHONPATH"] = environment.SourceDirectory +
                (string.IsNullOrEmpty(inheritedPath) ? "" : ";" + inheritedPath);
            info.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            info.EnvironmentVariables["PYTHONUTF8"] = "1";
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            return info;
        }

        /// <summary>
        /// 启动后端并异步读取标准输出和错误输出，关闭输入以避免隐藏终端等待交互。
        /// </summary>
        /// <param name="environment">目标项目及本地执行器。</param>
        /// <param name="options">本次启动选项。</param>
        /// <exception cref="InvalidOperationException">无法创建后端子进程。</exception>
        /// <remarks>每个执行器实例只启动一次；调用成功后可读取 Completion。</remarks>
        public void Start(LaunchEnvironment environment, LauncherOptions options)
        {
            log.Write("项目目录: " + environment.ProjectDirectory);
            log.Write("Python 源码: " + environment.SourceDirectory);
            log.Write("使用执行器: " + environment.Executable);
            log.Write(options.OneDragon ? "启动一条龙命令行模式..." : "后台启动 Python 启动器与 GUI 界面 (piped=True)...");
            process = new Process { StartInfo = CreateStartInfo(environment, options) };
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnOutput;
            if (!process.Start()) throw new InvalidOperationException("无法创建 Python 子进程。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            // 图形启动器没有可供 input() 交互的控制台，关闭输入以返回文件结束标志，避免阻塞。
            process.StandardInput.Close();
            Completion = Task.Run(() =>
            {
                // 不带参数的 WaitForExit 还会等待标准输出和错误输出的异步读取全部完成。
                process.WaitForExit();
                int exitCode = process.ExitCode;
                log.Write("Python 进程已退出，退出码: " + exitCode);
                return exitCode;
            });
        }

        /// <summary>
        /// 处理一行输出，记录日志、识别阶段，并确保错误通知只触发一次。
        /// </summary>
        private void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null) return;
            string clean = LauncherLog.Clean(args.Data);
            log.Write(clean);
            OutputReceived?.Invoke(clean);
            LaunchStage stage = LaunchStage.FromLog(clean);
            if (stage != null) StageChanged?.Invoke(stage);
            if (ErrorPattern.IsMatch(clean) && Interlocked.Exchange(ref reportedError, 1) == 0) ErrorDetected?.Invoke();
        }

        /// <summary>
        /// 释放进程对象持有的本地资源，不主动终止仍在运行的 Python 后端。
        /// </summary>
        public void Dispose() { process?.Dispose(); }
    }
}
