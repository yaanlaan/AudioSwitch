using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AudioSwitch.Interop;
using AudioSwitch.Services;

namespace AudioSwitch.Audio;

public sealed record AudioDevice(
    string Id,
    string Name,
    string Description,
    string InterfaceName,
    int FormFactor,
    bool IsDefaultConsole,
    bool IsDefaultMultimedia,
    bool IsDefaultCommunications)
{
    /// <summary>列表主标题：优先用系统短名（扬声器/耳机/显示器型号）；不可用时退回友好名。</summary>
    public string DisplayName
    {
        get
        {
            string shortName = Description.Trim();
            if (!string.IsNullOrWhiteSpace(shortName) && !shortName.Contains('%'))
            {
                return shortName;
            }

            return StripParenthetical(Name);
        }
    }

    /// <summary>列表副标题：声卡/驱动名，用来区分同名设备；无有效信息时返回空串。</summary>
    public string Subtitle
    {
        get
        {
            string displayName = DisplayName;
            string candidate = InterfaceName.Trim();

            if (!IsUsable(candidate))
            {
                candidate = Parenthetical(Name);
            }

            if (!IsUsable(candidate) || string.Equals(candidate, displayName, StringComparison.OrdinalIgnoreCase))
            {
                candidate = Description.Trim();
            }

            return IsUsable(candidate) && !string.Equals(candidate, displayName, StringComparison.OrdinalIgnoreCase)
                ? candidate
                : string.Empty;
        }
    }

    public string Tooltip => $"{Name}\n{InterfaceName}\n{Id}";

    /// <summary>过滤未解析的本地化占位符（如 %1%0）。</summary>
    private static bool IsUsable(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('%');

    private static string StripParenthetical(string value)
    {
        string text = value.Trim();
        int index = text.LastIndexOf(" (", StringComparison.Ordinal);
        if (index > 0)
        {
            text = text[..index];
        }

        return text.Trim();
    }

    private static string Parenthetical(string value)
    {
        string text = value.Trim();
        if (!text.EndsWith(')'))
        {
            return string.Empty;
        }

        int index = text.LastIndexOf(" (", StringComparison.Ordinal);
        return index > 0 ? text[(index + 2)..^1].Trim() : string.Empty;
    }
}

public sealed record SwitchResult(bool Success, int HResult, string Message);

/// <summary>
/// Core Audio 端点枚举 + IPolicyConfig 默认端点切换。
/// 所有调用都应在 STA 线程（WPF UI 线程 / CLI 主线程）上完成。
/// </summary>
public sealed class AudioDeviceService : IDisposable
{
    private static readonly ERole[] AllRoles =
    {
        ERole.eConsole,
        ERole.eMultimedia,
        ERole.eCommunications,
    };

    private readonly IMMDeviceEnumerator _enumerator;
    private bool _disposed;

    public AudioDeviceService()
    {
        _enumerator = ComFactory.Create<IMMDeviceEnumerator>(CoreAudioIds.MMDeviceEnumeratorClsid);
    }

    public IReadOnlyList<AudioDevice> GetOutputDevices()
    {
        ThrowIfDisposed();

        var defaults = GetDefaults();
        var result = new List<AudioDevice>();

        int hr = _enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.ACTIVE, out var collection);
        if (hr != 0)
        {
            Log.Warn($"EnumAudioEndpoints 失败 (0x{hr:X8})");
            return result;
        }

        if (collection.GetCount(out uint count) != 0)
        {
            Log.Warn("无法读取音频端点数量");
            return result;
        }

        for (uint i = 0; i < count; i++)
        {
            if (collection.Item(i, out var device) != 0)
            {
                continue;
            }

            try
            {
                if (device.GetId(out string id) != 0 || string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string name = string.Empty;
                string description = string.Empty;
                string interfaceName = string.Empty;
                uint formFactor = 0;

                if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) == 0)
                {
                    name = ReadString(store, PropertyKeys.FriendlyName) ?? string.Empty;
                    description = ReadString(store, PropertyKeys.DeviceDesc) ?? string.Empty;
                    interfaceName = ReadString(store, PropertyKeys.InterfaceFriendlyName) ?? string.Empty;
                    formFactor = ReadUInt32(store, PropertyKeys.FormFactor);
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = description;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "未知设备";
                }

                result.Add(new AudioDevice(
                    id.Trim(),
                    name.Trim(),
                    description.Trim(),
                    interfaceName.Trim(),
                    (int)formFactor,
                    IsSame(id, defaults[ERole.eConsole]),
                    IsSame(id, defaults[ERole.eMultimedia]),
                    IsSame(id, defaults[ERole.eCommunications])));
            }
            catch (COMException ex)
            {
                Log.Warn($"读取音频端点信息失败 (0x{ex.HResult:X8})");
            }
        }

        // 当前默认设备置顶，其余保持系统顺序，保证列表稳定
        var current = result.FindIndex(d => d.IsDefaultConsole);
        if (current > 0)
        {
            var item = result[current];
            result.RemoveAt(current);
            result.Insert(0, item);
        }

        return result;
    }

    public string? GetDefaultId(ERole role)
    {
        ThrowIfDisposed();

        int hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, role, out var device);
        if (hr != 0)
        {
            return null;
        }

        if (device.GetId(out string id) != 0 || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return id.Trim();
    }

    public IReadOnlyDictionary<ERole, string?> GetDefaults()
    {
        var defaults = new Dictionary<ERole, string?>();
        foreach (var role in AllRoles)
        {
            defaults[role] = TryGetDefaultId(role);
        }

        return defaults;
    }

    public SwitchResult SetDefault(string deviceId, bool includeCommunications)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return new SwitchResult(false, 0, "设备 ID 无效");
        }

        var roles = includeCommunications
            ? new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications }
            : new[] { ERole.eConsole, ERole.eMultimedia };

        IPolicyConfig policy;
        try
        {
            policy = PolicyConfigFactory.Create();
        }
        catch (Exception ex)
        {
            Log.Error("创建 IPolicyConfig 失败", ex);
            return new SwitchResult(false, ex.HResult, "无法访问 Windows 音频策略接口");
        }

        int firstHResult = 0;
        var failures = new List<string>();

        foreach (var role in roles)
        {
            int hr;
            try
            {
                hr = policy.SetDefaultEndpoint(deviceId, role);
            }
            catch (Exception ex)
            {
                hr = ex.HResult;
            }

            if (hr != 0)
            {
                if (firstHResult == 0)
                {
                    firstHResult = hr;
                }

                failures.Add($"{RoleText(role)}=0x{hr:X8}");
                Log.Warn($"SetDefaultEndpoint({RoleText(role)}) 失败: 0x{hr:X8}");
            }
        }

        if (failures.Count > 0)
        {
            return new SwitchResult(false, firstHResult, $"切换失败（{string.Join("，", failures)}）");
        }

        string? readBack = TryGetDefaultId(ERole.eConsole);
        if (!string.Equals(readBack, deviceId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"切换后回读不一致：期望 {deviceId}，实际 {readBack ?? "(无)"}");
            return new SwitchResult(false, 0, "切换未生效，系统默认输出没有改变");
        }

        Log.Info($"默认输出已切换到 {deviceId}");
        return new SwitchResult(true, 0, "已切换");
    }

    /// <summary>只设置单个角色（自检模式用于恢复原状态）。</summary>
    public int SetRoleDefault(string deviceId, ERole role)
    {
        ThrowIfDisposed();

        try
        {
            return PolicyConfigFactory.Create().SetDefaultEndpoint(deviceId, role);
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    private string? TryGetDefaultId(ERole role)
    {
        try
        {
            return GetDefaultId(role);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取默认音频端点失败：{ex.Message}");
            return null;
        }
    }

    private static bool IsSame(string id, string? other) =>
        other is not null && string.Equals(id.Trim(), other, StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out PROPVARIANT value) != 0)
        {
            return null;
        }

        try
        {
            return value.vt switch
            {
                VarTypes.VT_LPWSTR => Marshal.PtrToStringUni(value.pointerValue),
                VarTypes.VT_BSTR => Marshal.PtrToStringBSTR(value.pointerValue),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"读取端点属性失败：{ex.Message}");
            return null;
        }
        finally
        {
            Ole32.PropVariantClear(ref value);
        }
    }

    private static uint ReadUInt32(IPropertyStore store, PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out PROPVARIANT value) != 0)
        {
            return 0;
        }

        try
        {
            return value.vt == VarTypes.VT_UI4 ? (uint)(value.pointerValue.ToInt64() & 0xFFFFFFFF) : 0;
        }
        finally
        {
            Ole32.PropVariantClear(ref value);
        }
    }

    private static string RoleText(ERole role) => role switch
    {
        ERole.eConsole => "控制台",
        ERole.eMultimedia => "多媒体",
        ERole.eCommunications => "通信",
        _ => role.ToString(),
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose() => _disposed = true;
}
