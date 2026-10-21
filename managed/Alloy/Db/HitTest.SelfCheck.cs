using System;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The off-device checks: the hit test's gates, its ordering, its <c>CHitInfo</c> fill, the spatial
/// index's change-driven maintenance, and the pick cost over a synthetic area. Engine-free and GL-free, so
/// <c>run-demo.sh</c> runs all of it in the stub host.</summary>
public static unsafe partial class HitTestCheck
{
    /// <summary>Objects in the micro-benchmark's synthetic area, and rays cast at it. A few thousand objects
    /// is the heaviest measured area's order (ADR 0028 sizes the tables at ~3k gobs).</summary>
    internal const int BenchObjects = 3000, BenchRays = 2000;

    /// <summary>Microseconds per cast the benchmark measured, for the live comparison against the engine's
    /// own (<c>HitProbe</c>). Zero until <see cref="Run"/> has run.</summary>
    public static double BenchUsPerPick { get; private set; }

    /// <summary>Refuse once the SEAM is running. The fixture allocates records in the real tables, so a live
    /// session is the danger; a scene record is the discriminator, since the seam creates one per
    /// <c>NewCAurScene</c> and the game has none until it starts. It deliberately does NOT require the tables
    /// to be empty -- <see cref="Fixture.Tear"/> frees exactly the records it made and nothing else.</summary>
    private static bool Empty(ExtenderLog log, string what)
    {
        if (SceneDb.Scenes.Live == 0) return true;
        log.Always($"HitTest.{what}: REFUSED -- the seam is live ({SceneDb.Scenes.Live} scene(s), "
                 + $"{SceneDb.Objects.Live} object(s)) and the fixture writes to the real tables. "
                 + "Run it at boot, or /alloy hitbench before a scene exists.");
        return false;
    }

    /// <summary><c>/alloy hitbench</c>: re-measure the cast now. Deferred to the next period line rather
    /// than run from the chat command's own thread, so the measurement is not taken from inside whatever
    /// the command handler is holding.</summary>
    public static void BenchArm() => _benchArmed = true;
    private static bool _benchArmed;

    /// <summary>Called once per period by <see cref="HitTestService"/>: runs an armed benchmark.</summary>
    internal static void Pump(ExtenderLog log)
    {
        if (!_benchArmed) return;
        _benchArmed = false;
        Bench(log);
    }

    public static void Run(ExtenderLog log)
    {
        bool ok = true;
        if (!Empty(log, "SelfCheck")) return;
        ok &= Primitives(log);
        ok &= GuiPick(log);
        ok &= Grid(log);
        ok &= Recycle(log);
        ok &= ReMark(log);
        ok &= Scene(log);
        ok &= MergedHead(log);
        ok &= Bench(log);
        log.Info(ok ? "HitTest.SelfCheck: passed" : "HitTest.SelfCheck: see failures above");
    }

    private static bool Fail(ExtenderLog log, string what)
    { log.Info("HitTest.SelfCheck: FAILED -- " + what); return false; }

    /// <summary>The geometry primitives, against the engine's own semantics: a one-sided triangle, a box face
    /// code with its normal, the inside-the-box -1, and the sphere reject.</summary>
    private static bool Primitives(ExtenderLog log)
    {
        bool ok = true;
        // A triangle in the z=0 plane, wound so its normal is +z: a segment coming down hits it, the same
        // segment coming UP does not. One-sidedness is polyhit's, and it is what keeps a pick off far walls.
        Vector3 a = new(-1, -1, 0), b = new(1, -1, 0), c = new(0, 1, 0);
        Vector3 to = new(0, 0, -1);
        if (!HitTest.PolyHit(a, b, c, new Vector3(0, 0, 1), ref to)) ok = Fail(log, "polyhit missed a front-facing triangle");
        else if (MathF.Abs(to.Z) > 1e-5f) ok = Fail(log, $"polyhit point off the plane ({to.Z})");
        Vector3 up = new(0, 0, 1);
        if (HitTest.PolyHit(a, b, c, new Vector3(0, 0, -1), ref up)) ok = Fail(log, "polyhit hit a BACK face");
        // Outside the triangle but on its plane: the edge test must reject.
        Vector3 miss = new(5, 5, -1);
        if (HitTest.PolyHit(a, b, c, new Vector3(5, 5, 1), ref miss)) ok = Fail(log, "polyhit hit outside the edges");

        // The nearest of two parallel triangles wins, because the segment is shortened at the first.
        Vector3 seg = new(0, 0, -5);
        HitTest.PolyHit(a, b, c, new Vector3(0, 0, 5), ref seg);
        Vector3 far0 = new(a.X, a.Y, -2), far1 = new(b.X, b.Y, -2), far2 = new(c.X, c.Y, -2);
        if (HitTest.PolyHit(far0, far1, far2, new Vector3(0, 0, 5), ref seg))
            ok = Fail(log, "a farther triangle took the hit from the nearer one");

        // BoxIntersect's face code: +x face is code 2 (axis 0, sign +1) with normal +x.
        Vector3 bto = new(0, 0, 0);
        int code = HitTest.BoxFace(new Vector3(5, 0, 0), ref bto, new Vector3(-1), new Vector3(1), out Vector3 n);
        if (code != 2 || n != new Vector3(1, 0, 0)) ok = Fail(log, $"box face code {code} normal {n} (want 2, +x)");
        if (MathF.Abs(bto.X - 1f) > 1e-5f) ok = Fail(log, $"box entry point {bto}");
        // Inside the box is -1 and does not shorten the segment.
        Vector3 ito = new(5, 0, 0);
        if (HitTest.BoxFace(Vector3.Zero, ref ito, new Vector3(-1), new Vector3(1), out _) != -1 || ito.X != 5f)
            ok = Fail(log, "a segment starting inside the box was not -1");
        if (HitTest.BoxHit(new Vector3(5, 5, 5), new Vector3(6, 6, 6), new Vector3(-1), new Vector3(1)))
            ok = Fail(log, "BoxHit accepted a segment that misses the box");
        if (!HitTest.SphereHit(new Vector3(-5, 0, 0), new Vector3(5, 0, 0), Vector3.Zero, 1f))
            ok = Fail(log, "SphereHit missed a segment through the centre");
        if (HitTest.SphereHit(new Vector3(-5, 9, 0), new Vector3(5, 9, 0), Vector3.Zero, 1f))
            ok = Fail(log, "SphereHit accepted a segment 9m away");
        return ok;
    }

