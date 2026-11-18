# AGENTS.md

本文件面向在本仓库中工作的 AI 代理与贡献者。用户文档见 [README.md](README.md)。

## 项目概览

AudioSwitch 是一个 Windows 托盘工具，用于切换系统默认音频输出设备（扬声器 / 耳机 / 显示器）。

- **技术栈**：C# / WPF / .NET 9（`net9.0-windows`）。不引用 WinForms：托盘用 `Shell_NotifyIcon`，颜色选择用自绘 WPF 对话框。
- **零 NuGet 依赖**：Core Audio、`IPolicyConfig`、DWM、Win32 互操作全部手写。
- **无测试框架**：验收依赖程序内置的 CLI 自检与离屏渲染，见下文。
- **交付物**：NSIS 完整版/精简版安装程序、两种便携版 zip、可选单文件 exe。

## 快速命令

```powershell
# 构建（先结束正在运行的实例，否则文件被锁）
Get-Process AudioSwitch -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build AudioSwitch.sln -c Release

# 启动 GUI（会打开设置窗口并驻留托盘）
dotnet run --project src\AudioSwitch\AudioSwitch.csproj

# 无界面自检（Release 产物）
$exe = "src\AudioSwitch\bin\Release\net9.0-windows\AudioSwitch.exe"
& $exe --probe                  # 设备列表 + 三个角色的默认设备
& $exe --self-test              # 切换→校验→还原，退出码 0 为通过
& $exe --hotkey-test "Ctrl+Shift+F9"
& $exe --theme-info             # 系统主题、强调色、壁纸取色、DWM 磨砂可用性
& $exe --tray-test              # 托盘图标能否注册（无交互桌面会失败，属预期）
& $exe --check-update           # 检查 GitHub Releases（可加版本号参数做比较测试）
& $exe --download-update D:\Downloads

# 离屏渲染界面截图（无需可见桌面；会写 docs/appearance）
& $exe --render-all docs\appearance --bg docs\wallpaper.png
```

> WinExe 从 PowerShell 用 `& $exe` 启动不会等待进程；需要退出码时用
> `cmd /v:on /c ""$exe" --self-test & echo RC=!ERRORLEVEL!"`。

## 目录结构

```text
src/AudioSwitch/
  Interop/       Core Audio、IPolicyConfig、DWM/SWC、Win32 互操作（含 TrayNative）
  Audio/         设备枚举、默认设备读写、角色处理
  Services/      SettingsStore、Log、Autostart、SingleInstance、Cli、UpdateService
  Ui/            ThemeService、Palette、Backgrounds、TrayIcon、GlobalHotkey、
                 SettingsWindow、SwitcherPopup、ColorPickerDialog、DeviceItem、DevRenderer
  Assets/        app.ico / app.png（由 tools/make_icon.py 生成）
installer/       NSIS 安装脚本（默认完整版，/DLITE 为精简版）
scripts/         publish.ps1（单文件）、build-installer.ps1（完整版/精简版安装包+便携版）
tools/           图标与示例壁纸生成脚本（Pillow / numpy，仅开发期）
docs/appearance/ 由 --render-all 生成的界面截图
```

## 架构要点

- **启动顺序**（`App.OnStartup`）：异常处理 → CLI 命令 → `SettingsStore` → `ThemeService.Initialize` → `AudioDeviceService` → 单实例 Mutex/IPC → 托盘与全局热键 → 打开设置窗口或静默驻留。
- **设置**：统一通过 `SettingsStore.Current` 读写 `%APPDATA%\AudioSwitch\settings.json`。新增字段必须给默认值，并在 `AppSettings.Normalize()` 中做合法值收敛，保证旧配置可升级。
- **主题**：所有颜色/画刷只定义在 `Ui/Palette.xaml`，`ThemeService` 在运行时替换资源（明暗、强调色、卡片透明度、背景图）。XAML 一律使用 `{DynamicResource XxxBrush}`。
- **窗口材质**：设置窗与切换弹窗都有 `TintLayer / BackgroundImageLayer / ScrimLayer` 三层背景；新窗口需复用该结构并调用 `ThemeService.ApplyWindowEffects(window)`。
- **设备切换**：只通过 `AudioDeviceService.SetDefault(id, includeCommunications)` / `SetRoleDefault` 修改三个角色；UI 层不要直接 P/Invoke 音频接口。
- **托盘**：`Ui/TrayIcon` 不依赖 WinForms，用 `Interop/TrayNative` 的 `Shell_NotifyIcon` + 屏幕外隐藏窗口（`"AudioSwitchTrayWindow"`）接收 `WM_APP+1` 回调；`TaskbarCreated` 后重新注册。不要为省事改回 WinForms（会多引入约 24 MB 运行时）。
- **取色器**：`Ui/ColorPickerDialog` 是自绘 WPF 窗口，颜色校验统一走 `AppSettings.TryParseColor`，不要另写解析。
- **精简版**：同一份源码，`build-installer.ps1` 额外发布 self-contained=false 的 `build\app-lite`；NSIS 以 `/DLITE` 编译并检查 `%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\9.*`（以及用户级目录）。
- **更新检查**：`Services/UpdateService` 访问 GitHub Releases API（`yaanlaan/AudioSwitch`），`App.CheckForUpdatesAsync` 负责缓存 `LatestUpdate` 与错误、托盘提示；“关于”页负责展示、下载（默认 `%LOCALAPPDATA%\AudioSwitch\updates`）并启动安装包。发布时资产名必须是 `AudioSwitch-Setup-<版本>.exe`，否则更新下载找不到。
- **CLI**：输出格式为稳定的 `KEY|value` 行，供脚本与自检解析。新增命令需在 `Cli.TryRun`（或 `App` 的 `--render` 分支）注册，并同步 README「命令行」章节。

