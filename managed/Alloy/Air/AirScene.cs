using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The area's air over the ground mist grid: the layers its look hangs, the fronts a wind change sends and
/// what stirs it. Cold on area load, stepped once a frame on the game thread.</summary>
internal static class AirScene
{
    /// <summary>Take <paramref name="look"/> for the area just laid, by night or by day.</summary>
    internal static void Enter(AirLook look, bool night) { }

    /// <summary>Day turns to night or back.</summary>
    internal static void Night(bool night) { }

    /// <summary>A tree trunk standing at <paramref name="at"/>.</summary>
    internal static void Trunk(Vector2 at) { }

    /// <summary>A lamp at <paramref name="at"/> reaching <paramref name="reach"/> metres.</summary>
    internal static void Lamp(Vector3 at, float reach) { }

    /// <summary>A blast at <paramref name="at"/> of <paramref name="radius"/> metres.</summary>
    internal static void Blast(Vector3 at, float radius) { }

    /// <summary>Advance the air <paramref name="dt"/> seconds under the area's <paramref name="wind"/>.</summary>
    internal static void Step(float dt, Vector2 wind) { }

    /// <summary>How much of <paramref name="layer"/> hangs at <paramref name="at"/>.</summary>
    internal static float Density(AirLayer layer, Vector3 at) => 0f;

    /// <summary>The particles of <paramref name="layer"/>.</summary>
    internal static ReadOnlySpan<AirParticle> Particles(AirLayer layer) => ReadOnlySpan<AirParticle>.Empty;

    /// <summary>The gust front crossing the air now.</summary>
    internal static AirFront Front => default;

    /// <summary>The wisps' shape now.</summary>
    internal static AirWisps Wisps => default;

    /// <summary>The air's own motion at a ground point, in metres a second.</summary>
    internal static Vector2 Motion(float x, float y) => Vector2.Zero;

    /// <summary>Drop the area's air.</summary>
    internal static void Reset() { }
}
