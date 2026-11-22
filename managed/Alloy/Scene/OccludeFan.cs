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
    /// <summary>Each hit placeable's database handle, 0 where the entry is a part: a placeable fades whole.</summary>
    public readonly uint[] Obj = new uint[OccludeFan.MaxHits];
    /// <summary>Each hit tile or static part's <see cref="SceneDb"/> index (a tile's merged bucket head, the drawn
    /// slot's part), -1 where the entry is a placeable.</summary>
    public readonly int[] Part = new int[OccludeFan.MaxHits];
    /// <summary>How many entries are live this frame.</summary>
    public int N;
}

/// <summary>The occluder fade's pure logic over the database: the ray fan, one part's eased verdict, and the fold
/// into the part's drawn alpha. A placeable fades whole; a tile or static part fades unless the face hit is a
/// floor, ceiling or ground, which the ray passes through. Allocation-free.</summary>
internal static class OccludeFan
{
    public const int Rays = 5, HitsPerRay = 3, MaxHits = Rays * HitsPerRay;
    /// <summary>The fade's ease rate per second (a knob would only ever be set to this).</summary>
    public const float Rate = 10f;
    /// <summary>Seconds a part counts as hit after its last ray hit: a ray grazing an edge flips hit/miss
    /// frame to frame, and without the hold the fade re-eases every frame (flicker). Short enough that a part
    /// the camera turns away from is solid again within half a second.</summary>
    public const float Hold = 0.15f;
    /// <summary>A face whose normal is at least this close to vertical (45 degrees) is a floor, a ceiling or the
    /// ground, and never fades.</summary>
    public const float FloorCos = 0.7071f;

    /// <summary>Cast the fan from <paramref name="focus"/> to <paramref name="cam"/> in <paramref name="scene"/>,
    /// skipping <paramref name="ignore"/>, and record each placeable, tile or static hit in <paramref name="hits"/>.
    /// Returns the casts made.</summary>
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
                if (!SceneRay.Cast(scene, from, to, ignore, out HitResult hr, statics: true, faceNormal: true)) break;
                Record(hits, hr);
                from = hr.Point + d * SceneRay.Nudge;
                if (Vector3.Dot(to - from, d) <= 0f) break;
            }
        }
        return casts;
    }

    /// <summary>Add one hit to <paramref name="hits"/> when it is a part that fades: a placeable (whole), or a
    /// tile or static part whose hit face is not a floor. Creatures, doors and the ground plane never are.</summary>
    private static void Record(OccludeHits hits, in HitResult hr)
    {
        if (!hr.Obj.IsNull && SceneRay.IsPlaceable(Facade.ObjectShell(hr.Obj)))
        { hits.Obj[hits.N] = hr.Obj.Value; hits.Part[hits.N] = -1; hits.N++; return; }
        if (hr.Part < 0 || MathF.Abs(hr.Normal.Z) >= FloorCos) return;
        if (!hr.Obj.IsNull && (!SceneDb.Objects.Valid(hr.Obj) || !SceneDb.HitObjStatic[hr.Obj.Index])) return;
        hits.Obj[hits.N] = 0; hits.Part[hits.N] = hr.Part; hits.N++;
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
            if (part >= 0)
                for (int n = 0; n < hits.N && !hit; n++) hit = hits.Part[n] == part || (obj != 0 && hits.Obj[n] == obj);
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

    /// <summary>A part's drawn alpha: its own alpha times the fainter of its tile's roof and fog fade and its
    /// occluder factor <paramref name="occ"/>. A part the keyhole is cutting (<paramref name="dissolved"/>) draws
    /// as it does with the fade off: the keyhole already opens the view.</summary>
    public static float Fold(float own, float tile, float occ, bool dissolved)
        => dissolved ? own * tile : own * MathF.Min(tile, 1f - occ);

    /// <summary>The record's occluder cut for <paramref name="part"/>: the share of its record alpha (its own times its
    /// tile fade) the lit stage takes away inside the cylinder, so a faded fragment draws at <see cref="Fold"/>.
    /// 0 = no cut, which an unwritten lane reads as.</summary>
    public static float Cut(int part, float occ)
    {
        float tile = SceneDb.PartFade[part];
        if (occ <= 0f || tile <= 0f || SceneDb.PartDissolve[part] < 1f) return 0f;
        return 1f - MathF.Min(tile, 1f - occ) / tile;
    }

    /// <summary>The cylinder's radius around the camera ray the cut applies in: walls.radius, at least
    /// <see cref="CylinderMin"/> so a one-ray fan still opens a hole the character shows through.</summary>
    public static float Cylinder => MathF.Max(Walls.Radius.V, CylinderMin);

    /// <summary>The narrowest cylinder the cut applies in, in metres.</summary>
    public const float CylinderMin = 0.25f;

    /// <summary>The caster admission's fading test: a part fading by its tile's ramp casts no shadow.
    /// <paramref name="occ"/> is the part's occluder fade.</summary>
    public static bool CasterFading(float tile, float occ) => tile < 1f;
}
