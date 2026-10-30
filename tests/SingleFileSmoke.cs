using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

internal static class SingleFileSmoke
{
    private const string AssemblyVariable = "ONEDRAGON_SINGLE_FILE_SMOKE_ASSEMBLY";
    private const string InitialThemeVariable = "ONEDRAGON_INITIAL_SURFACE_THEME";

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static IntPtr FindLauncherWindow(int processId)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((window, parameter) =>
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            if (owner == (uint)processId && IsWindowVisible(window) && title.ToString() == "绝区零一条龙启动器")
            {
                found = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        // The hosted probe only references Windows framework assemblies. Its arguments are
        // deliberately valid launcher arguments, so the real App.OnStartup runs unchanged.
        if (args.Length > 0 && args[0] == "--preview") return ProbeWindows();
        if (args.Length != 1) { Console.Error.WriteLine("Usage: SingleFile.Smoke.exe <packed-launcher.exe>"); return 1; }
        string sourceExecutable = Path.GetFullPath(args[0]);
        string artifactDirectory = Path.GetDirectoryName(sourceExecutable);
        string directoryName = "OneDragon-single-file-" + Guid.NewGuid().ToString("N");
        string directory = Path.Combine(artifactDirectory, directoryName);
        try
        {
            Directory.CreateDirectory(directory);
            Console.WriteLine("单文件验证目录: " + directory);
            string executable = Path.Combine(directory, "OneDragon-Launcher.exe");
            File.Copy(sourceExecutable, executable);
            Assert(Directory.GetFiles(directory).Length == 1, "The isolated folder must contain only the launcher EXE.");
            RunHosted(executable, "--preview", "Light");
            RunHosted(executable, "--preview --dark", "Dark");
            RunStandalone(executable, "--preview");
            RunStandalone(executable, "--preview --dark");
            RunHosted(executable, "--preview");
            RunHosted(executable, "--preview --dark");
            Assert(Directory.GetFiles(directory).Length == 1, "The launcher unexpectedly extracted files beside the EXE.");
            Console.WriteLine("PASS: standalone EXE, light/dark startup, both windows, embedded dependencies; no DLL/config sidecars.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            string fullPath = Path.GetFullPath(directory);
            if (string.Equals(Path.GetDirectoryName(fullPath), artifactDirectory, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(fullPath), directoryName, StringComparison.Ordinal) &&
                Directory.Exists(fullPath))
                Directory.Delete(fullPath, true);
        }
    }

    private static ProcessStartInfo StartInfo(string executable, string arguments, string workingDirectory)
    {
        return new ProcessStartInfo(executable, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
    }

    private static void RunStandalone(string executable, string arguments)
    {
        using (var process = new Process { StartInfo = StartInfo(executable, arguments, Path.GetDirectoryName(executable)) })
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                var timeout = Stopwatch.StartNew();
                IntPtr window = IntPtr.Zero;
                while (!process.HasExited && timeout.ElapsedMilliseconds < 10000)
                {
                    window = FindLauncherWindow(process.Id);
                    if (window != IntPtr.Zero) break;
                    Thread.Sleep(100);
                }
                Assert(!process.HasExited && window != IntPtr.Zero, "Packed EXE did not open its main window.");
                Assert(PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero), "Packed EXE did not accept a normal window close.");
                Assert(process.WaitForExit(5000), "Packed EXE did not exit after closing the preview.");
                Assert(process.ExitCode == 0, "Packed EXE exited with code " + process.ExitCode + ": " + stderr.Result);
                Assert(stdout.Result.Contains("[界面预览]"), "The standalone process did not enter preview mode.");
                Console.WriteLine("PASS native single-file startup " + arguments);
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
        }
    }

    private static void RunHosted(string executable, string arguments, string initialTheme = null)
    {
        var info = StartInfo(typeof(SingleFileSmoke).Assembly.Location, arguments, Path.GetDirectoryName(executable));
        info.EnvironmentVariables[AssemblyVariable] = executable;
        if (initialTheme != null) info.EnvironmentVariables[InitialThemeVariable] = initialTheme;
        using (var process = Process.Start(info))
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                Assert(process.WaitForExit(15000), "Window/dependency probe timed out.");
                Assert(process.ExitCode == 0, "Window/dependency probe failed: " + stdout.Result + stderr.Result);
                Console.Write(stdout.Result);
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
        }
    }

