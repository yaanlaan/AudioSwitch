using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioSwitch.Interop;
using AudioSwitch.Services;
using Microsoft.Win32;

namespace AudioSwitch.Ui;

/// <summary>
/// 统一的主题服务：根据设置计算明暗、强调色、画刷与背景图，并负责窗口的 DWM 磨砂效果。
/// </summary>
internal sealed class ThemeService
{
    private static readonly Color DefaultAccent = Color.FromRgb(0x5B, 0x9D, 0xFF);
    private static readonly Color DarkBackground = Color.FromRgb(0x1B, 0x1B, 0x1F);
    private static readonly Color LightBackground = Color.FromRgb(0xF4, 0xF4, 0xF7);

    private readonly SettingsStore _store;
    private readonly Dispatcher _dispatcher;
    private DispatcherTimer? _debounce;
    private int _backgroundVersion;
    private BitmapSource? _image;
    private string? _imagePath;
    private string? _sampledWallpaperPath;
    private Color _sampledWallpaperAccent;
    private bool _sampledWallpaper;

    private ThemeService(SettingsStore store)
    {
        _store = store;
        _dispatcher = Application.Current.Dispatcher;
    }

    public static ThemeService? Current { get; private set; }

    public static ThemeService Initialize(SettingsStore store)
    {
        Current = new ThemeService(store);
        Current.Subscribe();
        Current.ApplyBackground();
        Current.Apply();
        return Current;
    }

    public event Action? Changed;

    public AppSettings Settings => _store.Current;

    public bool IsDark { get; private set; }

    public Color Accent { get; private set; } = DefaultAccent;

    public Color WindowBackground { get; private set; } = DarkBackground;

    public bool HasBackgroundImage => _image is not null;

    public bool UseAcrylic => Settings.UseAcrylic;

    public string BackdropDescription { get; private set; } = "solid";

