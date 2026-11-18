using System.Text.RegularExpressions;

namespace Fm350Sms;

/// <summary>A web link found in a message. <see cref="Host"/> is the ASCII (punycode) host, shown so look-alikes stand out.</summary>
internal sealed record Link(Uri Uri, string Host);

/// <summary>Finds clickable web links in SMS text. Only http/https (and bare www.) are accepted.</summary>
internal static partial class Links
{
    private const string TrailingPunctuation = ".,;:!?'\"״׳…";

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Candidate();

    public static IReadOnlyList<Link> Find(string text)
    {
        var result = new List<Link>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Candidate().Matches(text ?? string.Empty))
        {
            string raw = TrimEnd(m.Value);
            if (raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                raw = "https://" + raw;
            }

            if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.UserInfo.Length > 0          // https://bank.example@evil.example hides the real host
                || string.IsNullOrEmpty(uri.Host))
            {
                continue;
            }

            if (seen.Add(uri.AbsoluteUri))
            {
                result.Add(new Link(uri, uri.IdnHost));
            }
        }

        return result;
    }

    private static string TrimEnd(string s)
    {
        while (s.Length > 0)
        {
            char last = s[^1];
            if (TrailingPunctuation.Contains(last))
            {
                s = s[..^1];
            }
            else if (IsUnbalancedClose(s, last))
            {
                s = s[..^1];
            }
            else
            {
                break;
            }
        }

        return s;
    }

    // A closing bracket is part of the link only if the link also contains its opening bracket (e.g. wiki URLs).
    private static bool IsUnbalancedClose(string s, char last)
    {
        char open = last switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
        return open != '\0' && s.Count(c => c == open) < s.Count(c => c == last);
    }
}
