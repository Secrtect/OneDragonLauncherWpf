# OneDragon Launcher · WPF UI

将 `OneDragon-Launcher-Slint-main` 中的 Rust/Slint 启动器迁移到 C#、WPF 和 WPF UI 4.3.0。原始 Slint 目录保留，WPF 程序不依赖 Rust 或 Slint。

## 运行

需要 Windows 10/11、.NET Framework 4.7.2 或更高版本，以及已经安装好的一条龙 Python 项目与运行环境。

发布单文件位于 **`artifacts/OneDragonLauncherWpf.exe`**，ZIP 包 `artifacts/OneDragonLauncherWpf-Windows.zip` 中也只有这个 EXE。部署时只需复制这个 EXE 到一条龙项目根目录（与 `src`、`.venv`、`.install` 同级），不需要随附 WPF UI DLL 或 `.exe.config`。也可以保留启动器在独立目录，通过参数指定项目：

```powershell
.\OneDragonLauncherWpf.exe --project "D:\ZenlessZoneZero-OneDragon"
```

Visual Studio 选择 **Debug** 后直接按 **F5**，未指定项目时默认进入界面预览，不需要 Python 源码或运行环境，也不请求管理员权限。Debug 下单独指定 `--dark` 仍然是预览。

需要联调真实 Python 后端时，在“项目属性 → 调试 → 命令行参数”填写 `--project "D:\ZenlessZoneZero-OneDragon"`；`--onedragon` 也会进入真实 CLI 流程。Release 默认保持真实启动。

也可以显式预览界面，不提权、不运行 Python、不更新代码：

```powershell
.\OneDragonLauncherWpf.exe --preview
.\OneDragonLauncherWpf.exe --preview --dark
```

正常启动时，环境检查通过后会请求管理员权限，保持原版行为。帮助、版本和预览模式不需要管理员权限。

## 窗口与行为

- 启动窗口：520 × 300 DIP，原始 Logo、标题、状态、动态进度条和“查看日志”，WPF UI 标题栏与按钮。
- 日志窗口：580 × 420 DIP，可调整大小；实时显示 UTF-8 标准输出/错误输出、清除 ANSI 控制码、选择与复制日志，支持 Esc 关闭和重新打开。
- 默认在启动时读取系统深浅色主题，也可用 `--dark` 指定深色。浅色使用参考截图的淡蓝渐变底色；这里没有复用 Slint 的 Mica/窗口 Hook 实现。原生圆角由支持的 Windows 版本提供。
- 日志在底部时自动跟随；向上查看或选中文字时停止追尾。内存日志最多保留 10,000 条且不超过 1,048,576 个字符，复制的是当前保留日志。
- 检测到错误会自动打开日志。等待 30 秒仍未就绪时保留窗口并提示，之后继续监视，不静默隐藏失败。
- 每 500ms 检查本次启动进程及其子进程的可见窗口。检测到“绝区零 一条龙”主界面后，立即关闭启动窗口及已打开的日志窗口，不切换进度条完成状态、不延迟等待。
- 启动窗口关闭后，若 Python 仍在运行，启动器会在后台继续读取输出，避免关闭管道导致 Python 出错。Python 退出后释放后台进程；关闭日志窗口不会终止 Python。

## 后端兼容

源码目录查找：显式 `--project` 优先，否则从 EXE 目录向上最多检查五层，以及当前工作目录；支持直接的 `src/zzz_od`，以及 `ZenlessZoneZero-OneDragon`、`ZenlessZoneZero-OneDragon-main` 子目录。

执行器优先级：项目或启动器目录中的 `.venv/Scripts/python.exe`、`.venv/python.exe`、`.install/python/python.exe` → 本地 `.install/uv/uv.exe` 或 `.install/uv.exe` → PATH 中的 uv → PATH 中的 Python。忽略 Windows Store 的 Python 占位别名。

工作目录固定为实际 Python 项目根目录，注入 `PYTHONPATH`、`PYTHONUNBUFFERED=1`、`PYTHONUTF8=1`、`PYTHONIOENCODING=utf-8`。

GUI 模式保留原版入口：

```python
from one_dragon.devtools.python_launcher import run_python
run_python(['zzz_od', 'gui', 'app'], piped=True)
```

