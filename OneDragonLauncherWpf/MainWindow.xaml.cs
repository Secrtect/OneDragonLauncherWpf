using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using OneDragonLauncherWpf.Services;
using OneDragonLauncherWpf.ViewModels;
using Wpf.Ui.Controls;

namespace OneDragonLauncherWpf
{
    public partial class MainWindow : FluentWindow
    {
        private readonly LauncherOptions options;
        private readonly LauncherLog log = new LauncherLog();
        private readonly LauncherViewModel model = new LauncherViewModel();
        private PythonRunner runner;
        private TerminalWindow terminal;
        private bool busy;
        private bool dismissed;
        private bool ready;
        private bool started;

        public MainWindow() : this(LauncherOptions.Parse(new[] { "--preview" })) { }

        public MainWindow(LauncherOptions options)
        {
            this.options = options;
            InitializeComponent();
            WindowAppearance.Attach(this);
            DataContext = model;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (started) return;
            started = true;
            if (options.Preview)
            {
                model.Apply(new LaunchStage(45, "正在同步代码更新...", "Git 代码同步"));
                log.Write("[界面预览] 此模式不会请求管理员权限或启动 Python。");
                log.Write("识别到虚拟环境 Python: <项目目录>\\.venv\\Scripts\\python.exe");
                log.Write("使用 Python 启动器: one_dragon.devtools.python_launcher");
                log.Write("当前工作目录: <项目目录>");
                log.Write("目录核验通过");
                log.Write("开始配置环境变量");
                log.Write("PYTHONUTF8=1 / PYTHONIOENCODING=utf-8");
                log.Write("正在检查代码更新");
                log.Write("同步代码仓库...");
                log.Write("[界面预览] 可选择、复制日志，关闭后可重新打开。");
                return;
            }
            busy = true;
            try
            {
                log.Write("检查工作目录与 Python 环境...");
                var environment = await Task.Run(() => LaunchEnvironment.Resolve(options.ProjectDirectory, AppDomain.CurrentDomain.BaseDirectory, Environment.CurrentDirectory));
                if (dismissed) return;
                if (!WindowsIntegration.IsAdministrator())
                {
                    model.StatusMessage = "正在请求管理员权限...";
                    log.Write("检测到非管理员权限，正在请求 UAC 提权...");
                    using (var elevated = WindowsIntegration.RelaunchElevated(((App)Application.Current).OriginalArguments))
                    {
                        if (elevated == null) throw new InvalidOperationException("无法启动管理员进程。");
                    }
                    dismissed = true;
                    return;
                }
                runner = new PythonRunner(log);
                runner.StageChanged += stage => Dispatch(() => { if (!ready && !dismissed) model.Apply(stage); });
                runner.ErrorDetected += () => Dispatch(() => { if (!dismissed && !ready) OpenLog(); });
                runner.Start(environment, options);
                await WatchStartupAsync();
                int exitCode = await runner.Completion;
                if (!dismissed && exitCode != 0) ShowFailure("Python 启动失败", "Python 退出码: " + exitCode + "，请查看日志。");
                else if (!ready && !dismissed) ShowFailure("主程序尚未就绪", "Python 已退出，未检测到主界面，请查看日志。");
            }
            catch (Exception exception)
            {
                string message = exception is Win32Exception && ((Win32Exception)exception).NativeErrorCode == 1223
                    ? "已取消管理员授权，未启动 Python。" : exception.Message;
                if (!dismissed) ShowFailure("启动失败", message);
            }
            finally
            {
                busy = false;
                runner?.Dispose();
                if (dismissed || (ready && (terminal == null || !terminal.IsVisible))) Application.Current.Shutdown();
            }
        }

        private async Task WatchStartupAsync()
        {
            var family = new HashSet<uint> { (uint)runner.ProcessId };
            var timer = Stopwatch.StartNew();
            bool timeoutReported = false;
            while (!dismissed)
            {
                bool found = await Task.Run(() => WindowsIntegration.HasMainWindow(family));
                if (dismissed) return;
                if (found)
                {
                    ready = true;
                    log.Write("[窗口监视] 检测到本次启动的 OneDragon GUI，关闭启动器窗口。");
                    Close();
                    return;
                }
                if (runner.Completion.IsCompleted) return;
                if (!timeoutReported && timer.Elapsed >= TimeSpan.FromSeconds(30))
                {
                    timeoutReported = true;
                    model.StatusMessage = "启动耗时较长，正在等待主界面...";
                    model.StepText = "已等待 30 秒，可查看日志了解进度";
                    log.Write("[窗口监视] 30 秒内尚未检测到主界面，将继续等待；请查看日志确认更新或环境安装进度。");
                    OpenLog();
                }
                await Task.Delay(500);
            }
        }

        private void Dispatch(Action action)
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(action);
        }

        private void ShowFailure(string status, string detail)
        {
            ready = false;
            log.Write("[ERROR] " + detail);
            model.Fail(status, detail);
            if (!IsVisible) Show();
            OpenLog();
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e) { OpenLog(); }

        private void OpenLog()
        {
            if (terminal == null)
            {
                terminal = new TerminalWindow(log) { Owner = this };
                terminal.Closed += (sender, e) =>
                {
                    terminal = null;
                    if (ready && !busy) Application.Current.Shutdown();
                };
            }
            terminal.Show();
            terminal.Activate();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Keep draining the redirected pipes while Python is alive. Closing the UI must
            // not give the Python process BrokenPipeError or terminate the running automation.
            if (busy)
            {
                dismissed = true;
                terminal?.Close();
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (!busy) Application.Current.Shutdown();
        }
    }
}
