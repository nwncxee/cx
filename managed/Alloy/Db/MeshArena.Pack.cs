using System;
using System.Numerics;

namespace CX.Alloy.Db;

/// <summary>The arena's two PACKED streams: a vertex's tangent frame and its vertex paint, one 32-bit word
/// each. Both are written by the interleave and read straight back by the lit vertex stage, so the packing is
/// the whole contract between <c>MeshDef</c> and the GLSL -- there is no intermediate float form.</summary>
public static unsafe partial class MeshArena
{
    /// <summary>What the paint word holds for a mesh with no colour stream: opaque white, the identity for a
    /// multiply, so a consumer needs no "does this mesh carry paint" branch.</summary>
    internal const uint PaintWhite = 0xFFFFFFFFu;   // not-an-offset: four RGBA8 channels at full scale

    /// <summary>One tangent frame as <c>GL_INT_2_10_10_10_REV</c>: the unit tangent's xyz in three
    /// signed-normalized 10-bit lanes and its handedness in the 2-bit lane (+1 as 1, -1 as 3, two's
    /// complement). <b>Zero is "this mesh has no frame"</b> -- a real frame always has a non-zero handedness
    /// lane -- which is the one bit the lit stage tests to keep its derivative fallback.</summary>
    internal static uint PackTangent(Vector4 t)
    {
        float len2 = t.X * t.X + t.Y * t.Y + t.Z * t.Z;
        if (!(len2 > 1e-16f)) return 0;
        float s = 1f / MathF.Sqrt(len2);
        return Lane(t.X * s) | (Lane(t.Y * s) << 10) | (Lane(t.Z * s) << 20)
             | ((t.W < 0f ? 3u : 1u) << 30);
    }

    /// <summary>What the GPU reads back out of <paramref name="packed"/>: the sign-extended lanes over 511,
    /// and the handedness as +1/-1 (0 for the zero word, which is no frame at all).</summary>
    internal static Vector4 UnpackTangent(uint packed)
    {
        if (packed == 0) return default;
        int w = (int)(packed >> 30) & 3;
        return new Vector4(Signed(packed) / 511f, Signed(packed >> 10) / 511f, Signed(packed >> 20) / 511f,
                           w == 0 ? 0f : w <= 1 ? 1f : -1f);
    }

    /// <summary>One signed-normalized 10-bit lane. GL reads it back as <c>c / 511</c>, so the code is the
    /// value scaled by 511 and rounded, kept in two's complement.</summary>
    private static uint Lane(float v) => (uint)(int)MathF.Round(Math.Clamp(v, -1f, 1f) * 511f) & 0x3FF;

    /// <summary>The low ten bits of <paramref name="lane"/> read as a two's-complement signed value.</summary>
    private static int Signed(uint lane)
    {
        int c = (int)(lane & 0x3FF);
        return c >= 512 ? c - 1024 : c;
    }

    /// <summary>The two packed words this range uploaded for vertex <paramref name="v"/>, read off the host
    /// MIRROR -- byte for byte what went to the GPU. The bring-up and self-check read; false = no such range
    /// or vertex.</summary>
    internal static bool StreamProbe(int range, int v, out uint tangent, out uint paint)
    {
        tangent = 0; paint = 0;
        lock (_gate)
        {
            if (!Alive(range)) return false;
            ref Range r = ref _ranges[range];
            if ((uint)v >= (uint)r.NV) return false;
            Section s = _sect[(int)r.Format];
            int o = (r.Base + v) * StrideOf[(int)r.Format];
            if (o + StrideOf[(int)r.Format] > s.Mirror.Length) return false;
            tangent = BitConverter.SingleToUInt32Bits(s.Mirror[o + TanWord(r.Format)]);
            paint = BitConverter.SingleToUInt32Bits(s.Mirror[o + PaintWord(r.Format)]);
            return true;
        }
    }

    /// <summary>UV set <paramref name="set"/> (1..3) as this range uploaded it for vertex <paramref name="v"/>, off
    /// the host MIRROR; false = no such range or vertex, or a format that carries no UV sets.</summary>
    internal static bool UvSetProbe(int range, int v, int set, out System.Numerics.Vector2 uv)
    {
        uv = default;
        lock (_gate)
        {
            if (!Alive(range) || set is < 1 or > 3) return false;
            ref Range r = ref _ranges[range];
            if ((uint)v >= (uint)r.NV || !CarriesUvSets(r.Format)) return false;
            Section s = _sect[(int)r.Format];
            int o = (r.Base + v) * StrideOf[(int)r.Format];
            if (o + StrideOf[(int)r.Format] > s.Mirror.Length) return false;
            int w = o + UvSetWord(r.Format) + 2 * (set - 1);
            uv = new System.Numerics.Vector2(s.Mirror[w], s.Mirror[w + 1]);
            return true;
        }
    }
}
