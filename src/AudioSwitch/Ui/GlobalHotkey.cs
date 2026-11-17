using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using AudioSwitch.Interop;
using AudioSwitch.Services;

namespace AudioSwitch.Ui;

internal static class HotkeyParser
{
    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        bool hasModifier = false;
        bool hasKey = false;

        foreach (string token in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    hasModifier = true;
                    break;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    hasModifier = true;
                    break;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    hasModifier = true;
                    break;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.MOD_WIN;
                    hasModifier = true;
                    break;
                default:
                    if (hasKey || !TryParseKey(token, out virtualKey))
                    {
                        return false;
                    }

                    hasKey = true;
                    break;
            }
        }

        // F1–F24 可以单独使用；字母、数字和空格必须搭配修饰键，
        // 否则会吞掉日常输入（RegisterHotKey 的全局拦截）。
        return hasKey && (hasModifier || IsFunctionKey(virtualKey));
    }

    /// <summary>把键盘事件转换成可保存的热键文本。</summary>
    public static bool TryFromKey(Key key, ModifierKeys modifiers, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;

        if (key == Key.System)
        {
            error = "请按下完整的组合键";
            return false;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.None or Key.DeadCharProcessed or Key.ImeProcessed)
        {
            error = "请再按下字母、数字、F1–F24 或空格";
            return false;
        }

        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!IsSupportedKey(virtualKey))
        {
            error = "这个按键不能用作全局快捷键，请改用字母、数字、F1–F24 或空格";
            return false;
        }

        uint nativeModifiers = 0;
        if ((modifiers & ModifierKeys.Control) != 0) nativeModifiers |= NativeMethods.MOD_CONTROL;
        if ((modifiers & ModifierKeys.Alt) != 0) nativeModifiers |= NativeMethods.MOD_ALT;
        if ((modifiers & ModifierKeys.Shift) != 0) nativeModifiers |= NativeMethods.MOD_SHIFT;
        if ((modifiers & ModifierKeys.Windows) != 0) nativeModifiers |= NativeMethods.MOD_WIN;

        if (nativeModifiers == 0 && !IsFunctionKey(virtualKey))
        {
            error = "请至少搭配一个修饰键（Ctrl、Alt、Shift 或 Win），或使用 F1–F24";
            return false;
        }

        text = Format(nativeModifiers, virtualKey);
        return true;
    }

    private static bool IsSupportedKey(uint virtualKey) =>
        (virtualKey is >= 0x41 and <= 0x5A)
        || (virtualKey is >= 0x30 and <= 0x39)
        || IsFunctionKey(virtualKey)
        || virtualKey == 0x20;

    private static bool IsFunctionKey(uint virtualKey) => virtualKey is >= 0x70 and <= 0x87;

    public static string Format(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(virtualKey));
        return string.Join("+", parts);
    }

    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;

        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
        }

        if (token.Length is >= 2 and <= 3
            && (token[0] == 'f' || token[0] == 'F')
            && int.TryParse(token[1..], out int functionKey)
            && functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            return true;
        }

        if (token.Equals("space", StringComparison.OrdinalIgnoreCase))
        {
            virtualKey = 0x20;
            return true;
        }

        return false;
    }

    private static string KeyName(uint virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x30 and <= 0x39)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x70 + 1);
        }

        return virtualKey == 0x20 ? "Space" : $"VK{virtualKey:X2}";
    }
}

/// <summary>隐藏消息窗口 + RegisterHotKey，避免依赖主窗口生命周期。</summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0xA571;

    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkey()
    {
        var parameters = new HwndSourceParameters("AudioSwitchHiddenWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };

        try
        {
            parameters.ParentWindow = NativeMethods.HWND_MESSAGE;
            _source = new HwndSource(parameters);
        }
        catch (Exception ex)
        {
            Log.Warn($"消息窗口创建失败，改用屏幕外隐藏窗口：{ex.Message}");
            parameters.ParentWindow = IntPtr.Zero;
            parameters.PositionX = -32000;
            parameters.PositionY = -32000;
            _source = new HwndSource(parameters);
        }

        _source.AddHook(WndProc);
    }

    public event Action? Pressed;

    public bool IsRegistered => _registered;

    public string? LastError { get; private set; }

    public bool TryRegister(string hotkey)
    {
        Unregister();
        LastError = null;

        if (!HotkeyParser.TryParse(hotkey, out uint modifiers, out uint virtualKey))
        {
            LastError = "热键格式无效";
            return false;
        }

        if (!NativeMethods.RegisterHotKey(
                _source.Handle,
                HotkeyId,
                modifiers | NativeMethods.MOD_NOREPEAT,
                virtualKey))
        {
            LastError = "热键已被其他程序占用";
            Log.Warn($"注册全局热键失败：{hotkey}");
            return false;
        }

        _registered = true;
        Log.Info($"已注册全局热键 {hotkey}");
        return true;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try
            {
                Pressed?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Error("热键回调异常", ex);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
