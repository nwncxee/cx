using System.Buffers.Binary;
using System.Numerics;

namespace CX.Framework;

/// <summary>SHA-256 (FIPS 180-4) in managed code. .NET hashes through OpenSSL on Linux and aborts the process
/// where no libssl loads, so a hash CX takes at boot goes through here.</summary>
public static class Sha256
{
    /// <summary>The round constants: the first 32 bits of the fractional parts of the cube roots of the first 64 primes.</summary>
    private static readonly uint[] K =
    [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, // not-an-offset: FIPS 180-4 constants
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174, // not-an-offset: FIPS 180-4 constants
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, // not-an-offset: FIPS 180-4 constants
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, // not-an-offset: FIPS 180-4 constants
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, // not-an-offset: FIPS 180-4 constants
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, // not-an-offset: FIPS 180-4 constants
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, // not-an-offset: FIPS 180-4 constants
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2, // not-an-offset: FIPS 180-4 constants
    ];

    /// <summary>The initial hash: the first 32 bits of the fractional parts of the square roots of the first 8 primes.</summary>
    private static readonly uint[] H0 =
        [0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]; // not-an-offset: FIPS 180-4 constants

    /// <summary>The 32-byte SHA-256 digest of <paramref name="data"/>.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        uint[] h = [.. H0];
        Span<uint> w = stackalloc uint[64];
        int tail = data.Length % 64;
        int padded = data.Length - tail + (tail < 56 ? 64 : 128);
        Span<byte> last = stackalloc byte[128];
        data[^tail..].CopyTo(last);
        last[tail] = 0x80; // not-an-offset: the padding's leading one bit
        BinaryPrimitives.WriteUInt64BigEndian(last.Slice(padded - data.Length + tail - 8, 8), (ulong)data.Length * 8);
        for (int at = 0; at < padded; at += 64)
        {
            ReadOnlySpan<byte> block = at + 64 <= data.Length - tail ? data.Slice(at, 64) : last.Slice(at - (data.Length - tail), 64);
            Compress(h, block, w);
        }
        var digest = new byte[32];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4, 4), h[i]);
        return digest;
    }

    /// <summary>Fold one 64-byte <paramref name="block"/> into <paramref name="h"/>, with <paramref name="w"/> as the message schedule.</summary>
    private static void Compress(uint[] h, ReadOnlySpan<byte> block, Span<uint> w)
    {
        for (int t = 0; t < 16; t++) w[t] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(t * 4, 4));
        for (int t = 16; t < 64; t++)
        {
            uint s0 = BitOperations.RotateRight(w[t - 15], 7) ^ BitOperations.RotateRight(w[t - 15], 18) ^ (w[t - 15] >> 3);
            uint s1 = BitOperations.RotateRight(w[t - 2], 17) ^ BitOperations.RotateRight(w[t - 2], 19) ^ (w[t - 2] >> 10);
            w[t] = w[t - 16] + s0 + w[t - 7] + s1;
        }
        uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
        for (int t = 0; t < 64; t++)
        {
            uint t1 = hh + (BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25))
                         + ((e & f) ^ (~e & g)) + K[t] + w[t];
            uint t2 = (BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22))
                    + ((a & b) ^ (a & c) ^ (b & c));
            hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }
}
