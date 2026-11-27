using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The night mist an outdoor area with water gathers: thick over the water and creeping a few metres onto the
/// land, gathering at dusk and thinning at dawn, carried and torn by the wind and parted by walkers like the dust.</summary>
internal static partial class AirScene
{
    /// <summary>Mist a water column holds at rest by night, metres of full density.</summary>
    private const float BaseMist = 0.3f;

    /// <summary>How tall the mist stands over its floor, metres: past a standing character's waist.</summary>
    internal const float MistTop = 1.2f;

    /// <summary>The share of an emitter's held mist shown: its full depth a little thinner to the eye than the night's own
    /// lay, since it pools in hollows an eye looks down into.</summary>
    private const float PlacedShare = 0.25f;

    /// <summary>How fast the mist thins away from the water, metres, and the farthest onto land it creeps, metres.</summary>
    private const float Creep = 2f, CreepReach = 6f;

    /// <summary>How long the mist takes to gather once night falls, and to thin once day breaks, seconds.</summary>
    private const float DuskTau = 60f;

    /// <summary>How near night or day the night's share comes before it is taken as there.</summary>
    private const float Dawned = 1e-3f;

    /// <summary>The diagonal step of the distance to the water, columns.</summary>
    private static readonly float Diagonal = MathF.Sqrt(2f);

    /// <summary>Lay the mist each column holds at rest by night: <see cref="BaseMist"/> over the water at the area's hold,
    /// thinning over <see cref="Creep"/> to nothing past <see cref="CreepReach"/> onto the land.</summary>
    private static void LayMist(AirSceneState s)
    {
        LayMistRest(s);
        Array.Copy(s.MistRest, s.Mist, s.MistRest.Length);
    }

