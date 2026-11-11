using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Plain regression tests beside the far-zoom approved examples: what the fog following the zoom must leave
/// alone. Oracle: figures hand-worked off ln 100 = 4.60517: 0.204674 /m over a 45 m ramp, and 12% of it over 150 m of
/// open air, 0.00368413 /m, which is 0.092103 across the 25 m to the character.</summary>
internal static unsafe partial class FogVolume
{
    private static void FarZoomRegressions(Checker check)
    {
        bool volWas = VolumetricFog.Enabled.V, fogWas = Fog.Enabled.V, originWas = Fog.Origin.V;
        float heightWas = Fog.Height.V;

        foreach (float c in new[] { 25f, 10f, 0f, -1f })
        {
            Medium m = MediumFor(true, 15f, 60f, 0f, 0f, c);
            check.Expect(MathF.Abs(m.Start - 15f) < 1e-4f && MathF.Abs(m.End - 60f) < 1e-4f && MathF.Abs(m.Wall - 0.204674f) < 1e-5f,
                         $"a camera {c:F0} m from the player leaves the fog at the area's 15..60 m ({m})");
            check.Expect(MathF.Abs(OpaqueDistance(true, 15f, 60f, 0f, c) - 60f) < 1e-3f, $"...and opaque at 60 m ({c:F0} m)");
            check.Expect(MathF.Abs(Grid(85f, c).Near - 1f) < 1e-6f, $"...and the grid starting a metre from the eye ({c:F0} m)");
        }

        VolumetricFog.Enabled.V = false; Fog.Enabled.V = true; Fog.Origin.V = true; Fog.Height.V = 0f;
        check.Expect(MathF.Abs(SceneFog.FarForFog(45f, true, 15f, 60f, 80f) - 140f) < 1e-3f, "volumetric fog off: zoomed to 80 m the ramp still ends 60 m from the player");

        Medium air = MediumFor(false, 0f, 0f, 300f, 0f, 80f);
        check.Expect(air.Wall <= 0f && air.Haze <= 0f, $"no area fog and no haze: zoomed to 80 m the air holds nothing ({air})");
        check.Expect(float.IsPositiveInfinity(OpaqueDistance(false, 15f, 60f, 0f, 80f)), "...and nothing is hidden at any distance");
        float haze25 = MediumFor(false, 0f, 0f, 20f, 0.12f, 25f).Haze * 25f, haze80 = MediumFor(false, 0f, 0f, 20f, 0.12f, 80f).Haze * 80f;
        check.Expect(MathF.Abs(haze25 - 0.092103f) < 1e-5f, $"no area fog, haze 0.12: 25 m of it lies between the camera and the character ({haze25:F6})");
        check.Expect(MathF.Abs(haze80 - 0.092103f) < 1e-5f, $"...and zoomed to 80 m, the same depth of it ({haze80:F6})");
        check.Expect(VolumetricFog.Grids[Array.IndexOf(VolumetricFog.Levels, "medium")].D == MediumSlices, "Medium detail is the 64 slices the fade is counted in");

        VolumetricFog.Enabled.V = volWas; Fog.Enabled.V = fogWas; Fog.Origin.V = originWas; Fog.Height.V = heightWas;
    }
}
