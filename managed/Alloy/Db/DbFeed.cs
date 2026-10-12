using System;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Alloy's front end over <see cref="SceneDb"/>: the change-driven build of the renderer's records
/// from dirty parts and the visible set, so a still frame costs nothing but the visible-set query.
/// <see cref="RequestView"/> arrives from a camera's <c>RenderScene</c> slot, once per view, in the order the
/// game asked; <see cref="Present"/> runs the frame. Both on the GAME thread (ADR 0028 section 5).</summary>
public static partial class DbFeed
{
    /// <summary>Views one frame may ask for: the world's, the GUI panels' 3D views, and a probe capture's
    /// faces. A frame with more than this many is a bug rather than a busy frame, so the surplus is counted
    /// and refused loudly instead of growing the array.</summary>
    internal const int MaxViews = 64;

    private static readonly DbId[] _viewScene = new DbId[MaxViews];
    private static readonly DbId[] _viewCam = new DbId[MaxViews];
    private static int _viewN, _viewRefused;
    private static int _frames, _lastRecWrites;

    /// <summary>Chart offers one frame's fold may make: each takes the arena lock and allocates a job's
    /// arrays, and an area's whole tile set would otherwise go in one fold. Under the loading screen nothing
    /// is drawn for the copies to hitch, and at 8 a frame the drip WAS the warm load (2.2 s for 2,143 cache hits).</summary>
    private static int DbChartOffersPerFrame => LightmapBake.Hold ? 128 : 8;
    private static ExtenderLog? _log;
    private static bool _saidNoDraw;
    /// <summary>Inside the off-device self-check: its refusals are being PROVED, not reported.</summary>
    internal static bool _inSelfCheck;

    private static void Refuse(string what) { if (!_inSelfCheck) _log?.Info(what); }

    /// <summary>The renderer's per-view draw, registered when the renderer arms. A frame with records to draw and
    /// nothing registered is a loud refusal, said once -- never a quiet black screen.</summary>
    internal static Action<DbId, DbId>? Draw;

    /// <summary>Views this frame asked for, in order: what the snapshot publisher hands the render thread.</summary>
    internal static int ViewCount => _viewN;
    internal static DbId ViewScene(int k) => _viewScene[k];
    internal static DbId ViewCamera(int k) => _viewCam[k];

    internal static void Attach(ExtenderLog log) { _log = log; DrawRecipe.Log = log; }

    /// <summary>The game asked to render <paramref name="scene"/> through <paramref name="camera"/> (a camera's
    /// <c>RenderScene</c>); record the view, in order, for this frame's snapshot. Allocation-free, and it draws
    /// nothing: the frame runs once, at <see cref="Present"/>, so a scene asked for twice is two views over one
    /// build rather than two builds.</summary>
    public static void RequestView(DbId scene, DbId camera)
    {
        if (!Renderer.On) return;
        if (_viewN >= MaxViews)
        {
            if (_viewRefused++ == 0)
                Refuse($"DbFeed: more than {MaxViews} views in one frame -- the surplus is NOT DRAWN "
                     + "(a frame does not legitimately ask for this many; look for a view request that never resets)");
            return;
        }
        _viewScene[_viewN] = scene;
        _viewCam[_viewN] = camera;
        _viewN++;
    }

    /// <summary>The present boundary: publish the frame's snapshot and draw it (on the render thread once
    /// built). The dirty lists are folded ONCE for the frame -- every view draws the same records -- and then
    /// each view runs in the order the game asked for it.</summary>
    public static void Present()
    {
        if (!Renderer.On) { _viewN = 0; return; }
        _frames++;
        long tDirty = FrameZones.Now;
        Pumps();
        _lastRecWrites = TakeFrame();
        FrameZones.Add(FrameZones.Zone.DbDirty, tDirty);
        if (_viewN == 0) return;
        if (Draw == null)
        {
            if (!_saidNoDraw)
            {
                _saidNoDraw = true;
                Refuse($"DbFeed: {_viewN} view(s) requested and no renderer draw is registered -- NOTHING IS "
                     + "DRAWN this session. The scene database is being fed; the draw side is not armed.");
            }
            _viewN = 0;
            return;
        }
        for (int k = 0; k < _viewN; k++) Draw(_viewScene[k], _viewCam[k]);
        _viewN = 0;
    }

    /// <summary>The frame's dirty fold, on the GAME thread: intern any new draw recipes, then rewrite the
    /// renderer's persistent records for every dirty part and release the destroyed ones -- the first half of
    /// <see cref="Present"/>, without its draw loop. It reads and clears <see cref="DbTable"/>'s dirty lists,
    /// which the game thread is about to write again for frame N+1, so it cannot move off that thread.</summary>
    internal static void FoldFrame()
    {
        if (!Renderer.On) { _viewN = 0; return; }
        _frames++;
        long tDirty = FrameZones.Now;
        Pumps();
        _lastRecWrites = TakeFrame();
        FrameZones.Add(FrameZones.Zone.DbDirty, tDirty);
    }

