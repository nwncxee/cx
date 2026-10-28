using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>
/// The lightmap golden's fixture: two coloured lights over a hard shadow edge, with a hole whose middle the
/// dilation cannot reach, and what the finish's rule makes of each texel -- written out here from the rule, so
/// the encoder is never consulted about its own answer.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>Luminance weights, as the encoder's own CX_LUM gives them -- the fixture's lights are shaped
    /// against the same curve the finish normalizes by.</summary>
    private const double LumR = 0.2126, LumG = 0.7152, LumB = 0.0722;

    /// <summary>The finish's answer for one texel: the irradiance, the direction as xyz * 0.5 + 0.5, and the
    /// four mover shares as fractions of the texel's luminance.</summary>
    private readonly record struct LmTexel(Vector3 Irr, Vector3 Dir, Vector4 Share);

    /// <summary>Is (<paramref name="x"/>, <paramref name="y"/>) a texel the bake covered? A margin all round and
    /// a rectangular hole, whose middle is more than two texels from anything covered.</summary>
    private static bool LmCovered(int x, int y)
        => x >= 6 && x < 58 && y >= 6 && y < 58 && !(x >= 27 && x < 39 && y >= 20 && y < 44);

    /// <summary>The three scratch planes as the bake would leave them -- irradiance with coverage in alpha,
    /// luminance times direction, and the per-mover shares -- and <paramref name="want"/>, the finish's answer
    /// per texel or null where it has none.</summary>
    private static void LmFixture(out float[] pa, out float[] pb, out float[] pc, out LmTexel?[] want)
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
    /// texel within two (nearest first, then in scan order), and no texel at all past that.</summary>
    private static LmTexel?[] LmWant(float[] pa, float[] pb, float[] pc)
    {
        var want = new LmTexel?[LmN * LmN];
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
                var b = new Vector3(pb[src], pb[src + 1], pb[src + 2]);
                Vector3 dir = b.Length() > 1e-6f ? Vector3.Normalize(b) : new Vector3(0, 0, 1);
                double lum = Math.Max(LumR * pa[src] + LumG * pa[src + 1] + LumB * pa[src + 2], 1e-6);
                var share = new Vector4((float)Math.Clamp(pc[src] / lum, 0.0, 1.0),
                                        (float)Math.Clamp(pc[src + 1] / lum, 0.0, 1.0),
                                        (float)Math.Clamp(pc[src + 2] / lum, 0.0, 1.0),
                                        (float)Math.Clamp(pc[src + 3] / lum, 0.0, 1.0));
                want[y * LmN + x] = new LmTexel(Vector3.Max(new Vector3(pa[src], pa[src + 1], pa[src + 2]), Vector3.Zero),
                                                dir * 0.5f + new Vector3(0.5f), share);
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

    /// <summary>Did the finish answer for any texel of the 4x4 block (<paramref name="x"/>, <paramref name="y"/>)
    /// sits in? A block with one finished texel is encoded whole, so its other texels are not expected black.</summary>
    private static bool LmBlockFinished(LmTexel?[] want, int x, int y)
    {
        for (int by = y & ~3; by < (y & ~3) + 4; by++)
            for (int bx = x & ~3; bx < (x & ~3) + 4; bx++)
                if (LmInBand(bx, by) && want[by * LmN + bx] != null) return true;
        return false;
    }
}
