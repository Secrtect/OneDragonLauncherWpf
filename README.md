# OneDragon Launcher · WPF UI

将 `OneDragon-Launcher-Slint-main` 中的 Rust/Slint 启动器迁移到 C#、WPF 和 WPF UI 4.3.0。原始 Slint 目录保留，WPF 程序不依赖 Rust 或 Slint。

## 运行

需要 Windows 10/11、.NET Framework 4.7.2 或更高版本，以及已经安装好的一条龙 Python 项目与运行环境。

发布单文件位于 **`artifacts/OneDragon-Launcher.exe`**，ZIP 包 `artifacts/OneDragon-Launcher-Windows.zip` 中也只有这个 EXE。部署时只需复制这个 EXE 到一条龙项目根目录（与 `src`、`.venv`、`.install` 同级），不需要随附 WPF UI DLL 或 `.exe.config`。也可以保留启动器在独立目录，通过参数指定项目：

```powershell
.\OneDragon-Launcher.exe --project "D:\ZenlessZoneZero-OneDragon"
```

Visual Studio 选择 **Debug** 后直接按 **F5**，未指定项目时默认进入界面预览，不需要 Python 源码或运行环境，也不请求管理员权限。Debug 下单独指定 `--dark` 仍然是预览。

需要联调真实 Python 后端时，在“项目属性 → 调试 → 命令行参数”填写 `--project "D:\ZenlessZoneZero-OneDragon"`；`--onedragon` 也会进入真实 CLI 流程。Release 默认保持真实启动。

也可以显式预览界面，不提权、不运行 Python、不更新代码：

```powershell
.\OneDragon-Launcher.exe --preview
.\OneDragon-Launcher.exe --preview --dark
```

正常启动时，环境检查通过后会请求管理员权限，保持原版行为。帮助、版本和预览模式不需要管理员权限。

## 窗口与行为

- 启动窗口：520 × 300 DIP，原始 Logo、标题、状态、动态进度条和“查看日志”，WPF UI 标题栏与按钮。
- 日志窗口：580 × 420 DIP，可调整大小；实时显示 UTF-8 标准输出/错误输出、清除 ANSI 控制码、选择与复制日志，支持 Esc 关闭和重新打开。
- 默认在启动时读取系统深浅色主题，也可用 `--dark` 指定深色。浅色使用参考截图的淡蓝渐变底色；这里没有复用 Slint 的 Mica/窗口 Hook 实现。原生圆角由支持的 Windows 版本提供。
- 按钮、日志图标和正常进度条使用 WPF UI 动态强调色资源，启动时读取 Windows 强调色，运行期间随系统个性化设置更新；失败进度条使用主题的错误色。强调色更新不改变已选择的深浅色模式。
- 启动期间使用不确定进度条，不展示完成百分比。日志匹配更新阶段文字，通过阶段枚举防止倒退；Git 同步期间，左下角实时显示最新日志正文（去掉时间和级别），较长内容可悬停查看全文，进入下一阶段后恢复阶段说明。失败后保留错误文字，进度条停止动画并显示整条错误色。
- 日志在底部时自动跟随；向上查看或选中文字时停止追尾。内存日志最多保留 10,000 条且不超过 1,048,576 个字符，复制的是当前保留日志。
- 检测到错误会自动打开日志。等待 30 秒仍未就绪时保留窗口并提示，之后继续监视，不静默隐藏失败。
- 每 500ms 检查本次启动进程及其子进程的可见窗口。检测到标题匹配“绝区零 一条龙”的可见窗口即视为主界面已出现，立即交接后台、关闭启动窗口及日志窗口，并彻底退出启动器进程。
- GUI 启动时先挂起创建 Python/uv，加入启用 `KILL_ON_JOB_CLOSE` 的 Windows Job 后再恢复运行。主界面出现前关闭启动器，会终止本次启动的整个后台进程树，避免稍后弹出 Python 窗口。
- 检测到主界面后，先取消 Job 的 `KILL_ON_JOB_CLOSE` 标志，再释放句柄，Python 独立运行。不能直接关闭仍启用该标志的 Job 句柄，否则会终止 Python。
- 启动期间出现错误或等待超过 30 秒时，打开日志并继续监视；主界面随后出现时仍正常交接并退出启动器。主界面出现前手动关闭会终止后台。
- GUI 输出由已有 Python 进程内的线程通过内存管道转发。启动器退出后，线程继续读取并丢弃输出，避免 Python 及其子进程管道断开或阻塞；不新增日志文件或中转进程。Python 项目自身的日志策略保持不变。CLI 模式仍等待 Python 退出并返回退出码。

