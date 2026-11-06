"""版本占位符由 C# 资源加载处替换，仅修改当前 Python 进程的版本。"""

import one_dragon.version

one_dragon.version.__version__ = '__LAUNCHER_VERSION__'
