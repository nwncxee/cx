using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Services.Camera;

namespace CX.Alloy;

/// <summary>One frame's occluder hits: what each ray of the fan landed on. Allocated once by its owner and
/// refilled every frame.</summary>
internal sealed class OccludeHits
{
    /// <summary>The database handle of each recorded hit's object.</summary>
    public readonly uint[] Obj = new uint[OccludeFan.MaxHits];
    /// <summary>How many entries of <see cref="Obj"/> are live this frame.</summary>
    public int N;
}

/// <summary>The occluder fade's pure logic over the database: the ray fan, one part's eased verdict, and the fold
/// into the part's drawn alpha. Allocation-free.</summary>
internal static class OccludeFan
{
    public const int Rays = 5, HitsPerRay = 3, MaxHits = Rays * HitsPerRay;
    /// <summary>The fade's ease rate per second (a knob would only ever be set to this).</summary>
    public const float Rate = 8f;
    /// <summary>Seconds a part counts as hit after its last ray hit: a ray grazing an edge flips hit/miss
    /// frame to frame, and without the hold the screen-door re-thresholds every frame (flicker).</summary>
    public const float Hold = 0.25f;

    /// <summary>Cast the fan from <paramref name="focus"/> to <paramref name="cam"/> in <paramref name="scene"/>,
    /// skipping <paramref name="ignore"/>, and record each placeable hit in <paramref name="hits"/>. Returns the
    /// casts made.</summary>
    public static int Cast(DbId scene, Vector3 focus, Vector3 cam, DbId ignore, OccludeHits hits)
    {
        hits.N = 0;
        if (!Walls.Fade.V) return 0;
        Vector3 d = cam - focus;
        float len = d.Length();
        if (len < 0.05f) return 0;
        d /= len;
        Vector3 right = Vector3.Cross(d, Vector3.UnitZ);
        if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX; else right = Vector3.Normalize(right);
        Vector3 up = Vector3.Cross(right, d);
        float radius = Walls.Radius.V;
        int casts = 0;
        for (int r = 0; r < Rays; r++)
        {
            Vector3 o = r switch
            {
                1 => right * radius, 2 => -right * radius, 3 => up * radius, 4 => -up * radius, _ => Vector3.Zero,
            };
            Vector3 from = focus + o, to = cam + o;
            for (int h = 0; h < HitsPerRay; h++)
            {
                casts++;
                if (!SceneRay.Cast(scene, from, to, ignore, out Vector3 hit, out DbId obj)) break;
                if (SceneRay.IsPlaceable(Facade.ObjectShell(obj))) hits.Obj[hits.N++] = obj.Value;
                from = hit + d * SceneRay.Nudge;
                if (Vector3.Dot(to - from, d) <= 0f) break;
            }
        }
        return casts;
    }

    /// <summary>Ease one part's fade toward "hit this frame" and return it (0 = solid). <paramref name="part"/> is
    /// its <see cref="SceneDb"/> part index, -1 for none; <paramref name="held"/> is its hold timer.</summary>
    public static float Verdict(ref float fade, ref float held, int part, bool lit, OccludeHits hits, float dt)
    {
        float f = fade;
        bool hit = false;
        if (hits.N > 0 && lit)
        {
            uint obj = part >= 0 ? SceneDb.PartObj[part].Value : 0u;
            if (obj != 0)
                for (int n = 0; n < hits.N && !hit; n++) hit = hits.Obj[n] == obj;
        }
        if (hit) held = Hold;
        else if (held > 0f) { held -= dt; hit = true; }
        if (!hit && f == 0f) return 0f;
        float target = hit ? 1f - Walls.Floor.V : 0f;
        f += (target - f) * Math.Clamp(dt * Rate, 0f, 1f);
        if (MathF.Abs(target - f) < 0.005f) f = target;
        fade = f;
        return f;
    }

    /// <summary>A part's drawn alpha: its own alpha, its tile's roof and fog fade, and its occluder factor
    /// <paramref name="occ"/>; <paramref name="dissolved"/> says the keyhole is cutting it.</summary>
    public static float Fold(float own, float tile, float occ, bool dissolved) => own * tile * (1f - occ);

    /// <summary>The caster admission's fading test: a part fading by its tile's ramp casts no shadow.
    /// <paramref name="occ"/> is the part's occluder fade.</summary>
    public static bool CasterFading(float tile, float occ) => tile < 1f;
}
