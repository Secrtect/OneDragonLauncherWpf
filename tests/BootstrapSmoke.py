"""Exercise the embedded bootstraps with isolated Python fixtures, without running the game."""
import json
import pathlib
import re
import subprocess
import sys
import tempfile


source = (pathlib.Path(__file__).resolve().parents[1] /
          'OneDragonLauncherWpf/Services/PythonRunner.cs').read_text(encoding='utf-8-sig')


def constant(name):
    expression = re.search(r'public const string ' + name + r' = (.*?);\s*\n',
                           source, re.DOTALL).group(1)
    prefix, literal = expression.split('@"', 1)
    return ''.join(constant(part.strip()) for part in prefix.split('+') if part.strip()) + \
        literal[:-1].replace('""', '"')


cli = constant('CliBootstrap')
gui = constant('GuiBootstrap')
arguments = ['--onedragon', '--instance', '中文 实例', '--close-game', '--shutdown', '0']

with tempfile.TemporaryDirectory(prefix='OneDragon bootstrap ') as directory:
    root = pathlib.Path(directory)
    for package in ['one_dragon', 'one_dragon/devtools']:
        folder = root / package
        folder.mkdir(exist_ok=True)
        (folder / '__init__.py').write_text('', encoding='utf-8')

    # Match the failing import order: importing the launcher requires colorama first.
    (root / 'one_dragon/devtools/python_launcher.py').write_text('''
import json
from colorama import Fore, Style, init
init(autoreset=True)

def run_python(modules, piped, no_windows=None, args=None):
    import sys
    assert piped is True
    if modules == ['zzz_od', 'gui', 'app']:
        assert no_windows is None and args is None
        print('GUI_READY')
        return
    assert modules == ['zzz_od', 'application', 'zzz_application_launcher']
    assert no_windows is False
    assert 'pyuac' not in sys.modules
    print(json.dumps(args, ensure_ascii=False))
    print(Fore.GREEN + 'CLI_READY' + Style.RESET_ALL)
    sys.exit(7)
''', encoding='utf-8')

    setup = "import sys\nsys.path.insert(0, %r)\n" % directory
    # Deterministically simulate an absent top-level colorama even if globally installed.
    missing = '''
import importlib.abc
class MissingColorama(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname == 'colorama':
            raise ModuleNotFoundError("No module named 'colorama'", name='colorama')
sys.meta_path.insert(0, MissingColorama())
'''

    def run(bootstrap, extra='', args=()):
        return subprocess.run([sys.executable, '-I', '-S', '-X', 'utf8', '-u', '-c',
                               setup + extra + bootstrap, *args],
                              capture_output=True, encoding='utf-8', timeout=20)

    result = run(cli, missing, arguments)
    assert result.returncode == 7, result.stderr
    assert json.loads(result.stdout.splitlines()[0]) == arguments[1:]
    assert result.stdout.splitlines()[1] == 'CLI_READY'
    print('PASS: CLI missing colorama, direct entry point, arguments and exit code')

    result = run(gui, missing)
    assert result.returncode == 0 and result.stdout.strip() == 'GUI_READY', result.stderr
    print('PASS: GUI missing colorama still starts through the output bridge')

    # A discoverable implementation must be used rather than overwritten by the shim.
    (root / 'colorama.py').write_text('''
class Fore:
    GREEN = 'REAL_GREEN:'
class Style:
    RESET_ALL = ':REAL_RESET'
def init(**kwargs):
    pass
''', encoding='utf-8')
    result = run(cli, args=arguments)
    assert result.returncode == 7, result.stderr
    assert 'REAL_GREEN:CLI_READY:REAL_RESET' in result.stdout
    print('PASS: installed colorama is preserved')

    (root / 'colorama.py').write_text('import missing_colorama_dependency\n', encoding='utf-8')
    result = run(cli, args=arguments)
    assert result.returncode == 1 and 'missing_colorama_dependency' in result.stderr
    assert 'CLI_READY' not in result.stdout
    print('PASS: broken colorama dependency is reported rather than hidden')