    /// <summary>The mist each column holds at rest by night, as <see cref="LayMist"/> lays it, leaving what it holds now.</summary>
    private static void LayMistRest(AirSceneState s)
    {
        const int n = GroundMist.Side;
        float[] d = s.Spare;
        for (int c = 0; c < d.Length; c++)
            d[c] = GroundMist.WetAt(s.Ox + c % n + 0.5f, s.Oy + c / n + 0.5f) ? 0f : float.PositiveInfinity;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++) Reach(s, d, x, y, -1);
        for (int y = n - 1; y >= 0; y--)
            for (int x = n - 1; x >= 0; x--) Reach(s, d, x, y, 1);
        float lay = BaseMist * s.Hold;
        for (int c = 0; c < d.Length; c++)
            s.MistRest[c] = d[c] <= CreepReach && !float.IsNaN(s.Floor[c]) ? lay * MathF.Exp(-d[c] / Creep) : 0f;
    }

    /// <summary>One chamfer step of the distance to the water at column (<paramref name="x"/>, <paramref name="y"/>),
    /// from the neighbours already passed on side <paramref name="side"/>; the distance runs over floors only.</summary>
    private static void Reach(AirSceneState s, float[] d, int x, int y, int side)
    {
        const int n = GroundMist.Side;
        int c = y * n + x;
        if (float.IsNaN(s.Floor[c])) return;
        float best = d[c];
        best = MathF.Min(best, Near(d, x + side, y) + 1f);
        best = MathF.Min(best, Near(d, x, y + side) + 1f);
        best = MathF.Min(best, Near(d, x + side, y + side) + Diagonal);
        best = MathF.Min(best, Near(d, x - side, y + side) + Diagonal);
        d[c] = best;
    }

    /// <summary>The distance held at column (<paramref name="x"/>, <paramref name="y"/>), infinity off the grid.</summary>
    private static float Near(float[] d, int x, int y)
        => (uint)x < GroundMist.Side && (uint)y < GroundMist.Side ? d[y * GroundMist.Side + x] : float.PositiveInfinity;

    /// <summary>Ease the night's share of the mist toward night or day by <paramref name="dt"/>.</summary>
    private static void Dusk(AirSceneState s, float dt)
    {
        if (!s.Wets) return;
        float was = s.Dusk, to = s.Night ? 1f : 0f;
        s.Dusk = MathF.Abs(to - was) < Dawned ? to : was + (to - was) * (1f - MathF.Exp(-dt / DuskTau));
        if (s.Dusk != was) s.Gen++;
    }

    /// <summary>Carry the mist <paramref name="dt"/> seconds along the air's drift, each column taking what lay upwind of
    /// it; a column with no floor holds none. Hot: allocates nothing.</summary>
    private static void Carry(AirSceneState s, float dt)
    {
        Vector2 by = s.Drift * dt;
        if (!s.Wets || by == Vector2.Zero) return;
        const int n = GroundMist.Side;
        float[] from = s.Mist, to = s.Spare;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int c = y * n + x;
                if (float.IsNaN(s.Floor[c])) { to[c] = 0f; continue; }
                float sx = x - by.X, sy = y - by.Y;
                int x0 = (int)MathF.Floor(sx), y0 = (int)MathF.Floor(sy);
                float fx = sx - x0, fy = sy - y0;
                float low = Held(s, from, x0, y0) + fx * (Held(s, from, x0 + 1, y0) - Held(s, from, x0, y0));
                float high = Held(s, from, x0, y0 + 1) + fx * (Held(s, from, x0 + 1, y0 + 1) - Held(s, from, x0, y0 + 1));
                to[c] = low + fy * (high - low);
            }
        (s.Mist, s.Spare) = (to, from);
    }

    /// <summary>The mist column (<paramref name="x"/>, <paramref name="y"/>) holds, none off the grid or off the floor.</summary>
    private static float Held(AirSceneState s, float[] mist, int x, int y)
    {
        if ((uint)x >= GroundMist.Side || (uint)y >= GroundMist.Side) return 0f;
        int c = y * GroundMist.Side + x;
        return float.IsNaN(s.Floor[c]) ? 0f : mist[c];
    }

    /// <summary>The mist shown on column <paramref name="c"/> at world (<paramref name="x"/>, <paramref name="y"/>), metres of
    /// full density: the night's share of what lies, less what passers cleared, and its share of what an emitter laid there,
    /// no more than an emitter's own disc holds however deep a hollow pools it.</summary>
    private static float MistShown(AirSceneState s, int c, float x, float y)
        => s.Dusk * s.Mist[c] * (1f - s.Gap[c]) + PlacedShare * MathF.Min(GroundMist.Laid(x, y), GroundMist.SourceDepth);

    /// <summary>The mist shown on the column holding world (<paramref name="x"/>, <paramref name="y"/>), metres of full
    /// density; 0 off the air's floor or with the air off.</summary>
    internal static float MistAt(float x, float y)
    {
        AirSceneState s = _s;
        int c = Column(s, x, y);
        return c < 0 || !On(s) ? 0f : MistShown(s, c, MathF.Floor(x) + 0.5f, MathF.Floor(y) + 0.5f);
    }

    /// <summary>Does any mist show: night mist gathered over the area's water, or mist an emitter laid?</summary>
    private static bool Misty(AirSceneState s) => s.Wets && s.Dusk > 0f || GroundMist.Placed;

    /// <summary>Day turns to night or back: the night mist gathers or thins over the next minutes, and a meadow's
    /// pollen stops at nightfall and floats again by day.</summary>
    internal static void Night(bool night)
    {
        AirSceneState s = _s;
        if (s.Night == night) return;
        s.Night = night;
        s.Gen++;
        if (!s.Laid) return;
        if ((s.Look.Layers(night) & AirLayer.Pollen) == 0) s.Pollen.Seeded = false;
        else if (!s.Pollen.Seeded && GroundMist.Corner(out _, out _, out Vector2 feet)) SeedPollen(s, feet);
    }
}
