using System.Text;

namespace Fm350Sms;

internal static class ContactFiles
{
    internal static IReadOnlyList<ImportedContact> Read(string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8); // detects BOMs
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".csv" => ReadGoogleCsv(text),
            ".vcf" => ReadVCard(text),
            _ => throw new NotSupportedException($"Unsupported contacts file type: {ext}"),
        };
    }

    // ---- Google CSV ----

    internal static IReadOnlyList<ImportedContact> ReadGoogleCsv(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        List<List<string>> rows = ParseCsv(text);
        var result = new List<ImportedContact>();
        if (rows.Count == 0) return result;

        List<string> header = rows[0].Select(h => h.Trim()).ToList();
        int Col(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        int[] nameCols = new[] { "First Name", "Given Name", "Middle Name", "Additional Name", "Last Name", "Family Name" }
            .Select(Col).Where(i => i >= 0).ToArray();
        int[] fallbackCols = new[] { "Name", "File As", "Nickname", "Organization Name" }
            .Select(Col).Where(i => i >= 0).ToArray();
        int[] phoneCols = header
            .Select((h, i) => (h, i))
            .Where(x => x.h.StartsWith("Phone ", StringComparison.OrdinalIgnoreCase) && x.h.EndsWith(" - Value", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.i).ToArray();

        static string Cell(List<string> row, int i) => i < row.Count ? row[i].Trim() : "";

        for (int r = 1; r < rows.Count; r++)
        {
            List<string> row = rows[r];
            var keys = new List<string>();
            foreach (int pc in phoneCols)
            {
                foreach (string part in Cell(row, pc).Split(" ::: ", StringSplitOptions.RemoveEmptyEntries))
                {
                    string p = part.Trim();
                    if (p.Length == 0) continue;
                    string key = PhoneNumbers.Key(p);
                    if (key.Length > 0 && !keys.Contains(key)) keys.Add(key);
                }
            }
            if (keys.Count == 0) continue;

            string name = string.Join(" ", nameCols.Select(i => Cell(row, i)).Where(s => s.Length > 0));
            if (name.Length == 0)
                name = fallbackCols.Select(i => Cell(row, i)).FirstOrDefault(s => s.Length > 0) ?? "";
            if (name.Length == 0) name = PhoneNumbers.Display(keys[0]);
            result.Add(new ImportedContact(name, keys));
        }
        return result;
    }

    /// <summary>RFC 4180: quoted fields, doubled quotes, embedded commas and newlines.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; any = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); any = true; break;
                case '\r': break;
                case '\n':
                    if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add(row); }
                    row = new List<string>(); field.Clear(); any = false;
                    break;
                default: field.Append(c); any = true; break;
            }
        }
        if (any || field.Length > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    internal static string WriteGoogleCsv(IEnumerable<ContactRow> contacts)
    {
        var list = contacts.ToList();
        int maxPhones = Math.Max(1, list.Count == 0 ? 1 : list.Max(c => c.NumberKeys.Count));
        var sb = new StringBuilder();
        var header = new List<string> { "First Name", "Last Name" };
        for (int n = 1; n <= maxPhones; n++) { header.Add($"Phone {n} - Label"); header.Add($"Phone {n} - Value"); }
        sb.Append(string.Join(",", header)).Append("\r\n");
        foreach (ContactRow c in list)
        {
            var fields = new List<string> { Quote(c.DisplayName), "" };
            for (int n = 0; n < maxPhones; n++)
            {
                if (n < c.NumberKeys.Count) { fields.Add("Mobile"); fields.Add(Quote(PhoneNumbers.Display(c.NumberKeys[n]))); }
                else { fields.Add(""); fields.Add(""); }
            }
            sb.Append(string.Join(",", fields)).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ---- vCard ----

    internal static IReadOnlyList<ImportedContact> ReadVCard(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var result = new List<ImportedContact>();
        string? fn = null, n = null;
        List<string>? keys = null;

        foreach (string line in UnfoldVCard(text))
        {
            if (line.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase)) { fn = null; n = null; keys = new List<string>(); continue; }
            if (line.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (keys is { Count: > 0 })
                {
                    string name = !string.IsNullOrWhiteSpace(fn) ? fn!.Trim() : !string.IsNullOrWhiteSpace(n) ? n!.Trim() : PhoneNumbers.Display(keys[0]);
                    result.Add(new ImportedContact(name, keys));
                }
                keys = null;
                continue;
            }
            if (keys is null) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string[] head = line[..colon].Split(';');
            string prop = head[0];
            int dot = prop.LastIndexOf('.');
            if (dot >= 0) prop = prop[(dot + 1)..];
            prop = prop.ToUpperInvariant();
            string value = line[(colon + 1)..];

            bool qp = head.Skip(1).Any(p => p.Trim().Equals("ENCODING=QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase));
            if (qp) value = DecodeQuotedPrintable(value);

            switch (prop)
            {
                case "FN": fn = value; break;
                case "N":
                    {
                        string[] p = value.Split(';'); // family;given;additional;prefix;suffix
                        string Get(int i) => i < p.Length ? p[i].Trim() : "";
                        n = string.Join(" ", new[] { Get(1), Get(2), Get(0) }.Where(s => s.Length > 0));
                        break;
                    }
                case "TEL":
                    {
                        string v = value.Trim();
                        if (v.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) v = v[4..];
                        if (v.Length == 0) break;
                        string key = PhoneNumbers.Key(v);
                        if (!keys.Contains(key)) keys.Add(key);
                        break;
                    }
            }
        }
        return result;
    }

    private static IEnumerable<string> UnfoldVCard(string text)
    {
        string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var cur = new StringBuilder();
        bool have = false;
        foreach (string l in raw)
        {
            bool softBreak = have && cur.Length > 0 && cur[^1] == '=' && IsQuotedPrintableLine(cur.ToString());
            if (have && (l.StartsWith(' ') || l.StartsWith('\t')))
            {
                cur.Append(l[1..]); // RFC folding
            }
            else if (softBreak)
            {
                cur.Length--; // QP soft line break
                cur.Append(l);
            }
            else
            {
                if (have) yield return cur.ToString();
                cur.Clear();
                cur.Append(l);
                have = true;
            }
        }
        if (have) yield return cur.ToString();
    }

    private static bool IsQuotedPrintableLine(string line)
    {
        int colon = line.IndexOf(':');
        return colon > 0 && line[..colon].Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeQuotedPrintable(string s)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
