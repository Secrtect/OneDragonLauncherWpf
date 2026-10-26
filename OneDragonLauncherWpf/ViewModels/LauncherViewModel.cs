using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using OneDragonLauncherWpf.Services;

namespace OneDragonLauncherWpf.ViewModels
{
    public sealed class LauncherViewModel : INotifyPropertyChanged
    {
        private string statusMessage = "正在初始化环境并启动 GUI...";
        private string stepText = "检查工作目录与 Python 环境...";
        private double progress;
        private bool isIndeterminate = true;
        private Brush progressBrush = new SolidColorBrush(Color.FromRgb(0, 103, 192));
        public event PropertyChangedEventHandler PropertyChanged;
        public string StatusMessage { get { return statusMessage; } set { statusMessage = value; Changed(); } }
        public string StepText { get { return stepText; } set { stepText = value; Changed(); } }
        public double Progress { get { return progress; } set { progress = value; Changed(); } }
        public bool IsIndeterminate { get { return isIndeterminate; } set { isIndeterminate = value; Changed(); } }
        public Brush ProgressBrush { get { return progressBrush; } set { progressBrush = value; Changed(); } }
        private void Changed([CallerMemberName] string property = null) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property)); }

        public void Apply(LaunchStage stage)
        {
            if (stage.Progress < Progress) return;
            Progress = stage.Progress;
            StatusMessage = stage.Status;
            StepText = stage.Step;
        }

        public void Fail(string status, string detail)
        {
            StatusMessage = status;
            StepText = detail;
            IsIndeterminate = false;
            Progress = 100;
            ProgressBrush = new SolidColorBrush(Color.FromRgb(196, 43, 28));
        }

    }
}
