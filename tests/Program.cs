using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using OneDragonLauncherWpf.Services;
using OneDragonLauncherWpf.ViewModels;

internal static class Program
{
    private static int passed;
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--job-leaf")
        {
            System.Threading.Thread.Sleep(60000);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--job-tree")
        {
            using (var child = Process.Start(new ProcessStartInfo(typeof(Program).Assembly.Location, "--job-leaf")
                { UseShellExecute = false, CreateNoWindow = true }))
            {
                Console.WriteLine(child.Id);
                System.Threading.Thread.Sleep(60000);
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "--job-owner")
        {
            using (var owned = new StartupProcess(JobStartInfo(), line => Console.WriteLine(line)))
            {
                Console.Error.WriteLine(owned.Id);
                System.Threading.Thread.Sleep(60000);
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "-u")
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.WriteLine("当前工作目录: " + Environment.CurrentDirectory);
            Console.WriteLine("开始配置环境变量");
            Console.WriteLine("\x1b[32m正在检查代码更新\x1b[0m");
            for (int i = 0; i < 300; i++) { Console.WriteLine("标准输出 中文 " + i); Console.Error.WriteLine("标准错误 中文 " + i); }
            Console.Error.WriteLine("RuntimeError: 模拟启动失败");
            Console.WriteLine("最后一行：输出已完整读取");
            return 7;
        }

        string temporary = Path.Combine(Path.GetTempPath(), "OneDragon WPF tests 中文 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Check("Job 取消启动时终止父进程和后代", () => CheckJobLifetime(false));
            Check("Job 就绪交接后释放句柄保留父进程和后代", () => CheckJobLifetime(true));
            Check("启动器异常退出时 Job 自动终止整个进程树", () =>
            {
                var info = JobStartInfo();
                info.Arguments = "--job-owner";
                info.RedirectStandardOutput = info.RedirectStandardError = true;
                using (var owner = Process.Start(info))
                {
                    Process root = null, child = null;
                    try
                    {
                        var rootLine = owner.StandardError.ReadLineAsync();
                        var childLine = owner.StandardOutput.ReadLineAsync();
                        Assert(Task.WaitAll(new Task[] { rootLine, childLine }, 10000));
                        root = Process.GetProcessById(int.Parse(rootLine.Result));
                        child = Process.GetProcessById(int.Parse(childLine.Result));
                        owner.Kill();
                        Assert(owner.WaitForExit(10000) && root.WaitForExit(10000) && child.WaitForExit(10000));
                    }
                    finally
                    {
                        KillFixture(child); KillFixture(root); KillFixture(owner);
                        child?.Dispose(); root?.Dispose();
                    }
                }
            });
            Check("Debug 默认预览，显式项目与 CLI 保留真实启动", () =>
            {
                Assert(LauncherOptions.Parse(new string[0], defaultToPreview: true).Preview);
                var darkPreview = LauncherOptions.Parse(new[] { "--dark" }, defaultToPreview: true);
                Assert(darkPreview.Preview && darkPreview.Dark);
                Assert(!LauncherOptions.Parse(new[] { "--project", temporary }, defaultToPreview: true).Preview);
                Assert(!LauncherOptions.Parse(new[] { "--onedragon" }, defaultToPreview: true).Preview);
                Assert(LauncherOptions.Parse(new[] { "--preview", "--project", temporary }, defaultToPreview: true).Preview);
                Assert(!LauncherOptions.Parse(new[] { "--help" }, defaultToPreview: true).Preview);
                bool rejected = false;
                try { LauncherOptions.Parse(new[] { "--close-game" }, defaultToPreview: true); }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected);
            });
            Check("Release 默认真实启动，仅显式 preview 才预览", () =>
            {
                Assert(!LauncherOptions.Parse(new string[0]).Preview);
                Assert(!LauncherOptions.Parse(new[] { "--dark" }).Preview);
                Assert(LauncherOptions.Parse(new[] { "--preview" }).Preview);
            });
            Check("CLI 默认关机延迟与实例透传", () =>
            {
                var options = LauncherOptions.Parse(new[] { "-o", "-s", "-i", "1,2", "-c" });
                Assert(options.ShutdownSeconds == 60 && options.Instance == "1,2");
                Assert(options.GetPythonArguments().SequenceEqual(new[] { "--onedragon", "--instance", "1,2", "--close-game", "--shutdown", "60" }));
            });
            Check("CLI 等号参数、零秒和带空格路径", () =>
            {
                var options = LauncherOptions.Parse(new[] { "--onedragon", "--shutdown=0", "--project=" + temporary });
                Assert(options.ShutdownSeconds == 0 && options.ProjectDirectory == temporary);
            });
            Check("CLI 非法组合与缺失参数拒绝", () =>
            {
                Reject(new[] { "--close-game" }); Reject(new[] { "--instance" }); Reject(new[] { "-o", "-s", "abc" });
                Reject(new[] { "-o", "--shutdown=-1" }); Reject(new[] { "--preview", "-o" }); Reject(new[] { "--unknown" });
                Reject(new[] { "--preview=true" }); Reject(new[] { "--project=" });
            });
            Check("CLI 休眠和睡眠可组合关闭游戏与账号实例", () =>
            {
                foreach (string power in new[] { "--hibernate", "--sleep" })
                {
                    var options = LauncherOptions.Parse(new[] { "-o", power, "-c", "-i", "1,2" });
                    Assert(options.Hibernate == (power == "--hibernate") && options.Sleep == (power == "--sleep"));
                    Assert(!options.ShutdownSeconds.HasValue);
                    Assert(options.GetPythonArguments().SequenceEqual(new[] { "--onedragon", "--instance", "1,2", "--close-game", power }));
                    Reject(new[] { power });
                    Reject(new[] { "-o", power + "=true" });
                    Reject(new[] { "-o", power, "60" });
                    Reject(new[] { "--preview", "-o", power });
                }
            });
            Check("CLI 电源动作互斥，包括零秒关机和不同参数顺序", () =>
            {
                string[] powers = { "--shutdown", "--shutdown=0", "-s", "--hibernate", "--sleep" };
                foreach (string first in powers)
                    foreach (string second in powers)
                    {
                        bool firstShutdown = first.StartsWith("--shutdown") || first == "-s";
                        bool secondShutdown = second.StartsWith("--shutdown") || second == "-s";
                        if (first != second && !(firstShutdown && secondShutdown))
                            Reject(new[] { "-o", first, second });
                    }
                Reject(new[] { "-o", "--shutdown", "--hibernate", "--sleep" });
            });
            Check("Windows 参数转义往返（空格、中文、换行、引号、尾斜杠）", () =>
            {
                string[] values = { "", "normal", "中文 路径", @"D:\folder with spaces\", "a\"b", "first\nsecond", "\\\"", "\\\\" };
                Assert(Split("app.exe " + CommandLine.Join(values)).Skip(1).SequenceEqual(values));
            });

            string project = Path.Combine(temporary, "ZenlessZoneZero-OneDragon-main");
            Directory.CreateDirectory(Path.Combine(project, "src", "zzz_od"));
            string venv = Path.Combine(project, ".venv", "Scripts");
            Directory.CreateDirectory(venv);
            File.Copy(typeof(Program).Assembly.Location, Path.Combine(venv, "python.exe"));
            Directory.CreateDirectory(Path.Combine(project, ".install", "uv"));
            File.WriteAllText(Path.Combine(project, ".install", "uv", "uv.exe"), "fixture");
            LaunchEnvironment environment = null;
            Check("源码子目录识别与工作目录定位", () =>
            {
                string binary = Path.Combine(temporary, "launcher", "bin", "Debug");
                Directory.CreateDirectory(binary);
                environment = LaunchEnvironment.Resolve(null, binary, temporary);
                Assert(environment.ProjectDirectory == project && environment.Executable == Path.Combine(venv, "python.exe") && !environment.UseUv);
            });
            Check("显式项目路径优先且无效路径不静默回退", () =>
            {
                Assert(LaunchEnvironment.Resolve(project, temporary, temporary).ProjectDirectory == project);
                bool failed = false;
                try { LaunchEnvironment.Resolve(temporary, project, project); } catch (DirectoryNotFoundException) { failed = true; }
                Assert(failed);
            });
            Check("GUI 命令、UTF-8、PYTHONPATH 与工作目录", () =>
            {
                var info = PythonRunner.CreateStartInfo(environment, LauncherOptions.Parse(new string[0]));
                var arguments = Split("app.exe " + info.Arguments).Skip(1).ToArray();
                Assert(arguments.SequenceEqual(new[] { "-u", "-c", PythonRunner.GuiBootstrap }));
                Assert(info.WorkingDirectory == project && info.EnvironmentVariables["PYTHONUTF8"] == "1");
                Assert(info.EnvironmentVariables["PYTHONPATH"].StartsWith(Path.Combine(project, "src")));
                Assert(info.CreateNoWindow && info.RedirectStandardError && info.RedirectStandardOutput && !info.UseShellExecute);
            });
            Check("CLI 注入引导脚本与参数透传", () =>
            {
                var info = PythonRunner.CreateStartInfo(environment, LauncherOptions.Parse(new[] { "-o", "-i", "1,2", "-s", "90" }));
                Assert(Split("app.exe " + info.Arguments).Skip(1).SequenceEqual(new[] { "-u", "-c", PythonRunner.CliBootstrap, "--onedragon", "--instance", "1,2", "--shutdown", "90" }));
                foreach (string power in new[] { "--hibernate", "--sleep" })
                {
                    var powerInfo = PythonRunner.CreateStartInfo(environment, LauncherOptions.Parse(new[] { "-o", "-c", power }));
                    Assert(Split("app.exe " + powerInfo.Arguments).Skip(1).SequenceEqual(new[] { "-u", "-c", PythonRunner.CliBootstrap, "--onedragon", "--close-game", power }));
                }
            });
            Check("缺少内置 uv 时拒绝 PATH 和启动器目录中的环境", () =>
            {
                string incompleteRoot = Path.Combine(temporary, "incomplete");
                Directory.CreateDirectory(Path.Combine(incompleteRoot, "src", "zzz_od"));
                string externalTools = Path.Combine(temporary, "external-tools");
                Directory.CreateDirectory(externalTools);
                File.WriteAllText(Path.Combine(externalTools, "uv.exe"), "fixture");
                File.WriteAllText(Path.Combine(externalTools, "python.exe"), "fixture");
                string originalPath = Environment.GetEnvironmentVariable("PATH");
                try
                {
                    Environment.SetEnvironmentVariable("PATH", externalTools);
                    bool failed = false;
                    try { LaunchEnvironment.Resolve(incompleteRoot, project, project); }
                    catch (FileNotFoundException exception)
                    {
                        failed = exception.Message.Contains("安装器") &&
                            exception.FileName == Path.Combine(incompleteRoot, ".install", "uv", "uv.exe");
                    }
                    Assert(failed);
                }
                finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
            });
            Check("已有项目 Python 仍必须具备指定位置的内置 uv", () =>
            {
                string incompleteRoot = Path.Combine(temporary, "python-only");
                Directory.CreateDirectory(Path.Combine(incompleteRoot, "src", "zzz_od"));
                Directory.CreateDirectory(Path.Combine(incompleteRoot, ".venv", "Scripts"));
                File.WriteAllText(Path.Combine(incompleteRoot, ".venv", "Scripts", "python.exe"), "fixture");
                Directory.CreateDirectory(Path.Combine(incompleteRoot, ".install"));
                File.WriteAllText(Path.Combine(incompleteRoot, ".install", "uv.exe"), "fixture");
                bool failed = false;
                try { LaunchEnvironment.Resolve(incompleteRoot, project, project); }
                catch (FileNotFoundException exception) { failed = exception.Message.Contains("安装器"); }
                Assert(failed);
            });
            Check("仅使用目标项目的内置 uv，保留 frozen 参数", () =>
            {
                string uvRoot = Path.Combine(temporary, "uv project");
                Directory.CreateDirectory(Path.Combine(uvRoot, "src", "zzz_od"));
                Directory.CreateDirectory(Path.Combine(uvRoot, ".install", "uv"));
                File.WriteAllText(Path.Combine(uvRoot, ".install", "uv", "uv.exe"), "fixture");
                var uv = LaunchEnvironment.Resolve(uvRoot, project, temporary);
                Assert(uv.UseUv && uv.Executable == Path.Combine(uvRoot, ".install", "uv", "uv.exe"));
                var info = PythonRunner.CreateStartInfo(uv, LauncherOptions.Parse(new string[0]));
                Assert(Split("app.exe " + info.Arguments).Skip(1).Take(3).SequenceEqual(new[] { "run", "--frozen", "python" }));
                var cli = PythonRunner.CreateStartInfo(uv, LauncherOptions.Parse(new[] { "-o", "-c", "-s", "0" }));
                Assert(Split("app.exe " + cli.Arguments).Skip(1).SequenceEqual(new[] { "run", "--frozen", "python", "-u", "-c", PythonRunner.CliBootstrap, "--onedragon", "--close-game", "--shutdown", "0" }));
                foreach (string power in new[] { "--hibernate", "--sleep" })
                {
                    var powerInfo = PythonRunner.CreateStartInfo(uv, LauncherOptions.Parse(new[] { "-o", "-c", power }));
                    Assert(Split("app.exe " + powerInfo.Arguments).Skip(1).SequenceEqual(new[] { "run", "--frozen", "python", "-u", "-c", PythonRunner.CliBootstrap, "--onedragon", "--close-game", power }));
                }
            });
            Check("ANSI CSI/OSC 清除与中文保留", () =>
                Assert(LauncherLog.Clean("\x1b[32m中文\x1b[0m\r\x1b]0;title\x07 text") == "中文 text"));
            Check("启动阶段识别", () =>
            {
                Assert(LaunchStage.FromLog("目录核验通过").Phase == LaunchPhase.CheckingDirectory);
                Assert(LaunchStage.FromLog("开始配置环境变量").Phase == LaunchPhase.ConfiguringEnvironment);
                Assert(LaunchStage.FromLog("同步代码仓库").Phase == LaunchPhase.SyncingCode);
                Assert(LaunchStage.FromLog("正在同步运行依赖").Phase == LaunchPhase.CheckingDependencies);
                Assert(LaunchStage.FromLog("等待主界面弹出").Phase == LaunchPhase.StartingGui);
                Assert(LaunchStage.FromLog("普通日志") == null);
            });
            Check("启动阶段不倒退，失败文案不被后续日志覆盖", () =>
            {
                var model = new LauncherViewModel();
                var syncing = LaunchStage.FromLog("同步代码仓库");
                model.Apply(syncing);
                model.Apply(LaunchStage.FromLog("目录核验通过"));
                Assert(model.StatusMessage == syncing.Status && model.StepText == syncing.Step);
                var dependencies = LaunchStage.FromLog("正在同步运行依赖");
                model.Apply(dependencies);
                Assert(model.StatusMessage == dependencies.Status && model.StepText == dependencies.Step && !model.HasFailed);
                model.Fail("启动失败", "失败详情");
                model.Apply(LaunchStage.FromLog("等待主界面弹出"));
                Assert(model.HasFailed && model.StatusMessage == "启动失败" && model.StepText == "失败详情");
            });
            Check("Git 日志实时显示且不覆盖后续阶段或失败信息", () =>
            {
                var model = new LauncherViewModel();
                model.ApplyLog("普通日志");
                Assert(model.StepText == "检查工作目录与 Python 环境...");
                model.ApplyLog("2026-10-29 08:14:59,323 | INFO | 拉取远程代码");
                model.ApplyLog("2026-10-29 08:14:59,323 | INFO | 尝试代码源: GitHub");
                Assert(model.StepText == "尝试代码源: GitHub");
                model.ApplyLog("2026-10-29 08:15:01,062 | INFO | 拉取对象 94/94 (100%), done.");
                Assert(model.StepText == "拉取对象 94/94 (100%), done.");
                Assert(model.StatusMessage == "正在同步代码更新...");
                model.ApplyLog("处理增量 1/50 (2%)");
                model.ApplyLog("  ");
                model.ApplyLog("目录核验通过");
                Assert(model.StepText == "处理增量 1/50 (2%)");
                model.ApplyLog("2026-10-29 08:15:05,603 | WARNING | 暂时无法获取更新, 继续使用当前版本");
                Assert(model.StepText == "暂时无法获取更新, 继续使用当前版本");
                model.ApplyLog("开始检查运行环境...");
                model.ApplyLog("处理增量 50/50 (100%), done.");
                Assert(model.StepText == "同步 Python 运行环境");
                model.Fail("启动失败", "失败详情");
                model.ApplyLog("拉取远程代码");
                Assert(model.StepText == "失败详情" && model.StatusMessage == "启动失败");
            });
            Check("日志并发写入、增量与容量限制", () =>
            {
                var log = new LauncherLog();
                var original = Console.Out;
                try
                {
                    Console.SetOut(TextWriter.Null);
                    log.Write("initial");
                    long cursor = 0, first = 0; bool reset;
                    Assert(log.ReadSince(ref cursor, ref first, out reset).Contains("initial") && !reset);
                    Parallel.For(0, 11000, i => log.Write("行 " + i));
                    string text = log.ReadSince(ref cursor, ref first, out reset);
                    Assert(reset && text.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length == 10000);
                    Assert(log.ReadSince(ref cursor, ref first, out reset) == "");
                    log.Write("new line after trimming");
                    Assert(log.ReadSince(ref cursor, ref first, out reset).Contains("new line after trimming") && reset);
                }
                finally { Console.SetOut(original); }
            });
            Check("真实子进程管道：中文双流、尾部排空、错误一次通知与退出码", () =>
            {
                var log = new LauncherLog();
                int errors = 0; int stages = 0;
                var original = Console.Out;
                try
                {
                    Console.SetOut(TextWriter.Null);
                    using (var runner = new PythonRunner(log))
                    {
                        runner.ErrorDetected += () => System.Threading.Interlocked.Increment(ref errors);
                        runner.StageChanged += stage => System.Threading.Interlocked.Increment(ref stages);
                        runner.Start(environment, LauncherOptions.Parse(new string[0]));
                        Assert(runner.Completion.Wait(15000));
                        Assert(runner.Completion.Result == 7 && errors == 1 && stages == 3);
                        string text = log.Snapshot();
                        Assert(text.Contains("标准输出 中文 299") && text.Contains("标准错误 中文 299") && text.Contains("最后一行：输出已完整读取"));
                    }
                }
                finally { Console.SetOut(original); }
            });
            Console.WriteLine("PASS: " + passed + " checks");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            string root = Path.GetFullPath(temporary);
            string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("OneDragon WPF tests 中文 ")) Directory.Delete(root, true);
        }
    }

    private static void Check(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    private static ProcessStartInfo JobStartInfo()
    {
        return new ProcessStartInfo(typeof(Program).Assembly.Location, "--job-tree")
        { WorkingDirectory = Environment.CurrentDirectory, UseShellExecute = false, CreateNoWindow = true };
    }

    private static void CheckJobLifetime(bool release)
    {
        var childId = new TaskCompletionSource<int>();
        using (var owned = new StartupProcess(JobStartInfo(), line => childId.TrySetResult(int.Parse(line))))
        using (var root = Process.GetProcessById(owned.Id))
        {
            Process child = null;
            try
            {
                Assert(childId.Task.Wait(10000));
                child = Process.GetProcessById(childId.Task.Result);
                if (release) { owned.Release(); owned.Release(); }
                owned.Dispose();
                owned.Dispose();
                if (release) Assert(!root.WaitForExit(300) && !child.WaitForExit(300));
                else Assert(root.WaitForExit(10000) && child.WaitForExit(10000));
            }
            finally
            {
                KillFixture(child); KillFixture(root);
                child?.Dispose();
            }
            Assert(owned.Completion.Wait(10000));
        }
    }

    private static void KillFixture(Process process)
    {
        if (process != null && !process.HasExited) { process.Kill(); process.WaitForExit(10000); }
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Reject(string[] args)
    {
        bool failed = false;
        try { LauncherOptions.Parse(args); } catch (ArgumentException) { failed = true; }
        Assert(failed);
    }
    private static string[] Split(string command)
    {
        int count;
        IntPtr pointer = CommandLineToArgvW(command, out count);
        if (pointer == IntPtr.Zero) throw new Exception("CommandLineToArgvW failed");
        try
        {
            var args = new string[count];
            for (int i = 0; i < count; i++) args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size));
            return args;
        }
        finally { LocalFree(pointer); }
    }
}
