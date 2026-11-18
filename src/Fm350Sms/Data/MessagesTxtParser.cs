using System.Globalization;
using System.Text.RegularExpressions;

namespace Fm350Sms;

/// <summary>
/// Parses the v0.1.0 messages.txt log. v0.1.0 wrote each message as <c>[time] sender\r\n{Text}\r\n\r\n</c>, and the text
/// itself can hold blank lines; so a message starts only at a header line (<c>[yyyy-MM-dd HH:mm:ss] </c> or
/// <c>[unknown time] </c>) at the start of the file or right after a blank line. Anything else, a paragraph starting
/// with <c>[</c> included, belongs to the body of the message before it.
/// </summary>
internal static class MessagesTxtParser
{
    private static readonly Regex Header = new(
        @"^\[(?<time>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d|unknown time)\] (?<sender>.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<ReceivedSms> Parse(string text)
    {
        var result = new List<ReceivedSms>();
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        Match? header = null;
        var body = new List<string>();
        bool previousBlank = true; // the start of the file counts as following a blank line
        foreach (string line in lines)
        {
            Match m = previousBlank ? Header.Match(line) : Match.Empty;
            if (m.Success)
            {
                Add(result, header, body);
                header = m;
                body.Clear();
            }
            else if (header is not null)
            {
                body.Add(line);
            }

            previousBlank = line.Trim(' ', '\t').Length == 0;
        }

        Add(result, header, body);
        return result;
    }

    private static void Add(List<ReceivedSms> result, Match? header, List<string> body)
    {
        if (header is null) return;

        // Only the separator (and the file's last newline) goes: blank lines at the end; inner ones stay.
        int count = body.Count;
        while (count > 0 && body[count - 1].Trim(' ', '\t').Length == 0) count--;

        DateTimeOffset? time = null;
        if (DateTime.TryParseExact(header.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out DateTime dt))
        {
            time = new DateTimeOffset(dt);
        }

        result.Add(new ReceivedSms(header.Groups["sender"].Value.Trim(), time, string.Join("\n", body.Take(count)), []));
    }
}
