using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The parallax golden: <c>CxDisplace</c>, the march Alloy's lit stage walks a height map with, against the stock
/// engine's own <c>inc_displacement.shd</c>. Both sides are handed the same analytic height field, so the march
/// is the only thing that can differ -- angle, distance and screen height set the step count, and the tangent
/// frame orders the march's axes.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The height field both sides evaluate, as GLSL and (in <see cref="Height"/>) as C#: a ripple
    /// steep enough that a wrong step count or a swapped axis moves the answer, and smooth everywhere.</summary>
    private const string HeightGlsl = "0.5 + 0.4 * sin(12.566371 * p.x) * cos(18.849556 * p.y)";

    private static double Height(double x, double y) => 0.5 + 0.4 * Math.Sin(12.566371 * x) * Math.Cos(18.849556 * y);

    /// <summary>One march to check: the texture coordinate, the tangent frame's three columns (T, B, N), the
    /// surface-to-eye direction, the view depth and the screen height in pixels.</summary>
    private readonly record struct DisplaceCase(double U, double V, double[] T, double[] B, double[] N,
                                                double[] Eye, double Depth, double ScreenH);

    /// <summary>Hand-written marches: head-on and grazing, two tangent frames that permute the axes (so a
    /// transpose the wrong way round shows), and near and far enough that the step count falls to one and to
    /// none. Floats of 16 per case, as <c>CX_GOLD_IN</c> says.</summary>
    private static readonly DisplaceCase[] DisplaceCases =
    [
        new(0.3, 0.7, [1, 0, 0], [0, 1, 0], [0, 0, 1], [0.7071068, 0.0, 0.7071068], 3.0, 1080),
        new(0.15, 0.4, [1, 0, 0], [0, 1, 0], [0, 0, 1], [0.0, -0.9848078, 0.1736482], 10.0, 1080),
        new(0.6, 0.2, [0, 1, 0], [-1, 0, 0], [0, 0, 1], [0.5, 0.3, 0.8124038], 6.0, 2160),
        new(0.9, 0.55, [1, 0, 0], [0, 0, 1], [0, -1, 0], [0.3, -0.8, 0.5196152], 40.0, 1080),
        new(0.42, 0.13, [1, 0, 0], [0, 1, 0], [0, 0, 1], [0.5, 0.0, 0.8660254], 200.0, 1080),
    ];

    /// <summary>Floats in and out per case: the frame and the march's terms, then the coordinate it lands on.</summary>
    private const int DisplaceIn = 16, DisplaceOut = 2;

    /// <summary>Oracle: <see cref="StockDisplace"/>, which is <c>inc_displacement.shd</c>'s own march written out
    /// here -- a second shader, never ours. The subject is the shipped <c>CxDisplace</c> and the step count the
    /// shipped <c>CX_DISPLACE_STEPS</c> gives it, both read out of the embedded GLSL.</summary>
    private static void ParallaxSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string synth = ShaderSource.Get("cx_ownlit_synth.glsl"), head = ShaderSource.Get("cx_ownlit_head.glsl");
        string march = Function(synth, "vec2 CxDisplace("), steps = Declaration(head, "CX_DISPLACE_STEPS");
        check.Expect(march.Length > 0 && steps.Length > 0,
            "cx_ownlit_synth declares CxDisplace and cx_ownlit_head CX_DISPLACE_STEPS");
        if (march.Length == 0 || steps.Length == 0) return;

        // the march samples its height map through textureGrad: the macro hands both sides the same analytic
        // field instead, and with no `t` in the replacement the sampler itself never reaches the compiler
        string src = Version + "layout(local_size_x = 1) in;\n" + steps
                   + $"float cxH(vec2 p) {{ return {HeightGlsl}; }}\n"
                   + "#define textureGrad(t, p, a, b) vec4(cxH(p))\n" + march
                   + CaseBlocks(DisplaceIn, DisplaceOut) + """
                     void main()
                     {
                         int b = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
                         mat3 tsb = mat3(uIn[b + 2], uIn[b + 3], uIn[b + 4], uIn[b + 5], uIn[b + 6], uIn[b + 7],
                                         uIn[b + 8], uIn[b + 9], uIn[b + 10]);
                         vec2 r = CxDisplace(vec2(uIn[b], uIn[b + 1]), tsb, vec3(uIn[b + 11], uIn[b + 12], uIn[b + 13]),
                                             uIn[b + 14], uIn[b + 15], vec2(0.0), vec2(0.0));
                         uOut[o] = r.x; uOut[o + 1] = r.y;
                     }
                     """;
        PipelineId pipe = Compute(vk, src, "golden parallax", out ShaderId cs);
        check.Expect(!pipe.IsNull, "the parallax golden's stage builds for the device");
        if (!pipe.IsNull)
        {
            int n = DisplaceCases.Length;
            var cases = new float[n * DisplaceIn];
            for (int k = 0; k < n; k++)
            {
                DisplaceCase c = DisplaceCases[k];
                double[] row = [c.U, c.V, .. c.T, .. c.B, .. c.N, .. c.Eye, c.Depth, c.ScreenH];
                for (int i = 0; i < row.Length; i++) cases[k * DisplaceIn + i] = (float)row[i];
            }
            float[]? got = RunCases(vk, log, pipe, n, cases, n * DisplaceOut, "golden parallax");
            check.Expect(got != null, "the parallax golden dispatches and reads back");
            if (got != null)
            {
                double worst = 0;
                int moved = 0;
                for (int k = 0; k < n; k++)
                {
                    DisplaceCase c = DisplaceCases[k];
                    (double wu, double wv) = StockDisplace(c);
                    double err = Math.Max(Math.Abs(got[k * DisplaceOut] - wu), Math.Abs(got[k * DisplaceOut + 1] - wv));
                    worst = Math.Max(worst, err);
                    if (wu != c.U || wv != c.V) moved++;
                    check.Expect(err <= DisplaceTol,
                        $"parallax case {k}: ({got[k * DisplaceOut]:F6}, {got[k * DisplaceOut + 1]:F6}), "
                      + $"inc_displacement.shd says ({wu:F6}, {wv:F6})");
                }
                check.Expect(moved >= n - 1, $"only {moved} of {n} parallax cases displaced at all -- the fixture stopped marching");
                log.Info($"GpuGolden: parallax, {n} cases, {moved} displaced, within {worst:E2} of inc_displacement.shd");
            }
        }
        vk.Destroy(pipe);
        vk.Destroy(cs);
    }

    /// <summary>What a march may differ by and still be the same march: the GPU runs it in single precision over
    /// up to 32 dependent steps, where the oracle runs in double.</summary>
    private const double DisplaceTol = 1e-4;

    /// <summary><c>inc_displacement.shd</c>'s <c>DisplaceTextureCoords</c> at SHADER_QUALITY_MODE 2, offset 0 and
    /// multiplier 0 (so the 1.0 default) with the identity texture matrix -- the oracle, written from the stock
    /// shader. Returns the case's own coordinate where the stock march takes no step.</summary>
    private static (double U, double V) StockDisplace(in DisplaceCase c)
    {
        double[] ng = c.N;
        double m = 1.0 - 0.5 * Dot(c.Eye, ng);   // 1 + 0.5 * dot(vViewToSurface_n, ng)
        m *= m / (c.Depth * 0.125);              // -vPosView.z
        double it = 32.0 * Math.Clamp(m * c.ScreenH / 1080.0, 0.0, 1.0);
        if (!(it > 0.5)) return (c.U, c.V);
        double[] vd = [Dot(c.T, c.Eye), Dot(c.B, c.Eye), Dot(ng, c.Eye)];   // transpose(mTSB) * -vViewToSurface_n
        it = Math.Floor(it + 0.5);
        double seg = 1.0 / it;
        double zh = 0.5 * vd[2] + 0.5;
        double k = 0.05 * ((1.0 - vd[2]) / (zh * zh) + 1.0);
        double dx = vd[0] * k, dy = vd[1] * k;
        double cur = (1.0 - Height(c.U, c.V)) * seg;
        for (int i = (int)it; i > 1; i--)
        {
            double delta = 1.0 - Height(c.U - dx * cur, c.V - dy * cur) - cur;
            if (delta < 0.0) { delta /= cur; seg *= 0.5; }
            else delta /= 1.0 - cur;
            cur += delta * seg;
        }
        return (c.U - dx * cur, c.V - dy * cur);
    }

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}
