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
    internal const float Clear = 0f;

    /// <summary>The test a fragment nearer than the stored depth passes.</summary>
    internal const CompareOp Nearer = CompareOp.Greater;

    /// <summary>The test a fragment at or nearer than the stored depth passes.</summary>
    internal const CompareOp NearerOrEqual = CompareOp.GreaterEqual;

    /// <summary>GL projection or view-projection <paramref name="gl"/> as the world programs draw with it: clip z
    /// becomes <c>(w - z) / 2</c>, so the near plane lands at depth 1 and the far at 0. Folded in double, before
    /// any vertex is multiplied, which is where reversed-Z's precision comes from.</summary>
    internal static Matrix4x4 Fold(in Matrix4x4 gl)
    {
        Matrix4x4 m = gl;
        m.M13 = (float)(0.5 * ((double)gl.M14 - gl.M13));
        m.M23 = (float)(0.5 * ((double)gl.M24 - gl.M23));
        m.M33 = (float)(0.5 * ((double)gl.M34 - gl.M33));
        m.M43 = (float)(0.5 * ((double)gl.M44 - gl.M43));
        return m;
    }

    /// <summary>The GL matrix <see cref="Fold"/> made <paramref name="folded"/> from, for CPU geometry that reads GL's
    /// clip planes (frusta).</summary>
    internal static Matrix4x4 Unfold(in Matrix4x4 folded)
    {
        Matrix4x4 m = folded;
        m.M13 = (float)((double)folded.M14 - 2.0 * folded.M13);
        m.M23 = (float)((double)folded.M24 - 2.0 * folded.M23);
        m.M33 = (float)((double)folded.M34 - 2.0 * folded.M33);
        m.M43 = (float)((double)folded.M44 - 2.0 * folded.M43);
        return m;
    }

    /// <summary>GL matrix <paramref name="gl"/> as a world program draws with it into a map that keeps GL's window
    /// depth (a shadow cascade): clip z becomes <c>(z + w) / 2</c>.</summary>
    internal static Matrix4x4 FoldGl(in Matrix4x4 gl)
    {
        Matrix4x4 m = gl;
        m.M13 = (float)(0.5 * ((double)gl.M13 + gl.M14));
        m.M23 = (float)(0.5 * ((double)gl.M23 + gl.M24));
        m.M33 = (float)(0.5 * ((double)gl.M33 + gl.M34));
        m.M43 = (float)(0.5 * ((double)gl.M43 + gl.M44));
        return m;
    }

    /// <summary>A world program's two stages for Vulkan: its clip z comes off a folded matrix, so it passes through.</summary>
    internal static VkProgramText Translate(string vs, string fs, IReadOnlyDictionary<string, uint> units)
        => VkGlsl.Translate(vs, fs, units, glDepth: false);
}
