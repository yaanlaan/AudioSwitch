using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AudioSwitch.Interop;
using AudioSwitch.Services;

namespace AudioSwitch.Audio;

/// <summary>音量弹窗里的一个应用会话（同一进程的多个会话会合并）。</summary>
public sealed record AppVolumeSession(
    uint ProcessId,
    string Name,
    string Subtitle,
    string? ExecutablePath,
    float Volume,
    bool Muted,
    bool IsSystem,
    bool IsActive,
    int SessionCount);

/// <summary>
/// 默认输出设备的端点音量（总音量）与音频会话音量（单个应用）。
/// 所有调用都应在 STA 线程（WPF UI 线程 / CLI 主线程）上完成。
/// </summary>
public sealed class AudioVolumeService : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private string? _endpointId;
    private bool _disposed;

    public AudioVolumeService()
    {
        _enumerator = ComFactory.Create<IMMDeviceEnumerator>(CoreAudioIds.MMDeviceEnumeratorClsid);
    }

    /// <summary>读取总音量，返回 false 表示当前没有可用的输出设备。</summary>
    public bool TryGetMasterVolume(out float volume, out bool muted)
    {
        volume = 0;
        muted = false;

        IAudioEndpointVolume? endpoint = GetEndpointVolume();
        if (endpoint is null)
        {
            return false;
        }

        try
        {
            if (endpoint.GetMasterVolumeLevelScalar(out volume) != 0)
            {
                return false;
            }

            if (endpoint.GetMute(out muted) != 0)
            {
                muted = false;
            }

            volume = Math.Clamp(volume, 0f, 1f);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取总音量失败：{ex.Message}");
            return false;
        }
    }

    public bool SetMasterVolume(float volume)
    {
        IAudioEndpointVolume? endpoint = GetEndpointVolume();
        if (endpoint is null)
        {
            return false;
        }

        Guid context = Guid.Empty;
        try
        {
            // S_FALSE（1）表示值未变化，同样算成功。
            return endpoint.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0f, 1f), ref context) >= 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"设置总音量失败：{ex.Message}");
            return false;
        }
    }

    public bool SetMasterMute(bool muted)
    {
        IAudioEndpointVolume? endpoint = GetEndpointVolume();
        if (endpoint is null)
        {
            return false;
        }

        Guid context = Guid.Empty;
        try
        {
            // S_FALSE（1）表示值未变化，同样算成功。
            return endpoint.SetMute(muted, ref context) >= 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"设置总静音失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>枚举默认输出设备上的音频会话，按进程合并。</summary>
    public IReadOnlyList<AppVolumeSession> GetSessions()
    {
        var result = new List<AppVolumeSession>();
        if (!TryGetDefaultDevice(out IMMDevice? device) || device is null)
        {
            return result;
        }

        IAudioSessionManager2? manager;
        try
        {
            manager = AudioVolumeInterop.Activate<IAudioSessionManager2>(
                device,
                AudioVolumeIds.SessionManager2Iid);
        }
        catch (Exception ex)
        {
            Log.Warn($"打开音频会话管理器失败：{ex.Message}");
            return result;
        }

        if (manager.GetSessionEnumerator(out IAudioSessionEnumerator enumerator) != 0)
        {
            return result;
        }

        if (enumerator.GetCount(out int count) != 0)
        {
            return result;
        }

        var groups = new Dictionary<string, SessionGroup>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < count; i++)
        {
            if (enumerator.GetSession(i, out IAudioSessionControl control) != 0 || control is null)
            {
                continue;
            }

            if (control.GetState(out AudioSessionState state) != 0 || state == AudioSessionState.Expired)
            {
                continue;
            }

            if (control is not IAudioSessionControl2 control2)
            {
                continue;
            }

            uint pid = 0;
            if (control2.GetProcessId(out uint sessionPid) == 0)
            {
                pid = sessionPid;
            }

            bool isSystem = control2.IsSystemSoundsSession() == 0;
            if (isSystem)
            {
                pid = 0;
            }

            if (control is not ISimpleAudioVolume volumeControl
                || volumeControl.GetMasterVolume(out float volume) != 0)
            {
                continue;
            }

            bool muted = volumeControl.GetMute(out bool isMuted) == 0 && isMuted;
            float level = Math.Clamp(volume, 0f, 1f);

            string key = pid.ToString();
            if (!groups.TryGetValue(key, out SessionGroup? group))
            {
                group = new SessionGroup(pid, isSystem);
                groups[key] = group;
            }

            group.Add(control, control2, level, muted, state == AudioSessionState.Active);
        }

        foreach (SessionGroup group in groups.Values)
        {
            result.Add(group.ToSession());
        }

        return result
            .OrderByDescending(s => s.IsActive)
            .ThenBy(s => s.IsSystem ? 1 : 0)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>设置某个进程（或系统声音，pid 0）所有会话的音量。</summary>
    public bool SetSessionVolume(uint pid, float volume, bool? mute = null)
    {
        bool touched = false;
        bool success = true;
        foreach (IAudioSessionControl control in EnumerateSessionControls())
        {
            if (control is not IAudioSessionControl2 control2)
            {
                continue;
            }

            uint sessionPid = 0;
            control2.GetProcessId(out uint value);
            sessionPid = value;

            bool isSystem = control2.IsSystemSoundsSession() == 0;
            if (isSystem)
            {
                sessionPid = 0;
            }

            if (sessionPid != pid)
            {
                continue;
            }

            if (control is not ISimpleAudioVolume volumeControl)
            {
                continue;
            }

            Guid context = Guid.Empty;
            try
            {
                if (volumeControl.SetMasterVolume(Math.Clamp(volume, 0f, 1f), ref context) < 0)
                {
                    success = false;
                }
                else
                {
                    touched = true;
                }

                if (mute.HasValue && volumeControl.SetMute(mute.Value, ref context) < 0)
                {
                    success = false;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"设置应用音量失败（PID {pid}）：{ex.Message}");
                success = false;
            }
        }

        return touched && success;
    }

    private IEnumerable<IAudioSessionControl> EnumerateSessionControls()
    {
        if (!TryGetDefaultDevice(out IMMDevice? device) || device is null)
        {
            yield break;
        }

        IAudioSessionManager2? manager;
        try
        {
            manager = AudioVolumeInterop.Activate<IAudioSessionManager2>(
                device,
                AudioVolumeIds.SessionManager2Iid);
        }
        catch
        {
            yield break;
        }

        if (manager.GetSessionEnumerator(out IAudioSessionEnumerator enumerator) != 0
            || enumerator.GetCount(out int count) != 0)
        {
            yield break;
        }

        for (int i = 0; i < count; i++)
        {
            if (enumerator.GetSession(i, out IAudioSessionControl control) == 0 && control is not null)
            {
                yield return control;
            }
        }
    }

    private IAudioEndpointVolume? GetEndpointVolume()
    {
        if (!TryGetDefaultDevice(out IMMDevice? device) || device is null)
        {
            return null;
        }

        if (device.GetId(out string id) != 0 || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (_endpoint is not null && string.Equals(_endpointId, id, StringComparison.OrdinalIgnoreCase))
        {
            return _endpoint;
        }

        try
        {
            _endpoint = AudioVolumeInterop.Activate<IAudioEndpointVolume>(
                device,
                AudioVolumeIds.EndpointVolumeIid);
            _endpointId = id.Trim();
            return _endpoint;
        }
        catch (Exception ex)
        {
            Log.Warn($"打开端点音量接口失败：{ex.Message}");
            _endpoint = null;
            _endpointId = null;
            return null;
        }
    }

    private bool TryGetDefaultDevice(out IMMDevice? device)
    {
        device = null;
        if (_disposed)
        {
            return false;
        }

        int hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice multimedia);
        if (hr != 0)
        {
            hr = _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out multimedia);
        }

        if (hr != 0)
        {
            return false;
        }

        device = multimedia;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _endpoint = null;
        _endpointId = null;
    }

    private sealed class SessionGroup
    {
        private readonly List<(IAudioSessionControl2 Control, float Volume, bool Muted)> _sessions = new();
        private bool _active;
        private string _title = string.Empty;
        private string _subtitle = string.Empty;
        private string? _path;

        public SessionGroup(uint pid, bool isSystem)
        {
            ProcessId = pid;
            IsSystem = isSystem;
        }

        public uint ProcessId { get; }

        public bool IsSystem { get; }

        public void Add(IAudioSessionControl control, IAudioSessionControl2 control2, float volume, bool muted, bool active)
        {
            _sessions.Add((control2, volume, muted));
            _active |= active;

            if (IsSystem)
            {
                _title = "系统声音";
                _subtitle = "通知、提示音与系统音效";
                return;
            }

            if (string.IsNullOrEmpty(_title))
            {
                ResolveProcess(ProcessId, control, out _title, out _subtitle, out _path);
            }
        }

        public AppVolumeSession ToSession()
        {
            // 同一进程的多个会话通常音量一致，取第一个作为展示值。
            (IAudioSessionControl2 Control, float Volume, bool Muted) first = _sessions[0];
            bool allMuted = _sessions.All(s => s.Muted);
            return new AppVolumeSession(
                ProcessId,
                _title,
                _subtitle,
                _path,
                first.Volume,
                allMuted,
                IsSystem,
                _active,
                _sessions.Count);
        }

        private void ResolveProcess(
            uint pid,
            IAudioSessionControl control,
            out string title,
            out string subtitle,
            out string? path)
        {
            title = $"进程 {pid}";
            subtitle = string.Empty;
            path = null;

            try
            {
                using Process process = Process.GetProcessById((int)pid);
                string? file = null;
                try
                {
                    file = process.MainModule?.FileName;
                }
                catch
                {
                    // 受保护/系统进程可能拒绝访问，回退到进程名。
                }

                string processName = process.ProcessName;
                title = processName;
                path = file;

                if (!string.IsNullOrEmpty(file))
                {
                    try
                    {
                        FileVersionInfo info = FileVersionInfo.GetVersionInfo(file);
                        string? description = info.FileDescription?.Trim();
                        if (!string.IsNullOrEmpty(description))
                        {
                            title = description;
                        }

                        subtitle = Path.GetFileName(file);
                    }
                    catch
                    {
                        subtitle = Path.GetFileName(file);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"读取进程 {pid} 信息失败：{ex.Message}");
            }

            try
            {
                if (control.GetDisplayName(out string displayName) == 0
                    && !string.IsNullOrWhiteSpace(displayName)
                    && !string.Equals(displayName.Trim(), title, StringComparison.OrdinalIgnoreCase))
                {
                    subtitle = string.IsNullOrEmpty(subtitle)
                        ? displayName.Trim()
                        : $"{displayName.Trim()} · {subtitle}";
                }
            }
            catch
            {
                // 忽略会话显示名读取失败
            }
        }
    }
}