    /// <summary>The fold's three pumps, each bracketed: a still frame writes ~15 records yet the fold reads
    /// 0,7 ms, and one zone holding the record walk AND three drains cannot say which of them that is. Nested
    /// in <see cref="FrameZones.Zone.DbDirty"/> and not subtracted from it, so the period line carries both
    /// the fold's total and the names inside it.</summary>
    private static void Pumps()
    {
        // The arena's own pump belongs to the FRAME, not to the world colour pass: a GUI-only frame (the main
        // menu, a panel session) still has meshes finishing on the worker, and leaving the pump inside the
        // world frame left them uncollected for as long as no world view was drawn.
        long t = FrameZones.Now;
        MeshArena.Pump();
        FrameZones.Add(FrameZones.Zone.ArenaPump, t);
        // CX-120: and the chart offers, on the arena's own edge -- while the loading screen is up and
        // before any draw. See MeshChart.DripDb.
        t = FrameZones.Now;
        MeshChart.DripDb(DbChartOffersPerFrame);
        FrameZones.Add(FrameZones.Zone.ChartDrip, t);
        t = FrameZones.Now;
        DrawRecipe.Pump();
        if (DrawRecipe.Reclassify() > 0) AlloyRenderer.DbReclassified();
        // ...and the part columns the same material seeds, off the same landing edge. Ahead of the dirty
        // fold, so a part whose .mtr landed re-stages in THIS frame rather than the next.
        PartTree.ReseedMaterials();
        FrameZones.Add(FrameZones.Zone.RecipePump, t);
    }

    /// <summary>Close the frame's view recording: the snapshot has taken the list, so the next frame starts
    /// empty. Called by the publisher right after it copies the views, on the game thread.</summary>
    internal static void CloseViews() => _viewN = 0;

    /// <summary>Is a renderer draw registered? The publisher asks before it takes a frame across, so "nothing
    /// is drawn" is said once by <see cref="Present"/>'s own line rather than twice.</summary>
    internal static bool HasDraw => Draw != null;

    private static readonly int[] _seatTally = new int[SceneDb.MaxObjects];

    /// <summary>Cold diagnostic: who holds the seated slots -- seated parts that are dead or orphaned, how many
    /// objects hold any, and the five objects holding the most, by model, flags and position.</summary>
    internal static string SeatCensus()
    {
        Array.Clear(_seatTally);
        int dead = 0, orphan = 0, seated = 0;
        for (int p = 0; p < SceneDb.MaxParts; p++)
        {
            if (SceneDb.PartSlot[p] < 0) continue;
            seated++;
            if (!SceneDb.Parts.LiveAt(p)) { dead++; continue; }
            DbId o = SceneDb.PartObj[p];
            if (!SceneDb.Objects.Valid(o)) { orphan++; continue; }
            _seatTally[o.Index]++;
        }
        Span<int> top = stackalloc int[5];
        top.Fill(-1);
        int holders = 0;
        for (int o = 0; o < SceneDb.MaxObjects; o++)
        {
            int c = _seatTally[o];
            if (c == 0) continue;
            holders++;
            for (int t = 0; t < top.Length; t++)
            {
                if (top[t] >= 0 && _seatTally[top[t]] >= c) continue;
                for (int u = top.Length - 1; u > t; u--) top[u] = top[u - 1];
                top[t] = o;
                break;
            }
        }
        var byModel = new System.Collections.Generic.Dictionary<string, (int n, int o)>();
        for (int o = 0; o < SceneDb.MaxObjects; o++)
        {
            if (_seatTally[o] == 0) continue;
            string m = (SceneDb.ObjFlags[o] & ObjFlag.Tile) != 0 ? "(tile)" : SceneDb.ObjSeamModelName[o] ?? "";
            byModel[m] = byModel.TryGetValue(m, out var v) ? (v.n + 1, o) : (1, o);
        }
        var models = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, (int n, int o)>>(byModel);
        models.Sort((x, y) => y.Value.n.CompareTo(x.Value.n));
        var sb = new System.Text.StringBuilder($" | seats: {seated} seated, {dead} on DEAD parts, {orphan} orphaned, {holders} objects hold any; by model:");
        for (int k = 0; k < models.Count && k < 8; k++)
        {
            int o = models[k].Value.o;
            DbId sc = SceneDb.ObjScene[o];
            sb.Append($" ['{models[k].Key}' x{models[k].Value.n}, e.g. obj {o} flags={SceneDb.ObjFlags[o]} visible={SceneDb.ObjVisible[o]} "
                    + $"scene={sc.Value} attachTo={SceneDb.ObjAttachTo[o].Value} pos=({SceneDb.ObjPos[o].X:F1},{SceneDb.ObjPos[o].Y:F1},{SceneDb.ObjPos[o].Z:F1})]");
        }
        sb.Append("; most:");
        foreach (int o in top)
            if (o >= 0)
                sb.Append($" [{_seatTally[o]} parts of obj {o} model='{SceneDb.ObjSeamModelName[o]}' flags={SceneDb.ObjFlags[o]} "
                        + $"range={SceneDb.ObjPartCount[o]} pos=({SceneDb.ObjPos[o].X:F1},{SceneDb.ObjPos[o].Y:F1},{SceneDb.ObjPos[o].Z:F1})]");
        return sb.ToString();
    }

