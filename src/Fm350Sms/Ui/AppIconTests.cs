namespace Fm350Sms;

internal static class AppIconTests
{
    public static void Tests()
    {
        foreach (int size in new[] { 16, 24, 32, 48 })
        {
            using System.Drawing.Icon icon = AppIcon.Load(size);
            SelfTest.Check($"app icon {size}px", icon.Width == size && icon.Height == size, $"{icon.Width}x{icon.Height}");
        }

        string dir = Path.Combine(Path.GetTempPath(), "fm350sms-icon-" + Guid.NewGuid().ToString("N"));
        try
        {
            string png = AppIcon.WritePng(Path.Combine(dir, "app.png"));
            byte[] head = File.ReadAllBytes(png).Take(8).ToArray();
            SelfTest.Check("app icon png written", head.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
