using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>One inward-facing cull plane: a unit normal and the offset that makes
/// <see cref="SignedDistance"/> the distance from the plane, positive on the inside. Laid out as the
/// <c>vec4</c> the shaders read, so a table of these casts straight into a std140 member.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct CullPlane(Vector3 Normal, float Offset)
{
    /// <summary>A plane nothing is ever outside, for a frustum with no direction to build one from.</summary>
    internal static CullPlane KeepsEverything => new(Vector3.UnitZ, float.PositiveInfinity);

    /// <summary>Where <paramref name="p"/> sits: positive inside the plane, negative outside it.</summary>
    internal float SignedDistance(Vector3 p) => Vector3.Dot(Normal, p) + Offset;
}
