namespace Fm350Sms;

/// <summary>A number a new message can go to, with the contact name it belongs to.</summary>
internal sealed record Recipient(string Name, string NumberKey)
{
    public string NumberDisplay => PhoneNumbers.Display(NumberKey);
}

/// <summary>Rules of the compose box and the new-message dialog.</summary>
internal static class Compose
{
    internal const string TooLong = "ההודעה ארוכה מדי";

    /// <summary>
    /// "X תווים נותרו · Y הודעות" for the text typed so far (each part is billed as one SMS), or ההודעה ארוכה מדי beyond
    /// <see cref="SmsSubmit.MaxParts"/> parts.
    /// </summary>
    internal static string Counter(string text)
    {
        (int parts, int left) = SmsSubmit.Measure(text);
        return parts > SmsSubmit.MaxParts ? TooLong : $"{left} תווים נותרו · {parts} הודעות";
    }

    /// <summary>
    /// Status line of a sent bubble; a failed one is followed by the retry link. A failure that may have been delivered
    /// shows its error (<see cref="SmsEngine.TimeoutNote"/>, "נשלחו k מתוך n חלקים") instead of נכשלה.
    /// </summary>
    internal static string StatusLabel(SendStatus status, string? error) => status switch
    {
        SendStatus.Sending => "נשלחת…",
        SendStatus.Sent => error is null ? "נשלחה" : "נשלחה (המודם לא אישר)",
        SendStatus.Failed => SmsEngine.IsMaybeSentNote(error) ? error + " —" : "נכשלה —",
        _ => string.Empty,
    };

    /// <summary>Only a failed message offers נסה שוב (a Sent one the modem did not confirm was delivered).</summary>
    internal static bool CanRetry(SendStatus status, string? error) => status == SendStatus.Failed;

    internal const string RetryLabelNormal = "נסה שוב", RetryLabelAgain = "שלח שוב";

    /// <summary>Asked before resending a message that may already have been delivered.</summary>
    internal const string ResendConfirm = "ייתכן שההודעה כבר נשלחה. לשלוח שוב?";

    /// <summary>
    /// A failed send that may still have reached the recipient: the modem timed out, or some parts had gone out
    /// (<see cref="SmsEngine.SendFailureText"/>). Its retry is שלח שוב and asks first.
    /// </summary>
    internal static bool MaybeSent(SendStatus status, string? error) =>
        status == SendStatus.Failed && SmsEngine.IsMaybeSentNote(error);

    /// <summary>The retry link of a failed bubble: שלח שוב when the message may have been sent, else נסה שוב.</summary>
    internal static string RetryLabel(SendStatus status, string? error) =>
        MaybeSent(status, error) ? RetryLabelAgain : RetryLabelNormal;

    /// <summary>Send is allowed only for text with something other than white space, up to <see cref="SmsSubmit.MaxParts"/> parts.</summary>
    internal static bool CanSend(string text) =>
        !string.IsNullOrWhiteSpace(text) && SmsSubmit.Measure(text).Parts <= SmsSubmit.MaxParts;

    /// <summary>What a key press in the compose box does.</summary>
    internal enum KeyAction { Ignore, Send, Newline }

    /// <summary>Enter sends (when allowed; otherwise it is swallowed), Shift+Enter is a new line, Ctrl+Enter also sends.</summary>
    internal static KeyAction KeyActionFor(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers, bool canSend)
    {
        if (key != System.Windows.Input.Key.Enter) return KeyAction.Ignore;
        if (modifiers == System.Windows.Input.ModifierKeys.Shift) return KeyAction.Newline;
        if (modifiers is System.Windows.Input.ModifierKeys.None or System.Windows.Input.ModifierKeys.Control)
            return canSend ? KeyAction.Send : KeyAction.Ignore;
        return KeyAction.Newline;
    }

    /// <summary>Only phone numbers can be sent to (not alphanumeric senders such as a bank's name).</summary>
    internal static bool CanSendTo(string numberKey) => PhoneNumbers.IsPhone(numberKey);

    /// <summary>The number key of a typed phone number (at least 3 digits), or null when the input is not a number.</summary>
    internal static string? NumberFrom(string input)
    {
        string key = PhoneNumbers.Key(input);
        return PhoneNumbers.IsPhone(key) && key.Count(char.IsDigit) >= 3 ? key : null;
    }

    /// <summary>Each phone number of the contacts whose name or number matches <paramref name="query"/>, sorted by name.</summary>
    internal static IReadOnlyList<Recipient> Find(string query, IReadOnlyList<ContactRow> contacts, int max = 50)
    {
        string q = query.Trim();
        string qKey = PhoneNumbers.Key(q);
        bool numeric = q.Length > 0 && PhoneNumbers.IsPhone(qKey);

        bool NumberMatches(string key) =>
            numeric && (key.Contains(qKey, StringComparison.Ordinal) || PhoneNumbers.Matches(key, qKey))
            || q.Length > 0 && PhoneNumbers.Display(key).Contains(q, StringComparison.Ordinal);

        return contacts
            .SelectMany(c => c.NumberKeys.Where(PhoneNumbers.IsPhone).Select(k => (Contact: c, Key: k)))
            .Where(x => q.Length == 0 || x.Contact.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || NumberMatches(x.Key))
            .OrderBy(x => x.Contact.DisplayName, StringComparer.Ordinal)
            .ThenBy(x => x.Contact.Id)
            .Select(x => new Recipient(x.Contact.DisplayName, x.Key))
            .DistinctBy(r => r.NumberKey)
            .Take(max)
            .ToList();
    }
}