## 后端兼容

源码目录查找：显式 `--project` 优先，否则从 EXE 目录向上最多检查五层，以及当前工作目录；支持直接的 `src/zzz_od`，以及 `ZenlessZoneZero-OneDragon`、`ZenlessZoneZero-OneDragon-main` 子目录。

仅支持已通过一条龙安装器配置好的项目环境。启动前检查目标项目的 `.install/uv/uv.exe`，缺失时提示运行安装器安装或修复环境，即使已有 Python 也不会继续启动。不从 PATH 或启动器所在的其他目录查找 uv/Python。

内置 uv 存在后，优先使用目标项目中的 `.venv/Scripts/python.exe`、`.venv/python.exe`、`.install/python/python.exe` 执行引导代码；没有这些解释器时使用该项目的 `.install/uv/uv.exe run --frozen python`。

后端仍读取安装器保存的 `config/env.yml` 中的 `uv_path`，WPF 不填写或覆盖这个配置。内置 uv 存在不代表配置完整；若后端提示 UV 路径无效，需通过安装器修复。

工作目录固定为实际 Python 项目根目录，注入 `PYTHONPATH`、`PYTHONUNBUFFERED=1`、`PYTHONUTF8=1`、`PYTHONIOENCODING=utf-8`。

GUI 模式保留原版入口：

```python
from one_dragon.devtools.python_launcher import run_python
run_python(['zzz_od', 'gui', 'app'], piped=True)
```

GUI 和 CLI 模式均在导入 Python 启动器前注入 colorama 兼容 shim；只有缺少 colorama 时才启用，已安装的 colorama 不会被覆盖。隐藏控制台下的标准输入会立即提供 EOF，避免后端 `input()` 等待一个不存在的交互终端。

**Python 项目自身的限制**：当前本机 `python_launcher.verify_working_directory()` 会拒绝含空格或中文的项目路径。C# 的参数引用与 UTF-8 处理不改变这项后端校验，部署建议使用例如 `D:\ZenlessZoneZero-OneDragon` 的路径。

## 命令行

当前版本为 `2.5.6.0`，`--version` 显示 `OneDragon Launcher WPF v2.5.6.0`，取自 `AssemblyVersion`。Windows 文件、应用清单及项目发布版本也使用 `2.5.6.0`。修改版本后需重新构建并替换部署的 EXE；Git 标签不会自动修改程序版本。

```text
-h, --help                  帮助
-v, --version               版本
-o, --onedragon             一条龙 CLI 模式
-c, --close-game            运行后关闭游戏，需要 --onedragon
-s, --shutdown [秒]         运行后关机，默认 60 秒，需要 --onedragon
    --hibernate            运行后休眠，需要 --onedragon
    --sleep                运行后睡眠，需要 --onedragon
-i, --instance 1,2          账号实例，需要 --onedragon
    --project <目录>       Python 项目根目录
    --preview              仅预览界面
    --dark                 深色主题
```

`--shutdown`、`--hibernate`、`--sleep` 三选一，可与 `--close-game`、`--instance` 组合。休眠和睡眠参数不接受秒数。使用休眠或睡眠需要 Python 后端版本支持对应参数。

```powershell
# 运行账号 1、2，完成后关闭游戏并休眠
.\OneDragon-Launcher.exe -o -i 1,2 -c --hibernate

# 完成后睡眠
.\OneDragon-Launcher.exe -o --sleep
```

CLI 模式通过 `python -u -c` 先执行 colorama 兼容注入，再调用 `run_python(['zzz_od', 'application', 'zzz_application_launcher'], no_windows=False, args=..., piped=True)`，将 `--onedragon` 之外的后端参数透传并返回 Python 退出码。参数校验和管理员提权由 WPF 负责，无需导入依赖 pyuac 的 Python EXE 包装入口；需要提权时等待管理员进程并保留退出码。命令行里的关闭游戏、关机、休眠和睡眠行为由 Python 后端执行。

