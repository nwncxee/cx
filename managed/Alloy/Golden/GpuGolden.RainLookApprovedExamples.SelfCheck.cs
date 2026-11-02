using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The rain look golden (approved-spec, E1-E4): the shipped <c>CxWxShade</c> and <c>CxWxLamp</c> over the shipped
/// <c>CxPointLight</c>, for rain, snow and drips. Oracle: a blue sky behind a drop, a dark night and a 5 m lamp at 2 m and
/// 8 m, with the bounds the examples give, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The precipitation kinds the program draws: 1 rain, 2 snow, 3 roof drips.</summary>
    private static readonly (int Kind, string Name)[] WxKinds = [(1, "rain"), (2, "snow"), (3, "drips")];

    private static void RainLookApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string lib = ShaderSource.Get("cx_wx_shade.glsl"), lf = ShaderSource.Get("cx_lightfrag.glsl");
        string shade = Function(lib, "vec4 CxWxShade("), lamp = Function(lib, "vec3 CxWxLamp(");
        string point = Function(lf, "bool CxPointLight("), cut = Function(lf, "float CxReachCut(");
        string wrap = Function(lf, "float CxWrap(");
        bool have = shade.Length > 0 && lamp.Length > 0 && point.Length > 0 && cut.Length > 0 && wrap.Length > 0;
        check.Expect(have, "rain look: cx_wx_shade declares CxWxShade and CxWxLamp, cx_lightfrag CxPointLight");
        if (!have) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 12) + """
            const vec3 uFalloff = vec3(1.0, 0.0, 1.0);
            const vec2 uAtten = vec2(0.0);
            const vec2 uHot = vec2(0.0);
            const float uReach = 0.0, uLightFloor = 0.0, uWrap = 0.0, uWrapN = 0.0, uDayDim = 1.0, cxLod = 1.0;
            vec3 CxLinear(vec3 c) { return pow(max(c, vec3(0.0)), vec3(2.2)); }
            float CxSurfFade(vec3 L) { return 1.0; }
            """ + cut + wrap + point + shade + lamp + """
            void main()
            {
                int kind = int(uIn[gl_WorkGroupID.x * uint(CX_GOLD_IN)]);
                uint o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                vec4 day = CxWxShade(kind, 1.0, vec3(0.0), vec3(0.1, 0.3, 0.8));
                vec4 night = CxWxShade(kind, 1.0, vec3(0.001), vec3(0.003, 0.004, 0.005));
                vec4 lamp = vec4(0.0, 0.0, 0.0, 5.0);
                vec3 near = CxWxLamp(lamp, vec3(1.0, 0.8, 0.6), vec3(2.0, 0.0, 0.0));
                vec3 far = CxWxLamp(lamp, vec3(1.0, 0.8, 0.6), vec3(8.0, 0.0, 0.0));
                uOut[o] = day.r; uOut[o + 1u] = day.b; uOut[o + 2u] = day.a;
                uOut[o + 3u] = max(night.r, max(night.g, night.b)); uOut[o + 4u] = night.a;
                uOut[o + 5u] = dot(near, vec3(1.0)); uOut[o + 6u] = dot(far, vec3(1.0));
                vec4 litNear = CxWxShade(kind, 1.0, near, vec3(0.0));
                vec4 litFar = CxWxShade(kind, 1.0, far, vec3(0.0));
                uOut[o + 7u] = dot(litNear.rgb, vec3(1.0)); uOut[o + 8u] = dot(litFar.rgb, vec3(1.0));
            }
            """, "golden rain look");
        float[] cases = [1f, 2f, 3f];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 3, cases, 36, "golden rain look");
        vk.Destroy(pipe);
        check.Expect(got != null, "rain look: the stage was refused (logged above)");
        if (got == null) return;
        for (int k = 0; k < WxKinds.Length; k++)
        {
            int o = k * 12;
            string n = WxKinds[k].Name;
            log.Info(FormattableString.Invariant(
                $"GpuGolden: rain look, {n}: day rgb.r {got[o]:F3} rgb.b {got[o + 1]:F3} a {got[o + 2]:F3}, night {got[o + 3]:F4} a {got[o + 4]:F3}, lamp {got[o + 7]:F3} near {got[o + 8]:F3} far"));

            // Given a rain drop in daylight with the sun straight ahead of the camera and no lamp near
            // When the drop is shaded
            // Then it still hides a little of what is behind it, and its colour carries the sky seen through it
            check.Expect(got[o + 2] > 0f && got[o + 1] > got[o] && got[o + 1] > 0.02f,
                $"rain look E1 ({n}): alpha {got[o + 2]:F3} must be above 0 and the blue sky must show, blue {got[o + 1]:F3} over red {got[o]:F3}");

            // Given a rain drop at night, with a dark sky, near-zero ambient and no lamp near
            // When the drop is shaded
            // Then it adds almost no light, at most 0.02, and only dims what is behind it a little
            check.Expect(got[o + 3] <= 0.02f && got[o + 4] > 0f && got[o + 4] <= 0.2f,
                $"rain look E2 ({n}): adds {got[o + 3]:F4} (at most 0.02) and hides {got[o + 4]:F3} (above 0, at most 0.2)");

            // Given one rain drop 2 m from a lamp that reaches 5 m, and another 8 m from it
            // When both are shaded
            // Then the near drop is brighter than the far one, and the far one takes nothing from the lamp
            check.Expect(got[o + 5] > 0f && got[o + 6] == 0f && got[o + 7] > got[o + 8],
                $"rain look E3 ({n}): the lamp gives {got[o + 5]:F3} at 2 m and {got[o + 6]:F3} at 8 m; shaded {got[o + 7]:F3} against {got[o + 8]:F3}");
        }

        // Given a snow flake or a roof drip instead of a rain drop
        // When it is shaded in each of the three cases above
        // Then it follows the same three rules
        check.Expect(got[2] != got[14] && got[2] != got[26] && got[14] != got[26],
            $"rain look E4: rain, snow and drips must each take their own shading, not one copied across: daylight alphas {got[2]:F3}, {got[14]:F3}, {got[26]:F3}");
    }
}
