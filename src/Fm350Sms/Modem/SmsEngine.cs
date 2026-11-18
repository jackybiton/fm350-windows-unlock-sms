using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Fm350Sms;

/// <summary>How a send attempt ended: sent, sent but not confirmed by the modem, or failed.</summary>
internal enum SendOutcome { Sent, SentUnconfirmed, Failed }

/// <summary>
/// Background receiver and sender. One MTA thread owns the modem connection: it reads the modem's store,
/// assembles and saves new messages, toasts them, optionally deletes saved parts from the modem, sends
/// queued outgoing messages and reconnects after errors.
/// </summary>
/// <remarks>All events are raised on the engine thread; UI consumers must marshal to their own thread.</remarks>
internal sealed class SmsEngine : IDisposable
{
    /// <summary>
    /// E_MBN_FAILURE as the completion status of SmsSendPdu: this modem reports it even though the SMS is delivered,
    /// so the message counts as sent (and is never offered for a retry, which would send it twice).
    /// </summary>
    internal const int UnconfirmedSendStatus = unchecked((int)0x80548212);

    /// <summary>Stored in the error column of a Sent message the modem did not confirm.</summary>
    internal const string UnconfirmedNote = "המודם לא אישר את השליחה";

    /// <summary>The outcome of sending one part: <paramref name="error"/> is what the send threw, or null.</summary>
    /// <remarks>Only a completion status counts; the same HRESULT thrown by the call itself, or a timeout, is a failure.</remarks>
    internal static SendOutcome ClassifySend(Exception? error) => error switch
    {
        null => SendOutcome.Sent,
        ModemCompletionException { HResult: UnconfirmedSendStatus } => SendOutcome.SentUnconfirmed,
        _ => SendOutcome.Failed,
    };

    /// <summary>Error of a send the modem did not answer in time: it may have gone out.</summary>
    internal const string TimeoutNote = "ייתכן שנשלחה — המודם לא ענה";

    private static readonly System.Text.RegularExpressions.Regex PartialNote = new(@"^נשלחו \d+ מתוך \d+ חלקים$");

    /// <summary>
    /// The error stored for a failed send: <see cref="TimeoutNote"/> on a timeout, "נשלחו k מתוך n חלקים" when
    /// <paramref name="partsSent"/> parts had already gone out, else the exception's message.
    /// </summary>
    internal static string SendFailureText(Exception error, int partsSent, int totalParts) => error switch
    {
        TimeoutException => TimeoutNote,
        _ when partsSent > 0 => $"נשלחו {partsSent} מתוך {totalParts} חלקים",
        _ => error.Message,
    };

    /// <summary>True for an error from <see cref="SendFailureText"/> that means the message may have been delivered.</summary>
    internal static bool IsMaybeSentNote(string? error) =>
        error is not null && (error == TimeoutNote || PartialNote.IsMatch(error));

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly SmsDatabase _db;
    private volatile bool _deleteAfterSave;
    private readonly ConcurrentQueue<long> _sendQueue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly PartAssembler _assembler;
    private Thread? _thread;
    private bool? _available;
    private long _lastCheckTicks;
    private int _disposed;

    public SmsEngine(SmsDatabase db, bool deleteAfterSave)
    {
        _db = db;
        _deleteAfterSave = deleteAfterSave;
        _assembler = new PartAssembler(db.IsHandled, TimeSpan.FromMinutes(10));
    }

    /// <summary>A new incoming message row was saved (its id). Raised on the engine thread.</summary>
    public event Action<long>? MessageSaved;

    /// <summary>An outgoing message became Sent or Failed (its id). Raised on the engine thread.</summary>
    public event Action<long>? SendStatusChanged;

    /// <summary>The modem became reachable (true) or unreachable (false). Raised on the engine thread.</summary>
    public event Action<bool>? ModemAvailabilityChanged;

    /// <summary>Delete saved messages from the modem; can be changed while the engine runs.</summary>
    public bool DeleteAfterSave
    {
        get => _deleteAfterSave;
        set
        {
            if (_deleteAfterSave == value) return;
            _deleteAfterSave = value;
            Log.Write($"Delete after save: {value}.");
        }
    }

