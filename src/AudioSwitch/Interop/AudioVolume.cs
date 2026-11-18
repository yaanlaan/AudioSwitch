using System;
using System.Runtime.InteropServices;

namespace AudioSwitch.Interop;

/// <summary>Core Audio 音量相关接口：端点音量 + 音频会话（单应用音量）。</summary>
internal static class AudioVolumeIds
{
    public const string EndpointVolumeIid = "5CDF2C82-841E-4546-9722-0CF74078229A";
    public const string SessionManager2Iid = "77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F";

    public const int ClsCtxAll = 0x17;
}

public enum AudioSessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2,
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int GetChannelCount(out uint channelCount);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(ref Guid sessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(ref Guid sessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

    [PreserveSig]
    int RegisterSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterSessionNotification(IntPtr notification);

    [PreserveSig]
    int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr notification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetSession(int index, out IAudioSessionControl session);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig]
    int GetState(out AudioSessionState state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr notification);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl 的 9 个方法 + IAudioSessionControl2 新增的 5 个方法。
    // 这里独立声明完整 vtable，避免 COM 接口继承在不同运行时上的槽位差异。
    [PreserveSig]
    int GetState(out AudioSessionState state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingParam);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr notification);

    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);

    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string instanceIdentifier);

    [PreserveSig]
    int GetProcessId(out uint processId);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

internal static class AudioVolumeInterop
{
    /// <summary>在设备上激活指定的音频接口。</summary>
    public static T Activate<T>(IMMDevice device, string iid) where T : class
    {
        var interfaceId = new Guid(iid);
        int hr = device.Activate(
            ref interfaceId,
            AudioVolumeIds.ClsCtxAll,
            IntPtr.Zero,
            out IntPtr pointer);

        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            object instance = Marshal.GetObjectForIUnknown(pointer);
            if (instance is T typed)
            {
                return typed;
            }

            // 某些接口（如 IAudioSessionControl2）需要额外的 QueryInterface。
            return (T)instance;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}
