using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Fm350Sms;

/// <summary>
/// Finds out where writes to a folder really land. A process started from an MSIX-packaged app (for example the Claude
/// desktop app, or a shell or build it starts) can get AppData file-system virtualization: its writes to
/// %LOCALAPPDATA%\X go to %LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Local\X, which no process started from
/// Explorer ever reads. The app then runs on a second, private copy of the database.
/// </summary>
internal static partial class DataLocation
{
    /// <summary>
    /// The package-private folder that writes to <paramref name="dir"/> are redirected to, or null when they land in
    /// <paramref name="dir"/> itself. Creates <paramref name="dir"/> (the app would create it anyway) and a probe file
    /// that is deleted when closed.
    /// </summary>
    public static string? PackageRedirect(string dir)
    {
        string full = Path.GetFullPath(dir);
        Directory.CreateDirectory(full);
        string probe = Path.Combine(full, $".location-probe-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        string final;
        using (SafeFileHandle h = File.OpenHandle(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileOptions.DeleteOnClose))
        {
            final = FinalPath(h);
        }

        string landed = Path.GetDirectoryName(final)!;
        if (string.Equals(landed.TrimEnd(Path.DirectorySeparatorChar), full.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return null;

        // Only report package virtualization; junctions or subst drives also change the final path but are not a split.
        string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages") + "\\";
        return landed.StartsWith(packages, StringComparison.OrdinalIgnoreCase)
               && landed.Contains(@"\LocalCache\", StringComparison.OrdinalIgnoreCase)
            ? landed
            : null;
    }

    private static string FinalPath(SafeFileHandle h)
    {
        var buffer = new char[1024];
        uint n = GetFinalPathNameByHandleW(h, buffer, (uint)buffer.Length, 0);
        if (n == 0 || n >= buffer.Length) throw new IOException("GetFinalPathNameByHandle failed: " + Marshal.GetLastPInvokeError());
        string path = new(buffer, 0, (int)n);
        string bs = Path.DirectorySeparatorChar.ToString();
        string longPrefix = bs + bs + "?" + bs; // the \\?\ prefix
        string uncPrefix = longPrefix + "UNC" + bs;
        if (path.StartsWith(uncPrefix, StringComparison.Ordinal)) return bs + bs + path[uncPrefix.Length..];
        return path.StartsWith(longPrefix, StringComparison.Ordinal) ? path[longPrefix.Length..] : path;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