    /// <summary>A GUI panel's pick, end to end: an ORTHO camera, three control quads laid out at known pixels,
    /// and a pick at each one's own pixel. Without the ortho arm, <c>MouseDir</c>'s <c>tan(0)</c> collapses
    /// every pixel onto the camera's axis and every pick misses or answers the same control.</summary>
    private static bool GuiPick(ExtenderLog log)
    {
        bool ok = true;
        const float Vw = 1024f, Vh = 768f;
        var w = Fixture.BuildGui(Vw, Vh);

        // The ortho mapping is one hundredth of a pixel per world unit, so a control the fixture placed for
        // pixel (px, py) must be hit by a pick at (px, py) -- and only by that one.
        foreach ((int px, int py, DbId want, string name) in Fixture.GuiTargets)
        {
            if (!DbCamera.PickSegment(w.Cam, px, py, 0f, 0f, Vw, Vh, default, out Vector3 a, out Vector3 b))
            { ok = Fail(log, $"the ortho pick refused the pixel ({px},{py}) inside the panel's rect"); continue; }
            // The segment must look down world -Z and move with the pixel: the perspective arm's bug was
            // precisely that it did neither.
            if (MathF.Abs(a.X - b.X) > 1e-5f || MathF.Abs(a.Y - b.Y) > 1e-5f || b.Z >= a.Z)
            { ok = Fail(log, $"the ortho pick at ({px},{py}) is not a world -Z segment ({a} -> {b})"); continue; }
            var q = HitQuery.Pick(w.Scene, a, b);
            q.SurfaceMask = HitQuery.All;
            if (!HitTest.Cast(q, out HitResult r)) ok = Fail(log, $"the pick at ({px},{py}) missed {name}");
            else if (r.Obj != want) ok = Fail(log, $"the pick at ({px},{py}) answered the wrong control, not {name}");
        }

        ok &= TextRow(log, w, Vw, Vh);

        // Coplanar controls tie on depth, and Scene::HitCheckGob @0x45f860 keeps the LAST member that hits: a
        // portrait joined after the frame it sits on answers a pick on it, and the frame answers around it.
        DbId frame = Fixture.GuiExtra(w.Scene, "Frame", 400, 680, 300, 100);
        DbId portrait = Fixture.GuiExtra(w.Scene, "Portrait", 400, 680, 80, 24);
        DbId PickGui(int px, int py)
        {
            if (!DbCamera.PickSegment(w.Cam, px, py, 0f, 0f, Vw, Vh, default, out Vector3 pa, out Vector3 pb)) return default;
            var gq = HitQuery.Pick(w.Scene, pa, pb);
            gq.SurfaceMask = HitQuery.All;
            return HitTest.Cast(gq, out HitResult gr) ? gr.Obj : default;
        }
        if (PickGui(400, 680) != portrait) ok = Fail(log, "a pick on a control coplanar with its panel frame did not answer the control, the later member");
        if (PickGui(500, 680) != frame) ok = Fail(log, "a pick on a panel frame beside its control did not answer the frame");

        // Two different pixels must give two different segments -- the one-line statement of the bug.
        DbCamera.PickSegment(w.Cam, 100, 100, 0f, 0f, Vw, Vh, default, out Vector3 p0, out _);
        DbCamera.PickSegment(w.Cam, 700, 600, 0f, 0f, Vw, Vh, default, out Vector3 p1, out _);
        if (MathF.Abs(p0.X - p1.X) < 1e-4f || MathF.Abs(p0.Y - p1.Y) < 1e-4f)
            ok = Fail(log, "two different pixels produced the same ortho pick position (the tan(0) collapse)");

        // A pixel outside the camera's rect is the engine's own answer of 0, not a cast.
        if (DbCamera.PickSegment(w.Cam, (int)Vw + 10, 10, 0f, 0f, Vw, Vh, default, out _, out _))
            ok = Fail(log, "a pixel to the right of the panel's rect was not refused");
        if (DbCamera.PickSegment(w.Cam, 10, -5, 0f, 0f, Vw, Vh, default, out _, out _))
            ok = Fail(log, "a pixel above the panel's rect was not refused");

        // ...and a PERSPECTIVE camera still takes the other arm: the segment starts at the near plane and
        // the direction changes with the pixel.
        SceneDb.CamViewAngle[w.Cam.Index] = 60f;
        DbCamera.PickSegment(w.Cam, 100, 100, 0f, 0f, Vw, Vh, default, out Vector3 e0, out Vector3 f0);
        DbCamera.PickSegment(w.Cam, 700, 600, 0f, 0f, Vw, Vh, default, out _, out Vector3 f1);
        if ((f0 - e0).Length() < 1e-3f || (f0 - f1).Length() < 1e-3f)
            ok = Fail(log, "the perspective arm stopped varying with the pixel");

        Fixture.Tear();
        return ok;
    }

