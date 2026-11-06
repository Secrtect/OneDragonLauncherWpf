"""在无 MinGit、空 PATH 的临时仓库验证 pygit2 清理，不访问实际项目。"""
import os
from pathlib import Path
import subprocess
import sys
import tempfile

import pygit2

from BootstrapSource import constant


SCRIPT = constant('GitCheckpointCleanup.Script')
PREFIX = 'refs/codex/turn-diffs/checkpoints/'
SIGNATURE = pygit2.Signature('Fixture', 'fixture@example.invalid')
assert callable(getattr(pygit2.Repository, 'transaction', None)), '测试需要支持引用事务的 pygit2'


def run(root, setup='', code=SCRIPT, args=()):
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith('GIT_')}
    env.update(PATH='', PYTHONUTF8='1', PYTHONIOENCODING='utf-8')
    # 故意传入其他仓库环境变量，清理必须始终限定 cwd。
    env.update(GIT_DIR=str(root / 'not-the-repository'), GIT_NAMESPACE='wrong')
    result = subprocess.run([sys.executable, '-B', '-X', 'utf8', '-u', '-c', setup + code, *args],
                            cwd=root, env=env, capture_output=True, encoding='utf-8', timeout=20)
    assert result.returncode == 0, (result.stdout, result.stderr)
    return result.stdout


def fixture(root):
    repository = pygit2.init_repository(str(root), initial_head='main')
    builder = repository.TreeBuilder()
    builder.insert('tracked.txt', repository.create_blob(b'original'), pygit2.GIT_FILEMODE_BLOB)
    tree = builder.write()
    commit = repository.create_commit('refs/heads/main', SIGNATURE, SIGNATURE, 'initial', tree, [])
    repository.index.read_tree(tree)
    repository.index.write()
    (root / 'tracked.txt').write_text('local changes', encoding='utf-8')
    (root / 'untracked.txt').write_text('keep me', encoding='utf-8')
    repository.references.create(PREFIX + 'a-packed', tree)
    repository.references.create(PREFIX + 'keep-commit', commit)
    repository.references.create('refs/other/keep-tree', tree)
    repository.references.create('refs/codex/other/keep-tree', tree)
    repository.references.create(PREFIX + 'keep-symbolic', 'refs/other/keep-tree')
    repository.references.create('refs/tags/keep-tag', commit)
    repository.references.compress()
    repository.references.create(PREFIX + 'b-loose-中文', tree)
    repository.free()


def refs(root):
    repository = pygit2.Repository(str(root))
    try:
        return {name: str(repository.references[name].target) for name in repository.references}
    finally:
        repository.free()


