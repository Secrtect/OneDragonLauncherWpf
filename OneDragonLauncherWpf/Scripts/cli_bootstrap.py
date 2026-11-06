import sys

from one_dragon.devtools.python_launcher import run_python

# WPF 已完成参数校验和提权；跳过首位的启动器专用 --onedragon 参数。
run_python(
    ['zzz_od', 'application', 'zzz_application_launcher'],
    no_windows=False,
    args=sys.argv[2:],
    piped=True,
)
