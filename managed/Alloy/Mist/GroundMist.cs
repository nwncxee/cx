using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy;

/// <summary>Ground mist: a depth of mist per metre column over the walkmesh around the player, which the volumetric
/// fog reads as a density on the floor. ponytail: a seam with no model yet; every member answers "no mist".</summary>
internal static class GroundMist
{
    /// <summary>The field's lifetime owner: replaced whole at an area edge.</summary>
    private static GroundMistState _s = new();

    /// <summary>Take in one tile part's walkmesh, as the area seats it: its faces in model space under
    /// <paramref name="world"/>, each with its surfacemat.2da row. The floors and walls of every column come from these.</summary>
    internal static void TakeWalk(MeshDef walk, Matrix4x4 world) { }

    /// <summary>Centre the field on the player's feet, laying the floor each column takes from the walkmesh.</summary>
    internal static void Follow(Vector3 feet) { }

    /// <summary>The area's fog, as its current palette sets it: the even floor of mist it lays.</summary>
    internal static void AreaFog(bool on, float amount) { }

    /// <summary>Lay mist once: <paramref name="depth"/> metres over a disc of <paramref name="radius"/> at the
    /// floor below <paramref name="at"/>.</summary>
    internal static void Place(Vector3 at, float radius, float depth) { }

    /// <summary>Advance the field by <paramref name="dt"/> seconds under the area's <paramref name="wind"/>.</summary>
    internal static void Step(float dt, Vector2 wind) { }

    /// <summary>A detonation at <paramref name="at"/> that blows the mist out of <paramref name="radius"/>.</summary>
    internal static void Blast(Vector3 at, float radius) { }

    /// <summary>The depth of mist, in metres, over the world column at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal static float At(float x, float y) => 0f;

    /// <summary>The floor height the column at (<paramref name="x"/>, <paramref name="y"/>) lays its mist on; NaN for none.</summary>
    internal static float FloorAt(float x, float y) => float.NaN;

    /// <summary>Does the fog volume draw mist this frame, with or without rain?</summary>
    internal static bool Shows(bool raining) => false;

    /// <summary>The area edge: the old area's mist and walkmesh go with it.</summary>
    internal static void AreaEdge() => _s = new GroundMistState();
}

/// <summary>Everything the ground mist field holds for one area.</summary>
internal sealed class GroundMistState
{
}
