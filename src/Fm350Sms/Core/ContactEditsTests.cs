namespace Fm350Sms;

internal static class ContactEditsTests
{
    private static MessageRow Msg(long id, string key, int minute) =>
        new(id, Direction.In, key, key, null, new DateTimeOffset(2026, 11, 18, 10, minute, 0, TimeSpan.Zero), "x", SendStatus.None, null);

    internal static void Tests()
    {
        var unknown = new ThreadInfo("n:+972505550123", "050-555-0123", ["+972505550123"], null, false, null);
        RenamePlan p1 = ContactEdits.ForThread(unknown, []);
        SelfTest.Check("rename unknown thread creates manual", p1.ManualContactId is null && p1.NumberKeys.SequenceEqual(["+972505550123"]));

        var contacts = new List<ContactRow>
        {
            new(1, "Imported", "google-import", ["+972501111111", "+972502222222"]),
            new(2, "Mine", "manual", ["+972503333333"]),
        };
        var imported = new ThreadInfo("c:1", "Imported", ["+972502222222", "+972504444444"], 1, true, null);
        RenamePlan p2 = ContactEdits.ForThread(imported, contacts);
        SelfTest.Check("rename imported thread creates manual with all numbers",
            p2.ManualContactId is null && p2.NumberKeys.SequenceEqual(["+972502222222", "+972504444444", "+972501111111"]),
            string.Join("|", p2.NumberKeys));

        var manual = new ThreadInfo("c:2", "Mine", ["+972505555555"], 2, true, null);
        RenamePlan p3 = ContactEdits.ForThread(manual, contacts);
        SelfTest.Check("rename manual thread updates it, keeps its numbers",
            p3.ManualContactId == 2 && p3.NumberKeys.SequenceEqual(["+972505555555", "+972503333333"]), string.Join("|", p3.NumberKeys));

        SelfTest.Check("parse numbers lines and commas",
            ContactEdits.ParseNumbers("050-555-0123\r\n 052 123 4567 , 0505550123;\n\n*123").SequenceEqual(["+972505550123", "+972521234567", "*123"]),
            string.Join("|", ContactEdits.ParseNumbers("050-555-0123\r\n 052 123 4567 , 0505550123;\n\n*123")));

        var importedList = new List<ImportedContact> { new("A", ["+972501111111", "+972502222222"]), new("B", ["+972506666666"]) };
        var msgs = new List<MessageRow> { Msg(1, "+972501111111", 1), Msg(2, "+972502222222", 2), Msg(3, "+972507777777", 3), Msg(4, "+972506666666", 4) };
        var after = new List<ContactRow> { new(10, "A", "google-import", ["+972501111111", "+972502222222"]), new(11, "B", "google-import", ["+972506666666"]) };
        string summary = ContactEdits.ImportSummary(importedList, msgs, after);
        SelfTest.Check("import summary text", summary == "נקראו 2 אנשי קשר, 3 מספרים, 2 שיחות זוהו", summary);

        const string noContacts = "לא נמצאו אנשי קשר עם מספר טלפון בקובץ. אנשי הקשר הקיימים לא שונו.";
        SelfTest.Check("import empty refused", ContactEdits.ImportRefusal([]) == noContacts, ContactEdits.ImportRefusal([]));
        SelfTest.Check("import without numbers refused", ContactEdits.ImportRefusal([new("A", [])]) == noContacts);
        SelfTest.Check("import with numbers allowed", ContactEdits.ImportRefusal(importedList) is null);

        SelfTest.Check("export only manual", ContactEdits.ForExport(contacts).Select(c => c.Id).SequenceEqual([2L]));

        string existing = Path.GetTempPath();
        SelfTest.Check("import folder configured", ContactEdits.ImportFolder(existing, @"C:\dl") == existing);
        SelfTest.Check("import folder missing -> fallback", ContactEdits.ImportFolder(@"C:\no\such\folder\x9", @"C:\dl") == @"C:\dl");
        SelfTest.Check("import folder unset -> fallback", ContactEdits.ImportFolder(null, @"C:\dl") == @"C:\dl" && ContactEdits.ImportFolder(" ", @"C:\dl") == @"C:\dl");
    }
}
