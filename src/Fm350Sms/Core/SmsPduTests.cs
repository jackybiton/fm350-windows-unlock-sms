namespace Fm350Sms;

internal static class SmsPduTests
{
    internal static void Tests()
    {
        var cases = new (string Pdu, string From, string Text)[]
        {
            // GSM 7-bit
            ("07917283010010F5040BC87238880900F10000993092516195800AE8329BFD4697D9EC37", "27838890001", "hellohello"),
            // UCS2 Hebrew
            ("0791893562000000040C918935624455660008621181213454800805E905D505DD0021", "+985326445566", "שום!"),
            // GSM 7-bit with 8-bit concatenation header, part 1 of 2 ("hi")
            ("00440B915121551532F400005220811203454009050003CC0201D069", "+15125551234", "hi"),
        };

        foreach (var (pdu, from, text) in cases)
        {
            SmsPart part = SmsPdu.Decode(pdu);
            SelfTest.Check($"pdu {part.From} {part.PartNumber}/{part.PartCount}",
                part.From == from && part.Text == text, part.Text);
        }
    }
}
