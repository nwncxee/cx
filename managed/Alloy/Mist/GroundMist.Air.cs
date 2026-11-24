using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The air over the ground mist: a velocity per column that walkers and blasts stir, that coasts, curls and
/// dies away, and carries the mist and the height it was thrown up to along with it.</summary>
internal static partial class GroundMist
{
    /// <summary>The longest air step, in seconds, and the most of a column one step carries across one edge.</summary>
    private const float AirStep = 0.05f, MaxCourant = 0.2f;

    /// <summary>The air's drag: seconds a slow eddy takes to die, and the extra drag per metre a second of speed,
    /// so a blast's gust dies in a second while a walker's eddies linger for a few.</summary>
    private const float AirTau = 3f, AirQuad = 0.4f;

    /// <summary>The fastest the air moves, in metres a second, and the speed under which it is still.</summary>
    private const float MaxAir = 10f, AirRest = 0.005f;

    /// <summary>Air slower than this, in metres a second, swirls the mist without hauling it, so a walker's eddies stir
    /// what they pushed aside in place while a blast's gust rolls its ring outward.</summary>
    private const float Grip = 1.2f;

    /// <summary>A blast's gust at its rim, in metres a second, and how far out it blows, in blast radii.</summary>
    private const float BlastSpeed = 8f, BlastWind = 2f;

    /// <summary>How uneven a blast is around its point: a fraction of its gust and of the mist it throws.</summary>
    private const float Ragged = 0.6f;

    /// <summary>How tall a blast throws the mist it lands, as a share of its depth, the seconds that height takes to
    /// fall back, and the most of it a column holds.</summary>
    private const float LiftGain = 1.2f, LiftTau = 3f, LiftCap = 2f;

    /// <summary>A walker's bow wave, in metres a second at its feet, its two trailing eddies' speed and core radius,
    /// and how fast a step stirs the air toward them.</summary>
    private const float BowSpeed = 2.6f, Swirl = 2.4f, SwirlCore = 1.2f, Stir = 0.6f;

    /// <summary>How far, in metres, a walker's bow wave reaches the air around its feet.</summary>
    internal const float BowReach = 3.5f;

    /// <summary>Where a walker's trailing eddies sit: metres behind its feet, and either side of its path.</summary>
    private const float SwirlBack = 0.9f, SwirlSide = 1.2f;

    /// <summary>A walker who has stepped <paramref name="take"/> of a stride toward <paramref name="ahead"/> from
    /// <paramref name="feet"/> stirs the air it reached: a bow wave out ahead and beside, a pair of eddies behind.</summary>
    private static void StirWalk(GroundMistState s, Vector3 feet, Vector2 ahead, float take)
    {
        float len = ahead.Length();
        if (len <= 0f) return;
        Vector2 f = ahead / len, p = new(-f.Y, f.X), at = new(feet.X, feet.Y);
        Vector2 left = at - f * SwirlBack + p * SwirlSide, right = at - f * SwirlBack - p * SwirlSide;
        float blend = MathF.Min(take * Stir, 1f);
        for (int i = 0; i < s.Reached; i++)
        {
            int c = s.Queue[i];
            var d = new Vector2(s.Ox + c % Side + 0.5f, s.Oy + c / Side + 0.5f) - at;
            float r = d.Length();
            if (r > BowReach) continue;
            Vector2 t = Vector2.Zero;
            float along = Vector2.Dot(d, f);
            if (along > -0.5f && r > 0f) t += d / r * (BowSpeed * (1f - r / BowReach));
            if (along < 0f) t += Eddy(d + at - left, 1f) + Eddy(d + at - right, -1f);
            Vector2 v = new(s.Vx[c], s.Vy[c]);
            if (t.LengthSquared() <= v.LengthSquared()) continue;
            v += (t - v) * blend;
            s.Vx[c] = v.X;
            s.Vy[c] = v.Y;
            Wake(s, c);
        }
    }

    /// <summary>The air an eddy turning <paramref name="sign"/> moves at <paramref name="rel"/> from its core.</summary>
    private static Vector2 Eddy(Vector2 rel, float sign)
        => new Vector2(-rel.Y, rel.X) * (sign * Swirl * 2f * SwirlCore / (rel.LengthSquared() + SwirlCore * SwirlCore));

