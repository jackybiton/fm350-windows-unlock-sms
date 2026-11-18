using System.Text;

namespace Fm350Sms;

internal static class SmsDatabaseTests
{
    private static ReceivedSms Msg(string from = "+972505550123", string text = "hello") =>
        new(from, DateTimeOffset.Now, text, []);

    private static void Cleanup(string dir)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    internal static void Tests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm350sms-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using (var db = new SmsDatabase(Path.Combine(dir, "a.db")))
            {
                long? id = db.SaveIncoming(Msg(), ["h1", "h2"]);
                SelfTest.Check("db save + handled", id is not null && db.IsHandled("h1") && db.IsHandled("h2") && !db.IsHandled("zz"));
                SelfTest.Check("db duplicate hash rejected",
                    db.SaveIncoming(Msg(), ["h3", "h2"]) is null && db.AllMessages().Count == 1 && !db.IsHandled("h3"));
                db.DeleteMessages([id!.Value]);
                SelfTest.Check("db delete keeps handled", db.AllMessages().Count == 0 && db.IsHandled("h1") && db.IsHandled("h2"));
                db.MarkHandledOnly(["m1"]);
                SelfTest.Check("db mark handled only", db.IsHandled("m1") && db.AllMessages().Count == 0);
            }

            string path2 = Path.Combine(dir, "b.db");
            long outId;
            using (var db = new SmsDatabase(path2))
            {
                outId = db.AddOutgoing("050-555-0123", "out");
                var row = db.AllMessages().Single();
                SelfTest.Check("db outgoing sending",
                    row.Direction == Direction.Out && row.Status == SendStatus.Sending && row.SenderKey == "+972505550123");
            }

            using (var db = new SmsDatabase(path2))
            {
                int n = db.FailStaleSending();
                var row = db.AllMessages().Single();
                SelfTest.Check("db stale sending fails",
                    n == 1 && row.Id == outId && row.Status == SendStatus.Failed && row.Error == "interrupted");
                db.SetStatus(outId, SendStatus.Sent, null);
                SelfTest.Check("db set status", db.AllMessages().Single().Status == SendStatus.Sent);
            }

            var parsed = MessagesTxtParser.Parse("[2026-11-18 04:39:26] +972505550123\r\nמממ\r\n\r\n");
            SelfTest.Check("txt parse sample",
                parsed.Count == 1 && parsed[0].From == "+972505550123" && parsed[0].Text == "מממ" && parsed[0].Timestamp is not null);
            var multi = MessagesTxtParser.Parse("[unknown time] Partner\nline1\nline2\n\n[2026-11-18 05:00:00] 0501234567\nhi\n\n");
            SelfTest.Check("txt parse multi",
                multi.Count == 2 && multi[0].Text == "line1\nline2" && multi[0].Timestamp is null && multi[1].From == "0501234567");

            // v0.1.0 wrote "[time] from\r\n{Text}\r\n\r\n": a blank line inside the text is not a message boundary.
            var inner = MessagesTxtParser.Parse(
                "[2026-11-18 04:39:26] +972505550123\r\npara one\r\n\r\npara two\r\n\r\n[2026-11-18 05:00:00] 0501234567\r\nhi\r\n\r\n");
            SelfTest.Check("txt parse body with inner blank line",
                inner.Count == 2 && inner[0].Text == "para one\n\npara two" && inner[1].Text == "hi",
                string.Join(" | ", inner.Select(x => x.Text.Replace("\n", "\\n"))));
            var bracket = MessagesTxtParser.Parse(
                "[unknown time] Partner\r\nintro\r\n\r\n[note] not a header\r\n\r\n[2026-11-18 05:00:00] 0501234567\r\nhi\r\n\r\n");
            SelfTest.Check("txt parse paragraph starting with [ is body",
                bracket.Count == 2 && bracket[0].Text == "intro\n\n[note] not a header" && bracket[1].From == "0501234567",
                string.Join(" | ", bracket.Select(x => x.Text.Replace("\n", "\\n"))));
            var midLine = MessagesTxtParser.Parse("[unknown time] Partner\nline\n[2026-11-18 05:00:00] x\n\n");
            SelfTest.Check("txt parse header-like line not after a blank line is body",
                midLine.Count == 1 && midLine[0].Text == "line\n[2026-11-18 05:00:00] x", midLine.Count.ToString());

