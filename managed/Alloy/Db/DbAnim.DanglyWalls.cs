using System;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Alloy.Physics;

namespace CX.Alloy.Db;

/// <summary>The static faces a rigged Swing part keeps clear of (docs/spec/dangly-motions.md, section 5b), read
/// from what the scene draws: every tile and placeable mesh at its placed pose, never a creature, anything worn
/// or held, a door or an effect. A part gathers them once it has held its pose, and drops them when it moves.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Seconds a part holds its pose before its surroundings are gathered, and between gathers, so a
    /// placeable that arrives or goes after it is seen.</summary>
    private const double WallHold = 0.5, WallRefresh = 10.0;

    /// <summary>Gather time (seconds) one budget window may spend before no further gather starts in it, and the
    /// window: an area's first frames gather for every hanging part in it at once.</summary>
    private const double WallBudget = 0.0005, WallWindow = 1.0 / 60.0;

    /// <summary>A part pose within this of last frame's, per matrix element, has not moved.</summary>
    private const float WallPoseSlack = 1e-4f;

    /// <summary>Faces one mesh part may offer a gather: past it the part's mesh query is cut short and counted.</summary>
    internal const int WallMeshFaces = 4096;

    /// <summary>One Swing part's faces and the schedule they are gathered on: the part's pose last frame, the
    /// timestamp the next gather is due at, and whether a gather has filled them for the pose it holds.</summary>
    private sealed class DanglyWall
    {
        internal readonly DanglyColliders Faces = new();
        internal Matrix4x4 Seen;
        internal long Due;
        internal bool Ready;
    }

    /// <summary>The faces <paramref name="part"/> keeps clear of this frame, or null while it has none: collision
    /// off, a part that is not Swing or is in a GUI scene, one that moved since last frame or that has not yet
    /// been gathered for.</summary>
    private static DanglyColliders? DanglyWalls(int part, DanglyRig rig, MeshDef mesh, in Matrix4x4 world, bool on)
    {
        DanglyState d = _dangly;
        DbId obj = SceneDb.PartObj[part];
        DbId scene = on && rig.Class == DanglyClass.Swing && SceneDb.Objects.Valid(obj) ? SceneDb.ObjScene[obj.Index] : default;
        if (!SceneDb.Scenes.Valid(scene) || !SceneDb.SceneIsWorld[scene.Index])
        {
            d.Walls[part] = null;
            return null;
        }
        long now = Stopwatch.GetTimestamp();
        DanglyWall? w = d.Walls[part];
        if (w == null)
        {
            d.Walls[part] = new DanglyWall { Seen = world, Due = now + WallTicks(WallHold) };
            return null;
        }
        if (!WallPoseHeld(w.Seen, world))
        {
            w.Seen = world;
            w.Ready = false;
            w.Due = now + WallTicks(WallHold);
            return null;
        }
        if (now >= w.Due && WallBudgetLeft(now)) GatherWalls(part, rig, mesh, world, scene, w, now);
        if (!w.Ready) return null;
        d.WallParts++;
        return w.Faces;
    }

    private static long WallTicks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static bool WallPoseHeld(in Matrix4x4 a, in Matrix4x4 b)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (MathF.Abs(a[r, c] - b[r, c]) > WallPoseSlack) return false;
        return true;
    }

    /// <summary>Is there gather time left in this window? The window opens on the first ask after it closed.</summary>
    private static bool WallBudgetLeft(long now)
    {
        DanglyState d = _dangly;
        if (now - d.WallWindowAt >= WallTicks(WallWindow)) { d.WallWindowAt = now; d.WallSpent = 0; }
        return d.WallSpent < WallTicks(WallBudget);
    }

    /// <summary>Gather <paramref name="w"/> for <paramref name="part"/> at <paramref name="world"/>: every collider
    /// object of its scene whose sphere reaches the part's reach offers its faces there.</summary>
    private static void GatherWalls(int part, DanglyRig rig, MeshDef mesh, in Matrix4x4 world, DbId scene,
                                    DanglyWall w, long now)
    {
        DanglyState d = _dangly;
        DanglyGather g = d.Gather;
        // ponytail: one member-list walk per gather; a spatial index once gathers run in the thousands a second
        DbList? members = g.Begin(rig, world, mesh.Positions) ? SceneDb.SceneObjects[scene.Index] : null;
        for (int k = 0; members != null && k < members.Count; k++)
        {
            DbId id = members[k];
            if (!SceneDb.Objects.Valid(id) || !WallSource(id.Index)) continue;
            int o = id.Index;
            Vector4 sph = SceneDb.ObjSphere[o];
            if (!(sph.W > 0f) || !SphereTouches(sph, g.Lo, g.Hi)) continue;
            int first = SceneDb.ObjPartFirst[o];
            for (int p = first; p < first + SceneDb.ObjPartCount[o]; p++)
                if (p != part) WallFaces(g, p, d.WallScratch);
        }
        g.Finish(w.Faces);
        w.Ready = true;
        w.Due = now + WallTicks(WallRefresh);
        long spent = Stopwatch.GetTimestamp() - now;
        d.WallSpent += spent;
        d.GatherTicks += spent;
        d.Gathers++;
        d.GatheredFaces += w.Faces.Faces;
        d.WallDropped += g.Dropped;
    }

    /// <summary>Is object <paramref name="o"/> geometry a hanging part can rest against: a visible model that
    /// stays where it was placed -- not a creature or anything worn or held, not riding another object, not a
    /// door or an effect.</summary>
    private static bool WallSource(int o)
    {
        if (SceneDb.ObjKindOf[o] != ObjKind.Model || !SceneDb.ObjVisible[o]) return false;
        if ((SceneDb.ObjFlags[o] & ObjFlag.Creature) != 0 || !SceneDb.ObjAttachTo[o].IsNull) return false;
        byte cls = SceneDb.ObjModel[o]?.Classification ?? 0;
        return (cls & (HitTest.ClassEffect | HitTest.ClassDoor)) == 0;
    }

    private static bool SphereTouches(Vector4 s, Vector3 lo, Vector3 hi)
    {
        var c = new Vector3(s.X, s.Y, s.Z);
        Vector3 d = Vector3.Max(Vector3.Zero, Vector3.Max(lo - c, c - hi));
        return Vector3.Dot(d, d) <= s.W * s.W;
    }

    /// <summary>Offer the gather the faces of part <paramref name="p"/>'s drawn mesh in its reach, in world space
    /// and wound front-face counter-clockwise.</summary>
    private static void WallFaces(DanglyGather g, int p, int[] scratch)
    {
        if (!SceneDb.PartEnabled[p] || SceneDb.PartMerged[p] || SceneDb.PartKindOf[p] != PartKind.Mesh) return;
        MeshDef? m = SceneDb.PartMeshDef[p];
        if (m == null || !m.Render || m.Faces.Length == 0) return;
        Matrix4x4 w = SceneDb.PartWorld[p];
        if (!Matrix4x4.Invert(w, out Matrix4x4 inv)) return;
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            Vector3 v = Vector3.Transform(new Vector3((c & 1) == 0 ? g.Lo.X : g.Hi.X, (c & 2) == 0 ? g.Lo.Y : g.Hi.Y,
                                                      (c & 4) == 0 ? g.Lo.Z : g.Hi.Z), inv);
            lo = Vector3.Min(lo, v);
            hi = Vector3.Max(hi, v);
        }
        int n = MeshBvh.Overlap(m, lo, hi, scratch, out bool full);
        if (full) _dangly.WallTruncated++;
        bool mirrored = w.GetDeterminant() < 0f;
        int nv = m.Positions.Length;
        for (int i = 0; i < n; i++)
        {
            ref FaceDef f = ref m.Faces[scratch[i]];
            if ((uint)f.V0 >= (uint)nv || (uint)f.V1 >= (uint)nv || (uint)f.V2 >= (uint)nv) continue;
            Vector3 a = Vector3.Transform(m.Positions[f.V0], w), b = Vector3.Transform(m.Positions[f.V1], w),
                    c = Vector3.Transform(m.Positions[f.V2], w);
            if (mirrored) g.Offer(a, c, b);
            else g.Offer(a, b, c);
        }
    }

    /// <summary>The period's collision clause, empty when no part gathered or kept faces.</summary>
    private static string DanglyWallPeriod(int frames)
    {
        DanglyState d = _dangly;
        if (d.Gathers == 0 && d.WallParts == 0) return "";
        double ms = d.GatherTicks * 1000.0 / Stopwatch.Frequency;
        string s = $" | walls: {(frames > 0 ? d.WallParts / frames : 0)} part(s)/frame held clear, {d.Gathers} "
                 + $"gather(s) {ms:F2} ms, {(d.Gathers > 0 ? d.GatheredFaces / d.Gathers : 0)} face(s) each";
        if (d.WallDropped != 0) s += $", {d.WallDropped} face(s) or pair(s) past a cap";
        if (d.WallTruncated != 0) s += $", {d.WallTruncated} mesh query(s) TRUNCATED";
        d.WallParts = d.Gathers = d.GatheredFaces = d.WallDropped = d.WallTruncated = 0;
        d.GatherTicks = 0;
        return s;
    }
}
