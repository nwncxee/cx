using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The scene tint a <c>tinted</c> emitter's colour pair is multiplied by, sampled once at the
/// emitter's origin out of Alloy's own light rig.</summary>
internal static unsafe partial class Particles
{
    /// <summary>The engine's stand-in for "no distance at all": a light sampled at its own position attenuates
    /// by this over its reach rather than by the limit.</summary>
    private const float TintZeroDistance = 1e-6f;
    /// <summary>A light with no reach: it attenuates to this at its own position, and is given
    /// <see cref="TintPointRange"/> of reach everywhere else.</summary>
    private const float TintPointAtten = 0.9999f, TintPointRange = 0.01f;

    /// <summary>The scene tint at one world point, as <c>LightManager::GetEmitterLighting</c> @0x5c1970 sums
    /// it: each light's colour faded linearly over its own reach, the area pair added unattenuated, every
    /// channel clamped to 1. ponytail: a linear walk of the frame's lights per tinted emitter -- binning the
    /// rig per area cell is the upgrade, and <c>tint=</c> on the period line is the trip-wire.</summary>
    internal static Vector3 SceneTint(Vector3 origin)
    {
        int n = FrameInputs.LightCount;
        Vector4[] posRange = FrameInputs.AreaLightPosRange;
        Vector3[] colour = FrameInputs.AreaLightColor;
        _tintLights += n;
        Vector3 t = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            Vector4 pr = posRange[i];
            float dx = pr.X - origin.X, dy = pr.Y - origin.Y, dz = pr.Z - origin.Z;
            float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            float r = pr.W, a;
            if (d == 0f) a = r != 0f ? 1f - TintZeroDistance / r : TintPointAtten;
            else
            {
                a = 1f - d / (r != 0f ? r : TintPointRange);
                if (a < 0f) continue;
            }
            t += a * colour[i];
        }
        if (FrameInputs.HasAreaDiffuse) t += AsStored(FrameInputs.AreaDiffuse);
        if (FrameInputs.HasAreaAmbient) t += AsStored(FrameInputs.AreaAmbient);
        return Vector3.Min(t, Vector3.One);
    }

    /// <summary>An area term back in the space a light record stores it in. The rig linearises the pair for the
    /// shaders (<see cref="AlloyLights.SrgbGamma"/>); the tint is the one reader that wants the authored value,
    /// because what it multiplies is an authored vertex colour.</summary>
    private static Vector3 AsStored(Vector3 linear)
    {
        const float inv = 1f / AlloyLights.SrgbGamma;
        return new Vector3(MathF.Pow(MathF.Max(linear.X, 0f), inv),
                           MathF.Pow(MathF.Max(linear.Y, 0f), inv),
                           MathF.Pow(MathF.Max(linear.Z, 0f), inv));
    }
}
