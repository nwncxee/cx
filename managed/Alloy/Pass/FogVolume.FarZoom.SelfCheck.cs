using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Plain regression tests beside the far-zoom approved examples: what already held before the fog followed the
/// zoom and must go on holding. Oracle: figures hand-worked off ln 100 = 4.60517 over a 45 m ramp (0.204674 /m).</summary>
internal static unsafe partial class FogVolume
{
    private static void FarZoomRegressions(Checker check)
    {
        bool volWas = VolumetricFog.Enabled.V, fogWas = Fog.Enabled.V, originWas = Fog.Origin.V;
        float heightWas = Fog.Height.V;

        // inside the engine's 25 m camera limit the fog is the area's own, at the limit and nearer (E3)
        foreach (float c in new[] { 25f, 10f, 0f, -1f })
        {
            Medium m = MediumFor(true, 15f, 60f, 0f, 0f, c);
            check.Expect(MathF.Abs(m.Start - 15f) < 1e-4f && MathF.Abs(m.End - 60f) < 1e-4f && MathF.Abs(m.Wall - 0.204674f) < 1e-5f,
                         $"a camera {c:F0} m from the player leaves the fog at the area's 15..60 m ({m})");
            check.Expect(MathF.Abs(OpaqueDistance(true, 15f, 60f, 0f, c) - 60f) < 1e-3f, $"...and opaque at 60 m ({c:F0} m)");
            check.Expect(MathF.Abs(Grid(85f, c).Near - 1f) < 1e-6f, $"...and the grid starting a metre from the eye ({c:F0} m)");
        }

        // the plain fog is untouched at any zoom (E6): its far plane is the fog's end plus the camera's distance
        VolumetricFog.Enabled.V = false; Fog.Enabled.V = true; Fog.Origin.V = true; Fog.Height.V = 0f;
        check.Expect(MathF.Abs(SceneFog.FarForFog(45f, true, 15f, 60f, 80f) - 140f) < 1e-3f, "volumetric fog off: zoomed to 80 m the ramp still ends 60 m from the player");
        Fog.Origin.V = false;
        check.Expect(MathF.Abs(SceneFog.FarForFog(45f, true, 15f, 60f, 80f) - 45f) < 1e-3f, "...and measured from the eye it keeps the engine's far plane");

        // an area with no fog of its own stays clear at haze 0 (E7)
        Medium air = MediumFor(false, 0f, 0f, 300f, 0f, 80f);
        check.Expect(air.Wall <= 0f && air.Haze <= 0f, $"no area fog and no haze: zoomed to 80 m the air holds nothing ({air})");
        check.Expect(float.IsPositiveInfinity(OpaqueDistance(false, 15f, 60f, 0f, 80f)), "...and nothing is hidden at any distance");

        VolumetricFog.Enabled.V = volWas; Fog.Enabled.V = fogWas; Fog.Origin.V = originWas; Fog.Height.V = heightWas;
    }
}
