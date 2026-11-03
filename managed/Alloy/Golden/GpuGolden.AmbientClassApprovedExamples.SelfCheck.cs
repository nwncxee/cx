using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// The ambient-class light's approved example (approved-spec, E1) through the deferred rig: a tile-model light
/// reaches a pixel through the weight its ambient term takes, never the diffuse one. Oracle: the two weights written
/// by hand, red for the ambient and blue for the diffuse, so the colour of the frame says which one the light took.
/// </summary>
internal static partial class GpuGolden
{
    private static void AmbientClassApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given a material whose ambient colour is red and whose diffuse colour is blue, lit only by a white tile-model light
        // When it draws, with Deferred point lights on and with it off
        // Then it shades red, as the stock client does, not blue
        DeferredRigScene sc = ParityScene(Vector3.UnitZ, -2f, 1f,
                                          p => p with { DiffT = new Vector3(0f, 0f, 1f), AmbT = new Vector3(1f, 0f, 0f) });
        if (ParityCentre(vk, check, log, sc, "CX-447 E1 a tile light on red ambient, blue diffuse") is not { } c) return;
        log.Info(FormattableString.Invariant($"GpuGolden: ambient class, centre ({c.X:F4}, {c.Y:F4}, {c.Z:F4})"));
        check.Expect(c.X > 1e-3f && c.Z < 1e-5f,
                     FormattableString.Invariant($"CX-447 E1: a tile light shades the wall red, not blue ({c.X:F4}, {c.Y:F4}, {c.Z:F4})"));
    }
}
