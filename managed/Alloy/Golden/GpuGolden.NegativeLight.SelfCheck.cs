using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// A negative light (the Darkness spell's) through the deferred rig: its colour reaches the walk signed, linearised as
/// <c>GetColorSpaceFromSRGB</c> does (a channel at or under 0 passes unchanged), and the pixel is floored at black.
/// Oracle: the white lamp alone, hand-scaled by the share the negative lamp beside it takes away.
/// </summary>
internal static partial class GpuGolden
{
    private static void NegativeLightSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        static RigPixel Lit(RigPixel p) => p with { DiffT = Vector3.One };
        DeferredRigScene Lamps(params float[] grey)
        {
            DeferredRigScene sc = ParityScene(Vector3.UnitZ, -1f, 1f, Lit);
            float[] lights = [];
            foreach (float g in grey)
                lights = [.. lights, ParityLamp.At.X, ParityLamp.At.Y, ParityLamp.At.Z, ParityLamp.Reach, g, g, g, -1f];
            sc.Lights = lights;
            return sc;
        }

        if (ParityCentre(vk, check, log, Lamps(1f), "negative light: the white lamp alone") is not { } white) return;
        check.Expect(white.Y > 1e-3f, FormattableString.Invariant($"negative light: the white lamp lights the wall ({white.Y:F4})"));

        if (ParityCentre(vk, check, log, Lamps(1f, -1f), "negative light: white and (-1,-1,-1)") is { } cancel)
            check.Expect(MathF.Abs(cancel.Y) <= 1e-3f * white.Y + 1e-5f,
                         FormattableString.Invariant($"negative light: a (-1,-1,-1) lamp beside a white one cancels it ({cancel.Y:F4}, white {white.Y:F4})"));

        if (ParityCentre(vk, check, log, Lamps(1f, -0.25f), "negative light: white and (-0.25,-0.25,-0.25)") is { } quarter)
            check.Expect(MathF.Abs(quarter.Y - 0.75f * white.Y) <= 2e-3f * white.Y + 1e-5f,
                         FormattableString.Invariant($"negative light: a -0.25 lamp takes a quarter, linear as the engine passes it ({quarter.Y:F4}, expected {0.75f * white.Y:F4})"));

        if (ParityCentre(vk, check, log, Lamps(-1f), "negative light: (-1,-1,-1) alone") is { } alone)
            check.Expect(alone.X >= 0f && alone.Y >= 0f && alone.Z >= 0f && alone.Y <= 1e-5f,
                         FormattableString.Invariant($"negative light: a negative lamp alone floors the pixel at black, never below ({alone.X:F5}, {alone.Y:F5}, {alone.Z:F5})"));
    }
}
