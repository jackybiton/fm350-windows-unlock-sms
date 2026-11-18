namespace Fm350Sms;

internal static class Paths
{
    /// <summary>Environment variable that points the app at another data folder (development and testing only).</summary>
    public const string DataDirOverride = "FM350SMS_DATA_DIR";

    public static string DataDirectory { get; } = DataDirectoryFrom(Environment.GetEnvironmentVariable(DataDirOverride));

    /// <summary>The override folder when one is set, else %LOCALAPPDATA%\FM350Sms.</summary>
    internal static string DataDirectoryFrom(string? overrideDir) =>
        string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FM350Sms")
            : overrideDir.Trim();

    public static string MessagesFile => Path.Combine(DataDirectory, "messages.txt");
    public static string StateFile => Path.Combine(DataDirectory, "state.json");
    public static string LogFile => Path.Combine(DataDirectory, "fm350sms.log");
    public static string Database => Path.Combine(DataDirectory, "sms.db");
    public static string Settings => Path.Combine(DataDirectory, "settings.json");
}
