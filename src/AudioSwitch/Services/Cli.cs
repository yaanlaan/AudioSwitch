using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AudioSwitch.Audio;
using AudioSwitch.Interop;
using AudioSwitch.Ui;

namespace AudioSwitch.Services;

/// <summary>命令行模式：--probe / --switch / --self-test，便于无人值守验证。</summary>
internal static class Cli
{
    private static readonly ERole[] Roles =
    {
        ERole.eConsole,
        ERole.eMultimedia,
        ERole.eCommunications,
    };

    public static int? TryRun(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        // 无控制台或被重定向时也统一输出 UTF-8，便于脚本读取中文设备名
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            // 部分环境下设置编码会失败，不影响主流程
        }

        string command = args[0].Trim().ToLowerInvariant();
        return command switch
        {
            "--probe" => RunProbe(),
            "--switch" => RunSwitch(args),
            "--self-test" => RunSelfTest(),
            "--hotkey-test" => RunHotkeyTest(args),
            "--theme-info" => RunThemeInfo(),
            "--check-update" => RunCheckUpdate(args),
            "--download-update" => RunDownloadUpdate(args),
            "--tray-test" => RunTrayTest(),
            _ => null,
        };
    }

    /// <summary>验证 Shell_NotifyIcon 托盘图标能否注册与更新提示。</summary>
    private static int RunTrayTest()
    {
        try
        {
            using var tray = new TrayIcon();
            if (!tray.IsAvailable)
            {
                Print($"TRAY|FAIL|{(string.IsNullOrEmpty(tray.LastError) ? "Shell_NotifyIcon 注册失败" : tray.LastError)}");
                return 1;
            }

            tray.SetTooltip("AudioSwitch 托盘测试");
            tray.ShowBalloon("AudioSwitch", "托盘图标工作正常。");
            Print("TRAY|OK|托盘图标已注册");
            return 0;
        }
        catch (Exception ex)
        {
            Print($"TRAY|FAIL|{ex.Message}");
            return 1;
        }
    }

    /// <summary>验证按键串能否被解析并真正注册为全局热键。</summary>
    private static int RunHotkeyTest(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Print("USAGE|--hotkey-test <组合键，如 Ctrl+Shift+F9 或 F8>");
            return 2;
        }

        string combination = args[1].Trim();
        if (!Ui.HotkeyParser.TryParse(combination, out uint modifiers, out uint virtualKey))
        {
            Print($"FAIL|无法解析：{combination}（字母/数字需搭配 Ctrl、Alt、Shift 或 Win，也可单独使用 F1–F24）");
            return 1;
        }

        string normalized = Ui.HotkeyParser.Format(modifiers, virtualKey);

        try
        {
            using var hotkey = new Ui.GlobalHotkey();
            bool ok = hotkey.TryRegister(normalized);
            Print(ok ? $"HOTKEYTEST|OK|{normalized}" : $"FAIL|{hotkey.LastError}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Print($"FAIL|{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>输出系统主题、强调色、壁纸与当前设置，以及磨砂背景是否可用。</summary>
    private static int RunThemeInfo()
    {
        try
        {
            var store = new SettingsStore();
            AppSettings settings = store.Current;

            bool dark = ReadSystemIsDark();
            Color accent = Ui.ThemeService.GetSystemAccentColor();
            string? wallpaper = ThemeNative.GetWallpaperPath();

            Print(
                $"SYSTEM|{(dark ? "dark" : "light")}|accent=#{accent.R:X2}{accent.G:X2}{accent.B:X2}" +
                $"|wallpaper={wallpaper ?? "-"}|build={ThemeNative.BuildNumber}" +
                $"|mica={(ThemeNative.SupportsMica ? 1 : 0)}|backdrop={(ThemeNative.SupportsSystemBackdrop ? 1 : 0)}");

            Print(
                $"SETTINGS|theme={settings.ThemeMode}|color={settings.ColorSource}|accent={settings.AccentColor}" +
                $"|material={settings.Material}|opacity={settings.BackgroundOpacity:0.00}|blur={(settings.BlurBackground ? 1 : 0)}" +
                $"|background={settings.BackgroundImage ?? "-"}");

            bool sampled = Ui.ThemeService.TryDescribeImage(
                settings.BackgroundImage,
                out Color sampleAccent,
                out Color average,
                out double luminance,
                out string source);
            Print(sampled
                ? $"SAMPLE|{source}|accent=#{sampleAccent.R:X2}{sampleAccent.G:X2}{sampleAccent.B:X2}" +
                  $"|avg=#{average.R:X2}{average.G:X2}{average.B:X2}|luminance={luminance:0.00}|fallback=0"
                : "SAMPLE|-|-|-|fallback=1");

            // 用一个隐藏窗口实际尝试 DWM 磨砂，输出真实 HRESULT
            try
            {
                var probe = new Window
                {
                    Width = 2,
                    Height = 2,
                    Left = -4000,
                    Top = -4000,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                };
                probe.Show();
                IntPtr hwnd = new WindowInteropHelper(probe).Handle;
                bool ok = ThemeNative.TrySetBackdrop(hwnd, ThemeNative.BackdropKind.Acrylic, out string detail);
                Print($"BACKDROP|acrylic|{(ok ? "OK" : "FAIL")}|{detail}");
                probe.Close();
            }
            catch (Exception ex)
            {
                Print($"BACKDROP|acrylic|EXCEPTION|{ex.GetType().Name}: {ex.Message}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Print($"ERROR|{ex.GetType().Name}|{ex.Message}");
            Log.Error("--theme-info 失败", ex);
            return 1;
        }
    }

    private static int RunCheckUpdate(string[] args)
    {
        Version current = UpdateService.CurrentVersion;
        if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
        {
            if (!UpdateService.TryParseVersion(args[1], out Version overridden))
            {
                Print($"USAGE|无法解析版本号：{args[1]}");
                return 2;
            }

            current = overridden;
        }

        try
        {
            UpdateInfo info = UpdateService.FetchLatestAsync().GetAwaiter().GetResult();
            Print($"CURRENT|{current.ToString(3)}");
            Print($"LATEST|{info.Version.ToString(3)}|{info.ReleaseUrl}");
            Print($"SETUP|{info.SetupUrl ?? "-"}");
            Print(UpdateService.IsNewer(info, current)
                ? $"CHECK|available|{info.Version.ToString(3)}"
                : "CHECK|uptodate");
            return 0;
        }
        catch (Exception ex)
        {
            Print($"FAIL|{ex.GetType().Name}|{ex.Message}");
            Log.Warn($"--check-update 失败：{ex.Message}");
            return 1;
        }
    }

    private static int RunDownloadUpdate(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Print("USAGE|--download-update <目标目录>");
            return 2;
        }

        try
        {
            UpdateInfo info = UpdateService.FetchLatestAsync().GetAwaiter().GetResult();
            string path = UpdateService.DownloadSetupAsync(info, args[1]).GetAwaiter().GetResult();
            long size = new System.IO.FileInfo(path).Length;
            Print($"LATEST|{info.Version.ToString(3)}|{info.ReleaseUrl}");
            Print($"DOWNLOAD|OK|{path}|{size}");
            return 0;
        }
        catch (Exception ex)
        {
            Print($"DOWNLOAD|FAIL|{ex.GetType().Name}|{ex.Message}");
            Log.Warn($"--download-update 失败：{ex.Message}");
            return 1;
        }
    }

    private static bool ReadSystemIsDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
            {
                return value == 0;
            }
        }
        catch
        {
            // 读不到就按深色处理
        }

        return true;
    }

    private static int RunProbe()
    {
        try
        {
            using var audio = new AudioDeviceService();
            var devices = audio.GetOutputDevices();
            var defaults = audio.GetDefaults();
            var text = new StringBuilder();

            foreach (var device in devices)
            {
                text.AppendLine(
                    $"DEVICE|{device.Id}|{device.DisplayName}|{device.Subtitle}|{device.FormFactor}|{RoleFlags(device)}");
            }

            foreach (var role in Roles)
            {
                defaults.TryGetValue(role, out string? id);
                text.AppendLine($"DEFAULT|{RoleKey(role)}|{id ?? "-"}");
            }

            text.AppendLine($"END|{devices.Count}");
            Print(text.ToString());
            Log.Info($"--probe 输出：{Environment.NewLine}{text}");
            return 0;
        }
        catch (Exception ex)
        {
            Print($"ERROR|{ex.GetType().Name}|{ex.Message}");
            Log.Error("--probe 失败", ex);
            return 1;
        }
    }

    private static int RunSwitch(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Print("USAGE|--switch <序号|设备ID|名称片段> [--roles console,multimedia,communications]");
            return 2;
        }

        IReadOnlyList<ERole>? roles = ParseRoles(args);
        if (roles is null)
        {
            Print("USAGE|--roles 只接受 console、multimedia、communications，逗号分隔");
            return 2;
        }

        try
        {
            using var audio = new AudioDeviceService();
            var devices = audio.GetOutputDevices();
            AudioDevice? target = ResolveTarget(devices, args[1]);

            if (target is null)
            {
                Print($"NOTFOUND|{args[1]}");
                return 1;
            }

            if (roles.Count == 0 || roles.Count == Roles.Length)
            {
                var result = audio.SetDefault(target.Id, includeCommunications: true);
                Print(result.Success
                    ? $"OK|{target.Name}"
                    : $"FAIL|0x{result.HResult:X8}|{result.Message}");
                return result.Success ? 0 : 1;
            }

            bool allOk = true;
            foreach (var role in roles)
            {
                int hr = audio.SetRoleDefault(target.Id, role);
                allOk &= hr == 0;
                Print($"ROLE|{RoleKey(role)}|{(hr == 0 ? "OK" : $"FAIL|0x{hr:X8}")}");
            }

            return allOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            Print($"ERROR|{ex.GetType().Name}|{ex.Message}");
            Log.Error("--switch 失败", ex);
            return 1;
        }
    }

    /// <summary>解析可选的 --roles 参数；未指定时返回空列表，表示使用默认的三个角色。</summary>
    private static IReadOnlyList<ERole>? ParseRoles(string[] args)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, "--roles", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return Array.Empty<ERole>();
        }

        if (index + 1 >= args.Length)
        {
            return null;
        }

        var result = new List<ERole>();
        foreach (string raw in args[index + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "console":
                    result.Add(ERole.eConsole);
                    break;
                case "multimedia":
                    result.Add(ERole.eMultimedia);
                    break;
                case "communications":
                case "comms":
                    result.Add(ERole.eCommunications);
                    break;
                default:
                    return null;
            }
        }

        return result;
    }

    private static int RunSelfTest()
    {
        try
        {
            using var audio = new AudioDeviceService();
            var devices = audio.GetOutputDevices();
            Print($"DEVICES|{devices.Count}");

            if (devices.Count < 2)
            {
                Print("FAIL|至少需要 2 个可用输出设备才能自检");
                return 1;
            }

            var original = audio.GetDefaults();
            string? consoleId = original[ERole.eConsole];
            var target = devices.FirstOrDefault(
                d => !string.Equals(d.Id, consoleId, StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                Print("FAIL|找不到可切换的目标设备");
                return 1;
            }

            Print($"TARGET|{target.Name}|{target.Id}");
            Print($"ORIGINAL|{consoleId ?? "-"}");

            bool switched = false;
            bool verified = false;
            bool restoreOk = false;

            try
            {
                var result = audio.SetDefault(target.Id, includeCommunications: true);
                switched = result.Success;
                Print($"SWITCH|{(result.Success ? "OK" : "FAIL")}|0x{result.HResult:X8}|{result.Message}");

                if (result.Success)
                {
                    verified = Roles.All(role =>
                        string.Equals(audio.GetDefaultId(role), target.Id, StringComparison.OrdinalIgnoreCase));
                    Print($"VERIFY|{(verified ? "OK" : "FAIL")}");
                }
            }
            finally
            {
                bool restored = true;
                foreach (var role in Roles)
                {
                    if (original.TryGetValue(role, out string? originalId) && !string.IsNullOrEmpty(originalId))
                    {
                        restored &= audio.SetRoleDefault(originalId!, role) == 0;
                    }
                }

                bool restoreVerified = Roles.All(role =>
                {
                    original.TryGetValue(role, out string? originalId);
                    return string.Equals(audio.GetDefaultId(role), originalId, StringComparison.OrdinalIgnoreCase);
                });

                restoreOk = restored && restoreVerified;
                Print($"RESTORE|{(restoreOk ? "OK" : "FAIL")}");
            }

            return switched && verified && restoreOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            Print($"ERROR|{ex.GetType().Name}|{ex.Message}");
            Log.Error("--self-test 失败", ex);
            return 1;
        }
    }

    private static AudioDevice? ResolveTarget(IReadOnlyList<AudioDevice> devices, string query)
    {
        if (int.TryParse(query, out int index) && index >= 1 && index <= devices.Count)
        {
            return devices[index - 1];
        }

        return devices.FirstOrDefault(d => string.Equals(d.Id, query, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => d.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static string RoleFlags(AudioDevice device)
    {
        var roles = new List<string>();
        if (device.IsDefaultConsole) roles.Add("console");
        if (device.IsDefaultMultimedia) roles.Add("multimedia");
        if (device.IsDefaultCommunications) roles.Add("communications");
        return roles.Count == 0 ? "-" : string.Join(",", roles);
    }

    private static string RoleKey(ERole role) => role switch
    {
        ERole.eConsole => "console",
        ERole.eMultimedia => "multimedia",
        ERole.eCommunications => "communications",
        _ => role.ToString(),
    };

    private static void Print(string text)
    {
        try
        {
            Console.Out.WriteLine(text.TrimEnd());
            Console.Out.Flush();
        }
        catch
        {
            // 没有控制台时忽略
        }
    }
}
