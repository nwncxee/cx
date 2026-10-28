using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The lightmap golden's fixture and its verdict. Two coloured lights over a hard shadow edge, with a hole whose
/// middle the dilation cannot reach, and what the finish's rule makes of each texel -- written out here from the
/// rule, so the encoder is never consulted about its own answer.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>Luminance weights, as the encoder's own CX_LUM gives them -- the fixture's lights are shaped
    /// against the same curve the finish normalizes by.</summary>
    private const double LumR = 0.2126, LumG = 0.7152, LumB = 0.0722;

    /// <summary>Is (<paramref name="x"/>, <paramref name="y"/>) a texel the bake covered? A margin all round and
    /// a rectangular hole, whose middle is more than two texels from anything covered.</summary>
    private static bool LmCovered(int x, int y)
        => x >= 6 && x < 58 && y >= 6 && y < 58 && !(x >= 27 && x < 39 && y >= 20 && y < 44);

    /// <summary>The three scratch planes as the bake would leave them -- irradiance with coverage in alpha,
    /// luminance times direction, and the per-mover shares -- and <paramref name="want"/>, the finish's answer
    /// per texel (irradiance, direction encoded, shares) or null where it has none.</summary>
    private static void LmFixture(out float[] pa, out float[] pb, out float[] pc, out float[]?[] want)
    {
        pa = new float[LmN * LmN * 4];
        pb = new float[LmN * LmN * 4];
        pc = new float[LmN * LmN * 4];
        for (int y = 0; y < LmN; y++)
            for (int x = 0; x < LmN; x++)
            {
                // half strength 24 texels out: 3 m at the bake's default 8 texels per metre
                double d1 = ((x - 16.0) * (x - 16.0) + (y - 16.0) * (y - 16.0)) / 600.0;
                double d2 = ((x - 48.0) * (x - 48.0) + (y - 40.0) * (y - 40.0)) / 600.0;
                double shade = x + y < 40 ? 0.15 : 1.0;
                double[] near = [1.0, 0.6, 0.3], far = [0.2, 0.4, 1.0];
                double[] irr = new double[3];
                for (int c = 0; c < 3; c++) irr[c] = shade * (3.0 * near[c] / (1 + d1) + 2.0 * far[c] / (1 + d2));
                double lum = LumR * irr[0] + LumG * irr[1] + LumB * irr[2];
                double[] v = [(x - 32.0) / 40.0, (y - 32.0) / 50.0, 1.0];
                double[] share = [x / 63.0, y / 63.0, 0.5, 1.0 - x / 63.0];
                int i = (y * LmN + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    pa[i + c] = Half16(irr[c]);
                    pb[i + c] = Half16(v[c] * lum * 0.7);
                }
                pa[i + 3] = LmCovered(x, y) ? 2f : 0f;
                pb[i + 3] = 0f;
                for (int c = 0; c < 4; c++) pc[i + c] = Half16(share[c] * lum);
            }
        want = LmWant(pa, pb, pc);
    }

    /// <summary>A value as the scratch planes hold it: RGBA16F, so both sides see the same quantised texel.</summary>
    private static float Half16(double v) => (float)(Half)(float)v;

    /// <summary>The finish's rule over the planes: a covered texel as baked, an uncovered one the nearest covered
    /// texel within two (nearest first, then in scan order), and no texel at all past that. Ten floats a texel --
    /// irradiance, the direction as xyz * 0.5 + 0.5, and the four shares as fractions of the texel's luminance.</summary>
    private static float[]?[] LmWant(float[] pa, float[] pb, float[] pc)
    {
        var want = new float[]?[LmN * LmN];
        for (int y = 0; y < LmN; y++)
            for (int x = 0; x < LmN; x++)
            {
                int src = (y * LmN + x) * 4;
                if (pa[src + 3] <= 0f)
                {
                    int best = int.MaxValue;
                    int pick = -1;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int qx = x + dx, qy = y + dy;
                            if ((dx == 0 && dy == 0) || qx < 0 || qx >= LmN || qy < 0 || qy >= LmN) continue;
                            if (pa[(qy * LmN + qx) * 4 + 3] <= 0f || dx * dx + dy * dy >= best) continue;
                            best = dx * dx + dy * dy;
                            pick = (qy * LmN + qx) * 4;
                        }
                    if (pick < 0) continue;
                    src = pick;
                }
                double bx = pb[src], by = pb[src + 1], bz = pb[src + 2];
                double l = Math.Sqrt(bx * bx + by * by + bz * bz);
                double[] d = l > 1e-6 ? [bx / l, by / l, bz / l] : [0.0, 0.0, 1.0];
                double lum = Math.Max(LumR * pa[src] + LumG * pa[src + 1] + LumB * pa[src + 2], 1e-6);
                var w = new float[10];
                for (int c = 0; c < 3; c++)
                {
                    w[c] = Math.Max(pa[src + c], 0f);
                    w[3 + c] = (float)(d[c] * 0.5 + 0.5);
                }
                for (int c = 0; c < 4; c++) w[6 + c] = (float)Math.Clamp(pc[src + c] / lum, 0.0, 1.0);
                want[y * LmN + x] = w;
            }
        return want;
    }

    /// <summary>Where the shadow edge crosses a block, so a block that cannot hold a 6x step in one set of
    /// endpoints is judged apart from the smooth ones.</summary>
    private static bool LmEdgeBlock(int x, int y) => (x & ~3) + (y & ~3) < 40 && 40 <= (x | 3) + (y | 3);

    /// <summary>Inside the band the encoder was asked for: the texels of blocks <see cref="LmFirst"/> through
    /// the end of <see cref="LmBand"/>.</summary>
    private static bool LmInBand(int x, int y)
        => x >= LmFirst * 4 && x < (LmFirst + LmBand) * 4 && y >= LmFirst * 4 && y < (LmFirst + LmBand) * 4;

    /// <summary>The decoded planes against <paramref name="want"/>. The bounds are each format's, measured: one or
    /// two lines a BC6H block cannot follow two coloured lights crossing, nor a 6x shadow step past mode 1's delta
    /// range; directions and shares hold to a few 8-bit steps, and nothing is written outside the band.</summary>
    private static void LmJudge(Checker check, ExtenderLog log, float[]?[] want, float[] irr, float[] dir, byte[] shares)
    {
        double smooth = 0, all = 0, sum = 0, dirErr = 0, shareErr = 0;
        int n = 0, stray = 0, notBlack = 0;
        for (int y = 0; y < LmN; y++)
            for (int x = 0; x < LmN; x++)
            {
                int i = (y * LmN + x) * 4;
                float[]? w = LmInBand(x, y) ? want[y * LmN + x] : null;
                if (w == null)
                {
                    if (shares[i] != LmUntouched || shares[i + 1] != LmUntouched
                        || shares[i + 2] != LmUntouched || shares[i + 3] != LmUntouched) stray++;
                    if (!LmBlockFinished(want, x, y) && (irr[i] != 0f || irr[i + 1] != 0f || irr[i + 2] != 0f)) notBlack++;
                    continue;
                }
                double rel = 0;
                for (int c = 0; c < 3; c++) rel = Math.Max(rel, Math.Abs(irr[i + c] - w[c]) / Math.Max(w[c], 1e-3));
                all = Math.Max(all, rel);
                sum += rel;
                n++;
                if (!LmEdgeBlock(x, y)) smooth = Math.Max(smooth, rel);
                for (int c = 0; c < 3; c++) dirErr = Math.Max(dirErr, Math.Abs(dir[i + c] - w[3 + c]) * 255);
                for (int c = 0; c < 4; c++) shareErr = Math.Max(shareErr, Math.Abs(shares[i + c] / 255.0 - w[6 + c]) * 255);
            }
        double mean = sum / Math.Max(n, 1);
        check.Expect(n > 0, "the band the encoder wrote holds finished texels to judge");
        check.Expect(mean <= 0.015, $"BC6H irradiance is {mean * 100:F2}% out on average, past 1.5%");
        check.Expect(smooth <= 0.04, $"BC6H irradiance is {smooth * 100:F2}% out at worst off the shadow edge, past 4%");
        check.Expect(all <= 0.10, $"BC6H irradiance is {all * 100:F2}% out at worst on the shadow edge, past 10%");
        check.Expect(dirErr <= 6.0, $"the BC7 direction is {dirErr:F1} 8-bit steps out, past 6");
        check.Expect(shareErr <= 1.01, $"the mover shares are {shareErr:F2} 8-bit steps out, past 1");
        check.Expect(stray == 0, $"{stray} share texels were written outside what the finish answered for");
        check.Expect(notBlack == 0, $"{notBlack} texels of blocks the encoder never wrote came back non-black");
        log.Info($"GpuGolden: lightmap encoder, irradiance {mean * 100:F2}% mean, {smooth * 100:F2}% max off the shadow "
               + $"edge, {all * 100:F2}% on it, direction {dirErr:F1} and shares {shareErr:F2} 8-bit steps, "
               + $"{stray} stray share texels, {notBlack} texels of empty blocks not black");
    }

    /// <summary>Did the finish answer for any texel of the 4x4 block (<paramref name="x"/>, <paramref name="y"/>)
    /// sits in? A block with one finished texel is encoded whole, so its other texels are not expected black.</summary>
    private static bool LmBlockFinished(float[]?[] want, int x, int y)
    {
        for (int by = y & ~3; by < (y & ~3) + 4; by++)
            for (int bx = x & ~3; bx < (x & ~3) + 4; bx++)
                if (LmInBand(bx, by) && want[by * LmN + bx] != null) return true;
        return false;
    }
}
