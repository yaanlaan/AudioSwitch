using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioSwitch.Audio;
using AudioSwitch.Interop;
using AudioSwitch.Services;

namespace AudioSwitch.Ui;

/// <summary>音量弹窗中的一个应用（按进程合并后的展示项）。</summary>
public sealed class VolumeSessionItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _subtitle = string.Empty;
    private double _volume;
    private bool _muted;
    private ImageSource? _icon;

    public VolumeSessionItem(AppVolumeSession session)
    {
        ProcessId = session.ProcessId;
        IsSystem = session.IsSystem;
        ExecutablePath = session.ExecutablePath;
        _name = session.Name;
        _subtitle = session.Subtitle;
        _volume = session.Volume * 100;
        _muted = session.Muted;
        _icon = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public uint ProcessId { get; }

    public bool IsSystem { get; }

    public string? ExecutablePath { get; set; }

    public string Glyph => IsSystem ? "\uE767" : "\uE8D6";

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (SetField(ref _subtitle, value))
            {
                OnPropertyChanged(nameof(HasSubtitle));
            }
        }
    }

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    public ImageSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    public double Volume
    {
        get => _volume;
        set
        {
            double clamped = Math.Clamp(value, 0, 100);
            if (SetField(ref _volume, clamped))
            {
                OnPropertyChanged(nameof(PercentText));
            }
        }
    }

    public string PercentText => $"{(int)Math.Round(Volume)}%";

    public bool Muted
    {
        get => _muted;
        set
        {
            if (SetField(ref _muted, value))
            {
                OnPropertyChanged(nameof(MuteGlyph));
            }
        }
    }

    public string MuteGlyph => Muted ? "\uE74F" : "\uE767";

    /// <summary>用户正在拖动滑杆时，后台刷新不要覆盖它。</summary>
    public bool IsDragging { get; set; }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>总音量 + 单应用音量弹窗（由独立全局热键打开）。</summary>
public partial class VolumePopup : Window
{
    private const string MuteGlyph = "\uE74F";
    private const string VolumeGlyph = "\uE767";

    private readonly App _app;
    private readonly AudioVolumeService? _volume;
    private readonly ObservableCollection<VolumeSessionItem> _items = new();
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private DispatcherTimer? _timer;
    private bool _refreshing;
    private bool _masterDragging;
    private bool _closing;

    public VolumePopup(App app)
    {
        _app = app;
        InitializeComponent();

        try
        {
            _volume = new AudioVolumeService();
        }
        catch (Exception ex)
        {
            Log.Error("初始化音量服务失败", ex);
            _volume = null;
        }

        SessionList.ItemsSource = _items;

        SourceInitialized += (_, _) => _app.Theme?.ApplyWindowEffects(this);
        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += OnClosed;

        if (_app.Theme is not null)
        {
            _app.Theme.Changed += OnThemeChanged;
        }
    }

    public void PrepareForRender()
    {
        RefreshAll();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();
        PositionWindow();
        AnimateIn();
        RefreshAll();
        Focus();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Background, OnTimerTick, Dispatcher);
        _timer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _timer?.Stop();
        _timer = null;

        if (_app.Theme is not null)
        {
            _app.Theme.Changed -= OnThemeChanged;
        }

