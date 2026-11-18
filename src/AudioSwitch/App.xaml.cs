using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AudioSwitch.Audio;
using AudioSwitch.Interop;
using AudioSwitch.Services;
using AudioSwitch.Ui;

namespace AudioSwitch;

public partial class App : Application
{
    private AudioDeviceService? _audio;
    private SettingsStore? _settingsStore;
    private SingleInstance? _singleInstance;
    private TrayIcon? _tray;
    private GlobalHotkey? _hotkey;
    private GlobalHotkey? _volumeHotkey;
    private ThemeService? _theme;
    private SwitcherPopup? _popup;
    private VolumePopup? _volumePopup;
    private SettingsWindow? _settingsWindow;
    private UpdateInfo? _latestUpdate;
    private string? _updateError;
    private bool _updateCheckRunning;
    private bool _isExiting;

    internal AudioDeviceService Audio => _audio!;

    internal AppSettings Settings => _settingsStore!.Current;

    internal SettingsStore Store => _settingsStore!;

    internal ThemeService? Theme => _theme;

    internal bool IsExiting => _isExiting;

    internal bool IsHotkeyRegistered => Settings.HotkeyEnabled && _hotkey?.IsRegistered == true;

    internal string? HotkeyError => _hotkey?.LastError;

    internal bool IsVolumeHotkeyRegistered => Settings.VolumeHotkeyEnabled && _volumeHotkey?.IsRegistered == true;

    internal string? VolumeHotkeyError => _volumeHotkey?.LastError;

    /// <summary>最近一次检查到的新版本；没有新版本时为 null。</summary>
    internal UpdateInfo? LatestUpdate => _latestUpdate;

    /// <summary>最近一次检查更新的错误信息。</summary>
    internal string? UpdateError => _updateError;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterExceptionHandlers();

        int? exitCode = Cli.TryRun(e.Args);
        if (exitCode.HasValue)
        {
            Shutdown(exitCode.Value);
            return;
        }

        Log.Info($"启动 AudioSwitch {typeof(App).Assembly.GetName().Version}，参数：{string.Join(' ', e.Args)}");

        _settingsStore = new SettingsStore();
        _theme = ThemeService.Initialize(_settingsStore);

        try
        {
            _audio = new AudioDeviceService();
        }
        catch (Exception ex)
        {
            Log.Error("初始化音频服务失败", ex);
            ShowFatalError($"无法访问 Windows 音频接口。\n\n{ex.Message}\n\n日志：{Log.FilePath}");
            Shutdown(1);
            return;
        }

        bool wantsRender = Array.Exists(e.Args, a =>
            string.Equals(a, "--render", StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, "--render-all", StringComparison.OrdinalIgnoreCase));

        if (wantsRender)
        {
            _hotkey = new GlobalHotkey();
            bool hotkeyOk = ApplyHotkey(out string renderHotkeyMessage);
            Console.Out.WriteLine(hotkeyOk ? $"HOTKEY|OK|{Settings.Hotkey}" : $"HOTKEY|FAIL|{renderHotkeyMessage}");

            _volumeHotkey = new GlobalHotkey(0xA572);
            bool volumeHotkeyOk = ApplyVolumeHotkey(out string renderVolumeHotkeyMessage);
            Console.Out.WriteLine(volumeHotkeyOk
                ? $"VOLUME|OK|{Settings.VolumeHotkey}"
                : $"VOLUME|FAIL|{renderVolumeHotkeyMessage}");
        }


        if (DevRenderer.TryRun(e.Args, this, out int renderExitCode))
        {
            Shutdown(renderExitCode);
            return;
        }

        _singleInstance = new SingleInstance();
        if (!_singleInstance.IsFirstInstance)
        {
            // 已有实例：让它弹出切换列表，本进程退出
            SingleInstance.NotifyFirstInstance("show");
            Shutdown(0);
            return;
        }

        _singleInstance.MessageReceived += OnIpcMessage;
        _singleInstance.StartServer();

        Autostart.Reconcile(_settingsStore.Current);

        _tray = new TrayIcon();
        _tray.Clicked += ToggleSwitcher;
        UpdateTrayTooltip();

        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += ToggleSwitcher;
        if (!ApplyHotkey(out string hotkeyMessage))
        {
            Log.Warn($"全局热键不可用：{hotkeyMessage}");
        }

        _volumeHotkey = new GlobalHotkey(0xA572);
        _volumeHotkey.Pressed += ToggleVolumePopup;
        if (!ApplyVolumeHotkey(out string volumeHotkeyMessage))
        {
            Log.Warn($"音量快捷键不可用：{volumeHotkeyMessage}");
        }

        if (Settings.AutoCheckUpdates)
        {
            _ = CheckForUpdatesAsync(notify: true);
        }

