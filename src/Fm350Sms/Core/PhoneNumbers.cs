using System.Text.RegularExpressions;

namespace Fm350Sms;

internal static class PhoneNumbers
{
    private static readonly Regex PhoneRegex = new(@"^\+?\d+$", RegexOptions.Compiled);

    internal static string Key(string raw)
    {
        string t = raw.Trim();
        if (t.Any(char.IsLetter)) return t;

        t = t.Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace(".", "");
        if (t.StartsWith("00")) return "+" + t[2..];
        if (t.StartsWith('0') && t.Length > 1 && t[1] != '0') return "+972" + t[1..];
        return t;
    }

    internal static bool IsPhone(string key) => PhoneRegex.IsMatch(key);

    internal static bool Matches(string a, string b)
    {
        if (a == b) return true;
        if (!IsPhone(a) || !IsPhone(b)) return false;
        string da = a.TrimStart('+'), db = b.TrimStart('+');
        if (da.Length < 9 || db.Length < 9) return false;
        return da[^9..] == db[^9..];
    }

    internal static string Display(string key)
    {
        if (key.StartsWith("+972") && key.Length == 13 && IsPhone(key))
        {
            string n = key[4..];
            return $"0{n[..2]}-{n[2..5]}-{n[5..]}";
        }
        return key;
    }
}
