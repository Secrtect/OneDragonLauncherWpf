using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace OneDragonLauncherWpf
{
    /// <summary>
    /// 在原生窗口创建后、首次显示前设置不透明背景和标题栏主题。
    /// </summary>
    internal static class WindowAppearance
    {
        /// <summary>
        /// 设置桌面窗口管理器的窗口属性。
        /// </summary>
        /// <param name="window">目标窗口句柄。</param>
        /// <param name="attribute">需要设置的原生属性编号。</param>
        /// <param name="value">属性的整数值。</param>
        /// <param name="size">属性值占用的字节数。</param>
        /// <returns>原生调用结果；负值表示失败。</returns>
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        /// <summary>
        /// 注册原生窗口初始化回调，避免窗口首帧出现透明背景。
        /// </summary>
        /// <param name="window">需要设置外观的 WPF 窗口。</param>
        public static void Attach(Window window)
        {
            window.SourceInitialized += OnSourceInitialized;
        }

        /// <summary>
        /// 设置首帧不透明清屏色和原生深浅色主题，兼容不支持新主题属性的 Windows。
        /// </summary>
        private static void OnSourceInitialized(object sender, EventArgs e)
        {
            var window = (Window)sender;
            window.SourceInitialized -= OnSourceInitialized;
            // EnsureHandle 可能在可视化树连接前创建原生窗口句柄，
            // 因此通过句柄获取呈现源，避免此时调用 FromVisual(window)。
            IntPtr handle = new WindowInteropHelper(window).Handle;
            var source = HwndSource.FromHwnd(handle);

            // 此时 FluentWindow 已配置 WindowChrome，其玻璃边框可能导致
            // 原生窗口在渐变背景绘制完成前保持透明。用不透明清屏色填补这段空隙，
            // 同时保留窗口和内容原有的画刷。
            var solid = window.Background as SolidColorBrush;
            var gradient = window.Background as GradientBrush;
            Color background = solid != null ? solid.Color
                : gradient != null && gradient.GradientStops.Count > 0 ? gradient.GradientStops[0].Color
                : SystemColors.WindowColor;
            if (source.CompositionTarget != null)
                source.CompositionTarget.BackgroundColor = Color.FromRgb(background.R, background.G, background.B);

            // ApplicationThemeManager.Apply 执行时原生窗口尚未创建，
            // 因此在窗口显示前补设原生主题，避免等到加载完成后才生效。
            int dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? 1 : 0;
            int result = DwmSetWindowAttribute(source.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
            if (result < 0)
                result = DwmSetWindowAttribute(source.Handle, 19 /* Older Windows 10 builds */, ref dark, sizeof(int));
            // 较旧的 Windows 版本可能不支持这两个属性；即使原生深色模式不可用，
            // 界面主题和不透明呈现背景仍可正常生效。
            if (result < 0) Debug.WriteLine("Native window theme is unavailable: " + result);
        }
    }
}
