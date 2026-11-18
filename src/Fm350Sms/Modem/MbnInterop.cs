using System.Runtime.InteropServices;

namespace Fm350Sms;

// Minimal COM declarations from the Windows SDK mbnapi.h (Mobile Broadband API).
// Only the methods used here are typed; the rest are vtable placeholders and must never be called.

[ComImport, Guid("DCBBBAB6-201B-4BBB-AAEE-338E368AF6FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMbnInterfaceManager
{
    [return: MarshalAs(UnmanagedType.Interface)]
    object GetInterface([MarshalAs(UnmanagedType.BStr)] string interfaceId);

    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)]
    object[] GetInterfaces();
}

[ComImport, Guid("DCBBBAB6-2001-4BBB-AAEE-338E368AF6FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMbnInterface
{
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_InterfaceID();
}

internal enum MbnSmsFlag : uint
{
    All = 0,
    Index = 1,
    New = 2,
    Old = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MbnSmsFilter
{
    public MbnSmsFlag Flag;
    public uint MessageIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MbnSmsStatusInfo
{
    public uint Flag;
    public uint MessageIndex;
}

internal static class MbnSmsFormat
{
    public const uint Pdu = 1;
}

[ComImport, Guid("DCBBBAB6-2015-4BBB-AAEE-338E368AF6FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMbnSms
{
    void _GetSmsConfiguration();
    void _SetSmsConfiguration();
    uint SmsSendPdu([MarshalAs(UnmanagedType.BStr)] string pduData, byte size);
    void _SmsSendCdma();
    void _SmsSendCdmaPdu();
    uint SmsRead(ref MbnSmsFilter filter, uint format);
    uint SmsDelete(ref MbnSmsFilter filter);
    MbnSmsStatusInfo GetSmsStatus();
}

[ComImport, Guid("DCBBBAB6-2016-4BBB-AAEE-338E368AF6FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMbnSmsEvents
{
    void OnSmsConfigurationChange([MarshalAs(UnmanagedType.Interface)] object sms);
    void OnSetSmsConfigurationComplete([MarshalAs(UnmanagedType.Interface)] object sms, uint requestId, int status);
    void OnSmsSendComplete([MarshalAs(UnmanagedType.Interface)] object sms, uint requestId, int status);
    void OnSmsReadComplete(
        [MarshalAs(UnmanagedType.Interface)] object sms,
        uint format,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)] object[]? messages,
        [MarshalAs(UnmanagedType.VariantBool)] bool moreMessages,
        uint requestId,
        int status);
    void OnSmsNewClass0Message(
        [MarshalAs(UnmanagedType.Interface)] object sms,
        uint format,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)] object[]? messages);
    void OnSmsDeleteComplete([MarshalAs(UnmanagedType.Interface)] object sms, uint requestId, int status);
    void OnSmsStatusChange([MarshalAs(UnmanagedType.Interface)] object sms);
}

[ComImport, Guid("DCBBBAB6-2013-4BBB-AAEE-338E368AF6FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMbnSmsReadMsgPdu
{
    uint get_Index();
    uint get_Status();
    [return: MarshalAs(UnmanagedType.BStr)]
    string get_PduData();
}

internal static class MbnClsid
{
    public static readonly Guid InterfaceManager = new("BDFEE05B-4418-11DD-90ED-001C257CCFF1");
}
