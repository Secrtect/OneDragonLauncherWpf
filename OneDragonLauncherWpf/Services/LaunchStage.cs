namespace OneDragonLauncherWpf.Services
{
    public sealed class LaunchStage
    {
        public double Progress { get; private set; }
        public string Status { get; private set; }
        public string Step { get; private set; }

        public LaunchStage(double progress, string status, string step)
        { Progress = progress; Status = status; Step = step; }

        public static LaunchStage FromLog(string line)
        {
            if (line.Contains("当前工作目录") || line.Contains("目录核验通过"))
                return new LaunchStage(15, "正在检查工作目录...", "验证工作目录");
            if (line.Contains("开始配置环境变量"))
                return new LaunchStage(30, "正在配置运行环境...", "配置环境变量");
            if (line.Contains("拉取远程代码") || line.Contains("正在检查代码更新") || line.Contains("同步代码仓库"))
                return new LaunchStage(45, "正在同步代码更新...", "Git 代码同步");
            if (line.Contains("开始检查运行环境") || line.Contains("正在同步运行依赖"))
                return new LaunchStage(65, "正在检查运行依赖...", "同步 Python 运行环境");
            if (line.Contains("正在启动中") || line.Contains("等待主界面弹出"))
                return new LaunchStage(85, "正在启动主界面...", "OneDragon GUI 启动中");
            return null;
        }
    }
}
