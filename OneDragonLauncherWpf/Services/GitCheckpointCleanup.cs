using System;
using System.IO;
using System.Text;

namespace OneDragonLauncherWpf.Services
{
    /// <summary>提供嵌入 EXE 的独立清理脚本，复用项目 Python 中的 pygit2。</summary>
    public static class GitCheckpointCleanup
    {
        /// <summary>在 Python 业务入口前执行，不依赖外部脚本文件或 git.exe。</summary>
        public static readonly string Script = ReadScript();

        private static string ReadScript()
        {
            using (var stream = typeof(GitCheckpointCleanup).Assembly.GetManifestResourceStream(
                "OneDragonLauncherWpf.GitCheckpointCleanup.py"))
            {
                if (stream == null) throw new InvalidOperationException("启动器缺少内嵌 Git 清理脚本。");
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd() + "\n";
            }
        }
    }
}
