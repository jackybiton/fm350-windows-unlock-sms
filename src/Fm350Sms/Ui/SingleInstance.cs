using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Fm350Sms;

/// <summary>
/// One app (window + receiver) per user session. The first launch owns mutex <c>Local\FM350Sms.App</c> and listens on
/// pipe <c>FM350Sms.Activate</c>; a later launch sends its command line there and exits.
/// </summary>
internal sealed partial class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\FM350Sms.App";

    /// <summary>The mutex the v0.1.0 headless receiver held; held too so that receiver cannot start beside the app.</summary>
    private const string LegacyReceiverMutexName = @"Local\FM350Sms.Watch";

    private const string PipeName = "FM350Sms.Activate";
    private const int AsfwAny = -1;

    private readonly Mutex _mutex;
    private readonly Mutex? _legacyMutex;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _listener;

    private SingleInstance(Mutex mutex, Mutex? legacyMutex)
    {
        _mutex = mutex;
        _legacyMutex = legacyMutex;
    }

    /// <summary>
    /// Takes the app mutex, or returns null when another instance owns it. Call on the thread that later disposes it
    /// (a mutex is released by the thread that owns it).
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            mutex.Dispose();
            return null;
        }

        var legacy = new Mutex(true, LegacyReceiverMutexName, out bool legacyFree);
        if (!legacyFree)
        {
            // Another exe (an installed v0.1.0 receiver) still runs; this exe never starts a second receiver of its own.
            Log.Write("Warning: an older FM350 SMS receiver (Local\\FM350Sms.Watch) is still running.");
            legacy.Dispose();
            return new SingleInstance(mutex, null);
        }

        return new SingleInstance(mutex, legacy);
    }

    /// <summary>Hands <paramref name="args"/> to the running instance. False when it could not be reached.</summary>
    public static bool SendToRunning(IReadOnlyList<string> args)
    {
        AllowSetForegroundWindow(AsfwAny); // let the running instance bring its window to the front
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(5000);
            byte[] data = Encoding.UTF8.GetBytes(string.Join('\n', args));
            pipe.Write(data);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Log.Write("Could not reach the running instance: " + ex.Message);
            return false;
        }
    }

    /// <summary>Starts listening; <paramref name="received"/> is called on a background thread with each launch's arguments.</summary>
    public void Listen(Action<string[]> received)
    {
        _listener = new Thread(() => ListenLoop(received)) { IsBackground = true, Name = "FM350 SMS activation" };
        _listener.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_legacyMutex is not null)
        {
            _legacyMutex.ReleaseMutex();
            _legacyMutex.Dispose();
        }

        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private void ListenLoop(Action<string[]> received)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                pipe.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();
                using var buffer = new MemoryStream();
                pipe.CopyTo(buffer);
                string text = Encoding.UTF8.GetString(buffer.ToArray());
                received(text.Length == 0 ? [] : text.Split('\n'));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write("Activation pipe error: " + ex.Message);
                _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