    /// <summary>Time of the last successful read of the modem's store.</summary>
    public DateTimeOffset? LastCheck
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastCheckTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime();
        }
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FM350 SMS engine" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Queues an outgoing row (status Sending) for sending on the engine thread.</summary>
    /// <exception cref="ObjectDisposedException">The engine was disposed.</exception>
    public void QueueSend(long outgoingMessageId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _sendQueue.Enqueue(outgoingMessageId);
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently: the row stays Sending and FailStaleSending marks it failed on the next start.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Set();
        if (_thread is { } t && t != Thread.CurrentThread && !t.Join(TimeSpan.FromSeconds(40))) // a modem request can take 30 s
        {
            return; // still inside a modem call: leave the wait handles alive for it
        }

        _wake.Dispose();
        _stop.Dispose();
    }

    private bool Stopping => _stop.WaitOne(0);

    private void Loop()
    {
        Log.Write($"Engine started (delete after save: {_deleteAfterSave}).");
        while (!Stopping)
        {
            try
            {
                using var client = new ModemSmsClient();
                client.StoreChanged += () => _wake.Set();
                while (!Stopping)
                {
                    SendQueued(client);
                    ReceiveOnce(client);
                    WaitHandle.WaitAny([_stop, _wake], PollInterval);
                }
            }
            catch (Exception ex) when (ex is COMException or TimeoutException or InvalidCastException)
            {
                Log.Write("Modem not available, retrying: " + ex.Message);
                SetAvailable(false);
                FailQueued("המודם אינו זמין: " + ex.Message);
                _stop.WaitOne(RetryDelay);
            }
            catch (SqliteException ex)
            {
                // A busy or locked database must never kill the receiver.
                Log.Write("Database error, retrying: " + ex.Message);
                _stop.WaitOne(RetryDelay);
            }
            catch (Exception ex)
            {
                Log.Write("Engine error, retrying: " + ex);
                _stop.WaitOne(RetryDelay);
            }
        }
    }

    private void ReceiveOnce(ModemSmsClient client)
    {
        bool present = client.IsPresent;
        List<StoredSms> stored = client.ReadAll();
        Interlocked.Exchange(ref _lastCheckTicks, DateTimeOffset.UtcNow.UtcTicks);
        SetAvailable(present);

        SaveNew(_db, _assembler, stored, (id, message) =>
        {
            Log.Write($"Received SMS from {message.From} ({message.Sources.Count} PDU(s)).");
            Toast(message);
            MessageSaved?.Invoke(id);
        });

        if (_deleteAfterSave)
        {
            foreach (StoredSms sms in Deletable(_db, stored))
            {
                client.Delete(sms.InterfaceId, sms.Index);
            }
        }
    }

    private void SendQueued(ModemSmsClient client)
    {
        while (!Stopping && _sendQueue.TryDequeue(out long id))
        {
            MessageRow? row = _db.AllMessages().FirstOrDefault(m => m.Id == id && m.Direction == Direction.Out);
            if (row is null) continue;

            int sent = 0, total = 0;
            try
            {
                byte reference = (byte)Random.Shared.Next(256);
                bool unconfirmed = false;
                IReadOnlyList<SubmitPdu> pdus = SmsSubmit.Encode(row.Sender, row.Body, reference);
                total = pdus.Count;
                foreach (SubmitPdu pdu in pdus)
                {
                    try
                    {
                        client.Send(pdu.Hex, pdu.TpduLength);
                    }
                    catch (Exception ex) when (ClassifySend(ex) == SendOutcome.SentUnconfirmed)
                    {
                        unconfirmed = true; // delivered although the modem reported a failure: never resend it
                    }

                    sent++;
                }

                _db.SetStatus(id, SendStatus.Sent, unconfirmed ? UnconfirmedNote : null);
                Log.Write(unconfirmed ? $"Sent SMS #{id} (not confirmed by the modem)." : $"Sent SMS #{id}.");
            }
            catch (Exception ex) when (ex is not SqliteException)
            {
                // A timeout, or a failure after some parts went out, may still have reached the recipient: the error
                // says so, and the UI asks before sending it again.
                Log.Write($"Sending SMS #{id} failed after {sent} of {total} part(s): {ex.Message}");
                _db.SetStatus(id, SendStatus.Failed, SendFailureText(ex, sent, total));
            }

            SendStatusChanged?.Invoke(id);
        }
    }

    private void FailQueued(string error)
    {
        try
        {
            while (_sendQueue.TryPeek(out long id))
            {
                _db.SetStatus(id, SendStatus.Failed, error);
                _sendQueue.TryDequeue(out _);
                SendStatusChanged?.Invoke(id);
            }
        }
        catch (SqliteException ex)
        {
            Log.Write("Could not mark queued SMS failed: " + ex.Message);
        }
    }

    private void SetAvailable(bool available)
    {
        if (_available == available) return;
        _available = available;
        ModemAvailabilityChanged?.Invoke(available);
    }

    private void Toast(ReceivedSms message)
    {
        try
        {
            string key = PhoneNumbers.Key(message.From);
            ThreadInfo? thread = ThreadFor(key, _db.AllMessages(), _db.Contacts());
            Notifier.Show(thread?.Title ?? message.From, message.Text, message.Timestamp,
                thread is null ? "fm350sms:" : LaunchUri(thread.Id));
        }
        catch (Exception ex)
        {
            // Includes SqliteException from the thread lookup: the message is already saved, a toast is optional.
            Log.Write("Notification failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Saves every new message in <paramref name="stored"/> before anything else happens to it, then calls
    /// <paramref name="onSaved"/> only for messages that got a row. Undecodable PDUs are marked handled without a row.
    /// </summary>
    internal static void SaveNew(SmsDatabase db, PartAssembler assembler, IReadOnlyList<StoredSms> stored, Action<long, ReceivedSms> onSaved)
    {
        foreach (ReceivedSms message in assembler.TakeNew(stored, hash => db.MarkHandledOnly([hash])))
        {
            // Sources holds every stored PDU of the message, re-delivered duplicate parts included,
            // so none of them can later surface as a message of its own.
            if (db.SaveIncoming(message, message.Sources.Select(PartAssembler.KeyOf)) is long id)
            {
                onSaved(id, message);
            }
        }
    }

    /// <summary>The stored PDUs that are safe to delete from the modem: those committed with a message row.</summary>
    internal static List<StoredSms> Deletable(SmsDatabase db, IReadOnlyList<StoredSms> stored) =>
        stored.Where(s => db.HasMessageRow(PartAssembler.KeyOf(s))).ToList();

    internal static ThreadInfo? ThreadFor(string senderKey, IReadOnlyList<MessageRow> messages, IReadOnlyList<ContactRow> contacts) =>
        Threads.Build(messages, contacts).FirstOrDefault(t => t.NumberKeys.Contains(senderKey));

    /// <summary>The toast's protocol URI; the id is percent-encoded (an alphanumeric sender can hold %, #, ? or spaces).</summary>
    internal static string LaunchUri(string threadId) => LaunchArgs.UriScheme + ":thread/" + Uri.EscapeDataString(threadId);
}
