using System.Text;

namespace Fm350Sms;

/// <summary>GSM 03.38 7-bit default alphabet (basic + extension tables) with septet packing helpers.</summary>
internal static class Gsm7
{
    private const string Basic =
        "@£$¥èéùìòÇ\nØø\rÅå" +
        "Δ_ΦΓΛΩΠΨΣΘΞ\u001bÆæßÉ" +
        " !\"#¤%&'()*+,-./0123456789:;<=>?" +
        "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§" +
        "¿abcdefghijklmnopqrstuvwxyzäöñüà";

    private static readonly Dictionary<int, char> Extension = new()
    {
        [10] = '\f', [20] = '^', [40] = '{', [41] = '}', [47] = '\\',
        [60] = '[', [61] = '~', [62] = ']', [64] = '|', [101] = '€',
    };

    private static readonly Dictionary<char, int> ExtensionReverse = Extension.ToDictionary(p => p.Value, p => p.Key);

    internal const int Escape = 0x1B;

    /// <summary>Maps a character to its septet; for extension characters <paramref name="septet"/> is the code after the escape.</summary>
    internal static bool TryEncode(char c, out int septet, out bool extended)
    {
        extended = false;
        if (c != '\u001b')
        {
            int index = Basic.IndexOf(c);
            if (index >= 0)
            {
                septet = index;
                return true;
            }
        }

        if (ExtensionReverse.TryGetValue(c, out septet))
        {
            extended = true;
            return true;
        }

        septet = 0;
        return false;
    }

    internal static string Unpack(byte[] b, int start, int septets, int skipSeptets)
    {
        var sb = new StringBuilder();
        bool escape = false;
        for (int n = skipSeptets; n < septets; n++)
        {
            int bit = n * 7, index = start + bit / 8, shift = bit % 8;
            if (index >= b.Length)
            {
                break;
            }

            int value = b[index] >> shift;
            if (shift > 1 && index + 1 < b.Length)
            {
                value |= b[index + 1] << (8 - shift);
            }

            value &= 0x7F;
            if (escape)
            {
                sb.Append(Extension.TryGetValue(value, out char c) ? c : ' ');
                escape = false;
            }
            else if (value == Escape)
            {
                escape = true;
            }
            else
            {
                sb.Append(Basic[value]);
            }
        }

        return sb.ToString();
    }

    /// <summary>Packs septets LSB-first, starting <paramref name="fillBits"/> bits into the first octet.</summary>
    internal static byte[] Pack(IReadOnlyList<int> septets, int fillBits)
    {
        var result = new byte[(fillBits + septets.Count * 7 + 7) / 8];
        int bit = fillBits;
        foreach (int s in septets)
        {
            for (int k = 0; k < 7; k++, bit++)
            {
                if ((s >> k & 1) != 0)
                {
                    result[bit / 8] |= (byte)(1 << (bit % 8));
                }
            }
        }

        return result;
    }
}
