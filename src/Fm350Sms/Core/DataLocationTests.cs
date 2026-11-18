namespace Fm350Sms;

/// <summary>
/// Regression for the 2026-11-18 incident: instances started from an MSIX-packaged app (the Claude desktop app) wrote
/// %LOCALAPPDATA%\FM350Sms into %LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Local\FM350Sms, while an instance
/// started from Explorer used the real folder: two databases, and the data "vanished" between them.
/// </summary>
internal static class DataLocationTests
{
    internal static void Tests()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string name = "FM350Sms-selftest-" + Guid.NewGuid().ToString("N");
        string dir = Path.Combine(local, name);
        string packages = Path.Combine(local, "Packages");
        try
        {
            string? detected = DataLocation.PackageRedirect(dir);

            // Ground truth: after a write, is the folder physically inside some package's LocalCache?
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "probe.txt"), "x");
            string? physical = Directory.Exists(packages)
                ? Directory.EnumerateDirectories(packages)
                    .Select(p => Path.Combine(p, "LocalCache", "Local", name))
                    .FirstOrDefault(p => File.Exists(Path.Combine(p, "probe.txt")))
                : null;

            SelfTest.Check("data location redirect detected", string.Equals(detected, physical, StringComparison.OrdinalIgnoreCase),
                $"detected={detected ?? "none"} physical={physical ?? "none"}");

            string temp = Path.Combine(Path.GetTempPath(), "fm350sms-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                SelfTest.Check("data location temp not redirected", DataLocation.PackageRedirect(temp) is null);
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch (IOException) { }
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        // Self-tests must never write the user's log.
        string marker = "self-test log marker " + Guid.NewGuid().ToString("N");
        Log.Write(marker);
        string logDir = Path.GetFullPath(Path.GetDirectoryName(Log.CurrentFile)!);
        SelfTest.Check("self-test log isolated",
            !string.Equals(logDir, Path.GetFullPath(Paths.DataDirectory), StringComparison.OrdinalIgnoreCase)
            && File.Exists(Log.CurrentFile) && File.ReadAllText(Log.CurrentFile).Contains(marker),
            Log.CurrentFile);
    }
}