    /// <summary>A blast of <paramref name="radius"/> at <paramref name="at"/> gusts the air it reached outward, unevenly
    /// by <paramref name="lobes"/>, and throws the mist it landed up.</summary>
    private static void Gust(GroundMistState s, Vector3 at, float radius, in Lobes lobes)
    {
        float rim = radius + BlastRim, reach = radius * BlastWind;
        for (int i = 0; i < s.Reached; i++)
        {
            int c = s.Queue[i];
            float dx = s.Ox + c % Side + 0.5f - at.X, dy = s.Oy + c / Side + 0.5f - at.Y, r = MathF.Sqrt(dx * dx + dy * dy);
            if (r <= 0f || r > reach) continue;
            float n = lobes.Of(MathF.Atan2(dy, dx));
            float gust = BlastSpeed * (1f + Ragged * n) * (r <= rim ? r / rim : (reach - r) / (reach - rim));
            s.Vx[c] = Math.Clamp(s.Vx[c] + dx / r * gust, -MaxAir, MaxAir);
            s.Vy[c] = Math.Clamp(s.Vy[c] + dy / r * gust, -MaxAir, MaxAir);
            if (r > radius && r <= rim)
            {
                s.Lift[c] = MathF.Min(s.Lift[c] + s.Depth[c] * LiftGain * (1f + Ragged * n), s.Depth[c] * LiftCap);
                s.Lifted |= s.Lift[c] > 0f;
            }
            Wake(s, c);
        }
    }

    /// <summary>Column <paramref name="c"/> moves: the air's rect grows to hold it.</summary>
    private static void Wake(GroundMistState s, int c)
    {
        int x = c % Side, y = c / Side;
        if (!s.Air) { s.Ax0 = s.Ax1 = x; s.Ay0 = s.Ay1 = y; s.Air = true; return; }
        s.Ax0 = Math.Min(s.Ax0, x); s.Ax1 = Math.Max(s.Ax1, x);
        s.Ay0 = Math.Min(s.Ay0, y); s.Ay1 = Math.Max(s.Ay1, y);
    }

    /// <summary><paramref name="dt"/> seconds of the air, in steps short enough to carry no column further than
    /// <see cref="MaxCourant"/> of an edge: it coasts and turns, drags to a stop, and carries the mist. True if any moved.</summary>
    private static bool Air(GroundMistState s, float dt)
    {
        if (!s.Air) return LiftFalls(s, dt);
        int n = (int)MathF.Ceiling(MathF.Max(dt / AirStep, s.AirTop * dt / MaxCourant));
        n = Math.Clamp(n, 1, 200);
        float h = dt / n;
        for (int k = 0; k < n && s.Air; k++)
        {
            Carry(s, h);
            Coast(s, h);
        }
        LiftFalls(s, dt);
        return true;
    }

    /// <summary>The thrown-up height falls back over <see cref="LiftTau"/>; true while any stands.</summary>
    private static bool LiftFalls(GroundMistState s, float dt)
    {
        if (!s.Lifted) return false;
        float keep = MathF.Exp(-dt / LiftTau);
        bool any = false;
        for (int c = 0; c < s.Lift.Length; c++)
        {
            float q = MathF.Min(s.Lift[c] * keep, s.Depth[c] * LiftCap);
            if (q < Trace) q = 0f;
            s.Lift[c] = q;
            any |= q > 0f;
        }
        s.Lifted = any;
        return true;
    }

