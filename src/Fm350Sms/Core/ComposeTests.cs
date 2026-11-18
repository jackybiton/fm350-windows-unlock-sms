namespace Fm350Sms;

internal static class ComposeTests
{
    internal static void Tests()
    {
        SelfTest.Check("counter empty", Compose.Counter("") == "160 תווים נותרו · 1 הודעות", Compose.Counter(""));
        SelfTest.Check("counter gsm", Compose.Counter("hello") == "155 תווים נותרו · 1 הודעות", Compose.Counter("hello"));
        SelfTest.Check("counter hebrew", Compose.Counter("שלום") == "66 תווים נותרו · 1 הודעות", Compose.Counter("שלום"));
        SelfTest.Check("counter hebrew two parts", Compose.Counter(new string('א', 71)) == "63 תווים נותרו · 2 הודעות", Compose.Counter(new string('א', 71)));

        string tooLong = new('א', 671); // 11 parts
        SelfTest.Check("counter too long", Compose.Counter(tooLong) == "ההודעה ארוכה מדי", Compose.Counter(tooLong));
        SelfTest.Check("cannot send more than 10 parts", !Compose.CanSend(tooLong) && Compose.CanSend(new string('א', 670)));

        SelfTest.Check("status sending", Compose.StatusLabel(SendStatus.Sending, null) == "נשלחת…");
        SelfTest.Check("status sent", Compose.StatusLabel(SendStatus.Sent, null) == "נשלחה");
        SelfTest.Check("status sent unconfirmed",
            Compose.StatusLabel(SendStatus.Sent, SmsEngine.UnconfirmedNote) == "נשלחה (המודם לא אישר)" && !Compose.CanRetry(SendStatus.Sent, SmsEngine.UnconfirmedNote));
        SelfTest.Check("status failed + retry", Compose.StatusLabel(SendStatus.Failed, "x") == "נכשלה —" && Compose.CanRetry(SendStatus.Failed, "x"));
        string partial = SmsEngine.SendFailureText(new InvalidOperationException("x"), 1, 3);
        SelfTest.Check("maybe-sent timeout: שלח שוב with confirm",
            Compose.MaybeSent(SendStatus.Failed, SmsEngine.TimeoutNote) && Compose.RetryLabel(SendStatus.Failed, SmsEngine.TimeoutNote) == "שלח שוב");
        SelfTest.Check("maybe-sent partial: שלח שוב with confirm",
            Compose.MaybeSent(SendStatus.Failed, partial) && Compose.RetryLabel(SendStatus.Failed, partial) == "שלח שוב", partial);
        SelfTest.Check("plain failure: נסה שוב without confirm",
            !Compose.MaybeSent(SendStatus.Failed, "המודם אינו זמין: x") && Compose.RetryLabel(SendStatus.Failed, "x") == "נסה שוב"
            && !Compose.MaybeSent(SendStatus.Failed, null) && !Compose.MaybeSent(SendStatus.Sent, SmsEngine.TimeoutNote));
        SelfTest.Check("maybe-sent status shows the error",
            Compose.StatusLabel(SendStatus.Failed, SmsEngine.TimeoutNote) == "ייתכן שנשלחה — המודם לא ענה —"
            && Compose.StatusLabel(SendStatus.Failed, partial) == partial + " —", Compose.StatusLabel(SendStatus.Failed, partial));
        SelfTest.Check("retry confirm text", Compose.ResendConfirm == "ייתכן שההודעה כבר נשלחה. לשלוח שוב?");

        SelfTest.Check("status incoming empty", Compose.StatusLabel(SendStatus.None, null) == "" && !Compose.CanRetry(SendStatus.None, null));
        SelfTest.Check("can send text", Compose.CanSend("hi") && !Compose.CanSend("") && !Compose.CanSend("  \r\n\t"));
        SelfTest.Check("can send to phone only", Compose.CanSendTo("+972505550123") && !Compose.CanSendTo("Partner") && !Compose.CanSendTo(""));

        var k = System.Windows.Input.Key.Enter;
        var none = System.Windows.Input.ModifierKeys.None;
        SelfTest.Check("enter sends when allowed", Compose.KeyActionFor(k, none, true) == Compose.KeyAction.Send);
        SelfTest.Check("enter swallowed when not allowed", Compose.KeyActionFor(k, none, false) == Compose.KeyAction.Ignore);
        SelfTest.Check("shift+enter newline", Compose.KeyActionFor(k, System.Windows.Input.ModifierKeys.Shift, true) == Compose.KeyAction.Newline
            && Compose.KeyActionFor(k, System.Windows.Input.ModifierKeys.Shift, false) == Compose.KeyAction.Newline);
        SelfTest.Check("ctrl+enter sends", Compose.KeyActionFor(k, System.Windows.Input.ModifierKeys.Control, true) == Compose.KeyAction.Send);
        SelfTest.Check("other key ignored", Compose.KeyActionFor(System.Windows.Input.Key.A, none, true) == Compose.KeyAction.Ignore);

        SelfTest.Check("number from local", Compose.NumberFrom(" 050-555-0123 ") == "+972505550123");
        SelfTest.Check("number from name is null", Compose.NumberFrom("אמא") is null && Compose.NumberFrom("") is null && Compose.NumberFrom("12") is null);

        var contacts = new List<ContactRow>
        {
            new(1, "ישראל", "google-import", ["+972505550123", "+972521234567"]),
            new(2, "Dan", "manual", ["+972501111111"]),
            new(3, "Bank", "google-import", ["Partner"]),
        };
        var byName = Compose.Find("ישר", contacts);
        SelfTest.Check("find by name lists each number", byName.Count == 2 && byName.All(r => r.Name == "ישראל"));
        SelfTest.Check("find by local digits", Compose.Find("0505550", contacts).Select(r => r.NumberKey).SequenceEqual(["+972505550123"]));
        SelfTest.Check("find by display", Compose.Find("052-123", contacts).Select(r => r.NumberKey).SequenceEqual(["+972521234567"]));
        SelfTest.Check("find case-insensitive", Compose.Find("dan", contacts).Count == 1);
        SelfTest.Check("find skips non-phone", Compose.Find("Bank", contacts).Count == 0);
        SelfTest.Check("find empty lists all sorted", Compose.Find("", contacts).Select(r => r.Name).SequenceEqual(["Dan", "ישראל", "ישראל"]));
        SelfTest.Check("find max", Compose.Find("", contacts, 1).Count == 1);
    }
}
