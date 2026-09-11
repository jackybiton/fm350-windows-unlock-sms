using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Fm350Unlock.Service;

internal static partial class NativeMethods
{
    internal const uint GenericReadWrite = 0xC0000000;
    internal const uint OpenExisting = 3;
    internal const uint FileAttributeNormal = 0x80;
    internal const uint CrSuccess = 0;
    internal const uint DnStarted = 0x00000008;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint LocateDevNode(
        out uint deviceInstance,
        string deviceId,
        uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status")]
    internal static partial uint GetDevNodeStatus(
        out uint status,
        out uint problemNumber,
        uint deviceInstance,
        uint flags);
}
