using System;
using System.Numerics;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Services.Sound;

namespace CX.Alloy;

/// <summary>The area's air over the ground mist grid: the layers its look hangs, the fronts a wind change sends and
/// what stirs it. Cold on area load, stepped once a frame on the game thread.</summary>
internal static partial class AirScene
{
    private static AirSceneState _s = new();

    /// <summary>The stage that marches the dust and the mist, one ray a block of the main view.</summary>
    internal const string ShdCs = "cx_airdust_cs";

    /// <summary>The stage that lays the marched air over the lit frame at full resolution.</summary>
    internal const string DustUpCs = "cx_airdustup";

    /// <summary>The side of the block of the main view one ray of the dust stage stands for, pixels (both stages' <c>CX_AIR_DIV</c>).</summary>
    internal const int DustDiv = 4;

    /// <summary>Dust a column holds at Dust 1, metres of full density.</summary>
    private const float BaseDust = 0.1f;

    /// <summary>How tall the dust stands at rest, metres: below a standing character's knee.</summary>
    private const float RestHeight = 0.4f;

    /// <summary>Haze a column among the trunks holds, metres of full density, and how tall it stands, metres: over the crowns' undersides.</summary>
    private const float BaseHaze = 0.03f, HazeHeight = 6f;

    /// <summary>How far a trunk's haze reaches, metres, and the span at its edge it thins out over.</summary>
    private const float HazeReach = 4f, HazeFeather = 2f;

    /// <summary>The share of a column's haze a blast clears at its heart, falling to nothing at its edge.</summary>
    private const float BlastClears = 0.9f;

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

    /// <summary>Take <paramref name="look"/> for the area just laid, by night or by day, <paramref name="indoors"/> or out;
    /// its dust lies whole, gathered along the walls and thicker as the fog amount rises, shown at the dial. A forest's
    /// columns hold haze, which each <see cref="Trunk"/> lays; water outdoors holds night mist, whole by night.</summary>
    internal static void Enter(AirLook look, bool night, bool indoors = false)
    {
        var s = new AirSceneState { Look = look, Night = night, Indoors = indoors, Fresh = true, Gen = _s.Gen + 1, Held = Air.Enabled.Enforced, HeldOn = Air.Enabled.V, Dial = Air.Dust.V };
        _s = s;
        if (!GroundMist.Corner(out s.Ox, out s.Oy, out s.Feet)) return;
        s.Laid = true;
        s.Lays = GroundMist.Lays;
        float amount = MathF.Max(GroundMist.FogAmount, 0f);
        s.Hold = 1f + AmountGain * amount;
        AirLayer layers = look.Layers(night);
        s.Columns = (layers & AirLayer.Dust) != 0 ? AirLayer.Dust : layers & AirLayer.Haze;
        for (int y = 0; y < GroundMist.Side; y++)
            for (int x = 0; x < GroundMist.Side; x++)
                s.Floor[y * GroundMist.Side + x] = GroundMist.FloorAt(s.Ox + x + 0.5f, s.Oy + y + 0.5f);
        LayRest(s);
        Array.Copy(s.Rest, s.Dust, s.Rest.Length);
        s.Wets = (look.Layers(true) & AirLayer.NightMist) != 0;
        s.Dusk = night && s.Wets ? 1f : 0f;
        if (s.Wets) LayMist(s);
        SeedPollen(s, s.Feet);
    }

    /// <summary>Lay the dust each column holds at rest at Dust 1: gathered along the walls and thicker as the fog amount
    /// rises, none off the floor or where the columns hold haze, which each trunk lays.</summary>
    private static void LayRest(AirSceneState s)
    {
        float lay = BaseDust * s.Hold;
        bool dust = s.Columns == AirLayer.Dust;
        for (int y = 0; y < GroundMist.Side; y++)
            for (int x = 0; x < GroundMist.Side; x++)
            {
                int c = y * GroundMist.Side + x;
                bool floor = dust && !float.IsNaN(s.Floor[c]);
                s.Rest[c] = floor ? lay * (1f + WallGather * MathF.Exp(-ToWall(s, x, y) / GatherFall)) : 0f;
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
            Enter(AirPlaces.Resolve(Loaded(interior)), night, interior);
            foreach (Vector2 t in AirArea.Trunks) Trunk(t);
            s = _s;
        }
        if (night != s.Night) Night(night);
        s.Scene = scene.Value;
        s.Seq = frame;
        Step(dt, wind);
    }

