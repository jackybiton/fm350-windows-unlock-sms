using System.Security.Cryptography;
using System.Text;

namespace Fm350Sms;

/// <param name="Sources">Every stored PDU the message was built from, including duplicate copies of a part.</param>
internal sealed record ReceivedSms(string From, DateTimeOffset? Timestamp, string Text, IReadOnlyList<StoredSms> Sources);

/// <summary>
/// Turns the modem's stored PDUs into complete messages: skips PDUs already handled and joins
/// multi-part messages. Holds no persistent state; the caller supplies <c>isHandled</c>.
/// </summary>
internal sealed class PartAssembler
{
    private readonly Func<string, bool> _isHandled;
    private readonly TimeSpan _incompleteTimeout;
    private readonly Dictionary<string, DateTimeOffset> _firstSeenIncomplete = new();

    /// <param name="isHandled">Receives a PDU hash (see <see cref="KeyOf"/>).</param>
    /// <param name="incompleteTimeout">How long to wait for missing parts before emitting a partial message.</param>
    public PartAssembler(Func<string, bool> isHandled, TimeSpan incompleteTimeout)
    {
        _isHandled = isHandled;
        _incompleteTimeout = incompleteTimeout;
    }

    public static string KeyOf(StoredSms sms) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(sms.Pdu.Trim().ToUpperInvariant())));

    /// <summary>
    /// Returns the messages in <paramref name="stored"/> that were not handled before.
    /// <paramref name="onUndecodable"/> receives the hash of each PDU that cannot be decoded.
    /// </summary>
    public List<ReceivedSms> TakeNew(IReadOnlyList<StoredSms> stored, Action<string>? onUndecodable = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var result = new List<ReceivedSms>();
        var groups = new Dictionary<(string From, int Ref, int Count), List<(SmsPart Part, StoredSms Source)>>();

        foreach (StoredSms sms in stored)
        {
            string key = KeyOf(sms);
            if (_isHandled(key)) continue;

            SmsPart part;
            try
            {
                part = SmsPdu.Decode(sms.Pdu);
            }
            catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException)
            {
                Log.Write($"Skipping undecodable PDU #{sms.Index}: {ex.Message}");
                onUndecodable?.Invoke(key);
                continue;
            }

            if (part.PartCount <= 1)
            {
                result.Add(new ReceivedSms(part.From, part.Timestamp, part.Text, [sms]));
                continue;
            }

            var groupKey = (part.From, part.Reference, part.PartCount);
            if (!groups.TryGetValue(groupKey, out var list))
            {
                groups[groupKey] = list = [];
            }

            list.Add((part, sms));
        }

        foreach (var ((from, reference, count), parts) in groups)
        {
            string id = $"{from}|{reference}|{count}";
            var distinct = parts.GroupBy(p => p.Part.PartNumber).Select(g => g.First()).OrderBy(p => p.Part.PartNumber).ToList();
            bool complete = distinct.Count >= count;
            if (!complete)
            {
                _firstSeenIncomplete.TryAdd(id, now);
                if (now - _firstSeenIncomplete[id] < _incompleteTimeout)
                {
                    continue; // wait for the remaining parts
                }
            }

            _firstSeenIncomplete.Remove(id);
            string text = string.Concat(distinct.Select(p => p.Part.Text));
            if (!complete)
            {
                text += $" [{distinct.Count}/{count} parts received]";
            }

            // Sources lists every stored PDU of the group, re-delivered duplicate parts included, so that all
            // of them are recorded with this message and none becomes a message of its own later.
            result.Add(new ReceivedSms(from, distinct[0].Part.Timestamp, text, parts.Select(p => p.Source).ToList()));
        }

        return result.OrderBy(m => m.Timestamp ?? DateTimeOffset.MaxValue).ToList();
    }
}
