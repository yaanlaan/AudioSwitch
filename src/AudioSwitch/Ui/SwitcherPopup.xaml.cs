using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AudioSwitch.Audio;
using AudioSwitch.Interop;
using AudioSwitch.Services;

namespace AudioSwitch.Ui;

public partial class SwitcherPopup : Window
{
    private const double CardSlot = 196;
    private const int ListWidthDip = 376;

    private readonly App _app;
    private bool _switching;
    private bool _cards;

    public SwitcherPopup(App app)
    {
        _app = app;
        InitializeComponent();

        ApplyStyleSettings();
        RefreshDevices();

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

    private bool CloseOnSwitch => _app.Settings.PopupCloseOnSwitch;

    private void ApplyStyleSettings()
    {
        _cards = !string.Equals(_app.Settings.PopupStyle, "list", StringComparison.OrdinalIgnoreCase);

        DeviceList.ItemContainerStyle = (Style)FindResource(_cards ? "CardItemStyle" : "RowItemStyle");
        DeviceList.ItemTemplate = (DataTemplate)FindResource(_cards ? "CardTemplate" : "RowTemplate");
        DeviceList.ItemsPanel = (ItemsPanelTemplate)FindResource(_cards ? "CardPanelTemplate" : "RowPanelTemplate");
        KeyHintText.Text = _cards ? "方向键选择 · Enter 切换 · Esc 关闭" : "Enter 切换 · Esc 关闭";
    }

    private int ColumnsFor(int count)
    {
        if (!_cards)
        {
            return 1;
        }

        int configured = _app.Settings.PopupColumns;
        if (configured is 2 or 3 or 4)
        {
            return configured;
        }

        return count switch
        {
            <= 0 => 1,
            <= 3 => count,
            <= 6 => 3,
            _ => 4,
        };
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_app.Theme is not null)
        {
            _app.Theme.Changed -= OnThemeChanged;
        }
    }

    private void OnThemeChanged()
    {
        _app.Theme?.ApplyWindowEffects(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();
        PositionWindow();

        if (_app.Settings.AnimationEnabled)
        {
            AnimateIn();
        }
        else
        {
            Opacity = 1;
            SlideTransform.Y = 0;
        }

        if (DeviceList.Items.Count > 0)
        {
            DeviceList.Focus();
            if (DeviceList.SelectedItem is not null)
            {
                DeviceList.ScrollIntoView(DeviceList.SelectedItem);
            }
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_switching)
        {
            return;
        }

        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                return;
            case Key.Enter:
                if (DeviceList.SelectedItem is DeviceItem item)
                {
                    SwitchTo(item);
                    e.Handled = true;
                }

                return;
        }

        int index = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
            _ => -1,
        };

        if (index >= 0 && index < DeviceList.Items.Count)
        {
            DeviceList.SelectedIndex = index;
            DeviceList.ScrollIntoView(DeviceList.SelectedItem);
            e.Handled = true;
        }
    }

    private void RefreshDevices()
    {
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var devices = _app.Audio.GetOutputDevices();
            bool showIcons = _app.Settings.PopupShowIcons;
            var items = devices.Select(d => new DeviceItem(d) { ShowIcon = showIcons }).ToList();

            DeviceList.ItemsSource = items;
            DeviceCountText.Text = items.Count == 0 ? string.Empty : $"· {items.Count}";
            DeviceList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var current = items.FirstOrDefault(i => i.IsCurrent);
            DeviceList.SelectedItem = current ?? items.FirstOrDefault();

            UpdateWindowSize(items.Count);
        }
        catch (Exception ex)
        {
            Log.Error("刷新设备列表失败", ex);
            ShowError($"读取设备失败：{ex.Message}");
        }
    }

    private void UpdateWindowSize(int count)
    {
        if (!_cards)
        {
            Width = ListWidthDip;
            return;
        }

        int columns = ColumnsFor(count);
        // 卡片槽宽 196 + 左右内边距 24 + 根边框外距 24 + 滚动条余量
        Width = columns * CardSlot + 62;
    }

    private void SwitchTo(DeviceItem item)
    {
        if (_switching)
        {
            return;
        }

        if (item.IsCurrent)
        {
            Close();
            return;
        }

        _switching = true;
        try
        {
            SwitchResult result = _app.Audio.SetDefault(item.Device.Id, _app.Settings.SwitchCommunications);
            if (!result.Success)
            {
                Log.Warn($"切换失败：{result.Message}");
                ShowError(result.Message);
                return;
            }

            _app.HandleDeviceChanged(item.Device);

            if (CloseOnSwitch)
            {
                Close();
            }
            else
            {
                RefreshDevices();
            }
        }
        finally
        {
            _switching = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
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

        int margin = (int)Math.Round(12 * dpi.DpiScaleX);
        int gap = (int)Math.Round(12 * dpi.DpiScaleX);

        int workLeft;
        int workTop;
        int workRight;
        int workBottom;
        if (hasMonitor)
        {
            workLeft = monitorInfo.Work.Left;
            workTop = monitorInfo.Work.Top;
            workRight = monitorInfo.Work.Right;
            workBottom = monitorInfo.Work.Bottom;
        }
        else
        {
            Rect workArea = SystemParameters.WorkArea;
            workLeft = 0;
            workTop = 0;
            workRight = (int)(workArea.Right * dpi.DpiScaleX);
            workBottom = (int)(workArea.Bottom * dpi.DpiScaleY);
        }

        int x;
        int y;
        if (string.Equals(_app.Settings.PopupPosition, "cursor", StringComparison.OrdinalIgnoreCase))
        {
            // 鼠标附近：以光标为锚点，水平居中、向上展开
            x = cursor.X - widthPx / 2;
            y = cursor.Y - heightPx - gap;
        }
        else
        {
            // 屏幕中央：Alt+Tab 式居中
            x = workLeft + (workRight - workLeft - widthPx) / 2;
            y = workTop + (workBottom - workTop - heightPx) / 2;
        }

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
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation((_cards ? 14 : 6), 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void DeviceList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(DeviceList, (DependencyObject)e.OriginalSource) as ListBoxItem is { } container
            && container.DataContext is DeviceItem item)
        {
            SwitchTo(item);
            e.Handled = true;
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        DeviceList.Focus();
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

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _app.ExitApplication();
    }
}