    /// <summary>A tree trunk standing at <paramref name="at"/>: in a forest it hazes the columns about it, whole within
    /// <see cref="HazeReach"/> less <see cref="HazeFeather"/>, and sheds leaves while the player is near it.</summary>
    internal static void Trunk(Vector2 at)
    {
        AirSceneState s = _s;
        if (!s.Laid || s.Columns != AirLayer.Haze) return;
        AddTrunk(s.Leaves, at);
        Haze(s, at, true);
        s.Gen++;
    }

    /// <summary>Lay the haze a trunk at <paramref name="at"/> holds at rest over its columns, and into what they hold
    /// <paramref name="now"/> as well.</summary>
    private static void Haze(AirSceneState s, Vector2 at, bool now)
    {
        float lay = BaseHaze * s.Hold;
        int r = (int)MathF.Ceiling(HazeReach);
        int cx = (int)MathF.Floor(at.X) - s.Ox, cy = (int)MathF.Floor(at.Y) - s.Oy;
        for (int y = Math.Max(cy - r, 0); y <= Math.Min(cy + r, GroundMist.Side - 1); y++)
            for (int x = Math.Max(cx - r, 0); x <= Math.Min(cx + r, GroundMist.Side - 1); x++)
            {
                int c = y * GroundMist.Side + x;
                float d = Vector2.Distance(new Vector2(s.Ox + x + 0.5f, s.Oy + y + 0.5f), at);
                float w = Math.Clamp((HazeReach - d) / HazeFeather, 0f, 1f);
                if (w <= 0f || float.IsNaN(s.Floor[c])) continue;
                float was = s.Rest[c];
                s.Rest[c] = MathF.Min(was + lay * w, lay);
                if (now) s.Dust[c] += s.Rest[c] - was;
            }
    }

