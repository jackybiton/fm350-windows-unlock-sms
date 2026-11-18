using System.Windows;
using System.Windows.Threading;

namespace Fm350Sms;

/// <summary>
/// The app process: owns the database, the receive engine, the window and the tray icon. Runs on the STA UI thread;
/// engine events and activation requests arrive on other threads and are marshalled to the dispatcher.
/// </summary>
internal partial class App : Application
{
    private readonly LaunchArgs _args;
    private readonly SingleInstance _instance;
    private SmsDatabase? _db;
    private SmsEngine? _engine;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _started;

    internal App(LaunchArgs args, SingleInstance instance)
    {
        _args = args;
        _instance = instance;
        DispatcherUnhandledException += OnUnhandled;
        SessionEnding += (_, _) => _exiting = true;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Start();
            _started = true;
        }
        catch (Exception ex)
        {
            FailStartup(ex);
        }
    }

    private void Start()
    {
        try
        {
            Notifier.Register();
        }
        catch (Exception ex)
        {
            Log.Write("Could not register notifications: " + ex.Message);
        }

        Settings settings = Settings.Load(Paths.Settings);
        _db = new SmsDatabase(Paths.Database);
        _db.MigrateLegacy(Paths.DataDirectory);
        _db.FailStaleSending();

        _engine = new SmsEngine(_db, _args.DeleteAfterSave || settings.DeleteAfterSave);
        _window = new MainWindow(_db, _engine, settings);
        _tray = new TrayIcon(ShowWindow, ImportContacts, ExitApp);
        _window.SetIcon(_tray.Image);

        _engine.MessageSaved += _ => Dispatcher.BeginInvoke(() => _window.Reload());
        _engine.SendStatusChanged += _ => Dispatcher.BeginInvoke(() => _window.Reload());
        _engine.ModemAvailabilityChanged += available => Dispatcher.BeginInvoke(() => _window.SetModemAvailable(available));
        _engine.Start();

        _instance.Listen(raw => Dispatcher.BeginInvoke(() => Activate(LaunchArgs.Parse(raw))));
        Log.Write($"App started (minimized: {_args.Minimized}).");
        Activate(_args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _engine?.Dispose();
        _db?.Dispose();
        base.OnExit(e);
    }

    /// <summary>A launch of the exe (this one or a later one): show the window, on a thread when the launch names one.</summary>
    private void Activate(LaunchArgs args)
    {
        if (_window is null || _exiting) return;
        if (args.ThreadId is { } threadId)
        {
            bool found = _window.ShowThread(threadId);
            Log.Write($"Activated for a thread (found: {found}).");
        }
        else if (!args.Minimized || args.OpenRequested)
        {
            _window.BringToFront();
            Log.Write("Activated.");
        }
    }

    /// <summary>
    /// Startup failed: tell the user and end the process, so it does not linger without a window while holding the
    /// single-instance mutex (later launches would only hand their arguments to it).
    /// </summary>
    private void FailStartup(Exception ex)
    {
        Log.Write("Startup failed: " + ex);
        _exiting = true;
        try
        {
            _window?.CloseForExit();
            _tray?.Dispose();
            _engine?.Dispose();
            _db?.Dispose();
        }
        catch (Exception cleanup)
        {
            Log.Write("Cleanup after failed startup: " + cleanup.Message);
        }

        _window = null;
        _tray = null;
        _engine = null;
        _db = null;
        MessageBox.Show("FM350 SMS לא הצליח לעלות: " + ex.Message, "FM350 SMS", MessageBoxButton.OK, MessageBoxImage.Error,
            MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        Shutdown(1);
    }

    private void ShowWindow() => _window?.BringToFront();

    /// <summary>Tray menu ייבוא אנשי קשר.</summary>
    private void ImportContacts()
    {
        ShowWindow();
        _window?.ImportContacts();
    }

    private void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Write("Exit requested.");
        _window?.CloseForExit();
        _tray?.Dispose();
        _tray = null;

        // Stopping the engine can wait for a modem call (up to 40 s): do it off the UI thread, then shut down.
        SmsEngine? engine = _engine;
        _engine = null;
        Task.Run(() => engine?.Dispose()).ContinueWith(_ => Dispatcher.BeginInvoke(Shutdown), TaskScheduler.Default);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (!_started)
        {
            if (!_exiting) FailStartup(e.Exception);
            return;
        }

        // Once running, the receiver must keep going: log UI errors instead of ending the process.
        Log.Write("UI error: " + e.Exception);
    }
}
