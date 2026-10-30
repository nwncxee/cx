using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The shading-rate golden (approved-spec): six hand-made 16x16 tiles through the shipped <c>cx_vrs_cs</c> -- flat,
/// a checkerboard, vertical and horizontal stripes, and faint stripes either side of the threshold -- each tile's
/// rate read back off the device and held to the rate written beside it.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>Tiles in the fixture, side by side, and their edge in pixels.</summary>
    private const int VrsTiles = 6, VrsEdge = 16;

    /// <summary>The cases' threshold: 0.03 on the 0..1 perceptual scale, the fixture's own rather than the knob's default.</summary>
    private const float VrsContrast = 0.03f;

    /// <summary>Scene values whose perceptual level, sqrt(L / (1 + L)), is 0.50, 0.51 and 0.55: worked by hand
    /// (0.25 / 0.75, 0.2601 / 0.7399, 0.3025 / 0.6975), not computed from the shader's map.</summary>
    private const float VrsP50 = 0.33333334f, VrsP51 = 0.351534f, VrsP55 = 0.433692f;

    /// <summary>Oracle: the rates the cases call for, as Vulkan encodes them, (log2 width) &lt;&lt; 2 | log2 height --
    /// E1 flat 2x2 = 5, E2 checkerboard 1x1 = 0, E3 vertical stripes 1x2 = 1, E4 horizontal stripes 2x1 = 4,
    /// E5 a 0.01 step 2x2 = 5, E6 a 0.05 step 1x2 = 1.</summary>
    private static readonly byte[] VrsWant = [5, 0, 1, 4, 5, 1];

    /// <summary>One fixture pixel's scene value: tile <paramref name="t"/> at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static float VrsPixel(int t, int x, int y) => t switch
    {
        0 => 0.5f,
        1 => ((x + y) & 1) == 0 ? 0f : 1f,
        2 => (x & 1) == 0 ? 0f : 1f,
        3 => (y & 1) == 0 ? 0f : 1f,
        4 => (x & 1) == 0 ? VrsP50 : VrsP51,
        _ => (x & 1) == 0 ? VrsP50 : VrsP55,
    };

    private static void VrsSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        byte[]? got = VrsRates(vk, log, VrsTiles, VrsPixel);
        check.Expect(got != null, "the shading-rate golden's pipeline, images or readback were refused (logged above)");
        if (got != null)
            for (int t = 0; t < VrsTiles; t++)
                check.Expect(got[t] == VrsWant[t], $"shading-rate tile E{t + 1}: rate {got[t]}, the case calls for {VrsWant[t]}");
        VrsCoarseSelfCheck(vk, check, log);
    }
}
