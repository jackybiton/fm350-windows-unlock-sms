namespace Fm350Sms;

internal static class PhoneNumbersTests
{
    internal static void Tests()
    {
        var keys = new (string Raw, string Expected)[]
        {
            ("050-555-0123", "+972505550123"),
            ("0505550123", "+972505550123"),
            ("+972 50-555-0123", "+972505550123"),
            ("00972505550123", "+972505550123"),
            ("*123", "*123"),
            ("Partner", "Partner"),
        };
        foreach (var (raw, expected) in keys)
        {
            string got = PhoneNumbers.Key(raw);
            SelfTest.Check($"phone key {raw}", got == expected, got);
        }

        SelfTest.Check("phone match +972 vs local 9 digits", PhoneNumbers.Matches("+972505550123", "505550123"));
        SelfTest.Check("phone no match *123 vs 123", !PhoneNumbers.Matches("*123", "123"));
        SelfTest.Check("phone no match alpha vs number", !PhoneNumbers.Matches("Partner", "+972501234567"));
        SelfTest.Check("phone no match short vs long", !PhoneNumbers.Matches("123", "+972500000123"));
        SelfTest.Check("phone display", PhoneNumbers.Display("+972505550123") == "050-555-0123", PhoneNumbers.Display("+972505550123"));
    }
}
