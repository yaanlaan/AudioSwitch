using AudioSwitch.Audio;

namespace AudioSwitch.Ui;

/// <summary>设备列表项（供弹窗与设置窗口绑定）。</summary>
public sealed class DeviceItem
{
    public DeviceItem(AudioDevice device)
    {
        Device = device;
        DisplayName = device.DisplayName;
        Subtitle = device.Subtitle;
        HasSubtitle = !string.IsNullOrWhiteSpace(Subtitle);
        IsCurrent = device.IsDefaultConsole;
        Glyph = DeviceGlyphs.For(device.FormFactor);
        Tooltip = device.Tooltip;
    }

    public AudioDevice Device { get; }

    public string DisplayName { get; }

    public string Subtitle { get; }

    public bool HasSubtitle { get; }

    public bool IsCurrent { get; }

    public string Glyph { get; }

    public string Tooltip { get; }

    /// <summary>弹窗里是否显示设备图标（由设置控制）。</summary>
    public bool ShowIcon { get; set; } = true;
}

internal static class DeviceGlyphs
{
    public static string For(int formFactor) => formFactor switch
    {
        3 or 5 or 6 => "\uE7F6", // 耳机 / 耳麦 / 手柄
        9 => "\uE7F4",           // HDMI / 显示器
        8 => "\uE7F5",           // SPDIF
        _ => "\uE767",           // 扬声器 / 其它
    };
}
