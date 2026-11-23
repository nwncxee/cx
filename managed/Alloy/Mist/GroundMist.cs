using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;

namespace CX.Alloy;

/// <summary>Ground mist: a depth of mist per metre column over the walkmesh around the player, which the volumetric
/// fog reads as a density on the floor. Area fog lays an even floor of it and each mist emitter a disc.</summary>
internal static partial class GroundMist
{
    /// <summary>The field's lifetime owner: replaced whole at an area edge.</summary>
    private static GroundMistState _s = new();

    /// <summary>The columns along each side of the field, one metre each.</summary>
    internal const int Side = 256;

    /// <summary>The field recentres on the feet in steps of this many metres, so the floors are laid again rarely.</summary>
    private const int Snap = 16;

    /// <summary>A column's floor is its highest face at or below the feet plus this many metres.</summary>
    private const float Headroom = 2f;

    /// <summary>The feet climb or drop this far before the floors are laid again, for a deck over a road.</summary>
    private const float Relay = 1f;

    /// <summary>Metres of mist one metre of the area's fog amount lays, and the deepest floor area fog lays.</summary>
    private const float DepthPerAmount = 0.1f, MaxArea = 2f;

    /// <summary>The depth a mist emitter's disc fills to, and the limits on the disc's radius.</summary>
    private const float SourceDepth = 1f, MinReach = 2f, MaxReach = 10f;

    /// <summary>Seconds a column takes to rise toward what lays it, and to settle once nothing does.</summary>
    private const float RiseTau = 2f, SettleTau = 20f;

    /// <summary>Below this depth a column holds no mist.</summary>
    private const float Trace = 1e-4f;

    /// <summary>How many emitters one frame may feed; the rest that frame add nothing.</summary>
    internal const int MaxSources = 64;

    /// <summary>Take in one tile part's walkmesh, as the area seats it: its faces in model space under
    /// <paramref name="world"/>, each with its surfacemat.2da row. The floors of every column come from these.</summary>
    internal static void TakeWalk(MeshDef walk, Matrix4x4 world)
    {
        GroundMistState s = _s;
        foreach (FaceDef f in walk.Faces)
        {
            if (TileBuild.Solid(f.Surface)) continue;
            if ((uint)f.V0 >= (uint)walk.Positions.Length || (uint)f.V1 >= (uint)walk.Positions.Length
                || (uint)f.V2 >= (uint)walk.Positions.Length) continue;
            s.AddFloor(new MistFace(Vector3.Transform(walk.Positions[f.V0], world), Vector3.Transform(walk.Positions[f.V1], world),
                                    Vector3.Transform(walk.Positions[f.V2], world)));
        }
        s.Stale = true;
    }