    /// <summary>A GUI list ROW: a part-less text gob whose only bound is the quad
    /// <c>StringGob::HitCheck</c> @0x492920 tests. Both halves are the check -- the pick must MISS while the
    /// document has no box (as the engine answers) and must hit once the game sets one, with nothing between
    /// but the document change, because the game sets a row's box only after the row exists.</summary>
    private static bool TextRow(ExtenderLog log, Fixture.GuiWorld w, float vw, float vh)
    {
        bool ok = true;
        const int rx = 400, ry = 560, rw = 80, rh = 24;
        const float k = 0.01f;   // not-an-offset: the fixture's world units per panel pixel
        DbId row = TextObjects.Create(w.Scene, 1);
        if (row.IsNull) return Fail(log, "the text record for the list-row pick could not be made");
        int ri = row.Index;
        SceneDb.ObjPos[ri] = new Vector3((rx - rw * 0.5f) * k, (ry + rh * 0.5f) * k, 0f);
        SceneDb.ObjRot[ri] = Quaternion.Identity;
        SceneDb.ObjScale[ri] = 1f;
        SceneDb.ObjWorld[ri] = Matrix4x4.CreateTranslation(SceneDb.ObjPos[ri]);
        HitBounds.Refresh(ri);

        DbCamera.PickSegment(w.Cam, rx, ry, 0f, 0f, vw, vh, default, out Vector3 a, out Vector3 b);
        var q = HitQuery.Pick(w.Scene, a, b);
        q.SurfaceMask = HitQuery.All;
        if (HitTest.Cast(q, out HitResult r) && r.Obj == row)
            ok = Fail(log, "a text gob whose document has no box was pickable");

        TextSlots.SetMaxWidth(row, rw * k);
        TextSlots.SetMaxHeight(row, rh * k);
        HitBounds.Refresh(ri);
        if (!HitTest.Cast(q, out r)) ok = Fail(log, "the pick on a list row missed it entirely");
        else if (r.Obj != row) ok = Fail(log, "the pick on a list row answered something else -- the row is not in the index");
        else if (r.Part != -1) ok = Fail(log, "a text gob's hit named a part; it has none");

        // ...and ONLY on its own quad: two rows a row-height apart must not answer for each other, which is
        // exactly what the user saw (a click selecting a different row).
        if (DbCamera.PickSegment(w.Cam, rx, ry - rh * 2, 0f, 0f, vw, vh, default, out a, out b))
        {
            var above = HitQuery.Pick(w.Scene, a, b);
            above.SurfaceMask = HitQuery.All;
            if (HitTest.Cast(above, out HitResult ar) && ar.Obj == row)
                ok = Fail(log, "a text gob answered a pick outside its own quad");
        }

        // A model riding line 0, as a chat line's reply button does: in no scene, so only the line walk reaches it.
        // Exit's quad (centred on pixel 400,440 in its own frame) is carried to (rx + rw/2, ry), half over the row,
        // and lifted 0.1 toward the camera -- where BUT_CHAT_REPLY's Portrait node sits, in front of the text.
        DbId rider = w.Exit;
        HitGrid.Remove(rider.Index);
        SceneDb.ObjScene[rider.Index] = default;
        int rp = SceneDb.ObjPartFirst[rider.Index];
        for (int p = rp; p < rp + SceneDb.ObjPartCount[rider.Index]; p++)
            SceneDb.PartWorld[p] = Matrix4x4.CreateTranslation(0f, 0f, 0.1f);
        TextDoc? doc = TextObjects.Of(row);
        if (doc == null) ok = Fail(log, "the list row has no document to hang a line model on");
        else
        {
            float ax = (rx + rw / 2 - 400) * k - SceneDb.ObjPos[ri].X, ay = (ry - 440) * k - SceneDb.ObjPos[ri].Y;
            doc.PublishedQuads = new QuadList { Attached = [(rider, ax, ay)], AttachedCount = 1 };
            DbId PickAt(int px)
            {
                if (!DbCamera.PickSegment(w.Cam, px, ry, 0f, 0f, vw, vh, default, out Vector3 pa, out Vector3 pb))
                    return default;
                var lq = HitQuery.Pick(w.Scene, pa, pb);
                lq.SurfaceMask = HitQuery.All;
                return HitTest.Cast(lq, out HitResult lr) ? lr.Obj : default;
            }
            if (PickAt(rx + rw / 4) != rider)
                ok = Fail(log, "a pick on the model riding a list row's line did not answer that model");
            if (PickAt(rx - rw / 4) != row)
                ok = Fail(log, "a pick on a row with a line model, off the model, did not answer the row");
            if (PickAt(rx + rw * 3 / 4) == rider)
                ok = Fail(log, "a line model answered a pick outside its row's quad -- the engine never casts there");
            doc.ResetQuads();
        }

        HitGrid.Remove(ri);
        TextObjects.Destroy(row);
        return ok;
    }

    /// <summary>The spatial index: filing, the segment query, the change-driven re-file (and the same-cell
    /// case that must NOT re-file), and the unfile. It allocates its three objects through the table and
    /// counts only its OWN ids among a query's candidates, because other streams' self-checks run first and
    /// leave records behind.</summary>
    private static bool Grid(ExtenderLog log)
    {
        bool ok = true;
        var got = new int[64];
        var mine = new int[3];
        for (int k = 0; k < 3; k++)
        {
            DbId id = SceneDb.Objects.Alloc();
            if (id.IsNull) return Fail(log, "the object table had no room for the grid check");
            mine[k] = id.Index;
        }
        int a = mine[0], b = mine[1], c = mine[2];


        int[] g = got;
        int Mine(Vector3 from, Vector3 to)
        {
            int n = HitGrid.Query(from, to, g), seen = 0;
            for (int i = 0; i < n; i++)
                if (g[i] == a || g[i] == b || g[i] == c) seen++;
            return seen;
        }

        SceneDb.HitObjBMin[a] = new Vector3(0, 0, 0); SceneDb.HitObjBMax[a] = new Vector3(1, 1, 1);
        SceneDb.HitObjBMin[b] = new Vector3(100, 0, 0); SceneDb.HitObjBMax[b] = new Vector3(101, 1, 1);
        HitGrid.File(a); HitGrid.File(b);
        if (Mine(new Vector3(-5, 0.5f, 0.5f), new Vector3(5, 0.5f, 0.5f)) != 1)
            ok = Fail(log, "the grid query did not return exactly the near object");
        // The segment that reaches both cells must return both; the DDA walks the cells between them.
        if (Mine(new Vector3(-5, 0.5f, 0.5f), new Vector3(200, 0.5f, 0.5f)) != 2)
            ok = Fail(log, "the grid query along the row did not return both objects");

        // Change-driven: a move inside the same cell must not re-file, a move out of it must.
        long moved = HitGrid.Refiles, same = HitGrid.RefilesSkipped;
        SceneDb.HitObjBMin[a] = new Vector3(2, 2, 0); SceneDb.HitObjBMax[a] = new Vector3(3, 3, 1);
        HitGrid.File(a);
        if (HitGrid.RefilesSkipped != same + 1 || HitGrid.Refiles != moved)
            ok = Fail(log, "a same-cell move re-filed the grid");
        SceneDb.HitObjBMin[a] = new Vector3(50, 50, 0); SceneDb.HitObjBMax[a] = new Vector3(51, 51, 1);
        HitGrid.File(a);
        if (HitGrid.Refiles != moved + 1) ok = Fail(log, "a move across cells did not re-file the grid");
        if (Mine(new Vector3(-5, 0.5f, 0.5f), new Vector3(5, 0.5f, 0.5f)) != 0)
            ok = Fail(log, "the grid still answers for the object's old cell");
        if (Mine(new Vector3(50.5f, 45, 0.5f), new Vector3(50.5f, 55, 0.5f)) != 1)
            ok = Fail(log, "the grid does not answer for the object's new cell");

        HitGrid.Remove(a);
        if (Mine(new Vector3(50.5f, 45, 0.5f), new Vector3(50.5f, 55, 0.5f)) != 0)
            ok = Fail(log, "an unfiled object still answers");

        // An object wider than the per-object cell budget goes on the oversize list and is always visited.
        int over = HitGrid.Oversize;
        SceneDb.HitObjBMin[c] = new Vector3(-500, -500, 0); SceneDb.HitObjBMax[c] = new Vector3(500, 500, 1);
        HitGrid.File(c);
        if (HitGrid.Oversize != over + 1) ok = Fail(log, "a wide object was not put on the oversize list");
        if (Mine(new Vector3(9000, 9000, 0), new Vector3(9001, 9001, 1)) != 1)
            ok = Fail(log, "the oversize list was not visited");

        for (int k = 0; k < 3; k++)
        {
            HitGrid.Remove(mine[k]);
            SceneDb.HitObjBMin[mine[k]] = new Vector3(1f); SceneDb.HitObjBMax[mine[k]] = new Vector3(-1f);
            SceneDb.Objects.Free(SceneDb.Objects.IdAt(mine[k]));
        }
        return ok;
    }

