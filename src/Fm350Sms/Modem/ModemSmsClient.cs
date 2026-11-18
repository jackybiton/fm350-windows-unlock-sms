using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Fm350Sms;

internal sealed record StoredSms(string InterfaceId, uint Index, string Pdu);

/// <summary>
/// Reads, deletes and sends SMS on the modem through the Windows Mobile Broadband API (IMbnSms),
/// which talks MBIM to the device and needs neither an AT port nor administrator rights.
/// </summary>
internal sealed class ModemSmsClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly object _manager;
    private readonly IConnectionPoint _connectionPoint;
    private readonly int _cookie;
    private readonly EventSink _sink;

    public ModemSmsClient()
    {
        Type type = Type.GetTypeFromCLSID(MbnClsid.InterfaceManager, throwOnError: true)!;
        _manager = Activator.CreateInstance(type)!;
        _sink = new EventSink();
        var container = (IConnectionPointContainer)_manager;
        Guid iid = typeof(IMbnSmsEvents).GUID;
        container.FindConnectionPoint(ref iid, out IConnectionPoint? connectionPoint);
        _connectionPoint = connectionPoint ?? throw new COMException("IMbnSmsEvents connection point is missing.");
        _connectionPoint.Advise(_sink, out _cookie);
    }

    /// <summary>Raised when the modem reports a change in its SMS store (for example a new message).</summary>
    public event Action? StoreChanged
    {
        add => _sink.StoreChanged += value;
        remove => _sink.StoreChanged -= value;
    }

    /// <summary>True when at least one SMS-capable mobile broadband interface is present.</summary>
    public bool IsPresent => GetSmsInterfaces().Any();

    public List<StoredSms> ReadAll()
    {
        var result = new List<StoredSms>();
        foreach ((string id, IMbnSms sms) in GetSmsInterfaces())
        {
            var filter = new MbnSmsFilter { Flag = MbnSmsFlag.All };
            List<(uint Index, string Pdu)> messages = _sink.Run(() => sms.SmsRead(ref filter, MbnSmsFormat.Pdu), RequestTimeout);
            result.AddRange(messages.Select(m => new StoredSms(id, m.Index, m.Pdu)));
        }

        return result;
    }

    public void Delete(string interfaceId, uint index)
    {
        foreach ((string id, IMbnSms sms) in GetSmsInterfaces())
        {
            if (id == interfaceId)
            {
                var filter = new MbnSmsFilter { Flag = MbnSmsFlag.Index, MessageIndex = index };
                _sink.Run(() => sms.SmsDelete(ref filter), RequestTimeout);
            }
        }
    }

    /// <summary>
    /// Sends one SMS-SUBMIT PDU (hex including the leading SMSC octet) through the first SMS-capable interface
    /// and waits for <c>OnSmsSendComplete</c>. Throws <see cref="COMException"/> on a failure HRESULT.
    /// </summary>
    /// <param name="size">TPDU octet count, excluding the SMSC octet.</param>
    public void Send(string pduHex, byte size)
    {
        foreach ((_, IMbnSms sms) in GetSmsInterfaces())
        {
            _sink.Run(() => sms.SmsSendPdu(pduHex, size), RequestTimeout);
            return;
        }

        throw new COMException("No mobile broadband SMS interface is present.", unchecked((int)0x80070015)); // ERROR_NOT_READY
    }

    private IEnumerable<(string Id, IMbnSms Sms)> GetSmsInterfaces()
    {
        object[] interfaces;
        try
        {
            interfaces = ((IMbnInterfaceManager)_manager).GetInterfaces();
        }
        catch (COMException)
        {
            yield break; // no mobile broadband interface present right now
        }

        foreach (object item in interfaces)
        {
            if (item is IMbnSms sms)
            {
                yield return (((IMbnInterface)item).get_InterfaceID(), sms);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _connectionPoint.Unadvise(_cookie);
        }
        catch (COMException)
        {
        }

        Marshal.FinalReleaseComObject(_manager);
    }

    [ComVisible(true)]
    private sealed class EventSink : IMbnSmsEvents
    {
        private readonly object _gate = new();
        private readonly Dictionary<uint, Pending> _pending = new();

        public event Action? StoreChanged;

        /// <summary>Starts a request and waits for its completion callback.</summary>
        public List<(uint Index, string Pdu)> Run(Func<uint> start, TimeSpan timeout)
        {
            Pending pending;
            lock (_gate)
            {
                // Holding the lock across the call guarantees the completion callback,
                // which also takes the lock, finds the request registered.
                uint requestId = start();
                pending = new Pending();
                _pending[requestId] = pending;
            }

            if (!pending.Done.Wait(timeout))
            {
                throw new TimeoutException("The modem did not answer the SMS request in time.");
            }

            if (pending.Status < 0)
            {
                // Always a COMException (ThrowExceptionForHR would map some HRESULTs to other types).
                throw new ModemCompletionException(pending.Status);
            }

            return pending.Messages;
        }

        private void Complete(uint requestId, int status, object[]? messages, bool more)
        {
            lock (_gate)
            {
                if (!_pending.TryGetValue(requestId, out Pending? pending))
                {
                    return;
                }

                if (messages is not null)
                {
                    foreach (object message in messages)
                    {
                        var pdu = (IMbnSmsReadMsgPdu)message;
                        pending.Messages.Add((pdu.get_Index(), pdu.get_PduData()));
                    }
                }

                if (status < 0 || !more)
                {
                    pending.Status = status;
                    _pending.Remove(requestId);
                    pending.Done.Set();
                }
            }
        }

        public void OnSmsReadComplete(object sms, uint format, object[]? messages, bool moreMessages, uint requestId, int status)
        {
            try
            {
                Complete(requestId, status, messages, moreMessages);
            }
            catch (Exception ex)
            {
                // Never let an exception escape into the COM caller; fail the request instead.
                Log.Write("SMS read callback failed: " + ex.Message);
                Complete(requestId, ex.HResult < 0 ? ex.HResult : unchecked((int)0x80004005), null, false);
            }
        }

        public void OnSmsDeleteComplete(object sms, uint requestId, int status) => Complete(requestId, status, null, false);

        public void OnSmsStatusChange(object sms) => StoreChanged?.Invoke();

        public void OnSmsNewClass0Message(object sms, uint format, object[]? messages) => StoreChanged?.Invoke();

        public void OnSmsConfigurationChange(object sms) { }

        public void OnSetSmsConfigurationComplete(object sms, uint requestId, int status) { }

        public void OnSmsSendComplete(object sms, uint requestId, int status) => Complete(requestId, status, null, false);

        private sealed class Pending
        {
            public ManualResetEventSlim Done { get; } = new();
            public List<(uint Index, string Pdu)> Messages { get; } = new();
            public int Status { get; set; }
        }
    }
}

/// <summary>A modem request was accepted but its completion callback reported a failure status (not the call itself).</summary>
internal sealed class ModemCompletionException(int status)
    : COMException($"The modem reported error 0x{status:X8}.", status);