## 构建与验证

使用 Visual Studio 的“.NET 桌面开发”组件和 .NET Framework 4.7.2 targeting pack。依赖版本固定在 `packages.config`（包括 `Fody 6.9.3` 和 `Costura.Fody 6.2.0`），缺少程序包时构建脚本会自动通过 NuGet 还原到 `packages/`。

在仓库根目录运行：

```powershell
.\build.ps1
```

脚本定位 Visual Studio MSBuild，构建 Release、运行后端检查和单文件启动检查，生成 `artifacts/OneDragon-Launcher.exe` 与只包含该 EXE 的 ZIP。测试程序不依赖外部测试框架。

### GitHub Actions 自动打包

正式发布使用 **Release Windows ZIP** 工作流：在 GitHub 的 Windows 虚拟机上还原依赖、编译 Release EXE 并运行测试，然后调用 PowerShell `Compress-Archive` 压缩，最后将 `OneDragon-Launcher-Windows.zip` 上传到 **Releases** 的对应版本附件中。整个过程不依赖本地编译产物。

触发方式：在 **Actions → Release Windows ZIP → Run workflow** 选择要发布的分支，填写新版本号（如 `v1.0.1`）；或者推送 `v1.0.1` 这样的版本标签。手动发布时，尚不存在的标签会指向本次编译的提交；已有标签必须与所选源码提交一致。版本号格式为 `v主版本.次版本.修订版本`。

正式版本在仓库 **Releases → 对应版本 → Assets** 中下载 ZIP，解压后只有 `OneDragon-Launcher.exe`。

推送到 `main`、向 `main` 提交 Pull Request 时，`Build Windows ZIP` 工作流会在 GitHub 的 Windows 运行器上执行 `build.ps1 -SkipArchive`，完成依赖还原、Release EXE 构建和全部测试，再单独调用 PowerShell `Compress-Archive` 将 EXE 压缩成 ZIP 并上传。也可以在仓库 **Actions → Build Windows ZIP → Run workflow** 手动触发。本地直接运行 `build.ps1` 仍会自动完成编译、测试和压缩。

普通构建的临时产物位于运行记录的 **Artifacts**，保留 30 天；正式发布的 ZIP 位于 **Releases**，不受这个保留期限影响。

Release 使用 `FodyWeavers.xml` 配置的 Costura 将托管依赖嵌入 EXE，设置 `DisableCompression="true"` 关闭压缩，启动时直接从内存加载，省去解压步骤；不嵌入依赖的 PDB。单文件 EXE 约 7.08 MiB。Logo、图标和 XAML 本身已编入应用资源。Debug 保留普通 DLL 输出，便于 F5 调试。

`build.ps1` 固定构建 Release x64，生成的 EXE 需要 64 位 Windows。当前 `App.config` 只有 CLR 启动声明，单文件分发不需要它。若将来增加绑定重定向或其他外部配置，构建脚本会停止打包并提示检查，避免悄悄丢弃配置。`bin/x64/Release` 中剩余的 PDB、XML 文档和配置文件是构建输出，不属于单文件发布包。

Costura 只打包启动器的托管依赖，**不会把 .NET Framework、Python 解释器、一条龙源码、虚拟环境或 uv 打入 EXE**。这些运行要求与之前相同。

测试涵盖命令行校验、Windows 参数引用、源码目录与执行器选择、uv frozen 参数、中文/ANSI 日志、容量上限、并发写入，以及模拟子进程的双路输出排空、错误通知与退出码。测试不调用真实 Python 自动化、不操作游戏。

有真实 Python 解释器时，可额外运行 `python tests/OutputBridgeSmoke.py`：验证启动器管道关闭后 Python 及其子进程仍能大量输出，以及启动失败时中文日志、异常堆栈、尾部输出和退出码完整保留。此检查不依赖一条龙模块，不新增运行日志文件。

运行 `python tests/BootstrapSmoke.py` 可验证 GUI/CLI 在缺少 colorama 时的兼容注入、已安装模块不被覆盖、模块内部缺失依赖正常报错，以及 CLI 参数和退出码传递；使用隔离的模拟模块，不启动游戏。

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
