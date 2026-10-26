"""Run with a real Python interpreter; no OneDragon dependencies or game operations."""
import pathlib
import os
import subprocess
import sys

source = (pathlib.Path(__file__).resolve().parents[1] /
          'OneDragonLauncherWpf/Services/PythonRunner.cs').read_text(encoding='utf-8-sig')
bridge = source.split('public const string GuiOutputBridge = @"', 1)[1].split('";', 1)[0].replace('""', '"')


def start(code):
    return subprocess.Popen([sys.executable, '-u', '-c', bridge + code],
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            env={**os.environ, 'PYTHONUTF8': '1', 'PYTHONIOENCODING': 'utf-8'})


# Native writes and inherited handles exercise the GUI/uv descendants as well as print().
descendant = "import os; os.write(1, ('子进程输出\\n' * 50000).encode()); os.write(2, ('子进程错误\\n' * 50000).encode())"
with start("""
import subprocess
print('启动就绪', flush=True)
sys.stdin.readline()
for i in range(50000):
    print('主进程输出')
    print('主进程错误', file=sys.stderr)
subprocess.run([sys.executable, '-u', '-c', %r], check=True)
""" % descendant) as process:
    assert process.stdout.readline().decode('utf-8').strip() == '启动就绪'
    # Simulate the launcher going away while the Python tree keeps writing megabytes.
    process.stdout.close()
    process.stderr.close()
    process.stdin.write(b'continue\n')
    process.stdin.flush()
    try:
        assert process.wait(timeout=20) == 0
    except BaseException:
        process.kill()
        raise
print('PASS: Python and descendants survive disconnected launcher pipes without disk logs')

with start("""
for i in range(300):
    print('标准输出 中文 ' + str(i))
    print('标准错误 中文 ' + str(i), file=sys.stderr)
sys.stdout.write('最后一行无换行')
raise RuntimeError('模拟启动失败')
""") as process:
    stdout, stderr = process.communicate(timeout=20)
    assert process.returncode == 1
    assert '标准输出 中文 299' in stdout.decode('utf-8')
    assert stdout.decode('utf-8').endswith('最后一行无换行')
    assert '标准错误 中文 299' in stderr.decode('utf-8')
    assert 'RuntimeError: 模拟启动失败' in stderr.decode('utf-8')
print('PASS: startup errors, UTF-8 output, final output and failure exit code preserved')
