using System;
using System.Runtime.InteropServices;

namespace AudioSwitch.Interop;

internal static class CoreAudioIds
{
    public const string MMDeviceEnumeratorClsid = "BCDE0395-E52F-467C-8E3D-C4579291692E";
    public const string PolicyConfigClsid = "870AF99C-171D-4F9E-AF0D-E63DF40C2BC9";
}

public enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2,
}

public enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2,
}

[Flags]
public enum DeviceState
{
    ACTIVE = 0x1,
    DISABLED = 0x2,
    NOTPRESENT = 0x4,
    UNPLUGGED = 0x8,
    ALL = 0xF,
}

/// <summary>PROPERTYKEY（GUID + pid）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public int pid;
}

/// <summary>
/// PROPVARIANT。仅在 x64/arm64 目标下使用：8 字节头 + 16 字节联合体 = 24 字节。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public IntPtr pointerValue;
    public IntPtr padding;
}

internal static class VarTypes
{
    public const ushort VT_BSTR = 8;
    public const ushort VT_UI4 = 19;
    public const ushort VT_LPWSTR = 31;
}

internal static class PropertyKeys
{
    private static readonly Guid DevicePropertiesFmtId = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    /// <summary>PKEY_Device_FriendlyName</summary>
    public static readonly PROPERTYKEY FriendlyName = new() { fmtid = DevicePropertiesFmtId, pid = 14 };

    /// <summary>PKEY_Device_DeviceDesc</summary>
    public static readonly PROPERTYKEY DeviceDesc = new() { fmtid = DevicePropertiesFmtId, pid = 2 };

    /// <summary>PKEY_DeviceInterface_FriendlyName（声卡/驱动名）</summary>
    public static readonly PROPERTYKEY InterfaceFriendlyName = new()
    {
        fmtid = new Guid("026E516E-B814-414B-83CD-856D6FEF4822"),
        pid = 2,
    };

    /// <summary>PKEY_AudioEndpoint_FormFactor</summary>
    public static readonly PROPERTYKEY FormFactor = new()
    {
        fmtid = new Guid("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E"),
        pid = 0,
    };
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr iface);

    [PreserveSig]
    int OpenPropertyStore(int access, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out DeviceState state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PROPERTYKEY key);

    [PreserveSig]
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

    [PreserveSig]
    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

    [PreserveSig]
    int Commit();
}

internal static class Ole32
{
    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int PropVariantClear(ref PROPVARIANT value);
}

/// <summary>按 CLSID 创建 COM 组件，并 QI 为指定的 ComImport 接口。</summary>
internal static class ComFactory
{
    public static T Create<T>(string clsid) where T : class
    {
        var clsidType = Type.GetTypeFromCLSID(new Guid(clsid), throwOnError: true)
            ?? throw new InvalidOperationException($"无法解析 CLSID {clsid}");

        object instance;
        try
        {
            instance = Activator.CreateInstance(clsidType)
                ?? throw new InvalidOperationException($"无法创建 COM 对象 {clsid}");
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"无法创建 COM 对象 {clsid} (0x{ex.HResult:X8})", ex);
        }

        return (T)instance;
    }
}