            string legacy = Path.Combine(dir, "legacy");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "messages.txt"), "[2026-11-18 04:39:26] +972505550123\r\nמממ\r\n\r\n", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(legacy, "state.json"), "{\"ABC\":\"2026-11-18T00:00:00+00:00\"}");
            string cPath = Path.Combine(dir, "c.db");
            using (var db = new SmsDatabase(cPath))
            {
                db.MigrateLegacy(legacy);
                db.MigrateLegacy(legacy);
                var all = db.AllMessages();
                SelfTest.Check("db migrate once", all.Count == 1 && all[0].Body == "מממ" && db.IsHandled("ABC"));
                SelfTest.Check("db migrate keeps message time as received time",
                    all[0].SentAt is { } sent && all[0].ReceivedAt == sent, $"{all[0].SentAt:o} / {all[0].ReceivedAt:o}");
            }

            using (var db = new SmsDatabase(cPath))
            {
                db.MigrateLegacy(legacy);
                SelfTest.Check("db migrate idempotent across opens", db.AllMessages().Count == 1);
            }

            // A database migrated by an older build has received_at = migration time on import-txt rows.
            string fPath = Path.Combine(dir, "f.db");
            using (new SmsDatabase(fPath)) { }
            const string oldTime = "2026-11-18T04:39:26.0000000+02:00", migrated = "2026-11-18T12:00:00.0000000+02:00";
            void Raw(string sql)
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + fPath + ";Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }

            string Insert(string src) =>
                "INSERT INTO messages(sender,sender_key,sent_at,received_at,body,source) " +
                $"VALUES('+972505550123','+972505550123','{oldTime}','{migrated}','b','{src}')";
            Raw("DELETE FROM meta WHERE key='fix-import-received-at'");
            Raw(Insert("import-txt"));
            Raw(Insert("modem"));
            using (var db = new SmsDatabase(fPath))
            {
                var rows = db.AllMessages();
                SelfTest.Check("db open fixes import-txt received time",
                    rows.Count == 2 && rows[0].ReceivedAt == DateTimeOffset.Parse(oldTime) && rows[1].ReceivedAt == DateTimeOffset.Parse(migrated));
            }

            Raw(Insert("import-txt"));
            using (var db = new SmsDatabase(fPath))
            {
                SelfTest.Check("db received time fix runs once", db.AllMessages()[2].ReceivedAt == DateTimeOffset.Parse(migrated));
            }

            // A locked or unreadable v0.1.0 file: the app starts, nothing is marked migrated, the next start retries.
            string locked = Path.Combine(dir, "locked");
            Directory.CreateDirectory(locked);
            File.WriteAllText(Path.Combine(locked, "messages.txt"), "[2026-11-18 04:39:26] +972505550123\r\nלוק\r\n\r\n", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(locked, "state.json"), "{\"LOCKED\":\"2026-11-18T00:00:00+00:00\"}");
            string lPath = Path.Combine(dir, "l.db");
            foreach (string lockedName in new[] { "messages.txt", "state.json" })
            {
                using (var db = new SmsDatabase(lPath))
                using (new FileStream(Path.Combine(locked, lockedName), FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    string? error = null;
                    try { db.MigrateLegacy(locked); }
                    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                    SelfTest.Check($"db migrate locked {lockedName} does not throw", error is null, error);
                }

                long marked;
                using (var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + lPath + ";Pooling=False"))
                {
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM meta WHERE key='legacy-migrated'";
                    marked = (long)cmd.ExecuteScalar()!;
                }

                SelfTest.Check($"db migrate locked {lockedName} not marked done", marked == 0, marked.ToString());
            }

            using (var db = new SmsDatabase(lPath))
            {
                db.MigrateLegacy(locked); // unlocked now: the retry imports both
                SelfTest.Check("db migrate retries after unlock",
                    db.AllMessages().Count(m => m.Body == "לוק") == 1 && db.IsHandled("LOCKED"), db.AllMessages().Count.ToString());
            }

            // Schema version: a database from before PRAGMA user_version (0, no message_id index) is upgraded to 1.
            string vPath = Path.Combine(dir, "v.db");
            using (new SmsDatabase(vPath)) { }
            long Pragma(string sql)
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + vPath + ";Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                return Convert.ToInt64(cmd.ExecuteScalar());
            }

            const string indexQuery = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_message_parts_message_id'";
            SelfTest.Check("db new schema at user_version 1 with index", Pragma("PRAGMA user_version") == 1 && Pragma(indexQuery) == 1,
                Pragma("PRAGMA user_version").ToString());
            Pragma("DROP INDEX IF EXISTS ix_message_parts_message_id");
            Pragma("PRAGMA user_version=0");
            using (var db = new SmsDatabase(vPath))
            {
                db.SaveIncoming(Msg(text: "after upgrade"), ["vh1"]);
                SelfTest.Check("db old (user_version 0) opens and works", db.HasMessageRow("vh1"));
            }

            SelfTest.Check("db old upgraded to user_version 1", Pragma("PRAGMA user_version") == 1 && Pragma(indexQuery) == 1,
                Pragma("PRAGMA user_version").ToString());
            using (new SmsDatabase(vPath)) { }
            SelfTest.Check("db upgrade idempotent", Pragma("PRAGMA user_version") == 1 && Pragma(indexQuery) == 1);

            using (var db = new SmsDatabase(Path.Combine(dir, "d.db")))
            {
                long manual = db.SaveManualContact("Mom", ["+972501111111"]);
                ImportedContact[] g = [new("Alice", ["+972502222222", "+972503333333"])];
                db.ReplaceGoogleContacts(g);
                db.ReplaceGoogleContacts(g);
                var cs = db.Contacts();
                SelfTest.Check("db google replace keeps manual",
                    cs.Count == 2 && cs.Count(c => c.Origin == "google-import") == 1 &&
                    cs.Single(c => c.Origin == "manual").Id == manual &&
                    cs.Single(c => c.Origin == "google-import").NumberKeys.Count == 2);
                db.UpdateManualContact(manual, "Mum", ["+972504444444"]);
                var m = db.Contacts().Single(c => c.Id == manual);
                SelfTest.Check("db update manual", m.DisplayName == "Mum" && m.NumberKeys.SequenceEqual(["+972504444444"]));
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
