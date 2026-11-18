namespace Fm350Sms;

internal static class Log
{
    private static readonly object Gate = new();
    private static string? _redirectDir;

    /// <summary>The file <see cref="Write"/> appends to.</summary>
    public static string CurrentFile => _redirectDir is { } d ? Path.Combine(d, "fm350sms.log") : Paths.LogFile;

    /// <summary>Sends all later lines to <paramref name="dir"/> (self-tests: never the user's log).</summary>
    public static void RedirectTo(string dir)
    {
        lock (Gate)
        {
            _redirectDir = dir;
        }
    }

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                string file = CurrentFile;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
