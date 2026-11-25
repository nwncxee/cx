using System;
using System.Numerics;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Services.Sound;

namespace CX.Alloy;

/// <summary>The area's air over the ground mist grid: the layers its look hangs, the fronts a wind change sends and
/// what stirs it. Cold on area load, stepped once a frame on the game thread.</summary>
internal static class AirScene
{
    private static AirSceneState _s = new();

    /// <summary>The stage that lays the dust over the lit frame.</summary>
    internal const string ShdCs = "cx_airdust_cs";

    /// <summary>Dust a column holds at Dust 1, metres of full density.</summary>
    private const float BaseDust = 0.1f;

    /// <summary>How tall the dust stands at rest, metres: below a standing character's knee.</summary>
    private const float RestHeight = 0.4f;

    /// <summary>A change in the wind's speed smaller than this, metres a second, sends no front.</summary>
    private const float Calm = 0.05f;

    /// <summary>How fast a front runs, metres a second, and how far either side of the player it runs.</summary>
    private const float FrontSpeed = 10f, FrontReach = 10f;

    /// <summary>How strong a drop in the wind's speed sends its front, against a rise of the same size.</summary>
    private const float DropShare = 0.5f;

    /// <summary>The share of a column's dust a front tears per metre a second of change.</summary>
    private const float TearPerSpeed = 0.25f;

    /// <summary>How unevenly a front tears across its line, 0 to 1: what lays the dust in streaks.</summary>
    private const float Streak = 0.8f;

    /// <summary>How high a front throws the dust it tears, metres per share torn, and how fast that falls, seconds.</summary>
    private const float LiftPerTear = 1f, LiftTau = 3f;

    /// <summary>How fast dust settles back toward what the floor holds at rest, per second.</summary>
    private const float Refill = 0.05f;

    /// <summary>How fast a steady wind scours dust, per second per metre a second of wind.</summary>
    private const float Scour = 0.029f;

    /// <summary>The share of the wind's speed the air drifts at over the floor.</summary>
    private const float DriftShare = 0.5f;

    /// <summary>How much more dust a column by a wall holds at rest, against one in the open.</summary>
    private const float WallGather = 1f;

    /// <summary>How fast that falls off away from the wall, metres, and the furthest a wall is looked for, whole columns.</summary>
    private const float GatherFall = 1f;
    private const int GatherReach = 3;

    /// <summary>Per unit of the area's fog amount: how much more dust lies at rest, how much less a gust or a steady
    /// wind tears, and how much faster it settles back.</summary>
    private const float AmountGain = 0.1f;

    /// <summary>How far the slow stir of still air thickens and thins the dust, a share of what lies.</summary>
    private const float StirShare = 0.05f;

    /// <summary>The share of a column's dust a passer clears at its feet, how far its clearing reaches, metres, and how
    /// fast the dust closes back over it, seconds.</summary>
    private const float Parted = 0.7f, PartRadius = 0.6f, PartTau = 3f;

    /// <summary>A step longer than this in one frame is a teleport, metres, and parts nothing.</summary>
    private const float MaxPartStep = 3f;

    /// <summary>Take <paramref name="look"/> for the area just laid, by night or by day; its dust lies whole, gathered
    /// along the walls and thicker as the area's fog amount rises, laid at Dust 1 and shown at the dial.</summary>
    internal static void Enter(AirLook look, bool night)
    {
        var s = new AirSceneState { Look = look, Fresh = true, Gen = _s.Gen + 1, Held = Air.Enabled.Enforced, HeldOn = Air.Enabled.V, Dial = Air.Dust.V };
        _s = s;
        if (!GroundMist.Corner(out s.Ox, out s.Oy, out s.Feet)) return;
        s.Laid = true;
        float amount = MathF.Max(GroundMist.FogAmount, 0f);
        s.Hold = 1f + AmountGain * amount;
        bool dust = (look.Layers(night) & AirLayer.Dust) != 0;
        s.Any = dust;
        float lay = BaseDust * s.Hold;
        for (int y = 0; y < GroundMist.Side; y++)
            for (int x = 0; x < GroundMist.Side; x++)
                s.Floor[y * GroundMist.Side + x] = GroundMist.FloorAt(s.Ox + x + 0.5f, s.Oy + y + 0.5f);
        for (int y = 0; y < GroundMist.Side; y++)
            for (int x = 0; x < GroundMist.Side; x++)
            {
                int c = y * GroundMist.Side + x;
                bool floor = dust && !float.IsNaN(s.Floor[c]);
                s.Rest[c] = s.Dust[c] = floor ? lay * (1f + WallGather * MathF.Exp(-ToWall(s, x, y) / GatherFall)) : 0f;
            }
    }

