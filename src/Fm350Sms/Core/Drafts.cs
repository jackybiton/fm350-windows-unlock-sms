namespace Fm350Sms;

/// <summary>
/// Half-typed compose text kept per conversation, so switching threads (by a click, a toast or הודעה חדשה) never leaves
/// one conversation's text under another one, where a single Enter would send it to the wrong person.
/// </summary>
/// <remarks>
/// Keys are thread ids; a new message to a number without a thread uses <see cref="NewMessageKey"/>, which can never be a
/// thread id. Empty text is not stored.
/// </remarks>
internal sealed class Drafts
{
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

    /// <summary>The key the compose box currently belongs to (null: no conversation shown).</summary>
    public string? Current { get; private set; }

    /// <summary>The key of a new message to <paramref name="numberKey"/> that has no thread yet.</summary>
    internal static string NewMessageKey(string numberKey) => "new:" + numberKey;

    /// <summary>
    /// The compose box moves to <paramref name="key"/>: stores <paramref name="currentText"/> under the key it belonged to
    /// and returns the text saved for <paramref name="key"/> (empty when there is none). Same key: returns the text as is.
    /// </summary>
    public string Switch(string? key, string currentText)
    {
        if (key == Current) return currentText;
        Store(Current, currentText);
        Current = key;
        return key is not null && _texts.TryGetValue(key, out string? text) ? text : string.Empty;
    }

    /// <summary>The conversation's key changed (a new-message draft became a thread, or a thread moved under a contact).</summary>
    public void Rename(string from, string to)
    {
        if (from == to) return;
        if (_texts.Remove(from, out string? text) && !_texts.ContainsKey(to)) _texts[to] = text;
        if (Current == from) Current = to;
    }

    /// <summary>The message of <paramref name="key"/> was sent: forget only that draft.</summary>
    public void Clear(string key) => _texts.Remove(key);

    /// <summary>The stored text of <paramref name="key"/> (not the live compose box of <see cref="Current"/>).</summary>
    public string Get(string key) => _texts.TryGetValue(key, out string? text) ? text : string.Empty;

    private void Store(string? key, string text)
    {
        if (key is null) return;
        if (text.Length == 0) _texts.Remove(key);
        else _texts[key] = text;
    }
}