with tempfile.TemporaryDirectory(prefix='OneDragon pygit2 中文 ') as directory:
    base = Path(directory)
    root = base / 'cleanup'
    fixture(root)
    original = refs(root)
    # 模拟第二个目标被占用，确保第一个目标尚未删除。
    lock = root / '.git/refs/codex/turn-diffs/checkpoints/b-loose-中文.lock'
    lock.write_text('fixture', encoding='utf-8')
    try:
        output = run(root)
        assert '继续原有启动流程' in output and '已清理' not in output, output
        assert refs(root) == original
    finally:
        lock.unlink()
    print('PASS: lock conflict preserves all checkpoint refs')

    output = run(root)
    assert '已清理 2 个' in output, output
    expected = {name: oid for name, oid in original.items() if name not in (PREFIX + 'a-packed', PREFIX + 'b-loose-中文')}
    assert refs(root) == expected
    assert (root / 'tracked.txt').read_text(encoding='utf-8') == 'local changes'
    assert (root / 'untracked.txt').read_text(encoding='utf-8') == 'keep me'
    assert '没有需要清理' in run(root)
    print('PASS: loose/packed trees removed without git.exe; other refs and local files preserved; idempotent')

    race = base / 'race'
    fixture(race)
    before_race = refs(race)
    setup = '''
import pygit2
_original_transaction = pygit2.Repository.transaction
def _racing_transaction(repository):
    native = _original_transaction(repository)
    class RacingTransaction:
        def __enter__(self):
            native.__enter__()
            return self
        def lock_ref(self, name):
            if name.endswith('a-packed'):
                repository.references.create(name, repository.head.target, force=True)
            native.lock_ref(name)
        def remove(self, name):
            native.remove(name)
        def __exit__(self, *args):
            return native.__exit__(*args)
    return RacingTransaction()
pygit2.Repository.transaction = _racing_transaction
'''
    assert '已发生变化' in run(race, setup)
    after_race = refs(race)
    assert after_race[PREFIX + 'a-packed'] == before_race['refs/heads/main']
    assert after_race[PREFIX + 'b-loose-中文'] == before_race[PREFIX + 'b-loose-中文']
    print('PASS: reference changed between scan and lock is preserved')

    missing = '''
import importlib.abc, sys
class MissingPygit2(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname == 'pygit2':
            raise ModuleNotFoundError('missing pygit2', name='pygit2')
sys.meta_path.insert(0, MissingPygit2())
'''
    assert '未安装 pygit2' in run(race, missing)
    assert '不支持引用事务' in run(race, 'import pygit2\npygit2.Repository.transaction = None\n')
    assert refs(race) == after_race
    print('PASS: missing pygit2 and old API skip cleanup without changing refs')

    child = race / 'child'
    child.mkdir()
    assert run(child) == '' and refs(race) == after_race
    broken = base / 'broken'
    (broken / '.git').mkdir(parents=True)
    assert '继续原有启动流程' in run(broken)
    print('PASS: no ancestor repository discovery; invalid repository fails open')

    # 真实 libgit2 本地 fetch：先复现原错误，再执行清理并验证同一次更新能够成功。
    source_path = base / 'source.git'
    source = pygit2.init_repository(str(source_path), bare=True, initial_head='main')
    tree = source.TreeBuilder().write()
    first = source.create_commit('refs/heads/main', SIGNATURE, SIGNATURE, 'first', tree, [])
    dest_path = base / 'fetch'
    dest = pygit2.clone_repository(str(source_path), str(dest_path))
    dest.references.create(PREFIX + 'repro', tree)
    second = source.create_commit('refs/heads/main', SIGNATURE, SIGNATURE, 'second', tree, [first])
    try:
        dest.remotes['origin'].fetch(['+refs/heads/main:refs/remotes/origin/main'])
    except ValueError as error:
        assert 'object is not a committish' in str(error), error
    else:
        # 更新的 libgit2 可能已修复；清理后的 fetch 仍必须通过。
        print('NOTE: installed libgit2 does not reproduce the original fetch error')
    dest.free()
    assert '已清理 1 个' in run(dest_path)
    dest = pygit2.Repository(str(dest_path))
    dest.remotes['origin'].fetch(['+refs/heads/main:refs/remotes/origin/main'])
    assert dest.references['refs/remotes/origin/main'].target == second
    dest.free()
    source.free()
    print('PASS: local fetch succeeds after removing the triggering checkpoint')

    # 实际 GUI/CLI 引导字符串，模拟业务模块在导入时确认清理已经执行。
    for mode in ('GuiBootstrap', 'CliBootstrap'):
        project = base / mode
        fixture(project)
        setup = '''
import importlib.abc, importlib.util, sys, pygit2
class BusinessLoader(importlib.abc.Loader):
    def create_module(self, spec):
        return None
    def exec_module(self, module):
        repo = pygit2.Repository('.')
        try:
            assert 'refs/codex/turn-diffs/checkpoints/a-packed' not in repo.references
            assert 'refs/codex/turn-diffs/checkpoints/b-loose-中文' not in repo.references
        finally:
            repo.free()
        print('BUSINESS_IMPORTED')
        module.run_python = lambda *args, **kwargs: print('BUSINESS_STARTED')
class BusinessFinder(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname in ('one_dragon', 'one_dragon.devtools', 'one_dragon.version'):
            return importlib.util.spec_from_loader(fullname, loader=None, is_package=True)
        if fullname == 'one_dragon.devtools.python_launcher':
            return importlib.util.spec_from_loader(fullname, BusinessLoader())
sys.meta_path.insert(0, BusinessFinder())
'''
        output = run(project, setup, constant(mode), ('--onedragon',) if mode == 'CliBootstrap' else ())
        assert output.index('已清理 2 个') < output.index('BUSINESS_IMPORTED') < output.index('BUSINESS_STARTED')
    print('PASS: both real GUI/CLI bootstraps clean before importing and starting business code')

print('PASS: pygit2 checkpoint cleanup smoke checks')
