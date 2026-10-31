using System.Collections.Generic;
using System.Numerics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The world's depth convention: every depth target the world programs draw into, and every matrix that
/// feeds them. Casters and the GUI keep GL's depth through the translator's remap and are not described here.</summary>
internal static class WorldDepth
{
    /// <summary>What a world depth target is cleared to: the far plane.</summary>
    internal const float Clear = 1f;

    /// <summary>The test a fragment nearer than the stored depth passes.</summary>
    internal const CompareOp Nearer = CompareOp.Less;

    /// <summary>The test a fragment at or nearer than the stored depth passes.</summary>
    internal const CompareOp NearerOrEqual = CompareOp.LessEqual;

    /// <summary>GL projection <paramref name="gl"/> as the world programs draw with it.</summary>
    internal static Matrix4x4 Fold(in Matrix4x4 gl) => gl;

    /// <summary>A world program's two stages for Vulkan: its clip z comes off a folded matrix, so it passes through.</summary>
    internal static VkProgramText Translate(string vs, string fs, IReadOnlyDictionary<string, uint> units)
        => VkGlsl.Translate(vs, fs, units, glDepth: false);
}
