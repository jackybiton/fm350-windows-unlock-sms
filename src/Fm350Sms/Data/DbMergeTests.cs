using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Fm350Sms;

/// <summary><c>--merge-db</c> on temp databases only.</summary>
internal static class DbMergeTests
{
    private static ReceivedSms Msg(string text, string from = "+972505550123") => new(from, DateTimeOffset.Now, text, []);

    private static void Raw(string path, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string Hash(string path)
    {
        if (!File.Exists(path)) return "missing";
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    internal static void Tests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fm350sms-merge-test-" + Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(dir, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "messages.txt"), "[2026-11-18 04:39:26] +972505550123\r\nמממ\r\n\r\n", new UTF8Encoding(true));
        string source = Path.Combine(dir, "src", "sms.db");
        string target = Path.Combine(dir, "dst", "sms.db");
        try
        {
            using (var t = new SmsDatabase(target))
            {
                t.MigrateLegacy(legacy); // same txt: hash as the source
                t.SaveIncoming(Msg("in-dup"), ["h1"]);
                long o = t.AddOutgoing("0501234567", "out-dup");
                t.SetStatus(o, SendStatus.Sent, null);
                t.SaveManualContact("Mom", ["+972501111111"]);
            }

            // The source stays open (as a running instance would), so its new rows are only in the WAL.
            var s = new SmsDatabase(source);
            try
            {
                s.MigrateLegacy(legacy);
                s.SaveIncoming(Msg("in-dup"), ["h1", "h1b"]);
                s.SaveIncoming(Msg("in-new"), ["h2", "h3"]);
                long o1 = s.AddOutgoing("0501234567", "out-dup");
                s.SetStatus(o1, SendStatus.Sent, null);
                long o2 = s.AddOutgoing("0501234567", "out-dup"); // same text, sent long after: another message
                s.SetStatus(o2, SendStatus.Sent, null);
                Raw(source, $"UPDATE messages SET sent_at='{DateTimeOffset.Now.AddSeconds(30):o}' WHERE id={o2}");
                s.AddOutgoing("0501234567", "out-new");
                s.MarkHandledOnly(["m1", "h1"]);
                s.SaveManualContact("Mom", ["+972501111111"]);
                s.SaveManualContact("Mom", ["+972501111111", "+972502222222"]); // same name, other numbers
                s.SaveManualContact("Dad", ["+972503333333"]);
                s.ReplaceGoogleContacts([new("Alice", ["+972504444444"]), new("Bob", ["+972505555555"])]);
                Raw(source, "INSERT INTO meta(key,value) VALUES('merge-test','src')");
                Raw(source, "UPDATE meta SET value='src' WHERE key='legacy-migrated'");

                SelfTest.Check("merge source has a WAL", new FileInfo(source + "-wal").Length > 0);
                string before = Hash(source) + Hash(source + "-wal");

                MergeResult r = DbMerge.Merge(target, source);
                SelfTest.Check("merge source unchanged", before == Hash(source) + Hash(source + "-wal"));
                SelfTest.Check("merge counts",
                    r is { MessagesAdded: 3, MessagesSkipped: 3, ManualContactsAdded: 2, ImportedContactsAdded: 2, MetaAdded: 1 },
                    $"added={r.MessagesAdded} skipped={r.MessagesSkipped} parts={r.PartsAdded} manual={r.ManualContactsAdded} " +
                    $"imported={r.ImportedContactsAdded} meta={r.MetaAdded}");

                using (var t = new SmsDatabase(target))
                {
                    var all = t.AllMessages();
                    SelfTest.Check("merge txt and pdu dedup", all.Count(m => m.Body == "מממ") == 1 && all.Count(m => m.Body == "in-dup") == 1);
                    SelfTest.Check("merge new incoming with parts",
                        all.Count(m => m.Body == "in-new") == 1 && t.HasMessageRow("h2") && t.HasMessageRow("h3"));
                    SelfTest.Check("merge dup links missing part", t.HasMessageRow("h1b"));
                    SelfTest.Check("merge outgoing within 2 s dedup, later one kept",
                        all.Count(m => m.Body == "out-dup") == 2 && all.Count(m => m.Body == "out-new") == 1);
                    SelfTest.Check("merge handled-only part", t.IsHandled("m1") && !t.HasMessageRow("m1"));
                    var cs = t.Contacts();
                    SelfTest.Check("merge manual contacts", cs.Count(c => c.Origin == "manual") == 3
                                                           && cs.Count(c => c.DisplayName == "Mom") == 2);
                    SelfTest.Check("merge google into empty target", cs.Count(c => c.Origin == "google-import") == 2);
                }

                SelfTest.Check("merge meta only if missing",
                    Scalar(target, "SELECT COUNT(*) FROM meta WHERE key='merge-test'") == 1
                    && Scalar(target, "SELECT COUNT(*) FROM meta WHERE key='legacy-migrated' AND value='1'") == 1);
                SelfTest.Check("merge backup made",
                    File.Exists(r.Backup) && Path.GetFileName(r.Backup).StartsWith("sms.db.bak-", StringComparison.Ordinal)
                    && Scalar(r.Backup, "SELECT COUNT(*) FROM messages") == 3);

                MergeResult again = DbMerge.Merge(target, source);
                SelfTest.Check("merge idempotent",
                    again is { MessagesAdded: 0, PartsAdded: 0, ManualContactsAdded: 0, ImportedContactsAdded: 0, MetaAdded: 0 }
                    && again.Backup != r.Backup,
                    $"added={again.MessagesAdded} parts={again.PartsAdded}");
                SelfTest.Check("merge source unchanged after second run", before == Hash(source) + Hash(source + "-wal"));
            }
            finally
            {
                s.Dispose();
            }

            // A target that already has imported contacts keeps them: the source's are not added.
            string target2 = Path.Combine(dir, "dst2", "sms.db");
            using (var t = new SmsDatabase(target2)) t.ReplaceGoogleContacts([new("Carol", ["+972506666666"])]);
            MergeResult r2 = DbMerge.Merge(target2, source);
            using (var t = new SmsDatabase(target2))
            {
                SelfTest.Check("merge google skipped when target has some",
                    r2.ImportedContactsAdded == 0 && t.Contacts().Count(c => c.Origin == "google-import") == 1);
            }

            SelfTest.Check("merge does not copy legacy-migrated",
                Scalar(source, "SELECT COUNT(*) FROM meta WHERE key='legacy-migrated'") == 1
                && Scalar(target2, "SELECT COUNT(*) FROM meta WHERE key='legacy-migrated'") == 0);

            // The 2026-11-18 recovery: the same v0.1.0 SMS is an import-txt row (txt: hash, its PDU hash handled-only)
            // in one database and a modem row (PDU hash) in the other. Either way round it must not be added twice.
            ReceivedSms legacySms = MessagesTxtParser.Parse(File.ReadAllText(Path.Combine(legacy, "messages.txt")))[0];
            string txtHash;
            using (var probe = new SmsDatabase(Path.Combine(dir, "probe", "sms.db")))
            {
                probe.MigrateLegacy(legacy);
                using var conn = new SqliteConnection($"Data Source={Path.Combine(dir, "probe", "sms.db")};Pooling=False");
                conn.Open();
                using SqliteCommand q = conn.CreateCommand();
                q.CommandText = "SELECT pdu_hash FROM message_parts WHERE pdu_hash LIKE 'txt:%'";
                txtHash = (string)q.ExecuteScalar()!;
            }

            string modemDb = Path.Combine(dir, "modem", "sms.db"), txtDb = Path.Combine(dir, "txt", "sms.db");
            using (var m = new SmsDatabase(modemDb)) m.SaveIncoming(legacySms, ["PDU-H"]);
            using (var t = new SmsDatabase(txtDb))
            {
                t.MigrateLegacy(legacy);
                t.MarkHandledOnly(["PDU-H"]);
            }

            string modemTarget = Path.Combine(dir, "modem-target", "sms.db"), txtTarget = Path.Combine(dir, "txt-target", "sms.db");
            Directory.CreateDirectory(Path.GetDirectoryName(modemTarget)!);
            Directory.CreateDirectory(Path.GetDirectoryName(txtTarget)!);
            File.Copy(modemDb, modemTarget);
            File.Copy(txtDb, txtTarget);

            MergeResult a = DbMerge.Merge(modemTarget, txtDb);
            using (var t = new SmsDatabase(modemTarget))
            {
                SelfTest.Check("merge import-txt row into modem row is a duplicate",
                    a.MessagesAdded == 0 && a.MessagesSkipped == 1 && t.AllMessages().Count == 1
                    && t.IsHandled(txtHash) && !t.HasMessageRow(txtHash) && t.HasMessageRow("PDU-H"),
                    $"added={a.MessagesAdded} skipped={a.MessagesSkipped}");
            }

            MergeResult b = DbMerge.Merge(txtTarget, modemDb);
            using (var t = new SmsDatabase(txtTarget))
            {
                SelfTest.Check("merge modem row into import-txt row is a duplicate",
                    b.MessagesAdded == 0 && b.MessagesSkipped == 1 && t.AllMessages().Count == 1 && t.IsHandled("PDU-H"),
                    $"added={b.MessagesAdded} skipped={b.MessagesSkipped}");
            }

            using (var t = new SmsDatabase(Path.Combine(dir, "other", "sms.db")))
            {
                t.SaveIncoming(legacySms with { Text = "different text" }, ["PDU-X"]);
            }

            SelfTest.Check("merge content dedup needs same text",
                DbMerge.Merge(Path.Combine(dir, "other", "sms.db"), txtDb).MessagesAdded == 1);

            // Two different source messages (other PDU hashes) from the same sender with the same text within 2 s are
            // two messages: content dedup compares only with what the target had before the merge.
            string twinsSrc = Path.Combine(dir, "twins-src", "sms.db"), twinsDst = Path.Combine(dir, "twins-dst", "sms.db");
            DateTimeOffset at = new(2026, 11, 18, 9, 0, 0, TimeSpan.FromHours(2));
            using (var t = new SmsDatabase(twinsSrc))
            {
                t.SaveIncoming(new ReceivedSms("+972505550123", at, "ok", []), ["TW1"]);
                t.SaveIncoming(new ReceivedSms("+972505550123", at.AddSeconds(1), "ok", []), ["TW2"]);
                long a1 = t.AddOutgoing("0501234567", "same");
                long a2 = t.AddOutgoing("0501234567", "same");
                t.SetStatus(a1, SendStatus.Sent, null);
                t.SetStatus(a2, SendStatus.Sent, null);
            }

            using (new SmsDatabase(twinsDst)) { }
            MergeResult twins = DbMerge.Merge(twinsDst, twinsSrc);
            SelfTest.Check("merge keeps same-content source messages",
                twins.MessagesAdded == 4 && twins.MessagesSkipped == 0, $"added={twins.MessagesAdded} skipped={twins.MessagesSkipped}");
            MergeResult twinsAgain = DbMerge.Merge(twinsDst, twinsSrc);
            SelfTest.Check("merge same-content twins idempotent", twinsAgain.MessagesAdded == 0 && twinsAgain.MessagesSkipped == 4,
                $"added={twinsAgain.MessagesAdded} skipped={twinsAgain.MessagesSkipped}");

            var output = new StringWriter();
            int redirected = DbMerge.Command(Path.Combine(dir, "dst3", "sms.db"), source, _ => @"C:\elsewhere", output);
            SelfTest.Check("merge refuses redirected folder", redirected == 3 && !Directory.Exists(Path.Combine(dir, "dst3")));
            SelfTest.Check("merge refuses same file",
                DbMerge.Command(target, Path.Combine(dir, "dst", ".", "sms.db"), _ => null, output) == 4);
            SelfTest.Check("merge refuses missing source",
                DbMerge.Command(target, Path.Combine(dir, "nope.db"), _ => null, output) == 2);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
