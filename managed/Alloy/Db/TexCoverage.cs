using System;
using CX.Framework;
using static CX.Alloy.DdsReader;

namespace CX.Alloy.Db;

/// <summary>What a texture's alpha channel MEANS, which is what decides whether a draw is opaque, alpha-tested
/// or blended. Asked by <c>DrawRecipe</c>, answered once per texture at its decode on the <c>cx-tex</c> worker,
/// and re-asked through the registry's dependents edge -- so a recipe that classified against
/// <see cref="Unknown"/> is re-classified when the texture lands.</summary>
internal enum TexCoverage : byte
{
    /// <summary>Not decided yet (still loading), or the decode could not answer. A caller keeps its own
    /// conservative guess and is told again at arrival.</summary>
    Unknown,
    /// <summary>Every texel is fully opaque: the draw may go in the opaque class and the early-Z prepass.</summary>
    Opaque,
    /// <summary>BINARY alpha -- every texel is in or out, nothing in between. An alpha-tested cutout: still
    /// depth-correct, still prepassable, no sorting needed.</summary>
    Cutout,
    /// <summary>Fractional alpha somewhere: the draw has to blend, and therefore to sort.</summary>
    Blended,
}

/// <summary>The coverage verdict, derived from the DECODED pixels and nothing else: the engine's
/// <c>GetIsOpaque</c> memoises verdicts nobody checked a texel for and the GL internal format lies too (DXT1
/// encoders emit punchthrough blocks for opaque black), so only the sampler's answer counts. BC1 is the measured
/// exception -- uploaded as <c>COMPRESSED_RGB_S3TC_DXT1</c>, its sampler alpha is never below 1.0, so Opaque.</summary>
internal static unsafe class TexCoverageOf
{
    /// <summary>Alpha at or below this is "out", at or above <see cref="OpaqueAt"/> is "in", anything between
    /// makes the texture <see cref="TexCoverage.Blended"/>. A tolerance rather than exact 0 and 255 because a
    /// BCn alpha block's endpoint interpolation lands a nominal 255 on 254; the cost of no tolerance is calling
    /// a plain cutout blended and sorting the whole creature.</summary>
    internal const int CutoutAt = 2, OpaqueAt = 253;

    /// <summary>The verdict for an RGBA8 image, read off the alpha channel at <paramref name="stride"/>-byte
    /// intervals. <paramref name="stride"/> 1 or 3 means there is no alpha channel at all.</summary>
    internal static TexCoverage OfRaster(ReadOnlySpan<byte> px, int w, int h, int stride)
    {
        if (stride < 4) return TexCoverage.Opaque;
        long n = (long)w * h;
        if (n <= 0 || px.Length < n * stride) return TexCoverage.Unknown;
        bool anyOut = false;
        for (long i = 0; i < n; i++)
        {
            int a = px[(int)(i * stride + 3)];
            if (a >= OpaqueAt) continue;
            if (a > CutoutAt) return TexCoverage.Blended;   // one fractional texel settles it
            anyOut = true;
        }
        return anyOut ? TexCoverage.Cutout : TexCoverage.Opaque;
    }

    /// <summary>The verdict for a BCn level 0. BC1 and the single/two-channel and HDR formats carry no alpha
    /// the sampler will return; BC2, BC3 and BC7 are decoded and read.</summary>
    internal static TexCoverage OfBcn(ReadOnlySpan<byte> blocks, int w, int h, Fmt f, ref byte[] scratch)
    {
        switch (f)
        {
            // No alpha in what the sampler answers -- see the class remarks for BC1 in particular.
            case Fmt.Bc1:
            case Fmt.Bc4: case Fmt.Bc4S:
            case Fmt.Bc5: case Fmt.Bc5S:
            case Fmt.Bc6U: case Fmt.Bc6S:
                return TexCoverage.Opaque;
            case Fmt.Bc2: case Fmt.Bc3: case Fmt.Bc7:
                break;
            default:
                return TexCoverage.Unknown;
        }
        long want = (long)w * h * 4;
        if (w <= 0 || h <= 0 || want > int.MaxValue) return TexCoverage.Unknown;
        if (blocks.Length < LevelSize(w, h, Channels(f))) return TexCoverage.Unknown;
        if (scratch.Length < want) scratch = new byte[want];
        BcnDecode.Unpack(blocks, w, h, f, scratch.AsSpan(0, (int)want));
        return OfRaster(scratch.AsSpan(0, (int)want), w, h, 4);
    }