    /// <summary>One step of the air carrying the mist and its lift across each linked edge, by the edge's mean speed
    /// from the upwind column. Nothing crosses the field's edge, a wall or a step, so the mass is kept.</summary>
    private static void Carry(GroundMistState s, float h)
    {
        int x0 = Math.Max(s.Ax0 - 1, 0), x1 = Math.Min(s.Ax1 + 1, Side - 1), y0 = Math.Max(s.Ay0 - 1, 0), y1 = Math.Min(s.Ay1 + 1, Side - 1);
        float[] dd = s.Spare, dq = s.Target;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int c = y * Side + x;
                dd[c] = 0f;
                dq[c] = 0f;
            }
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int c = y * Side + x;
                if (float.IsNaN(s.Floor[c])) continue;
                if (x < x1) Edge(s, c, c + 1, Hauls((s.Vx[c] + s.Vx[c + 1]) * 0.5f) * h, dd, dq);
                if (y < y1) Edge(s, c, c + Side, Hauls((s.Vy[c] + s.Vy[c + Side]) * 0.5f) * h, dd, dq);
            }
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int c = y * Side + x;
                s.Depth[c] = MathF.Max(s.Depth[c] + dd[c], 0f);
                s.Lift[c] = MathF.Max(s.Lift[c] + dq[c], 0f);
            }
    }

    /// <summary>The speed at which air of <paramref name="v"/> across an edge hauls the mist: what it has past <see cref="Grip"/>.</summary>
    private static float Hauls(float v) => v > Grip ? v - Grip : v < -Grip ? v + Grip : 0f;

    /// <summary>The mist and lift <paramref name="u"/> of an edge carries from <paramref name="a"/> toward <paramref name="b"/>
    /// (negative: back), into <paramref name="dd"/> and <paramref name="dq"/>; none across a wall or a step.</summary>
    private static void Edge(GroundMistState s, int a, int b, float u, float[] dd, float[] dq)
    {
        if (u == 0f || !Linked(s, a, b)) return;
        u = Math.Clamp(u, -MaxCourant, MaxCourant);
        int from = u > 0f ? a : b;
        float m = MathF.Abs(u) * s.Depth[from], q = MathF.Abs(u) * s.Lift[from];
        float sign = u > 0f ? 1f : -1f;
        dd[a] -= sign * m; dd[b] += sign * m;
        dq[a] -= sign * q; dq[b] += sign * q;
    }

    /// <summary>One step of the air carrying itself, back along its own motion from neighbours linked to each column,
    /// then dragging, then the rect shrinking to the columns still moving.</summary>
    private static void Coast(GroundMistState s, float h)
    {
        int x0 = s.Ax0, x1 = s.Ax1, y0 = s.Ay0, y1 = s.Ay1;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int c = y * Side + x;
                float vx = s.Vx[c], vy = s.Vy[c];
                if (float.IsNaN(s.Floor[c]) || (vx == 0f && vy == 0f)) { s.Nx[c] = 0f; s.Ny[c] = 0f; continue; }
                float px = x - vx * h, py = y - vy * h;
                int ix = (int)MathF.Floor(px), iy = (int)MathF.Floor(py);
                float fx = px - ix, fy = py - iy;
                Vector2 got = Sample(s, c, ix, iy) * ((1f - fx) * (1f - fy)) + Sample(s, c, ix + 1, iy) * (fx * (1f - fy))
                            + Sample(s, c, ix, iy + 1) * ((1f - fx) * fy) + Sample(s, c, ix + 1, iy + 1) * (fx * fy);
                float drag = 1f / (1f + h * (1f / AirTau + AirQuad * got.Length()));
                s.Nx[c] = got.X * drag;
                s.Ny[c] = got.Y * drag;
            }
        bool any = false;
        float top = 0f;
        int nx0 = Side, nx1 = -1, ny0 = Side, ny1 = -1;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int c = y * Side + x;
                float vx = s.Nx[c], vy = s.Ny[c], sp = MathF.Sqrt(vx * vx + vy * vy);
                if (sp < AirRest) { vx = 0f; vy = 0f; }
                else
                {
                    any = true;
                    top = MathF.Max(top, sp);
                    nx0 = Math.Min(nx0, x); nx1 = Math.Max(nx1, x); ny0 = Math.Min(ny0, y); ny1 = Math.Max(ny1, y);
                }
                s.Vx[c] = vx;
                s.Vy[c] = vy;
            }
        s.Air = any;
        s.AirTop = top;
        if (any) { s.Ax0 = nx0; s.Ax1 = nx1; s.Ay0 = ny0; s.Ay1 = ny1; }
    }

    /// <summary>The air at column (<paramref name="x"/>, <paramref name="y"/>) as column <paramref name="c"/> reads it:
    /// still past the field, over no floor, or behind a wall or a step from <paramref name="c"/>.</summary>
    private static Vector2 Sample(GroundMistState s, int c, int x, int y)
    {
        if ((uint)x >= Side || (uint)y >= Side) return Vector2.Zero;
        int at = y * Side + x;
        return at == c || Linked(s, c, at) ? new Vector2(s.Vx[at], s.Vy[at]) : Vector2.Zero;
    }

    /// <summary>A blast's unevenness around its point: three lobes whose phases its position seeds.</summary>
    private readonly struct Lobes
    {
        private readonly float _a, _b, _c;

        public Lobes(Vector3 at)
        {
            uint h = (uint)(int)MathF.Floor(at.X * 4f) * 73856093u ^ (uint)(int)MathF.Floor(at.Y * 4f) * 19349663u ^ 0x9E3779B9u;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            _a = (h & 0x3FF) / 1024f * MathF.Tau;
            _b = ((h >> 10) & 0x3FF) / 1024f * MathF.Tau;
            _c = ((h >> 20) & 0x3FF) / 1024f * MathF.Tau;
        }

        /// <summary>The unevenness toward <paramref name="angle"/>, from -1 to 1.</summary>
        public float Of(float angle)
            => (MathF.Sin(3f * angle + _a) + 0.7f * MathF.Sin(5f * angle + _b) + 0.5f * MathF.Sin(2f * angle + _c)) / 2.2f;
    }
}
