using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The motes a crypt's lamps hold in their light: each wanders a path of its own, drifts with the air, and goes
/// dark past its lamp's reach.</summary>
internal static partial class AirScene
{
    /// <summary>The most lamps that hold motes at once, and the motes each holds.</summary>
    internal const int MaxLamps = 16, PerLamp = 40, MaxMotes = MaxLamps * PerLamp;

    /// <summary>A lamp reaching less than this, metres, holds no motes.</summary>
    private const float MinLampReach = 1f;

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

    /// <summary>Does the area's look hang motes?</summary>
    private static bool Hangs(AirSceneState s) => (s.Look.Layers(s.Night) & AirLayer.Motes) != 0;

    /// <summary>A lamp at <paramref name="at"/> reaching <paramref name="reach"/> metres; it fills its light with motes at
    /// once. A lamp already seated there takes the new reach; past <see cref="MaxLamps"/> one holds none.</summary>
    internal static void Lamp(Vector3 at, float reach)
    {
        AirSceneState s = _s;
        AirMotes m = s.Motes;
        if (!s.Laid || !(reach >= MinLampReach)) return;
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
        m.Pos[i] = p;
        m.Vel[i] = Vector3.Zero;
        m.Phase[i] = MathF.Tau * new Vector3(Hash(key, 3), Hash(key, 4), Hash(key, 5));
        m.Rate[i] = new Vector3(Rate(Hash(key, 6)), Rate(Hash(key, 7)), Rate(Hash(key, 8)));
        m.Dark[i] = false;
        m.Born[i] = m.Clock;
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
        float follow = 1f - MathF.Exp(-dt / (f.Live ? RaceTau : FollowTau));
        int n = m.Lamps * PerLamp;
        for (int i = 0; i < n; i++)
        {
            if (m.Dark[i])
            {
                if (m.Calm >= CalmWait + MoteWait * Hash((uint)i * 0x9E3779B1u, m.Births[i] + 9u)) Seed(m, i);
                continue;
            }
            Vector3 w = m.Rate[i] * m.Clock + m.Phase[i];
            Vector3 own = Wander * new Vector3(MathF.Sin(w.X), MathF.Sin(w.Y), 0.5f * MathF.Sin(w.Z));
            m.Vel[i] += (air + own - m.Vel[i]) * follow;
            m.Pos[i] += m.Vel[i] * dt;
            int l = i / PerLamp;
            if (Vector3.DistanceSquared(m.Pos[i], m.LampAt[l]) > m.LampReach[l] * m.LampReach[l]) m.Dark[i] = true;
        }
    }

    /// <summary>The particles of <paramref name="layer"/>: the motes in their lamps' light, in a fixed order while each
    /// stays lit. None with the air off or in a place that hangs none.</summary>
    internal static ReadOnlySpan<AirParticle> Particles(AirLayer layer)
    {
        AirSceneState s = _s;
        if (layer != AirLayer.Motes || !s.Laid || !On(s) || !Hangs(s)) return ReadOnlySpan<AirParticle>.Empty;
        AirMotes m = s.Motes;
        int n = 0;
        for (int i = 0; i < m.Lamps * PerLamp; i++)
            if (!m.Dark[i]) m.Shown[n++] = new AirParticle(m.Pos[i], m.Vel[i]);
        return m.Shown.AsSpan(0, n);
    }
}

/// <summary>One area's lamps and the motes in their light, mote i held by lamp i / <see cref="AirScene.PerLamp"/>.</summary>
internal sealed class AirMotes
{
    /// <summary>Each seated lamp's place, reach and the floor under it, and how many are seated.</summary>
    public readonly Vector3[] LampAt = new Vector3[AirScene.MaxLamps];
    public readonly float[] LampReach = new float[AirScene.MaxLamps];
    public readonly float[] LampFloor = new float[AirScene.MaxLamps];
    public int Lamps;

    /// <summary>Per mote: where it is, how it moves, its path's phase and turn, and whether it has left the light.</summary>
    public readonly Vector3[] Pos = new Vector3[AirScene.MaxMotes];
    public readonly Vector3[] Vel = new Vector3[AirScene.MaxMotes];
    public readonly Vector3[] Phase = new Vector3[AirScene.MaxMotes];
    public readonly Vector3[] Rate = new Vector3[AirScene.MaxMotes];
    public readonly bool[] Dark = new bool[AirScene.MaxMotes];

    /// <summary>Per mote: how many times it was set afloat, and the motes' clock when it last was.</summary>
    public readonly uint[] Births = new uint[AirScene.MaxMotes];
    public readonly float[] Born = new float[AirScene.MaxMotes];

    /// <summary>How long the motes have floated, and how long since a front last blew, seconds.</summary>
    public float Clock, Calm;

    /// <summary>The lit motes as <see cref="AirScene.Particles"/> hands them out.</summary>
    public readonly AirParticle[] Shown = new AirParticle[AirScene.MaxMotes];
}
