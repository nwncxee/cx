using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The droplet golden (approved-spec, B1-B5, B7): the shipped <c>CxWxDropSize</c> and <c>CxWxBead</c> from
/// <c>cx_wx_drop</c>. Oracle: a drop 1 m away on a 1440-line 60-degree view, a lamp to its left at night, a blue
/// daylight sky and a dark night, with the bounds the examples give, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The bead's outputs: B1 and B7's extents, B5's widened and true extents and its dim, B2's two sides,
    /// B3's rim and centre, B4's brightest texel.</summary>
    private const int DropletOut = 16;

    private static void RainDropletApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string lib = ShaderSource.Get("cx_wx_drop.glsl");
        bool have = Function(lib, "vec3 CxWxDropSize(").Length > 0 && Function(lib, "vec4 CxWxBead(").Length > 0;
        check.Expect(have, "rain droplets: cx_wx_drop declares CxWxDropSize and CxWxBead");
        if (!have) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, DropletOut) + lib + """
            const vec3 cxRight = vec3(1.0, 0.0, 0.0), cxUp = vec3(0.0, 0.0, 1.0), cxV = vec3(0.0, -1.0, 0.0);
            float Lum(vec4 c) { return dot(c.rgb, vec3(0.2126, 0.7152, 0.0722)); }
            void main()
            {
                uint o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                // a 1440-line view at 60 degrees: 1247 pixels per metre at 1 m, 20 at 62 m
                vec3 near = CxWxDropSize(1247.0, 12.0, 0.0), blur = CxWxDropSize(1247.0, 12.0, 1.0);
                vec3 far = CxWxDropSize(20.0, 12.0, 0.0), truth = CxWxDropSize(1.0e5, 12.0, 0.0);
                uOut[o] = near.x; uOut[o + 1u] = near.y; uOut[o + 2u] = blur.x; uOut[o + 3u] = blur.y;
                uOut[o + 4u] = far.x; uOut[o + 5u] = far.y; uOut[o + 6u] = far.z; uOut[o + 7u] = truth.x; uOut[o + 8u] = truth.y;
                vec3 black = vec3(0.0), dusk = vec3(0.003, 0.004, 0.005);
                vec3 skyUp = vec3(0.3, 0.5, 0.9), skyLow = vec3(0.35, 0.4, 0.45);
                float left = 0.0, right = 0.0, night = 0.0;
                for (int i = 0; i < 16; i++)
                    for (int j = 0; j < 16; j++)
                    {
                        vec2 uv = (vec2(i, j) + 0.5) / 8.0 - 1.0;
                        if (dot(uv, uv) > 1.0) continue;
                        vec4 lamp = CxWxBead(uv, cxV, cxRight, cxUp, black, black, -cxRight, vec3(1.0), cxUp, black);
                        if (uv.x < -0.1) left = max(left, Lum(lamp));
                        if (uv.x > 0.1) right = max(right, Lum(lamp));
                        vec4 n = CxWxBead(uv, cxV, cxRight, cxUp, dusk, dusk, cxUp, black, cxUp, black);
                        night = max(night, max(n.r, max(n.g, n.b)));
                    }
                vec3 rim = vec3(0.0);
                for (int k = 0; k < 16; k++)
                {
                    float a = float(k) * 0.3926991;
                    rim += CxWxBead(vec2(cos(a), sin(a)) * 0.92, cxV, cxRight, cxUp, skyUp, skyLow, cxUp, black, cxUp, black).rgb / 16.0;
                }
                vec4 centre = CxWxBead(vec2(0.0), cxV, cxRight, cxUp, skyUp, skyLow, cxUp, black, cxUp, black);
                uOut[o + 9u] = left; uOut[o + 10u] = right;
                uOut[o + 11u] = Lum(vec4(rim, 0.0)); uOut[o + 12u] = Lum(centre); uOut[o + 13u] = rim.b; uOut[o + 14u] = rim.r;
                uOut[o + 15u] = night;
            }
            """, "golden rain droplets");
        float[] cases = [0f];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 1, cases, DropletOut, "golden rain droplets");
        vk.Destroy(pipe);
        check.Expect(got != null, "rain droplets: the stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant(
            $"GpuGolden: rain droplets, 1 m {got[0] * 1000:F2} x {got[1] * 1000:F2} mm, blurred {got[2] * 1000:F1} x {got[3] * 1000:F2} mm, far {got[4] * 20:F2} x {got[5] * 20:F2} px dim {got[6]:F4}, lamp left {got[9]:F3} right {got[10]:F4}, rim {got[11]:F3} centre {got[12]:F3}, night {got[15]:F4}"));

        // Given a rain drop 1 m from the camera
        // When it is drawn
        // Then it shows as a round bead a few millimetres across, at most twice as long on screen as it is wide
        check.Expect(got[1] >= 0.001f && got[1] <= 0.006f && got[0] <= 2f * got[1],
            FormattableString.Invariant($"rain droplets B1: {got[0] * 1000:F2} mm along by {got[1] * 1000:F2} mm across must be 1-6 mm wide and at most twice as long"));

        // Given a rain drop at night with a lamp 2 m to its left and no other light
        // When it is shaded
        // Then its left side carries a bright glint and its right side stays dark
        check.Expect(got[9] >= 0.1f && got[10] <= 0.02f,
            FormattableString.Invariant($"rain droplets B2: the lamp side peaks at {got[9]:F3} (at least 0.1), the far side at {got[10]:F4} (at most 0.02)"));

        // Given a rain drop in daylight with no lamp near
        // When it is shaded
        // Then its rim shows the sky reflected and is brighter than its centre
        check.Expect(got[11] > got[12] && got[13] > got[14],
            FormattableString.Invariant($"rain droplets B3: the rim {got[11]:F3} must outshine the centre {got[12]:F3} and carry the blue sky, blue {got[13]:F3} over red {got[14]:F3}"));

        // Given a rain drop at night, with a dark sky, near-zero ambient and no lamp near
        // When it is shaded
        // Then it adds almost no light, at most 0.02, anywhere on the bead
        check.Expect(got[15] <= 0.02f, FormattableString.Invariant($"rain droplets B4: the brightest texel adds {got[15]:F4}, at most 0.02"));

        // Given TAA off, the camera still, and a rain drop too far away to cover a whole pixel
        // When it is drawn
        // Then it shows as at least one pixel, dimmed by the share of the pixel it covers, and never vanishes from one frame to the next
        float drawn = got[4] * got[5], truth = got[7] * got[8];
        check.Expect(got[4] * 20f >= 1f && got[5] * 20f >= 1f && got[6] > 0f && MathF.Abs(got[6] * drawn - truth) <= truth * 0.01f,
            FormattableString.Invariant($"rain droplets B5: at 20 px/m it covers {got[4] * 20:F2} x {got[5] * 20:F2} px (at least 1 each) dimmed {got[6]:F4}; dim x drawn area {got[6] * drawn:E3} must equal the drop's own {truth:E3}"));

        // Given the rain motion blur setting turned up
        // When a rain drop 1 m from the camera is drawn
        // Then it stretches along its fall, still shaded as a water drop; at the default setting it is the bead of B1
        check.Expect(got[2] >= 4f * got[3] && got[3] >= 0.001f && got[3] <= 0.006f,
            FormattableString.Invariant($"rain droplets B7: blurred, {got[2] * 1000:F1} mm along must be at least four times its {got[3] * 1000:F2} mm width"));
    }
}
