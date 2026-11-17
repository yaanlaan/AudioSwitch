using System;
using Microsoft.Win32;

namespace AudioSwitch.Services;

internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AudioSwitch";
    private const string TrayArgument = "--tray";

    public static string? ReadRaw()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取开机自启设置失败：{ex.Message}");
            return null;
        }
    }

    public static bool IsEnabled() => !string.IsNullOrWhiteSpace(ReadRaw());

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (enabled && string.IsNullOrEmpty(exePath))
            {
                Log.Warn("无法获取可执行文件路径，无法开启开机自启");
                return false;
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                Log.Warn("无法打开 Run 注册表项");
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{exePath}\" {TrayArgument}", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
            return false;
        }
    }

    /// <summary>启动时校正自启路径（exe 被移动后仍能自启）。</summary>
    public static void Reconcile(AppSettings settings)
    {
        if (!settings.StartWithWindows)
        {
            return;
        }

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return;
        }

        string expected = $"\"{exePath}\" {TrayArgument}";
        if (string.Equals(ReadRaw(), expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Log.Info("开机自启路径发生变化，正在更新");
        SetEnabled(true);
    }
}
