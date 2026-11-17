using System;
using System.Drawing;
using System.Windows.Forms;
using AudioSwitch.Services;

namespace AudioSwitch.Ui;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private Icon? _ownedIcon;
    private bool _disposed;

    public TrayIcon()
    {
        _ownedIcon = LoadIcon();
        _notifyIcon = new NotifyIcon
        {
            Text = "AudioSwitch",
            Icon = _ownedIcon,
            Visible = true,
        };
        _notifyIcon.MouseUp += OnMouseUp;
    }

    public event Action? Clicked;

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text 上限 127 个字符
        string trimmed = text.Length > 120 ? text[..120] : text;
        _notifyIcon.Text = trimmed;
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button is MouseButtons.Left or MouseButtons.Right or MouseButtons.Middle)
        {
            try
            {
                Clicked?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("托盘点击回调异常", ex);
            }
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource?.Stream is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"加载内嵌图标失败：{ex.Message}");
        }

        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取可执行文件图标失败：{ex.Message}");
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.MouseUp -= OnMouseUp;
        _notifyIcon.Dispose();
        _ownedIcon?.Dispose();
        _ownedIcon = null;
    }
}
