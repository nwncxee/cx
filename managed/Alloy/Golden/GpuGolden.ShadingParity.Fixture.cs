using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The shading-parity goldens' rig scene: one plain wall facing the camera head-on, one light two metres in front of
/// its centre, and every pixel's record set by hand so a single term of the walk reaches the frame.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The light every parity scene hangs in front of the wall's centre: position and reach, white.</summary>
    private static readonly Vector4 ParityLamp = new(0f, 0f, 2f, 20f);

    /// <summary>A parity scene: the wall's pixels face <paramref name="n"/> and read slot 3, opaque unless
    /// <paramref name="alpha"/> says otherwise; the lamp's slot word is <paramref name="word"/> (-1 casts no shadow,
    /// -2 the ambient class, 0 cube slot 0). <paramref name="edit"/> sets each pixel's weights.</summary>
    private static DeferredRigScene ParityScene(Vector3 n, float word, float alpha, Func<RigPixel, RigPixel> edit)
    {
        DeferredRigScene sc = DwCamera(new Vector3(0f, 0f, 10f), Vector3.Zero);
        DwSurfaces(sc, [new(Vector3.Zero, Vector3.UnitZ, (_, _) => DwStone)]);
        for (int i = 0; i < sc.Pixels.Length; i++)
        {
            if (sc.Pixels[i].Flags == 0f) continue;
            RigPixel p = sc.Pixels[i];
            p.N = n; p.Slot = 3; p.SpecT = 0f; p.DiffT = Vector3.Zero; p.AmbT = Vector3.Zero;
            p.ShSlots = new Vector4(0f, 1f, 2f, 3f); p.ShVis = Vector4.Zero;
            sc.Pixels[i] = edit(p);
        }
        sc.Lights = [ParityLamp.X, ParityLamp.Y, ParityLamp.Z, ParityLamp.W, 1f, 1f, 1f, word];
        sc.Cubes = word >= 0f ? [ParityLamp] : [];
        sc.PartAlpha = [1f, 1f, 1f, alpha];
        sc.Plain = true;
        return sc;
    }

    /// <summary>The shipped <c>CxWrap</c> over <paramref name="cases"/>, three floats each (N.L, wrap, normal-mapped);
    /// null with the check failed when the library lost it or the stage was refused.</summary>
    private static float[]? WrapCases(VkBackend vk, Checker check, ExtenderLog log, float[] cases)
    {
        string lf = ShaderSource.Get("cx_lightfrag.glsl");
        string wrap = Function(lf, "float CxWrap(");
        check.Expect(wrap.Length > 0, "cx_lightfrag declares CxWrap");
        if (wrap.Length == 0) return null;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(3, 1) + Declaration(lf, "CX_SOFTEN")
            + "bool cxFragNormal = false;\n" + wrap + """
            void main()
            {
                uint i = gl_WorkGroupID.x;
                cxFragNormal = uIn[i * uint(CX_GOLD_IN) + 2u] > 0.5;
                uOut[i] = CxWrap(uIn[i * uint(CX_GOLD_IN)], uIn[i * uint(CX_GOLD_IN) + 1u]);
            }
            """, "golden wrap");
        int n = cases.Length / 3;
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, n, cases, n, "golden wrap");
        vk.Destroy(pipe);
        check.Expect(got != null, "the CxWrap stage was refused (logged above)");
        return got;
    }

    /// <summary>The prefix a <c>CxSunLight</c> stage compiles against: a white sun along +Z, no wrap,
    /// and the shipped softening, translucence and sun functions. Empty when the library lost one.</summary>
    private static string SunStage(string lf)
    {
        string wrap = Function(lf, "float CxWrap("), sun = Function(lf, "vec3 CxSunLight("), through = Function(lf, "vec3 CxTranslucent(");
        if (wrap.Length == 0 || sun.Length == 0 || through.Length == 0) return "";
        return Declaration(lf, "CX_SOFTEN") + """
            const vec3 uAreaDiffuse = vec3(1.0), uAreaDir = vec3(0.0, 0.0, 1.0);
            const float uWrapN = 0.0;
            bool cxFragNormal = false;
            float cxTransl = 0.0;
            """ + wrap + through + sun;
    }

    /// <summary>The fixed march every displacement case takes: the parallax golden's first coordinate, frame and eye.</summary>
    private static readonly DisplaceCase DisplaceWall = new(0.3f, 0.7f, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
                                                            Vector3.Normalize(new Vector3(0.6f, 0.2f, 0.7f)), 3f, 1080f);

    /// <summary>The shipped <c>CxDisplace</c> on <see cref="DisplaceWall"/> over the analytic height field, once per
    /// (offset, multiplier) pair in <paramref name="om"/>; two floats back per case, null with the check failed.</summary>
    private static float[]? DisplaceRuns(VkBackend vk, Checker check, ExtenderLog log, float[] om)
    {
        string march = Function(ShaderSource.Get("cx_ownlit_synth.glsl"), "vec2 CxDisplace(");
        string steps = Declaration(ShaderSource.Get("cx_ownlit_head.glsl"), "CX_DISPLACE_STEPS");
        check.Expect(march.Length > 0 && steps.Length > 0, "cx_ownlit_synth declares CxDisplace and cx_ownlit_head CX_DISPLACE_STEPS");
        if (march.Length == 0 || steps.Length == 0) return null;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + steps
            + $"float cxH(vec2 p) {{ return {HeightGlsl}; }}\n"
            + "#define textureGrad(t, p, a, b) vec4(cxH(p))\n" + march + CaseBlocks(2, 2) + """
            void main()
            {
                uint i = gl_WorkGroupID.x;
                vec2 r = CxDisplace(vec2(0.3, 0.7), mat3(1.0), normalize(vec3(0.6, 0.2, 0.7)), 3.0, 1080.0, vec2(0.0), vec2(0.0),
                                    vec2(uIn[i * uint(CX_GOLD_IN)], uIn[i * uint(CX_GOLD_IN) + 1u]));
                uOut[i * uint(CX_GOLD_OUT)] = r.x; uOut[i * uint(CX_GOLD_OUT) + 1u] = r.y;
            }
            """, "golden displacement terms");
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, om.Length / 2, om, om.Length, "golden displacement terms");
        vk.Destroy(pipe);
        check.Expect(got != null, "the displacement stage was refused (logged above)");
        return got;
    }

    /// <summary>Case <paramref name="k"/> of a <see cref="DisplaceRuns"/> answer lands on <paramref name="w"/>.</summary>
    private static bool DisplacedTo(float[] got, int k, (double U, double V) w)
        => Math.Abs(got[k * 2] - w.U) <= DisplaceTol && Math.Abs(got[k * 2 + 1] - w.V) <= DisplaceTol;

    /// <summary>The wall's centre pixel of <paramref name="sc"/>'s frame, or null with the check failed.</summary>
    private static Vector3? ParityCentre(VkBackend vk, Checker check, ExtenderLog log, DeferredRigScene sc, string what)
    {
        if (DwFrame(vk, check, log, sc, what) is not { } px) return null;
        int at = ((DwH / 2) * DwW + DwW / 2) * 4;
        return new Vector3(px[at], px[at + 1], px[at + 2]);
    }
}
