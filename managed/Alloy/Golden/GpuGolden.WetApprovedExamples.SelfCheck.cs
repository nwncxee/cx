using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The wet-surface goldens (approved-spec, E1 to E7): the shipped <c>cx_wx_look</c> library run on the device over
/// hand-picked floors. Oracles: the floors' brightness, maps and heights, and the sheen, mirror and contrast bounds,
/// all written by hand here; the surface read in each case is the library's answer, never a copy of its formula.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The <c>wx.filmrough</c> default, 0.22 perceptual, as the alpha the lit stage is handed.</summary>
    private const float WxKnobAlpha = 0.22f * 0.22f;

    /// <summary>A sharp mirror image: a film lobe at or under this alpha (0.32 perceptual) draws a lamp as an image.</summary>
    private const float WxMirrorAlpha = 0.1f;

    /// <summary>A soft sheen: a film lobe at or over this alpha (0.45 perceptual) spreads a lamp into a glow.</summary>
    private const float WxSheenAlpha = 0.2f;

    /// <summary>Linear albedo luminance of the fixture floors: a dark cobble, a light stone, an almost black floor.</summary>
    private const float WxDarkLum = 0.04f, WxLightLum = 0.35f, WxBlackLum = 0.005f;

    /// <summary>Roughness: stock no-PBR (the generated maximum), a polished map's texel, a rough map's texel.</summary>
    private const float WxStockRough = 0.55f, WxPolished = 0.08f, WxRoughMap = 0.85f;

    /// <summary>The <c>wx.puddledepth</c> default (m) and a mid puddles knob, the levels every pooling case runs at.</summary>
    private const float WxDepth = 0.03f, WxPuddles = 0.5f;

    /// <summary>Floats per film case in (luminance, roughness, known, knob alpha) and out (weight, alpha, flatten).</summary>
    private const int WxFilmIn = 4, WxFilmOut = 3;

    /// <summary>The film cases, in order: a no-PBR dark cobble, a no-PBR light stone, a no-PBR almost black floor,
    /// a dark floor its maps call polished, and a dark floor its maps call rough.</summary>
    private static readonly float[] WxFilms =
    [
        WxDarkLum, WxStockRough, 0f, WxKnobAlpha,
        WxLightLum, WxStockRough, 0f, WxKnobAlpha,
        WxBlackLum, WxStockRough, 0f, WxKnobAlpha,
        WxDarkLum, WxPolished, 1f, WxKnobAlpha,
        WxDarkLum, WxRoughMap, 1f, WxKnobAlpha,
    ];

    private static void WetSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string f0 = Declaration(ShaderSource.Get("cx_ssr.glsl"), "CX_DIELECTRIC_F0");
        string lib = f0 + ShaderSource.Get("cx_wx_look.glsl");
        string soak = ShaderSource.Get("cx_wx_soak.glsl");
        bool have = f0.Length > 0 && Function(lib, "vec3 CxWxFilmLook(").Length > 0 && Function(lib, "float CxWxMicro(").Length > 0;
        check.Expect(have, "wet E1-E7: cx_ssr declares CX_DIELECTRIC_F0 and cx_wx_look CxWxFilmLook and CxWxMicro");
        if (!have) return;

        int n = WxFilms.Length / WxFilmIn;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + soak + lib + CaseBlocks(WxFilmIn, WxFilmOut) + """
            void main()
            {
                uint a = gl_WorkGroupID.x * uint(CX_GOLD_IN), b = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                vec3 f = CxWxFilmLook(1.0, uIn[a], uIn[a + 1u], uIn[a + 2u] > 0.5, uIn[a + 3u]);
                uOut[b] = f.x; uOut[b + 1u] = f.y; uOut[b + 2u] = f.z;
            }
            """, "golden wet film");
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, n, WxFilms, n * WxFilmOut, "golden wet film");
        vk.Destroy(pipe);
        check.Expect(got != null, "wet E1-E5: the film stage was refused (logged above)");
        if (got == null) return;
        float W(int c) => got[c * WxFilmOut];
        float A(int c) => got[c * WxFilmOut + 1];
        float Lamp(int c) => W(c) / (A(c) * A(c));

        // Given a no-PBR dark cobble floor and a no-PBR light stone floor side by side
        // When rain leaves standing water on both
        // Then the light stone reflects the lamp above it visibly more than the dark cobble
        check.Expect(Lamp(1) >= 2f * Lamp(0) && A(1) < A(0),
            $"wet E1: light stone lamp {Lamp(1):F2} (alpha {A(1):F3}), dark cobble {Lamp(0):F2} (alpha {A(0):F3}): the light one must reflect at least twice as much, sharper");

        // Given a no-PBR floor that is almost black
        // When rain leaves standing water on it
        // Then it still shows a faint wet sheen, not a dry matte surface
        check.Expect(W(2) >= 0.1f && A(2) <= 0.5f, $"wet E1b: almost black floor's film weight {W(2):F3}, alpha {A(2):F3}: a sheen is at least 0.1 of a film, alpha 0.5 at most");

        // Given a no-PBR dark cobble floor under a lamp
        // When rain leaves standing water on it
        // Then the lamp shows on it as a soft wet sheen, never as a sharp mirror image
        check.Expect(A(0) >= WxSheenAlpha && W(0) > 0f, $"wet E2: dark cobble film alpha {A(0):F3}, weight {W(0):F3}: a sheen is alpha {WxSheenAlpha} or more");

        log.Info(FormattableString.Invariant($"GpuGolden: wet film, weight/alpha dark {W(0):F2}/{A(0):F3} light {W(1):F2}/{A(1):F3} black {W(2):F2}/{A(2):F3} polished {W(3):F2}/{A(3):F3} rough {W(4):F2}/{A(4):F3}"));
        WetShowThrough(vk, check, log, soak, W(0), got[2]);

        // Given a dark floor whose PBR maps say it is polished smooth
        // When rain leaves standing water on it
        // Then the lamp shows in it as a sharp mirror image
        check.Expect(A(3) <= WxMirrorAlpha && W(3) >= 0.9f, $"wet E4: polished dark floor film alpha {A(3):F3}, weight {W(3):F3}: a mirror is alpha {WxMirrorAlpha} or less at nearly full weight");

        // Given a dark floor whose PBR maps say it is rough and porous
        // When rain leaves standing water on it
        // Then it reflects the lamp no more than a no-PBR floor of the same brightness does
        check.Expect(Lamp(4) <= Lamp(0) && A(4) >= WxSheenAlpha,
            $"wet E5: rough-mapped dark floor lamp {Lamp(4):F2} (alpha {A(4):F3}) against the no-PBR one's {Lamp(0):F2}");

        WetPooling(vk, check, log, lib);
    }

    /// <summary>E3: a dark cobble's stone (0.06) and joint (0.02) soaked under the film weight the library gave it,
    /// at the <c>wx.wetdark</c> default of 1; the pair must keep 80 percent of its dry contrast, and the film may
    /// cover under half the floor's own look.</summary>
    private static void WetShowThrough(VkBackend vk, Checker check, ExtenderLog log, string soak, float weight, float flatten)
    {
        float[] cases = [0.06f, weight, 0.02f, weight];
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + soak + CaseBlocks(2, 1) + """
            void main()
            {
                uint i = gl_WorkGroupID.x, a = i * uint(CX_GOLD_IN);
                uOut[i] = CxWetSoak(vec3(uIn[a]), uIn[a + 1u]).g;
            }
            """, "golden wet soak");
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 2, cases, 2, "golden wet soak");
        vk.Destroy(pipe);
        check.Expect(got != null, "wet E3: the soak stage was refused (logged above)");
        if (got == null) return;
        float dry = 0.06f / 0.02f, wet = got[0] / Math.Max(got[1], 1e-6f);

        // Given a no-PBR dark cobble floor
        // When rain leaves standing water on it
        // Then its stones and joints stay plainly visible through the water
        check.Expect(weight < 0.5f && flatten < 0.5f && wet >= 0.8f * dry,
            $"wet E3: film weight {weight:F3}, normal flattened {flatten:F3}, stone/joint contrast {wet:F2} wet against {dry:F2} dry");
    }

    /// <summary>E6 and E7: metres each point stands above its basin's low point, from <c>CxWxMicro</c>, with
    /// the micro-relief noise at its mean. E6's fine relief is the height map's texel (groove 0, top 1) on a PBR floor
    /// of roughness 0.3; E7's comes from a no-PBR cobble's joint (0.02) and stone (0.07) against their mean (0.05).</summary>
    private static void WetPooling(VkBackend vk, Checker check, ExtenderLog log, string lib)
    {
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + lib + CaseBlocks(6, 4) + """
            void main()
            {
                float d = uIn[0], p = uIn[1], rough = uIn[2], joint = uIn[3], stone = uIn[4], near = uIn[5];
                float poroPbr = CxWxPoroOf(0.3, 0.1, 0.0, 0.2, true);
                uOut[0] = CxWxMicro(d, p, 0.5, 0.0, poroPbr);
                uOut[1] = CxWxMicro(d, p, 0.5, 1.0, poroPbr);
                uOut[2] = CxWxMicro(d, p, 0.5, CxWxFineLum(joint, near), CxWxPoroOf(rough, CX_DIELECTRIC_F0, 0.0, joint, false));
                uOut[3] = CxWxMicro(d, p, 0.5, CxWxFineLum(stone, near), CxWxPoroOf(rough, CX_DIELECTRIC_F0, 0.0, stone, false));
            }
            """, "golden wet pooling");
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 1, [WxDepth, WxPuddles, WxStockRough, 0.02f, 0.07f, 0.05f], 4, "golden wet pooling");
        vk.Destroy(pipe);
        check.Expect(got != null, "wet E6-E7: the pooling stage was refused (logged above)");
        if (got == null) return;
        float level = 0.5f * WxDepth;
        log.Info(FormattableString.Invariant($"GpuGolden: wet pooling at {level:F4} m, groove {got[0]:F4} top {got[1]:F4} joint {got[2]:F4} stone {got[3]:F4}"));

        // Given a floor whose height map has grooves between raised stones
        // When rain fills the ground's basin halfway
        // Then water stands in the grooves and the tops of the stones stay out of it
        check.Expect(got[0] < level && got[1] > level,
            $"wet E6: groove stands {got[0]:F4} m, top {got[1]:F4} m, against a water level of {level:F4} m");

        // Given a no-PBR cobble floor whose joints are darker than its stones
        // When rain fills the ground's basin halfway
        // Then water collects in the joints before it covers the stones
        check.Expect(got[2] < got[3], $"wet E7: joint stands {got[2]:F4} m, stone {got[3]:F4} m: the joint must fill first");
    }
}
