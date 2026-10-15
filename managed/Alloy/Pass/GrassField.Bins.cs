using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The grass pass's frustum test, <c>BinOutside</c> @0x47a620's sphere-against-planes loop.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>The six frustum planes the database's patch cull tests against.</summary>
    private static readonly Vector4[] _binPlanes = new Vector4[6];

    /// <summary>Sphere against six inward-facing planes: outside if it is wholly behind any one of them, which
    /// is <c>BinOutside</c>'s loop with the engine's outward sign flipped.</summary>
    private static bool SphereVisible(Vector3 centre, float radius, ReadOnlySpan<Vector4> planes)
    {
        for (int i = 0; i < 6; i++)
        {
            float d = planes[i].X * centre.X + planes[i].Y * centre.Y + planes[i].Z * centre.Z
                    + planes[i].W;
            if (d < -radius) return false;
        }
        return true;
    }
}