    /// <summary>Advance the air <paramref name="dt"/> seconds under the area's <paramref name="wind"/>; at Low no front starts, with the air off nothing moves.</summary>
    internal static void Step(float dt, Vector2 wind)
    {
        AirSceneState s = _s;
        if (!s.Laid) return;
        if (s.Lays != GroundMist.Lays) Recentre(s);
        Dial(s);
        if (!On(s))
        {
            s.Stepped = s.Fresh = false;
            s.Front = default;
            return;
        }
        float speed = wind.Length();
        bool still = Air.Still;
        if (!s.Stepped && !s.Fresh) Settle(s, speed);
        else if (s.Stepped && !still && MathF.Abs(speed - s.Speed) > Calm) Launch(s, speed - s.Speed, speed > 0f ? wind / speed : s.Dir);
        s.Stepped = true;
        s.Fresh = false;
        s.Speed = speed;
        if (speed > 0f) s.Dir = wind / speed;
        s.Drift = wind * DriftShare;
        StepMotes(s, dt);
        StepPollen(s, dt);
        StepWisps(s, dt, wind, speed);
        StepLeaves(s, dt);
        Dusk(s, dt);
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
            s.Mist[c] += back * (s.MistRest[c] - s.Mist[c]) - off * s.Mist[c];
        }
        Carry(s, dt);
    }

    /// <summary>Take the Dust dial as it stands, a hold's value while one is in force; a change shows at the next upload.</summary>
    private static void Dial(AirSceneState s)
    {
        float dial = Air.Dust.V;
        if (dial == s.Dial) return;
        s.Dial = dial;
        s.Gen++;
    }

    /// <summary>Lay each column's dust and mist as a steady <paramref name="speed"/> leaves it, where scour and refill balance.</summary>
    private static void Settle(AirSceneState s, float speed)
    {
        float keep = Refill * s.Hold / (Refill * s.Hold + Scour * speed / s.Hold);
        for (int c = 0; c < s.Dust.Length; c++) (s.Dust[c], s.Mist[c]) = (s.Rest[c] * keep, s.MistRest[c] * keep);
    }

    /// <summary>A creature that stepped from <paramref name="was"/> to <paramref name="now"/> clears the dust at its feet,
    /// which closes back over its path, and swirls the motes it passes through. Hot: allocates nothing; at Low and with the air off it parts nothing.</summary>
    internal static void Part(Vector3 was, Vector3 now)
    {
        AirSceneState s = _s;
        if (!s.Laid || !On(s) || Air.Still) return;
        if (Vector2.Distance(new Vector2(was.X, was.Y), new Vector2(now.X, now.Y)) > GroundMist.MaxPartStep) return;
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
        Stir(s, was, now);
    }

    /// <summary>Send a front along <paramref name="dir"/> for a change of <paramref name="change"/> metres a second.</summary>
    private static void Launch(AirSceneState s, float change, Vector2 dir)
    {
        s.Front = new AirFront(true, change > 0f ? change : -change * DropShare, dir);
        s.Line = -FrontReach;
        TearWisps(s, s.Front.Strength);
    }

    /// <summary>Run the live front on by <paramref name="dt"/>, tearing the dust and mist of each column it crosses and
    /// lifting the dust.</summary>
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
                bool dust = s.Dust[c] > 0f;
                if (!dust && s.Mist[c] <= 0f) continue;
                Vector2 at = new Vector2(s.Ox + x + 0.5f, s.Oy + y + 0.5f) - s.Feet;
                float along = Vector2.Dot(at, f.Dir);
                if (along <= from || along > to) continue;
                float torn = MathF.Min(tear * (1f + Streak * Uneven(Vector2.Dot(at, across))), 1f);
                s.Dust[c] *= 1f - torn;
                s.Mist[c] *= 1f - torn;
                if (dust) s.Lift[c] += LiftPerTear * torn;
            }
    }

    /// <summary>How unevenly a front tears at <paramref name="across"/> metres along its line, from -1 to 1.</summary>
    private static float Uneven(float across)
        => (MathF.Sin(2.1f * across + 0.7f) + 0.7f * MathF.Sin(3.7f * across + 2.3f) + 0.5f * MathF.Sin(1.3f * across + 4.1f)) / 2.2f;

    /// <summary>How much of <paramref name="layer"/> hangs at <paramref name="at"/>: the dust, a forest's haze or the
    /// mist thickest on the floor and thinning to nothing at its top, the dust stirred slowly where it lies, or the
    /// wisps high overhead. None with the air off.</summary>
    internal static float Density(AirLayer layer, Vector3 at)
    {
        AirSceneState s = _s;
        if (layer == AirLayer.Wisps) return On(s) ? WispAt(s, at) : 0f;
        int c = Column(s, at.X, at.Y);
        if (c < 0 || !On(s)) return 0f;
        if (layer == AirLayer.NightMist) return Profile(MistShown(s, c, at.X, at.Y), at.Z - s.Floor[c], MistTop);
        if (layer == AirLayer.None || layer != s.Columns) return 0f;
        (float depth, float h) = Shown(s, c, at.X, at.Y);
        return Profile(depth, at.Z - s.Floor[c], h);
    }

    /// <summary>The density <paramref name="z"/> metres over its floor of a layer <paramref name="depth"/> metres deep
    /// standing <paramref name="h"/> tall: thickest on the floor, nothing at its top, integrating to its depth.</summary>
    private static float Profile(float depth, float z, float h) => z < 0f || z >= h ? 0f : 2f * depth * (1f - z / h) / h;

    /// <summary>Does the area's dust, haze or mist lie, to be drawn? False with the air off or in a place with none.</summary>
    internal static bool Lies => _s.Laid && On(_s) && (_s.Columns != AirLayer.None || Misty(_s));

    /// <summary>Is the area the air lies in an interior, its air lit by the room and not the sky?</summary>
    internal static bool Indoors => _s.Indoors;

    /// <summary>Is the air on in this area? A hold reads as it stood when the area was entered, so a later change waits
    /// for the next entry; the player's own switch reads live.</summary>
    internal static bool On() => On(_s);

    private static bool On(AirSceneState s) => s.Held ? s.HeldOn : Air.Enabled.Own;

    /// <summary>Runs on each time the dust's columns change, across areas, so an upload knows to send.</summary>
    internal static int Gen => _s.Gen;

    /// <summary>The dust or haze shown on the column holding world (<paramref name="x"/>, <paramref name="y"/>), metres
    /// of full density, and the height it reaches over its floor; both 0 off the air's floor.</summary>
    internal static (float Depth, float Top) Shown(float x, float y)
    {
        AirSceneState s = _s;
        int c = Column(s, x, y);
        return c < 0 ? (0f, 0f) : Shown(s, c, MathF.Floor(x) + 0.5f, MathF.Floor(y) + 0.5f);
    }

    /// <summary>Column <paramref name="c"/>'s depth stirred at (<paramref name="x"/>, <paramref name="y"/>) and its top: the
    /// dust at the Dust dial below the knee, the haze whole up past the crowns' undersides.</summary>
    private static (float Depth, float Top) Shown(AirSceneState s, int c, float x, float y)
    {
        bool haze = s.Columns == AirLayer.Haze;
        float depth = (haze ? 1f : s.Dial) * s.Dust[c] * (1f - s.Gap[c]) * (1f + StirShare * Stir(x, y, s.Clock));
        return (depth, (haze ? HazeHeight : RestHeight) + s.Lift[c]);
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

    /// <summary>The gust front crossing the air now.</summary>
    internal static AirFront Front => _s.Front;

    /// <summary>The air's own motion at a ground point, in metres a second.</summary>
    internal static Vector2 Motion(float x, float y) => _s.Drift;

    /// <summary>Drop the area's air.</summary>
    internal static void Reset() => _s = new AirSceneState { Gen = _s.Gen + 1 };
}

