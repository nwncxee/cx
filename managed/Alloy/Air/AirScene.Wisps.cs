using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The pale wisps high over a meadow: streaks the wind stretches along its heading and carries with it, torn
/// thin by a gust front and filling back once it passes. Nothing on the ground touches them.</summary>
internal static partial class AirScene
{
    /// <summary>The stage that lays the wisps over the lit frame, and the records <see cref="PackWisps"/> writes.</summary>
    internal const string WispCs = "cx_airwisp_cs";
    internal const int WispsPacked = 5;

    /// <summary>How far over the floor the wisps' band starts and how deep it is, metres: thickest at its middle.</summary>
    private const float WispLow = 30f, WispDepth = 60f;

    /// <summary>How thick the wisps are at their middle at fog amount 0, per metre.</summary>
    private const float WispBase = 0.01f;

    /// <summary>How fast the wisps creep in still air, metres a second, and the share of the wind's speed they take.</summary>
    internal const float WispCalm = 0.2f;
    private const float WispWind = 0.9f;

    /// <summary>How fast the wisps take a change in the wind, seconds, and how much longer each metre a second of it stretches them.</summary>
    private const float WispTau = 2f, WispPull = 0.5f;

    /// <summary>The share of the wisps a front tears per metre a second of change, and how fast they fill back, per second.</summary>
    private const float WispTear = 0.25f, WispRefill = 0.2f;

    /// <summary>How long a streak runs along the wind, metres before any stretch, and the two widths across it, metres.</summary>
    private const float WispLong = 24f, WispWideA = 16f / 3f, WispWideB = 3.2f;

    /// <summary>How far a streak wanders across the wind, metres, and the span across it of one wander, metres.</summary>
    private const float WispWarp = 4f, WispKnot = 12f;

    /// <summary>The wisps' shape now.</summary>
    internal static AirWisps Wisps => new(_s.Sheet.Stretch, _s.Sheet.Carried);

    /// <summary>Carry and stretch the wisps <paramref name="dt"/> on under <paramref name="wind"/>, and fill back what a
    /// front tore behind its line. Every step the air takes, at Low too.</summary>
    private static void StepWisps(AirSceneState s, float dt, Vector2 wind, float speed)
    {
        AirWispSheet w = s.Sheet;
        float ease = 1f - MathF.Exp(-dt / WispTau), back = 1f - MathF.Exp(-dt * WispRefill * s.Hold);
        w.Vel += (s.Dir * WispCalm + wind * WispWind - w.Vel) * ease;
        w.Carried += w.Vel * dt;
        w.Stretch += (s.Dir * (1f + WispPull * speed) - w.Stretch) * ease;
        w.Fill += (1f - w.Fill) * back;
    }

    /// <summary>A front of <paramref name="strength"/> sets out: the wisps behind its line are torn, those ahead stay whole
    /// until it reaches them.</summary>
    private static void TearWisps(AirSceneState s, float strength)
    {
        AirWispSheet w = s.Sheet;
        w.Ahead = w.Fill;
        w.Fill *= 1f - MathF.Min(WispTear * strength / s.Hold, 1f);
    }

    /// <summary>How thick the wisps hang at <paramref name="at"/>: none below their band or past it, thickest in its middle,
    /// in streaks along the wind; thicker as the area's fog amount rises.</summary>
    private static float WispAt(AirSceneState s, Vector3 at)
    {
        int c = Column(s, at.X, at.Y);
        if (c < 0 || (s.Look.Layers(s.Night) & AirLayer.Wisps) == 0) return 0f;
        float z = at.Z - s.Floor[c] - WispLow;
        if (z <= 0f || z >= WispDepth) return 0f;
        AirWispSheet w = s.Sheet;
        bool ahead = s.Front.Live && Vector2.Dot(new Vector2(at.X, at.Y) - s.Feet, s.Front.Dir) > s.Line;
        return WispBase * s.Hold * (ahead ? w.Ahead : w.Fill) * MathF.Sin(MathF.PI * z / WispDepth) * Streaks(w, at.X, at.Y);
    }

