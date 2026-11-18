namespace Fm350Sms;

internal sealed record ThreadInfo(
    string Id, string Title, IReadOnlyList<string> NumberKeys, long? ContactId, bool IsSavedContact, MessageRow? Last);

/// <summary>Conversation grouping computed at read time: one thread per contact, else per sender key.</summary>
internal static class Threads
{
    internal static IReadOnlyList<ThreadInfo> Build(IReadOnlyList<MessageRow> messages, IReadOnlyList<ContactRow> contacts)
    {
        List<ContactRow> ordered = OrderContacts(contacts);
        var groups = new Dictionary<string, List<MessageRow>>();
        var contactOf = new Dictionary<string, ContactRow?>();
        foreach (MessageRow m in messages)
        {
            ContactRow? c = Resolve(m.SenderKey, ordered);
            string id = ThreadId(m.SenderKey, c);
            if (!groups.TryGetValue(id, out var list)) groups[id] = list = new List<MessageRow>();
            list.Add(m);
            contactOf[id] = c;
        }

        var result = new List<ThreadInfo>();
        foreach (var (id, list) in groups)
        {
            ContactRow? c = contactOf[id];
            MessageRow last = list.OrderBy(x => x.ReceivedAt).ThenBy(x => x.Id).Last();
            List<string> keys = list.OrderByDescending(x => x.ReceivedAt).Select(x => x.SenderKey).Distinct().ToList();
            string title = c is not null ? c.DisplayName : PhoneNumbers.Display(keys[0]);
            result.Add(new ThreadInfo(id, title, keys, c?.Id, c is not null, last));
        }
        return result
            .OrderByDescending(t => t.Last!.ReceivedAt)
            .ThenByDescending(t => t.Last!.Id)
            .ToList();
    }

    internal static IReadOnlyList<MessageRow> MessagesOf(string threadId, IReadOnlyList<MessageRow> messages, IReadOnlyList<ContactRow> contacts)
    {
        List<ContactRow> ordered = OrderContacts(contacts);
        return messages
            .Where(m => ThreadId(m.SenderKey, Resolve(m.SenderKey, ordered)) == threadId)
            .OrderBy(m => m.ReceivedAt).ThenBy(m => m.Id)
            .ToList();
    }

    internal static IReadOnlyList<ThreadInfo> Search(string query, IReadOnlyList<MessageRow> messages, IReadOnlyList<ContactRow> contacts)
    {
        IReadOnlyList<ThreadInfo> all = Build(messages, contacts);
        string q = query.Trim();
        if (q.Length == 0) return all;

        // A typed number in any form ("0505550123") also finds the thread of that number.
        string qKey = PhoneNumbers.Key(q);
        bool numeric = PhoneNumbers.IsPhone(qKey);
        List<ContactRow> ordered = OrderContacts(contacts);
        var bodyHits = new HashSet<string>();
        foreach (MessageRow m in messages)
        {
            if (m.Body.Contains(q, StringComparison.OrdinalIgnoreCase))
                bodyHits.Add(ThreadId(m.SenderKey, Resolve(m.SenderKey, ordered)));
        }

        return all.Where(t =>
            t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || t.NumberKeys.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || PhoneNumbers.Display(k).Contains(q, StringComparison.OrdinalIgnoreCase)
                                     || numeric && (k.Contains(qKey, StringComparison.Ordinal) || PhoneNumbers.Matches(k, qKey)))
            || bodyHits.Contains(t.Id)).ToList();
    }

    /// <summary>The id of the thread messages from or to <paramref name="numberKey"/> belong to (whether or not it has messages yet).</summary>
    internal static string IdFor(string numberKey, IReadOnlyList<ContactRow> contacts) =>
        ThreadId(numberKey, Resolve(numberKey, OrderContacts(contacts)));

    private static string ThreadId(string senderKey, ContactRow? c) => c is not null ? "c:" + c.Id : "n:" + senderKey;

    // Manual before google-import, then lower Id first.
    private static List<ContactRow> OrderContacts(IReadOnlyList<ContactRow> contacts) =>
        contacts.OrderBy(c => c.Origin == "manual" ? 0 : 1).ThenBy(c => c.Id).ToList();

    private static ContactRow? Resolve(string senderKey, List<ContactRow> ordered)
    {
        foreach (ContactRow c in ordered)
            if (c.NumberKeys.Contains(senderKey)) return c;
        foreach (ContactRow c in ordered)
            if (c.NumberKeys.Any(k => PhoneNumbers.Matches(k, senderKey))) return c;
        return null;
    }
}
