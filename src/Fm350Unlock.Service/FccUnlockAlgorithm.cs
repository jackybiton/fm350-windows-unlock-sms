using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Fm350Unlock.Service;

internal static class FccUnlockAlgorithm
{
    private const string VendorSeed = "DW5931EFCCLOCK";

    public static uint CreateResponse(string challengeHex)
    {
        if (challengeHex.Length != 8 || !challengeHex.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "The FCC challenge must contain exactly eight hexadecimal digits.",
                nameof(challengeHex));
        }

        byte[] challenge = Convert.FromHexString(challengeHex);
        byte[] response = CreateResponseBytes(challenge);
        return BinaryPrimitives.ReadUInt32BigEndian(response);
    }

    public static byte[] CreateResponseBytes(ReadOnlySpan<byte> challenge)
    {
        if (challenge.Length != 4)
        {
            throw new ArgumentException(
                "The FCC challenge must contain exactly four bytes.",
                nameof(challenge));
        }

        byte[] vendorDigest = SHA256.HashData(Encoding.ASCII.GetBytes(VendorSeed));
        Span<byte> combined = stackalloc byte[8];
        challenge.CopyTo(combined);
        vendorDigest.AsSpan(0, 4).CopyTo(combined[4..]);
        byte[] responseDigest = SHA256.HashData(combined);
        return responseDigest[..4];
    }

    public static void SelfTest()
    {
        const uint expected = 771_512_217;
        uint actual = CreateResponse("c53900f7");
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Algorithm self-test failed: expected {expected}, got {actual}.");
        }

        byte[] responseBytes = CreateResponseBytes(Convert.FromHexString("c53900f7"));
        if (!responseBytes.SequenceEqual(Convert.FromHexString("2dfc5799")))
        {
            throw new InvalidOperationException("Algorithm byte-order self-test failed.");
        }
    }
}