## 编码约定

- 每个文件显式 `using`（`ImplicitUsings=disable`），可空引用已启用；使用 file-scoped namespace。
- 私有字段 `_camelCase`；异步方法以 `Async` 结尾；UI 事件处理器命名 `Xxx_Yyy`。
- 注释与用户可见文案使用简体中文；日志采用现有格式，不输出敏感信息。
- 不新增 NuGet 包；平台能力用 `Interop/` 中的自写声明。
- 不在 UI 线程做阻塞 I/O；图片加载、主题取色、设置保存沿用现有异步/去抖模式。

## 陷阱清单

1. **画刷会被冻结**：`Palette.xaml` 加载后的 `SolidColorBrush` 不能原地改 `Color`，否则抛 `InvalidOperationException`，可能在不可见桌面弹错误框导致进程挂起。必须替换资源项：
   ```csharp
   Application.Current.Resources[key] = new SolidColorBrush(color);
   ```
2. **主题画刷必须用 `DynamicResource`**，`StaticResource` 不会随主题更新。
3. **背景图用绝对路径**：`Backgrounds.Load` 内部会 `GetFullPath`，但调用方仍应传入绝对路径；相对路径曾导致图片不显示。
4. **测试会改动真实系统状态**：`--self-test`、`--hotkey-test`、安装/卸载测试都会改默认设备、热键或注册表。测试前记录 `--probe` 结果，结束后确认已还原，必要时手动恢复。
5. **构建前结束进程**，否则 `MSB3027` 文件被占用。
6. **NSIS 打包**：`installer/installer.nsi` 必须保存为 UTF-8 **带 BOM**；`makensis` 需要可写的 `TEMP/TMP`；改安装逻辑后必须重跑 `scripts/build-installer.ps1` 并做静默安装/卸载验证。
7. **受限环境下文件写入可能被拒**：应用写 `%APPDATA%`/`%TEMP%` 失败时日志会静默跳过，此时以 CLI 输出为准。
8. **版本号**：`csproj` 的 `<Version>` 是唯一来源，打包脚本会读取它；发版时同步 README 下载链接与更新记录。
9. **更新检查依赖网络与资产命名**：GitHub API 偶发 SSL 握手失败，`UpdateService` 已用 `SendWithRetryAsync` 重试 3 次；不要改成强制 HTTP/1.1（release-assets 需要 HTTP/2，会稳定失败）。Release 资产名必须匹配 `AudioSwitch-Setup-*.exe`，否则查得到版本却下载不到。
10. **托盘在非交互桌面不可用**：`--tray-test` 在服务会话、无可见桌面的沙箱/远程环境会返回 `Win32 错误 5`（拒绝访问），这是环境限制而非代码缺陷；真实登录桌面正常。不要为此改回 WinForms 或在启动时弹错误框。

## 修改后的验收清单

- [ ] `dotnet build AudioSwitch.sln -c Release` 0 warning 0 error
- [ ] `--probe` 正常，`--self-test` 通过且设备状态已还原
- [ ] `--hotkey-test`：合法组合 `OK`（退出码 0），非法组合 `FAIL`（退出码 1）
- [ ] 交互桌面下 `--tray-test` 返回 `TRAY|OK`（沙箱里错误 5 属预期）
- [ ] 改 UI 后运行 `--render-all`，逐张检查 `docs/appearance` 截图
- [ ] 新增设置项：默认值 + `Normalize` + README 设置表
- [ ] 改安装器：重建后验证完整版与精简版的静默安装、启动、卸载
- [ ] 涉及发布方式：确认 `build\app`（约 127 MB）与 `build\app-lite`（约 0.5 MB）都按预期生成
- [ ] 更新 README / 更新记录，必要时同步本文件

## 发布流程

1. 修改 `src/AudioSwitch/AudioSwitch.csproj` 的 `<Version>`。
2. 更新 README 下载链接、更新记录与截图（如界面有变）。
3. 构建并跑完上面的验收清单。
4. 生成产物：
   ```powershell
   pwsh -File scripts\build-installer.ps1   # Setup/Portable + Lite-Setup/Lite 四个产物
   pwsh -File scripts\publish.ps1           # 可选：单文件 dist\AudioSwitch.exe
   ```
5. 生成 `dist\SHA256SUMS.txt`（四个产物的 SHA-256）。
6. `git add/commit`，打标签 `vX.Y.Z` 并推送。
7. `gh release create vX.Y.Z --title ... --notes-file ...`，上传 Setup、Lite-Setup、Portable、Lite zip、SHA256SUMS（共五个资产）。

## 禁止事项

- 提交 `dist/`、`build/`、`build-tools/`、`.bundle/` 或用户配置文件。
- 在代码、脚本、文档中写入真实用户名或本机绝对路径。
- 为测试永久修改默认音频设备、热键、开机自启或 `settings.json`。
- 手工编辑 `Assets/app.ico`、`docs/appearance/*.png`：分别用 `tools/make_icon.py` 与 `--render-all` 生成。
- 引入遥测、新的运行时依赖，或除更新检查（`UpdateService` → GitHub Releases）之外的其他联网请求。
