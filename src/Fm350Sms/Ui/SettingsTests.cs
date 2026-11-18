namespace Fm350Sms;

internal static class SettingsTests
{
    internal static void Tests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm350sms-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "settings.json");
        try
        {
            Settings missing = Settings.Load(path);
            SelfTest.Check("settings defaults", missing.ContactsFolder is null && !missing.DeleteAfterSave);

            new Settings { ContactsFolder = @"C:\x\contacts", DeleteAfterSave = true }.Save(path);
            Settings loaded = Settings.Load(path);
            SelfTest.Check("settings round trip", loaded.ContactsFolder == @"C:\x\contacts" && loaded.DeleteAfterSave);

            File.WriteAllText(path, "{\r\n    \"ContactsFolder\":  \"C:\\\\x\\\\contacts\",\r\n    \"DeleteAfterSave\":  true\r\n}");
            Settings fromInstaller = Settings.Load(path);
            SelfTest.Check("settings as written by Install-Sms.ps1", fromInstaller.DeleteAfterSave && fromInstaller.ContactsFolder == @"C:\x\contacts",
                fromInstaller.ContactsFolder);

            File.WriteAllText(path, "{ not json");
            SelfTest.Check("settings corrupt gives defaults", !Settings.Load(path).DeleteAfterSave);

            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FM350Sms");
            SelfTest.Check("data dir default", Paths.DataDirectoryFrom(null) == local && Paths.DataDirectoryFrom("  ") == local);
            SelfTest.Check("data dir override", Paths.DataDirectoryFrom(@"C:\tmp\fm") == @"C:\tmp\fm");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