    /// <summary>Centre the field on the player's feet, laying the floor each column takes from the walkmesh.</summary>
    internal static void Follow(Vector3 feet)
    {
        GroundMistState s = _s;
        int ox = (int)MathF.Floor(feet.X / Snap) * Snap - Side / 2, oy = (int)MathF.Floor(feet.Y / Snap) * Snap - Side / 2;
        if (s.Laid && !s.Stale && ox == s.Ox && oy == s.Oy && MathF.Abs(feet.Z - s.FeetZ) < Relay) return;
        int oldX = s.Ox, oldY = s.Oy;
        bool had = s.Laid;
        s.Ox = ox; s.Oy = oy; s.FeetZ = feet.Z; s.Laid = true; s.Stale = false;
        LayFloors(s);
        Array.Clear(s.Spare);
        float area = s.AreaLevel;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int c = y * Side + x;
                if (float.IsNaN(s.Floor[c])) continue;
                int px = x + ox - oldX, py = y + oy - oldY, from = py * Side + px;
                s.Spare[c] = had && (uint)px < Side && (uint)py < Side ? s.Depth[from] : area;
            }
        (s.Depth, s.Spare) = (s.Spare, s.Depth);
        s.Gen++;
    }

    /// <summary>Each column's floor: the highest face over its centre at or below the feet plus the headroom.</summary>
    private static void LayFloors(GroundMistState s)
    {
        Array.Fill(s.Floor, float.NaN);
        float top = s.FeetZ + Headroom;
        for (int t = 0; t < s.Tris; t++)
        {
            (Vector3 a, Vector3 b, Vector3 c) = s.Tri[t];
            int x0 = Math.Max((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))) - s.Ox, 0);
            int x1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))) - s.Ox, Side - 1);
            int y0 = Math.Max((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))) - s.Oy, 0);
            int y1 = Math.Min((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))) - s.Oy, Side - 1);
            float det = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            if (MathF.Abs(det) < 1e-8f) continue;
            float inv = 1f / det;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = s.Ox + x + 0.5f - a.X, py = s.Oy + y + 0.5f - a.Y;
                    float u = (px * (c.Y - a.Y) - (c.X - a.X) * py) * inv;
                    float v = ((b.X - a.X) * py - px * (b.Y - a.Y)) * inv;
                    const float Edge = -1e-4f;
                    if (u < Edge || v < Edge || u + v > 1f - Edge) continue;
                    float z = a.Z + u * (b.Z - a.Z) + v * (c.Z - a.Z);
                    int at = y * Side + x;
                    if (z <= top && (float.IsNaN(s.Floor[at]) || z > s.Floor[at])) s.Floor[at] = z;
                }
        }
    }

    /// <summary>The area's fog, as its current palette sets it: the even floor of mist it lays.</summary>
    internal static void AreaFog(bool on, float amount)
    {
        GroundMistState s = _s;
        s.AreaLevel = on ? Math.Clamp(amount * DepthPerAmount, 0f, MaxArea) : 0f;
        if (s.Stepped) return;
        for (int c = 0; c < s.Depth.Length; c++) s.Depth[c] = float.IsNaN(s.Floor[c]) ? 0f : s.AreaLevel;
        s.Any = s.AreaLevel > Trace;
        s.Gen++;
    }

    /// <summary>Lay mist once: <paramref name="depth"/> metres over a disc of <paramref name="radius"/> at the
    /// floor below <paramref name="at"/>.</summary>
    internal static void Place(Vector3 at, float radius, float depth)
    {
        GroundMistState s = _s;
        if (!s.Laid || depth <= 0f) return;
        Disc(s, at, radius, s.Depth, depth, true);
        s.Any = true;
        s.Gen++;
    }

    /// <summary>One frame of a mist emitter at <paramref name="at"/> reaching <paramref name="reach"/>: it fills the
    /// disc below it toward <see cref="SourceDepth"/> while it goes on feeding.</summary>
    internal static void Source(Vector3 at, float reach)
    {
        GroundMistState s = _s;
        if (s.Sources >= MaxSources) return;
        s.Source[s.Sources++] = new Vector4(at, Math.Clamp(reach, MinReach, MaxReach));
    }

    /// <summary>Into each floored column of the disc of <paramref name="radius"/> around <paramref name="at"/>, whose floor
    /// lies no higher than a metre over it: <paramref name="value"/> added to <paramref name="into"/>, or raised to.</summary>
    private static void Disc(GroundMistState s, Vector3 at, float radius, float[] into, float value, bool add)
    {
        int x0 = Math.Max((int)MathF.Floor(at.X - radius) - s.Ox, 0), x1 = Math.Min((int)MathF.Ceiling(at.X + radius) - s.Ox, Side - 1);
        int y0 = Math.Max((int)MathF.Floor(at.Y - radius) - s.Oy, 0), y1 = Math.Min((int)MathF.Ceiling(at.Y + radius) - s.Oy, Side - 1);
        float r2 = radius * radius;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float dx = s.Ox + x + 0.5f - at.X, dy = s.Oy + y + 0.5f - at.Y;
                int c = y * Side + x;
                if (dx * dx + dy * dy > r2 || float.IsNaN(s.Floor[c]) || s.Floor[c] > at.Z + 1f) continue;
                into[c] = add ? into[c] + value : MathF.Max(into[c], value);
            }
    }

    /// <summary>Advance the field by <paramref name="dt"/> seconds under the area's <paramref name="wind"/>.</summary>
    internal static void Step(float dt, Vector2 wind)
    {
        GroundMistState s = _s;
        int sources = s.Sources;
        s.Sources = 0;
        if (!VolumetricFog.Enabled.V || !s.Laid || dt <= 0f) return;
        s.Stepped = true;
        Array.Fill(s.Target, s.AreaLevel);
        for (int i = 0; i < sources; i++)
        {
            Vector4 src = s.Source[i];
            Disc(s, new Vector3(src.X, src.Y, src.Z), src.W, s.Target, SourceDepth, false);
        }
        float up = 1f - MathF.Exp(-dt / RiseTau), down = 1f - MathF.Exp(-dt / SettleTau);
        bool any = false, moved = false;
        for (int c = 0; c < s.Depth.Length; c++)
        {
            if (float.IsNaN(s.Floor[c])) { s.Depth[c] = 0f; continue; }
            float d = s.Depth[c], t = s.Target[c];
            d += (t - d) * (d < t ? up : down);
            if (d < Trace) d = 0f;
            moved |= d != s.Depth[c];
            s.Depth[c] = d;
            any |= d > 0f;
        }
        s.Any = any;
        if (moved) s.Gen++;
    }

    /// <summary>A detonation at <paramref name="at"/> that blows the mist out of <paramref name="radius"/>.</summary>
    internal static void Blast(Vector3 at, float radius) { }

    /// <summary>The field's column for world (<paramref name="x"/>, <paramref name="y"/>), or -1 outside it.</summary>
    private static int Column(GroundMistState s, float x, float y)
    {
        if (!s.Laid) return -1;
        int cx = (int)MathF.Floor(x) - s.Ox, cy = (int)MathF.Floor(y) - s.Oy;
        return (uint)cx < Side && (uint)cy < Side ? cy * Side + cx : -1;
    }

    /// <summary>The depth of mist, in metres, over the world column at (<paramref name="x"/>, <paramref name="y"/>);
    /// past the field's edge, the area's own floor of it.</summary>
    internal static float At(float x, float y)
    {
        GroundMistState s = _s;
        int c = Column(s, x, y);
        return c < 0 ? s.AreaLevel : s.Depth[c];
    }

    /// <summary>The floor height the column at (<paramref name="x"/>, <paramref name="y"/>) lays its mist on; NaN for none.</summary>
    internal static float FloorAt(float x, float y)
    {
        GroundMistState s = _s;
        int c = Column(s, x, y);
        return c < 0 ? float.NaN : s.Floor[c];
    }

    /// <summary>Does the fog volume draw mist this frame, with or without rain?</summary>
    internal static bool Shows(bool raining) => VolumetricFog.Enabled.V && _s.Any;

    /// <summary>The area edge: the old area's mist and walkmesh go with it.</summary>
    internal static void AreaEdge() => _s = new GroundMistState();
}