    /// <summary>The RECYCLED slot: one dirty entry carrying both a destroy and a create must leave the NEW
    /// tenant in the index. A free and an alloc between two <c>TakeDirty</c> points OR into one entry, and an
    /// area change does exactly that to every tile -- taking the destroy and skipping the rest unfiles the new
    /// area's FLOOR, which never moves again, so nothing ever marks it for a re-file.</summary>
    private static bool Recycle(ExtenderLog log)
    {
        bool ok = true;
        var got = new int[64];
        var world = Fixture.Build();
        var at = new Vector3(400f, 400f, 0f);
        DbId dead = Fixture.Floor(world.Scene, at);
        int slot = dead.Index;

        // The area edge: the old floor dies and a new one takes the SAME slot, with no fold between them.
        SceneDb.Objects.TakeDirty();
        SceneDb.Parts.TakeDirty();
        Fixture.Kill(dead);
        DbId live = Fixture.Floor(world.Scene, at);
        if (live.Index != slot)
        {
            Fixture.Tear();
            return Fail(log, "the object table did not hand the freed slot straight back -- the recycle case "
                           + "cannot be set up (the free list is not LIFO any more)");
        }
        if ((SceneDb.Objects.DirtyAt(slot) & (Dirty.Destroyed | Dirty.Created)) != (Dirty.Destroyed | Dirty.Created))
            ok = Fail(log, "a free plus an alloc did not leave BOTH marks on the slot -- the hazard this case "
                         + "covers no longer exists, and the case is testing nothing");

        // The fold, then the floor probe ComputeZValue issues: a vertical segment straight down.
        long recycled = HitFeed.Recycled;
        HitFeed.Fold();
        if (HitFeed.Recycled != recycled + 1)
            ok = Fail(log, "the fold did not report the slot as a hand-over");
        int n = HitGrid.Query(at + new Vector3(0, 0, 1f), at - new Vector3(0, 0, 1f), got);
        bool filed = false;
        for (int i = 0; i < n; i++) if (got[i] == slot) filed = true;
        if (!filed) ok = Fail(log, "the fold UNFILED the recycled slot's new tenant -- the index does not "
                                 + "hand back a record the database holds");
        var q = HitQuery.Ray(world.Scene, at + new Vector3(0, 0, 1f), at - new Vector3(0, 0, 1f), statics: true);
        q.SurfaceMask = HitQuery.All;
        q.Ground = false;
        if (!HitTest.Cast(q, out HitResult r))
            ok = Fail(log, "a floor probe over the recycled floor found no ground (the Z=9 sentinel case)");
        else if (r.Obj != live)
            ok = Fail(log, $"the floor probe answered record {r.Obj.Value}, not the new tenant {live.Value}");

        Fixture.Tear();
        return ok;
    }

    /// <summary>The RE-MARK: a second mark on a record already on the dirty list, carrying a bit it already
    /// had and driving no compose. Nothing the fold compares could see it -- the bits do not change, the list
    /// does not grow, the compose count stands still -- so the stamp is <see cref="DbTable.MarkClock"/>. If
    /// this check fails, a re-marked record keeps a stale hit box: it is clicked where it WAS.</summary>
    private static bool ReMark(ExtenderLog log)
    {
        bool ok = true;
        var world = Fixture.Build();
        var at = new Vector3(600f, 600f, 0f);
        DbId o = Fixture.Floor(world.Scene, at);
        int i = o.Index;

        // Settled: one fold has seen this record, so its stamp is current.
        SceneDb.Objects.TakeDirty();
        SceneDb.Objects.Mark(i, Dirty.Xform);
        HitFeed.Fold();
        float z0 = SceneDb.HitObjBMax[i].Z;
        long refreshed = HitBounds.Refreshed;
        HitFeed.Fold();
        if (HitBounds.Refreshed != refreshed)
            ok = Fail(log, "a second fold over an unchanged entry refreshed it anyway -- the stamp is not "
                         + "skipping, and the whole still-frame saving is gone");

        // The re-mark: the world matrices move (as a compose would have left them), the SAME dirty bit is
        // re-OR'd, and nothing composes. Neither the bits, the list length nor the compose count moves.
        Dirty bits = SceneDb.Objects.DirtyAt(i);
        int takes = SceneDb.Objects.DirtyTakes, n = SceneDb.Objects.DirtyCount;
        int serial = DbAnim.ComposeCount[i];
        int first = SceneDb.ObjPartFirst[i];
        for (int p = first; p < first + SceneDb.ObjPartCount[i]; p++)
            SceneDb.PartWorld[p] *= Matrix4x4.CreateTranslation(0f, 0f, 10f);
        SceneDb.Objects.Mark(i, Dirty.Xform);
        if (SceneDb.Objects.DirtyAt(i) != bits || SceneDb.Objects.DirtyTakes != takes
            || SceneDb.Objects.DirtyCount != n || DbAnim.ComposeCount[i] != serial)
            ok = Fail(log, "the re-mark moved one of the signals it is supposed to be invisible to -- this "
                         + "case is testing nothing");

        HitFeed.Fold();
        if (MathF.Abs(SceneDb.HitObjBMax[i].Z - z0 - 10f) > 1e-3f)
            ok = Fail(log, $"the fold did not refresh a RE-MARKED record: its hit box tops out at "
                         + $"{SceneDb.HitObjBMax[i].Z} and the geometry at {z0 + 10f}");

        Fixture.Tear();
        return ok;
    }

    /// <summary>A floor cast onto a tile merge bucket's head lands on the mesh the part DRAWS. Oracle: the
    /// hand-written 0.01, the drawn height of <c>tin01_a07_02</c>'s rug read off the model file.</summary>
    private static bool MergedHead(ExtenderLog log)
    {
        bool ok = true;
        var world = Fixture.Build();
        var at = new Vector3(700f, 700f, 0f);
        Fixture.MergedHead(world.Scene, at);
        HitQuery q = HitQuery.Ray(world.Scene, at + new Vector3(0.5f, 0.5f, 0.5f), at + new Vector3(0.5f, 0.5f, -0.5f), statics: true);
        if (!HitTest.Cast(q, out HitResult r))
            ok = Fail(log, "a floor cast straight down onto a merged tile bucket hit nothing");
        else if (MathF.Abs(r.Point.Z - 0.01f) > 1e-3f)
            ok = Fail(log, $"a floor cast onto a merged tile bucket landed at z {r.Point.Z:F3}, the drawn rug is at "
                         + "0.010 -- the hit test is casting the node's own mesh under the bucket's zeroed pose");
        Fixture.Tear();
        return ok;
    }

