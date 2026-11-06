"""仅在缺少 colorama 时提供兼容模块，不掩盖已安装模块的依赖错误。"""

import sys
import types

try:
    import colorama
except ModuleNotFoundError as exc:
    if exc.name != 'colorama':
        raise

    class _UniversalDummy:
        def __getattr__(self, name):
            return self

        def __call__(self, *args, **kwargs):
            return self

        def __str__(self):
            return ''

        def __repr__(self):
            return ''

        def __add__(self, other):
            return str(other) if isinstance(other, str) else ''

        def __radd__(self, other):
            return str(other) if isinstance(other, str) else ''

    d = _UniversalDummy()
    m = types.ModuleType('colorama')
    m.__getattr__ = lambda name: d
    for name in ['colorama', 'colorama.ansi', 'colorama.ansitowin32', 'colorama.win32', 'colorama.initialise']:
        sys.modules[name] = m
