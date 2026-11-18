using System.Text;

namespace Fm350Sms;

internal sealed record SmsPart(
    string From,
    DateTimeOffset? Timestamp,
    string Text,
    int Reference,
    int PartCount,
    int PartNumber);

/// <summary>Decodes GSM 03.40 SMS-DELIVER PDUs (GSM 7-bit, 8-bit and UCS2, with concatenation headers).</summary>
internal static class SmsPdu
{
    public static SmsPart Decode(string hexPdu)
    {
        byte[] b = Convert.FromHexString(hexPdu.Trim());
        int i = 1 + b[0]; // skip SMSC
        int firstOctet = b[i++];
        if ((firstOctet & 0x03) != 0)
        {
            throw new FormatException($"Not an SMS-DELIVER PDU (MTI {firstOctet & 0x03}).");
        }

        bool hasUserDataHeader = (firstOctet & 0x40) != 0;

        int addressDigits = b[i++];
        int typeOfAddress = b[i++];
        int addressBytes = (addressDigits + 1) / 2;
        string from = (typeOfAddress & 0x70) == 0x50
            ? Gsm7.Unpack(b, i, addressDigits * 4 / 7, 0)
            : DecodeSemiOctets(b, i, addressBytes, (typeOfAddress & 0x70) == 0x10);
        i += addressBytes;

        i++; // protocol identifier
        int dataCoding = b[i++];
        DateTimeOffset? timestamp = DecodeTimestamp(b, i);
        i += 7;

        int userDataLength = b[i++];
        int userDataStart = i;
        int headerLength = 0, reference = 0, partCount = 1, partNumber = 1;
        if (hasUserDataHeader)
        {
            int udhl = b[i];
            headerLength = udhl + 1;
            for (int k = i + 1; k + 1 < i + 1 + udhl;)
            {
                int iei = b[k], len = b[k + 1];
                if (iei == 0x00 && len == 3)
                {
                    reference = b[k + 2]; partCount = b[k + 3]; partNumber = b[k + 4];
                }
                else if (iei == 0x08 && len == 4)
                {
                    reference = (b[k + 2] << 8) | b[k + 3]; partCount = b[k + 4]; partNumber = b[k + 5];
                }

                k += 2 + len;
            }
        }

        int alphabet = (dataCoding & 0xC0) == 0x00 ? (dataCoding >> 2) & 0x03
            : (dataCoding & 0xF0) == 0xF0 ? (dataCoding >> 2) & 0x01
            : (dataCoding & 0xF0) == 0xE0 ? 2
            : 0;

        string text = alphabet switch
        {
            2 => Encoding.BigEndianUnicode.GetString(b, userDataStart + headerLength, userDataLength - headerLength),
            1 => Encoding.Latin1.GetString(b, userDataStart + headerLength, userDataLength - headerLength),
            _ => Gsm7.Unpack(b, userDataStart, userDataLength, headerLength == 0 ? 0 : (headerLength * 8 + 6) / 7),
        };

        return new SmsPart(from, timestamp, text, reference, Math.Max(partCount, 1), Math.Max(partNumber, 1));
    }

    private static string DecodeSemiOctets(byte[] b, int start, int count, bool international)
    {
        var sb = new StringBuilder(international ? "+" : string.Empty);
        for (int k = 0; k < count; k++)
        {
            sb.Append((char)('0' + (b[start + k] & 0x0F)));
            int high = b[start + k] >> 4;
            if (high != 0x0F)
            {
                sb.Append((char)('0' + high));
            }
        }

        return sb.ToString();
    }

    private static DateTimeOffset? DecodeTimestamp(byte[] b, int i)
    {
        int Digit(int k) => (b[i + k] & 0x0F) * 10 + (b[i + k] >> 4);
        try
        {
            int quarterHours = (b[i + 6] & 0x07) * 10 + (b[i + 6] >> 4);
            if ((b[i + 6] & 0x08) != 0)
            {
                quarterHours = -quarterHours;
            }

            return new DateTimeOffset(2000 + Digit(0), Digit(1), Digit(2), Digit(3), Digit(4), Digit(5),
                TimeSpan.FromMinutes(quarterHours * 15));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