    /// <summary>How far column (<paramref name="x"/>, <paramref name="y"/>) stands from the nearest column with no floor,
    /// metres from its edge; infinity past <see cref="GatherReach"/>.</summary>
    private static float ToWall(AirSceneState s, int x, int y)
    {
        float best = float.PositiveInfinity;
        for (int dy = -GatherReach; dy <= GatherReach; dy++)
            for (int dx = -GatherReach; dx <= GatherReach; dx++)
            {
                int nx = x + dx, ny = y + dy;
                bool open = (uint)nx < GroundMist.Side && (uint)ny < GroundMist.Side && !float.IsNaN(s.Floor[ny * GroundMist.Side + nx]);
                if (open) continue;
                best = MathF.Min(best, MathF.Sqrt(dx * dx + dy * dy) - 0.5f);
            }
        return best;
    }

    /// <summary>An area's corners and edges, none: before any area load is seen the look reads its place from the frame's
    /// interior flag alone.</summary>
    private static readonly List<string[]> NoTiles = [];

    /// <summary>The features of the area just loaded, or, when no load was seen, an area with only <paramref name="interior"/>.</summary>
    private static AreaFeatures Loaded(bool interior)
    {
        AreaFeatures f = AirArea.Features;
        return f.Tileset.Length > 0 || f.Corners.Count > 0 ? f : AreaFeatures.Of(string.Empty, interior ? SoundScene.FlagInterior : 0u, NoTiles, NoTiles);
    }

    /// <summary>Frame <paramref name="frame"/> of <paramref name="scene"/>: a new area drops the last one's dust and takes
    /// its own look once its floor is laid, then the air steps. Once a frame; allocates only on a new area.</summary>
    internal static void Frame(DbId scene, int frame, bool interior, bool night, float dt, Vector2 wind)
    {
        AirSceneState s = _s;
        bool fresh = s.Scene != scene.Value;
        if (!fresh && s.Seq == frame) return;
        if (fresh && s.Laid) { Reset(); s = _s; }
        if (!s.Laid && GroundMist.Corner(out _, out _, out _))
        {
            Enter(AirPlaces.Resolve(Loaded(interior)), night);
            s = _s;
        }
        s.Scene = scene.Value;
        s.Seq = frame;
        Step(dt, wind);
    }

    /// <summary>Day turns to night or back.</summary>
    internal static void Night(bool night) { }

    /// <summary>A tree trunk standing at <paramref name="at"/>.</summary>
    internal static void Trunk(Vector2 at) { }

    /// <summary>A lamp at <paramref name="at"/> reaching <paramref name="reach"/> metres.</summary>
    internal static void Lamp(Vector3 at, float reach) { }

    /// <summary>A blast at <paramref name="at"/> of <paramref name="radius"/> metres.</summary>
    internal static void Blast(Vector3 at, float radius) { }

