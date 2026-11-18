using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fm350Sms;

internal enum Direction { In, Out }

internal enum SendStatus { None, Sending, Sent, Failed }

internal sealed record MessageRow(
    long Id, Direction Direction, string Sender, string SenderKey, DateTimeOffset? SentAt,
    DateTimeOffset ReceivedAt, string Body, SendStatus Status, string? Error);

/// <summary>Origin is "google-import" or "manual".</summary>
internal sealed record ContactRow(long Id, string DisplayName, string Origin, IReadOnlyList<string> NumberKeys);

internal sealed class SmsDatabase : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS messages(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            sender TEXT NOT NULL,
            sender_key TEXT NOT NULL,
            sent_at TEXT NULL,
            received_at TEXT NOT NULL,
            body TEXT NOT NULL,
            source TEXT NOT NULL,
            direction TEXT NOT NULL DEFAULT 'In',
            status TEXT NOT NULL DEFAULT 'None',
            error TEXT NULL);
        CREATE TABLE IF NOT EXISTS message_parts(
            pdu_hash TEXT PRIMARY KEY,
            message_id INTEGER NULL);
        CREATE INDEX IF NOT EXISTS ix_message_parts_message_id ON message_parts(message_id);
        CREATE TABLE IF NOT EXISTS contacts(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            display_name TEXT NOT NULL,
            origin TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS contact_numbers(
            contact_id INTEGER NOT NULL,
            number_key TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_contact_numbers ON contact_numbers(contact_id);
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
        """;

    /// <summary>PRAGMA user_version of the schema above. Databases from before versioning are 0 and are upgraded.</summary>
    internal const int SchemaVersion = 1;

    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public SmsDatabase(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Run("PRAGMA journal_mode=WAL;", null);
        Run(Schema, null);
        Upgrade();
        FixImportedReceivedTimes();
    }

    /// <summary>
    /// Brings an older schema up to <see cref="SchemaVersion"/>. Each step is idempotent, so an interrupted upgrade
    /// simply runs again. Version 1: the message_parts(message_id) index (also created by <see cref="Schema"/>).
    /// </summary>
    private void Upgrade()
    {
        long version;
        using (SqliteCommand v = Cmd("PRAGMA user_version", null))
        {
            version = Convert.ToInt64(v.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (version >= SchemaVersion) return;
        using SqliteTransaction tx = _db.BeginTransaction();
        Run("CREATE INDEX IF NOT EXISTS ix_message_parts_message_id ON message_parts(message_id)", tx);
        Run($"PRAGMA user_version={SchemaVersion}", tx);
        tx.Commit();
    }

    /// <summary>
    /// One-time repair: older builds stored the migration time as received_at of messages imported from messages.txt,
    /// which put them out of order. Their message time is the right one.
    /// </summary>
    private void FixImportedReceivedTimes()
    {
        using SqliteTransaction tx = _db.BeginTransaction();
        using (SqliteCommand m = Cmd("SELECT 1 FROM meta WHERE key='fix-import-received-at'", tx))
        {
            if (m.ExecuteScalar() is not null) return;
        }

        Run("UPDATE messages SET received_at = sent_at WHERE source='import-txt' AND sent_at IS NOT NULL", tx);
        Run("INSERT INTO meta(key,value) VALUES('fix-import-received-at','1')", tx);
        tx.Commit();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }

        SqliteConnection.ClearAllPools();
    }

    public bool IsHandled(string pduHash)
    {
        lock (_gate)
        {
            return HashExists(pduHash, null);
        }
    }

    /// <summary>True when the PDU was saved as part of a message row (not undecodable or handled-only).</summary>
    public bool HasMessageRow(string pduHash)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Cmd("SELECT 1 FROM message_parts WHERE pdu_hash=$h AND message_id IS NOT NULL", null);
            cmd.Parameters.AddWithValue("$h", pduHash);
            return cmd.ExecuteScalar() is not null;
        }
    }

    /// <summary>Saves the message and its PDU hashes in one transaction; null if any hash was already known.</summary>
    public long? SaveIncoming(ReceivedSms msg, IEnumerable<string> pduHashes) =>
        SaveCore(msg, pduHashes.Distinct().ToList(), "modem", DateTimeOffset.Now);

    private long? SaveCore(ReceivedSms msg, List<string> hashes, string source, DateTimeOffset receivedAt)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            if (hashes.Any(h => HashExists(h, tx))) return null; // transaction rolls back on dispose

            long id;
            using (SqliteCommand ins = Cmd(
                "INSERT INTO messages(sender,sender_key,sent_at,received_at,body,source,direction,status) " +
                "VALUES($s,$k,$sent,$recv,$b,$src,'In','None'); SELECT last_insert_rowid();", tx))
            {
                ins.Parameters.AddWithValue("$s", msg.From);
                ins.Parameters.AddWithValue("$k", PhoneNumbers.Key(msg.From));
                ins.Parameters.AddWithValue("$sent", msg.Timestamp is { } t ? Fmt(t) : DBNull.Value);
                ins.Parameters.AddWithValue("$recv", Fmt(receivedAt));
                ins.Parameters.AddWithValue("$b", msg.Text);
                ins.Parameters.AddWithValue("$src", source);
                id = (long)ins.ExecuteScalar()!;
            }

            foreach (string h in hashes)
            {
                using SqliteCommand part = Cmd("INSERT INTO message_parts(pdu_hash,message_id) VALUES($h,$id)", tx);
                part.Parameters.AddWithValue("$h", h);
                part.Parameters.AddWithValue("$id", id);
                part.ExecuteNonQuery();
            }

            tx.Commit();
            return id;
        }
    }

    /// <summary>Records PDUs as handled without a message row (undecodable or legacy ones).</summary>
    public void MarkHandledOnly(IEnumerable<string> pduHashes)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            foreach (string h in pduHashes)
            {
                using SqliteCommand cmd = Cmd("INSERT OR IGNORE INTO message_parts(pdu_hash,message_id) VALUES($h,NULL)", tx);
                cmd.Parameters.AddWithValue("$h", h);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public long AddOutgoing(string to, string body)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Cmd(
                "INSERT INTO messages(sender,sender_key,sent_at,received_at,body,source,direction,status) " +
                "VALUES($s,$k,NULL,$recv,$b,'app','Out','Sending'); SELECT last_insert_rowid();", null);
            cmd.Parameters.AddWithValue("$s", to);
            cmd.Parameters.AddWithValue("$k", PhoneNumbers.Key(to));
            cmd.Parameters.AddWithValue("$recv", Fmt(DateTimeOffset.Now));
            cmd.Parameters.AddWithValue("$b", body);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void SetStatus(long id, SendStatus s, string? error)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Cmd(
                "UPDATE messages SET status=$st, error=$e, sent_at=CASE WHEN $st='Sent' THEN $now ELSE sent_at END WHERE id=$id", null);
            cmd.Parameters.AddWithValue("$st", s.ToString());
            cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", Fmt(DateTimeOffset.Now));
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Marks every message still "Sending" as failed; call at startup (a previous run was interrupted).</summary>
    public int FailStaleSending()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Cmd("UPDATE messages SET status='Failed', error='interrupted' WHERE status='Sending'", null);
            return cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<MessageRow> AllMessages()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = Cmd(
                "SELECT id,direction,sender,sender_key,sent_at,received_at,body,status,error FROM messages ORDER BY id", null);
            using SqliteDataReader r = cmd.ExecuteReader();
            var list = new List<MessageRow>();
            while (r.Read())
            {
                list.Add(new MessageRow(
                    r.GetInt64(0), Enum.Parse<Direction>(r.GetString(1)), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : ParseTime(r.GetString(4)), ParseTime(r.GetString(5)), r.GetString(6),
                    Enum.Parse<SendStatus>(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8)));
            }

            return list;
        }
    }

    /// <summary>Deletes the rows but keeps their message_parts (message_id NULL) so the modem copy stays handled.</summary>
    public void DeleteMessages(IEnumerable<long> ids)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            foreach (long id in ids)
            {
                using (SqliteCommand parts = Cmd("UPDATE message_parts SET message_id=NULL WHERE message_id=$id", tx))
                {
                    parts.Parameters.AddWithValue("$id", id);
                    parts.ExecuteNonQuery();
                }

                using SqliteCommand del = Cmd("DELETE FROM messages WHERE id=$id", tx);
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<ContactRow> Contacts()
    {
        lock (_gate)
        {
            var numbers = new Dictionary<long, List<string>>();
            using (SqliteCommand n = Cmd("SELECT contact_id, number_key FROM contact_numbers ORDER BY rowid", null))
            using (SqliteDataReader r = n.ExecuteReader())
            {
                while (r.Read())
                {
                    long cid = r.GetInt64(0);
                    if (!numbers.TryGetValue(cid, out var l)) numbers[cid] = l = [];
                    l.Add(r.GetString(1));
                }
            }

            using SqliteCommand cmd = Cmd("SELECT id, display_name, origin FROM contacts ORDER BY id", null);
            using SqliteDataReader c = cmd.ExecuteReader();
            var list = new List<ContactRow>();
            while (c.Read())
            {
                long id = c.GetInt64(0);
                list.Add(new ContactRow(id, c.GetString(1), c.GetString(2),
                    numbers.TryGetValue(id, out var l2) ? l2 : []));
            }

            return list;
        }
    }

    /// <summary>
    /// Replaces all google-import contacts; manual contacts are never touched. Callers check
    /// <see cref="ContactEdits.ImportRefusal"/> first, so an empty file never wipes the imported contacts.
    /// </summary>
    public void ReplaceGoogleContacts(IEnumerable<ImportedContact> contacts)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            Run("DELETE FROM contact_numbers WHERE contact_id IN (SELECT id FROM contacts WHERE origin='google-import')", tx);
            Run("DELETE FROM contacts WHERE origin='google-import'", tx);
            foreach (ImportedContact c in contacts)
            {
                InsertContact(c.Name, "google-import", c.NumberKeys, tx);
            }

            tx.Commit();
        }
    }

    public long SaveManualContact(string name, IEnumerable<string> numberKeys)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            long id = InsertContact(name, "manual", numberKeys, tx);
            tx.Commit();
            return id;
        }
    }

    public void UpdateManualContact(long id, string name, IEnumerable<string> numberKeys)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _db.BeginTransaction();
            using (SqliteCommand u = Cmd("UPDATE contacts SET display_name=$n WHERE id=$id AND origin='manual'", tx))
            {
                u.Parameters.AddWithValue("$n", name);
                u.Parameters.AddWithValue("$id", id);
                if (u.ExecuteNonQuery() == 0) return; // not a manual contact: leave untouched
            }

            using (SqliteCommand d = Cmd("DELETE FROM contact_numbers WHERE contact_id=$id", tx))
            {
                d.Parameters.AddWithValue("$id", id);
                d.ExecuteNonQuery();
            }

            AddNumbers(id, numberKeys, tx);
            tx.Commit();
        }
    }

    /// <summary>
    /// One-time import of v0.1.0 state.json and messages.txt. Leaves both files untouched. A file that cannot be read
    /// now (locked, no access) is logged and skipped without marking the import done, so the next start tries again;
    /// the import is idempotent (known hashes are skipped).
    /// </summary>
    public void MigrateLegacy(string dataDir)
    {
        lock (_gate)
        {
            using (SqliteCommand m = Cmd("SELECT 1 FROM meta WHERE key='legacy-migrated'", null))
            {
                if (m.ExecuteScalar() is not null) return;
            }

            bool complete = true;
            string state = Path.Combine(dataDir, "state.json");
            if (File.Exists(state))
            {
                try
                {
                    var hashes = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(state));
                    if (hashes is not null) MarkHandledOnly(hashes.Keys);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    complete = false;
                    Log.Write("Could not read legacy state.json, will retry at the next start: " + ex.Message);
                }
                catch (JsonException ex)
                {
                    Log.Write("Legacy state.json is not valid, skipped: " + ex.Message); // retrying would not help
                }
            }

            string txt = Path.Combine(dataDir, "messages.txt");
            if (File.Exists(txt))
            {
                string? content = null;
                try
                {
                    content = File.ReadAllText(txt, Encoding.UTF8);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    complete = false;
                    Log.Write("Could not read legacy messages.txt, will retry at the next start: " + ex.Message);
                }

                foreach (ReceivedSms msg in content is null ? [] : MessagesTxtParser.Parse(content))
                {
                    string time = msg.Timestamp is { } t ? Fmt(t) : "";
                    string hash = "txt:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{time}|{msg.From}|{msg.Text}")));
                    SaveCore(msg, [hash], "import-txt", msg.Timestamp ?? DateTimeOffset.Now); // keeps the threads in message order
                }
            }

            if (complete) Run("INSERT OR REPLACE INTO meta(key,value) VALUES('legacy-migrated','1')", null);
        }
    }

    private bool HashExists(string hash, SqliteTransaction? tx)
    {
        using SqliteCommand cmd = Cmd("SELECT 1 FROM message_parts WHERE pdu_hash=$h", tx);
        cmd.Parameters.AddWithValue("$h", hash);
        return cmd.ExecuteScalar() is not null;
    }

    private long InsertContact(string name, string origin, IEnumerable<string> keys, SqliteTransaction tx)
    {
        long id;
        using (SqliteCommand c = Cmd("INSERT INTO contacts(display_name,origin) VALUES($n,$o); SELECT last_insert_rowid();", tx))
        {
            c.Parameters.AddWithValue("$n", name);
            c.Parameters.AddWithValue("$o", origin);
            id = (long)c.ExecuteScalar()!;
        }

        AddNumbers(id, keys, tx);
        return id;
    }

    private void AddNumbers(long id, IEnumerable<string> keys, SqliteTransaction tx)
    {
        foreach (string k in keys.Distinct())
        {
            using SqliteCommand n = Cmd("INSERT INTO contact_numbers(contact_id,number_key) VALUES($id,$k)", tx);
            n.Parameters.AddWithValue("$id", id);
            n.Parameters.AddWithValue("$k", k);
            n.ExecuteNonQuery();
        }
    }

    private SqliteCommand Cmd(string sql, SqliteTransaction? tx)
    {
        SqliteCommand cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private void Run(string sql, SqliteTransaction? tx)
    {
        using SqliteCommand cmd = Cmd(sql, tx);
        cmd.ExecuteNonQuery();
    }

    private static string Fmt(DateTimeOffset t) => t.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
}
