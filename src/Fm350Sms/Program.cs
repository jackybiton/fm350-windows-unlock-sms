using System.Runtime.InteropServices;
using System.Text;

namespace Fm350Sms;

internal static partial class Program
{
    /// <summary>
    /// STA so the WPF app can run on this thread. Console commands run on an MTA worker thread, as the modem COM API
    /// (<see cref="ModemSmsClient"/>) expects; the receive engine has its own MTA thread.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        LaunchArgs parsed = LaunchArgs.Parse(args);
        if (parsed.IsConsole)
        {
            AttachConsole(-1); // WinExe: borrow the console of the shell that started us
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine();
            int code = 1;
            var worker = new Thread(() => code = RunConsole(parsed)) { Name = "FM350 SMS command" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            worker.Join();
            return code;
        }

        try
        {
            return RunApp(parsed, args);
        }
        catch (Exception ex)
        {
            Log.Write("Fatal: " + ex);
            return 1;
        }
    }

    private static int RunConsole(LaunchArgs args)
    {
        try
        {
            switch (args.Command)
            {
                case "--self-test": return SelfTest.Run() == 0 ? 0 : 1;
                case "--list": return List();
                case "--register": Notifier.Register(); return 0;
                case "--unregister": Notifier.Unregister(); return 0;
                case "--test-notification":
                    Notifier.Register();
                    Notifier.Show("FM350 SMS", "בדיקת התראה / notification test", DateTimeOffset.Now, LaunchArgs.UriScheme + ":");
                    return 0;
                case "--where": return Where();
                case "--merge-db": return DbMerge.Command(Paths.Database, args.CommandArgument, DataLocation.PackageRedirect, Console.Out);
                default:
                    Console.WriteLine($"Unknown argument: {args.Unknown}");
                    Console.WriteLine("Usage: Fm350Sms.exe [--minimized] [--delete-after-save] [fm350sms:thread/<id>]" +
                                      " | --list | --test-notification | --register | --unregister | --self-test" +
                                      " | --where | --merge-db <sms.db>");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Fatal: " + ex);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// The window, tray icon and receiver in one process. A second launch hands its arguments to the running instance
    /// (which comes to the front, on the thread named by a <c>fm350sms:thread/&lt;id&gt;</c> argument) and exits.
    /// </summary>
    private static int RunApp(LaunchArgs args, string[] raw)
    {
        // Before the mutex and before any log line: a virtualized instance must not write anything or block the real one.
        if (DataLocation.PackageRedirect(Paths.DataDirectory) is { } redirected)
        {
            System.Windows.MessageBox.Show(
                "FM350 SMS הופעל מתוך אפליקציה ארוזה (MSIX), ולכן הנתונים היו נשמרים בעותק נפרד ולא בתיקייה האמיתית." +
                " הפעל אותו מסייר הקבצים או מתפריט התחל.\n\n" +
                $"Data folder {Paths.DataDirectory} is redirected to {redirected}; the app would use a separate database.",
                "FM350 SMS", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error,
                System.Windows.MessageBoxResult.OK,
                System.Windows.MessageBoxOptions.RtlReading | System.Windows.MessageBoxOptions.RightAlign);
            return 3;
        }

        using SingleInstance? instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.SendToRunning(raw);
            return 0;
        }

        var app = new App(args, instance);
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// The effective data folder and whether writes to it are redirected into a package's private copy (MSIX
    /// virtualization). Exit code 0 when not redirected, 3 when redirected; the installers stop on 3.
    /// </summary>
    private static int Where()
    {
        string? redirected = DataLocation.PackageRedirect(Paths.DataDirectory);
        Console.WriteLine($"data: {Paths.DataDirectory}");
        Console.WriteLine($"redirected: {redirected ?? "no"}");
        return redirected is null ? 0 : 3;
    }

    private static int List()
    {
        using var client = new ModemSmsClient();
        List<StoredSms> stored = client.ReadAll();
        Console.WriteLine($"{stored.Count} message part(s) stored on the modem.");
        var everything = new PartAssembler(_ => false, TimeSpan.Zero).TakeNew(stored);
        foreach (ReceivedSms message in everything)
        {
            Console.WriteLine();
            string time = message.Timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown time";
            Console.WriteLine($"[{time}] {message.From}{Environment.NewLine}{message.Text}");
        }

        return 0;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
