using System.Numerics;

namespace CX.Engine;

/// <summary>One light in the area-light upload: position and range, colour, and the cube slot in the spare lane.</summary>
public struct AreaLightRec
{
    /// <summary>Position xyz and reach.</summary>
    public Vector4 PosRange;
    /// <summary>Colour.</summary>
    public Vector3 Color;
    /// <summary>The light's shadow-cube slot, -1 for none, plus the static bit.</summary>
    public float Slot;
}

/// <summary>One mover box: its corners and the lone mover's radius.</summary>
public struct MoverBox
{
    /// <summary>Minimum corner.</summary>
    public Vector3 Min;
    /// <summary>Maximum corner.</summary>
    public Vector3 Max;
    /// <summary>The lone mover's own radius, -1 once boxes merged.</summary>
    public float Radius;
}

/// <summary>One caster-gather source record: the draw's range, its owner, batch and detail runs.</summary>
public struct CasterSrcRec
{
    /// <summary>Index count; 0 draws nothing.</summary>
    public uint IndexCount;
    /// <summary>First index, in elements.</summary>
    public uint FirstIndex;
    /// <summary>Base vertex.</summary>
    public uint BaseVertex;
    /// <summary>Ownership slot.</summary>
    public uint Slot;
    /// <summary>Batch the draw belongs to.</summary>
    public uint Batch;
    /// <summary>First output position of the draw's batch.</summary>
    public uint OutBase;
    /// <summary>1 for a mover this frame.</summary>
    public uint SetFlag;
    /// <summary>Index count of detail level 1.</summary>
    public uint Count1;
    /// <summary>First index of detail level 1.</summary>
    public uint First1;
    /// <summary>Index count of detail level 2.</summary>
    public uint Count2;
    /// <summary>First index of detail level 2.</summary>
    public uint First2;
}

/// <summary>One caster-gather view record, as the layered dispatch and depth stages read it.</summary>
public struct CasterViewRec
{
    /// <summary>The view-projection matrix.</summary>
    public Matrix4x4 Vp;
    /// <summary>Focus point.</summary>
    public Vector3 Focus;
    /// <summary>Reach.</summary>
    public float Reach;
    /// <summary>View direction.</summary>
    public Vector3 Dir;
    /// <summary>Distance along the direction.</summary>
    public float Along;
    /// <summary>1 when the receiver cull applies to this view.</summary>
    public float Receiver;
    /// <summary>1 for a point-light view.</summary>
    public float Point;
    /// <summary>The caster set the view admits.</summary>
    public float Set;
    /// <summary>The layer the view renders to.</summary>
    public float Layer;
    /// <summary>First detail threshold.</summary>
    public float LodR1;
    /// <summary>Second detail threshold.</summary>
    public float LodR2;
    /// <summary>Unused lane.</summary>
    public float SpareA;
    /// <summary>Unused lane.</summary>
    public float SpareB;
}

/// <summary>One cull-pass source record.</summary>
public struct CullSrcRec
{
    /// <summary>Index count.</summary>
    public uint IndexCount;
    /// <summary>First index, in elements.</summary>
    public uint FirstIndex;
    /// <summary>Base vertex.</summary>
    public uint BaseVertex;
    /// <summary>Sphere slot.</summary>
    public uint Slot;
    /// <summary>Batch.</summary>
    public uint Batch;
    /// <summary>First output position of the batch.</summary>
    public uint OutBase;
}

/// <summary>One indirect draw command.</summary>
public struct DrawCmdRec
{
    /// <summary>Index count.</summary>
    public uint Count;
    /// <summary>Instance count.</summary>
    public uint InstanceCount;
    /// <summary>First index.</summary>
    public uint FirstIndex;
    /// <summary>Base vertex.</summary>
    public uint BaseVertex;
    /// <summary>Base instance.</summary>
    public uint BaseInstance;
}

/// <summary>One bounding sphere.</summary>
public struct SphereRec
{
    /// <summary>Centre.</summary>
    public Vector3 Centre;
    /// <summary>Radius.</summary>
    public float Radius;
}

