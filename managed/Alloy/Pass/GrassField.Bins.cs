using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The grass pass's frustum test, <c>BinOutside</c> @0x47a620's sphere-against-planes loop.</summary>
internal static unsafe partial class GrassField
{
    /// <summary>The six frustum planes the database's patch cull tests against.</summary>
    private static readonly CullPlane[] _binPlanes = new CullPlane[6];

    /// <summary>Sphere against six inward-facing planes: outside if it is wholly behind any one of them, which
    /// is <c>BinOutside</c>'s loop with the engine's outward sign flipped.</summary>
    private static bool SphereVisible(Vector3 centre, float radius, ReadOnlySpan<CullPlane> planes)
    {
        for (int i = 0; i < 6; i++)
            if (planes[i].SignedDistance(centre) < -radius) return false;
        return true;
    }
}
