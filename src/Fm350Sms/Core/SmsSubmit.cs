using System.Text;

namespace Fm350Sms;

/// <param name="Hex">PDU hex including the leading 00 (use the SIM's SMSC).</param>
/// <param name="TpduLength">Octet count excluding the SMSC octet; the size argument of SmsSendPdu.</param>
internal sealed record SubmitPdu(string Hex, byte TpduLength);

/// <summary>Encodes GSM 03.40 SMS-SUBMIT PDUs (GSM 7-bit or UCS2, with 8-bit concatenation).</summary>
internal static class SmsSubmit
{
    /// <summary>Longest message the app sends, in parts (the UI refuses longer text, <see cref="Encode"/> throws).</summary>
    internal const int MaxParts = 10;

    private const int GsmSingle = 160, GsmPart = 153, UcsSingle = 70, UcsPart = 67;

    internal static bool FitsGsm7(string text) => text.All(c => Gsm7.TryEncode(c, out _, out _));

    internal static (int Parts, int CharsLeft) Measure(string text)
    {
        if (FitsGsm7(text))
        {
            List<List<int>> chunks = SplitGsm(text);
            int last = chunks[^1].Count;
            return chunks.Count == 1 ? (1, GsmSingle - last) : (chunks.Count, GsmPart - last);
        }

        List<string> ucs = SplitUcs2(text);
        int lastLen = ucs[^1].Length;
        return ucs.Count == 1 ? (1, UcsSingle - lastLen) : (ucs.Count, UcsPart - lastLen);
    }

    internal static IReadOnlyList<SubmitPdu> Encode(string destination, string text, byte reference)
    {
        string key = PhoneNumbers.Key(destination);
        if (!PhoneNumbers.IsPhone(key))
        {
            throw new ArgumentException($"Not a phone number: {destination}", nameof(destination));
        }

        if (Measure(text).Parts > MaxParts)
        {
            throw new ArgumentException($"Message too long: more than {MaxParts} parts.", nameof(text));
        }

        byte[] address = EncodeAddress(key);
        var pdus = new List<SubmitPdu>();
        if (FitsGsm7(text))
        {
            List<List<int>> chunks = SplitGsm(text);
            for (int i = 0; i < chunks.Count; i++)
            {
                bool multi = chunks.Count > 1;
                byte[] header = multi ? Udh(reference, chunks.Count, i + 1) : [];
                byte[] packed = Gsm7.Pack(chunks[i], multi ? 1 : 0);
                int udl = chunks[i].Count + (multi ? 7 : 0);
                pdus.Add(Build(address, 0x00, multi, udl, [.. header, .. packed]));
            }
        }
        else
        {
            List<string> chunks = SplitUcs2(text);
            for (int i = 0; i < chunks.Count; i++)
            {
                bool multi = chunks.Count > 1;
                byte[] header = multi ? Udh(reference, chunks.Count, i + 1) : [];
                byte[] data = [.. header, .. Encoding.BigEndianUnicode.GetBytes(chunks[i])];
                pdus.Add(Build(address, 0x08, multi, data.Length, data));
            }
        }

        return pdus;
    }

    private static byte[] Udh(byte reference, int total, int seq) => [0x05, 0x00, 0x03, reference, (byte)total, (byte)seq];

    private static SubmitPdu Build(byte[] address, byte dcs, bool hasUdh, int udl, byte[] userData)
    {
        var pdu = new List<byte> { 0x00, (byte)(hasUdh ? 0x51 : 0x11), 0x00 };
        pdu.AddRange(address);
        pdu.AddRange([0x00, dcs, 0xAA, (byte)udl]);
        pdu.AddRange(userData);
        return new SubmitPdu(Convert.ToHexString(pdu.ToArray()), checked((byte)(pdu.Count - 1)));
    }

    private static byte[] EncodeAddress(string key)
    {
        bool international = key.StartsWith('+');
        string digits = key.TrimStart('+');
        if (digits.Length == 0)
        {
            throw new ArgumentException("Empty destination.");
        }

        var bytes = new byte[(digits.Length + 1) / 2];
        for (int i = 0; i < digits.Length; i += 2)
        {
            int low = digits[i] - '0';
            int high = i + 1 < digits.Length ? digits[i + 1] - '0' : 0x0F;
            bytes[i / 2] = (byte)(high << 4 | low);
        }

        return [(byte)digits.Length, (byte)(international ? 0x91 : 0x81), .. bytes];
    }

    /// <summary>Septets (escape + code for extension chars) grouped to fit a single message or each part, never splitting an extension pair.</summary>
    private static List<List<int>> SplitGsm(string text)
    {
        var all = new List<List<int>>();
        foreach (char c in text)
        {
            Gsm7.TryEncode(c, out int septet, out bool extended);
            all.Add(extended ? [Gsm7.Escape, septet] : [septet]);
        }

        int total = all.Sum(g => g.Count);
        int limit = total <= GsmSingle ? GsmSingle : GsmPart;
        var chunks = new List<List<int>> { new() };
        foreach (List<int> g in all)
        {
            if (chunks[^1].Count + g.Count > limit)
            {
                chunks.Add([]);
            }

            chunks[^1].AddRange(g);
        }

        return chunks;
    }

    /// <summary>UTF-16 code units, never splitting a surrogate pair.</summary>
    private static List<string> SplitUcs2(string text)
    {
        if (text.Length <= UcsSingle)
        {
            return [text];
        }

        var chunks = new List<string>();
        for (int i = 0; i < text.Length;)
        {
            int len = Math.Min(UcsPart, text.Length - i);
            if (len == UcsPart && i + len < text.Length && char.IsHighSurrogate(text[i + len - 1]))
            {
                len--;
            }

            chunks.Add(text.Substring(i, len));
            i += len;
        }

        return chunks;
    }
}
