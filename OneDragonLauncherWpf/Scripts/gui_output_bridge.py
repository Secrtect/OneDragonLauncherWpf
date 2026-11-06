"""转发 GUI 及后代进程的输出；启动器退出后继续排空管道。"""

import atexit
import os
import sys
import threading

if os.name == 'nt':
    import ctypes
    import msvcrt

    _set_std_handle = ctypes.WinDLL('kernel32', use_last_error=True).SetStdHandle
    _set_std_handle.argtypes = [ctypes.c_ulong, ctypes.c_void_p]
    _set_std_handle.restype = ctypes.c_int


def _publish_handle(fd):
    if os.name == 'nt' and not _set_std_handle(ctypes.c_ulong(-10 - fd), msvcrt.get_osfhandle(fd)):
        raise ctypes.WinError(ctypes.get_last_error())


def _relay_output(source, target):
    try:
        while True:
            data = os.read(source, 8192)
            if not data:
                break
            if target is not None:
                try:
                    while data:
                        data = data[os.write(target, data):]
                except OSError:
                    os.close(target)
                    target = None
    finally:
        os.close(source)
        if target is not None:
            os.close(target)


_output_threads = []
for _fd in (1, 2):
    _target = os.dup(_fd)
    _source, _writer = os.pipe()
    os.dup2(_writer, _fd)
    os.close(_writer)
    _publish_handle(_fd)
    _thread = threading.Thread(target=_relay_output, args=(_source, _target), daemon=True)
    _thread.start()
    _output_threads.append(_thread)


def _finish_output():
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.flush()
        except (OSError, ValueError):
            pass
    # 空设备不会写入磁盘文件；释放本地管道写入端，并排空剩余输出。
    with open(os.devnull, 'wb') as sink:
        for fd in (1, 2):
            os.dup2(sink.fileno(), fd)
            _publish_handle(fd)
    for thread in _output_threads:
        thread.join(timeout=2)


atexit.register(_finish_output)
