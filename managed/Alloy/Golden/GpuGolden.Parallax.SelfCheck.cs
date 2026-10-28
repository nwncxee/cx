using System;
using System.Numerics;
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
    private readonly record struct DisplaceCase(float U, float V, Vector3 T, Vector3 B, Vector3 N,
                                                Vector3 Eye, float Depth, float ScreenH);

    /// <summary>Hand-written marches: head-on and grazing, two tangent frames that permute the axes (so a
    /// transpose the wrong way round shows), and near and far enough that the step count falls to one and to
    /// none. Floats of 16 per case, as <c>CX_GOLD_IN</c> says.</summary>
    private static readonly DisplaceCase[] DisplaceCases =
    [
        new(0.3f, 0.7f, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(0.7071068f, 0f, 0.7071068f), 3f, 1080f),
        new(0.15f, 0.4f, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(0f, -0.9848078f, 0.1736482f), 10f, 1080f),
        new(0.6f, 0.2f, new(0, 1, 0), new(-1, 0, 0), new(0, 0, 1), new(0.5f, 0.3f, 0.8124038f), 6f, 2160f),
        new(0.9f, 0.55f, new(1, 0, 0), new(0, 0, 1), new(0, -1, 0), new(0.3f, -0.8f, 0.5196152f), 40f, 1080f),
        new(0.42f, 0.13f, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(0.5f, 0f, 0.8660254f), 200f, 1080f),
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

        // the macro drops its `t`, so no height sampler reaches the compiler and both sides march one field
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
                int b = k * DisplaceIn;
                cases[b] = c.U;
                cases[b + 1] = c.V;
                Lanes(cases, b + 2, c.T);
                Lanes(cases, b + 5, c.B);
                Lanes(cases, b + 8, c.N);
                Lanes(cases, b + 11, c.Eye);
                cases[b + 14] = c.Depth;
                cases[b + 15] = c.ScreenH;
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

    /// <summary>A direction's three components into the case row at <paramref name="at"/>.</summary>
    private static void Lanes(float[] row, int at, Vector3 v)
    {
        row[at] = v.X;
        row[at + 1] = v.Y;
        row[at + 2] = v.Z;
    }

    /// <summary>What a march may differ by and still be the same march: the GPU runs it in single precision over
    /// up to 32 dependent steps, where the oracle runs in double.</summary>
    private const double DisplaceTol = 1e-4;

    /// <summary><c>inc_displacement.shd</c>'s <c>DisplaceTextureCoords</c> at SHADER_QUALITY_MODE 2, offset 0 and
    /// multiplier 0 (so the 1.0 default) with the identity texture matrix -- the oracle, written from the stock
    /// shader. Returns the case's own coordinate where the stock march takes no step.</summary>
    private static (double U, double V) StockDisplace(in DisplaceCase c)
    {
        double m = 1.0 - 0.5 * Dot(c.Eye, c.N);   // 1 + 0.5 * dot(vViewToSurface_n, ng)
        m *= m / (c.Depth * 0.125);               // -vPosView.z
        double it = 32.0 * Math.Clamp(m * c.ScreenH / 1080.0, 0.0, 1.0);
        if (!(it > 0.5)) return (c.U, c.V);
        // transpose(mTSB) * -vViewToSurface_n: the frame's columns against the eye direction
        double vdT = Dot(c.T, c.Eye), vdB = Dot(c.B, c.Eye), vdN = Dot(c.N, c.Eye);
        it = Math.Floor(it + 0.5);
        double seg = 1.0 / it;
        double zh = 0.5 * vdN + 0.5;
        double k = 0.05 * ((1.0 - vdN) / (zh * zh) + 1.0);
        double dx = vdT * k, dy = vdB * k;
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

    /// <summary>A dot product in double, so the oracle's precision is the oracle's and not the march's.</summary>
    private static double Dot(Vector3 a, Vector3 b) => (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z;
}