    public static Color Mix(Color foreground, Color background, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(foreground.R * amount + background.R * (1 - amount)),
            (byte)Math.Round(foreground.G * amount + background.G * (1 - amount)),
            (byte)Math.Round(foreground.B * amount + background.B * (1 - amount)));
    }

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    public static Color GetSystemAccentColor()
    {
        if (ThemeNative.TryGetSystemAccent(out byte r, out byte g, out byte b))
        {
            return Color.FromRgb(r, g, b);
        }

        return DefaultAccent;
    }

    public static bool TryDescribeImage(string? customPath, out Color accent, out Color average, out double luminance, out string source)
    {
        accent = DefaultAccent;
        average = DefaultAccent;
        luminance = 0.5;
        source = "none";

        string? path = customPath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            path = ThemeNative.GetWallpaperPath();
        }

        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return false;
        }

        source = path;
        BitmapSource? image = Backgrounds.Load(path, 512);
        if (image is null)
        {
            return false;
        }

        return Backgrounds.TrySampleAccent(image, out accent, out average, out luminance);
    }

    public void Apply()
    {
        IsDark = ComputeIsDark();
        Accent = EnsureContrast(ComputeAccent(), IsDark);
        WindowBackground = IsDark ? DarkBackground : LightBackground;

        bool translucent = UseAcrylic || HasBackgroundImage;
        double glass = Math.Clamp(Settings.GlassOpacity, 0.2, 1.0);
        byte glassAlpha = translucent ? (byte)Math.Round(255 * glass) : (byte)0xFF;
        byte surfaceAlpha = glassAlpha;
        byte hoverAlpha = translucent ? (byte)Math.Min(0xFF, glassAlpha + 0x14) : (byte)0xFF;
        byte borderAlpha = translucent ? (byte)0x66 : (byte)0xFF;
        byte selectedAlpha = translucent ? (byte)Math.Min(0xFF, glassAlpha + 0x22) : (byte)0xFF;
        byte softAlpha = translucent ? (byte)Math.Min(0xFF, glassAlpha + 0x08) : (byte)0xFF;

        Color surface = IsDark ? Color.FromRgb(0x23, 0x23, 0x29) : Colors.White;
        Color surfaceAlt = IsDark ? Color.FromRgb(0x2B, 0x2B, 0x33) : Color.FromRgb(0xEC, 0xEC, 0xF2);
        Color hover = surfaceAlt;
        Color border = IsDark ? Color.FromRgb(0x34, 0x34, 0x3D) : Color.FromRgb(0xDC, 0xDC, 0xE4);
        Color textPrimary = IsDark ? Color.FromRgb(0xF2, 0xF3, 0xF7) : Color.FromRgb(0x1B, 0x1B, 0x1F);
        Color textSecondary = IsDark ? Color.FromRgb(0x9A, 0x9D, 0xA8) : Color.FromRgb(0x62, 0x63, 0x6E);
        Color trackOff = IsDark ? Color.FromRgb(0x3A, 0x3A, 0x44) : Color.FromRgb(0xCD, 0xCD, 0xD6);
        Color thumb = IsDark ? Color.FromRgb(0x42, 0x42, 0x4D) : Color.FromRgb(0xC2, 0xC2, 0xCC);
        Color thumbHover = IsDark ? Color.FromRgb(0x5A, 0x5A, 0x68) : Color.FromRgb(0x9E, 0x9E, 0xA9);
        Color danger = IsDark ? Color.FromRgb(0xFF, 0x6B, 0x6B) : Color.FromRgb(0xE0, 0x3E, 0x45);
        Color success = IsDark ? Color.FromRgb(0x63, 0xD6, 0x8B) : Color.FromRgb(0x2E, 0x9E, 0x5B);

        Color selected = Mix(Accent, WindowBackground, 0.20);
        Color accentSoft = Mix(Accent, WindowBackground, IsDark ? 0.22 : 0.14);

        SetBrush("BgBrush", UseAcrylic || HasBackgroundImage ? Colors.Transparent : WindowBackground);

        byte tintAlpha = HasBackgroundImage ? (byte)0x80 : UseAcrylic ? (byte)0xC0 : (byte)0xFF;
        Color tint = UseAcrylic || HasBackgroundImage ? WithAlpha(WindowBackground, tintAlpha) : WindowBackground;
        SetBrush("WindowTintBrush", tint);

        // 弹窗面积小、浮在内容之上，需要比主窗口更实一些，文字才清晰。
        byte popupAlpha = HasBackgroundImage ? (byte)0xC6 : UseAcrylic ? (byte)0xF0 : (byte)0xFF;
        SetBrush("PopupBgBrush", WithAlpha(WindowBackground, popupAlpha));
        SetBrush("SurfaceBrush", WithAlpha(surface, surfaceAlpha));
        SetBrush("GlassBrush", WithAlpha(surface, glassAlpha));
        SetBrush("CardBorderBrush", IsDark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0));
        SetBrush("NavActiveBrush", WithAlpha(Mix(Accent, WindowBackground, IsDark ? 0.22 : 0.16), translucent ? (byte)0xD9 : (byte)0xFF));
        SetBrush("SurfaceAltBrush", WithAlpha(surfaceAlt, hoverAlpha));
        SetBrush("HoverBrush", WithAlpha(hover, hoverAlpha));
        SetBrush("SelectedBrush", WithAlpha(selected, selectedAlpha));
        SetBrush("BorderBrush", WithAlpha(border, borderAlpha));
        SetBrush("TextPrimaryBrush", textPrimary);
        SetBrush("TextSecondaryBrush", textSecondary);
        SetBrush("AccentBrush", Accent);
        SetBrush("AccentSoftBrush", WithAlpha(accentSoft, softAlpha));
        SetBrush("DangerBrush", danger);
        SetBrush("SuccessBrush", success);
        SetBrush("TrackOffBrush", trackOff);
        SetBrush("ScrollThumbBrush", thumb);
        SetBrush("ScrollThumbHoverBrush", thumbHover);
        SetBrush("ScrimBrush", HasBackgroundImage
            ? (IsDark ? Color.FromArgb(0x66, 0, 0, 0) : Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF))
            : Colors.Transparent);

        Application.Current.Resources["BackgroundImageBrush"] = new ImageBrush
        {
            ImageSource = _image,
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
            Opacity = Math.Clamp(Settings.BackgroundOpacity, 0.2, 1.0),
        };

        Changed?.Invoke();
    }

    public void ApplyBackground()
    {
        int version = ++_backgroundVersion;
        string? path = Settings.BackgroundImage;

        if (string.IsNullOrWhiteSpace(path))
        {
            _image = null;
            _imagePath = null;
            return;
        }

        if (string.Equals(path, _imagePath, StringComparison.OrdinalIgnoreCase) && _image is not null)
        {
            return;
        }

        string capturedPath = path;
        _ = Task.Run(() =>
        {
            BitmapSource? image = Backgrounds.Load(capturedPath);
            if (image is not null && Settings.BlurBackground)
            {
                image = Backgrounds.CreateBlurred(capturedPath, image);
            }

            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (version != _backgroundVersion)
                {
                    return;
                }

                _image = image;
                _imagePath = image is null ? null : capturedPath;
                Apply();
            }));
        });
    }

    /// <summary>同步加载背景图（离屏渲染用），避免异步结果赶不上渲染。</summary>
    internal void ApplyBackgroundNow()
    {
        _backgroundVersion++;
        string? path = Settings.BackgroundImage;

        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            _image = null;
            _imagePath = null;
            Apply();
            return;
        }

        BitmapSource? image = Backgrounds.Load(path);
        if (image is not null && Settings.BlurBackground)
        {
            image = Backgrounds.CreateBlurred(path, image);
        }

        _image = image;
        _imagePath = path;
        Apply();
    }

    /// <summary>设置变化后重新加载背景并刷新配色。</summary>
    public void RefreshAll()
    {
        ApplyBackground();
        Apply();
    }

    public bool ApplyWindowEffects(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        ThemeNative.SetDarkTitleBar(hwnd, IsDark);
        ThemeNative.SetRoundedCorners(hwnd);

        bool backdrop = false;
        if (UseAcrylic)
        {
            backdrop = ThemeNative.TrySetBackdrop(hwnd, ThemeNative.BackdropKind.Acrylic, out string detail);
            BackdropDescription = detail;
        }
        else
        {
            ThemeNative.TrySetBackdrop(hwnd, ThemeNative.BackdropKind.None, out string detail);
            BackdropDescription = detail;
        }

        if (window.AllowsTransparency)
        {
            // 分层窗口无法使用 DWM 背景，只能依靠半透明画刷。
            window.Background = Brushes.Transparent;
            return false;
        }

        window.Background = backdrop || HasBackgroundImage ? Brushes.Transparent : new SolidColorBrush(WindowBackground);
        if (backdrop)
        {
            HwndSource? source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget is not null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
        }

        return backdrop;
    }

    public void Shutdown()
    {
        Unsubscribe();
        Current = null;
    }

    private bool ComputeIsDark()
    {
        if (Settings.IsDarkThemeFixed)
        {
            return true;
        }

        if (Settings.IsLightThemeFixed)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            if (value is int number)
            {
                return number == 0;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取系统主题失败，按深色处理：{ex.Message}");
        }

        return true;
    }

    private Color ComputeAccent()
    {
        string source = Settings.ColorSource;

        if (string.Equals(source, "fixed", StringComparison.OrdinalIgnoreCase))
        {
            if (AppSettings.TryParseColor(Settings.AccentColor, out uint argb))
            {
                return Color.FromArgb(0xFF, (byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
            }

            return DefaultAccent;
        }

        if (string.Equals(source, "image", StringComparison.OrdinalIgnoreCase))
        {
            if (_image is not null && Backgrounds.TrySampleAccent(_image, out Color accent, out _, out _))
            {
                return accent;
            }

            if (TryGetWallpaperAccent(out Color wallpaperAccent))
            {
                return wallpaperAccent;
            }
        }

        return GetSystemAccentColor();
    }

    private bool TryGetWallpaperAccent(out Color accent)
    {
        accent = DefaultAccent;

        string? path = ThemeNative.GetWallpaperPath();
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return false;
        }

        if (_sampledWallpaper && string.Equals(path, _sampledWallpaperPath, StringComparison.OrdinalIgnoreCase))
        {
            accent = _sampledWallpaperAccent;
            return true;
        }

        BitmapSource? image = Backgrounds.Load(path, 512);
        if (image is null || !Backgrounds.TrySampleAccent(image, out Color sampled, out _, out _))
        {
            return false;
        }

        _sampledWallpaper = true;
        _sampledWallpaperPath = path;
        _sampledWallpaperAccent = sampled;
        accent = sampled;
        return true;
    }

    /// <summary>
    /// 调整强调色，使其在对应背景上足够醒目：保留色相，只提升明度与饱和度。
    /// Windows 的强调色常常非常暗（例如 #072128），直接使用几乎看不清。
    /// </summary>
    private static Color EnsureContrast(Color color, bool dark)
    {
        ToHsv(color, out double hue, out double saturation, out double value);

        if (saturation < 0.35)
        {
            // 灰调强调色没有辨识度，混入默认蓝色。
            color = Mix(DefaultAccent, color, 0.65);
            ToHsv(color, out hue, out saturation, out value);
        }

        value = dark ? Math.Max(value, 0.80) : Math.Min(value, 0.62);
        saturation = Math.Max(saturation, 0.58);
        return FromHsv(hue, saturation, value);
    }

    private static void ToHsv(Color color, out double hue, out double saturation, out double value)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        hue = 0;
        if (delta > 0.0001)
        {
            if (max == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                hue = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                hue = 60 * (((r - g) / delta) + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        saturation = max <= 0 ? 0 : delta / max;
        value = max;
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m = value - chroma;

        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static void SetBrush(string key, Color color)
    {
        // 资源字典里的画刷可能已被 WPF 冻结，不能原地修改颜色；
        // 直接替换资源项，DynamicResource 会通知所有窗口刷新。
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private void Subscribe()
    {
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"订阅系统外观变化失败：{ex.Message}");
        }
    }

    private void Unsubscribe()
    {
        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        catch
        {
            // 退出阶段忽略。
        }

        _debounce?.Stop();
        _debounce = null;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!Settings.UseSystemTheme
            && !string.Equals(Settings.ColorSource, "system", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Settings.ColorSource, "image", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _dispatcher.BeginInvoke(new Action(() =>
        {
            _debounce ??= new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(300),
            };
            _debounce.Stop();
            _debounce.Tick -= DebounceTick;
            _debounce.Tick += DebounceTick;
            _debounce.Start();
        }));
    }

    private void DebounceTick(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        Apply();
    }
}