只有缺少 colorama 时才使用原版兼容 shim，已安装的 colorama 不会被覆盖。隐藏控制台下的标准输入会立即提供 EOF，避免后端 `input()` 等待一个不存在的交互终端。

**Python 项目自身的限制**：当前本机 `python_launcher.verify_working_directory()` 会拒绝含空格或中文的项目路径。C# 的参数引用与 UTF-8 处理不改变这项后端校验，部署建议使用例如 `D:\ZenlessZoneZero-OneDragon` 的路径。

## 命令行

```text
-h, --help                  帮助
-v, --version               版本
-o, --onedragon             一条龙 CLI 模式
-c, --close-game            运行后关闭游戏，需要 --onedragon
-s, --shutdown [秒]         运行后关机，默认 60 秒，需要 --onedragon
-i, --instance 1,2          账号实例，需要 --onedragon
    --project <目录>       Python 项目根目录
    --preview              仅预览界面
    --dark                 深色主题
```

CLI 模式调用 `python -u -m zzz_od.win_exe.launcher`，透传参数并返回 Python 退出码；需要提权时等待管理员进程并保留退出码。命令行里的关闭游戏和关机行为由 Python 后端执行。

## 构建与验证

使用 Visual Studio 的“.NET 桌面开发”组件和 .NET Framework 4.7.2 targeting pack。现有 `packages/` 目录保留；新增固定版本的 `Fody 6.9.3` 和 `Costura.Fody 6.2.0`，缺少程序包时构建脚本会自动通过 NuGet 还原。

在仓库根目录运行：

```powershell
.\build.ps1
```

脚本定位 Visual Studio MSBuild，构建 Release、运行 15 组后端检查和单文件启动检查，生成 `artifacts/OneDragonLauncherWpf.exe` 与只包含该 EXE 的 ZIP。测试程序不依赖外部测试框架。

Release 使用 `FodyWeavers.xml` 配置的 Costura 将托管依赖嵌入 EXE，设置 `DisableCompression="true"` 关闭压缩，启动时直接从内存加载，省去解压步骤；不嵌入依赖的 PDB。单文件 EXE 约 7.08 MiB。Logo、图标和 XAML 本身已编入应用资源。Debug 保留普通 DLL 输出，便于 F5 调试。

当前 `App.config` 只有 CLR 启动声明，单文件分发不需要它。若将来增加绑定重定向或其他外部配置，构建脚本会停止打包并提示检查，避免悄悄丢弃配置。`bin/Release` 中剩余的 PDB、XML 文档和配置文件是构建输出，不属于单文件发布包。

Costura 只打包启动器的托管依赖，**不会把 .NET Framework、Python 解释器、一条龙源码、虚拟环境或 uv 打入 EXE**。这些运行要求与之前相同。

测试涵盖命令行校验、Windows 参数引用、源码目录与执行器选择、uv frozen 参数、中文/ANSI 日志、容量上限、并发写入，以及模拟子进程的双路输出排空、错误通知与退出码。测试不调用真实 Python 自动化、不操作游戏。

单文件检查会将 EXE 独自复制到一个临时空目录：直接启动浅色/深色预览并正常关闭，再验证主窗口、日志窗口和全部六个运行依赖可以加载，以及嵌入资源未压缩；目录中没有 DLL 或配置文件。检查结束会清理临时目录。

实际游戏/自动化流程和 UAC 授权仍需要在目标运行环境验证；仓库中的模拟进程测试不代表完成了真实游戏联调。

## 代码入口

- `App.xaml(.cs)`：主题、参数解析、CLI 分支。
- `MainWindow.xaml(.cs)`：启动窗口和启动生命周期。
- `TerminalWindow.xaml(.cs)`：日志窗口与复制/滚动交互。
- `ViewModels/LauncherViewModel.cs`：进度与状态绑定。
- `Services/LaunchEnvironment.cs`：源码目录和 Python/uv 检测。
- `Services/PythonRunner.cs`：启动参数、UTF-8 管道和日志事件。
- `Services/WindowsIntegration.cs`：管理员重启、控制台接入与进程窗口监视。
- `Services/LauncherLog.cs`、`LaunchStage.cs`、`LauncherOptions.cs`：日志缓冲、阶段识别与命令行解析。
- `FodyWeavers.xml`：Release 依赖嵌入与压缩配置。
- `tests/SingleFileSmoke.cs`：单文件独立启动与 WPF 资源加载验证。
