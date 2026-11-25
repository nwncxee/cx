using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The motes a crypt's lamps hold in their light: each wanders a path of its own, drifts with the air, and goes
/// dark past its lamp's reach.</summary>
internal static partial class AirScene
{
    /// <summary>The most lamps that hold motes at once, and the motes each holds.</summary>
    internal const int MaxLamps = 16, PerLamp = 40, MaxMotes = MaxLamps * PerLamp;

    /// <summary>The stage that draws the motes, and the records <see cref="PackMotes"/> writes at most: a head and one a mote.</summary>
    internal const string MoteCs = "cx_airmote_cs";
    internal const int MotesPacked = MaxMotes + 1;

    /// <summary>How long a mote takes to show once set afloat, seconds, and the share of its lamp's reach it fades out over at the edge.</summary>
    private const float MoteFadeIn = 1f, MoteEdge = 0.2f;

    /// <summary>Two lamps closer than this, metres, are one lamp.</summary>
    private const float SameLamp = 0.25f;

    /// <summary>The share of a lamp's reach its motes are seeded within, and how far over the floor the lowest floats, metres.</summary>
    private const float MoteCore = 0.6f, MoteFloorGap = 0.3f;

    /// <summary>How fast a mote wanders on its own, metres a second, and the slowest and fastest turn of its path, radians a second.</summary>
    private const float Wander = 0.1f, RateLo = 0.25f, RateHi = 0.6f;

    /// <summary>The share of the air's drift a mote takes, and how fast it takes it, seconds.</summary>
    private const float MoteShare = 0.2f, FollowTau = 1f;

    /// <summary>How fast a live front sends a mote racing, metres a second for each metre a second of its strength, and how
    /// fast a mote takes the front, seconds.</summary>
    private const float RacePerSpeed = 3f, RaceTau = 0.15f;

    /// <summary>How long the air stays calm before a mote that left the light floats in it again, seconds, and the most each
    /// mote waits on top of that.</summary>
    private const float CalmWait = 2f, MoteWait = 2f;

    /// <summary>How long a swirl or a throw takes to die away, seconds.</summary>
    private const float KickTau = 2f;

    /// <summary>The reach and height of a walker's wake through the motes, metres, and how fast it swirls the nearest, metres a second.</summary>
    private const float WakeRadius = 1.5f, WakeHeight = 2.2f, WakeSpeed = 2.5f;

    /// <summary>How fast a wake's eddy turns a swirled mote, radians a second, so it circles about where it was rather than
    /// being swept along.</summary>
    private const float EddyTurn = 3f;

    /// <summary>How fast a blast throws a mote at its heart, metres a second, falling to nothing at its edge.</summary>
    private const float BlastSpeed = 4f;

    /// <summary>Does the area's look hang motes?</summary>
    private static bool Hangs(AirSceneState s) => (s.Look.Layers(s.Night) & AirLayer.Motes) != 0;

    /// <summary>A lamp at <paramref name="at"/> reaching <paramref name="reach"/> metres; it fills its light with motes at
    /// once. A lamp already seated there takes the new reach; past <see cref="MaxLamps"/> one holds none.</summary>
    internal static void Lamp(Vector3 at, float reach)
    {
        AirSceneState s = _s;
        AirMotes m = s.Motes;
        if (!s.Laid || !(reach > 0f)) return;
        for (int l = 0; l < m.Lamps; l++)
            if (Vector3.DistanceSquared(m.LampAt[l], at) < SameLamp * SameLamp) { m.LampReach[l] = reach; return; }
        if (m.Lamps == MaxLamps) return;
        int k = m.Lamps++;
        int c = Column(s, at.X, at.Y);
        m.LampAt[k] = at;
        m.LampReach[k] = reach;
        m.LampFloor[k] = c >= 0 ? s.Floor[c] : at.Z - MoteCore * reach;
        for (int i = k * PerLamp; i < (k + 1) * PerLamp; i++)
        {
            m.Births[i] = 0;
            Seed(m, i);
        }
    }

