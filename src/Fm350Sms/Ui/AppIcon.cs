using System.Drawing;
using System.Reflection;

namespace Fm350Sms;

/// <summary>The app icon embedded in the exe (Assets\app.ico, Assets\app.png).</summary>
internal static class AppIcon
{
    /// <summary>The icon at the size closest to <paramref name="size"/> pixels. The caller disposes it.</summary>
    public static Icon Load(int size)
    {
        using Stream stream = Open("Fm350Sms.app.ico");
        return new Icon(stream, size, size);
    }

    /// <summary>Writes the 256 px PNG to <paramref name="path"/> (used as the notification icon) and returns the path.</summary>
    public static string WritePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using Stream stream = Open("Fm350Sms.app.png");
        using FileStream file = File.Create(path);
        stream.CopyTo(file);
        return path;
    }

    private static Stream Open(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException("Embedded resource missing: " + name);
}
