"""读取实际 C# 引导表达式和内嵌 Python 资源，供隔离测试复用。"""
import pathlib
import re


ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = (ROOT / 'OneDragonLauncherWpf/Services/PythonRunner.cs').read_text(encoding='utf-8-sig')


def constant(name):
    if name == 'LauncherVersion.Value':
        source = (ROOT / 'OneDragonLauncherWpf/Services/LauncherVersion.cs').read_text(encoding='utf-8-sig')
        return re.search(r'public const string Value = "([^"]+)";', source).group(1)
    if name == 'GitCheckpointCleanup.Script':
        return (ROOT / 'OneDragonLauncherWpf/Scripts/git_checkpoint_cleanup.py').read_text(encoding='utf-8-sig') + '\n'
    expression = re.search(r'public (?:const|static readonly) string ' + name + r' = (.*?);\s*\n',
                           SOURCE, re.DOTALL).group(1)
    parts = re.findall(r'@"(?:[^"]|"")*"|[A-Za-z_][\w.]*', expression)
    return ''.join(part[2:-1].replace('""', '"') if part.startswith('@"') else constant(part)
                   for part in parts)
