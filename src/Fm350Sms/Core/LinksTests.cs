namespace Fm350Sms;

internal static class LinksTests
{
    public static void Tests()
    {
        static string Urls(string text) => string.Join(" | ", Links.Find(text).Select(l => l.Uri.AbsoluteUri));
        static string Hosts(string text) => string.Join(" | ", Links.Find(text).Select(l => l.Host));

        SelfTest.Check("links none", Urls("שלום, מה נשמע?") == "");
        SelfTest.Check("links https", Urls("להצטרפות>> https://web.example.co.il/Webinar26") == "https://web.example.co.il/Webinar26");
        SelfTest.Check("links http", Urls("ראו http://example.com/a?b=1&c=2") == "http://example.com/a?b=1&c=2");
        SelfTest.Check("links www gets https", Urls("באתר www.example.com עכשיו") == "https://www.example.com/");
        SelfTest.Check("links trailing punctuation trimmed",
            Urls("היכנסו ל-https://example.com/x. תודה!") == "https://example.com/x"
            && Urls("(https://example.com/y)") == "https://example.com/y"
            && Urls("https://example.com/z, ועוד") == "https://example.com/z");
        SelfTest.Check("links several", Urls("a https://a.example b www.b.example") == "https://a.example/ | https://www.b.example/");
        SelfTest.Check("links duplicates once", Urls("https://a.example https://a.example") == "https://a.example/");
        SelfTest.Check("links other schemes ignored",
            Urls("file:///C:/x javascript:alert(1) ftp://a.example mailto:a@b.example") == "");
        SelfTest.Check("links userinfo rejected", Urls("https://bank.example@evil.example/login") == "");
        SelfTest.Check("links host shown", Hosts("https://web.example.co.il/Webinar26") == "web.example.co.il");
        SelfTest.Check("links punycode host shown", Hosts("https://xn--4dbrk0ce.example/") == "xn--4dbrk0ce.example");
        SelfTest.Check("links hebrew around", Urls("שלוםhttps://a.example/פ") == "https://a.example/%D7%A4");
        SelfTest.Check("links empty", Urls("") == "" && Urls("https://") == "");
    }
}