    /// <summary>The engine's own channel count for a format, which is what <see cref="LevelSize"/> wants.</summary>
    private static int Channels(Fmt f) => f is Fmt.Bc1 ? 3 : f is Fmt.Bc4 or Fmt.Bc4S ? 1 : 4;

    /// <summary>The <c>texcov:</c> note's text: which decode answered, and -- for the two verdicts that take a
    /// texture OUT of the 0.2 cutout -- what that costs the draw. <paramref name="route"/> names the container
    /// and format, which is what makes a texture that draws solid findable in <c>cx.log</c>.</summary>
    internal static string Why(string route, TexCoverage cov) => cov switch
    {
        TexCoverage.Opaque => $"{route} -> Opaque: the cutout test is off for it, so nothing is discarded",
        TexCoverage.Unknown => $"{route} -> Unknown: the decode could not answer, so it keeps the 0.2 cutout",
        _ => $"{route} -> {cov}",
    };

    /// <summary>Off-device: the two tolerances, each format's verdict and the three raster shapes.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void Expect(bool cond, string what)
        {
            if (cond) return;
            ok = false;
            log.Always($"TexCoverage.SelfCheck: {what}");
        }
        static byte[] Rgba(params byte[] alphas)
        {
            var b = new byte[alphas.Length * 4];
            for (int i = 0; i < alphas.Length; i++) b[i * 4 + 3] = alphas[i];
            return b;
        }
        Expect(OfRaster(Rgba(255, 255, 255, 255), 2, 2, 4) == TexCoverage.Opaque, "all-255 alpha is not Opaque");
        Expect(OfRaster(Rgba(255, 0, 255, 0), 2, 2, 4) == TexCoverage.Cutout, "0/255 alpha is not Cutout");
        Expect(OfRaster(Rgba(255, 128, 255, 255), 2, 2, 4) == TexCoverage.Blended, "a 128 texel is not Blended");
        Expect(OfRaster(Rgba(255, 254, 253, 2), 2, 2, 4) == TexCoverage.Cutout,
            $"the tolerance is wrong: >= {OpaqueAt} must read as in and <= {CutoutAt} as out");
        Expect(OfRaster(Rgba(255, 252, 255, 255), 2, 2, 4) == TexCoverage.Blended,
            $"alpha {OpaqueAt - 1} must be fractional");
        Expect(OfRaster(Rgba(3, 3, 3, 3), 2, 2, 4) == TexCoverage.Blended,
            $"alpha {CutoutAt + 1} must be fractional");
        Expect(OfRaster(new byte[4], 2, 2, 1) == TexCoverage.Opaque, "a one-byte raster has no alpha to read");
        Expect(OfRaster(new byte[12], 2, 2, 3) == TexCoverage.Opaque, "a three-byte raster has no alpha to read");
        Expect(OfRaster(Rgba(255), 4, 4, 4) == TexCoverage.Unknown, "a short raster was not refused");