    /// <summary>Set mote <paramref name="i"/> afloat at a fresh point in its lamp's light, on a path of its own.</summary>
    private static void Seed(AirMotes m, int i)
    {
        int l = i / PerLamp;
        uint key = (uint)i * 0x9E3779B1u ^ m.Births[i]++ * 0x85EBCA77u;
        Vector3 at = m.LampAt[l];
        float core = MoteCore * m.LampReach[l];
        float r = core * MathF.Cbrt(Hash(key, 0)), up = 2f * Hash(key, 1) - 1f, round = MathF.Tau * Hash(key, 2);
        float flat = MathF.Sqrt(MathF.Max(1f - up * up, 0f));
        Vector3 p = at + r * new Vector3(flat * MathF.Cos(round), flat * MathF.Sin(round), up);
        float lo = m.LampFloor[l] + MoteFloorGap;
        if (p.Z < lo) p.Z = MathF.Min(2f * lo - p.Z, at.Z + core);
        Afloat(m, i, key, p);
    }

    /// <summary>Set grain <paramref name="i"/> of <paramref name="g"/> afloat at <paramref name="p"/>, still, on the path
    /// <paramref name="key"/> draws for it.</summary>
    private static void Afloat(AirGrains g, int i, uint key, Vector3 p)
    {
        g.Pos[i] = p;
        g.Vel[i] = g.Kick[i] = Vector3.Zero;
        g.Spin[i] = 0f;
        g.Phase[i] = MathF.Tau * new Vector3(Hash(key, 3), Hash(key, 4), Hash(key, 5));
        g.Rate[i] = new Vector3(Rate(Hash(key, 6)), Rate(Hash(key, 7)), Rate(Hash(key, 8)));
        g.Dark[i] = false;
        g.Born[i] = g.Clock;
    }

    /// <summary>A path's turn for <paramref name="h"/> from 0 to 1.</summary>
    private static float Rate(float h) => RateLo + (RateHi - RateLo) * h;

    /// <summary>A fixed number from 0 to 1 for <paramref name="key"/>'s draw <paramref name="n"/>.</summary>
    private static float Hash(uint key, uint n)
    {
        uint x = key + n * 0x632BE5ABu;
        x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
        return (x >> 8) * (1f / (1 << 24));
    }

    /// <summary>Move every lit mote on by <paramref name="dt"/>: its own wander, the air's drift and a live front's rush; a
    /// mote that left the light floats in it again once the air has been calm a while. Hot: allocates nothing.</summary>
    private static void StepMotes(AirSceneState s, float dt)
    {
        AirMotes m = s.Motes;
        if (m.Lamps == 0 || !Hangs(s)) return;
        m.Clock += dt;
        AirFront f = s.Front;
        m.Calm = f.Live ? 0f : m.Calm + dt;
        Vector3 air = new(s.Drift * MoteShare + (f.Live ? f.Dir * (RacePerSpeed * f.Strength) : Vector2.Zero), 0f);
        float follow = 1f - MathF.Exp(-dt / (f.Live ? RaceTau : FollowTau)), fade = MathF.Exp(-dt / KickTau);
        int n = m.Lamps * PerLamp;
        for (int i = 0; i < n; i++)
        {
            if (m.Dark[i])
            {
                if (m.Calm >= CalmWait + MoteWait * Hash((uint)i * 0x9E3779B1u, m.Births[i] + 9u)) Seed(m, i);
                continue;
            }
            Float(m, i, air, follow, fade, dt);
            int l = i / PerLamp;
            if (Vector3.DistanceSquared(m.Pos[i], m.LampAt[l]) > m.LampReach[l] * m.LampReach[l]) m.Dark[i] = true;
        }
    }

    /// <summary>Move grain <paramref name="i"/> of <paramref name="g"/> on by <paramref name="dt"/>: its own wander on top of
    /// <paramref name="air"/>, taken at <paramref name="follow"/>, and its swirl or throw turning and dying by <paramref name="fade"/>.</summary>
    private static void Float(AirGrains g, int i, Vector3 air, float follow, float fade, float dt)
    {
        Vector3 w = g.Rate[i] * g.Clock + g.Phase[i];
        Vector3 own = Wander * new Vector3(MathF.Sin(w.X), MathF.Sin(w.Y), 0.5f * MathF.Sin(w.Z));
        g.Vel[i] += (air + own - g.Vel[i]) * follow;
        g.Kick[i] = Turn(g.Kick[i], g.Spin[i] * dt) * fade;
        g.Pos[i] += (g.Vel[i] + g.Kick[i]) * dt;
    }

