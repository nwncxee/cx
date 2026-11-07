using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>
/// The distant look of a cut-out texture (approved-spec, E1, E4, E5, E6): colour and fullness per mip level of a leaf
/// sheet with pale colour under its holes. Oracle: the sheet painted here, whose leaf colour and third-of-the-texels
/// coverage are hand-written, and the name of the retired knob. The on-screen halves are checked on the real client.
/// </summary>
internal static unsafe class MipChainApprovedExamples
{
    private const int Side = 64;
    private const byte LeafR = 73, LeafG = 51, LeafB = 25, MatteR = 166, MatteG = 160, MatteB = 130;
    /// <summary>The red a kept texel may reach: the leaf's own, plus rounding and what an edge's filter may mix in.</summary>
    private const int LeafRedLimit = LeafR + 6;
    /// <summary>Texels of the sheet a leaf covers, a third: one diagonal stripe in three.</summary>
    private const float SheetCoverage = 1f / 3f;
    /// <summary>The discard's alpha as a byte: a texel at or under it is a hole.</summary>
    private const int Hole = 51;

    /// <summary>Leaves in diagonal stripes on a pale matte, the shape of the reported bush texture.</summary>
    internal static byte[] Sheet()
    {
        var px = new byte[Side * Side * 4];
        Span<MipChain.Texel> t = MemoryMarshal.Cast<byte, MipChain.Texel>(px.AsSpan());
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
                t[MipChain.Cell(Side, x, y)] = (x + y) % 3 == 0 ? new MipChain.Texel(LeafR, LeafG, LeafB, 255) : new MipChain.Texel(MatteR, MatteG, MatteB, 0);
        return px;
    }

    /// <summary>The worst kept texel's red over a level, and over the bilinear midpoints the cut would keep.</summary>
    private static int BrightestKept(ReadOnlySpan<byte> level, int w, int h)
    {
        ReadOnlySpan<MipChain.Texel> t = MemoryMarshal.Cast<byte, MipChain.Texel>(level);
        int worst = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                MipChain.Texel o = t[MipChain.Cell(w, x, y)], e = t[MipChain.Cell(w, (x + 1) % w, y)];
                if (o.A > Hole) worst = Math.Max(worst, o.R);
                if ((o.A + e.A) / 2 > Hole) worst = Math.Max(worst, (o.R + e.R) / 2);
            }
        return worst;
    }

    private static float Kept(ReadOnlySpan<byte> level)
    {
        ReadOnlySpan<MipChain.Texel> t = MemoryMarshal.Cast<byte, MipChain.Texel>(level);
        int kept = 0;
        foreach (MipChain.Texel p in t) if (p.A > Hole) kept++;
        return (float)kept / t.Length;
    }

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MipChainApprovedExamples.SelfCheck");
        byte[] sheet = Sheet();
        byte[]? chain = MipChain.Raster(sheet, Side, Side);
        check.Expect(chain != null, "a sheet with holes got no chain of its own");

        // Given a TDN topiary whose leaf texture hides pale colour under its transparent texels, with AA off
        // When the player looks at it from up close and then from far away
        // Then the leaf edges keep the leaf's own colour at every distance, with no pale outline
        if (chain != null)
        {
            long off = 0;
            for (int w = Side; w >= 1; off += (long)w * w * 4, w >>= 1)
            {
                int red = BrightestKept(chain.AsSpan((int)off, w * w * 4), w, w);
                check.Expect(red <= LeafRedLimit, $"E1: a kept texel at {w}x{w} is red {red}, past the leaf's {LeafR}");
            }
        }

        // Given a tree whose leaf texture is a compressed DDS with stored mips and a pale colour under its holes
        // When the player looks at it from far away
        // Then its leaf edges show no pale outline either
        byte[]? dds = MipChain.Dds(StoredBc3(sheet), Side, Side, 7, DdsReader.Fmt.Bc3);
        check.Expect(dds != null, "E4: a compressed sheet with holes got no chain of its own");
        if (dds != null)
        {
            long off = BcnEncode.LevelSize(Side, Side, true);
            var rgba = new byte[Side * Side * 4];
            for (int w = Side / 2; w >= 4; off += BcnEncode.LevelSize(w, w, true), w >>= 1)
            {
                BcnDecode.Unpack(dds.AsSpan((int)off, BcnEncode.LevelSize(w, w, true)), w, w, DdsReader.Fmt.Bc3, rgba.AsSpan(0, w * w * 4));
                int red = BrightestKept(rgba.AsSpan(0, w * w * 4), w, w);
                check.Expect(red <= LeafRedLimit + 4, $"E4: a kept texel at {w}x{w} is red {red}, past the leaf's {LeafR}");
            }
        }

        // Given a leafy tree seen up close
        // When the player walks away until it is small on screen
        // Then its silhouette is as full as up close: it neither thins out nor fills in solid
        if (chain != null)
        {
            long off = 0;
            for (int w = Side; w >= 4; off += (long)w * w * 4, w >>= 1)
            {
                float kept = Kept(chain.AsSpan((int)off, w * w * 4));
                check.Expect(Math.Abs(kept - SheetCoverage) <= 0.1f, $"E5: {w}x{w} keeps {kept:F2} of its texels, not about {SheetCoverage:F2}");
            }
        }

        // Given the settings panel and a cx.cfg that still holds alloy.cutmip
        // When the client starts
        // Then Distant cut-out fill is no longer offered, and the old line changes nothing on screen
        const string retired = "cutmip";
        check.Expect(KnobGroup.Find("alloy" + "." + retired) == null, "E6: the fill slider is still declared");

        log.Info(check.Ok ? "MipChainApprovedExamples.SelfCheck: passed" : "MipChainApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The sheet as a stored BC3 chain box-filtered over its holes, as a texture tool leaves it.</summary>
    private static byte[] StoredBc3(byte[] sheet)
    {
        long total = 0;
        for (int w = Side; w >= 1; w >>= 1) total += BcnEncode.LevelSize(w, w, true);
        var o = new byte[total];
        byte[] level = (byte[])sheet.Clone();
        long at = 0;
        for (int w = Side; w >= 1; w >>= 1)
        {
            int size = BcnEncode.LevelSize(w, w, true);
            BcnEncode.EncodeLevel(level.AsSpan(0, w * w * 4), w, w, true, o.AsSpan((int)at, size));
            at += size;
            if (w == 1) break;
            int n = w / 2;
            var next = new byte[n * n * 4];
            ReadOnlySpan<MipChain.Texel> from = MemoryMarshal.Cast<byte, MipChain.Texel>(level);
            Span<MipChain.Texel> to = MemoryMarshal.Cast<byte, MipChain.Texel>(next.AsSpan());
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int x2 = x + x, y2 = y + y;
                    MipChain.Texel a = from[MipChain.Cell(w, x2, y2)], b = from[MipChain.Cell(w, x2 + 1, y2)], c = from[MipChain.Cell(w, x2, y2 + 1)], d = from[MipChain.Cell(w, x2 + 1, y2 + 1)];
                    to[MipChain.Cell(n, x, y)] = new MipChain.Texel((byte)((a.R + b.R + c.R + d.R + 2) >> 2), (byte)((a.G + b.G + c.G + d.G + 2) >> 2),
                                                       (byte)((a.B + b.B + c.B + d.B + 2) >> 2), (byte)((a.A + b.A + c.A + d.A + 2) >> 2));
                }
            level = next;
        }
        return o;
    }
}
