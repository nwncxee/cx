using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The late rain golden (approved-spec, A1-A3, B1-B3): where the fall and the crowns draw for each resolve, the shipped
/// <c>CxWxOver</c> the TAA stage composites rain with, and the shipped <c>CxSplashHidden</c> a late crown is depth-tested
/// with. Oracle: 0.4 under (0.2, a 0.25) at exposure 1 and 2, a wall at 5 m before a crown at 8 m, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>Where the fall and drips draw under <paramref name="fsr"/> and <paramref name="taa"/>: 0 in the scene,
    /// 1 after the upscale, 2 after the TAA resolve; -1 until the choice is wired.</summary>
    static partial void RainDrawsWhere(bool fsr, bool taa, ref int where);

    /// <summary>Where the crowns draw, in <see cref="RainDrawsWhere"/>'s numbers; -1 until the choice is wired.</summary>
    static partial void SplashDrawsWhere(bool fsr, bool taa, ref int where);

    private static void RainLateApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        int off = -1, fsr = -1, taa = -1;
        RainDrawsWhere(false, false, ref off);
        RainDrawsWhere(true, false, ref fsr);
        RainDrawsWhere(false, true, ref taa);

        // Given rain and TAA live
        // When the frame draws
        // Then fall and drips draw after the TAA resolve; under FSR after the upscale; with neither, in the scene
        check.Expect(off == 0 && fsr == 1 && taa == 2,
            $"late rain A1: the fall draws at {off} with no resolve (want 0, the scene), {fsr} under FSR (want 1, after the upscale) and {taa} under TAA (want 2, after its resolve)");

        string tm = ShaderSource.Get("cx_tonemap.glsl");
        string tone = Function(tm, "vec3 CxShoulder(") + Function(tm, "vec3 CxAces(") + Function(tm, "vec3 CxNeutral(")
                    + Function(tm, "vec3 CxTonemap(");
        string over = Function(ShaderSource.Get("cx_wx_over.glsl"), "vec3 CxWxOver(");
        bool have = over.Length > 0 && tone.Length > 0;
        check.Expect(have, "late rain: cx_wx_over declares CxWxOver");
        if (have)
        {
            PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(8, 1) + tone + over + """
                void main()
                {
                    uint i = gl_WorkGroupID.x * uint(CX_GOLD_IN);
                    vec4 wx = vec4(uIn[i + 1u], uIn[i + 1u], uIn[i + 1u], uIn[i + 2u]);
                    vec3 v = CxWxOver(vec3(uIn[i]), wx, uIn[i + 3u], int(uIn[i + 4u]), uIn[i + 5u], uIn[i + 6u]);
                    uOut[gl_WorkGroupID.x * uint(CX_GOLD_OUT)] = v.g;
                }
                """, "golden late rain");
            float[] cases = [0.4f, 0f, 0f, 1f, 1f, 0.8f, 0f, 0f,
                             0.4f, 0.2f, 0.25f, 1f, 0f, 0f, 0f, 0f,
                             0.4f, 0.2f, 0.25f, 2f, 0f, 0f, 0f, 0f];
            float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 3, cases, 3, "golden late rain");
            vk.Destroy(pipe);
            check.Expect(got != null, "late rain: the stage was refused (logged above)");
            if (got != null)
            {
                // Given a TAA pixel and no rain over it
                // When the TAA stage writes it out
                // Then it is the resolved value unchanged
                Near(check, "late rain A2: 0.4 under no rain", got[0], 0.4, 1e-6);

                // Given a TAA pixel of display-linear 0.4 under rain of premultiplied (0.2, a 0.25), exposure 1, no curve
                // When the TAA stage writes it out
                // Then it is display-linear 0.5 (0.4 x 0.75 + 0.2) and at exposure 2, 0.7
                Near(check, "late rain A3: 0.4 under (0.2, a 0.25) at exposure 1", got[1], 0.5, 1e-5);
                Near(check, "late rain A3: 0.4 under (0.2, a 0.25) at exposure 2", got[2], 0.7, 1e-5);
            }
        }

        int sOff = -1, sFsr = -1, sTaa = -1;
        SplashDrawsWhere(false, false, ref sOff);
        SplashDrawsWhere(true, false, ref sFsr);
        SplashDrawsWhere(false, true, ref sTaa);

        // Given rain and FSR or TAA live
        // When the frame draws
        // Then the crowns draw with the late rain; with neither, in the scene as today
        check.Expect(sOff == 0 && sFsr == 1 && sTaa == 2,
            $"late rain B1: the crowns draw at {sOff} with no resolve (want 0), {sFsr} under FSR (want 1) and {sTaa} under TAA (want 2)");

        string sf = ShaderSource.Get("cx_splash_f.glsl");
        string hidden = Function(sf, "bool CxSplashHidden("), hide = Declaration(sf, "CX_SPLASH_HIDE");
        have = hidden.Length > 0 && hide.Length > 0;
        check.Expect(have, "late rain: cx_splash_f declares CxSplashHidden and CX_SPLASH_HIDE");
        if (!have) return;
        PipelineId sp = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(2, 1) + hide + hidden + """
            void main()
            {
                uint i = gl_WorkGroupID.x * uint(CX_GOLD_IN);
                uOut[gl_WorkGroupID.x * uint(CX_GOLD_OUT)] = CxSplashHidden(uIn[i], uIn[i + 1u]) ? 1.0 : 0.0;
            }
            """, "golden late splash");
        float[] depths = [5f, 8f, 5f, 5.01f];
        float[]? hid = sp.IsNull ? null : RunCases(vk, log, sp, 2, depths, 2, "golden late splash");
        vk.Destroy(sp);
        check.Expect(hid != null, "late rain: the splash stage was refused (logged above)");
        if (hid == null) return;

        // Given a crown 8 m away behind a wall 5 m away
        // When it draws after the upscale
        // Then it is discarded
        check.Expect(hid[0] == 1f, $"late rain B2: a crown at 8 m behind a wall at 5 m is hidden (got {hid[0]})");

        // Given a crown lying on the surface 5 m away
        // When it draws after the upscale
        // Then it is kept
        check.Expect(hid[1] == 0f, $"late rain B3: a crown at 5.01 m on a surface at 5 m is kept (got {hid[1]})");
    }
}
