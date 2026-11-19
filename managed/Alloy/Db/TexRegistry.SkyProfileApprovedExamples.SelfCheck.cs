using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using CX.Framework;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy.Db;

/// <summary>The sky profile texture read byte for byte (approved-spec, E2-E5): a 64x4 data texture of float32s packed
/// into RGBA8 texels, decoded as a sky's texture0 is. Oracle: the texels written by hand here, which are the bytes a
/// stock client uploads.</summary>
internal static unsafe partial class TexRegistry
{
    private const int ProfileW = 64, ProfileH = 4;
    /// <summary>The <c>.txi</c> word that marks a texture as data.</summary>
    private const string DataTxi = "data 1\r\n";
    /// <summary>6360.0 and -0.105 as little-endian float32 bytes: 00 C0 C6 45 and 3D 0A D7 BD.</summary>
    private static readonly Rgba8 Value6360 = new(0, 192, 198, 69), ValueMinus0105 = new(61, 10, 215, 189);
    /// <summary>0.0 and 1e-8, whose top byte (00, and 32 hex = 50) falls at or under the cut-out's 51.</summary>
    private static readonly Rgba8 ValueZero = new(0, 0, 0, 0), ValueOneEMinus8 = new(119, 204, 43, 50);
    /// <summary>Two texels either side of the cut: alpha 51 is a hole to a cut-out, 52 is not.</summary>
    private static readonly Rgba8 AtCut = new(12, 34, 56, 51), PastCut = new(12, 34, 56, 52);

    internal static bool SkyProfileApprovedExamplesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TexRegistry.SkyProfileApprovedExamples.SelfCheck");
        Cx548E2(check);
        Cx548E3(check);
        Cx548E4(check);
        Cx548E5(check);
        log.Info(check.Ok ? "TexRegistry.SkyProfileApprovedExamples.SelfCheck: passed"
                          : "TexRegistry.SkyProfileApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given that texture holding 0.0 and 1e-8 (a top byte below 51) next to non-zero texels
    // When the twin reads them
    // Then both come back exact.
    private static void Cx548E2(Checker check)
    {
        Job j = SkyDecode(DataTxi, Tga(Profile(), ProfileW, ProfileH));
        try
        {
            if (!Decoded(check, "E2", j)) return;
            ReadOnlySpan<Rgba8> top = Level0(j);
            ExpectFloat(check, "E2", top[MipChain.Cell(ProfileW, 2, 0)], 0f);
            ExpectFloat(check, "E2", top[MipChain.Cell(ProfileW, 3, 0)], 1e-8f);
        }
        finally { FreeJob(j); }
    }

    // Given texels (12, 34, 56, 51) and (12, 34, 56, 52)
    // When the twin reads them
    // Then both come back unchanged.
    private static void Cx548E3(Checker check)
    {
        Job j = SkyDecode(DataTxi, Tga(Profile(), ProfileW, ProfileH));
        try
        {
            if (!Decoded(check, "E3", j)) return;
            ReadOnlySpan<Rgba8> top = Level0(j);
            Rgba8 atCut = top[MipChain.Cell(ProfileW, 0, 1)], pastCut = top[MipChain.Cell(ProfileW, 1, 1)];
            check.Expect(atCut == AtCut, $"E3: the texel written {AtCut} reads {atCut}");
            check.Expect(pastCut == PastCut, $"E3: the texel written {PastCut} reads {pastCut}");
        }
        finally { FreeJob(j); }
    }

    // Given the data texture
    // When the twin reads every texel at its centre with filtering
    // Then each matches the exact read.
    private static void Cx548E4(Checker check)
    {
        Rgba8[] written = Profile();
        Job j = SkyDecode(DataTxi, Tga(written, ProfileW, ProfileH));
        try
        {
            if (!Decoded(check, "E4", j)) return;
            ReadOnlySpan<Rgba8> top = Level0(j);
            int wrong = 0;
            string first = "";
            for (int y = 0; y < ProfileH; y++)
                for (int x = 0; x < ProfileW; x++)
                {
                    Rgba8 filtered = Bilinear(top, ProfileW, ProfileH, (x + 0.5f) / ProfileW, (y + 0.5f) / ProfileH);
                    Rgba8 exact = written[MipChain.Cell(ProfileW, x, y)];
                    if (filtered == exact) continue;
                    if (wrong++ == 0) first = $"({x},{y}) filters to {filtered}, written {exact}";
                }
            check.Expect(wrong == 0, $"E4: {wrong} of {ProfileW * ProfileH} texels read differently through the filter, first {first}");
        }
        finally { FreeJob(j); }
    }

    // Given the same area with the texture transcode on and off
    // When the twin draws
    // Then both read identical bytes, equal to a stock client's.
    private static void Cx548E5(Checker check)
    {
        byte[] stock = MemoryMarshal.AsBytes(Profile().AsSpan()).ToArray();
        bool boot = Renderer.Bcn.Boot;
        byte[]? on, off;
        try
        {
            Renderer.Bcn.Boot = true;
            on = Level0Bytes(check, "E5 (transcode on)");
            Renderer.Bcn.Boot = false;
            off = Level0Bytes(check, "E5 (transcode off)");
        }
        finally { Renderer.Bcn.Boot = boot; }
        if (on == null || off == null) return;
        check.Expect(on.AsSpan().SequenceEqual(off), $"E5: transcode on and off read different bytes, first at byte {FirstDiff(on, off)}");
        check.Expect(on.AsSpan().SequenceEqual(stock), $"E5: transcode on reads byte {FirstDiff(on, stock)} unlike a stock client");
        check.Expect(off.AsSpan().SequenceEqual(stock), $"E5: transcode off reads byte {FirstDiff(off, stock)} unlike a stock client");
    }

    /// <summary>The profile row's texels: the four values in row 0, the cut's two texels beside two values in row 1, the
    /// rest the zeros of unused slots. Each 2x2 block of the first four columns holds a hole beside a texel past the cut.</summary>
    private static Rgba8[] Profile()
    {
        var t = new Rgba8[ProfileW * ProfileH];
        t[MipChain.Cell(ProfileW, 0, 0)] = Value6360;
        t[MipChain.Cell(ProfileW, 1, 0)] = ValueMinus0105;
        t[MipChain.Cell(ProfileW, 2, 0)] = ValueZero;
        t[MipChain.Cell(ProfileW, 3, 0)] = ValueOneEMinus8;
        t[MipChain.Cell(ProfileW, 0, 1)] = AtCut;
        t[MipChain.Cell(ProfileW, 1, 1)] = PastCut;
        t[MipChain.Cell(ProfileW, 2, 1)] = Value6360;
        t[MipChain.Cell(ProfileW, 3, 1)] = ValueMinus0105;
        return t;
    }

    /// <summary>An uncompressed 32-bit TGA of <paramref name="texels"/>, its header laid out by hand from the file
    /// format and each texel stored B, G, R, A as the format orders it.</summary>
    private static byte[] Tga(ReadOnlySpan<Rgba8> texels, int w, int h)
    {
        const int HeaderLen = 18, TypeAt = 2, WidthAt = 12, HeightAt = 14, BitsAt = 16, DescriptorAt = 17;
        const byte TrueColour = 2, Bits = 32, AlphaBits = 8;
        var file = new byte[HeaderLen + texels.Length * 4];
        file[TypeAt] = TrueColour; file[BitsAt] = Bits; file[DescriptorAt] = AlphaBits;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(WidthAt), (ushort)w);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(HeightAt), (ushort)h);
        Span<Rgba8> stored = MemoryMarshal.Cast<byte, Rgba8>(file.AsSpan(HeaderLen));
        for (int i = 0; i < texels.Length; i++) stored[i] = new Rgba8(texels[i].B, texels[i].G, texels[i].R, texels[i].A);
        return file;
    }

    /// <summary>The file decoded as the worker decodes a sky's texture0 -- a loose TGA, acquired to cut -- after its
    /// <c>.txi</c> words reached the job. Slot 0, which nothing allocates, takes the material words and gets them back.</summary>
    private static Job SkyDecode(string? txi, byte[] tga)
    {
        var j = new Job { Kind = TexKind.Plain, Cuts = true, Loc = new TexLocation(KindLoose, 0, "", 0, -1, tga.Length, false, TgaResType) };
        byte blend = _txiBlend[0];
        bool decal = _txiDecal[0];
        try { TxiMaterial(0, txi, j); }
        finally { _txiBlend[0] = blend; _txiDecal[0] = decal; }
        Decode(j, tga);
        return j;
    }

    private static bool Decoded(Checker check, string example, Job j)
    {
        check.Expect(j.Why == null && j.Data != null && j.Channels == 4 && j.W == ProfileW && j.H == ProfileH,
                     $"{example}: the data texture did not decode as a {ProfileW}x{ProfileH} RGBA sheet ({j.Why ?? $"{j.W}x{j.H}, {j.Channels} channel(s)"})");
        return j.Data != null && j.Channels == 4 && j.W == ProfileW && j.H == ProfileH;
    }

    /// <summary>Level 0 as the upload sends it: the first level of a chain, or the sheet the device mips.</summary>
    private static ReadOnlySpan<Rgba8> Level0(Job j) => MemoryMarshal.Cast<byte, Rgba8>(Level(j, 0, j.W * j.H * 4));

    private static byte[]? Level0Bytes(Checker check, string example)
    {
        Job j = SkyDecode(DataTxi, Tga(Profile(), ProfileW, ProfileH));
        try { return Decoded(check, example, j) ? Level(j, 0, j.W * j.H * 4).ToArray() : null; }
        finally { FreeJob(j); }
    }

    private static void FreeJob(Job j)
    {
        if (j.Data == null) return;
        NativeMemory.Free(j.Data);
        j.Data = null;
    }

    private static void ExpectFloat(Checker check, string example, Rgba8 texel, float want)
    {
        float got = BinaryPrimitives.ReadSingleLittleEndian(MemoryMarshal.AsBytes(new ReadOnlySpan<Rgba8>(in texel)));
        check.Expect(BitConverter.SingleToInt32Bits(got) == BitConverter.SingleToInt32Bits(want),
                     $"{example}: a texel packing {want:R} reads {got:R} ({texel})");
    }

    /// <summary>A linear, repeating sample of <paramref name="level"/> at (<paramref name="u"/>, <paramref name="v"/>),
    /// each channel rounded back to a byte: the four nearest texels weighted by distance, as a device filters.</summary>
    private static Rgba8 Bilinear(ReadOnlySpan<Rgba8> level, int w, int h, float u, float v)
    {
        float fx = u * w - 0.5f, fy = v * h - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float ax = fx - x0, ay = fy - y0;
        Rgba8 a = level[MipChain.Cell(w, WrapIndex(x0, w), WrapIndex(y0, h))], b = level[MipChain.Cell(w, WrapIndex(x0 + 1, w), WrapIndex(y0, h))];
        Rgba8 c = level[MipChain.Cell(w, WrapIndex(x0, w), WrapIndex(y0 + 1, h))], d = level[MipChain.Cell(w, WrapIndex(x0 + 1, w), WrapIndex(y0 + 1, h))];
        byte Mix(byte p, byte q, byte r, byte s)
            => (byte)MathF.Round((p * (1 - ax) + q * ax) * (1 - ay) + (r * (1 - ax) + s * ax) * ay);
        return new Rgba8(Mix(a.R, b.R, c.R, d.R), Mix(a.G, b.G, c.G, d.G), Mix(a.B, b.B, c.B, d.B), Mix(a.A, b.A, c.A, d.A));
    }

    private static int WrapIndex(int i, int n) => ((i % n) + n) % n;

    private static int FirstDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return n;
    }
}
