using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The G-buffer golden: every lane <c>CxGbufPack</c> writes, read back through <c>CxGbufRead</c> and
/// <c>CxGbufFade</c>, held to the precision docs/spec/deferred-lighting.md states for it. The ownership test is
/// checked both ways -- a record at its own depth is read, one at a moved depth and one behind a cleared G0 are
/// not -- and the slot lane is driven to the last ownership slot there is.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>One record to pack and read back. The arrays are three or four long, as the lane they feed.</summary>
    private readonly record struct GbufCase(double[] N, int Flags, double ViewDepth, double Rough, double Spec,
                                            double Fresnel, double Coat, double CoatRough, double[] SpecC, int Slot,
                                            double SpecT, double[] DiffT, double LmLum, double[] LmMover,
                                            double[] LmShare, double[] SurfFade);

    /// <summary>The last ownership slot there is: the slot lane has to reach it, which is why a case asks for it.
    /// A parameter of the fixture, not an expected value -- every lane's oracle is the literal beside it.</summary>
    private static int LastSlot => AlloyRenderer.MaxOwned - 1;

    /// <summary>Hand-written records: everything at or near its floor, everything at its ceiling (the last slot,
    /// every flag, a fade at its maximum), an ordinary oblique surface with tiny weights, and one with negative
    /// and over-range terms the pack has to clamp. Each is the oracle for its own lanes.</summary>
    private static GbufCase[] GbufCases =>
    [
        new([0, 0, 1], 1, 0.5, 0.02, 0.04, 0, 0, 0, [0.04, 0.04, 0.04], 0, 1.0, [0.5, 0.25, 0.125], 0,
            [0, 0, 0, 0], [0, 0, 0], [0, 0, 1, 0]),
        new([0, 0, -1], 63, 37.25, 1, 1, 1, 1, 1, [1, 1, 1], LastSlot, 10.0, [40.0, 0.001, 3.0], 12.5,
            [1, 0.5, 0.25, 0], [55.0, 54.0, 0], [0.6, 0, 0.8, 10.0]),
        new([0.267, -0.535, 0.802], 7, 900.0, 0.31, 0.6, 0.35, 0.55, 0.02, [0.9, 0.3, 0.004], 4097, 0.37,
            [1e-4, 2e-4, 3e-4], 0.0213, [0.2, 0.4, 0.6, 0.8], [0.7, 0.8, 0.9], [0, 0, -1, 1.0]),
        new([-0.6, 0.64, -0.48], 41, 3.1, 0.75, 0.11, 0.9, 0.08, 0.3, [0, 0.5, 0.25], 99999, 0,
            [0, 0, 0], 300.0, [0, 1, 0, 1], [1000.0, 1.0, 0.01], [-0.48, 0.6, 0.64, 4.37]),
    ];

    /// <summary>Floats in and out per case: the record's lanes in, then everything read back plus the three
    /// ownership answers, the chart's record and the fade.</summary>
    private const int GbufIn = 32, GbufOut = 40;

    /// <summary>Floats per chart record (<c>CxChart</c>: rec and pick), and the chart record a baked slot gets --
    /// page 2 at (16, 32) with a quarter of a page texel per atlas texel, picking channels 3, none, 7 and 254.</summary>
    private const int ChartFloats = 8;
    private static readonly float[] BakedChart = [2f, 16f, 32f, 0.25f, 3f, -1f, 7f, 254f];
    private static readonly float[] UnbakedChart = [-1f, 0f, 0f, 0f, -1f, -1f, -1f, -1f];

    /// <summary>What a baked record reads back as: <c>CxLmTexelWorld</c> is 1 / (8 * 0.25) for
    /// <see cref="BakedChart"/>'s scale, then its four picks -- written out here, not computed from it.</summary>
    private static readonly float[] BakedRead = [0.5f, 3f, -1f, 7f, 254f];
    private static readonly float[] UnbakedRead = [0f, -1f, -1f, -1f, -1f];

    /// <summary>Oracle: <see cref="GbufCases"/>, hand-written records, against the lanes they come back in -- the
    /// subject is the shipped <c>cx_gbuf</c> and <c>cx_chartbuf</c>, which only ever round-trip them. The chart
    /// records and what each reads back are hand-written too, each the other's independent half.</summary>
    private static void GbufSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        GbufCase[] cases = GbufCases;
        // uLmPages gates the lightmap SAMPLERS, which this golden never reaches: a const 0 folds them out, and
        // the chart's own record -- the lanes under test -- is read either way
        string chart = ShaderSource.Get("cx_chartbuf.glsl")
            .Replace("uniform int uLmPages;", "const int uLmPages = 0;", StringComparison.Ordinal);
        check.Expect(chart.Contains("const int uLmPages", StringComparison.Ordinal),
            "cx_chartbuf still declares uLmPages as the loose uniform this golden folds out");
        string src = Version + "layout(local_size_x = 1) in;\n" + chart + ShaderSource.Get("cx_gbuf.glsl")
                   + CaseBlocks(GbufIn, GbufOut) + """
                     void main()
                     {
                         int b = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
                         CxGbuf g;
                         g.N = normalize(vec3(uIn[b], uIn[b + 1], uIn[b + 2])); g.flags = uint(uIn[b + 3]); g.viewDepth = uIn[b + 4];
                         g.rough = uIn[b + 5]; g.spec = uIn[b + 6]; g.fresnel = uIn[b + 7]; g.coat = uIn[b + 8]; g.coatRough = uIn[b + 9];
                         g.specC = vec3(uIn[b + 10], uIn[b + 11], uIn[b + 12]); g.slot = int(uIn[b + 13]); g.specT = uIn[b + 14];
                         g.diffT = vec3(uIn[b + 15], uIn[b + 16], uIn[b + 17]); g.lmLum = uIn[b + 18];
                         g.lmMover = vec4(uIn[b + 19], uIn[b + 20], uIn[b + 21], uIn[b + 22]);
                         g.lmShare = vec3(uIn[b + 23], uIn[b + 24], uIn[b + 25]);
                         g.surfFade = vec4(normalize(vec3(uIn[b + 26], uIn[b + 27], uIn[b + 28])), uIn[b + 29]);
                         uvec4 w0, w1;
                         uint w2;
                         CxGbufPack(g, w0, w1, w2);
                         CxGbuf r, x;
                         bool ok = CxGbufRead(w0, w1, g.viewDepth, r);
                         uOut[o + 26] = ok ? 1.0 : 0.0;
                         uOut[o + 27] = CxGbufRead(w0, w1, g.viewDepth * 1.02, x) ? 1.0 : 0.0;
                         uOut[o + 28] = CxGbufRead(uvec4(0u), w1, g.viewDepth, x) ? 1.0 : 0.0;
                         if (!ok) return;
                         uOut[o] = r.N.x; uOut[o + 1] = r.N.y; uOut[o + 2] = r.N.z; uOut[o + 3] = float(r.flags); uOut[o + 4] = r.viewDepth;
                         uOut[o + 5] = r.rough; uOut[o + 6] = r.spec; uOut[o + 7] = r.fresnel; uOut[o + 8] = r.coat; uOut[o + 9] = r.coatRough;
                         uOut[o + 10] = r.specC.r; uOut[o + 11] = r.specC.g; uOut[o + 12] = r.specC.b; uOut[o + 13] = float(r.slot);
                         uOut[o + 14] = r.specT; uOut[o + 15] = r.diffT.r; uOut[o + 16] = r.diffT.g; uOut[o + 17] = r.diffT.b;
                         uOut[o + 18] = r.lmLum; uOut[o + 19] = r.lmMover.x; uOut[o + 20] = r.lmMover.y; uOut[o + 21] = r.lmMover.z;
                         uOut[o + 22] = r.lmMover.w; uOut[o + 23] = r.lmShare.r; uOut[o + 24] = r.lmShare.g; uOut[o + 25] = r.lmShare.b;
                         uOut[o + 29] = r.lmTexel; uOut[o + 30] = r.lmPick.x; uOut[o + 31] = r.lmPick.y; uOut[o + 32] = r.lmPick.z;
                         uOut[o + 33] = r.lmPick.w;
                         CxGbufFade(w2, r);
                         uOut[o + 34] = r.surfFade.x; uOut[o + 35] = r.surfFade.y; uOut[o + 36] = r.surfFade.z; uOut[o + 37] = r.surfFade.w;
                     }
                     """;
        PipelineId pipe = Compute(vk, src, "golden gbuf lanes", out ShaderId cs);
        check.Expect(!pipe.IsNull, "the G-buffer golden's stage builds for the device");
        if (!pipe.IsNull)
        {
            BufferId charts = GbufCharts(vk, log, cases);
            check.Expect(!charts.IsNull, "the chart buffer the slot lane indexes exists");
            if (!charts.IsNull)
            {
                int n = cases.Length;
                var flat = new float[n * GbufIn];
                for (int k = 0; k < n; k++) GbufRow(cases[k]).CopyTo(flat, k * GbufIn);
                float[]? got = RunCases(vk, log, pipe, n, flat, n * GbufOut, "golden gbuf lanes",
                    [new BufferBinding(SsboSlots.Chart, charts)]);
                check.Expect(got != null, "the G-buffer golden dispatches and reads back");
                if (got != null) GbufJudge(check, log, cases, got);
            }
            vk.Destroy(charts);
        }
        vk.Destroy(pipe);
        vk.Destroy(cs);
    }

    /// <summary>One case's lanes as the stage reads them, padded to <see cref="GbufIn"/>.</summary>
    private static float[] GbufRow(in GbufCase c)
    {
        double[] row =
        [
            .. c.N, c.Flags, c.ViewDepth, c.Rough, c.Spec, c.Fresnel, c.Coat, c.CoatRough, .. c.SpecC, c.Slot,
            c.SpecT, .. c.DiffT, c.LmLum, .. c.LmMover, .. c.LmShare, .. c.SurfFade,
        ];
        var f = new float[GbufIn];
        for (int i = 0; i < row.Length && i < GbufIn; i++) f[i] = (float)row[i];
        return f;
    }

    /// <summary>The chart buffer, every slot's record zero but the ones the cases name: the last slot baked, and
    /// each other case's slot explicitly unbaked. Sized for the whole ownership table, so the slot lane's own
    /// ceiling is a real index into it.</summary>
    private static BufferId GbufCharts(VkBackend vk, ExtenderLog log, GbufCase[] cases)
    {
        long bytes = (long)AlloyRenderer.MaxOwned * ChartFloats * sizeof(float);
        BufferId b = vk.CreateBuffer(new BufferDesc(bytes, BufferUse.Storage | BufferUse.CopyDst, MemoryKind.Device,
                                                    "golden gbuf charts"));
        if (b.IsNull) return b;
        var cl = new CommandList("golden gbuf charts", 4096, log);
        cl.ZeroBuffer(b);
        foreach (GbufCase c in cases)
        {
            int slot = c.Slot & GbufSlotMask;
            if (slot >= AlloyRenderer.MaxOwned) continue;
            float[] rec = slot == LastSlot ? BakedChart : UnbakedChart;
            cl.UpdateBuffer(b, (long)slot * ChartFloats * sizeof(float), MemoryMarshal.AsBytes(rec.AsSpan()));
        }
        if (vk.Submit(cl)) return b;
        vk.Destroy(b);
        return default;
    }

    /// <summary>The slot lane's width, as cx_gbuf's CX_GB_SLOT_BITS gives it: a slot past it comes back folded.</summary>
    private const int GbufSlotMask = (1 << 18) - 1;

    /// <summary>Each lane against the case it came from, at the precision the format gives that lane.</summary>
    private static void GbufJudge(Checker check, ExtenderLog log, GbufCase[] cases, float[] got)
    {
        double worstN = 0;
        for (int k = 0; k < cases.Length; k++)
        {
            GbufCase c = cases[k];
            ReadOnlySpan<float> r = got.AsSpan(k * GbufOut, GbufOut);
            string tag = $"gbuf case {k}";
            check.Expect(r[26] == 1f && r[27] == 0f && r[28] == 0f,
                $"{tag}: the record reads at its own depth and not at a moved one or behind a cleared G0 "
              + $"(got {r[26]:F0}, {r[27]:F0}, {r[28]:F0}, want 1, 0, 0)");
            if (r[26] != 1f) continue;
            worstN = Math.Max(worstN, Angle(r[..3], c.N));
            check.Expect(Angle(r[..3], c.N) <= NormalDeg, $"{tag}: the normal came back {Angle(r[..3], c.N):F4} deg off");
            Near(check, $"{tag}: flags", r[3], c.Flags, 0);
            Near(check, $"{tag}: viewDepth", r[4], c.ViewDepth, c.ViewDepth / 2048);
            Near(check, $"{tag}: rough", r[5], c.Rough, Unorm8);
            Near(check, $"{tag}: spec", r[6], c.Spec, Unorm8);
            Near(check, $"{tag}: fresnel", r[7], c.Fresnel, Unorm8);
            Near(check, $"{tag}: coat", r[8], c.Coat, 0.5 / 127 + 1e-6);
            Near(check, $"{tag}: coatRough", r[9], c.CoatRough, Sq(c.CoatRough, 127));
            for (int j = 0; j < 3; j++) Near(check, $"{tag}: specC", r[10 + j], c.SpecC[j], Sq(c.SpecC[j], 255));
            Near(check, $"{tag}: slot", r[13], c.Slot & GbufSlotMask, 0);
            Near(check, $"{tag}: specT", r[14], c.SpecT, Math.Abs(c.SpecT) / 2048 + 1e-7);
            Near(check, $"{tag}: lmLum", r[18], c.LmLum, Math.Abs(c.LmLum) / 2048 + 1e-7);
            for (int j = 0; j < 4; j++) Near(check, $"{tag}: lmMover", r[19 + j], c.LmMover[j], Unorm8);
            // the weights go out as shared-exponent RGB: nine mantissa bits against the block's largest channel
            for (int j = 0; j < 3; j++) Near(check, $"{tag}: diffT", r[15 + j], c.DiffT[j], Max(c.DiffT) / 400 + 1e-9);
            for (int j = 0; j < 3; j++) Near(check, $"{tag}: lmShare", r[23 + j], c.LmShare[j], Max(c.LmShare) / 400 + 1e-9);
            // G2: the fade in 8 bits over [0, 10], and the geometric normal at G0's own precision where there is one
            Near(check, $"{tag}: surfFade", r[37], c.SurfFade[3], 0.5 / 255 * 10.0 + 1e-6);
            if (c.SurfFade[3] > 0)
            {
                double ga = Angle(r[34..37], c.SurfFade);
                worstN = Math.Max(worstN, ga);
                check.Expect(ga <= NormalDeg, $"{tag}: the geometric normal came back {ga:F4} deg off");
            }
            float[] want = (c.Slot & GbufSlotMask) == LastSlot ? BakedRead : UnbakedRead;
            check.Expect(r[29..34].SequenceEqual(want),
                $"{tag}: slot {c.Slot & GbufSlotMask}'s chart read back [{string.Join(", ", r[29..34].ToArray())}], "
              + $"want [{string.Join(", ", want)}]");
        }
        log.Info($"GpuGolden: gbuf lanes, {cases.Length} cases, normal within {worstN:F4} deg");
    }

    /// <summary>Half a step of an 8-bit unorm lane, and the degrees an octahedral normal may be off by.</summary>
    private const double Unorm8 = 0.5 / 255 + 1e-6, NormalDeg = 0.06;

    /// <summary>Half a step of a sqrt-encoded unorm lane at <paramref name="steps"/>, which widens toward 0.</summary>
    private static double Sq(double x, int steps) => 2.0 * Math.Sqrt(x) * 0.5 / steps + 0.25 / (steps * (double)steps) + 1e-6;

    private static double Max(double[] v) => Math.Max(v[0], Math.Max(v[1], v[2]));

    private static void Near(Checker check, string what, double got, double want, double tol)
        => check.Expect(Math.Abs(got - want) <= tol, $"{what} came back {got:G6} for {want:G6} (tolerance {tol:G3})");

    /// <summary>The angle in degrees between a read-back direction and the case's own, both normalized.</summary>
    private static double Angle(ReadOnlySpan<float> got, double[] want)
    {
        double wl = Math.Sqrt(want[0] * want[0] + want[1] * want[1] + want[2] * want[2]);
        double gl = Math.Sqrt(got[0] * (double)got[0] + got[1] * (double)got[1] + got[2] * (double)got[2]);
        if (wl < 1e-9 || gl < 1e-9) return 180.0;
        double d = (got[0] * want[0] + got[1] * want[1] + got[2] * want[2]) / (wl * gl);
        return Math.Acos(Math.Clamp(d, -1.0, 1.0)) * 180.0 / Math.PI;
    }
}
