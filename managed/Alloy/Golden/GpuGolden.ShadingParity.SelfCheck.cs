using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// The shading-parity examples that held before the change (so they carry no approved-spec mark): a placed light
/// keeps the diffuse weight, an opaque part lets no light through, a raised Diffuse wrap widens the band without
/// moving the peak, and a wall with no displacement terms keeps its relief. Oracles: the hand-written weights and
/// literals below, and <see cref="StockDisplace"/>.
/// </summary>
internal static partial class GpuGolden
{
    private static void ShadingParitySelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given the same red-ambient, blue-diffuse material, lit only by a white placed light facing it
        // When it draws, with Deferred point lights on and with it off
        // Then it shades blue, as before
        DeferredRigScene placed = ParityScene(Vector3.UnitZ, -1f, 1f,
                                              p => p with { DiffT = new Vector3(0f, 0f, 1f), AmbT = new Vector3(1f, 0f, 0f) });
        if (ParityCentre(vk, check, log, placed, "CX-447 E2 a placed light on red ambient, blue diffuse") is { } c)
            check.Expect(c.Z > 1e-3f && c.X < 1e-5f,
                         FormattableString.Invariant($"CX-447 E2: a placed light shades the wall blue, not red ({c.X:F4}, {c.Y:F4}, {c.Z:F4})"));

        // Given a fully opaque part, with one placed light straight behind it
        // When it draws
        // Then its near face receives nothing from that light, as before
        if (ParityCentre(vk, check, log, ParityScene(-Vector3.UnitZ, -1f, 1f, p => p with { DiffT = Vector3.One }),
                         "CX-449 E12 an opaque wall, the lamp behind") is { } opaque)
            check.Expect(opaque.Y < 1e-5f, FormattableString.Invariant($"CX-449 E12: the lamp behind an opaque wall gives {opaque.Y:F5}, not 0"));

        // Given a smooth surface lit by one distant placed light, with Diffuse wrap at 1
        // When the surface faces 90 degrees off the light
        // Then it receives more than the 0.056 it gets with Diffuse wrap at 0, and head-on it still receives exactly the head-on brightness
        if (WrapCases(vk, check, log, [0f, 1f, 0f, 1f, 1f, 0f]) is { } band)
            check.Expect(band[0] > 0.056f && band[1] == 1f,
                         FormattableString.Invariant($"CX-448 E10: wrap 1 gives {band[0]:F4} at 90 degrees (over 0.056) and {band[1]:F4} head-on (exactly 1)"));

        // Given a height-mapped wall whose .mtr sets neither parameter
        // When it draws
        // Then its relief is the same as before this change
        if (DisplaceRuns(vk, check, log, [0f, 0f, 0.5f, 2f]) is not { } got) return;
        (double U, double V) plain = StockDisplace(DisplaceWall), both = StockDisplace(DisplaceWall, 0.5, 2.0);
        check.Expect(DisplacedTo(got, 0, plain),
                     FormattableString.Invariant($"CX-450 E19: no terms land on ({got[0]:F5}, {got[1]:F5}), stock ({plain.U:F5}, {plain.V:F5})"));
        // stock shifts the start by the unscaled reach, then scales the march
        check.Expect(DisplacedTo(got, 1, both),
                     FormattableString.Invariant($"CX-450: offset 0.5 with multiplier 2 lands on ({got[2]:F5}, {got[3]:F5}), stock ({both.U:F5}, {both.V:F5})"));
    }
}
