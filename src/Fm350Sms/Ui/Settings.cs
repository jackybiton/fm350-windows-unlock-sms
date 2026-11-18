using System.Text.Json;

namespace Fm350Sms;

/// <summary>User settings, stored as JSON at <see cref="Paths.Settings"/>.</summary>
internal sealed class Settings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Folder the contacts import dialog opens in; null = the default.</summary>
    public string? ContactsFolder { get; set; }

    /// <summary>Delete messages from the modem once they are saved (off by default).</summary>
    public bool DeleteAfterSave { get; set; }

    /// <summary>Reads the settings file; a missing or unreadable file gives the defaults.</summary>
    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new Settings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write("Settings unreadable, using defaults: " + ex.Message);
        }

        return new Settings();
    }

    /// <summary>Writes the settings file atomically (temp file, then replace).</summary>
    public void Save(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}