/// <summary>One grass face: three corners.</summary>
public struct GrassTriRec
{
    /// <summary>First corner.</summary>
    public Vector3 A;
    /// <summary>Second corner.</summary>
    public Vector3 B;
    /// <summary>Third corner.</summary>
    public Vector3 C;
}

/// <summary>One lens-flare record: three vec4s and two spare lanes.</summary>
public struct HaloRec
{
    /// <summary>Light position.</summary>
    public Vector3 Pos;
    /// <summary>Occlusion softness.</summary>
    public float Softness;
    /// <summary>Display colour with the fade folded in.</summary>
    public Vector3 Tint;
    /// <summary>Flare radius.</summary>
    public float FlareRadius;
    /// <summary>First flare element's size times the halo size.</summary>
    public float Size;
    /// <summary>First flare element's position along the axis.</summary>
    public float FlarePosition;
    /// <summary>Unused lanes.</summary>
    public Vector2 Spare;
}

/// <summary>One highlight-glow quad.</summary>
public struct GlowRec
{
    /// <summary>Centre xyz and alpha.</summary>
    public Vector4 Centre;
    /// <summary>Half-axis U.</summary>
    public Vector4 AxisU;
    /// <summary>Half-axis V.</summary>
    public Vector4 AxisV;
    /// <summary>Colour.</summary>
    public Vector4 Colour;
}

/// <summary>One particle record: seven vec4s, the two colours carried as raw bits.</summary>
public struct ParticleRec
{
    /// <summary>Position.</summary>
    public Vector3 Pos;
    /// <summary>Colour bits.</summary>
    public float Col;
    /// <summary>Far end.</summary>
    public Vector3 Far;
    /// <summary>Far colour bits.</summary>
    public float ColFar;
    /// <summary>Right axis.</summary>
    public Vector3 Right;
    /// <summary>Softness.</summary>
    public float Soft;
    /// <summary>Up axis.</summary>
    public Vector3 Up;
    /// <summary>Render type.</summary>
    public float Type;
    /// <summary>Centre.</summary>
    public Vector3 Centre;
    /// <summary>Unused lane.</summary>
    public float CentreW;
    /// <summary>Texture window, left.</summary>
    public float U0;
    /// <summary>Texture window, right.</summary>
    public float U1;
    /// <summary>Texture window, top.</summary>
    public float V0;
    /// <summary>Texture window, bottom.</summary>
    public float V1;
    /// <summary>Where the centre was last frame.</summary>
    public Vector3 Prev;
    /// <summary>Unused lane.</summary>
    public float PrevW;
}

/// <summary>One texel of the weather track texture.</summary>
public struct TrackTexel
{
    /// <summary>Print depth.</summary>
    public float Depth;
    /// <summary>When the print was stamped.</summary>
    public float Stamp;
    /// <summary>Tile repeat the print belongs to, x.</summary>
    public float TileX;
    /// <summary>Tile repeat the print belongs to, y.</summary>
    public float TileY;
}

/// <summary>One RGBA8 texel.</summary>
public struct TexelRec
{
    /// <summary>Red.</summary>
    public byte R;
    /// <summary>Green.</summary>
    public byte G;
    /// <summary>Blue.</summary>
    public byte B;
    /// <summary>Alpha.</summary>
    public byte A;
}

/// <summary>One resident grass face as the scatter stage reads it: three corners, each followed by one word.</summary>
public struct GrassFace
{
    /// <summary>First corner.</summary>
    public Vector3 A;
    /// <summary>Blades before this face in its patch.</summary>
    public int FirstBlade;
    /// <summary>Second corner.</summary>
    public Vector3 B;
    /// <summary>The face's scatter seed.</summary>
    public uint Seed;
    /// <summary>Third corner.</summary>
    public Vector3 C;
    /// <summary>Blades on this face.</summary>
    public int BladeCount;
}