        var scratch = Array.Empty<byte>();
        // A 4x4 BC1 block in punchthrough mode (c0 <= c1) whose indices all name slot 3 -- the one case the
        // format calls transparent, and the one the sampler never returns, because Alloy uploads BC1 as RGB.
        var bc1 = new byte[8];
        bc1[0] = 0x00; bc1[1] = 0x00; bc1[2] = 0xFF; bc1[3] = 0xFF;   // c0 = 0x0000 <= c1 = 0xffff
        bc1[4] = bc1[5] = bc1[6] = bc1[7] = 0xFF;                      // every index = 3
        Expect(OfBcn(bc1, 4, 4, Fmt.Bc1, ref scratch) == TexCoverage.Opaque,
            "a fully punchthrough BC1 block is not Opaque -- which is the measured sampler behaviour");
        Expect(OfBcn(new byte[8], 4, 4, Fmt.Bc4, ref scratch) == TexCoverage.Opaque, "BC4 has no alpha");
        Expect(OfBcn(new byte[16], 4, 4, Fmt.Bc5, ref scratch) == TexCoverage.Opaque, "BC5 has no alpha");
        // A BC2 block: the first eight bytes are 4-bit alpha per texel. All 0xf = opaque; 0x0/0xf = cutout;
        // an 0x8 nibble is fractional.
        var bc2 = new byte[16];
        for (int i = 0; i < 8; i++) bc2[i] = 0xFF;
        Expect(OfBcn(bc2, 4, 4, Fmt.Bc2, ref scratch) == TexCoverage.Opaque, "an all-0xf BC2 alpha block is not Opaque");
        bc2[0] = 0x0F;
        Expect(OfBcn(bc2, 4, 4, Fmt.Bc2, ref scratch) == TexCoverage.Cutout, "a 0x0 BC2 nibble is not Cutout");
        bc2[0] = 0x8F;
        Expect(OfBcn(bc2, 4, 4, Fmt.Bc2, ref scratch) == TexCoverage.Blended, "a 0x8 BC2 nibble is not Blended");
        // A BC3 block: a0/a1 then 3-bit indices. a0 = a1 = 255 is opaque whatever the indices say.
        var bc3 = new byte[16];
        bc3[0] = 255; bc3[1] = 255;
        Expect(OfBcn(bc3, 4, 4, Fmt.Bc3, ref scratch) == TexCoverage.Opaque,
            "a BC3 block with both alpha endpoints at 255 is not Opaque");
        bc3[0] = 255; bc3[1] = 0;
        // The sixteen 3-bit indices pack LSB-first across six bytes, so "every index = 1" is the 48-bit value
        // 0x249249249249, little-endian. With a0 > a1 the table is the six-interpolation one and index 1 is
        // a1 itself, so every texel reads alpha 0.
        byte[] idx1 = { 0x49, 0x92, 0x24, 0x49, 0x92, 0x24 };   // not-an-offset: packed 3-bit index fields
        idx1.CopyTo(bc3, 2);
        Expect(OfBcn(bc3, 4, 4, Fmt.Bc3, ref scratch) == TexCoverage.Cutout,
            "a BC3 block of 255/0 endpoints read at index 1 is not Cutout");
        Expect(OfBcn(new byte[4], 4, 4, Fmt.Bc3, ref scratch) == TexCoverage.Unknown, "a short BC3 block was not refused");
        Expect(OfBcn(new byte[16], 4, 4, Fmt.None, ref scratch) == TexCoverage.Unknown, "an unknown format answered");

        // The texcov: note. Oracle: the two verdicts the 0.2 cutout cannot reach are written out here by
        // hand, so a route that stops naming the format -- the only thing that makes a solid-drawing texture
        // findable in cx.log -- fails rather than printing a bare enum name.
        const string route = "DDS Bc1, 3 channel(s)";
        Expect(Why(route, TexCoverage.Opaque).StartsWith(route, StringComparison.Ordinal)
               && Why(route, TexCoverage.Opaque).Contains("the cutout test is off", StringComparison.Ordinal),
            "the Opaque note does not name its route and say the test is off");
        Expect(Why(route, TexCoverage.Unknown).Contains("keeps the 0.2 cutout", StringComparison.Ordinal),
            "the Unknown note does not say the part keeps the cutout");
        Expect(Why(route, TexCoverage.Cutout).Contains("Cutout", StringComparison.Ordinal)
               && !Why(route, TexCoverage.Cutout).Contains("cutout test is off", StringComparison.Ordinal),
            "a Cutout verdict must not claim the test is off -- its alpha IS tested");
        log.Info(ok ? "TexCoverage.SelfCheck: passed" : "TexCoverage.SelfCheck: FAILED");
        return ok;
    }
}
