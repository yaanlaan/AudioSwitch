using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioSwitch.Interop;
using AudioSwitch.Services;
using Forms = System.Windows.Forms;

namespace AudioSwitch.Ui;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private bool _initializing = true;
    private bool _suppressNavigation;
    private bool _recordingHotkey;
    private bool _checkedUpdates;
    private DispatcherTimer? _saveDebounce;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();

        string versionText = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.2.0";
        VersionText.Text = $"v{versionText}";
        AboutVersionText.Text = $"版本 {versionText}";
        SettingsPathText.Text = SettingsStore.FilePath;

        SourceInitialized += (_, _) => ApplyThemeToWindow();
        Loaded += OnLoaded;
        Activated += (_, _) => RefreshCurrentDevice();
        Closing += OnClosing;
        Closed += OnClosed;

        if (_app.Theme is not null)
        {
            _app.Theme.Changed += OnThemeChanged;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        RefreshCurrentDevice();
        _initializing = false;
    }

    /// <summary>离屏渲染用：不触发窗口 Loaded，也能把界面填充成正常状态。</summary>
    internal void PrepareForRender(string page = "general")
    {
        LoadSettings();
        RefreshCurrentDevice();
        _initializing = false;
        SelectPage(page);
    }

    internal void SelectPage(string tag)
    {
        _suppressNavigation = true;
        try
        {
            switch (tag)
            {
                case "appearance":
                    NavAppearance.IsChecked = true;
                    break;
                case "popup":
                    NavPopup.IsChecked = true;
                    break;
                case "hotkey":
                    NavHotkey.IsChecked = true;
                    break;
                case "about":
                    NavAbout.IsChecked = true;
                    break;
                default:
                    NavGeneral.IsChecked = true;
                    tag = "general";
                    break;
            }

            PageGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
            PageAppearance.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
            PagePopup.Visibility = tag == "popup" ? Visibility.Visible : Visibility.Collapsed;
            PageHotkey.Visibility = tag == "hotkey" ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _suppressNavigation = false;
        }
    }

    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressNavigation || sender is not RadioButton radio)
        {
            return;
        }

        SelectPage(radio.Tag as string ?? "general");
    }

    private void LoadSettings()
    {
        AppSettings settings = _app.Settings;

        // 以注册表实际状态为准，避免设置文件与系统不一致
        bool autostart = Autostart.IsEnabled();
        if (autostart != settings.StartWithWindows)
        {
            settings.StartWithWindows = autostart;
            _app.SaveSettings();
        }

        StartWithWindowsToggle.IsChecked = autostart;
        CloseToTrayToggle.IsChecked = settings.CloseToTray;
        SwitchCommunicationsToggle.IsChecked = settings.SwitchCommunications;

        UpdateAppearanceControls();
        UpdateBackgroundControls();
        UpdatePopupControls();
        UpdateHotkeyDisplay();
        SettingsPathText.Text = SettingsStore.FilePath;

        AutoCheckUpdatesToggle.IsChecked = settings.AutoCheckUpdates;
        RefreshUpdateState();
    }

    private void UpdateAppearanceControls()
    {
        AppSettings settings = _app.Settings;

        ThemeSystemRadio.IsChecked = settings.UseSystemTheme;
        ThemeLightRadio.IsChecked = settings.IsLightThemeFixed;
        ThemeDarkRadio.IsChecked = settings.IsDarkThemeFixed;

        ColorSystemRadio.IsChecked = string.Equals(settings.ColorSource, "system", StringComparison.OrdinalIgnoreCase);
        ColorFixedRadio.IsChecked = string.Equals(settings.ColorSource, "fixed", StringComparison.OrdinalIgnoreCase);
        ColorImageRadio.IsChecked = string.Equals(settings.ColorSource, "image", StringComparison.OrdinalIgnoreCase);

        MaterialAcrylicRadio.IsChecked = settings.UseAcrylic;
        MaterialSolidRadio.IsChecked = !settings.UseAcrylic;
        MaterialAcrylicRadio.IsEnabled = ThemeNative.SupportsMica;
        MaterialHintText.Text = ThemeNative.SupportsMica
            ? "磨砂玻璃会透出桌面与背景图片"
            : "当前 Windows 版本不支持磨砂材质，已使用纯色";

        bool fixedColor = string.Equals(settings.ColorSource, "fixed", StringComparison.OrdinalIgnoreCase);
        AccentPanel.IsEnabled = fixedColor;
        AccentPanel.Opacity = fixedColor ? 1.0 : 0.45;
        ColorHintText.Text = fixedColor
            ? "挑选一个强调色，或点“自定义…”使用系统取色器。"
            : "切换到“固定配色”后可挑选强调色。";

        GlassOpacitySlider.Value = Math.Clamp(settings.GlassOpacity, 0.2, 1.0) * 100;
        GlassOpacityText.Text = $"{(int)Math.Round(GlassOpacitySlider.Value)}%";

        SelectAccentSwatch(settings.AccentColor);
    }

    private void SelectAccentSwatch(string color)
    {
        foreach (RadioButton swatch in AccentPanel.Children.OfType<RadioButton>())
        {
            string tag = swatch.Tag as string ?? string.Empty;
            swatch.IsChecked = string.Equals(tag, color, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void UpdateBackgroundControls()
    {
        AppSettings settings = _app.Settings;
        string? path = settings.BackgroundImage;
        bool hasImage = !string.IsNullOrWhiteSpace(path) && File.Exists(path);

        if (hasImage)
        {
            BackgroundNameText.Text = Path.GetFileName(path!);
            ClearBackgroundButton.IsEnabled = true;
            BackgroundThumb.Source = Backgrounds.Load(path, 96);
        }
        else
        {
            BackgroundNameText.Text = "未设置，使用纯色或磨砂背景";
            BackgroundThumb.Source = null;
            ClearBackgroundButton.IsEnabled = false;
        }

        BackgroundOptionsPanel.IsEnabled = hasImage;
        BackgroundOptionsPanel.Opacity = hasImage ? 1 : 0.45;
        OpacitySliderControl.Value = Math.Clamp(settings.BackgroundOpacity, 0.2, 1.0) * 100;
        OpacityValueText.Text = $"{(int)Math.Round(OpacitySliderControl.Value)}%";
        BlurToggle.IsChecked = settings.BlurBackground;
    }

    private void UpdatePopupControls()
    {
        AppSettings settings = _app.Settings;

        PopupStyleListRadio.IsChecked = string.Equals(settings.PopupStyle, "list", StringComparison.OrdinalIgnoreCase);
        PopupStyleCardsRadio.IsChecked = !PopupStyleListRadio.IsChecked;

        bool cards = PopupStyleCardsRadio.IsChecked == true;
        PopupColumnsRow.IsEnabled = cards;
        PopupColumnsRow.Opacity = cards ? 1.0 : 0.45;

        PopupColumnsAutoRadio.IsChecked = settings.PopupColumns == 0;
        PopupColumns2Radio.IsChecked = settings.PopupColumns == 2;
        PopupColumns3Radio.IsChecked = settings.PopupColumns == 3;
        PopupColumns4Radio.IsChecked = settings.PopupColumns == 4;

        PopupPositionCursorRadio.IsChecked = string.Equals(settings.PopupPosition, "cursor", StringComparison.OrdinalIgnoreCase);
        PopupPositionCenterRadio.IsChecked = !PopupPositionCursorRadio.IsChecked;

        PopupShowIconsToggle.IsChecked = settings.PopupShowIcons;
        PopupCloseOnSwitchToggle.IsChecked = settings.PopupCloseOnSwitch;
        AnimationToggle.IsChecked = settings.AnimationEnabled;
    }

    private void UpdateHotkeyDisplay()
    {
        HotkeyDisplay.Text = _app.Settings.Hotkey;
        HotkeyEnabledToggle.IsChecked = _app.Settings.HotkeyEnabled;

        if (!_app.Settings.HotkeyEnabled)
        {
            SetHotkeyStatus("已关闭，托盘图标仍可使用", isError: false);
            return;
        }

        if (_app.IsHotkeyRegistered)
        {
            SetHotkeyStatus($"已生效：{_app.Settings.Hotkey}", isError: false);
        }
        else
        {
            SetHotkeyStatus(_app.HotkeyError ?? "热键未生效", isError: true);
        }
    }

    public void RefreshCurrentDevice()
    {
        try
        {
            var devices = _app.Audio.GetOutputDevices();
            var current = devices.FirstOrDefault(d => d.IsDefaultConsole);

            CurrentDeviceName.Text = current?.DisplayName ?? "未找到可用输出设备";
            CurrentDeviceSubtitle.Text = current?.Subtitle ?? string.Empty;
            CurrentDeviceSubtitle.Visibility = string.IsNullOrWhiteSpace(CurrentDeviceSubtitle.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            CurrentDeviceIcon.Text = current is null ? "\uE767" : DeviceGlyphs.For(current.FormFactor);
        }
        catch (Exception ex)
        {
            Log.Error("刷新当前设备失败", ex);
            CurrentDeviceName.Text = "读取设备失败";
            CurrentDeviceSubtitle.Visibility = Visibility.Collapsed;
        }
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton radio)
        {
            return;
        }

        _app.Settings.ThemeMode = radio.Tag as string ?? "system";
        _app.SaveSettings();
        _app.Theme?.RefreshAll();
        SetFooterStatus("主题已更新", isError: false);
    }

    private void ColorSourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton radio)
        {
            return;
        }

        _app.Settings.ColorSource = radio.Tag as string ?? "system";
        _app.SaveSettings();
        UpdateAppearanceControls();
        _app.Theme?.RefreshAll();
        SetFooterStatus("配色来源已更新", isError: false);
    }

    private void AccentSwatch_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton radio)
        {
            return;
        }

        string? color = radio.Tag as string;
        if (string.IsNullOrWhiteSpace(color))
        {
            return;
        }

        _app.Settings.AccentColor = color;
        _app.Settings.ColorSource = "fixed";
        _app.SaveSettings();
        _initializing = true;
        UpdateAppearanceControls();
        _initializing = false;
        _app.Theme?.RefreshAll();
        SetFooterStatus("强调色已更新", isError: false);
    }

    private void CustomAccent_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var dialog = new Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = ParseMediaColor(_app.Settings.AccentColor),
            };

            if (dialog.ShowDialog() != Forms.DialogResult.OK)
            {
                return;
            }

            string hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            _app.Settings.AccentColor = hex;
            _app.Settings.ColorSource = "fixed";
            _app.SaveSettings();
            _initializing = true;
            UpdateAppearanceControls();
            _initializing = false;
            _app.Theme?.RefreshAll();
            SetFooterStatus($"强调色已设为 {hex}", isError: false);
        }
        catch (Exception ex)
        {
            Log.Error("打开取色器失败", ex);
            SetFooterStatus("无法打开系统取色器", isError: true);
        }
    }

    private static System.Drawing.Color ParseMediaColor(string? hex)
    {
        if (AppSettings.TryParseColor(hex, out uint argb))
        {
            return System.Drawing.Color.FromArgb(
                0xFF,
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
        }

        return System.Drawing.Color.FromArgb(0x5B, 0x9D, 0xFF);
    }

    private void MaterialRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton radio)
        {
            return;
        }

        _app.Settings.Material = radio.Tag as string ?? "acrylic";
        _app.SaveSettings();
        _app.Theme?.RefreshAll();
        SetFooterStatus(_app.Settings.UseAcrylic ? "已启用磨砂玻璃" : "已切换为纯色材质", isError: false);
    }

    private void GlassOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
        {
            return;
        }

        _app.Settings.GlassOpacity = Math.Clamp(e.NewValue / 100.0, 0.2, 1.0);
        GlassOpacityText.Text = $"{(int)Math.Round(e.NewValue)}%";
        _app.Theme?.Apply();
        ScheduleSave();
    }

    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择背景图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            _app.Settings.BackgroundImage = dialog.FileName;
            _app.SaveSettings();
            _app.Theme?.RefreshAll();
            UpdateBackgroundControls();
            SetFooterStatus($"背景图片已更新：{Path.GetFileName(dialog.FileName)}", isError: false);
        }
        catch (Exception ex)
        {
            Log.Error("选择背景图片失败", ex);
            SetFooterStatus("选择背景图片失败", isError: true);
        }
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.BackgroundImage = null;
        _app.SaveSettings();
        _app.Theme?.RefreshAll();
        UpdateBackgroundControls();
        SetFooterStatus("已清除背景图片", isError: false);
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
        {
            return;
        }

        _app.Settings.BackgroundOpacity = Math.Clamp(e.NewValue / 100.0, 0.2, 1.0);
        OpacityValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
        _app.Theme?.Apply();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        // 拖动时每帧都写盘太浪费，停手 150ms 后再保存。
        _saveDebounce ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _saveDebounce.Stop();
        _saveDebounce.Tick -= SaveDebounceTick;
        _saveDebounce.Tick += SaveDebounceTick;
        _saveDebounce.Start();
    }

    private void SaveDebounceTick(object? sender, EventArgs e)
    {
        _saveDebounce?.Stop();
        _app.SaveSettings();
    }

    private void BlurToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _app.Settings.BlurBackground = BlurToggle.IsChecked == true;
        _app.SaveSettings();
        _app.Theme?.RefreshAll();
        SetFooterStatus(_app.Settings.BlurBackground ? "背景图片已模糊" : "背景图片已清晰显示", isError: false);
    }

    private void PopupStyleRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton radio)
        {
            return;
        }

        _app.Settings.PopupStyle = radio.Tag as string ?? "cards";
        _app.SaveSettings();
        bool cards = _app.Settings.PopupStyle != "list";
        PopupColumnsRow.IsEnabled = cards;
        PopupColumnsRow.Opacity = cards ? 1.0 : 0.45;
        SetFooterStatus(_app.Settings.PopupStyle == "list" ? "切换弹窗已改为列表样式" : "切换弹窗已改为磁贴样式", isError: false);
    }

    private void PopupColumnsRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton radio)
        {
            return;
        }

        _app.Settings.PopupColumns = int.TryParse(radio.Tag as string, out int columns) ? columns : 0;
        _app.SaveSettings();
        SetFooterStatus(_app.Settings.PopupColumns == 0 ? "每行数量：自动" : $"每行数量：{_app.Settings.PopupColumns}", isError: false);
    }

    private void PopupPositionRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing || sender is not RadioButton radio)
        {
            return;
        }

        _app.Settings.PopupPosition = radio.Tag as string ?? "center";
        _app.SaveSettings();
        SetFooterStatus(_app.Settings.PopupPosition == "cursor" ? "弹窗将出现在鼠标附近" : "弹窗将出现在屏幕中央", isError: false);
    }

    private void PopupShowIconsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _app.Settings.PopupShowIcons = PopupShowIconsToggle.IsChecked == true;
        _app.SaveSettings();
        SetFooterStatus(_app.Settings.PopupShowIcons ? "弹窗将显示设备图标" : "弹窗已隐藏设备图标", isError: false);
    }

    private void PopupCloseOnSwitchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _app.Settings.PopupCloseOnSwitch = PopupCloseOnSwitchToggle.IsChecked == true;
        _app.SaveSettings();
        SetFooterStatus(_app.Settings.PopupCloseOnSwitch ? "切换后自动关闭弹窗" : "切换后保留弹窗", isError: false);
    }

    private void AnimationToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _app.Settings.AnimationEnabled = AnimationToggle.IsChecked == true;
        _app.SaveSettings();
        SetFooterStatus(_app.Settings.AnimationEnabled ? "已启用弹窗动画" : "已关闭弹窗动画", isError: false);
    }

    private void PreviewPopup_Click(object sender, RoutedEventArgs e) => _app.ShowSwitcher();

    private void StartWithWindowsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        bool enabled = StartWithWindowsToggle.IsChecked == true;
        if (!Autostart.SetEnabled(enabled))
        {
            StartWithWindowsToggle.IsChecked = !enabled;
            SetFooterStatus("设置开机自启失败，请查看日志", isError: true);
            return;
        }

        _app.Settings.StartWithWindows = enabled;
        _app.SaveSettings();
        SetFooterStatus(enabled ? "已开启开机自动启动" : "已关闭开机自动启动", isError: false);
    }

    private void CloseToTrayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _app.Settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
        _app.SaveSettings();
        SetFooterStatus(
            _app.Settings.CloseToTray ? "关闭窗口时将最小化到托盘" : "关闭窗口时将退出 AudioSwitch",
            isError: false);
    }

    private void SwitchCommunicationsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _app.Settings.SwitchCommunications = SwitchCommunicationsToggle.IsChecked == true;
        _app.SaveSettings();
        SetFooterStatus(
            _app.Settings.SwitchCommunications ? "切换时会同时设置通信设备" : "切换时只修改控制台与多媒体设备",
            isError: false);
    }

    private void HotkeyEnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _app.Settings.HotkeyEnabled = HotkeyEnabledToggle.IsChecked == true;
        _app.SaveSettings();
        _app.ApplyHotkey(out string message);
        UpdateHotkeyDisplay();
        SetFooterStatus(
            _app.Settings.HotkeyEnabled ? "已启用全局快捷键" : "已关闭全局快捷键",
            isError: false);
    }

    private void HotkeyRecorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_app.Settings.HotkeyEnabled == false)
        {
            return;
        }

        BeginRecording();
        e.Handled = true;
    }

    private void BeginRecording()
    {
        _recordingHotkey = true;
        HotkeyDisplay.Text = "请按下组合键…";
        SetHotkeyStatus("按 Esc 取消", isError: false);
        HotkeyRecorder.BorderBrush = (Brush)FindResource("AccentBrush");
        HotkeyRecorder.Focus();
        Keyboard.Focus(HotkeyRecorder);
    }

    private void EndRecording()
    {
        _recordingHotkey = false;
        HotkeyRecorder.ClearValue(BorderBrushProperty);
        UpdateHotkeyDisplay();
    }

    private void HotkeyRecorder_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey)
        {
            return;
        }

        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            EndRecording();
            SetFooterStatus("已取消热键录制", isError: false);
            return;
        }

        if (!HotkeyParser.TryFromKey(key, Keyboard.Modifiers, out string text, out string error))
        {
            setRecordingError(error);
            return;
        }

        string previous = _app.Settings.Hotkey;
        _app.Settings.Hotkey = text;
        _app.SaveSettings();
        bool ok = _app.ApplyHotkey(out string message);
        if (!ok)
        {
            // 注册失败（常见原因：已被其他程序占用）：回滚到原热键并重新注册。
            _app.Settings.Hotkey = previous;
            _app.SaveSettings();
            _app.ApplyHotkey(out _);
        }

        _recordingHotkey = false;
        HotkeyRecorder.ClearValue(BorderBrushProperty);
        UpdateHotkeyDisplay();

        if (ok)
        {
            HotkeyDisplay.Text = text;
            SetHotkeyStatus($"已生效：{text}", isError: false);
            SetFooterStatus($"全局热键已设为 {text}", isError: false);
        }
        else
        {
            SetHotkeyStatus($"无法注册 {text}：{message}", isError: true);
            SetFooterStatus($"热键被占用，已保留原热键 {previous}", isError: true);
        }

        void setRecordingError(string description)
        {
            HotkeyDisplay.Text = "请按下组合键…";
            SetHotkeyStatus(description, isError: true);
            HotkeyRecorder.Focus();
        }
    }

    private void HotkeyRecorder_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_recordingHotkey)
        {
            return;
        }

        EndRecording();
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        _app.Settings.Hotkey = AppSettings.DefaultHotkey;
        _app.Settings.HotkeyEnabled = true;
        _app.SaveSettings();
        bool ok = _app.ApplyHotkey(out string message);
        HotkeyDisplay.Text = AppSettings.DefaultHotkey;
        HotkeyEnabledToggle.IsChecked = true;
        SetHotkeyStatus(ok ? $"已生效：{AppSettings.DefaultHotkey}" : message, isError: !ok);
        SetFooterStatus(ok ? "已恢复默认热键" : "恢复默认热键失败", isError: !ok);
    }

    private void OpenSwitcher_Click(object sender, RoutedEventArgs e) => _app.ShowSwitcher();

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(Log.FilePath))
            {
                Log.Info("用户打开日志（文件此前不存在）");
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Log.FilePath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开日志文件失败", ex);
            SetFooterStatus($"打开日志失败：{ex.Message}", isError: true);
        }
    }

    private void UnblockExe_Click(object sender, RoutedEventArgs e)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            SetFooterStatus("找不到当前程序文件", isError: true);
            return;
        }

        string zoneIdentifier = exePath + ":Zone.Identifier";
        try
        {
            if (!File.Exists(zoneIdentifier))
            {
                SetFooterStatus("文件没有被 Windows 标记，无需解除锁定", isError: false);
                return;
            }

            File.Delete(zoneIdentifier);
            Log.Info($"已解除文件锁定：{exePath}");
            SetFooterStatus("已解除锁定，下次启动不会再被 SmartScreen 拦截", isError: false);
        }
        catch (Exception ex)
        {
            Log.Error("解除文件锁定失败", ex);
            SetFooterStatus($"解除锁定失败：{ex.Message}", isError: true);
        }
    }

    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开系统声音设置失败", ex);
            SetFooterStatus("无法打开系统声音设置", isError: true);
        }
    }

    private void AutoCheckUpdatesToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        _app.Settings.AutoCheckUpdates = AutoCheckUpdatesToggle.IsChecked == true;
        _app.SaveSettings();
        SetFooterStatus(_app.Settings.AutoCheckUpdates ? "将在启动时自动检查更新" : "已关闭启动时自动检查更新", isError: false);

        if (_app.Settings.AutoCheckUpdates)
        {
            _ = _app.CheckForUpdatesAsync(notify: false);
        }
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.IsEnabled = false;
        SetUpdateStatus("正在检查更新…");

        try
        {
            UpdateInfo? update = await _app.CheckForUpdatesAsync(notify: false);
            _checkedUpdates = true;
            RefreshUpdateState();

            if (update is not null)
            {
                SetFooterStatus($"发现新版本 {update.DisplayVersion}", isError: false);
            }
            else if (_app.UpdateError is null)
            {
                SetFooterStatus("已是最新版本", isError: false);
            }
            else
            {
                SetFooterStatus("检查更新失败", isError: true);
            }
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            DownloadUpdateButton.IsEnabled = true;
        }
    }

    private async void DownloadUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateInfo? update = _app.LatestUpdate;
        if (update is null)
        {
            return;
        }

        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.IsEnabled = false;

        try
        {
            var progress = new Progress<double>(percent =>
                SetUpdateStatus($"正在下载安装包… {percent:P0}"));
            string path = await UpdateService.DownloadSetupAsync(update, progress: progress);

            SetUpdateStatus("下载完成，正在启动安装程序…");
            Log.Info($"启动版本更新：{update.DisplayVersion} -> {path}");
            UpdateService.LaunchInstaller(path);
            _app.ExitApplication();
        }
        catch (Exception ex)
        {
            Log.Error("下载更新失败", ex);
            SetUpdateStatus($"下载失败：{ex.Message}");
            SetFooterStatus("下载更新失败", isError: true);
            CheckUpdateButton.IsEnabled = true;
            DownloadUpdateButton.IsEnabled = true;
        }
    }

    private void ReleaseNotesButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateInfo? update = _app.LatestUpdate;
        try
        {
            UpdateService.OpenReleasePage(update?.ReleaseUrl ?? string.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("打开更新说明失败", ex);
            SetFooterStatus("无法打开更新说明", isError: true);
        }
    }

    private void RefreshUpdateState()
    {
        UpdateInfo? update = _app.LatestUpdate;
        string current = UpdateService.CurrentVersion.ToString(3);

        if (update is not null)
        {
            SetUpdateStatus($"发现新版本 {update.DisplayVersion}（当前 v{current}）。");
            DownloadUpdateButton.Visibility = string.IsNullOrWhiteSpace(update.SetupUrl)
                ? Visibility.Collapsed
                : Visibility.Visible;
            ReleaseNotesButton.Visibility = string.IsNullOrWhiteSpace(update.ReleaseUrl)
                ? Visibility.Collapsed
                : Visibility.Visible;
            return;
        }

        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        ReleaseNotesButton.Visibility = Visibility.Collapsed;

        if (_app.UpdateError is not null)
        {
            SetUpdateStatus($"检查失败：{_app.UpdateError}");
        }
        else if (_checkedUpdates)
        {
            SetUpdateStatus($"已是最新版本 v{current}。");
        }
        else
        {
            SetUpdateStatus("尚未检查更新。");
        }
    }

    private void SetUpdateStatus(string text)
    {
        UpdateStatusText.Text = text;
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetHotkeyStatus(string text, bool isError)
    {
        HotkeyStatusText.Text = text;
        HotkeyStatusText.Foreground = isError
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("TextSecondaryBrush");
    }

    private void SetFooterStatus(string text, bool isError)
    {
        FooterStatusText.Text = text;
        FooterStatusText.Foreground = isError
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("TextSecondaryBrush");
        FooterStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyThemeToWindow()
    {
        _app.Theme?.ApplyWindowEffects(this);
    }

    private void OnThemeChanged()
    {
        ApplyThemeToWindow();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _saveDebounce?.Stop();
        if (_app.Theme is not null)
        {
            _app.Theme.Changed -= OnThemeChanged;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_app.IsExiting)
        {
            return;
        }

        if (_app.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // 关闭窗口即退出程序（异步退出，避免在 Closing 内重入）
        Dispatcher.BeginInvoke(new Action(() => _app.ExitApplication()));
    }
}
