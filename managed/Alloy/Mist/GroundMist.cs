using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;

namespace CX.Alloy;

/// <summary>Ground mist: a depth of mist per metre column over the walkmesh around the player, and the floor heights
/// the air's dust lies on; <see cref="Pack"/> hands both to the dust stage. Area fog lays an even floor of mist and each
/// mist emitter a disc.</summary>
internal static partial class GroundMist
{
    /// <summary>The field's lifetime owner: replaced whole at an area edge.</summary>
    private static GroundMistState _s = new();

    /// <summary>The columns along each side of the field, one metre each.</summary>
    internal const int Side = 256;

    /// <summary>The half-metre cells along each side of the field, whose floors the air lies over.</summary>
    internal const int QuarterSide = Side * 2;

    /// <summary>The field recentres on the feet in steps of this many metres, so the floors are laid again rarely.</summary>
    private const int Snap = 16;

    /// <summary>A column's floor is its highest face at or below the feet plus this many metres.</summary>
    private const float Headroom = 2f;

    /// <summary>The feet climb or drop this far before the floors are laid again, for a deck over a road.</summary>
    private const float Relay = 1f;

    /// <summary>Metres of mist one metre of the area's fog amount lays, and the deepest floor area fog lays.</summary>
    private const float DepthPerAmount = 1f / 3f, MaxArea = 2f;

    /// <summary>The depth a mist emitter's disc fills to.</summary>
    internal const float SourceDepth = 1f;

    /// <summary>The limits on an emitter disc's radius.</summary>
    private const float MinReach = 2f, MaxReach = 10f;

    /// <summary>Seconds a column takes to rise toward what lays it, and to settle once nothing does.</summary>
    private const float RiseTau = 4f, SettleTau = 20f;

    /// <summary>Below this depth a column holds no mist.</summary>
    private const float Trace = 1e-4f;

