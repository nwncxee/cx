using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The sky profile examples that already held before data textures were built (E1, E6-E8): they must keep holding.
/// Oracle: the hand-written profile texels, and chain sizes of a 64x4 and a 64x64 sheet worked by hand.</summary>
internal static unsafe partial class TexRegistry
{
    /// <summary>Hand-worked chain sizes: 64x4 down to 1x1 is 351 texels, 64x64 down to 1x1 is 5461, at four bytes each.</summary>
    private const long ProfileChainBytes = 1404, LeafChainBytes = 21844;
    /// <summary>The red a kept texel of the leaf sheet may reach: the sheet's hand-written leaf red 73, plus 6 for rounding.</summary>
    private const int LeafRedLimit = 79;

    internal static bool SkyProfileHoldsTodaySelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TexRegistry.SkyProfileHoldsToday.SelfCheck");
        Cx548E1(check);
        Cx548E6(check);
        Cx548E7(check);
        Cx548E8(check);
        log.Info(check.Ok ? "TexRegistry.SkyProfileHoldsToday.SelfCheck: passed"
                          : "TexRegistry.SkyProfileHoldsToday.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given a data texture on a sky material holding 6360.0 and -0.105 packed into texels
    // When the twin reads them at level 0
    // Then it gets exactly those values.
    private static void Cx548E1(Checker check)
    {
        Job j = SkyDecode(DataTxi, Tga(Profile(), ProfileW, ProfileH));
        try
        {
            if (!Decoded(check, "E1", j)) return;
            ReadOnlySpan<Rgba8> top = Level0(j);
            ExpectFloat(check, "E1", top[MipChain.Cell(ProfileW, 0, 0)], 6360f);
            ExpectFloat(check, "E1", top[MipChain.Cell(ProfileW, 1, 0)], -0.105f);
        }
        finally { FreeJob(j); }
    }

    // Given a cut-out leaf texture in the same area
    // When seen close up and far away
    // Then its edges still show no pale outline. (holds today)
    private static void Cx548E6(Checker check)
    {
        FreeJob(SkyDecode(DataTxi, Tga(Profile(), ProfileW, ProfileH)));
        const int Side = 64;
        Job leaf = SkyDecode(null, Tga(MemoryMarshal.Cast<byte, Rgba8>(MipChainApprovedExamples.Sheet().AsSpan()), Side, Side));
        try
        {
            check.Expect(leaf.Why == null && leaf.Chained, $"E6: the leaf sheet got no chain of its own ({leaf.Why ?? "decoded unchained"})");
            if (!leaf.Chained) return;
            long off = 0;
            for (int w = Side; w >= 1; off += (long)w * w * 4, w >>= 1)
            {
                int red = BrightestKeptRed(MemoryMarshal.Cast<byte, Rgba8>(Level(leaf, off, w * w * 4)));
                check.Expect(red <= LeafRedLimit, $"E6: a kept leaf texel at {w}x{w} is red {red}, past the leaf's {LeafRedLimit}");
            }
        }
        finally { FreeJob(leaf); }
    }

    // Given one area before and after the change
    // When /alloy reports texture memory
    // Then it differs by the data texture's own bytes at most.
    private static void Cx548E7(Checker check)
    {
        const int Side = 64;
        Job data = SkyDecode(DataTxi, Tga(Profile(), ProfileW, ProfileH));
        Job leaf = SkyDecode(null, Tga(MemoryMarshal.Cast<byte, Rgba8>(MipChainApprovedExamples.Sheet().AsSpan()), Side, Side));
        try
        {
            check.Expect(data.Why == null && leaf.Why == null, $"E7: the area did not decode ({data.Why ?? leaf.Why})");
            long after = data.Bytes + leaf.Bytes, before = ProfileChainBytes + LeafChainBytes;
            check.Expect(Math.Abs(after - before) <= ProfileChainBytes,
                         $"E7: the area holds {after} texture bytes, {after - before} off the {before} it held, past the data texture's {ProfileChainBytes}");
        }
        finally { FreeJob(data); FreeJob(leaf); }
    }

    // Given a machine where the extender's renderer stands down
    // When the sky draws
    // Then the engine sky reads the data texture exactly.
    private static void Cx548E8(Checker check)
    {
        byte[] tga = Tga(Profile(), ProfileW, ProfileH);
        byte[] stock = MemoryMarshal.AsBytes(Profile().AsSpan()).ToArray();
        RasterReader.Verdict verdict = RasterReader.ParseTga(tga, out RasterReader.Tga t, out string? why);
        check.Expect(verdict == RasterReader.Verdict.Pixels, $"E8: the engine's TGA decode stands aside for the data texture ({why})");
        if (verdict != RasterReader.Verdict.Pixels) return;
        var engine = new byte[t.Bytes];
        RasterReader.DecodeTga(tga, t, engine);
        check.Expect(engine.AsSpan().SequenceEqual(stock), $"E8: the engine's decode reads byte {FirstDiff(engine, stock)} unlike the file");
    }

    /// <summary>The reddest texel past the cut (alpha over 51) in one level of the leaf sheet.</summary>
    private static int BrightestKeptRed(ReadOnlySpan<Rgba8> level)
    {
        int red = 0;
        foreach (Rgba8 t in level) if (t.A > 51) red = Math.Max(red, t.R);
        return red;
    }
}
