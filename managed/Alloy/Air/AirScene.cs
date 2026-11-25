using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The area's air over the ground mist grid: the layers its look hangs, the fronts a wind change sends and
/// what stirs it. Cold on area load, stepped once a frame on the game thread.</summary>
internal static class AirScene
{
    private static AirSceneState _s = new();

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

    /// <summary>Take <paramref name="look"/> for the area just laid, by night or by day.</summary>
    internal static void Enter(AirLook look, bool night)
    {
        var s = new AirSceneState { Look = look };
        _s = s;
        if (!GroundMist.Corner(out s.Ox, out s.Oy, out s.Feet)) return;
        s.Laid = true;
        bool dust = (look.Layers(night) & AirLayer.Dust) != 0;
        for (int y = 0; y < GroundMist.Side; y++)
            for (int x = 0; x < GroundMist.Side; x++)
            {
                int c = y * GroundMist.Side + x;
                float f = GroundMist.FloorAt(s.Ox + x + 0.5f, s.Oy + y + 0.5f);
                s.Floor[c] = f;
                s.Rest[c] = s.Dust[c] = dust && !float.IsNaN(f) ? BaseDust * Air.Dust.V : 0f;
            }
    }

    /// <summary>Day turns to night or back.</summary>
    internal static void Night(bool night) { }

    /// <summary>A tree trunk standing at <paramref name="at"/>.</summary>
    internal static void Trunk(Vector2 at) { }

    /// <summary>A lamp at <paramref name="at"/> reaching <paramref name="reach"/> metres.</summary>
    internal static void Lamp(Vector3 at, float reach) { }

    /// <summary>A blast at <paramref name="at"/> of <paramref name="radius"/> metres.</summary>
    internal static void Blast(Vector3 at, float radius) { }

    /// <summary>Advance the air <paramref name="dt"/> seconds under the area's <paramref name="wind"/>; at Low no front starts, and with the air off nothing moves.</summary>
    internal static void Step(float dt, Vector2 wind)
    {
        AirSceneState s = _s;
        if (!s.Laid) return;
        if (!Air.Enabled.V)
        {
            s.Stepped = false;
            s.Front = default;
            return;
        }
        float speed = wind.Length();
        if (s.Stepped && Air.Quality.V != Air.Levels[0] && MathF.Abs(speed - s.Speed) > Calm) Launch(s, speed - s.Speed, speed > 0f ? wind / speed : s.Dir);
        if (!s.Stepped) Settle(s, speed);
        s.Stepped = true;
        s.Speed = speed;
        if (speed > 0f) s.Dir = wind / speed;
        s.Drift = wind * DriftShare;
        Sweep(s, dt);
        float fall = MathF.Exp(-dt / LiftTau), back = Refill * dt, off = Scour * speed * dt;
        for (int c = 0; c < s.Lift.Length; c++)
        {
            s.Lift[c] *= fall;
            s.Dust[c] += back * (s.Rest[c] - s.Dust[c]) - off * s.Dust[c];
        }
    }

    /// <summary>Lay each column's dust as a steady <paramref name="speed"/> leaves it, where scour and refill balance.</summary>
    private static void Settle(AirSceneState s, float speed)
    {
        float keep = Refill / (Refill + Scour * speed);
        for (int c = 0; c < s.Dust.Length; c++) s.Dust[c] = s.Rest[c] * keep;
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

    /// <summary>How much of <paramref name="layer"/> hangs at <paramref name="at"/>.</summary>
    internal static float Density(AirLayer layer, Vector3 at)
    {
        AirSceneState s = _s;
        int c = Column(s, at.X, at.Y);
        if (c < 0 || layer != AirLayer.Dust) return 0f;
        float h = RestHeight + s.Lift[c], z = at.Z - s.Floor[c];
        return z < 0f || z >= h ? 0f : s.Dust[c] / h;
    }

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
    internal static void Reset() => _s = new AirSceneState();
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

    /// <summary>Per column, row-major from the corner: its floor (NaN for none), the dust it holds at rest, its dust now
    /// and how high a front threw it.</summary>
    public readonly float[] Floor = new float[GroundMist.Side * GroundMist.Side];
    public readonly float[] Rest = new float[GroundMist.Side * GroundMist.Side];
    public readonly float[] Dust = new float[GroundMist.Side * GroundMist.Side];
    public readonly float[] Lift = new float[GroundMist.Side * GroundMist.Side];

    /// <summary>The wind's speed and heading the last step, and whether there was one.</summary>
    public float Speed;
    public Vector2 Dir = Vector2.UnitX;
    public bool Stepped;

    /// <summary>How the air drifts over the floor, metres a second.</summary>
    public Vector2 Drift;

    /// <summary>The front crossing the air, and where its line stands along it from the player's feet, metres.</summary>
    public AirFront Front;
    public float Line;
}