    /// <summary>How fast mist evens out over flat ground: the share of a depth difference one metre of depth sends a second.</summary>
    private const float FlowRate = 0.5f;

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
            s.Tri.Add(new MistFace(Vector3.Transform(walk.Positions[f.V0], world), Vector3.Transform(walk.Positions[f.V1], world),
                                    Vector3.Transform(walk.Positions[f.V2], world), TileBuild.Water(f.Surface)));
        }
        s.Stale = true;
    }

    /// <summary>Centre the field on the player's feet, laying the floor each column takes from the walkmesh.</summary>
    internal static void Follow(Vector3 feet)
    {
        GroundMistState s = _s;
        s.Player = new Vector2(feet.X, feet.Y);
        int ox = CornerFor(feet.X), oy = CornerFor(feet.Y);
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
        s.Lays++;
    }

    /// <summary>Each column's floor: the highest face over its centre at or below the feet plus the headroom, and
    /// whether that face is water; and the same floor over each quarter of it.</summary>
    private static void LayFloors(GroundMistState s)
    {
        Array.Fill(s.Floor, float.NaN);
        Array.Clear(s.Wet);
        for (int y = 0; y < QuarterSide; y++)
            for (int x = 0; x < QuarterSide; x++) s.Quarter[y, x] = float.NaN;
        Lay(s, 1);
        Lay(s, 2);
        LayInland(s);
    }

    /// <summary>The metres the dust thins over to nothing at the walkable floor's edge, the distance from a floorless
    /// column's centre at which it starts to show, and the columns round each the nearest such centre is looked for in.</summary>
    private const float Thin = 2f, Rim = 0.5f;
    private const int Reach = 2;

    /// <summary>Each column's share of its dust: 1 inland, thinning to nothing within <see cref="Thin"/> of the nearest
    /// floorless column, off the field counted as one, so no line shows where the walkmesh ends.</summary>
    private static void LayInland(GroundMistState s)
    {
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side; x++)
            {
                int c = y * Side + x;
                float near = Reach + Thin;
                for (int j = -Reach; j <= Reach && !float.IsNaN(s.Floor[c]); j++)
                    for (int i = -Reach; i <= Reach; i++)
                    {
                        int nx = x + i, ny = y + j, n = ny * Side + nx;
                        if ((uint)nx < Side && (uint)ny < Side && !float.IsNaN(s.Floor[n])) continue;
                        near = MathF.Min(near, MathF.Sqrt(i * i + j * j));
                    }
                s.Inland[c] = float.IsNaN(s.Floor[c]) ? 0f : Math.Clamp((near - Rim) / Thin, 0f, 1f);
            }
    }

    /// <summary>Lay the floors <paramref name="per"/> cells to the metre (1: the columns and their water, 2: the
    /// quarters), each the highest face over its centre at or below the feet plus the headroom.</summary>
    private static void Lay(GroundMistState s, int per)
    {
        float top = s.FeetZ + Headroom, cell = 1f / per;
        int side = Side * per;
        foreach ((Vector3 a, Vector3 b, Vector3 c, bool water) in s.Tri)
        {
            int x0 = Math.Max((int)MathF.Floor((MathF.Min(a.X, MathF.Min(b.X, c.X)) - s.Ox) * per), 0);
            int x1 = Math.Min((int)MathF.Ceiling((MathF.Max(a.X, MathF.Max(b.X, c.X)) - s.Ox) * per), side - 1);
            int y0 = Math.Max((int)MathF.Floor((MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - s.Oy) * per), 0);
            int y1 = Math.Min((int)MathF.Ceiling((MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - s.Oy) * per), side - 1);
            float det = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
            if (MathF.Abs(det) < 1e-8f) continue;
            float inv = 1f / det;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = s.Ox + (x + 0.5f) * cell - a.X, py = s.Oy + (y + 0.5f) * cell - a.Y;
                    float u = (px * (c.Y - a.Y) - (c.X - a.X) * py) * inv;
                    float v = ((b.X - a.X) * py - px * (b.Y - a.Y)) * inv;
                    const float Edge = -1e-4f;
                    if (u < Edge || v < Edge || u + v > 1f - Edge) continue;
                    float z = a.Z + u * (b.Z - a.Z) + v * (c.Z - a.Z);
                    if (per > 1) { Raise(ref s.Quarter[y, x], z, top); continue; }
                    int at = y * Side + x;
                    if (Raise(ref s.Floor[at], z, top)) s.Wet[at] = water;
                }
        }
    }

    /// <summary>Take <paramref name="z"/> as <paramref name="floor"/> when it is no higher than <paramref name="top"/> and
    /// above the floor so far (NaN: none); whether it did.</summary>
    private static bool Raise(ref float floor, float z, float top)
    {
        if (z > top || !float.IsNaN(floor) && z <= floor) return false;
        floor = z;
        return true;
    }

    /// <summary>The area's fog, as its current palette sets it: the even floor of mist it lays, filled whole until the
    /// field steps. With the mist off it is only remembered, so the off state does no work.</summary>
    internal static void AreaFog(bool on, float amount)
    {
        GroundMistState s = _s;
        s.AreaLevel = on ? Math.Clamp(amount * DepthPerAmount, 0f, MaxArea) : 0f;
        s.Amount = on ? amount : 0f;
        if (s.Stepped || !AirScene.On()) return;
        for (int c = 0; c < s.Depth.Length; c++) s.Depth[c] = float.IsNaN(s.Floor[c]) ? 0f : s.AreaLevel;
        s.Any = s.AreaLevel > Trace;
        s.Over = false;
        s.Gen++;
    }

    /// <summary>Lay mist once: <paramref name="depth"/> metres over a disc of <paramref name="radius"/> at the
    /// floor below <paramref name="at"/>.</summary>
    internal static void Place(Vector3 at, float radius, float depth)
    {
        GroundMistState s = _s;
        if (!s.Laid || depth <= 0f) return;
        Disc(s, at, radius, s.Depth, depth, true);
        s.Any = s.Over = true;
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

    /// <summary>Advance the field by <paramref name="dt"/> seconds under the area's <paramref name="wind"/>. With the mist off
    /// it holds no step, so the area's fog lays the field afresh once the mist comes back on.</summary>
    internal static void Step(float dt, Vector2 wind)
    {
        GroundMistState s = _s;
        int sources = s.Sources;
        s.Sources = 0;
        if (!AirScene.On())
        {
            s.Stepped = false;
            return;
        }
        if (!s.Laid || dt <= 0f) return;
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
        moved |= Air(s, dt);
        bool any = false, over = false;
        for (int c = 0; c < s.Depth.Length; c++)
        {
            if (s.Depth[c] < Trace) s.Depth[c] = 0f;
            any |= s.Depth[c] > 0f;
            over |= s.Depth[c] > s.AreaLevel + Trace;
        }
        (s.Any, s.Over) = (any, over);
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
        if (wind == Vector2.Zero || LiesStill()) return false;
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

    /// <summary>The radius a walker clears at its feet, and the ring out to which the mist it pushes lands.</summary>
    private const float ClearRadius = 1f, WakeRadius = 2.5f;

    /// <summary>A walker clears its disc fully over this much of its own travel.</summary>
    private const float PartStride = 0.1f;

    /// <summary>A step longer than this in one frame is a teleport, not a walk, and parts nothing.</summary>
    internal const float MaxPartStep = 3f;

    /// <summary>Walkers part the mist only this far from the player along either axis: the air box.</summary>
    private const float PartBox = 32f;

    /// <summary>A walker that stepped from <paramref name="was"/> to <paramref name="now"/> pushes the mist at its feet
    /// out to its sides and ahead, never back onto its path, none onto a wall or across a step. Mass is kept; hot, allocates nothing.</summary>
    internal static void Part(Vector3 was, Vector3 now)
    {
        GroundMistState s = _s;
        float moved = Vector2.Distance(new Vector2(was.X, was.Y), new Vector2(now.X, now.Y));
        if (!AirScene.On() || LiesStill() || !s.Any || moved > MaxPartStep) return;
        if (MathF.Abs(now.X - s.Player.X) > PartBox || MathF.Abs(now.Y - s.Player.Y) > PartBox) return;
        float take = MathF.Min(moved / PartStride, 1f);
        var ahead = new Vector2(now.X - was.X, now.Y - was.Y);
        if (!Push(s, now, ClearRadius, WakeRadius, BowReach, take, ahead, default, 0f)) return;
        StirWalk(s, now, ahead, take);
    }

    /// <summary>A blast's mist lands on a ring this many metres wide outside its radius.</summary>
    private const float BlastRim = 2f;

    /// <summary>A detonation at <paramref name="at"/> blows all the mist inside <paramref name="radius"/> out onto a ragged
    /// ring just past it, thrown up tall, and gusts the air it reaches outward; none onto a wall or across a step.</summary>
    internal static void Blast(Vector3 at, float radius)
    {
        GroundMistState s = _s;
        if (!AirScene.On() || LiesStill() || !s.Any || !(radius > 0f)) return;
        radius = MathF.Min(radius, MaxBlast);
        var lobes = new Lobes(at);
        if (!Push(s, at, radius, radius + BlastRim, radius * BlastWind, 1f, Vector2.Zero, lobes, Ragged)) return;
        Gust(s, at, radius, lobes);
    }

    /// <summary>The widest blast the field throws, in metres: its gust reaches twice as far, well inside the field.</summary>
    private const float MaxBlast = 20f;

    /// <summary>Move <paramref name="take"/> of the mist within <paramref name="clear"/> of <paramref name="centre"/> onto the
    /// columns out to <paramref name="wake"/> not behind <paramref name="ahead"/>, weighted round by <paramref name="lobes"/>, over
    /// ground the air reaches from the centre without crossing a wall or a step. The reached columns, out to <paramref name="reach"/>,
    /// are left in the queue; false if none was. Mass is kept.</summary>
    private static bool Push(GroundMistState s, Vector3 centre, float clear, float wake, float reach, float take, Vector2 ahead, in Lobes lobes, float ragged)
    {
        s.Reached = 0;
        int at = Column(s, centre.X, centre.Y);
        if (at < 0 || float.IsNaN(s.Floor[at])) return false;
        Fill(s, at, centre, MathF.Max(reach, wake));
        float taken = 0f, weight = 0f;
        for (int i = 0; i < s.Reached; i++)
        {
            int c = s.Queue[i];
            float dx = s.Ox + c % Side + 0.5f - centre.X, dy = s.Oy + c / Side + 0.5f - centre.Y, r2 = dx * dx + dy * dy;
            if (r2 <= clear * clear) taken += s.Depth[c] * take;
            else if (r2 <= wake * wake && dx * ahead.X + dy * ahead.Y >= 0f) weight += 1f + ragged * lobes.Of(MathF.Atan2(dy, dx));
        }
        if (weight <= 0f) return true;
        float share = taken / weight;
        for (int i = 0; i < s.Reached; i++)
        {
            int c = s.Queue[i];
            float dx = s.Ox + c % Side + 0.5f - centre.X, dy = s.Oy + c / Side + 0.5f - centre.Y, r2 = dx * dx + dy * dy;
            if (r2 <= clear * clear) s.Depth[c] -= s.Depth[c] * take;
            else if (r2 <= wake * wake && dx * ahead.X + dy * ahead.Y >= 0f) s.Depth[c] += share * (1f + ragged * lobes.Of(MathF.Atan2(dy, dx)));
        }
        return true;
    }

    /// <summary>Queue every column within <paramref name="radius"/> of <paramref name="centre"/> that the ground joins to
    /// <paramref name="from"/> edge by edge, with no wall or step between, nearest first.</summary>
    private static void Fill(GroundMistState s, int from, Vector3 centre, float radius)
    {
        if (++s.Stamp == int.MaxValue) { Array.Clear(s.Mark); s.Stamp = 1; }
        int stamp = s.Stamp, n = 0, head = 0;
        float r2 = radius * radius;
        s.Mark[from] = stamp;
        s.Queue[n++] = from;
        while (head < n)
        {
            int c = s.Queue[head++], x = c % Side, y = c / Side;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), ny = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                if ((uint)nx >= Side || (uint)ny >= Side) continue;
                int to = ny * Side + nx;
                if (s.Mark[to] == stamp) continue;
                float dx = s.Ox + nx + 0.5f - centre.X, dy = s.Oy + ny + 0.5f - centre.Y;
                if (dx * dx + dy * dy > r2 || !Linked(s, c, to)) continue;
                s.Mark[to] = stamp;
                s.Queue[n++] = to;
            }
        }
        s.Reached = n;
    }

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

    /// <summary>How tall the mist stands, in metres, over the world column at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal static float Height(float x, float y)
    {
        GroundMistState s = _s;
        int c = Column(s, x, y);
        return c < 0 ? s.AreaLevel : s.Depth[c] + s.Lift[c];
    }

    /// <summary>How fast the mist over the world column at (<paramref name="x"/>, <paramref name="y"/>) moves across the ground, in metres a second.</summary>
    internal static Vector2 Motion(float x, float y)
    {
        GroundMistState s = _s;
        int c = Column(s, x, y);
        return c < 0 ? Vector2.Zero : new Vector2(s.Vx[c], s.Vy[c]);
    }

    /// <summary>The mist an emitter or a placement laid over the world column at (<paramref name="x"/>, <paramref name="y"/>),
    /// metres over the area's own floor of it: the area's fog lays none the air shows.</summary>
    internal static float Laid(float x, float y)
    {
        GroundMistState s = _s;
        int c = Column(s, x, y);
        float over = c < 0 || !s.Over ? 0f : s.Depth[c] - s.AreaLevel;
        return over > Trace ? over : 0f;
    }

    /// <summary>Does any column hold mist an emitter or a placement laid?</summary>
    internal static bool Placed => _s.Over;

    /// <summary>Is the floor of the column at (<paramref name="x"/>, <paramref name="y"/>) open water?</summary>
    internal static bool WetAt(float x, float y)
    {
        GroundMistState s = _s;
        int c = Column(s, x, y);
        return c >= 0 && s.Wet[c];
    }

    /// <summary>The floor height the column at (<paramref name="x"/>, <paramref name="y"/>) lays its mist on; NaN for none.</summary>
    internal static float FloorAt(float x, float y)
    {
        GroundMistState s = _s;
        int c = Column(s, x, y);
        return c < 0 ? float.NaN : s.Floor[c];
    }

    /// <summary>The area's fog amount, 0 with its fog off.</summary>
    internal static float FogAmount => _s.Amount;

    /// <summary>The field's corner in whole metres and the player's feet across the ground; false while no floor is laid.</summary>
    internal static bool Corner(out int ox, out int oy, out Vector2 feet)
    {
        GroundMistState s = _s;
        (ox, oy, feet) = (s.Ox, s.Oy, s.Player);
        return s.Laid;
    }

    /// <summary>Does the field hold mist this frame: the air on and an area that lays some? It marks the packed field as
    /// showing and fills each column's mist depth there.</summary>
    internal static bool Shows() => AirScene.On() && _s.Any;

    /// <summary>A fresh field: the old area's mist and walkmesh go, and its generation runs on, so the next upload always sends.</summary>
    internal static void Reset() => _s = new GroundMistState { Gen = _s.Gen + 1, Lays = _s.Lays + 1 };

    /// <summary>Whether the mist lies still: at the lowest fog detail, or at Low air detail, no wind, walker or blast moves it.</summary>
    private static bool LiesStill() => CX.Alloy.Air.Still || VolumetricFog.Quality.V == VolumetricFog.Levels[0];

    /// <summary>Runs on each time the field's floors are laid afresh: a re-centre, a climb or a new area's walk.</summary>
    internal static int Lays => _s.Lays;



    /// <summary>The field's corner along one axis for feet at <paramref name="feet"/>: the snap they stand in, less half the side.</summary>
    internal static int CornerFor(float feet) => (int)MathF.Floor(feet / Snap) * Snap - Side / 2;
}

