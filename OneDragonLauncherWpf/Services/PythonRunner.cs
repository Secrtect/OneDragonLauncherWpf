using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OneDragonLauncherWpf.Services
{
    public sealed class PythonRunner : IDisposable
    {
        private readonly LauncherLog log;
        private Process process;
        private int reportedError;
        public event Action<LaunchStage> StageChanged;
        public event Action ErrorDetected;
        public Task<int> Completion { get; private set; }
        public int ProcessId { get { return process.Id; } }
        private static readonly Regex ErrorPattern = new Regex(
            @"\bERROR\b|\bCRITICAL\b|Traceback \(most recent call last\)|\b\w*(?:Error|Exception):", RegexOptions.Compiled);

        // Preserve the original piped entry point. Only shim colorama when it is actually absent.
        public const string GuiBootstrap = @"import sys, types
try:
    import colorama
except ModuleNotFoundError as exc:
    if exc.name != 'colorama':
        raise
    class _UniversalDummy:
        def __getattr__(self, name): return self
        def __call__(self, *a, **k): return self
        def __str__(self): return ''
        def __repr__(self): return ''
        def __add__(self, other): return str(other) if isinstance(other, str) else ''
        def __radd__(self, other): return str(other) if isinstance(other, str) else ''
    d = _UniversalDummy()
    m = types.ModuleType('colorama')
    m.__getattr__ = lambda name: d
    for name in ['colorama', 'colorama.ansi', 'colorama.ansitowin32', 'colorama.win32', 'colorama.initialise']:
        sys.modules[name] = m
from one_dragon.devtools.python_launcher import run_python
run_python(['zzz_od', 'gui', 'app'], piped=True)
";

        public PythonRunner(LauncherLog log) { this.log = log; }

        public static ProcessStartInfo CreateStartInfo(LaunchEnvironment environment, LauncherOptions options)
        {
            var args = new List<string>();
            if (environment.UseUv) args.AddRange(new[] { "run", "--frozen", "python" });
            args.Add("-u");
            if (options.OneDragon)
            {
                args.AddRange(new[] { "-m", "zzz_od.win_exe.launcher" });
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
            // A GUI launcher has no console for Python input() prompts; deliver EOF instead of hanging.
            process.StandardInput.Close();
            Completion = Task.Run(() =>
            {
                // Parameterless WaitForExit also drains both asynchronous output readers.
                process.WaitForExit();
                int exitCode = process.ExitCode;
                log.Write("Python 进程已退出，退出码: " + exitCode);
                return exitCode;
            });
        }

        private void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null) return;
            string clean = LauncherLog.Clean(args.Data);
            log.Write(clean);
            LaunchStage stage = LaunchStage.FromLog(clean);
            if (stage != null) StageChanged?.Invoke(stage);
            if (ErrorPattern.IsMatch(clean) && Interlocked.Exchange(ref reportedError, 1) == 0) ErrorDetected?.Invoke();
        }

        public void Dispose() { process?.Dispose(); }
    }
}
