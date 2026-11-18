namespace Fm350Sms;

internal static class ThreadsTests
{
    private static MessageRow Msg(long id, string key, int minute, string body = "x", Direction d = Direction.In) =>
        new(id, d, key, key, null, new DateTimeOffset(2026, 11, 18, 10, minute, 0, TimeSpan.Zero), body, SendStatus.None, null);

    internal static void Tests()
    {
        var msgs = new List<MessageRow> { Msg(1, "+972505550123", 1), Msg(2, "+972521234567", 5, "hello שלום"), Msg(3, "+972501111111", 3) };
        var contacts = new List<ContactRow> { new(10, "אמא", "google-import", ["+972505550123", "+972521234567"]) };
        var t = Threads.Build(msgs, contacts);
        SelfTest.Check("threads one contact two numbers", t.Count == 2, t.Count.ToString());
        SelfTest.Check("threads sorted newest first + contact title", t[0].Id == "c:10" && t[0].Title == "אמא" && t[0].IsSavedContact && t[0].ContactId == 10 && t[0].Last?.Id == 2);
        SelfTest.Check("threads unknown number", t[1].Id == "n:+972501111111" && !t[1].IsSavedContact && t[1].ContactId is null && t[1].Title == "050-111-1111",
            t[1].Id + " " + t[1].Title);
        SelfTest.Check("threads numbers of contact thread", t[0].NumberKeys.Count == 2);

        var dup = new List<ContactRow>
        {
            new(5, "Second", "google-import", ["+972505550123"]),
            new(2, "First", "google-import", ["+972505550123"]),
        };
        var td = Threads.Build([Msg(1, "+972505550123", 1)], dup);
        SelfTest.Check("threads same number two imports -> lower id", td.Count == 1 && td[0].Title == "First", td[0].Title);

        var mix = new List<ContactRow>
        {
            new(1, "Imported", "google-import", ["+972505550123"]),
            new(9, "Manual", "manual", ["+972505550123"]),
        };
        var tm = Threads.Build([Msg(1, "+972505550123", 1)], mix);
        SelfTest.Check("threads manual beats import", tm[0].Title == "Manual" && tm[0].Id == "c:9", tm[0].Title);

        var tier = new List<ContactRow>
        {
            new(1, "Manual last9", "manual", ["505550123"]),
            new(2, "Imported exact", "google-import", ["+972505550123"]),
        };
        var tt = Threads.Build([Msg(1, "+972505550123", 1)], tier);
        SelfTest.Check("threads exact beats last-9 across origins", tt[0].Title == "Imported exact", tt[0].Title);

        var tl = Threads.Build([Msg(1, "+972505550123", 1)], [new(1, "Last9", "google-import", ["505550123"])]);
        SelfTest.Check("threads last-9 fallback", tl[0].Title == "Last9");

        var ts = Threads.Build([Msg(1, "*123", 1)], [new(1, "Short", "google-import", ["123"])]);
        SelfTest.Check("threads short code exact only", !ts[0].IsSavedContact && ts[0].Id == "n:*123");

        var ta = Threads.Build([Msg(1, "Partner", 1)], []);
        SelfTest.Check("threads alpha sender title", ta[0].Title == "Partner");

        var of = Threads.MessagesOf("c:10", msgs, contacts);
        SelfTest.Check("MessagesOf oldest first", of.Count == 2 && of[0].Id == 1 && of[1].Id == 2);
        SelfTest.Check("MessagesOf unknown thread empty", Threads.MessagesOf("n:nope", msgs, contacts).Count == 0);

        SelfTest.Check("search by title", Threads.Search("אמא", msgs, contacts).Count == 1);
        SelfTest.Check("search by display number", Threads.Search("050-111", msgs, contacts).Select(x => x.Id).SequenceEqual(["n:+972501111111"]));
        SelfTest.Check("search by body case-insensitive", Threads.Search("HELLO", msgs, contacts).Select(x => x.Id).SequenceEqual(["c:10"]));
        SelfTest.Check("search empty returns all", Threads.Search("  ", msgs, contacts).Count == 2);
        SelfTest.Check("search local digits finds number", Threads.Search("0501111111", msgs, contacts).Select(x => x.Id).SequenceEqual(["n:+972501111111"]));
        SelfTest.Check("search local digits finds contact thread", Threads.Search("0505550123", msgs, contacts).Select(x => x.Id).SequenceEqual(["c:10"]));
        SelfTest.Check("search local prefix digits", Threads.Search("050111", msgs, contacts).Select(x => x.Id).SequenceEqual(["n:+972501111111"]));
        SelfTest.Check("search last-9 match", Threads.Search("501111111", msgs, contacts).Select(x => x.Id).SequenceEqual(["n:+972501111111"]));

        SelfTest.Check("IdFor contact number", Threads.IdFor("+972521234567", contacts) == "c:10");
        SelfTest.Check("IdFor unknown number", Threads.IdFor("+972507777777", contacts) == "n:+972507777777");
    }
}
