using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The crown golden (approved-spec, E1-E5 and C2): the shipped crown sheet, rim beads and puddle column from
/// <c>cx_wx_hit</c> and <c>cx_wx_drop</c>, and the splash's life. Oracle: a crown 2 m and 5 m from a 1440-line
/// 60-degree view, a blue daylight sky and a dim night one, a puddle at the depth setting, bounds written by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The crown stage's outputs: E1's height, end and life, E2's worst aspects, E3's rim, middle and see-through,
    /// E4's farthest bead, E5's crown and drop, C2's crown, ring, rebound and column.</summary>
    private const int CrownOut = 20;

    private static void RainCrownApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string look = ShaderSource.Get("cx_wx_look.glsl"), hit = ShaderSource.Get("cx_wx_hit.glsl");
        string drop = ShaderSource.Get("cx_wx_drop.glsl");
        string standing = Function(look, "float CxWxStanding(");
        bool have = standing.Length > 0 && Function(hit, "vec2 CxCrownShape(").Length > 0
                 && Function(hit, "vec2 CxSplashColumn(").Length > 0 && Function(hit, "vec2 CxBeadFlight(").Length > 0
                 && Function(hit, "vec2 CxSplashBeadSize(").Length > 0 && Function(drop, "vec4 CxCrownSheet(").Length > 0;
        check.Expect(have, "rain crown: cx_wx_hit declares CxCrownShape, CxSplashColumn, CxBeadFlight and CxSplashBeadSize, cx_wx_drop CxCrownSheet");
        if (!have) return;
        string terms = FormattableString.Invariant(
            $"const float CX_GOLD_LIFE = {Weather.SplashLifeSec:R};\nconst float CX_GOLD_BLUR = {Weather.Blur.Default:R};\nconst float CX_GOLD_SPEED = {Weather.RainSpeed.Default:R};\n");
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, CrownOut) + terms
            + standing + hit + drop + """
            float Lum(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }
            const vec3 cxR = vec3(1.0, 0.0, 0.0), cxU = vec3(0.0, 0.0, 1.0), cxV = vec3(0.0, -1.0, 0.0), cxK = vec3(0.0);
            /// The brightest premultiplied luminance a crown at its tallest shows, over a grid across its quad.
            float SheetPeak(vec3 skyUp, vec3 skyLow)
            {
                vec2 s = CxCrownShape(0.25);
                float best = 0.0;
                for (int i = 0; i < 64; i++)
                    for (int j = 0; j < 32; j++)
                    {
                        vec2 q = vec2((float(i) + 0.5) / 32.0 - 1.0, (float(j) + 0.5) / 32.0) * vec2(2.0 * s.y, s.x);
                        best = max(best, Lum(CxCrownSheet(q, s, 0.0, cxV, cxR, cxU, skyUp, skyLow, cxU, cxK).rgb));
                    }
                return best;
            }
            void main()
            {
                uint o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                float tall = 0.0;
                for (int i = 0; i <= 200; i++) tall = max(tall, CxCrownShape(float(i) / 200.0).x);
                uOut[o] = tall; uOut[o + 1u] = CxCrownShape(1.0).x; uOut[o + 2u] = CX_GOLD_LIFE;
                // the worst bead aspect from rest to 6 m/s, 1 m to 60 m away; the worst column aspect over its life
                float bead = 0.0, column = 0.0;
                for (int i = 0; i <= 12; i++)
                    for (int j = 0; j < 4; j++)
                    {
                        vec2 b = CxSplashBeadSize(float(i) * 0.5, 1247.0 / pow(4.0, float(j)));
                        bead = max(bead, b.x / b.y);
                    }
                for (int i = 1; i < 100; i++)
                {
                    vec2 c = CxSplashColumn(float(i) / 100.0);
                    if (c.x > 1e-4) column = max(column, c.x / max(c.y, 1e-6));
                }
                uOut[o + 3u] = bead; uOut[o + 4u] = column;
                // the tallest crown in daylight: the rim edge-on at mid height, the middle seen straight through
                vec2 s = CxCrownShape(0.25);
                vec3 skyUp = vec3(0.3, 0.5, 0.9), skyLow = vec3(0.35, 0.4, 0.45);
                float rimL = 0.0;
                for (int i = 0; i < 64; i++)
                {
                    vec2 q = vec2((float(i) + 0.5) / 32.0 - 1.0, 0.5) * vec2(2.0 * s.y, s.x);
                    rimL = max(rimL, Lum(CxCrownSheet(q, s, 0.0, cxV, cxR, cxU, skyUp, skyLow, cxU, cxK).rgb));
                }
                vec4 mid = CxCrownSheet(vec2(0.0, 0.5 * s.x), s, 0.0, cxV, cxR, cxU, skyUp, skyLow, cxU, cxK);
                uOut[o + 5u] = rimL; uOut[o + 6u] = Lum(mid.rgb); uOut[o + 7u] = mid.a;
                // every bead off a full crown, at both ends of its launch speed and tilt, until it lands
                vec2 rim = CxCrownShape(CX_BEAD_BREAK);
                float reach = 0.0;
                for (int a = 0; a < 2; a++)
                    for (int b = 0; b < 2; b++)
                        for (int t = 0; t < 1000; t++)
                        {
                            vec2 p = CxBeadFlight(float(t) * 0.001, CX_BEAD_V[a], CX_BEAD_TILT[b], vec2(rim.y, rim.x));
                            if (p.y >= 0.0) reach = max(reach, p.x);
                        }
                uOut[o + 8u] = reach;
                // night, no lamp: a crown 5 m away against a falling drop 5 m away under the same sky
                vec3 night = vec3(0.02, 0.022, 0.025);
                float crown = SheetPeak(night, night);
                vec3 size = CxWxDropSize(1247.0 / 5.0, CX_GOLD_SPEED, CX_GOLD_BLUR);
                float fall = 0.0;
                for (int i = 0; i < 32; i++)
                    for (int j = 0; j < 32; j++)
                    {
                        vec2 uv = (vec2(i, j) + 0.5) / 16.0 - 1.0;
                        if (dot(uv, uv) > 1.0) continue;
                        fall = max(fall, Lum(CxWxBead(uv, cxV, cxR, cxU, night, night, cxU, cxK, cxU, cxK).rgb) * size.z);
                    }
                uOut[o + 9u] = crown; uOut[o + 10u] = fall;
                // a puddle at the depth setting: the crown, the ring, the rebound, and the column over its life
                vec4 deep = CxSplashByWater(CxWxStanding(0.02, 0.01, 0.01));
                float colTop = 0.0;
                for (int i = 0; i <= 100; i++) colTop = max(colTop, CxSplashColumn(float(i) / 100.0).x);
                uOut[o + 11u] = deep.x; uOut[o + 12u] = deep.z; uOut[o + 13u] = deep.w;
                uOut[o + 14u] = colTop; uOut[o + 15u] = CxSplashColumn(1.0).x; uOut[o + 16u] = CxSplashColumn(0.0).x;
            }
            """, "golden rain crown");
        float[] cases = [0f];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 1, cases, CrownOut, "golden rain crown");
        vk.Destroy(pipe);
        check.Expect(got != null, "rain crown: the stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant(
            $"GpuGolden: rain crown, {got[0] * 100:F1} cm tall over {got[2]:F2} s, bead aspect {got[3]:F2} column {got[4]:F2}, rim {got[5]:F3} middle {got[6]:F3} a {got[7]:F2}, reach {got[8] * 100:F1} cm, night crown {got[9]:F4} drop {got[10]:F4}, column {got[14] * 100:F1} cm"));

        // Given a drop landing on bare wet stone 2 m from the camera, in daylight
        // When it lands
        // Then a thin, see-through crown of water rises from where it hit, a few centimetres tall at most, and falls back within a fifth of a second
        check.Expect(got[0] >= 0.005f && got[0] <= 0.05f && got[1] <= 1e-4f && got[2] <= 0.2f,
            FormattableString.Invariant($"rain crown E1: {got[0] * 100:F2} cm at its tallest (0.5 to 5), {got[1] * 100:F3} cm left at the end of a {got[2]:F3} s life (none, within 0.2 s)"));

        // Given any impact at the default settings, on stone or in a puddle
        // When it is drawn
        // Then no part of it shows as a thin line flying away from where the drop hit: every droplet it throws is a round bead, at most twice as long on screen as it is wide
        check.Expect(got[3] <= 2f && got[4] <= 4f,
            FormattableString.Invariant($"rain crown E2: a bead is at worst {got[3]:F2} times as long as wide (at most 2); the puddle column {got[4]:F2} times as tall as wide (at most 4)"));

        // Given a crown in daylight with no lamp near
        // When it is shaded
        // Then its rim, seen edge-on, reflects the sky more brightly than its middle, and the ground behind shows through its middle
        check.Expect(got[5] > got[6] && got[7] > 0f && got[7] <= 0.5f,
            FormattableString.Invariant($"rain crown E3: the rim edge-on {got[5]:F3} must outshine the middle {got[6]:F3}, which hides {got[7]:F2} of the ground (above 0, at most 0.5)"));

        // Given a crown off bare stone
        // When it collapses
        // Then a few round beads break from its rim and fall back, none landing farther than 20 cm from where the drop hit
        check.Expect(got[8] > 0f && got[8] <= 0.2f,
            FormattableString.Invariant($"rain crown E4: the farthest bead lands {got[8] * 100:F1} cm out (at most 20)"));

        // Given rain at night on a street with no lamp within 5 m
        // When drops land on the bare ground
        // Then their crowns are at least as visible on the ground as the falling rain over it
        check.Expect(got[9] > 0f && got[9] >= got[10],
            FormattableString.Invariant($"rain crown E5: a crown 5 m away peaks at {got[9]:F4}, a falling drop at {got[10]:F4}"));

        // Given a formed puddle as deep as the puddle-depth setting
        // When a drop lands in its middle
        // Then a short, thick column of water rises from the dip the drop left and falls back, a ring spreads round it, and no crown rises
        check.Expect(got[11] == 0f && got[12] > 0f && got[13] > 0f && got[14] > 0f && got[14] <= got[0] && got[15] <= 1e-4f && got[16] <= 1e-4f,
            FormattableString.Invariant($"rain crown C2: crown {got[11]:F2} (must be 0), ring {got[12]:F2}, rebound {got[13]:F2}; the column rises to {got[14] * 100:F2} cm (above 0, no taller than a crown) from {got[16] * 100:F3} and back to {got[15] * 100:F3}"));
    }
}