/// <summary>One frame's mist emitter: where it stands and the radius of the disc it fills.</summary>
internal readonly record struct MistSource(Vector3 At, float Reach);

/// <summary>Everything the ground mist field holds for one area: its floor faces, the columns around the player and
/// this frame's emitters.</summary>
internal sealed class GroundMistState
{
    /// <summary>The area's floor faces, in world space.</summary>
    public readonly List<MistFace> Tri = new(1024);

    /// <summary>The field's corner in whole metres, the feet height its floors were laid for, whether they are, and
    /// whether faces came in since.</summary>
    public int Ox, Oy;
    public float FeetZ;

    /// <summary>The player's feet across the ground, the centre of the box walkers part the mist in.</summary>
    public Vector2 Player;
    public bool Laid, Stale;

    /// <summary>Bumped whenever a floor or a depth changes, so the upload sends the field only when it moved; and each
    /// time the floors are laid afresh, so the air knows to lay its own over them.</summary>
    public int Gen, Lays;

    /// <summary>The scene the live walk reads, how far into its parts table it has looked, and the frame it last stepped.</summary>
    public uint Scene;
    public int Walked, Seq = -1;

    /// <summary>Per column, row-major from the corner: its floor (NaN for none), its depth of mist and this step's target.</summary>
    public float[] Floor = new float[GroundMist.Side * GroundMist.Side];
    public float[] Depth = new float[GroundMist.Side * GroundMist.Side];
    public float[] Spare = new float[GroundMist.Side * GroundMist.Side];
    public float[] Target = new float[GroundMist.Side * GroundMist.Side];