    /// <summary>Advance the air <paramref name="dt"/> seconds under the area's <paramref name="wind"/>; at Low no front starts, with the air off nothing moves.</summary>
    internal static void Step(float dt, Vector2 wind)
    {
        AirSceneState s = _s;
        if (!s.Laid) return;
        Dial(s);
        if (!On(s))
        {
            s.Stepped = s.Fresh = false;
            s.Front = default;
            return;
        }
        float speed = wind.Length();
        bool still = Air.Quality.V == Air.Levels[0];
        if (!s.Stepped && !s.Fresh) Settle(s, speed);
        else if (s.Stepped && !still && MathF.Abs(speed - s.Speed) > Calm) Launch(s, speed - s.Speed, speed > 0f ? wind / speed : s.Dir);
        s.Stepped = true;
        s.Fresh = false;
        s.Speed = speed;
        if (speed > 0f) s.Dir = wind / speed;
        s.Drift = wind * DriftShare;
        if (still) return;
        s.Gen++;
        s.Clock += dt;
        Sweep(s, dt);
        float fall = MathF.Exp(-dt / LiftTau), back = Refill * s.Hold * dt, off = Scour * speed * dt / s.Hold, close = MathF.Exp(-dt / PartTau);
        for (int c = 0; c < s.Lift.Length; c++)
        {
            s.Lift[c] *= fall;
            s.Gap[c] *= close;
            s.Dust[c] += back * (s.Rest[c] - s.Dust[c]) - off * s.Dust[c];
        }
    }

    /// <summary>Take the Dust dial as it stands, a hold's value while one is in force; a change shows at the next upload.</summary>
    private static void Dial(AirSceneState s)
    {
        float dial = Air.Dust.V;
        if (dial == s.Dial) return;
        s.Dial = dial;
        s.Gen++;
    }

    /// <summary>Lay each column's dust as a steady <paramref name="speed"/> leaves it, where scour and refill balance.</summary>
    private static void Settle(AirSceneState s, float speed)
    {
        float keep = Refill * s.Hold / (Refill * s.Hold + Scour * speed / s.Hold);
        for (int c = 0; c < s.Dust.Length; c++) s.Dust[c] = s.Rest[c] * keep;
    }

    /// <summary>A creature that stepped from <paramref name="was"/> to <paramref name="now"/> clears the dust at its feet,
    /// which closes back over its path. Hot: allocates nothing; at Low and with the air off it parts nothing.</summary>
    internal static void Part(Vector3 was, Vector3 now)
    {
        AirSceneState s = _s;
        if (!s.Laid || !On(s) || Air.Quality.V == Air.Levels[0]) return;
        if (Vector2.Distance(new Vector2(was.X, was.Y), new Vector2(now.X, now.Y)) > MaxPartStep) return;
        int x0 = (int)MathF.Floor(now.X - PartRadius) - s.Ox, x1 = (int)MathF.Floor(now.X + PartRadius) - s.Ox;
        int y0 = (int)MathF.Floor(now.Y - PartRadius) - s.Oy, y1 = (int)MathF.Floor(now.Y + PartRadius) - s.Oy;
        for (int y = Math.Max(y0, 0); y <= Math.Min(y1, GroundMist.Side - 1); y++)
            for (int x = Math.Max(x0, 0); x <= Math.Min(x1, GroundMist.Side - 1); x++)
            {
                float nx = Math.Clamp(now.X, s.Ox + x, s.Ox + x + 1f) - now.X, ny = Math.Clamp(now.Y, s.Oy + y, s.Oy + y + 1f) - now.Y;
                float reach = 1f - MathF.Sqrt(nx * nx + ny * ny) / PartRadius;
                int c = y * GroundMist.Side + x;
                if (reach > 0f) s.Gap[c] = MathF.Max(s.Gap[c], Parted * reach);
            }
        s.Gen++;
    }

    /// <summary>Send a front along <paramref name="dir"/> for a change of <paramref name="change"/> metres a second.</summary>
    private static void Launch(AirSceneState s, float change, Vector2 dir)
    {
        s.Front = new AirFront(true, change > 0f ? change : -change * DropShare, dir);
        s.Line = -FrontReach;
    }