        bool silent = Array.Exists(e.Args, a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!silent)
        {
            OpenSettings();
        }
    }

    internal void SaveSettings() => _settingsStore?.Save();

    internal void OnIpcMessage(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (message.Equals("show", StringComparison.OrdinalIgnoreCase))
            {
                ShowSwitcher();
            }
            else if (message.Equals("settings", StringComparison.OrdinalIgnoreCase))
            {
                OpenSettings();
            }
            else if (message.Equals("volume", StringComparison.OrdinalIgnoreCase))
            {
                ShowVolumePopup();
            }
        }));
    }

    internal void ToggleSwitcher()
    {
        if (_popup is not null)
        {
            CloseSwitcher();
            return;
        }

        ShowSwitcher();
    }

    internal void ShowSwitcher()
    {
        if (_popup is not null)
        {
            _popup.Activate();
            return;
        }

        CloseVolumePopup();

        try
        {
            var popup = new SwitcherPopup(this);
            _popup = popup;
            popup.Closed += (_, _) =>
            {
                if (ReferenceEquals(_popup, popup))
                {
                    _popup = null;
                }
            };
            popup.Show();
            popup.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("打开切换弹窗失败", ex);
            _popup = null;
        }
    }

    private void CloseSwitcher()
    {
        SwitcherPopup? popup = _popup;
        _popup = null;
        popup?.Close();
    }

    internal void ToggleVolumePopup()
    {
        if (_volumePopup is not null)
        {
            CloseVolumePopup();
            return;
        }

        ShowVolumePopup();
    }

    internal void ShowVolumePopup()
    {
        if (_volumePopup is not null)
        {
            _volumePopup.Activate();
            return;
        }

        CloseSwitcher();

        try
        {
            var popup = new VolumePopup(this);
            _volumePopup = popup;
            popup.Closed += (_, _) =>
            {
                if (ReferenceEquals(_volumePopup, popup))
                {
                    _volumePopup = null;
                }
            };
            popup.Show();
            popup.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("打开音量弹窗失败", ex);
            _volumePopup = null;
        }
    }

    private void CloseVolumePopup()
    {
        VolumePopup? popup = _volumePopup;
        _volumePopup = null;
        popup?.Close();
    }

    internal void OpenSettings()
    {
        try
        {
            if (_settingsWindow is null)
            {
                var window = new SettingsWindow(this);
                _settingsWindow = window;
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_settingsWindow, window))
                    {
                        _settingsWindow = null;
                    }
                };
            }

            _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            _settingsWindow.RefreshCurrentDevice();
        }
        catch (Exception ex)
        {
            Log.Error("打开设置窗口失败", ex);
        }
    }

    internal bool ApplyHotkey(out string message)
    {
        if (_hotkey is null)
        {
            message = "热键尚未初始化";
            return false;
        }

        if (!Settings.HotkeyEnabled)
        {
            _hotkey.Unregister();
            message = "快捷键已关闭";
            return true;
        }

        if (_hotkey.TryRegister(Settings.Hotkey))
        {
            message = "已生效";
            return true;
        }

        message = _hotkey.LastError ?? "注册失败";
        return false;
    }

    internal bool ApplyVolumeHotkey(out string message)
    {
        if (_volumeHotkey is null)
        {
            message = "音量快捷键尚未初始化";
            return false;
        }

        if (!Settings.VolumeHotkeyEnabled)
        {
            _volumeHotkey.Unregister();
            message = "音量快捷键已关闭";
            return true;
        }

        if (_volumeHotkey.TryRegister(Settings.VolumeHotkey))
        {
            message = "已生效";
            return true;
        }

        message = _volumeHotkey.LastError ?? "注册失败";
        return false;
    }

    internal void HandleDeviceChanged(AudioDevice device)
    {
        UpdateTrayTooltip();
        Log.Info($"用户切换到 {device.DisplayName} ({device.Id})");
    }

    /// <summary>检查 GitHub Releases；发现新版本时更新 LatestUpdate，notify 为真时弹托盘气泡。</summary>
    internal async Task<UpdateInfo?> CheckForUpdatesAsync(bool notify)
    {
        if (_updateCheckRunning)
        {
            return _latestUpdate;
        }

        _updateCheckRunning = true;
        try
        {
            UpdateInfo info = await UpdateService.FetchLatestAsync();
            _updateError = null;

            if (UpdateService.IsNewer(info, UpdateService.CurrentVersion))
            {
                bool isFirstNotice = _latestUpdate is null || _latestUpdate.Version != info.Version;
                _latestUpdate = info;
                if (notify && isFirstNotice)
                {
                    _tray?.ShowBalloon(
                        $"发现新版本 {info.DisplayVersion}",
                        "打开设置 → 关于，即可下载并安装。");
                }
            }
            else
            {
                _latestUpdate = null;
            }

            return _latestUpdate;
        }
        catch (Exception ex)
        {
            _updateError = ex.Message;
            Log.Warn($"检查更新失败：{ex.Message}");
            return null;
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    internal void UpdateTrayTooltip()
    {
        if (_tray is null || _audio is null)
        {
            return;
        }

        try
        {
            string? id = _audio.GetDefaultId(ERole.eConsole);
            AudioDevice? device = null;
            if (id is not null)
            {
                device = _audio.GetOutputDevices()
                    .FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            }

            _tray.SetTooltip(device is null ? "AudioSwitch" : $"AudioSwitch — 当前：{device.DisplayName}");
        }
        catch (Exception ex)
        {
            Log.Warn($"更新托盘提示失败：{ex.Message}");
        }
    }

    internal void ExitApplication()
    {
        _isExiting = true;
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _popup = null;
        _volumePopup = null;
        _settingsWindow = null;
        _theme?.Shutdown();
        _hotkey?.Dispose();
        _volumeHotkey?.Dispose();
        _tray?.Dispose();
        _audio?.Dispose();
        _singleInstance?.Dispose();
        Log.Info("AudioSwitch 已退出");
        base.OnExit(e);
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Log.Error("未处理异常", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI 线程未处理异常", e.Exception);
        e.Handled = true;
    }

    private static void ShowFatalError(string message)
    {
        try
        {
            MessageBox.Show(message, "AudioSwitch", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 没有桌面环境时忽略
        }
    }
}
