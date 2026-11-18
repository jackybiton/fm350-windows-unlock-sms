namespace Fm350Sms;

internal static class SmsEngineTests
{
    // Two-part GSM 7-bit SMS-DELIVER from +15125551234, reference 0xCC, each part "hi".
    private const string Part1 = "00440B915121551532F400005220811203454009050003CC0201D069";
    private const string Part1Again = "00440B915121551532F400005220811203554009050003CC0201D069"; // re-delivered, other timestamp
    private const string Part2 = "00440B915121551532F400005220811203454009050003CC0202D069";

    internal static void Tests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm350sms-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var db = new SmsDatabase(Path.Combine(dir, "e.db"));
            using (var live = new SmsEngine(db, deleteAfterSave: false))
            {
                live.DeleteAfterSave = true;
                SelfTest.Check("engine delete-after-save can change while running", live.DeleteAfterSave);
            }

            var stored = new List<StoredSms> { new("if", 1, Part1), new("if", 2, Part1Again), new("if", 3, Part2) };

            var saved = new List<(long Id, ReceivedSms Msg)>();
            SmsEngine.SaveNew(db, new PartAssembler(db.IsHandled, TimeSpan.FromMinutes(10)), stored, (id, m) => saved.Add((id, m)));
            SelfTest.Check("engine multipart saved once",
                saved.Count == 1 && saved[0].Msg.Text == "hihi" && db.AllMessages().Count == 1, saved.Count.ToString());
            SelfTest.Check("engine duplicate part hash stored",
                stored.All(s => db.IsHandled(PartAssembler.KeyOf(s))));

            // A later pass that gives up on incomplete groups immediately must not turn the duplicate into a message.
            var again = new List<(long, ReceivedSms)>();
            SmsEngine.SaveNew(db, new PartAssembler(db.IsHandled, TimeSpan.Zero), stored, (id, m) => again.Add((id, m)));
            SelfTest.Check("engine duplicate part not a second message", again.Count == 0 && db.AllMessages().Count == 1,
                db.AllMessages().Count.ToString());

            var junk = new StoredSms("if", 9, "ABCDEF");
            db.MarkHandledOnly([PartAssembler.KeyOf(junk)]);
            SelfTest.Check("db message row vs handled-only",
                db.HasMessageRow(PartAssembler.KeyOf(stored[1])) && !db.HasMessageRow(PartAssembler.KeyOf(junk)) && !db.HasMessageRow("none"));
            List<StoredSms> deletable = SmsEngine.Deletable(db, [.. stored, junk, new("if", 10, "0011")]);
            SelfTest.Check("engine deletes only parts with a message row",
                deletable.Count == 3 && deletable.All(stored.Contains), deletable.Count.ToString());

            var msgs = new List<MessageRow>
            {
                new(1, Direction.In, "+972505550123", "+972505550123", null, DateTimeOffset.Now, "a", SendStatus.None, null),
                new(2, Direction.In, "+972501111111", "+972501111111", null, DateTimeOffset.Now, "b", SendStatus.None, null),
            };
            var contacts = new List<ContactRow> { new(10, "אמא", "manual", ["+972505550123"]) };
            ThreadInfo? t1 = SmsEngine.ThreadFor("+972505550123", msgs, contacts);
            ThreadInfo? t2 = SmsEngine.ThreadFor("+972501111111", msgs, contacts);
            SelfTest.Check("engine toast thread contact", t1 is { Id: "c:10", Title: "אמא" }, t1?.Id);
            SelfTest.Check("engine toast thread number", t2?.Id == "n:+972501111111", t2?.Id);
            SelfTest.Check("engine toast launch uri", LaunchArgs.ThreadIdFromUri(SmsEngine.LaunchUri("c:10")) == "c:10");
            const string odd = "n:50%25 off #1? yes";
            string oddUri = SmsEngine.LaunchUri(odd);
            SelfTest.Check("engine launch uri escapes the thread id",
                oddUri.StartsWith("fm350sms:thread/", StringComparison.Ordinal) && oddUri.IndexOfAny(['#', '?', ' ']) < 0, oddUri);
            SelfTest.Check("engine launch uri round trip with % # ? space", LaunchArgs.ThreadIdFromUri(oddUri) == odd,
                LaunchArgs.ThreadIdFromUri(oddUri));
            SelfTest.Check("engine launch uri round trip via --uri", LaunchArgs.Parse(["--uri", oddUri]).ThreadId == odd);

            var engine = new SmsEngine(db, deleteAfterSave: false); // never started: no modem access
            engine.Dispose();
            engine.Dispose();
            bool refused = false;
            try { engine.QueueSend(1); } catch (ObjectDisposedException) { refused = true; }
            SelfTest.Check("engine queue after dispose refused", refused);

            const int unconfirmed = unchecked((int)0x80548212), otherFailure = unchecked((int)0x80548211);
            SelfTest.Check("send outcome success", SmsEngine.ClassifySend(null) == SendOutcome.Sent);
            SelfTest.Check("send outcome 0x80548212 completion -> sent unconfirmed",
                SmsEngine.ClassifySend(new ModemCompletionException(unconfirmed)) == SendOutcome.SentUnconfirmed);
            SelfTest.Check("send outcome 0x80548211 completion -> failed",
                SmsEngine.ClassifySend(new ModemCompletionException(otherFailure)) == SendOutcome.Failed);
            SelfTest.Check("send outcome timeout -> failed",
                SmsEngine.ClassifySend(new TimeoutException()) == SendOutcome.Failed);
            SelfTest.Check("send failure timeout text",
                SmsEngine.SendFailureText(new TimeoutException("t"), 0, 1) == "ייתכן שנשלחה — המודם לא ענה"
                && SmsEngine.SendFailureText(new TimeoutException("t"), 2, 3) == "ייתכן שנשלחה — המודם לא ענה",
                SmsEngine.SendFailureText(new TimeoutException("t"), 0, 1));
            SelfTest.Check("send failure partial text",
                SmsEngine.SendFailureText(new System.Runtime.InteropServices.COMException("c"), 2, 3) == "נשלחו 2 מתוך 3 חלקים",
                SmsEngine.SendFailureText(new System.Runtime.InteropServices.COMException("c"), 2, 3));
            SelfTest.Check("send failure before any part keeps the error",
                SmsEngine.SendFailureText(new System.Runtime.InteropServices.COMException("boom"), 0, 3) == "boom");
            SelfTest.Check("send outcome 0x80548212 from the call itself -> failed",
                SmsEngine.ClassifySend(new System.Runtime.InteropServices.COMException("x", unconfirmed)) == SendOutcome.Failed);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
