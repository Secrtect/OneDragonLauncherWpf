using System;
using System.IO;
using System.Text;

namespace OneDragonLauncherWpf.Services
{
    /// <summary>读取编译进 EXE 的 Python 源码，保持单文件分发。</summary>
    internal static class EmbeddedPythonScript
    {
        internal static string Read(string name)
        {
            string resourceName = "OneDragonLauncherWpf." + name + ".py";
            using (var stream = typeof(EmbeddedPythonScript).Assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) throw new InvalidOperationException("启动器缺少内嵌 Python 脚本: " + resourceName);
                // 追加换行，避免拼接时下一段源码被末行注释吞掉。
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd() + "\n";
            }
        }
    }
}
