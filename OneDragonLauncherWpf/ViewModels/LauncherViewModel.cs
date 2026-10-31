using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OneDragonLauncherWpf.Services;

namespace OneDragonLauncherWpf.ViewModels
{
    /// <summary>
    /// 提供启动窗口的状态文本和失败标志，防止阶段倒退或覆盖错误。
    /// </summary>
    public sealed class LauncherViewModel : INotifyPropertyChanged
    {
        private string statusMessage = "正在初始化环境并启动 GUI...";
        private string stepText = "检查工作目录与 Python 环境...";
        private LaunchPhase currentPhase = LaunchPhase.Initializing;
        private bool hasFailed;
        private readonly Stopwatch guiWait = new Stopwatch();
        private bool guiWaitReported;
        public bool IsStartingGui { get { return currentPhase == LaunchPhase.StartingGui; } }
        private static readonly Regex LogPrefix = new Regex(
            @"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(?:[,.]\d+)?\s*\|\s*[A-Z]+\s*\|\s*", RegexOptions.Compiled);
        /// <summary>
        /// 通知 WPF 绑定某个属性的值已变化。
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>
        /// 显示当前启动阶段或失败原因的主标题。
        /// </summary>
        public string StatusMessage { get { return statusMessage; } set { statusMessage = value; Changed(); } }
        /// <summary>
        /// 显示当前步骤或失败详情。
        /// </summary>
        public string StepText { get { return stepText; } set { stepText = value; Changed(); } }
        /// <summary>
        /// 表示已进入失败状态，用于停止正常进度显示。
        /// </summary>
        public bool HasFailed { get { return hasFailed; } private set { hasFailed = value; Changed(); } }
        /// <summary>
        /// 向绑定层发送属性变化通知。
        /// </summary>
        /// <param name="property">变化的属性名，默认由调用成员名称提供。</param>
        private void Changed([CallerMemberName] string property = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property)); }

        /// <summary>
        /// 应用新的启动阶段，忽略倒退阶段和失败后的普通状态更新。
        /// </summary>
        /// <param name="stage">从后端日志识别出的非空启动阶段。</param>
        public void Apply(LaunchStage stage)
        {
            if (HasFailed || stage.Phase < currentPhase) return;
            // Repeated completion logs must not restart the wait or restore the initial text.
            if (stage.Phase == LaunchPhase.StartingGui && IsStartingGui) return;
            currentPhase = stage.Phase;
            if (IsStartingGui) guiWait.Start();
            StatusMessage = stage.Status;
            StepText = stage.Step;
        }

        /// <summary>
        /// 更新阶段，并在 Git 同步期间将最新日志正文显示为步骤说明。
        /// </summary>
        /// <param name="line">已清理控制字符的后端日志。</param>
        public void ApplyLog(string line)
        {
            if (HasFailed || string.IsNullOrWhiteSpace(line)) return;
            var stage = LaunchStage.FromLog(line);
            if (stage != null)
            {
                if (stage.Phase < currentPhase) return;
                Apply(stage);
            }
            if (currentPhase != LaunchPhase.SyncingCode) return;
            string message = LogPrefix.Replace(line, "").Trim();
            if (message.Length > 0) StepText = message;
        }

        /// <summary>仅在环境就绪后的界面启动阶段，三秒后更新提示，六秒后请求显示日志。</summary>
        /// <returns>首次等待满六秒时返回 true，通知窗口显示日志。</returns>
        public bool UpdateGuiWait()
        {
            if (HasFailed || !IsStartingGui) return false;
            double seconds = guiWait.Elapsed.TotalSeconds;
            if (seconds >= 4 && StepText != "加载核心组件耗时稍长，正在努力准备中...")
                StepText = "加载核心组件耗时稍长，正在努力准备中...";
            if (seconds < 10 || guiWaitReported) return false;
            guiWaitReported = true;
            return true;
        }

        /// <summary>
        /// 显示错误并锁定失败状态，防止后续日志恢复正常进度。
        /// </summary>
        /// <param name="status">失败标题。</param>
        /// <param name="detail">供用户排查的详细说明。</param>
        public void Fail(string status, string detail)
        {
            StatusMessage = status;
            StepText = detail;
            HasFailed = true;
        }

    }
}
