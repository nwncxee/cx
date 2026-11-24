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

    /// <summary>How fast mist evens out over flat ground: the share of a depth difference one metre of depth sends a second.</summary>
    private const float FlowRate = 1f;

    /// <summary>How much harder a drop in the floor drives mist than the same difference in its depth: it runs off
    /// slopes and creeps on the flat, so a pool keeps its place while a hillside drains.</summary>
    private const float SlopeGain = 30f;

    /// <summary>The most of a height difference one flow step moves across one edge; past a quarter the step rings.</summary>
    private const float MaxShare = 0.24f;

    /// <summary>The highest floor step mist crosses between neighbouring columns: a deck's edge is a drop, not a slope.</summary>
    private const float MaxStep = 1f;

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
        s.Source[s.Sources++] = new MistSource(at, Math.Clamp(reach, MinReach, MaxReach));
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
            MistSource src = s.Source[i];
            Disc(s, src.At, src.Reach, s.Target, SourceDepth, false);
        }
        float up = 1f - MathF.Exp(-dt / RiseTau), down = 1f - MathF.Exp(-dt / SettleTau);
        bool moved = false;
        for (int c = 0; c < s.Depth.Length; c++)
        {
            float d = s.Depth[c], t = s.Target[c];
            float e = float.IsNaN(s.Floor[c]) ? 0f : d + (t - d) * (d < t ? up : down);
            if (e < Trace) e = 0f;
            moved |= e != d;
            s.Depth[c] = e;
        }
        moved |= Flow(s, dt);
        moved |= Drift(s, dt, wind);
        bool any = false;
        for (int c = 0; c < s.Depth.Length; c++)
        {
            if (s.Depth[c] < Trace) s.Depth[c] = 0f;
            any |= s.Depth[c] > 0f;
        }
        s.Any = any;
        if (moved) s.Gen++;
    }

    /// <summary>One step of mist running downhill: each column sends each linked neighbour it stands above a share of the
    /// difference, by its depth, every share scaled back so no column sends more than it holds. Mass is kept; true if any moved.</summary>
    private static bool Flow(GroundMistState s, float dt)
    {
        bool sent = false;
        float[] depth = s.Depth, scale = s.Spare, next = s.Target;
        float rate = FlowRate * dt;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int c = y * Side + x;
                float d = depth[c];
                if (d <= 0f) continue;
                float send = Send(s, c, x - 1, y, rate) + Send(s, c, x + 1, y, rate) + Send(s, c, x, y - 1, rate) + Send(s, c, x, y + 1, rate);
                scale[c] = send > d ? d / send : 1f;
                sent |= send > 0f;
                next[c] = d - MathF.Min(send, d);
            }
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int c = y * Side + x;
                if (depth[c] <= 0f) next[c] = 0f;
                next[c] += Take(s, c, x - 1, y, rate) + Take(s, c, x + 1, y, rate) + Take(s, c, x, y - 1, rate) + Take(s, c, x, y + 1, rate);
            }
        (s.Depth, s.Target) = (next, depth);
        return sent;
    }

    /// <summary>One step of the area's wind carrying mist, in metres a second: each column sends its downwind neighbours a
    /// share by the wind's speed, none into a wall or across a step, and past the field's edge the area's floor blows in.</summary>
    private static bool Drift(GroundMistState s, float dt, Vector2 wind)
    {
        if (wind == Vector2.Zero || VolumetricFog.Quality.V == VolumetricFog.Levels[0]) return false;
        float fx = MathF.Min(MathF.Abs(wind.X) * dt, MaxShare), fy = MathF.Min(MathF.Abs(wind.Y) * dt, MaxShare);
        int sx = wind.X < 0f ? -1 : 1, sy = wind.Y < 0f ? -1 : 1;
        float[] depth = s.Depth, next = s.Target;
        bool sent = false;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int c = y * Side + x;
                if (float.IsNaN(s.Floor[c])) { next[c] = 0f; continue; }
                float d = depth[c];
                float e = d - Blown(s, c, x + sx, y, fx * d) - Blown(s, c, x, y + sy, fy * d)
                            + BlownIn(s, c, x - sx, y, fx) + BlownIn(s, c, x, y - sy, fy);
                sent |= e != d;
                next[c] = e;
            }
        (s.Depth, s.Target) = (next, depth);
        return sent;
    }

    /// <summary>What column <paramref name="c"/> loses to its downwind neighbour at (<paramref name="x"/>, <paramref name="y"/>):
    /// <paramref name="share"/> past the field's edge or across a linked edge, none into a wall or over a step.</summary>
    private static float Blown(GroundMistState s, int c, int x, int y, float share)
        => (uint)x >= Side || (uint)y >= Side || Linked(s, c, y * Side + x) ? share : 0f;

    /// <summary>What column <paramref name="c"/> gains from its upwind neighbour at (<paramref name="x"/>, <paramref name="y"/>):
    /// <paramref name="share"/> of that neighbour's depth, or of the area's floor past the field's edge.</summary>
    private static float BlownIn(GroundMistState s, int c, int x, int y, float share)
    {
        if ((uint)x >= Side || (uint)y >= Side) return share * s.AreaLevel;
        int from = y * Side + x;
        return Linked(s, c, from) ? share * s.Depth[from] : 0f;
    }

    /// <summary>Whether mist crosses between columns <paramref name="a"/> and <paramref name="b"/>: both floored, a step of at most <see cref="MaxStep"/>.</summary>
    private static bool Linked(GroundMistState s, int a, int b) => MathF.Abs(s.Floor[a] - s.Floor[b]) <= MaxStep;

    /// <summary>The share column <paramref name="c"/> would send to its neighbour at (<paramref name="x"/>, <paramref name="y"/>):
    /// none past the field, to a column with no floor, across a step higher than <see cref="MaxStep"/>, or uphill.</summary>
    private static float Send(GroundMistState s, int c, int x, int y, float rate)
    {
        if ((uint)x >= Side || (uint)y >= Side) return 0f;
        int to = y * Side + x;
        if (!Linked(s, c, to)) return 0f;
        float drop = SlopeGain * (s.Floor[c] - s.Floor[to]) + s.Depth[c] - s.Depth[to];
        return drop > 0f ? MathF.Min(rate * s.Depth[c], MaxShare) * drop : 0f;
    }

    /// <summary>What column <paramref name="c"/> takes from its neighbour at (<paramref name="x"/>, <paramref name="y"/>): that
    /// neighbour's share toward it, scaled as the neighbour's own sending was.</summary>
    private static float Take(GroundMistState s, int c, int x, int y, float rate)
    {
        if ((uint)x >= Side || (uint)y >= Side) return 0f;
        int from = y * Side + x;
        return s.Depth[from] > 0f ? Send(s, from, c % Side, c / Side, rate) * s.Spare[from] : 0f;
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

    /// <summary>Does the fog volume draw mist this frame?</summary>
    internal static bool Shows() => VolumetricFog.Enabled.V && _s.Any;

    /// <summary>The area edge: the old area's mist and walkmesh go with it.</summary>
    internal static void AreaEdge() => _s = new GroundMistState();
}

/// <summary>One frame's mist emitter: where it stands and the radius of the disc it fills.</summary>
internal readonly record struct MistSource(Vector3 At, float Reach);

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

    /// <summary>This frame's emitters.</summary>
    public MistSource[] Source = new MistSource[GroundMist.MaxSources];
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