/// <summary>Everything the air holds for one area: its look, its columns over the mist grid and the front crossing it.</summary>
internal sealed class AirSceneState
{
    /// <summary>The look the area took, by night or by day, and the motes in its lamps' light.</summary>
    public AirLook Look;
    public bool Night;
    public readonly AirMotes Motes = new();

    /// <summary>Whether the area is an interior, which no sky lights.</summary>
    public bool Indoors;

    /// <summary>The pollen about the player, and the wisps high overhead.</summary>
    public readonly AirPollen Pollen = new();
    public readonly AirWispSheet Sheet = new();

    /// <summary>A forest's trunks and the leaves they shed.</summary>
    public readonly AirLeaves Leaves = new();

    /// <summary>The grid's corner in whole metres and the player's feet across the ground, and whether a floor is laid.</summary>
    public int Ox, Oy;
    public Vector2 Feet;
    public bool Laid;

    /// <summary>The mist field's floor lay the columns were laid over: when it runs on, they follow it.</summary>
    public int Lays;

    /// <summary>Per column, row-major from the corner: its floor (NaN for none), the dust it holds at rest and its dust
    /// now, both at Dust 1, and how high a front threw it.</summary>
    public readonly float[] Floor = new float[GroundMist.Side * GroundMist.Side];
    public readonly float[] Rest = new float[GroundMist.Side * GroundMist.Side];
    public readonly float[] Dust = new float[GroundMist.Side * GroundMist.Side];
    public readonly float[] Lift = new float[GroundMist.Side * GroundMist.Side];

    /// <summary>Per column, the night mist it holds at rest and now, before the night's share; and a scratch column set.</summary>
    public readonly float[] MistRest = new float[GroundMist.Side * GroundMist.Side];
    public float[] Mist = new float[GroundMist.Side * GroundMist.Side];
    public float[] Spare = new float[GroundMist.Side * GroundMist.Side];

    /// <summary>Whether the area's water can gather night mist, and the night's share of it shown now, 0 to 1.</summary>
    public bool Wets;
    public float Dusk;

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

    /// <summary>The layer the columns hold, dust or a forest's haze, none in a place with neither; and the upload
    /// generation its columns are at.</summary>
    public AirLayer Columns;
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