    /// <summary>Run the live front on by <paramref name="dt"/>, tearing and lifting the dust of each column it crosses.</summary>
    private static void Sweep(AirSceneState s, float dt)
    {
        AirFront f = s.Front;
        if (!f.Live) return;
        float from = s.Line, to = from + FrontSpeed * dt;
        s.Line = to;
        if (to > FrontReach) s.Front = f with { Live = false };
        float tear = TearPerSpeed * f.Strength;
        tear /= s.Hold;
        Vector2 across = new(-f.Dir.Y, f.Dir.X);
        for (int y = 0; y < GroundMist.Side; y++)
            for (int x = 0; x < GroundMist.Side; x++)
            {
                int c = y * GroundMist.Side + x;
                if (s.Dust[c] <= 0f) continue;
                Vector2 at = new Vector2(s.Ox + x + 0.5f, s.Oy + y + 0.5f) - s.Feet;
                float along = Vector2.Dot(at, f.Dir);
                if (along <= from || along > to) continue;
                float torn = MathF.Min(tear * (1f + Streak * Uneven(Vector2.Dot(at, across))), 1f);
                s.Dust[c] *= 1f - torn;
                s.Lift[c] += LiftPerTear * torn;
            }
    }

    /// <summary>How unevenly a front tears at <paramref name="across"/> metres along its line, from -1 to 1.</summary>
    private static float Uneven(float across)
        => (MathF.Sin(2.1f * across + 0.7f) + 0.7f * MathF.Sin(3.7f * across + 2.3f) + 0.5f * MathF.Sin(1.3f * across + 4.1f)) / 2.2f;

    /// <summary>How much of <paramref name="layer"/> hangs at <paramref name="at"/>: the dust thickest on the floor and
    /// thinning to nothing at its top, stirred slowly where it lies. None with the air off.</summary>
    internal static float Density(AirLayer layer, Vector3 at)
    {
        AirSceneState s = _s;
        int c = Column(s, at.X, at.Y);
        if (c < 0 || layer != AirLayer.Dust || !On(s)) return 0f;
        float h = RestHeight + s.Lift[c], z = at.Z - s.Floor[c];
        if (z < 0f || z >= h) return 0f;
        return 2f * s.Dial * s.Dust[c] * (1f - s.Gap[c]) * (1f + StirShare * Stir(at.X, at.Y, s.Clock)) * (1f - z / h) / h;
    }

    /// <summary>Does the area's dust lie, to be drawn? False with the air off or in a place with none.</summary>
    internal static bool Lies => _s.Laid && _s.Any && On(_s);

    /// <summary>Is the air on in this area? A hold reads as it stood when the area was entered, so a later change waits
    /// for the next entry; the player's own switch reads live.</summary>
    internal static bool On() => On(_s);

    private static bool On(AirSceneState s) => s.Held ? s.HeldOn : Air.Enabled.Own;

    /// <summary>Runs on each time the dust's columns change, across areas, so an upload knows to send.</summary>
    internal static int Gen => _s.Gen;

    /// <summary>The dust shown on the column holding world (<paramref name="x"/>, <paramref name="y"/>), metres of full
    /// density, and the height it reaches over its floor; both 0 off the air's floor.</summary>
    internal static (float Depth, float Top) Shown(float x, float y)
    {
        AirSceneState s = _s;
        int c = Column(s, x, y);
        if (c < 0) return (0f, 0f);
        float cx = MathF.Floor(x) + 0.5f, cy = MathF.Floor(y) + 0.5f;
        return (s.Dial * s.Dust[c] * (1f - s.Gap[c]) * (1f + StirShare * Stir(cx, cy, s.Clock)), RestHeight + s.Lift[c]);
    }

