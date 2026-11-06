"""业务启动前清理 Codex tree 检查点；复用项目 pygit2，不调用外部 Git。"""


def _cleanup_codex_tree_checkpoints() -> None:
    from pathlib import Path

    repository = None
    try:
        root = Path.cwd()
        # 不向上查找仓库；兼容普通 .git 目录和 worktree 的 .git 文件。
        if not (root / '.git').exists():
            return
        try:
            import pygit2
        except ModuleNotFoundError as error:
            if error.name != 'pygit2':
                raise
            print('[Git 检查] 当前 Python 未安装 pygit2，跳过 Codex 快照清理。', flush=True)
            return

        repository = pygit2.Repository(str(root), flags=pygit2.enums.RepositoryOpenFlag.NO_SEARCH)
        prefix = 'refs/codex/turn-diffs/checkpoints/'
        targets = []
        for name in sorted(repository.references):
            if not name.startswith(prefix):
                continue
            reference = repository.references[name]
            # 符号引用的 target 是字符串；不解析或删除符号引用。
            if isinstance(reference.target, pygit2.Oid) and isinstance(repository[reference.target], pygit2.Tree):
                targets.append((name, reference.target))

        if not targets:
            print('[Git 检查] 没有需要清理的 Codex tree 快照引用。', flush=True)
            return
        if not callable(getattr(repository, 'transaction', None)):
            print('[WARNING] 当前 pygit2 不支持引用事务，跳过 Codex 快照清理，继续原有启动流程。', flush=True)
            return

        # 先锁住并重新核对全部目标，再安排删除；锁冲突或引用变化时不提交删除。
        with repository.transaction() as transaction:
            for name, original_target in targets:
                transaction.lock_ref(name)
                if repository.references[name].target != original_target:
                    raise RuntimeError('检查点引用已发生变化，停止本次清理: ' + name)
            for name, _ in targets:
                transaction.remove(name)
        print(f'[Git 检查] 已清理 {len(targets)} 个 Codex tree 快照引用（未备份）。', flush=True)
    except Exception as error:
        print(f'[WARNING] Codex 快照检查或清理未完成，继续原有启动流程: {error}', flush=True)
    finally:
        if repository is not None:
            repository.free()


_cleanup_codex_tree_checkpoints()
del _cleanup_codex_tree_checkpoints