    /// <summary>Build a synthetic scene in the real tables and cast at it: nearest-hit ordering across
    /// objects, the door-alpha gate, a static placeable, a trigger prism, a rotated and scaled object, and
    /// the CHitInfo fill.</summary>
    private static bool Scene(ExtenderLog log)
    {
        bool ok = true;
        var world = Fixture.Build();

        // Ordering: three boxes on the x axis, the cast from -x must take the nearest.
        var q = HitQuery.Pick(world.Scene, new Vector3(-50, 0, 0.5f), new Vector3(50, 0, 0.5f));
        if (!HitTest.Cast(q, out HitResult r)) ok = Fail(log, "the pick missed every box");
        else if (r.Obj != world.Near) ok = Fail(log, "the pick took a farther box than the nearest");
        else if (r.Kind != HitKind.Object) ok = Fail(log, $"the pick reported kind {r.Kind}, want Object");
        else if (r.Point.X > -9f) ok = Fail(log, $"the hit point {r.Point} is not on the near box's -x face");

        // ...and from +x it must take the other end, which is the same test run backwards.
        q.From = new Vector3(20, 0, 0.5f); q.To = new Vector3(-50, 0, 0.5f);
        if (!HitTest.Cast(q, out r) || r.Obj != world.Far) ok = Fail(log, "the reversed pick did not take the far box");

        // The door gate: a door faded to or below mindooralpha is not pickable, above it is. Each record
        // below owns its own x column, so the cast along y can only be answered by the one it aims at.
        q = HitQuery.Pick(world.Scene, new Vector3(30, -50, 0.5f), new Vector3(30, 50, 0.5f));
        SceneDb.HitObjFadeAlpha[world.Door.Index] = HitTest.MinDoorAlpha;
        if (HitTest.Cast(q, out r)) ok = Fail(log, "a faded-out door was still hit");
        SceneDb.HitObjFadeAlpha[world.Door.Index] = 1f;
        if (!HitTest.Cast(q, out r) || r.Obj != world.Door) ok = Fail(log, "an opaque door was not hit");
        // DB mode has no Gob+0x10c: DoorFade.StepDb is the ONE writer of the fade, and it must leave the
        // pick's column and the draw's PartFade agreeing -- a door faded out of the picture but not out of
        // the pick (or the reverse) is the bug that single writer exists to prevent.
        SceneDb.HitObjFadeAlpha[world.Door.Index] = 1f;
        for (int i = 0; i < 40; i++) DoorFade.StepDb(world.Door, 0.1f, false, HitTest.MinDoorAlpha);
        int dp = SceneDb.ObjPartFirst[world.Door.Index];
        if (SceneDb.HitObjFadeAlpha[world.Door.Index] != HitTest.MinDoorAlpha)
            ok = Fail(log, $"StepDb did not clamp the fade to mindooralpha ({SceneDb.HitObjFadeAlpha[world.Door.Index]})");
        if (SceneDb.PartFade[dp] != SceneDb.HitObjFadeAlpha[world.Door.Index])
            ok = Fail(log, "StepDb left PartFade and the pick's fade column disagreeing");
        if (HitTest.Cast(q, out r)) ok = Fail(log, "a door faded out by StepDb was still hit");
        for (int i = 0; i < 40; i++) DoorFade.StepDb(world.Door, 0.1f, true, HitTest.MinDoorAlpha);
        if (SceneDb.HitObjFadeAlpha[world.Door.Index] != 1f || SceneDb.PartFade[dp] != 1f)
            ok = Fail(log, "StepDb did not drive the fade back to opaque in both columns");
        if (!HitTest.Cast(q, out r) || r.Obj != world.Door) ok = Fail(log, "a door StepDb reopened was not hit");

        // ...and an object at zero alpha is out of the pick entirely (Scene::HitCheckGob's own gate).
        SceneDb.HitObjAlpha[world.Door.Index] = 0f;
        if (HitTest.Cast(q, out r)) ok = Fail(log, "a zero-alpha object was still hit");
        SceneDb.HitObjAlpha[world.Door.Index] = 1f;

        // A character is hit unless the caller said to skip characters.
        q = HitQuery.Pick(world.Scene, new Vector3(50, -50, 0.5f), new Vector3(50, 50, 0.5f));
        if (!HitTest.Cast(q, out r) || r.Obj != world.Creature) ok = Fail(log, "a creature was not hit");
        q.SkipCharacters = true;
        if (HitTest.Cast(q, out r)) ok = Fail(log, "a creature was hit with SkipCharacters set");

        // An effect-classified model is never hit, whatever its alpha.
        q = HitQuery.Pick(world.Scene, new Vector3(110, -50, 0.5f), new Vector3(110, 50, 0.5f));
        if (HitTest.Cast(q, out r)) ok = Fail(log, "an effect model was hit");

        // The static placeable: reachable with StaticPlaceables, invisible without -- and WITHOUT the engine's
        // global, which is the whole point of making it a per-cast flag.
        q = HitQuery.Pick(world.Scene, new Vector3(70, -50, 0.5f), new Vector3(70, 50, 0.5f));
        q.StaticPlaceables = false;
        if (HitTest.Cast(q, out r)) ok = Fail(log, "a static placeable was hit by a cast that did not ask for them");
        q.StaticPlaceables = true;
        if (!HitTest.Cast(q, out r)) ok = Fail(log, "a static placeable was not hit by a statics cast");
        else if (r.Obj != world.StaticProp) ok = Fail(log, "the statics cast hit the wrong record");
        else if (r.Kind != HitKind.Static) ok = Fail(log, $"a static hit reported kind {r.Kind}, want Static");

        // The trigger prism: only a cast whose surface mask carries the trigger bit sees it.
        q = HitQuery.Ray(world.Scene, new Vector3(90, -50, 0.5f), new Vector3(90, 50, 0.5f));
        if (HitTest.Cast(q, out r)) ok = Fail(log, "a trigger was hit by a cast whose mask clears bit 30");
        q.SurfaceMask = HitQuery.All;
        if (!HitTest.Cast(q, out r) || r.Obj != world.Trigger) ok = Fail(log, "a trigger was not hit with the trigger bit set");
        else
        {
            if (r.Surface != HitQuery.TriggerSurface) ok = Fail(log, $"the trigger hit reported surface {r.Surface}");
            if (r.Trigger != Fixture.TriggerPayload) ok = Fail(log, $"the trigger payload came back as 0x{r.Trigger:x}");
        }

        // A rotated + scaled object: the segment must enter its ROTATED face, so the world hit point and the
        // world normal are both answers only a correct per-part inverse can give. A 1m box scaled 3x and spun
        // 30 degrees about z, cast along x through its centre line: the entry is on the local -x face at
        // x = -1.5/cos(30) from the centre, and the normal is that face's, rotated.
        q = HitQuery.Pick(world.Scene, new Vector3(120, 0, 0.5f), new Vector3(180, 0, 0.5f));
        if (!HitTest.Cast(q, out r) || r.Obj != world.Spun) ok = Fail(log, "a rotated, scaled object was not hit");
        else
        {
            float want = 130f - 1.5f / MathF.Cos(MathF.PI / 6f);
            if (MathF.Abs(r.Point.X - want) > 0.01f) ok = Fail(log, $"the rotated hit landed at x={r.Point.X:F4}, want {want:F4}");
            if (MathF.Abs(r.Normal.Length() - 1f) > 1e-3f) ok = Fail(log, $"the rotated hit's normal is not unit ({r.Normal})");
            Vector3 wn = new(-MathF.Cos(MathF.PI / 6f), -MathF.Sin(MathF.PI / 6f), 0f);
            if ((r.Normal - wn).Length() > 0.01f) ok = Fail(log, $"the rotated hit's normal is {r.Normal}, want {wn}");
        }

        // Triangle mode on the near box: a face index, and NO normal -- the engine's PartTriMesh triangle arm
        // leaves CHitInfo+0x5c zero, which a caller must be able to tell apart from a real normal.
        q = HitQuery.Ray(world.Scene, new Vector3(-50, 0, 0.5f), new Vector3(-5, 0, 0.5f));
        if (!HitTest.Cast(q, out r)) ok = Fail(log, "triangle mode missed the near box");
        else
        {
            if (r.Obj != world.Near) ok = Fail(log, "triangle mode hit the wrong box");
            // PartTriMesh's triangle arm ClearOutputs and writes only the two points: ClearOutput's -1 face,
            // no surface bit and no normal are what the game reads back, so they are what CX must report.
            if (r.Face != -1) ok = Fail(log, $"a PartTriMesh triangle hit reported face {r.Face} (vanilla leaves -1)");
            if (r.Surface != -1) ok = Fail(log, $"a PartTriMesh triangle hit reported surface {r.Surface} (vanilla writes none)");
            if (r.Normal != Vector3.Zero) ok = Fail(log, "a PartTriMesh triangle hit returned a normal (vanilla leaves it zero)");
            if (r.LocalPoint == Vector3.Zero) ok = Fail(log, "a triangle hit left the part-space point at zero");
            if (MathF.Abs(r.Point.X + 10.5f) > 1e-3f) ok = Fail(log, $"the triangle hit point is {r.Point}, want x=-10.5");
        }

        // The ignore list, as Scene::HitCheckGob applies it: the nearest box skipped, the next one taken.
        q = HitQuery.Pick(world.Scene, new Vector3(-50, 0, 0.5f), new Vector3(50, 0, 0.5f));
        q.Ignore = new[] { world.Near }; q.IgnoreCount = 1;
        if (!HitTest.Cast(q, out r) || r.Obj != world.Mid) ok = Fail(log, "an ignored object was still hit");
        // The seam's form of the same list: a bitset over record indices, with no length ceiling (a mouse pick's
        // list holds every non-usable object in the area).
        q = HitQuery.Pick(world.Scene, new Vector3(-50, 0, 0.5f), new Vector3(50, 0, 0.5f));
        q.IgnoreBits = new ulong[(SceneDb.MaxObjects + 63) / 64];
        q.IgnoreBits[world.Near.Index >> 6] |= 1UL << (world.Near.Index & 63);
        if (!HitTest.Cast(q, out r) || r.Obj != world.Mid) ok = Fail(log, "an object on the ignore bitset was still hit");
        // An area transition (AurPartTriggerCreate's prism, DbTriggers in DB mode): the trigger bit admits it and the
        // hit carries its payload; without the bit the segment passes through.
        DbTriggers.AddForCheck(new[] { new Vector3(300, -1, 0.5f), new Vector3(302, -1, 0.5f), new Vector3(302, 1, 0.5f),
                                       new Vector3(300, -1, 0.5f), new Vector3(302, 1, 0.5f), new Vector3(300, 1, 0.5f) },
                               world.Scene, 0x7777);
        q = HitQuery.Ray(world.Scene, new Vector3(301, 0, 10f), new Vector3(301, 0, -10f));
        q.SurfaceMask = HitQuery.All;
        if (!HitTest.Cast(q, out r) || r.Trigger != 0x7777 || r.Surface != HitQuery.TriggerSurface || MathF.Abs(r.Point.Z - 0.5f) > 1e-3f)
            ok = Fail(log, "an area-transition prism was not hit with its payload");
        q.SurfaceMask = HitQuery.NoTriggers;
        if (HitTest.Cast(q, out r) && r.Trigger == 0x7777) ok = Fail(log, "a cast without the trigger bit hit a transition");
        // The scene's destruction takes its prisms and only its prisms: the highlight would read a freed one.
        DbTriggers.AddForCheck(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0) }, default, 0x8888);
        DbTriggers.SceneRevoke(world.Scene);
        if (DbTriggers.Count != 1) ok = Fail(log, $"a scene revoke left {DbTriggers.Count} prism(s), want 1 (the other scene's)");
        q.SurfaceMask = HitQuery.All;
        if (HitTest.Cast(q, out r) && r.Trigger == 0x7777) ok = Fail(log, "a revoked scene's transition was still hit");
        // The highlight reads the prism's copies only: the self-check entry's engine pointer is garbage, so an
        // enrolment that dereferenced it would fault here instead of counting.
        DbTriggers.Entry lit = DbTriggers.At(0);
        lit.Outline = new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(2, 2, 0) };
        lit.Highlighted = true; lit.Half = true; lit.Height = 1f; lit.Colour = new Vector4(1f, 1f, 1f, 0.5f);
        long enrolled = TriggerHighlight.Enrolled;
        TriggerHighlight.Enroll();
        if (TriggerHighlight.Enrolled != enrolled + 1) ok = Fail(log, "a lit prism was not enrolled from its copied state");
        lit.Colour.W = 0f;
        TriggerHighlight.Enroll();
        if (TriggerHighlight.Enrolled != enrolled + 1) ok = Fail(log, "a prism with alpha 0 was enrolled");
        DbTriggers.ClearForCheck();
        TriggerHighlight.Enroll();

        // A grafted body piece reports its creature: the game reads the hit object's external type off it.
        DbId nearUp = SceneDb.ObjAttachTo[world.Near.Index];
        SceneDb.ObjAttachTo[world.Near.Index] = world.Mid;
        SceneDb.ObjSeamFlags[world.Near.Index] |= (uint)SeamFlag.Composed;
        q = HitQuery.Pick(world.Scene, new Vector3(-50, 0, 0.5f), new Vector3(50, 0, 0.5f));
        if (!HitTest.Cast(q, out r) || r.Obj != world.Mid || MathF.Abs(r.Point.X + 10.5f) > 0.6f)
            ok = Fail(log, "a hit on a grafted piece did not report its creature at the piece's own point");
        SceneDb.ObjSeamFlags[world.Near.Index] &= ~(uint)SeamFlag.Composed;
        SceneDb.ObjAttachTo[world.Near.Index] = nearUp;

        // A miss leaves the record cleared, kind None -- never a stale hit from the cast before.
        q = HitQuery.Pick(world.Scene, new Vector3(-50, 500, 0.5f), new Vector3(50, 500, 0.5f));
        if (HitTest.Cast(q, out r) || r.Kind != HitKind.None || r.Face != -1)
            ok = Fail(log, "a miss did not come back cleared");

        // A scaled trigger: the engine's PartTrigger::HitCheckGeom never applies the accumulated scale, so a
        // prism on an object scaled 4x still covers only its own 2m extent. A cast aimed 3m off-centre must
        // miss it, and one aimed through the centre must hit.
        q = HitQuery.Ray(world.Scene, new Vector3(200, -50, 0.5f), new Vector3(200, 50, 0.5f));
        q.SurfaceMask = HitQuery.All;
        if (!HitTest.Cast(q, out r) || r.Obj != world.BigTrigger) ok = Fail(log, "a scaled trigger was not hit at all");
        q.From = new Vector3(203, -50, 0.5f); q.To = new Vector3(203, 50, 0.5f);
        if (HitTest.Cast(q, out r)) ok = Fail(log, "a scaled trigger grew with its object's scale (vanilla ignores it)");

        // g_HitCheckBoxExpansion widens a box pick, and the engine adds it AFTER scaling the box by the
        // accumulated scale -- so on the 3x object the box grows by the expansion itself, not by 3x it. The
        // 30-degree spin turns a 0,5 face offset into 0,5/cos(30) along the ray; applying the expansion
        // before the scale instead would move the hit by three times that, which is the discriminator.
        q = HitQuery.Pick(world.Scene, new Vector3(120, 0, 0.5f), new Vector3(180, 0, 0.5f));
        if (!HitTest.Cast(q, out r)) ok = Fail(log, "the expansion check's unexpanded pick missed");
        float bare = r.Point.X;
        q.BoxExpand = new Vector3(0.5f);
        float wantMove = 0.5f / MathF.Cos(MathF.PI / 6f);
        if (!HitTest.Cast(q, out r)) ok = Fail(log, "an expanded box pick missed");
        else if (MathF.Abs((bare - r.Point.X) - wantMove) > 0.02f)
            ok = Fail(log, $"the box expansion moved the hit by {bare - r.Point.X:F3}m, want {wantMove:F3} "
                         + "(it must be added after the scale, not before)");
        q.BoxExpand = default;

        // The walkmesh part: box-only must NOT apply to it. The engine's PartAABB::HitCheckGeom does not read
        // CHitInfo+0x20, and if CX let it, a pick whose camera stands inside the tile's walkmesh box would
        // collapse onto the camera and click-to-move would stop working -- so the box pick must land on the
        // walkmesh SURFACE, metres away, not at the ray origin.
        q = HitQuery.Pick(world.Scene, new Vector3(150, 0, 20f), new Vector3(150, 0, -20f));
        if (!HitTest.Cast(q, out r)) ok = Fail(log, "a box-only pick did not reach the walkmesh");
        else
        {
            if (r.Obj != world.Floor) ok = Fail(log, "the walkmesh pick hit the wrong record");
            if (r.Kind != HitKind.Static) ok = Fail(log, $"the walkmesh pick reported kind {r.Kind}");
            if (MathF.Abs(r.Point.Z) > 0.01f) ok = Fail(log, $"the walkmesh pick landed at z={r.Point.Z:F3}, want the surface at 0");
            if (r.Face < 0) ok = Fail(log, "a walkmesh hit came back with no face index (the AABB arm writes one)");
            if (r.Surface < 0) ok = Fail(log, "a walkmesh hit came back with no surface id");
            if (r.Normal == Vector3.Zero) ok = Fail(log, "a walkmesh hit came back with no normal (the AABB arm writes one)");
        }
        // ...and the walkmesh is gated by hitcheck_static_bsp_parts, not by the placeable flag.
        q.WalkMesh = false;
        if (HitTest.Cast(q, out r)) ok = Fail(log, "the walkmesh was hit with its own gate off");
        q.WalkMesh = true;
        // The surface mask DOES filter a walkmesh face (HitCheckAABBnode is the one arm that tests it).
        // Ground off explicitly: the z=0 plane arm answers any downward segment that crosses zero, so it
        // would mask whether the WALKMESH's own surface filter worked. (It is also what the ambient default
        // used to hide here.)
        q.SurfaceMask = 0;
        q.Ground = false;
        if (HitTest.Cast(q, out r))
            ok = Fail(log, $"a walkmesh face was hit with its surface masked out (part {r.Part}, "
                         + $"face {r.Face}, surface {r.Surface})");

        // ...and the z=0 ground plane, which is the last static arm: with it ON the same masked cast still
        // answers the plane, with Obj null and no face -- vanilla's own shape for that hit.
        q.Ground = true;
        if (!HitTest.Cast(q, out r)) ok = Fail(log, "the z=0 ground plane did not answer a downward segment");
        else if (!r.Obj.IsNull || r.Part != -1 || r.Face != -1 || MathF.Abs(r.Point.Z) > 1e-4f)
            ok = Fail(log, $"the ground-plane hit is not vanilla's shape (obj {r.Obj.Value}, part {r.Part}, at {r.Point})");
        q.Ground = false;

        // A disabled part prunes its subtree: the engine reaches a child only through its parent.
        q = HitQuery.Pick(world.Scene, new Vector3(-50, 0, 0.5f), new Vector3(50, 0, 0.5f));
        int np = SceneDb.ObjPartFirst[world.Near.Index];
        SceneDb.PartEnabled[np] = false;
        if (HitTest.Cast(q, out r) && r.Obj == world.Near) ok = Fail(log, "a disabled part was still hit");
        SceneDb.PartEnabled[np] = true;

        ok &= FillCheck(log, world);
        Fixture.Tear();
        return ok;
    }

    /// <summary>The CHitInfo fill: every output field at the engine's offset, with the engine's own
    /// conventions (the surface as a bit, the face and SafePointer index at -1 when absent, the local point
    /// left in part space).</summary>
    private static bool FillCheck(ExtenderLog log, Fixture.World world)
    {
        const byte Pattern = 0xCD;                                  // not-an-offset: the fill byte
        const int Pattern32 = unchecked((int)0xCDCDCDCD);           // not-an-offset: the fill byte, widened
        const long Pattern64 = unchecked((long)0xCDCDCDCDCDCDCDCD); // not-an-offset: the fill byte, widened

        bool ok = true;
        var q = HitQuery.Ray(world.Scene, new Vector3(70, -50, 0.5f), new Vector3(70, 50, 0.5f), statics: true);
        q.SurfaceMask = HitQuery.All;
        if (!HitTest.Cast(q, out HitResult r)) return Fail(log, "the fill check's cast missed");

        byte* info = stackalloc byte[EngineLayout.SizeOfHitInfo];
        for (int i = 0; i < EngineLayout.SizeOfHitInfo; i++) info[i] = Pattern;
        // The SafePointer pair is the ONE part of the record a fill is allowed to assume is initialised: the
        // engine's operator= reads the old pointer and unregisters it, so a record that has never been Reset
        // hands it garbage to dereference. Every real caller's record has been through Reset; this one has
        // to be, or the check itself is the crash. (LIVE-FOUND: the 0xCD fill below used to reach
        // SafePointer<CAurObject>::operator= and segfault the client during service start.)
        var h = new CHitInfo((nint)info);
        h.Gob = 0;
        h.SafeIndex = -1;
        HitTest.Fill(r, info);

        if (h.Kind != (int)r.Kind) ok = Fail(log, "CHitInfo kind");
        if (h.Point != r.Point) ok = Fail(log, "CHitInfo point");
        if (h.LocalPoint != r.LocalPoint) ok = Fail(log, "CHitInfo local point");
        if (h.Normal != r.Normal) ok = Fail(log, "CHitInfo normal");
        if (h.Face != r.Face) ok = Fail(log, "CHitInfo face");
        long surf = h.Surface;
        long wantSurf = r.Surface >= 0 ? 1L << r.Surface : 0L;
        if (surf != wantSurf) ok = Fail(log, $"CHitInfo surface 0x{surf:x}, want 0x{wantSurf:x}");
        // The object pointer goes in through the engine's SafePointer::operator= and NEVER by hand: the pair
        // (pointer, back-index) is what ClearOutput and ~CHitInfo walk, and an index of -1 beside a non-null
        // pointer indexes array[-1]. Off-device the symbol does not resolve, so the contract to check is that
        // the pair was left exactly as the caller had it and the refusal was counted.
        long unassigned = HitTest.UnassignedObjects;
        if (EngineFn.SafePointerObjectAssign == null)
        {
            if (unassigned == 0) ok = Fail(log, "an unresolvable SafePointer assignment was not counted");
            // Left exactly as the caller had it: null pointer, -1 index. Never a non-null pointer beside a
            // -1 index, which is the pair ClearOutput and ~CHitInfo index array[-1] with.
            if (h.Gob != 0 || h.SafeIndex != -1)
                ok = Fail(log, "an unresolvable assignment still wrote the SafePointer pair");
        }
        else if (h.Gob != 0)
            // A STATIC hit (this cast's) leaves the object null, exactly as Scene::HitCheck does -- and the
            // engine's own operator= is what put it there, so the back-index is its, not ours.
            ok = Fail(log, "a static hit reported an object in CHitInfo+0x48");
        if (h.Trigger != r.Trigger) ok = Fail(log, "CHitInfo trigger payload");
        // The input fields are the caller's: a fill must not touch them (the engine's does not), so each one
        // still reads back as the whole-record fill pattern.
        if (h.Mode != Pattern32 || h.BoxOnly != Pattern32 || h.RejectInside != Pattern32
            || h.IgnoreCount != Pattern32 || h.SurfaceMask != Pattern64 || h.IgnoreList != Pattern64
            || h.DoorAlpha != Pattern || h.SkipCharacters != Pattern)
            ok = Fail(log, "the fill overwrote an INPUT field of CHitInfo");
        // The offsets themselves: identical on both builds, and the record is 0x80 bytes.
        if (EngineLayout.HitInfoSurfaceMask != 0x08 || EngineLayout.HitInfoBoxOnly != 0x20
            || EngineLayout.HitInfoLocalPoint != 0x34 || EngineLayout.HitInfoSurface != 0x40
            || EngineLayout.HitInfoSafeIndex != 0x50 || EngineLayout.HitInfoFace != 0x58
            || EngineLayout.HitInfoNormal != 0x5c || EngineLayout.HitInfoPartName != 0x68
            || EngineLayout.HitInfoTrigger != 0x70 || EngineLayout.HitInfoRejectInside != 0x78
            || EngineLayout.HitInfoSkipCharacters != 0x7c || EngineLayout.HitInfoDoorAlpha != 0x7d)
            ok = Fail(log, "a CHitInfo offset row does not match the disassembly");
        return ok;
    }

    /// <summary>The micro-benchmark the ticket asks for: N rays against a synthetic area of a few thousand
    /// objects, with the grid's work and the brute-force cost of the same answer beside it. Reported, not
    /// asserted -- a number on a developer's box is a shape, not a budget.</summary>
    internal static bool Bench(ExtenderLog log)
    {
        if (!Empty(log, "Bench")) return false;
        var world = Fixture.BuildGrid(BenchObjects);

        // Triangle mode, so the per-mesh BVH is on the clock too: box-only (what a mouse pick actually uses)
        // is strictly cheaper, and the number that has to beat the engine is the expensive one.
        var q = HitQuery.Ray(world.Scene, Vector3.Zero, Vector3.One);
        q.SurfaceMask = HitQuery.All;
        long t0 = Stopwatch.GetTimestamp();
        int hits = 0;
        for (int i = 0; i < BenchRays; i++)
        {
            float a = i * 0.0163f;
            q.From = new Vector3(MathF.Cos(a) * 400f + 250f, MathF.Sin(a) * 400f + 250f, 0.5f);
            q.To = new Vector3(250f, 250f, 0.5f);
            if (HitTest.Cast(q, out _)) hits++;
        }
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        BenchUsPerPick = ms * 1000.0 / BenchRays;
        long cells = HitTest.CellVisits, objs = HitTest.ObjTests, parts = HitTest.PartTests;

        log.Info($"HitTest.Bench: {BenchRays} picks over {BenchObjects} objects in {ms:F2}ms "
               + $"({ms * 1000.0 / BenchRays:F1}us/pick, {hits} hits); per pick {cells / (double)BenchRays:F1} candidates, "
               + $"{objs / (double)BenchRays:F1} objects tested, {parts / (double)BenchRays:F1} parts; "
               + HitGrid.Stats() + $"; {MeshBvh.Built} mesh trees / {MeshBvh.BuiltFaces} faces");
        Fixture.Tear();
        // The index must actually narrow: a pick that tests every object is a grid that is not working.
        if (objs / (double)BenchRays > BenchObjects / 4.0)
            return Fail(log, $"a pick tested {objs / (double)BenchRays:F0} of {BenchObjects} objects -- the index did not narrow");
        return true;
    }
}