    /// <summary>The still air's slow stir at world (<paramref name="x"/>, <paramref name="y"/>) after <paramref name="t"/>
    /// seconds, from -1 to 1: two crossing waves, fixed in the world, that creep across it.</summary>
    private static float Stir(float x, float y, float t)
        => 0.5f * (MathF.Sin(1.1f * x + 0.5f * y - 0.31f * t) + MathF.Sin(-0.4f * x + 1.2f * y - 0.23f * t + 1.3f));

    /// <summary>The air's column for world (<paramref name="x"/>, <paramref name="y"/>), or -1 outside it or off the floor.</summary>
    private static int Column(AirSceneState s, float x, float y)
    {
        if (!s.Laid) return -1;
        int cx = (int)MathF.Floor(x) - s.Ox, cy = (int)MathF.Floor(y) - s.Oy;
        int c = (uint)cx < GroundMist.Side && (uint)cy < GroundMist.Side ? cy * GroundMist.Side + cx : -1;
        return c < 0 || float.IsNaN(s.Floor[c]) ? -1 : c;
    }

    /// <summary>The particles of <paramref name="layer"/>.</summary>
    internal static ReadOnlySpan<AirParticle> Particles(AirLayer layer) => ReadOnlySpan<AirParticle>.Empty;

    /// <summary>The gust front crossing the air now.</summary>
    internal static AirFront Front => _s.Front;

    /// <summary>The wisps' shape now.</summary>
    internal static AirWisps Wisps => default;

    /// <summary>The air's own motion at a ground point, in metres a second.</summary>
    internal static Vector2 Motion(float x, float y) => _s.Drift;

    /// <summary>Drop the area's air.</summary>
    internal static void Reset() => _s = new AirSceneState { Gen = _s.Gen + 1 };
}

/// <summary>Everything the air holds for one area: its look, its columns over the mist grid and the front crossing it.</summary>
internal sealed class AirSceneState
{
    /// <summary>The look the area took.</summary>
    public AirLook Look;

    /// <summary>The grid's corner in whole metres and the player's feet across the ground, and whether a floor is laid.</summary>
    public int Ox, Oy;
    public Vector2 Feet;
    public bool Laid;

    /// <summary>Per column, row-major from the corner: its floor (NaN for none), the dust it holds at rest and its dust
    /// now, both at Dust 1, and how high a front threw it.</summary>
    public readonly float[] Floor = new float[GroundMist.Side * GroundMist.Side];
    public readonly float[] Rest = new float[GroundMist.Side * GroundMist.Side];
    public readonly float[] Dust = new float[GroundMist.Side * GroundMist.Side];
    public readonly float[] Lift = new float[GroundMist.Side * GroundMist.Side];

    /// <summary>Per column, how much of its dust a passer has cleared, 0 to 1.</summary>
    public readonly float[] Gap = new float[GroundMist.Side * GroundMist.Side];

    /// <summary>The Dust dial the columns show at, as the last step read it.</summary>
    public float Dial = 1f;

    /// <summary>How strongly the area's fog amount holds its dust: more at rest, torn less, back sooner. 1 at amount 0.</summary>
    public float Hold = 1f;

    /// <summary>The wind's speed and heading the last step, and whether there was one; and whether the area is just
    /// entered, its dust lying whole until it steps.</summary>
    public float Speed;
    public Vector2 Dir = Vector2.UnitX;
    public bool Stepped, Fresh;

    /// <summary>How long the air has stirred, seconds.</summary>
    public float Clock;

    /// <summary>Whether a hold set the Air switch when the area was entered, and what it held it at.</summary>
    public bool Held, HeldOn;

    /// <summary>Whether the area's look lays dust, and the upload generation its columns are at.</summary>
    public bool Any;
    public int Gen;

    /// <summary>How the air drifts over the floor, metres a second.</summary>
    public Vector2 Drift;

    /// <summary>The front crossing the air, and where its line stands along it from the player's feet, metres.</summary>
    public AirFront Front;
    public float Line;

    /// <summary>The scene the live air was framed in and the frame it last stepped.</summary>
    public uint Scene;
    public int Seq = int.MinValue;
}
