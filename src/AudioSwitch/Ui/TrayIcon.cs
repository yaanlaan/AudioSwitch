using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using AudioSwitch.Interop;
using AudioSwitch.Services;

namespace AudioSwitch.Ui;

/// <summary>纯 Win32（Shell_NotifyIcon）实现的托盘图标，避免引入 WinForms。</summary>
public sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = TrayNative.WM_APP + 1;

    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly bool _ownsIcon;
    private readonly int _taskbarCreatedMessage;
    private bool _added;
    private bool _disposed;

    public TrayIcon()
    {
        // 使用屏幕外的普通隐藏窗口（而不是 message-only 窗口）：
        // 部分 Windows 版本/外壳组合下 message-only 窗口无法注册托盘图标。
        var parameters = new HwndSourceParameters("AudioSwitchTrayWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW，避免出现在 Alt+Tab
            PositionX = -32000,
            PositionY = -32000,
        };

        try
        {
            _source = new HwndSource(parameters);
        }
        catch (Exception ex)
        {
            Log.Error("托盘消息窗口创建失败", ex);
            throw;
        }

        _source.AddHook(WndProc);
        _taskbarCreatedMessage = TrayNative.RegisterWindowMessageW("TaskbarCreated");

        (_icon, _ownsIcon) = LoadIcon();
        _added = AddIcon("AudioSwitch");
        if (!_added)
        {
            Log.Warn($"托盘图标注册失败：{LastError}");
        }
    }

    public event Action? Clicked;

    /// <summary>托盘图标是否已成功注册。</summary>
    public bool IsAvailable => _added;

    /// <summary>注册失败时的 Win32 错误描述。</summary>
    public string? LastError { get; private set; }

    public void SetTooltip(string text)
    {
        if (!_added)
        {
            return;
        }

        string trimmed = text.Length > 120 ? text[..120] : text;
        var data = CreateData();
        data.uFlags = TrayNative.NIF_TIP | TrayNative.NIF_SHOWTIP;
        data.szTip = trimmed;
        TrayNative.Shell_NotifyIconW(TrayNative.NIM_MODIFY, ref data);
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_added)
        {
            return;
        }

        try
        {
            var data = CreateData();
            data.uFlags = TrayNative.NIF_INFO;
            data.szInfoTitle = title.Length > 63 ? title[..63] : title;
            data.szInfo = message.Length > 255 ? message[..255] : message;
            data.dwInfoFlags = TrayNative.NIIF_INFO;
            data.uTimeoutOrVersion = 6000;
            TrayNative.Shell_NotifyIconW(TrayNative.NIM_MODIFY, ref data);
        }
        catch (Exception ex)
        {
            Log.Warn($"显示托盘通知失败：{ex.Message}");
        }
    }

    private TrayNative.NOTIFYICONDATAW CreateData()
    {
        return new TrayNative.NOTIFYICONDATAW
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<TrayNative.NOTIFYICONDATAW>(),
            hWnd = _source.Handle,
            uID = IconId,
            szTip = string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    private bool AddIcon(string tooltip)
    {
        var data = CreateData();
        data.uFlags = TrayNative.NIF_MESSAGE | TrayNative.NIF_ICON | TrayNative.NIF_TIP | TrayNative.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = tooltip;

        if (!TrayNative.Shell_NotifyIconW(TrayNative.NIM_ADD, ref data))
        {
            int error = Marshal.GetLastWin32Error();
            LastError = $"Shell_NotifyIcon NIM_ADD 失败，Win32 错误 {error}（0x{error:X8}）";
            return false;
        }

        data.uTimeoutOrVersion = TrayNative.NOTIFYICON_VERSION_4;
        TrayNative.Shell_NotifyIconW(TrayNative.NIM_SETVERSION, ref data);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            // 资源管理器重启后重新注册托盘图标
            _added = AddIcon("AudioSwitch");
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage)
        {
            return IntPtr.Zero;
        }

        int mouseMessage = (int)((long)lParam & 0xFFFF);
        if (mouseMessage is TrayNative.WM_LBUTTONUP or TrayNative.WM_RBUTTONUP or TrayNative.WM_MBUTTONUP)
        {
            try
            {
                Clicked?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("托盘点击回调异常", ex);
            }

            handled = true;
        }

        return IntPtr.Zero;
    }

    private static (IntPtr Icon, bool OwnsIcon) LoadIcon()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)
                && TrayNative.ExtractIconExW(path, 0, out IntPtr large, out IntPtr small, 1) > 0)
            {
                if (large != IntPtr.Zero)
                {
                    if (small != IntPtr.Zero)
                    {
                        TrayNative.DestroyIcon(small);
                    }

                    return (large, true);
                }

                if (small != IntPtr.Zero)
                {
                    return (small, true);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取可执行文件图标失败：{ex.Message}");
        }

        IntPtr fallback = TrayNative.LoadIconW(IntPtr.Zero, TrayNative.IDI_APPLICATION);
        return (fallback, false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            var data = CreateData();
            TrayNative.Shell_NotifyIconW(TrayNative.NIM_DELETE, ref data);
            _added = false;
        }

        if (_ownsIcon && _icon != IntPtr.Zero)
        {
            TrayNative.DestroyIcon(_icon);
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
