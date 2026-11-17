using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AudioSwitch.Services;

public sealed class AppSettings
{
    public const string DefaultHotkey = "Ctrl+Alt+A";
    public const string DefaultAccent = "#5B9DFF";

    public string Hotkey { get; set; } = DefaultHotkey;

    public bool StartWithWindows { get; set; }

    public bool CloseToTray { get; set; } = true;

    public bool SwitchCommunications { get; set; } = true;

    /// <summary>system、light 或 dark。</summary>
    public string ThemeMode { get; set; } = "system";

    /// <summary>system、fixed 或 image。</summary>
    public string ColorSource { get; set; } = "system";

    /// <summary>固定配色时使用的强调色，#RRGGBB。</summary>
    public string AccentColor { get; set; } = DefaultAccent;

    /// <summary>自定义背景图片的绝对路径；为空表示使用当前壁纸。</summary>
    public string? BackgroundImage { get; set; }

    /// <summary>背景图的不透明度，0.2 - 1.0。</summary>
    public double BackgroundOpacity { get; set; } = 0.55;

    /// <summary>是否对背景图做模糊处理。</summary>
    public bool BlurBackground { get; set; } = true;

    /// <summary>acrylic（云母/亚克力磨砂）或 solid（纯色）。</summary>
    public string Material { get; set; } = "acrylic";

    /// <summary>卡片的不透明度，0.2 - 1.0；越小越透出桌面。</summary>
    public double GlassOpacity { get; set; } = 0.72;

    /// <summary>cards（Alt+Tab 式磁贴）或 list（列表）。</summary>
    public string PopupStyle { get; set; } = "cards";

    /// <summary>磁贴每行数量；0 表示按设备数量自动。</summary>
    public int PopupColumns { get; set; }

    /// <summary>cursor（鼠标附近）或 center（当前屏幕中央）。</summary>
    public string PopupPosition { get; set; } = "center";

    /// <summary>切换成功后是否立刻关闭弹窗。</summary>
    public bool PopupCloseOnSwitch { get; set; } = true;

    /// <summary>是否在弹窗中显示设备图标。</summary>
    public bool PopupShowIcons { get; set; } = true;

    /// <summary>弹窗的淡入/滑动动画。</summary>
    public bool AnimationEnabled { get; set; } = true;

    /// <summary>是否启用全局热键。</summary>
    public bool HotkeyEnabled { get; set; } = true;

    public bool IsDarkThemeFixed => string.Equals(ThemeMode, "dark", StringComparison.OrdinalIgnoreCase);

    public bool IsLightThemeFixed => string.Equals(ThemeMode, "light", StringComparison.OrdinalIgnoreCase);

    public bool UseSystemTheme => !IsDarkThemeFixed && !IsLightThemeFixed;

    public bool UseAcrylic => !string.Equals(Material, "solid", StringComparison.OrdinalIgnoreCase);

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Hotkey) || !Ui.HotkeyParser.TryParse(Hotkey, out _, out _))
        {
            Hotkey = DefaultHotkey;
        }

        ThemeMode = NormalizeChoice(ThemeMode, "light", "dark") ?? "system";
        ColorSource = NormalizeChoice(ColorSource, "fixed", "image") ?? "system";
        Material = string.Equals(Material, "solid", StringComparison.OrdinalIgnoreCase) ? "solid" : "acrylic";
        PopupStyle = string.Equals(PopupStyle, "list", StringComparison.OrdinalIgnoreCase) ? "list" : "cards";
        PopupPosition = NormalizeChoice(PopupPosition, "cursor") ?? "center";
        GlassOpacity = Math.Clamp(GlassOpacity, 0.2, 1.0);
        PopupColumns = PopupColumns is 2 or 3 or 4 ? PopupColumns : 0;

        if (!TryParseColor(AccentColor, out _))
        {
            AccentColor = DefaultAccent;
        }

        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.2, 1.0);

        if (!string.IsNullOrWhiteSpace(BackgroundImage) && !File.Exists(BackgroundImage))
        {
            Log.Warn($"背景图片不存在，已忽略：{BackgroundImage}");
            BackgroundImage = null;
        }
    }

    private static string? NormalizeChoice(string? value, params string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        foreach (string option in allowed)
        {
            if (string.Equals(trimmed, option, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        return null;
    }

    public static bool TryParseColor(string? text, out uint argb)
    {
        argb = 0xFF000000;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string hex = text.Trim();
        if (hex.StartsWith('#'))
        {
            hex = hex[1..];
        }

        // 允许 #RGB、#RRGGBB、#AARRGGBB。
        if (hex.Length == 3)
        {
            string expanded = string.Create(6, hex, static (span, source) =>
            {
                for (int i = 0; i < 3; i++)
                {
                    span[i * 2] = source[i];
                    span[i * 2 + 1] = source[i];
                }
            });
            hex = expanded;
        }

        if (hex.Length == 6)
        {
            hex = "FF" + hex;
        }

        if (hex.Length != 8 ||
            !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
        {
            return false;
        }

        argb = value;
        return true;
    }
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(Log.DirectoryPath, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public SettingsStore()
    {
        Load();
    }

    public void Load()
    {
        if (!File.Exists(FilePath))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            string json = File.ReadAllText(FilePath);
            Current = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            Current.Normalize();
        }
        catch (Exception ex)
        {
            Log.Error("读取设置失败，回退到默认设置", ex);
            TryBackupBrokenFile();
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, SerializerOptions));
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex);
        }
    }

    private static void TryBackupBrokenFile()
    {
        try
        {
            string backup = FilePath + ".bad";
            File.Copy(FilePath, backup, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"备份损坏的设置文件失败：{ex.Message}");
        }
    }
}
