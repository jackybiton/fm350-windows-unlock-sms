using System.Text;

namespace Fm350Sms;

internal static class SmsSubmitTests
{
    internal static void Tests()
    {
        var single = SmsSubmit.Encode("+46708251358", "hellohello", 0);
        SelfTest.Check("submit reference vector", single.Count == 1 && single[0].Hex == "0011000B916407281553F80000AA0AE8329BFD4697D9EC37",
            single.Count > 0 ? single[0].Hex : "none");
        SelfTest.Check("submit reference tpdu length", single.Count == 1 && single[0].TpduLength == 23);

        SelfTest.Check("measure 160 gsm", SmsSubmit.Measure(new string('a', 160)) == (1, 0));
        SelfTest.Check("measure 161 gsm", SmsSubmit.Measure(new string('a', 161)).Parts == 2);
        SelfTest.Check("measure 70 ucs2", SmsSubmit.Measure(new string('א', 70)) == (1, 0));
        SelfTest.Check("measure 71 ucs2", SmsSubmit.Measure(new string('א', 71)).Parts == 2);
        SelfTest.Check("measure euro", SmsSubmit.Measure("€").CharsLeft == 158, SmsSubmit.Measure("€").CharsLeft.ToString());
        SelfTest.Check("fits gsm7", SmsSubmit.FitsGsm7("abc€{") && !SmsSubmit.FitsGsm7("שלום"));

        var heb = SmsSubmit.Encode("0505550123", "שלום", 0);
        string hex = heb[0].Hex;
        // 00 | 11 00 | 0C 91 <digits> | 00 08 AA | 08 | UD
        SelfTest.Check("hebrew pdu", hex == "0011000C91795250551032" + "0008AA08" + "05E905DC05D505DD", hex);
        SelfTest.Check("hebrew tpdu length", heb[0].TpduLength == 22, heb[0].TpduLength.ToString());

        string text = string.Concat(Enumerable.Repeat("שלום עולם! ", 20))[..200];
        var parts = SmsSubmit.Encode("0505550123", text, 7);
        bool headers = parts.Count == 3;
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            byte[] b = Convert.FromHexString(parts[i].Hex);
            // 00, first, mr, addrLen, toa, 6 addr bytes, pid, dcs, vp, udl, udhl...
            int p = 1 + 1 + 1 + 1 + 1 + (b[3] + 1) / 2 + 3;
            int udl = b[p];
            headers &= b[0] == 0 && b[1] == 0x51 && udl == b.Length - p - 1
                && b[p + 1] == 5 && b[p + 2] == 0 && b[p + 3] == 3 && b[p + 4] == 7 && b[p + 5] == 3 && b[p + 6] == i + 1
                && parts[i].TpduLength == b.Length - 1;
            sb.Append(Encoding.BigEndianUnicode.GetString(b, p + 7, b.Length - p - 7));
        }

        SelfTest.Check("ucs2 multipart headers", headers);
        SelfTest.Check("ucs2 multipart roundtrip", sb.ToString() == text);

        var gsm = SmsSubmit.Encode("0505550123", new string('a', 161), 9);
        SelfTest.Check("gsm multipart count", gsm.Count == 2);
        var decoded = new StringBuilder();
        foreach (var part in gsm)
        {
            byte[] pb = Convert.FromHexString(part.Hex);
            int udlPos = 5 + (pb[3] + 1) / 2 + 3;
            decoded.Append(Gsm7.Unpack(pb, udlPos + 1, pb[udlPos], 7));
        }

        SelfTest.Check("submit 10 gsm parts allowed", SmsSubmit.Encode("0505550123", new string('a', 1530), 1).Count == 10);
        SelfTest.Check("submit 10 ucs2 parts allowed", SmsSubmit.Encode("0505550123", new string('א', 670), 1).Count == 10);
        bool Throws(string body)
        {
            try { SmsSubmit.Encode("0505550123", body, 1); return false; }
            catch (ArgumentException) { return true; }
        }

        SelfTest.Check("submit 11 gsm parts refused", Throws(new string('a', 1531)));
        SelfTest.Check("submit 11 ucs2 parts refused", Throws(new string('א', 671)));

        SelfTest.Check("gsm multipart roundtrip", decoded.ToString() == new string('a', 161), decoded.Length.ToString());
    }
}