    /// <summary>The module edge: every resref-keyed cache lets go of the module that ended, so the next one
    /// (a server's NWSync content after a singleplayer module) reads its own models, textures, materials and
    /// <c>.txi</c>s rather than the stock ones still resident. <c>OwnedTextures.ModuleUnloaded</c> reaches
    /// none of these. Game thread.</summary>
    internal static void ModuleEdge(ExtenderLog? log)
    {
        var (mEvicted, mHeld) = CX.Alloy.Mdl.ModelCache.ModuleEdge();
        var (tRetired, tHeld) = TexRegistry.ModuleEdge();
        MtrDef.Forget();
        MtrFeed.ModuleEdge();
        CX.Alloy.Seam.TexSeam.ModuleEdge();
        log?.Info($"dbfeed: module edge -- models {mEvicted} evicted, {mHeld} still held (dropped from lookup); "
                + $"textures {tRetired} retired, {tHeld} still held (dropped from lookup); materials and txi forgotten");
    }

    /// <summary>The feed's period line: what the change-driven build actually did.</summary>
    internal static string Stats()
        => $"dbfeed: frames {_frames} rec-writes {_lastRecWrites} visible {LastVisible} retry {Retrying} "
         + $"recipes {DrawRecipe.Count} (diffuse {DrawRecipe.DiffuseCensus().resolved}/{DrawRecipe.DiffuseCensus().named} resolved, {DrawRecipe.DiffuseCensus().standIn} stand-in) grid {DbGrid.Filed}/{DbGrid.Movers}/{DbGrid.Unboxed} {DrawRecipe.WaterNote()}"
         + (DrawRecipe.Spilled > 0 ? $" RECIPES SPILLED {DrawRecipe.Spilled}" : "")
         + (DrawRecipe.RetiredInUse > 0 ? $" RECIPES EVICTED IN USE {DrawRecipe.RetiredInUse}" : "")
         + (DrawRecipe.DoubleRetire > 0 ? $" RECIPES RETIRED TWICE {DrawRecipe.DoubleRetire}" : "")
         + (DrawRecipe.TwinRefused > 0 ? $" twin-shadowed {DrawRecipe.TwinRefused}" : "")
         + (DrawRecipe.BoxGrown > 0 ? $" box-grown {DrawRecipe.BoxGrown} (first {DrawRecipe.BoxGrownFirst})" : "")
         + (DrawRecipe.Retired > 0 ? $" retired {DrawRecipe.Retired}" : "")
         + (DrawRecipe.Reclassified > 0 ? $" reclassified {DrawRecipe.Reclassified}" : "")
         + (NoRecipe > 0 ? $" NO-MODEL {NoRecipe}" : "")
         + (RetryOverflow > 0 ? $" RETRY OVERFLOW {RetryOverflow}" : "")
         + (BadRange > 0 ? $" BAD RANGES {BadRange}" : "")
         + (LeakedJoins > 0 ? $" re-init releases {LeakedJoins}" : "")
         + (Recycled > 0 ? $" recycled {Recycled}" : "")
         + (_viewRefused > 0 ? $" VIEWS REFUSED {_viewRefused}" : "")
         + $" static-restage {StaticPartsLastFrame}/frame ({StaticParts} records, {StaticFolds} objects"
         + (StaticPartBits != Dirty.None ? $", {StaticPartBits}" : "")
         + (StaticFoldBits != Dirty.None ? $" via {StaticFoldBits}" : "") + ")" + SeatCensus();

    /// <summary>R1-30: a session edge -- the context is gone, so every GL-backed thing the feed points at is
    /// too. The records themselves survive (they are CPU state and the game still owns the objects); the
    /// visible-set index and the non-mesh side tables are rebuilt from the next fold.</summary>
    internal static void ContextLost()
    {
        DbGrid.Reset();
        DbInputs.Reset();
        for (int p = 0; p < SceneDb.MaxParts; p++)
            if (SceneDb.Parts.LiveAt(p)) SceneDb.Parts.Mark(p, Dirty.Created);
    }

    /// <summary>Off-device: reset every counter and the view list, so a self-check starts from a known state.</summary>
    internal static void ResetForSelfCheck()
    {
        _viewN = 0; _viewRefused = 0; _frames = 0; _lastRecWrites = 0;
        _saidNoDraw = false;
    }
}
