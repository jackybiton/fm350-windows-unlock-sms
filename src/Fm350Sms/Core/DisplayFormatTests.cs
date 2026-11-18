namespace Fm350Sms;

internal static class DisplayFormatTests
{
    internal static void Tests()
    {
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Local);
        DateTimeOffset At(int y, int mo, int d, int h, int mi) => new(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Local));

        SelfTest.Check("time today", DisplayFormat.ThreadTime(At(2026, 11, 18, 4, 39), now) == "04:39");
        SelfTest.Check("time yesterday", DisplayFormat.ThreadTime(At(2026, 11, 17, 23, 5), now) == "אתמול");
        SelfTest.Check("time this year", DisplayFormat.ThreadTime(At(2026, 11, 15, 8, 0), now) == "15/11");
        SelfTest.Check("time other year", DisplayFormat.ThreadTime(At(2025, 1, 2, 8, 0), now) == "02/01/25");
        SelfTest.Check("message time", DisplayFormat.MessageTime(At(2026, 11, 18, 4, 39), now) == "18/11 04:39");
        SelfTest.Check("message time other year", DisplayFormat.MessageTime(At(2025, 1, 2, 8, 0), now) == "02/01/2025 08:00");
        SelfTest.Check("preview one line", DisplayFormat.Preview("שלום\r\nעולם") == "שלום עולם");
        SelfTest.Check("direction hebrew", DisplayFormat.IsRightToLeft("100 ש\"ח, OK"));
        SelfTest.Check("direction latin", !DisplayFormat.IsRightToLeft("Partner: החשבונית"));
        SelfTest.Check("direction number", !DisplayFormat.IsRightToLeft("+972 50-555-0123") && !DisplayFormat.IsRightToLeft(""));
        SelfTest.Check("preview shortened", DisplayFormat.Preview(new string('א', 70), 10) == new string('א', 10) + "…");
    }
}
