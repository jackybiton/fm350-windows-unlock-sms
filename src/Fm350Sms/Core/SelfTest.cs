namespace Fm350Sms;

internal static class SelfTest
{
    // Each task registers its suite here.
    internal static readonly Action[] Suites =
    [
        SmsPduTests.Tests,
        SmsSubmitTests.Tests,
        PhoneNumbersTests.Tests,
        SmsDatabaseTests.Tests,
        ContactFilesTests.Tests,
        ThreadsTests.Tests,
        SmsEngineTests.Tests,
        LaunchArgsTests.Tests,
        DisplayFormatTests.Tests,
        SettingsTests.Tests,
        ContactEditsTests.Tests,
        ComposeTests.Tests,
        DataLocationTests.Tests,
        DbMergeTests.Tests,
        DraftsTests.Tests,
    ];

    private static int _failures;

    internal static void Check(string name, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? "" : ": " + detail)}");
    }

    internal static int Run()
    {
        _failures = 0;
        string logDir = Path.Combine(Path.GetTempPath(), "fm350sms-selftest-log-" + Guid.NewGuid().ToString("N"));
        Log.RedirectTo(logDir); // engine and database code log; tests must never touch the user's data folder
        foreach (Action suite in Suites)
        {
            try
            {
                suite();
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine($"FAIL {suite.Method.DeclaringType?.Name ?? suite.Method.Name}: {ex.Message}");
            }
        }
        try { Directory.Delete(logDir, true); } catch (IOException) { }
        Console.WriteLine(_failures == 0 ? "Self-test passed." : $"Self-test failed: {_failures}.");
        return _failures;
    }
}