        _volume?.Dispose();
    }

    private void OnThemeChanged()
    {
        _app.Theme?.ApplyWindowEffects(this);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!IsVisible || _closing)
        {
            return;
        }

        RefreshLevels();
    }

    private void RefreshAll()
    {
        if (_volume is null)
        {
            ShowError("无法访问 Windows 音量接口，请查看日志。");
            return;
        }

        _refreshing = true;
        try
        {
            ErrorText.Visibility = Visibility.Collapsed;
            DeviceText.Text = ResolveDeviceName();
            RefreshMaster();
            MergeSessions(_volume.GetSessions());
        }
        catch (Exception ex)
        {
            Log.Warn($"刷新音量失败：{ex.Message}");
            ShowError($"读取音量失败：{ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshLevels()
    {
        if (_volume is null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            RefreshMaster();
            MergeSessions(_volume.GetSessions());
        }
        catch (Exception ex)
        {
            Log.Warn($"定时刷新音量失败：{ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshMaster()
    {
        if (_volume is null || !_volume.TryGetMasterVolume(out float volume, out bool muted))
        {
            ShowError("当前没有可用的输出设备。");
            return;
        }

        if (!_masterDragging && !MasterSlider.IsMouseCaptured && !MasterSlider.IsKeyboardFocused)
        {
            MasterSlider.Value = volume * 100;
        }

        MasterPercentText.Text = $"{(int)Math.Round(volume * 100)}%";
        MasterMuteGlyph.Text = muted ? MuteGlyph : VolumeGlyph;
        MasterStatusText.Text = muted ? "已静音" : "默认输出设备";
    }

    private void MergeSessions(IReadOnlyList<AppVolumeSession> sessions)
    {
        var active = new HashSet<uint>(sessions.Select(s => s.ProcessId));
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (!active.Contains(_items[i].ProcessId))
            {
                _items.RemoveAt(i);
            }
        }

        foreach (AppVolumeSession session in sessions)
        {
            VolumeSessionItem? item = _items.FirstOrDefault(i => i.ProcessId == session.ProcessId);
            if (item is null)
            {
                item = new VolumeSessionItem(session);
                item.Icon = LoadIcon(session.ExecutablePath);
                _items.Add(item);
                continue;
            }

            item.Name = session.Name;
            item.Subtitle = session.Subtitle;
            if (!string.Equals(item.ExecutablePath, session.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                item.ExecutablePath = session.ExecutablePath;
                item.Icon = LoadIcon(session.ExecutablePath);
            }

            if (!item.IsDragging)
            {
                item.Volume = session.Volume * 100;
                item.Muted = session.Muted;
            }
        }

        SessionCountText.Text = _items.Count == 0 ? string.Empty : $"· {_items.Count}";
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionList.Visibility = _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private string ResolveDeviceName()
    {
        try
        {
            AudioDevice? device = _app.Audio.GetOutputDevices()
                .FirstOrDefault(d => d.IsDefaultConsole) ?? _app.Audio.GetOutputDevices().FirstOrDefault();
            return device is null ? "默认输出设备" : $"输出：{device.DisplayName}";
        }
        catch
        {
            return "默认输出设备";
        }
    }

    private void MasterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        MasterPercentText.Text = $"{(int)Math.Round(e.NewValue)}%";
        if (_refreshing || _volume is null)
        {
            return;
        }

        _volume.SetMasterVolume((float)(e.NewValue / 100.0));
    }

    private void MasterMute_Click(object sender, RoutedEventArgs e)
    {
        if (_volume is null || !_volume.TryGetMasterVolume(out _, out bool muted))
        {
            return;
        }

        bool next = !muted;
        if (_volume.SetMasterMute(next))
        {
            MasterMuteGlyph.Text = next ? MuteGlyph : VolumeGlyph;
            MasterStatusText.Text = next ? "已静音" : "默认输出设备";
        }
    }

    private void SessionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing || _volume is null || sender is not Slider slider
            || slider.DataContext is not VolumeSessionItem item)
        {
            return;
        }

        _volume.SetSessionVolume(item.ProcessId, (float)(e.NewValue / 100.0));
    }

    private void SessionMute_Click(object sender, RoutedEventArgs e)
    {
        if (_volume is null || sender is not FrameworkElement element
            || element.DataContext is not VolumeSessionItem item)
        {
            return;
        }

        bool next = !item.Muted;
        if (_volume.SetSessionVolume(item.ProcessId, (float)(item.Volume / 100.0), next))
        {
            item.Muted = next;
        }
    }

    private void SessionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: VolumeSessionItem item })
        {
            item.IsDragging = true;
        }
    }

    private void SessionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: VolumeSessionItem item })
        {
            item.IsDragging = false;
            _volume?.SetSessionVolume(item.ProcessId, (float)(item.Volume / 100.0), item.Muted);
        }
    }

    private void MasterSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _masterDragging = true;

    private void MasterSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _masterDragging = false;

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshAll();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private ImageSource? LoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (_iconCache.TryGetValue(path, out ImageSource? cached))
        {
            return cached;
        }

        ImageSource? source = null;
        try
        {
            if (TrayNative.ExtractIconExW(path, 0, out IntPtr large, out IntPtr small, 1) > 0)
            {
                IntPtr handle = small != IntPtr.Zero ? small : large;
                if (handle != IntPtr.Zero)
                {
                    source = Imaging.CreateBitmapSourceFromHIcon(
                        handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                }

                if (small != IntPtr.Zero)
                {
                    TrayNative.DestroyIcon(small);
                }

                if (large != IntPtr.Zero)
                {
                    TrayNative.DestroyIcon(large);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取应用图标失败：{ex.Message}");
        }

        _iconCache[path] = source;
        return source;
    }

    private void PositionWindow()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
        IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        bool hasMonitor = monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref monitorInfo);

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int widthPx = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int heightPx = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);

        int workLeft = 0;
        int workTop = 0;
        int workRight = (int)(SystemParameters.WorkArea.Right * dpi.DpiScaleX);
        int workBottom = (int)(SystemParameters.WorkArea.Bottom * dpi.DpiScaleY);
        if (hasMonitor)
        {
            workLeft = monitorInfo.Work.Left;
            workTop = monitorInfo.Work.Top;
            workRight = monitorInfo.Work.Right;
            workBottom = monitorInfo.Work.Bottom;
        }

        int margin = (int)Math.Round(12 * dpi.DpiScaleX);
        int x = workLeft + (workRight - workLeft - widthPx) / 2;
        int y = workTop + (workBottom - workTop - heightPx) / 2;

        x = Math.Clamp(x, workLeft + margin, Math.Max(workLeft + margin, workRight - widthPx - margin));
        y = Math.Clamp(y, workTop + margin, Math.Max(workTop + margin, workBottom - heightPx - margin));

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void AnimateIn()
    {
        if (!_app.Settings.AnimationEnabled)
        {
            Opacity = 1;
            SlideTransform.Y = 0;
            return;
        }

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _app.OpenSettings();
    }

    private void SoundSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("打开系统声音设置失败", ex);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