    /// <summary>Per half-metre cell, by row then column from the corner: the floor over its centre (NaN for none).</summary>
    public readonly float[,] Quarter = new float[GroundMist.QuarterSide, GroundMist.QuarterSide];

    /// <summary>Per column, the share of its dust it shows, thinning toward the walkable floor's edge.</summary>
    public readonly float[] Inland = new float[GroundMist.Side * GroundMist.Side];

    /// <summary>Per column, whether its floor is open water.</summary>
    public readonly bool[] Wet = new bool[GroundMist.Side * GroundMist.Side];

    /// <summary>The depth the area's fog lays, whether the field has stepped since the area began or Volumetric fog came
    /// back on, and whether any column holds mist.</summary>
    public float AreaLevel;

    /// <summary>The area's fog amount as its palette sets it, 0 with the area's fog off.</summary>
    public float Amount;

    /// <summary>The air's upload generation the field last folded into its own.</summary>
    public int AirGen = int.MinValue;
    public bool Stepped, Any;

    /// <summary>Whether any column holds mist over the area's floor of it.</summary>
    public bool Over;

    /// <summary>This frame's emitters.</summary>
    public MistSource[] Source = new MistSource[GroundMist.MaxSources];
    public int Sources;

    /// <summary>Per column: the air's velocity in metres a second, the next step's, and the height the mist was thrown up over its depth.</summary>
    public float[] Vx = new float[GroundMist.Side * GroundMist.Side];
    public float[] Vy = new float[GroundMist.Side * GroundMist.Side];
    public float[] Nx = new float[GroundMist.Side * GroundMist.Side];
    public float[] Ny = new float[GroundMist.Side * GroundMist.Side];
    public float[] Lift = new float[GroundMist.Side * GroundMist.Side];

    /// <summary>Whether any air moves, the columns it moves over, and its fastest speed; whether any mist stands thrown up.</summary>
    public bool Air, Lifted;
    public int Ax0, Ax1, Ay0, Ay1;
    public float AirTop;

    /// <summary>The columns a push reached, nearest first, how many, and the stamp marking them reached.</summary>
    public int[] Queue = new int[GroundMist.Side * GroundMist.Side];
    public int[] Mark = new int[GroundMist.Side * GroundMist.Side];
    public int Reached, Stamp;
}

/// <summary>One floor face of the walkmesh, its three corners in world space, and whether it is open water.</summary>
internal readonly record struct MistFace(Vector3 A, Vector3 B, Vector3 C, bool Wet);