/// <summary>Everything the ground mist field holds for one area: its floor faces, the columns around the player and
/// this frame's emitters.</summary>
internal sealed class GroundMistState
{
    /// <summary>The area's floor faces, in world space, and how many.</summary>
    public MistFace[] Tri = new MistFace[1024];
    public int Tris;

    /// <summary>The field's corner in whole metres, the feet height its floors were laid for, whether they are, and
    /// whether faces came in since.</summary>
    public int Ox, Oy;
    public float FeetZ;
    public bool Laid, Stale;

    /// <summary>Bumped whenever a floor or a depth changes, so the upload sends the field only when it moved.</summary>
    public int Gen;

    /// <summary>The scene the live walk reads, how far into its parts table it has looked, and the frame it last stepped.</summary>
    public uint Scene;
    public int Walked, Seq = -1;

    /// <summary>Per column, row-major from the corner: its floor (NaN for none), its depth of mist and this step's target.</summary>
    public float[] Floor = new float[GroundMist.Side * GroundMist.Side];
    public float[] Depth = new float[GroundMist.Side * GroundMist.Side];
    public float[] Spare = new float[GroundMist.Side * GroundMist.Side];
    public float[] Target = new float[GroundMist.Side * GroundMist.Side];

    /// <summary>The depth the area's fog lays, whether the field has stepped since the area began, and whether any column holds mist.</summary>
    public float AreaLevel;
    public bool Stepped, Any;

    /// <summary>This frame's emitters: position and disc radius each.</summary>
    public Vector4[] Source = new Vector4[GroundMist.MaxSources];
    public int Sources;

    /// <summary>One floor face, the list grown as an area's walkmesh comes in.</summary>
    public void AddFloor(in MistFace f)
    {
        if (Tris == Tri.Length) Array.Resize(ref Tri, Tri.Length * 2);
        Tri[Tris++] = f;
    }
}

/// <summary>One floor face of the walkmesh, its three corners in world space.</summary>
internal readonly record struct MistFace(Vector3 A, Vector3 B, Vector3 C);