    private static int ProbeWindows()
    {
        bool passed = false;
        try
        {
            string path = Environment.GetEnvironmentVariable(AssemblyVariable);
            Assert(!string.IsNullOrEmpty(path), "Missing isolated assembly path.");
            // The native process checks above cover executable activation. Loading bytes here
            // avoids adding the fixture directory to the probe's assembly search paths.
            var assembly = Assembly.Load(File.ReadAllBytes(path));
            string[] embeddedDependencies = { "Wpf.Ui", "Wpf.Ui.Abstractions", "System.Buffers", "System.Memory", "System.Numerics.Vectors", "System.Runtime.CompilerServices.Unsafe" };
            string[] resources = assembly.GetManifestResourceNames();
            foreach (string dependency in embeddedDependencies)
                Assert(resources.Contains("costura." + dependency.ToLowerInvariant() + ".dll"), "Missing uncompressed dependency: " + dependency);
            Assert(!resources.Any(resource => resource.StartsWith("costura.", StringComparison.Ordinal) &&
                resource.EndsWith(".compressed", StringComparison.Ordinal)), "Embedded dependencies must not be compressed.");

            var app = (Application)Activator.CreateInstance(assembly.GetType("OneDragonLauncherWpf.App", true));
            app.GetType().GetMethod("InitializeComponent").Invoke(app, null);
            string initialTheme = Environment.GetEnvironmentVariable(InitialThemeVariable);
            if (initialTheme != null)
            {
                ProbeInitialSurfaces(assembly, app, initialTheme == "Dark");
                app.Shutdown();
                return 0;
            }
            app.DispatcherUnhandledException += (sender, args) =>
            {
                Console.Error.WriteLine(args.Exception);
                args.Handled = true;
                app.Shutdown(1);
            };

            Window terminal = null;
            int phase = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (sender, args) =>
            {
                try
                {
                    var main = app.MainWindow;
                    Assert(main != null && main.IsLoaded && main.IsVisible, "Main window did not load.");
                    if (phase == 0)
                    {
                        main.GetType().GetMethod("OpenLog", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null);
                        terminal = app.Windows.Cast<Window>().Single(window => window.GetType().Name == "TerminalWindow");
                        phase++;
                        return;
                    }
                    Assert(terminal.IsLoaded && terminal.IsVisible, "Log window did not load.");
                    var text = (TextBox)terminal.FindName("LogText");
                    Assert(text.Text.Contains("[界面预览]"), "Log window did not receive preview output.");
                    var progress = (ProgressBar)main.FindName("StartupProgressBar");
                    if (phase == 1)
                    {
                        Assert(progress.IsIndeterminate, "Startup must use an indeterminate progress bar.");
                        main.DataContext.GetType().GetMethod("Fail").Invoke(main.DataContext, new object[] { "模拟失败", "仅验证界面状态" });
                        phase++;
                        return;
                    }
                    Assert(!progress.IsIndeterminate && progress.Value == progress.Maximum,
                        "Failure must stop the animation and fill the progress bar.");
                    Assert(progress.Foreground == main.FindResource("SystemFillColorCriticalBrush"),
                        "Failure must use the error brush.");
                    foreach (string dependency in embeddedDependencies)
                    {
                        var loaded = Assembly.Load(dependency);
                        Assert(string.IsNullOrEmpty(loaded.Location), "Dependency was loaded from disk instead of embedded resources: " + dependency);
                    }
                    passed = true;
                    Console.WriteLine("PASS packed WPF main/log windows and all six dependencies loaded from memory.");
                    Console.WriteLine("PASS indeterminate startup and full error bar after failure.");
                    timer.Stop();
                    app.Shutdown();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine(exception);
                    timer.Stop();
                    app.Shutdown(1);
                }
            };
            timer.Start();
            app.Run();
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); }
        return passed ? 0 : 1;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ProbeInitialSurfaces(Assembly assembly, Application app, bool dark)
    {
        // Force both themes independently of the machine's personalization setting.
        app.GetType().GetMethod("ApplyTheme", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(app, new object[] { dark });
        var main = (Window)Activator.CreateInstance(assembly.GetType("OneDragonLauncherWpf.MainWindow", true));
        var log = Activator.CreateInstance(assembly.GetType("OneDragonLauncherWpf.Services.LauncherLog", true));
        var terminal = (Window)Activator.CreateInstance(assembly.GetType("OneDragonLauncherWpf.TerminalWindow", true), log);
        foreach (var window in new[] { main, terminal })
        {
            // Create the real HWND without Show/Loaded. This catches fixes applied too
            // late to protect the first frame, as well as missing wiring on either window.
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            Assert(!window.IsLoaded && !IsWindowVisible(handle), "Initial surface probe must run before the window is displayed.");
            var target = HwndSource.FromHwnd(handle).CompositionTarget;
            Color expected = dark ? Color.FromRgb(32, 38, 43) : Color.FromRgb(237, 246, 250);
            Assert(target != null && target.BackgroundColor == expected,
                window.GetType().Name + " has the wrong pre-show render background: " + target?.BackgroundColor);
            int nativeDark;
            int result = DwmGetWindowAttribute(handle, 20, out nativeDark, sizeof(int));
            if (result < 0) result = DwmGetWindowAttribute(handle, 19, out nativeDark, sizeof(int));
            if (result >= 0)
                Assert(nativeDark == (dark ? 1 : 0), window.GetType().Name + " has the wrong native theme before Show.");
            else
                Console.WriteLine("SKIP native dark-mode assertion: DWM attribute unavailable.");
        }
        Console.WriteLine("PASS pre-show opaque background and native theme for main/log windows: " + (dark ? "Dark" : "Light"));
    }
}
