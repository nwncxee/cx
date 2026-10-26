using System;
using System.Numerics;
using CX.Alloy.Db;

namespace CX.Alloy;

internal static unsafe partial class Beams
{
    /// <summary>The beams the Vulkan frame draws this pass, under the same gates as <see cref="Draw"/>: the light's
    /// direction, the projection's alpha and how many of the captured volumes have resident geometry (the rest are
    /// counted, as GL counts them).</summary>
    internal static int StageVk(out Vector3 dir, out float alpha, out float zClip)
    {
        alpha = _alpha != null ? MathF.Abs(*_alpha) : DefaultAlpha;
        zClip = WorldZClip;
        dir = default;
        if (Disabled || _off || _count == 0 || (_enable != null && *_enable == 0) || !DirectionDb(out dir)) return 0;
        int staged = 0;
        for (int i = 0; i < _count; i++)
            if (MeshArena.Runs(_beams[i].Range, 0, out _, out _, out int count, out _) && count > 0) _sBeam[staged++] = i;
            else _dbSkipped++;
        _drawn += staged;
        return staged;
    }

    /// <summary>Staged beam <paramref name="k"/>'s part and arena range.</summary>
    internal static (int Part, int Range) VkBeam(int k) => (_beams[_sBeam[k]].Part, _beams[_sBeam[k]].Range);

    /// <summary>The beam colour: white light at the projection's alpha, linear.</summary>
    internal static float VkTint => MathF.Pow(TintRgb, AlloyLights.SrgbGamma);

    /// <summary>The beam stages' text, for the Vulkan program.</summary>
    internal static string VkVs => BeamDbVs;
    internal static string VkFs => BeamFs;
}
