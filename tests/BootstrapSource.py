"""读取实际 C# 引导表达式和内嵌 Python 资源，供隔离测试复用。"""
import pathlib
import re
import xml.etree.ElementTree as ET


ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = (ROOT / 'OneDragonLauncherWpf/Services/PythonRunner.cs').read_text(encoding='utf-8-sig')
PROJECT = ROOT / 'OneDragonLauncherWpf/OneDragonLauncherWpf.csproj'
NS = {'msbuild': 'http://schemas.microsoft.com/developer/msbuild/2003'}
RESOURCES = {
    item.find('msbuild:LogicalName', NS).text: PROJECT.parent / item.attrib['Include'].replace('\\', '/')
    for item in ET.parse(PROJECT).findall('.//msbuild:EmbeddedResource', NS)
    if item.find('msbuild:LogicalName', NS) is not None
}


def script(name):
    return RESOURCES[f'OneDragonLauncherWpf.{name}.py'].read_text(encoding='utf-8-sig') + '\n'


def constant(name):
    if name == 'LauncherVersion.Value':
        source = (ROOT / 'OneDragonLauncherWpf/Services/LauncherVersion.cs').read_text(encoding='utf-8-sig')
        return re.search(r'public const string Value = "([^"]+)";', source).group(1)
    if name == 'GitCheckpointCleanup.Script':
        return script('GitCheckpointCleanup')
    expression = re.search(r'public (?:const|static readonly) string ' + name + r' = (.*?);\s*\n',
                           SOURCE, re.DOTALL).group(1)
    replacement = re.search(r'\.Replace\("([^"]+)", ([\w.]+)\)', expression)
    if replacement:
        expression = expression[:replacement.start()]
    expression = re.sub(r'EmbeddedPythonScript.Read\("([^"]+)"\)',
                        lambda match: '@"' + script(match.group(1)).replace('"', '""') + '"', expression)
    parts = re.findall(r'@"(?:[^"]|"")*"|[A-Za-z_][\w.]*', expression)
    result = ''.join(part[2:-1].replace('""', '"') if part.startswith('@"') else constant(part)
                     for part in parts)
    if replacement:
        result = result.replace(replacement.group(1), constant(replacement.group(2)))
    return result
