using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Fm350Sms;

internal sealed record MergeResult(
    int MessagesAdded, int MessagesSkipped, int PartsAdded, int ManualContactsAdded, int ImportedContactsAdded,
    int MetaAdded, string Backup);

/// <summary>
/// <c>--merge-db</c>: copies the history of another <c>sms.db</c> (for example the private copy an MSIX-virtualized
/// instance wrote) into the current database. Backs the target up first, never writes the source, and is idempotent.
/// </summary>
internal static class DbMerge
{
    /// <summary>Outgoing rows with the same number and text whose times differ by at most this are the same message.</summary>
    private static readonly TimeSpan OutgoingWindow = TimeSpan.FromSeconds(2);

    private sealed record SourceMessage(
        long Id, string Sender, string SenderKey, string? SentAt, string ReceivedAt, string Body, string Source,
        string Direction, string Status, string? Error);

    private sealed record SourceContact(long Id, string Name, string Origin, List<string> Keys);

    /// <summary>
    /// The console command. Returns 0 on success, 2 for a bad source, 3 when the target folder is redirected
    /// (the merge would go into a package-private copy) and 4 when source and target are the same file.
    /// </summary>
    internal static int Command(string target, string? source, Func<string, string?> redirect, TextWriter output)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            output.WriteLine("Usage: Fm350Sms.exe --merge-db <path to sms.db>");
            return 2;
        }

        string dataDir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        if (redirect(dataDir) is { } redirected)
        {
            output.WriteLine("התיקייה מנותבת לעותק פרטי של אפליקציה ארוזה (MSIX); המיזוג בוטל. הרץ מ-PowerShell רגיל.");
            output.WriteLine($"Data folder is redirected to {redirected}; not merging. Run this from a normal PowerShell window.");
            return 3;
        }

        if (!File.Exists(source))
        {
            output.WriteLine($"Source database not found: {source}");
            return 2;
        }

        if (SameFile(target, source))
        {
            output.WriteLine("Source and target are the same database; nothing to merge.");
            return 4;
        }

        // The backup line comes first and as soon as it exists, so a caller sees it even if the merge then fails.
        MergeResult r = Merge(target, source, backup => output.WriteLine($"Backup:               {backup}"));
        output.WriteLine($"Messages added:       {r.MessagesAdded}");
        output.WriteLine($"Skipped (duplicates): {r.MessagesSkipped}");
        output.WriteLine($"Message parts added:  {r.PartsAdded}");
        output.WriteLine($"Contacts added:       {r.ManualContactsAdded + r.ImportedContactsAdded} " +
                         $"(manual {r.ManualContactsAdded}, imported {r.ImportedContactsAdded})");
        output.WriteLine($"Meta keys added:      {r.MetaAdded}");
        Log.Write($"Merged database: {r.MessagesAdded} message(s) added, {r.MessagesSkipped} duplicate(s), " +
                  $"{r.PartsAdded} part(s), {r.ManualContactsAdded + r.ImportedContactsAdded} contact(s).");
        return 0;
    }

    internal static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    internal static MergeResult Merge(string targetPath, string sourcePath, Action<string>? backedUp = null)
    {
        // Creates the schema (and runs the one-time fixes) when the target is new or old.
        new SmsDatabase(targetPath).Dispose();

        string work = Path.Combine(Path.GetTempPath(), "fm350sms-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // A copy of the db and its WAL: opening the copy replays the WAL, and the source is never opened at all.
            string copy = Path.Combine(work, "source.db");
            CopyShared(sourcePath, copy);
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(sourcePath + suffix)) CopyShared(sourcePath + suffix, copy + suffix);
            }

            new SmsDatabase(copy).Dispose(); // the copy only: replays the WAL and brings an older schema up to date
            using SqliteConnection src = Open(copy);
            using SqliteConnection dst = Open(targetPath);
            string backup = Backup(dst, targetPath);
            backedUp?.Invoke(backup);
            MergeResult counts = MergeInto(dst, src);
            return counts with { Backup = backup };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            // Only the temp copy; a failure here must not turn a committed merge into an error.
            try { Directory.Delete(work, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static MergeResult MergeInto(SqliteConnection dst, SqliteConnection src)
    {
        List<SourceMessage> messages = ReadMessages(src);
        var sourceParts = new List<(string Hash, long? MessageId)>();
        using (SqliteCommand c = Cmd(src, "SELECT pdu_hash, message_id FROM message_parts ORDER BY rowid", null))
        using (SqliteDataReader r = c.ExecuteReader())
        {
            while (r.Read()) sourceParts.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1)));
        }

        List<SourceContact> sourceContacts = ReadContacts(src);
        var sourceMeta = new List<(string Key, string? Value)>();
        using (SqliteCommand c = Cmd(src, "SELECT key, value FROM meta", null))
        using (SqliteDataReader r = c.ExecuteReader())
        {
            while (r.Read()) sourceMeta.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        }

        using SqliteTransaction tx = dst.BeginTransaction();

        // PDU hashes known to the target; updated as rows are added, so the source also deduplicates by hash.
        var hashes = new Dictionary<string, long?>(StringComparer.Ordinal);
        using (SqliteCommand c = Cmd(dst, "SELECT pdu_hash, message_id FROM message_parts", tx))
        using (SqliteDataReader r = c.ExecuteReader())
        {
            while (r.Read()) hashes[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetInt64(1);
        }

        // The target's rows from before this merge, for the content rule only. Never extended with added rows: two
        // different source messages with the same number and text within 2 s are two messages and both are added.
        var timed = new List<(string Direction, string Key, string Body, DateTimeOffset Time)>();
        using (SqliteCommand c = Cmd(dst, "SELECT direction, sender_key, body, sent_at, received_at FROM messages", tx))
        using (SqliteDataReader r = c.ExecuteReader())
        {
            while (r.Read())
            {
                timed.Add((r.GetString(0), r.GetString(1), r.GetString(2),
                    Time(r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4))));
            }
        }

        ILookup<long, string> partsByMessage = sourceParts.Where(p => p.MessageId is not null)
            .ToLookup(p => p.MessageId!.Value, p => p.Hash);
        int added = 0, skipped = 0, parts = 0;
        foreach (SourceMessage m in messages)
        {
            List<string> own = partsByMessage[m.Id].ToList();
            DateTimeOffset time = Time(m.SentAt, m.ReceivedAt);
            long? existingId = null;
            string? known = m.Direction == nameof(Direction.In) ? own.FirstOrDefault(hashes.ContainsKey) : null;
            bool byHash = known is not null;
            if (byHash) existingId = hashes[known!];

            // Same number, text and time ±2 s as a row the target already had. Outgoing rows have no PDU hash; and the
            // same incoming SMS can be an import-txt row (txt: hash only) in one database and a modem row (PDU hashes)
            // in the other.
            bool duplicate = byHash || timed.Any(t => t.Direction == m.Direction && t.Key == m.SenderKey && t.Body == m.Body
                                                      && (t.Time - time).Duration() <= OutgoingWindow);

            if (duplicate)
            {
                skipped++;
                // Parts the target lacks stay handled, so the modem copy is never saved again: linked to the target's
                // copy when it was found by hash, handled-only when it was found by content.
                foreach (string h in own.Where(h => !hashes.ContainsKey(h)))
                {
                    parts += InsertPart(dst, h, existingId, tx);
                    hashes[h] = existingId;
                }

                continue;
            }

            long id;
            using (SqliteCommand ins = Cmd(dst,
                "INSERT INTO messages(sender,sender_key,sent_at,received_at,body,source,direction,status,error) " +
                "VALUES($s,$k,$sent,$recv,$b,$src,$dir,$st,$e); SELECT last_insert_rowid();", tx))
            {
                ins.Parameters.AddWithValue("$s", m.Sender);
                ins.Parameters.AddWithValue("$k", m.SenderKey);
                ins.Parameters.AddWithValue("$sent", (object?)m.SentAt ?? DBNull.Value);
                ins.Parameters.AddWithValue("$recv", m.ReceivedAt);
                ins.Parameters.AddWithValue("$b", m.Body);
                ins.Parameters.AddWithValue("$src", m.Source);
                ins.Parameters.AddWithValue("$dir", m.Direction);
                ins.Parameters.AddWithValue("$st", m.Status);
                ins.Parameters.AddWithValue("$e", (object?)m.Error ?? DBNull.Value);
                id = (long)ins.ExecuteScalar()!;
            }

            added++;
            foreach (string h in own)
            {
                parts += InsertPart(dst, h, id, tx);
                hashes[h] = id;
            }
        }

        // Handled-only parts, and parts of rows the source no longer has: handled, without a message row.
        var messageIds = messages.Select(m => m.Id).ToHashSet();
        foreach ((string hash, long? messageId) in sourceParts)
        {
            if (messageId is { } mid && messageIds.Contains(mid)) continue;
            if (hashes.ContainsKey(hash)) continue;
            parts += InsertPart(dst, hash, null, tx);
            hashes[hash] = null;
        }

        // Contacts: manual ones unless the same name with the same numbers exists; imported ones only into a target
        // that has none (a Google import replaces them all anyway).
        List<SourceContact> targetContacts = ReadContacts(dst, tx);
        var manualSignatures = targetContacts.Where(c => c.Origin == "manual").Select(Signature).ToHashSet(StringComparer.Ordinal);
        bool targetHasImported = targetContacts.Any(c => c.Origin == "google-import");
        int manualAdded = 0, importedAdded = 0;
        foreach (SourceContact c in sourceContacts)
        {
            if (c.Origin == "manual")
            {
                if (!manualSignatures.Add(Signature(c))) continue;
                InsertContact(dst, c, tx);
                manualAdded++;
            }
            else if (c.Origin == "google-import" && !targetHasImported)
            {
                InsertContact(dst, c, tx);
                importedAdded++;
            }
        }

        int metaAdded = 0;
        foreach ((string key, string? value) in sourceMeta)
        {
            // Not this marker: it means "this folder's messages.txt was imported", which is not true for the target.
            if (key == "legacy-migrated") continue;
            using SqliteCommand c = Cmd(dst, "INSERT OR IGNORE INTO meta(key,value) VALUES($k,$v)", tx);
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            metaAdded += c.ExecuteNonQuery();
        }

        tx.Commit();
        return new MergeResult(added, skipped, parts, manualAdded, importedAdded, metaAdded, "");
    }

    private static List<SourceMessage> ReadMessages(SqliteConnection db)
    {
        var list = new List<SourceMessage>();
        using SqliteCommand c = Cmd(db,
            "SELECT id,sender,sender_key,sent_at,received_at,body,source,direction,status,error FROM messages ORDER BY id", null);
        using SqliteDataReader r = c.ExecuteReader();
        while (r.Read())
        {
            list.Add(new SourceMessage(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9)));
        }

        return list;
    }

    private static List<SourceContact> ReadContacts(SqliteConnection db, SqliteTransaction? tx = null)
    {
        var numbers = new Dictionary<long, List<string>>();
        using (SqliteCommand c = Cmd(db, "SELECT contact_id, number_key FROM contact_numbers ORDER BY rowid", tx))
        using (SqliteDataReader r = c.ExecuteReader())
        {
            while (r.Read())
            {
                long id = r.GetInt64(0);
                if (!numbers.TryGetValue(id, out var l)) numbers[id] = l = [];
                l.Add(r.GetString(1));
            }
        }

        var list = new List<SourceContact>();
        using (SqliteCommand c = Cmd(db, "SELECT id, display_name, origin FROM contacts ORDER BY id", tx))
        using (SqliteDataReader r = c.ExecuteReader())
        {
            while (r.Read())
            {
                long id = r.GetInt64(0);
                list.Add(new SourceContact(id, r.GetString(1), r.GetString(2), numbers.TryGetValue(id, out var l) ? l : []));
            }
        }

        return list;
    }

    private static string Signature(SourceContact c) =>
        c.Name + "\u0001" + string.Join("\u0002", c.Keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static void InsertContact(SqliteConnection db, SourceContact c, SqliteTransaction tx)
    {
        long id;
        using (SqliteCommand ins = Cmd(db, "INSERT INTO contacts(display_name,origin) VALUES($n,$o); SELECT last_insert_rowid();", tx))
        {
            ins.Parameters.AddWithValue("$n", c.Name);
            ins.Parameters.AddWithValue("$o", c.Origin);
            id = (long)ins.ExecuteScalar()!;
        }

        foreach (string k in c.Keys.Distinct(StringComparer.Ordinal))
        {
            using SqliteCommand n = Cmd(db, "INSERT INTO contact_numbers(contact_id,number_key) VALUES($id,$k)", tx);
            n.Parameters.AddWithValue("$id", id);
            n.Parameters.AddWithValue("$k", k);
            n.ExecuteNonQuery();
        }
    }

    private static int InsertPart(SqliteConnection db, string hash, long? messageId, SqliteTransaction tx)
    {
        using SqliteCommand c = Cmd(db, "INSERT OR IGNORE INTO message_parts(pdu_hash,message_id) VALUES($h,$id)", tx);
        c.Parameters.AddWithValue("$h", hash);
        c.Parameters.AddWithValue("$id", (object?)messageId ?? DBNull.Value);
        return c.ExecuteNonQuery();
    }

    /// <summary>Online backup (consistent even with a WAL) to sms.db.bak-yyyyMMdd-HHmmss next to the target.</summary>
    private static string Backup(SqliteConnection db, string targetPath)
    {
        string full = Path.GetFullPath(targetPath);
        string stem = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileName(full) + ".bak-" +
                                                                   DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        string path = stem;
        for (int i = 2; File.Exists(path); i++) path = $"{stem}-{i}";
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            backup.Open();
            db.BackupDatabase(backup);
        }

        return path;
    }

    private static DateTimeOffset Time(string? sentAt, string receivedAt) =>
        DateTimeOffset.Parse(sentAt ?? receivedAt, CultureInfo.InvariantCulture);

    private static void CopyShared(string from, string to)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static SqliteCommand Cmd(SqliteConnection db, string sql, SqliteTransaction? tx)
    {
        SqliteCommand c = db.CreateCommand();
        c.CommandText = sql;
        c.Transaction = tx;
        return c;
    }
}
