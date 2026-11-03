using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The impact golden (approved-spec, C1-C6): the shipped micro-relief and standing water from <c>cx_wx_look</c>, the
/// crown by water from <c>cx_wx_hit</c> and the crown droplet from <c>cx_splash_f</c> over <c>cx_wx_drop</c>'s bead.
/// Oracle: cobble tops at relief 1 and joints at 0 under a basin between them, a puddle at the depth setting, dry
/// wet ground, a dark joint beside pale stone with no height map and a lamp to a droplet's left, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The impact stage's outputs: C1's two standings and crowns, C2's, C3's, C4's steps, C5's and C6's sides.</summary>
    private const int ImpactOut = 24;

    private static void RainImpactApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string look = ShaderSource.Get("cx_wx_look.glsl"), hit = ShaderSource.Get("cx_wx_hit.glsl");
        string drop = ShaderSource.Get("cx_wx_drop.glsl"), splash = ShaderSource.Get("cx_splash_f.glsl");
        string micro = Function(look, "float CxWxMicro("), fineLum = Function(look, "float CxWxFineLum(");
        string standing = Function(look, "float CxWxStanding("), byWater = Function(hit, "vec4 CxSplashByWater(");
        string droplet = Function(splash, "vec4 CxSplashDrop(");
        bool have = micro.Length > 0 && fineLum.Length > 0 && standing.Length > 0 && byWater.Length > 0
                 && droplet.Length > 0 && Function(drop, "vec4 CxWxBead(").Length > 0;
        check.Expect(have, "rain impact: cx_wx_look declares CxWxStanding, cx_wx_hit CxSplashByWater, cx_splash_f CxSplashDrop over cx_wx_drop's bead");
        if (!have) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, ImpactOut)
            + Declaration(look, "CX_WX_MICRO_AMP") + Declaration(look, "CX_WX_MICRO_MEAN") + Declaration(look, "CX_WX_FINE_W")
            + micro + fineLum + standing + hit + drop + droplet + """
            void main()
            {
                uint o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                // a 1 cm relief and a 1 cm puddle-depth setting, the puddles knob at its middle, firm ground
                const float depth = 0.01, pool = 0.01;
                float top = CxWxMicro(depth, 0.5, 0.5, 1.0, 0.0), joint = CxWxMicro(depth, 0.5, 0.5, 0.0, 0.0);
                float basin = 0.5 * (top + joint);
                float sTop = CxWxStanding(basin, top, pool), sJoint = CxWxStanding(basin, joint, pool);
                vec4 hTop = CxSplashByWater(sTop), hJoint = CxSplashByWater(sJoint);
                uOut[o] = sTop; uOut[o + 1u] = sJoint; uOut[o + 2u] = hTop.x; uOut[o + 3u] = hTop.y; uOut[o + 4u] = hJoint.x; uOut[o + 5u] = hJoint.y;
                vec4 deep = CxSplashByWater(CxWxStanding(joint + pool, joint, pool));
                uOut[o + 6u] = deep.x; uOut[o + 7u] = deep.z; uOut[o + 8u] = deep.w;
                vec4 dry = CxSplashByWater(CxWxStanding(joint - pool, joint, pool));
                uOut[o + 9u] = dry.x; uOut[o + 10u] = dry.y; uOut[o + 11u] = dry.z;
                // from the dry ground across the shoreline into the middle, in quarter-millimetre steps of water
                float stepX = 0.0, stepY = 0.0, rises = 0.0;
                vec4 last = CxSplashByWater(CxWxStanding(joint - pool, joint, pool));
                for (int i = 1; i <= 160; i++)
                {
                    vec4 h = CxSplashByWater(CxWxStanding(joint - pool + float(i) * 0.00025, joint, pool));
                    stepX = max(stepX, abs(h.x - last.x)); stepY = max(stepY, abs(h.y - last.y));
                    if (h.x > last.x + 1e-6 || h.y > last.y + 1e-6) rises += 1.0;
                    last = h;
                }
                uOut[o + 12u] = stepX; uOut[o + 13u] = stepY; uOut[o + 14u] = rises;
                // no height map: a joint darker than the stones round it, a stone paler than them
                float fJ = CxWxFineLum(0.05, 0.2), fS = CxWxFineLum(0.3, 0.2);
                float mJ = CxWxMicro(depth, 0.5, 0.5, fJ, 0.0), mS = CxWxMicro(depth, 0.5, 0.5, fS, 0.0);
                float b = 0.5 * (mJ + mS);
                vec4 hS = CxSplashByWater(CxWxStanding(b, mS, pool)), hJ = CxSplashByWater(CxWxStanding(b, mJ, pool));
                uOut[o + 15u] = hS.x; uOut[o + 16u] = hS.y; uOut[o + 17u] = hJ.x; uOut[o + 18u] = hJ.y;
                vec4 dp = CxSplashByWater(CxWxStanding(mJ + pool, mJ, pool));
                uOut[o + 19u] = dp.x;
                // a crown droplet at night with a lamp to its left: camera looking along +y
                vec3 r = vec3(1.0, 0.0, 0.0), u = vec3(0.0, 0.0, 1.0), v = vec3(0.0, -1.0, 0.0), k = vec3(0.0);
                float left = 0.0, right = 0.0;
                for (int i = 0; i < 32; i++)
                    for (int j = 0; j < 32; j++)
                    {
                        vec2 uv = (vec2(i, j) + 0.5) / 16.0 - 1.0;
                        if (dot(uv, uv) > 1.0) continue;
                        float l = dot(CxSplashDrop(uv, v, r, u, k, k, -r, vec3(1.0), u, k).rgb, vec3(0.2126, 0.7152, 0.0722));
                        if (uv.x < -0.1) left = max(left, l);
                        if (uv.x > 0.1) right = max(right, l);
                    }
                uOut[o + 20u] = left; uOut[o + 21u] = right;
            }
            """, "golden rain impact");
        float[] cases = [0f];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 1, cases, ImpactOut, "golden rain impact");
        vk.Destroy(pipe);
        check.Expect(got != null, "rain impact: the stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant(
            $"GpuGolden: rain impact, top {got[0]:F2} -> crown {got[2]:F2} v {got[3]:F2}, joint {got[1]:F2} -> {got[4]:F2} v {got[5]:F2}, puddle crown {got[6]:F2} ring {got[7]:F2} rebound {got[8]:F2}, steps {got[12]:F3}/{got[13]:F3}"));

        // Given a cobbled street with a height map, water standing in the joints but not over the cobble tops
        // When one drop lands on a cobble top and another in a joint
        // Then the cobble top throws a full crown of droplets, and the joint throws fewer, lower ones
        check.Expect(got[0] == 0f && got[1] > 0f && got[2] == 1f && got[4] < got[2] && got[5] < got[3],
            FormattableString.Invariant($"rain impact C1: water top {got[0]:F2} joint {got[1]:F2}; crown top {got[2]:F2} (must be 1) speed {got[3]:F2}, joint {got[4]:F2} speed {got[5]:F2} (both lower)"));

        // Given a formed puddle as deep as the puddle-depth setting
        // When a drop lands in its middle
        // Then it leaves a ring on the water and at most a small rebound, with no crown of droplets
        check.Expect(got[6] == 0f && got[7] > 0f && got[8] > 0f && got[8] <= 0.5f,
            FormattableString.Invariant($"rain impact C2: crown {got[6]:F2} (must be 0), ring {got[7]:F2} (above 0), rebound {got[8]:F2} (above 0, at most 0.5)"));

        // Given ground wet from rain where no water stands yet
        // When drops land on it
        // Then every drop throws a full crown, as it does today
        check.Expect(got[9] == 1f && got[10] == 1f && got[11] > 0f,
            FormattableString.Invariant($"rain impact C3: crown {got[9]:F2}, speed {got[10]:F2} (both 1), ring {got[11]:F2}"));

        // Given a puddle, its shoreline, and the dry ground beside it
        // When drops land from the dry ground across the shoreline into the middle
        // Then their crowns weaken steadily as the water deepens, with no jump at the shoreline
        check.Expect(got[12] <= 0.1f && got[13] <= 0.1f && got[14] == 0f,
            FormattableString.Invariant($"rain impact C4: the largest quarter-millimetre step is {got[12]:F3} in the crown, {got[13]:F3} in its speed (at most 0.1); {got[14]:F0} steps rose"));

        // Given a surface with no height map, with a puddle on it
        // When drops land in the puddle and beside it
        // Then they follow C1 to C4 by the water the surface shows standing there
        check.Expect(got[15] == 1f && got[17] < got[15] && got[18] < got[16] && got[19] == 0f,
            FormattableString.Invariant($"rain impact C5: stone crown {got[15]:F2} speed {got[16]:F2}, joint {got[17]:F2} speed {got[18]:F2}, in the puddle {got[19]:F2}"));

        // Given a crown thrown up beside a lamp at night
        // When its droplets are shaded
        // Then each glints on its lamp side the way a falling drop does (B2)
        check.Expect(got[20] >= 0.1f && got[21] <= 0.02f,
            FormattableString.Invariant($"rain impact C6: the lamp side peaks at {got[20]:F3} (at least 0.1), the far side at {got[21]:F4} (at most 0.02)"));
    }
}
