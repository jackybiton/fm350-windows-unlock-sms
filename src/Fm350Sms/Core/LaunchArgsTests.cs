namespace Fm350Sms;

internal static class LaunchArgsTests
{
    internal static void Tests()
    {
        SelfTest.Check("uri thread id", LaunchArgs.ThreadIdFromUri("fm350sms:thread/c:10") == "c:10");
        SelfTest.Check("uri number thread id", LaunchArgs.ThreadIdFromUri("fm350sms:thread/n:+972505550123") == "n:+972505550123");
        SelfTest.Check("uri escaped and trailing slash",
            LaunchArgs.ThreadIdFromUri("FM350SMS:thread/n%3APartner%20IL/") == "n:Partner IL");
        SelfTest.Check("uri engine round trip", LaunchArgs.ThreadIdFromUri(SmsEngine.LaunchUri("c:7")) == "c:7");
        SelfTest.Check("uri without thread", LaunchArgs.ThreadIdFromUri("fm350sms:") is null
                                             && LaunchArgs.ThreadIdFromUri("fm350sms:thread/") is null
                                             && LaunchArgs.ThreadIdFromUri("fm350sms:other/c:1") is null);

        LaunchArgs app = LaunchArgs.Parse([]);
        SelfTest.Check("args none is app", !app.IsConsole && !app.Minimized && !app.OpenRequested);

        LaunchArgs watch = LaunchArgs.Parse(["--watch"]);
        SelfTest.Check("args --watch is app in tray", !watch.IsConsole && !watch.DeleteAfterSave && watch.Minimized);

        LaunchArgs startup = LaunchArgs.Parse(["--delete-after-save", "--minimized"]);
        SelfTest.Check("args minimized + delete", !startup.IsConsole && startup.Minimized && startup.DeleteAfterSave);

        LaunchArgs uri = LaunchArgs.Parse(["fm350sms:thread/c:3"]);
        SelfTest.Check("args toast uri", !uri.IsConsole && uri.OpenRequested && uri.ThreadId == "c:3");

        LaunchArgs bare = LaunchArgs.Parse(["fm350sms:"]);
        SelfTest.Check("args bare uri opens window", !bare.IsConsole && bare.OpenRequested && bare.ThreadId is null);

        LaunchArgs merge = LaunchArgs.Parse(["--merge-db", @"C:\x\sms.db"]);
        SelfTest.Check("args merge-db path", merge.IsConsole && merge.Command == "--merge-db" && merge.CommandArgument == @"C:\x\sms.db");
        SelfTest.Check("args send-test removed", LaunchArgs.Parse(["--send-test", "1"]) is { Command: null, Unknown: "--send-test" });
        SelfTest.Check("args where console", LaunchArgs.Parse(["--where"]) is { IsConsole: true, Command: "--where" });

        LaunchArgs selfTest = LaunchArgs.Parse(["--self-test"]);
        SelfTest.Check("args self-test console", selfTest.IsConsole && selfTest.Command == "--self-test");

        LaunchArgs registered = LaunchArgs.Parse(["--uri", "fm350sms:thread/c:3"]);
        SelfTest.Check("args --uri thread", !registered.IsConsole && registered.OpenRequested && registered.ThreadId == "c:3");

        // A URI with quotes split into several tokens by a launcher that does not percent-encode.
        LaunchArgs injected = LaunchArgs.Parse(["--uri", "fm350sms:\"", "--merge-db", "1"]);
        SelfTest.Check("args --uri injection not console", !injected.IsConsole && injected.Command is null
                                                            && !injected.DeleteAfterSave && injected.ThreadId is null);
        LaunchArgs injectedDelete = LaunchArgs.Parse(["--uri", "fm350sms:thread/c:1", "--delete-after-save", "--unregister"]);
        SelfTest.Check("args --uri injection no delete", !injectedDelete.IsConsole && !injectedDelete.DeleteAfterSave
                                                          && injectedDelete.OpenRequested);
        LaunchArgs bareUri = LaunchArgs.Parse(["fm350sms:x", "--delete-after-save"]);
        SelfTest.Check("args uri ignores delete", !bareUri.DeleteAfterSave && !bareUri.IsConsole);
        LaunchArgs bareSend = LaunchArgs.Parse(["--merge-db", "1", "fm350sms:thread/c:2"]);
        SelfTest.Check("args uri ignores command", !bareSend.IsConsole && bareSend.Command is null && bareSend.ThreadId == "c:2");
        LaunchArgs emptyUri = LaunchArgs.Parse(["--uri"]);
        SelfTest.Check("args --uri empty opens window", !emptyUri.IsConsole && emptyUri.OpenRequested && emptyUri.ThreadId is null);

        LaunchArgs junk = LaunchArgs.Parse(["--bogus"]);
        SelfTest.Check("args unknown is console usage", junk.IsConsole && junk.Command is null && junk.Unknown == "--bogus");
    }
}
