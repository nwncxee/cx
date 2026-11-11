using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The volumetric fog at far zoom (approved examples E1, E2, E4, E5). Oracle: the wall's documented ramp summed
/// every centimetre (<see cref="WallDepth"/>), and figures hand-worked for a 15..60 m fog: 91.407% reaches a camera
/// 25 m out, 80 / 25 = 3.2, and 64 slices spaced evenly in log depth put 64 ln(85/40) / ln 85 = 10.86 across the fade.</summary>
internal static unsafe partial class FogVolume
{
    /// <summary>The depth slices of Medium detail, written here by hand: the count E5's fade is measured in.</summary>
    private const int MediumSlices = 64;

    private static void FarZoomApprovedExamples(Checker check)
    {
        bool volWas = VolumetricFog.Enabled.V;
        float hazeWas = VolumetricFog.Haze.V;
        VolumetricFog.Enabled.V = true; VolumetricFog.Haze.V = 0f;
        Medium at25 = MediumFor(true, 15f, 60f, 0f, 0f, 25f), at80 = MediumFor(true, 15f, 60f, 0f, 0f, 80f);

        // Given an area whose fog runs from 15 m to 60 m, with volumetric fog on
        // When I zoom the camera out from 25 m to 80 m from my character
        // Then my character is as clear of fog as it was at 25 m
        float clear25 = MathF.Exp(-WallDepth(at25, 25f)), clear80 = MathF.Exp(-WallDepth(at80, 80f));
        check.Expect(MathF.Abs(clear25 - 0.91407f) < 1e-3f, $"E1 from 25 m the character keeps 91.4% ({clear25:F4})");
        check.Expect(MathF.Abs(clear80 - 0.91407f) < 1e-3f, $"E1 from 80 m the character keeps the same 91.4% ({clear80:F4})");

        // Given an area whose fog runs from 15 m to 60 m, with volumetric fog on
        // When I zoom the camera out from 25 m to 80 m from my character
        // Then the fog hides the ground at the same place on the screen as it did at 25 m, so I see 3.2 times as far across the area
        float hidden = OpaqueDistance(true, 15f, 60f, 0f, 80f);
        check.Expect(MathF.Abs(hidden - 192f) < 0.01f, $"E2 from 80 m the fog hides the ground 3.2 x 60 = 192 m from the character ({hidden:F2})");
        check.Expect(MathF.Abs(MathF.Exp(-WallDepth(at80, 192f)) - 0.01f) < 2e-4f, "E2 ...where the wall, summed, leaves 1%");
        check.Expect(MathF.Abs(at80.Start - 48f) < 1e-3f, $"E2 ...and starts 3.2 x 15 = 48 m out ({at80.Start:F2})");

        // Given an area whose fog runs from 15 m to 60 m, with volumetric fog on
        // When I zoom the camera out to 80 m from my character
        // Then everything the fog has not fully hidden is still drawn: the world ends inside the fog, never in front of it
        float far = SceneFog.FarForFog(45f, true, 15f, 60f, 80f);
        check.Expect(MathF.Abs(far - 272f) < 0.01f, $"E4 from 80 m the world is drawn to the 192 m the fog hides at, plus the camera's 80 ({far:F2})");

        // Given an area whose fog runs from 15 m to 60 m, with volumetric fog on at Medium detail
        // When I zoom the camera out to 150 m from my character
        // Then the edge where the ground fades into fog is as smooth as it is at 25 m, with no stepped rings
        float fade25 = FadeSlices(25f), fade150 = FadeSlices(150f);
        check.Expect(MathF.Abs(fade25 - 10.86f) < 0.05f, $"E5 from 25 m the fade past the character crosses 10.86 of the 64 slices ({fade25:F2})");
        check.Expect(MathF.Abs(fade150 - 10.86f) < 0.05f, $"E5 from 150 m it crosses as many ({fade150:F2})");

        VolumetricFog.Enabled.V = volWas; VolumetricFog.Haze.V = hazeWas;
    }

    /// <summary>How many of Medium detail's 64 slices, even in log depth, the 15..60 m fog's fade spans on the ray
    /// through the character, from a camera <paramref name="c"/> metres out.</summary>
    private static float FadeSlices(float c)
    {
        Medium m = MediumFor(true, 15f, 60f, 0f, 0f, c);
        (float near, float far, _) = Grid(SceneFog.FarForFog(45f, true, 15f, 60f, c), c);
        return MediumSlices * MathF.Log((c + m.End) / (c + m.Start)) / MathF.Log(far / near);
    }
}