    /// <summary>A walker who stepped from <paramref name="was"/> to <paramref name="now"/> swirls the motes and the pollen
    /// about it, round its sides and up, the nearest fastest; a grain keeps the stronger of this and the swirl it had.</summary>
    private static void Stir(AirSceneState s, Vector3 was, Vector3 now)
    {
        AirMotes m = s.Motes;
        Vector2 step = new(now.X - was.X, now.Y - was.Y);
        if (step == Vector2.Zero) return;
        Vector2 ahead = Vector2.Normalize(step), side = new(-ahead.Y, ahead.X);
        if (m.Lamps > 0 && Hangs(s)) Swirl(m, m.Lamps * PerLamp, ahead, side, now);
        if (Glints(s)) Swirl(s.Pollen, MaxPollen, ahead, side, now);
    }

    /// <summary>Swirl the first <paramref name="n"/> grains of <paramref name="g"/> about a walker at <paramref name="now"/>
    /// heading <paramref name="ahead"/>.</summary>
    private static void Swirl(AirGrains g, int n, Vector2 ahead, Vector2 side, Vector3 now)
    {
        for (int i = 0; i < n; i++)
        {
            Vector3 p = g.Pos[i];
            Vector2 off = new(p.X - now.X, p.Y - now.Y);
            float d = off.Length();
            if (g.Dark[i] || d >= WakeRadius || p.Z < now.Z || p.Z > now.Z + WakeHeight) continue;
            Vector2 round = Vector2.Dot(off, side) < 0f ? -side : side;
            Vector3 swirl = Vector3.Normalize(new Vector3(0.7f * ahead + 0.5f * round, 0.3f)) * (WakeSpeed * (1f - 0.5f * d / WakeRadius));
            if (swirl.LengthSquared() <= g.Kick[i].LengthSquared()) continue;
            g.Kick[i] = swirl;
            g.Spin[i] = round == side ? EddyTurn : -EddyTurn;
        }
    }

    /// <summary>A blast at <paramref name="at"/> of <paramref name="radius"/> metres throws the motes and the pollen in it
    /// outward, the nearest fastest; at Low and with the air off it throws none.</summary>
    internal static void Blast(Vector3 at, float radius)
    {
        AirSceneState s = _s;
        AirMotes m = s.Motes;
        if (!s.Laid || !On(s) || Air.Quality.V == Air.Levels[0] || !(radius > 0f)) return;
        if (m.Lamps > 0 && Hangs(s)) Throw(m, m.Lamps * PerLamp, at, radius);
        if (Glints(s)) Throw(s.Pollen, MaxPollen, at, radius);
    }

    /// <summary>Throw the first <paramref name="n"/> grains of <paramref name="g"/> out from a blast at <paramref name="at"/>.</summary>
    private static void Throw(AirGrains g, int n, Vector3 at, float radius)
    {
        for (int i = 0; i < n; i++)
        {
            Vector3 off = g.Pos[i] - at;
            float d = off.Length();
            if (g.Dark[i] || d >= radius || d == 0f) continue;
            Vector3 thrown = off * (BlastSpeed * (1f - d / radius) / d);
            if (thrown.LengthSquared() <= g.Kick[i].LengthSquared()) continue;
            g.Kick[i] = thrown;
            g.Spin[i] = 0f;
        }
    }

    /// <summary><paramref name="v"/> turned <paramref name="by"/> radians about the vertical.</summary>
    private static Vector3 Turn(Vector3 v, float by)
    {
        if (by == 0f) return v;
        (float sin, float cos) = MathF.SinCos(by);
        return new Vector3(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, v.Z);
    }

    /// <summary>The area's static lights as lamps: each of the first <paramref name="count"/> that is static seats its
    /// motes, the first <see cref="MaxLamps"/> of them.</summary>
    internal static void Lamps(Vector4[] posRange, bool[] isStatic, int count)
    {
        // ponytail: the walk's first static lights hold the motes; the lamps nearest the player once an area has more than MaxLamps
        if (posRange == null || isStatic == null) return;
        int n = Math.Min(count, Math.Min(posRange.Length, isStatic.Length));
        for (int i = 0; i < n && _s.Motes.Lamps < MaxLamps; i++)
            if (isStatic[i]) Lamp(new Vector3(posRange[i].X, posRange[i].Y, posRange[i].Z), posRange[i].W);
    }

