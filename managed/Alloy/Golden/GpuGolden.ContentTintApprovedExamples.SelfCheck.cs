using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// A dye stage that rebuilds its own shine (approved-spec, E1, E2, E3, E6): a fixture of that shape through the shipped
/// translator, on the device. Oracle: stock <c>inc_material</c>'s generated material, worked by hand for each dye.
/// </summary>
internal static partial class GpuGolden
{
    private static void ContentTintApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        TintAnswer[]? got = ContentTintRun(vk, check, log, "cx_fixtint",
        [
            new(new Vector4(0.4f, 0.05f, 0.05f, 230f / 255f), Vector2.Zero, 0f),
            new(new Vector4(0.02f, 0.02f, 0.02f, 254f / 255f), Vector2.Zero, 0f),
            new(new Vector4(0.5f, 0.5f, 0.5f, 1f), new Vector2(0.6f, 0.35f), 0f),
        ]);
        if (got == null) return;
        TintAnswer hair = got[0], cloth = got[1], slot = got[2];
        var red = new Vector3(0.133209f, 0.001373f, 0.001373f);
        log.Info(FormattableString.Invariant($"GpuGolden: content tint, hair {hair.Pbr} {hair.Spec} {hair.Albedo}, cloth {cloth.Pbr} {cloth.Spec}, slot {slot.Pbr}"));

        // Given a character whose hair is dyed dark red by a shader that rebuilds its own shine
        // When they stand by a lamp with Alloy on
        // Then the hair shines like metal and its highlight is dark red, as in the stock client
        check.Expect(TintNear(hair.Pbr, new Vector3(0.777255f, 0.445833f, 1f)) && TintNear(hair.Spec, red)
                     && TintNear(hair.Env, 0.098039f),
                     FormattableString.Invariant($"E1: dyed hair is the metal (0.7773, 0.4458, 1) with a dark red highlight, not {hair.Pbr} {hair.Spec} env {hair.Env:F4}"));

        // Given a character wearing cloth dyed black by a shader that rebuilds its own shine
        // When they stand by a lamp with Alloy on
        // Then the cloth stays black with almost no highlight, as in the stock client, and does not read grey
        check.Expect(TintNear(cloth.Pbr, new Vector3(0.069490f, 0.545833f, 0.294902f)) && TintNear(cloth.Spec, new Vector3(0.000647f)),
                     FormattableString.Invariant($"E2: black cloth has a highlight of 0.0006, not {cloth.Pbr} {cloth.Spec}"));

        // Given a character whose hair is dyed dark red by a shader that rebuilds its own shine
        // When they walk from a blue-lit room into an orange-lit one with Alloy on
        // Then the hair's base colour stays the dye's colour, and only its reflection changes
        check.Expect(TintNear(hair.Albedo, red),
                     FormattableString.Invariant($"E3: the hair's base colour is the dye's, with none of the probe's blue in it, not {hair.Albedo}"));

        // Given an RGB part with a slot whose shine and roughness are set (rgbS and rgbR)
        // When it is drawn with Alloy on
        // Then the slot shows that shine and roughness, as in the stock client
        check.Expect(TintNear(slot.Pbr.X, 0.6f) && TintNear(slot.Pbr.Y, 0.35f),
                     FormattableString.Invariant($"E6: a slot's shine 0.6 and roughness 0.35 reach the material, not {slot.Pbr}"));
    }
}
