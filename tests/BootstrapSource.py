"""读取实际 C# 引导表达式和内嵌 Python 资源，供隔离测试复用。"""
import pathlib
import re


ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = (ROOT / 'OneDragonLauncherWpf/Services/PythonRunner.cs').read_text(encoding='utf-8-sig')


def constant(name):
    if name == 'GitCheckpointCleanup.Script':
        return (ROOT / 'OneDragonLauncherWpf/Scripts/git_checkpoint_cleanup.py').read_text(encoding='utf-8-sig') + '\n'
    expression = re.search(r'public (?:const|static readonly) string ' + name + r' = (.*?);\s*\n',
                           SOURCE, re.DOTALL).group(1)
    prefix, literal = expression.split('@"', 1)
    return ''.join(constant(part.strip()) for part in prefix.split('+') if part.strip()) + \
        literal[:-1].replace('""', '"')