    /// <summary>Do motes float this frame: the air on, in a place that hangs them, with a lamp seated?</summary>
    internal static bool Floats => _s.Laid && On(_s) && Hangs(_s) && _s.Motes.Lamps > 0;

    /// <summary>Write the lit motes into <paramref name="into"/> as the mote stage reads them: a head holding their count,
    /// then each one's place and how brightly it shows. Returns the records written, 0 when none float. Hot: allocates nothing.</summary>
    internal static int PackMotes(Span<Vector4> into)
    {
        if (!Floats || into.Length < 1) return 0;
        AirMotes m = _s.Motes;
        int n = 0;
        for (int i = 0; i < m.Lamps * PerLamp && n + 1 < into.Length; i++)
        {
            if (m.Dark[i]) continue;
            int l = i / PerLamp;
            float edge = (m.LampReach[l] - Vector3.Distance(m.Pos[i], m.LampAt[l])) / (MoteEdge * m.LampReach[l]);
            float glow = Math.Clamp(MathF.Min((m.Clock - m.Born[i]) / MoteFadeIn, edge), 0f, 1f);
            into[++n] = new Vector4(m.Pos[i], glow);
        }
        into[0] = new Vector4(n, 0f, 0f, 0f);
        return n + 1;
    }

    /// <summary>The particles of <paramref name="layer"/>: the motes in their lamps' light, or the pollen about the player,
    /// in a fixed order while each stays lit. None with the air off or in a place that hangs none.</summary>
    internal static ReadOnlySpan<AirParticle> Particles(AirLayer layer)
    {
        AirSceneState s = _s;
        if (!s.Laid || !On(s)) return ReadOnlySpan<AirParticle>.Empty;
        if (layer == AirLayer.Motes && Hangs(s)) return Hand(s.Motes, s.Motes.Lamps * PerLamp);
        if (layer == AirLayer.Pollen && Glints(s)) return Hand(s.Pollen, MaxPollen);
        return ReadOnlySpan<AirParticle>.Empty;
    }

    /// <summary>The lit grains of the first <paramref name="n"/> of <paramref name="g"/>, each moving as it drifts and is swirled.</summary>
    private static ReadOnlySpan<AirParticle> Hand(AirGrains g, int n)
    {
        int k = 0;
        for (int i = 0; i < n; i++)
            if (!g.Dark[i]) g.Shown[k++] = new AirParticle(g.Pos[i], g.Vel[i] + g.Kick[i]);
        return g.Shown.AsSpan(0, k);
    }
}

/// <summary>Grains that float in the air, each on a path of its own: where it is, how it drifts, what swirls it.</summary>
internal class AirGrains(int capacity)
{
    /// <summary>Per grain: where it is, how it drifts, the swirl or throw on top and the eddy turning it, its path's phase and turn, and whether it is out of sight.</summary>
    public readonly Vector3[] Pos = new Vector3[capacity];
    public readonly Vector3[] Vel = new Vector3[capacity];
    public readonly Vector3[] Kick = new Vector3[capacity];
    public readonly float[] Spin = new float[capacity];
    public readonly Vector3[] Phase = new Vector3[capacity];
    public readonly Vector3[] Rate = new Vector3[capacity];
    public readonly bool[] Dark = new bool[capacity];

    /// <summary>Per grain: how many times it was set afloat, and the grains' clock when it last was.</summary>
    public readonly uint[] Births = new uint[capacity];
    public readonly float[] Born = new float[capacity];

    /// <summary>How long the grains have floated, and how long since a front last blew, seconds.</summary>
    public float Clock, Calm;

    /// <summary>The lit grains as <see cref="AirScene.Particles"/> hands them out.</summary>
    public readonly AirParticle[] Shown = new AirParticle[capacity];
}

/// <summary>One area's lamps and the motes in their light, mote i held by lamp i / <see cref="AirScene.PerLamp"/>.</summary>
internal sealed class AirMotes() : AirGrains(AirScene.MaxMotes)
{
    /// <summary>Each seated lamp's place, reach and the floor under it, and how many are seated.</summary>
    public readonly Vector3[] LampAt = new Vector3[AirScene.MaxLamps];
    public readonly float[] LampReach = new float[AirScene.MaxLamps];
    public readonly float[] LampFloor = new float[AirScene.MaxLamps];
    public int Lamps;
}
