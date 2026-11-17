using System;
using System.Runtime.InteropServices;
using System.Text;
using AudioSwitch.Services;

namespace AudioSwitch.Interop;

/// <summary>
/// 窗口外观相关的 Win32 / DWM 调用：深色标题栏、云母与亚克力背景、系统强调色、壁纸路径。
/// </summary>
internal static class ThemeNative
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaMicaEffect = 1029;

    private const int DwmSystemBackdropAuto = 0;
    private const int DwmSystemBackdropNone = 1;
    private const int DwmSystemBackdropMainWindow = 2;
    private const int DwmSystemBackdropTransientWindow = 3;

    private const int WcaAccentPolicy = 19;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const int AccentEnableBlurBehind = 3;
    private const int AccentDisabled = 0;

    private const uint SpiGetDeskWallpaper = 0x0073;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint colorization, out bool opaqueBlend);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder? buffer, uint winIni);

    public static int BuildNumber => Environment.OSVersion.Version.Build;

    public static bool SupportsMica => BuildNumber >= 22000;

    /// <summary>Windows 11 的云母/亚克力背景需要 22621+；更早的版本退化为 SetWindowCompositionAttribute 的亚克力模糊。</summary>
    public static bool SupportsSystemBackdrop => BuildNumber >= 22621;

    public enum BackdropKind
    {
        None = 0,
        Mica = 1,
        Acrylic = 2,
    }

    /// <summary>设置深色标题栏。dark=true 表示深色。</summary>
    public static bool SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) == 0;
    }

    /// <summary>设置 Windows 11 的圆角窗口。</summary>
    public static bool SetRoundedCorners(IntPtr hwnd)
    {
        int preference = 2; // DWMWCP_ROUND
        return DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int)) == 0;
    }

    /// <summary>
    /// 尝试给窗口启用磨砂背景。返回 true 时调用方应把窗口背景设为 Transparent。
    /// </summary>
    public static bool TrySetBackdrop(IntPtr hwnd, BackdropKind kind, out string detail)
    {
        detail = string.Empty;

        if (kind == BackdropKind.None)
        {
            ApplyBackdropType(hwnd, DwmSystemBackdropNone);
            DisableAccentBlur(hwnd);
            detail = "solid";
            return false;
        }

        if (SupportsSystemBackdrop)
        {
            int backdrop = kind == BackdropKind.Mica ? DwmSystemBackdropMainWindow : DwmSystemBackdropTransientWindow;
            int hr = ApplyBackdropType(hwnd, backdrop);
            if (hr == 0)
            {
                ExtendFrame(hwnd);
                detail = $"backdrop={(kind == BackdropKind.Mica ? "mica" : "acrylic")}";
                return true;
            }

            Log.Warn($"DWMWA_SYSTEMBACKDROP_TYPE 失败（HRESULT 0x{hr:X8}），尝试旧版亚克力。");
            detail = $"system-backdrop-hresult=0x{hr:X8}";
        }

        // Windows 10 / 早期 Windows 11：Mica 不存在，只有 SetWindowCompositionAttribute 的模糊。
        bool ok = EnableAccentBlur(hwnd, kind == BackdropKind.Acrylic
            ? AccentEnableAcrylicBlurBehind
            : AccentEnableBlurBehind);
        if (ok)
        {
            ExtendFrame(hwnd);
            detail += " accent-blur";
            return true;
        }

        detail += " fallback=solid";
        return false;
    }

    private static int ApplyBackdropType(IntPtr hwnd, int backdrop)
    {
        int value = backdrop;
        return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref value, sizeof(int));
    }

    private static void ExtendFrame(IntPtr hwnd)
    {
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    private static bool EnableAccentBlur(IntPtr hwnd, int accentState)
    {
        try
        {
            var policy = new AccentPolicy { AccentState = accentState, AccentFlags = 0, GradientColor = 0 };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WcaAccentPolicy,
                    Data = pointer,
                    SizeOfData = size,
                };
                return SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"SetWindowCompositionAttribute 调用失败：{ex.Message}");
            return false;
        }
    }

    private static void DisableAccentBlur(IntPtr hwnd)
    {
        try
        {
            var policy = new AccentPolicy { AccentState = AccentDisabled };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, pointer, false);
                var data = new WindowCompositionAttributeData { Attribute = WcaAccentPolicy, Data = pointer, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch
        {
            // 关闭失败不影响纯色回退。
        }
    }

    /// <summary>系统强调色（DWM 自动着色，例如壁纸主色）。</summary>
    public static bool TryGetSystemAccent(out byte r, out byte g, out byte b)
    {
        r = 0x5B;
        g = 0x9D;
        b = 0xFF;

        try
        {
            if (DwmGetColorizationColor(out uint colorization, out _) == 0)
            {
                // DwmGetColorizationColor 返回 0xAARRGGBB。
                r = (byte)((colorization >> 16) & 0xFF);
                g = (byte)((colorization >> 8) & 0xFF);
                b = (byte)(colorization & 0xFF);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取系统强调色失败：{ex.Message}");
        }

        return false;
    }

    /// <summary>当前桌面壁纸的路径，可能为空（例如纯色桌面）。</summary>
    public static string? GetWallpaperPath()
    {
        try
        {
            var buffer = new StringBuilder(1024);
            if (SystemParametersInfo(SpiGetDeskWallpaper, (uint)buffer.Capacity, buffer, 0))
            {
                string path = buffer.ToString();
                return string.IsNullOrWhiteSpace(path) ? null : path;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取壁纸路径失败：{ex.Message}");
        }

        return null;
    }
}