    /// <summary>The wisps' streaks at world (<paramref name="x"/>, <paramref name="y"/>), from 0 to 2 and about 1 across a
    /// streak's width: bands along the wind that wander across it, each waxing and waning along it.</summary>
    private static float Streaks(AirWispSheet w, float x, float y)
    {
        float len = w.Stretch.Length();
        Vector2 dir = len > 0f ? w.Stretch / len : Vector2.UnitX, q = new Vector2(x, y) - w.Carried;
        float along = Vector2.Dot(q, dir) / (WispLong * MathF.Max(len, 1f)), across = q.Y * dir.X - q.X * dir.Y;
        float a = across + WispWarp * WispWander(along, across / WispKnot), b = across + WispWarp * WispWander(along + 7.3f, across / WispKnot + 3.1f);
        return 1f + 0.6f * MathF.Sin(MathF.Tau * a / WispWideA) * (0.5f + 0.5f * MathF.Sin(MathF.Tau * along + 0.4f))
                  + 0.4f * MathF.Sin(MathF.Tau * b / WispWideB + 2.1f) * (0.5f + 0.5f * MathF.Sin(1.7f * MathF.Tau * along + 2.9f));
    }

    /// <summary>Smooth value noise at (<paramref name="x"/>, <paramref name="y"/>), -1 to 1, one lattice cell a unit wide;
    /// the wisp stage's CxWispWander, its hash bit for bit.</summary>
    private static float WispWander(float x, float y)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y), u = x - fx, v = y - fy;
        int ix = (int)fx, iy = (int)fy;
        u = u * u * (3f - 2f * u);
        v = v * v * (3f - 2f * v);
        float lo = float.Lerp(WispLattice(ix, iy), WispLattice(ix + 1, iy), u), hi = float.Lerp(WispLattice(ix, iy + 1), WispLattice(ix + 1, iy + 1), u);
        return 2f * float.Lerp(lo, hi, v) - 1f;
    }


    /// <summary>The lattice point (<paramref name="ix"/>, <paramref name="iy"/>)'s value, 0 to 1.</summary>
    private static float WispLattice(int ix, int iy)
    {
        uint h = unchecked((uint)ix * 0x8da6b343u ^ (uint)iy * 0xd8163841u);
        h = unchecked((h ^ (h >> 13)) * 0x5bd1e995u);
        h ^= h >> 15;
        return (h & 0xffffu) / 65535f;
    }

    /// <summary>The wisps' streaks now at world (<paramref name="x"/>, <paramref name="y"/>), as the wisp stage lays them.</summary>
    internal static float StreakAt(float x, float y) => Streaks(_s.Sheet, x, y);

    /// <summary>Do wisps hang over this frame: the air on, in a place that hangs them?</summary>
    internal static bool Hung => _s.Laid && On(_s) && (_s.Look.Layers(_s.Night) & AirLayer.Wisps) != 0;

    /// <summary>Write the wisps into <paramref name="into"/> as the wisp stage reads them: their band, how thick, the front's
    /// line and heading from the feet, how far carried and stretched, what is left behind and ahead of the line, and the
    /// streaks' sizes and wander. Returns the records written, 0 when none hang.
    /// Hot: allocates nothing.</summary>
    internal static int PackWisps(Span<Vector4> into)
    {
        if (!Hung || into.Length < WispsPacked) return 0;
        AirSceneState s = _s;
        AirWispSheet w = s.Sheet;
        int c = Column(s, s.Feet.X, s.Feet.Y);
        float floor = c >= 0 ? s.Floor[c] : 0f;
        float line = s.Front.Live ? s.Line : float.MaxValue;
        into[0] = new Vector4(floor + WispLow, WispDepth, WispBase * s.Hold, line);
        into[1] = new Vector4(w.Carried.X, w.Carried.Y, w.Stretch.X, w.Stretch.Y);
        into[2] = new Vector4(s.Front.Dir.X, s.Front.Dir.Y, s.Feet.X, s.Feet.Y);
        into[3] = new Vector4(w.Fill, w.Ahead, WispLong, WispWideA);
        into[4] = new Vector4(WispWideB, WispWarp, WispKnot, 0f);
        return WispsPacked;
    }
}

/// <summary>The wisps over one area: how far the wind carried them and how fast, how far it stretches them, and how
/// much of them is left behind and ahead of a front.</summary>
internal sealed class AirWispSheet
{
    /// <summary>How far the wind has carried the wisps and how fast it carries them now, and how far it stretches them, metres.</summary>
    public Vector2 Carried, Vel = Vector2.UnitX * AirScene.WispCalm, Stretch = Vector2.UnitX;

    /// <summary>The share of the wisps left behind a front's line, and ahead of it, 0 to 1.</summary>
    public float Fill = 1f, Ahead = 1f;
}
