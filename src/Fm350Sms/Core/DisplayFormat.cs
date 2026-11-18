namespace Fm350Sms;

/// <summary>Hebrew time labels shown in the window.</summary>
internal static class DisplayFormat
{
    /// <summary>Thread list: "HH:mm" today, "אתמול" yesterday, "dd/MM" this year, else "dd/MM/yy".</summary>
    internal static string ThreadTime(DateTimeOffset time, DateTime now)
    {
        DateTime local = time.ToLocalTime().DateTime;
        if (local.Date == now.Date) return local.ToString("HH:mm");
        if (local.Date == now.Date.AddDays(-1)) return "אתמול";
        return local.Year == now.Year ? local.ToString("dd/MM") : local.ToString("dd/MM/yy");
    }

    /// <summary>Message bubble: "dd/MM HH:mm", with the year when it is not this year.</summary>
    internal static string MessageTime(DateTimeOffset time, DateTime now)
    {
        DateTime local = time.ToLocalTime().DateTime;
        return local.ToString(local.Year == now.Year ? "dd/MM HH:mm" : "dd/MM/yyyy HH:mm");
    }

    /// <summary>First line of the body, shortened for the thread list.</summary>
    internal static string Preview(string body, int max = 60)
    {
        string line = body.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }

    /// <summary>
    /// True when the text should be laid out right-to-left: its first strong character is Hebrew or Arabic.
    /// Numbers, Latin text and text without letters are left-to-right, so "050-555-0123" and "+972 ..." keep their order.
    /// </summary>
    internal static bool IsRightToLeft(string text)
    {
        foreach (char c in text)
        {
            if ((int)c is >= 0x0590 and <= 0x08FF or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFC) return true; // Hebrew, Arabic
            if (char.IsLetter(c)) return false;
        }

        return false;
    }
}
