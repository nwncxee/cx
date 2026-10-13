using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The three properties the feed lives by, over a SYNTHETIC database -- no engine, no GL, so
/// run-demo exercises them. Each failure is silent live and would be chased elsewhere: a build that writes
/// records on a still frame reads as a slow renderer, a visible-set query that drops a record as missing
/// geometry, a view list that reorders as the GUI drawing behind the world.</summary>
public static unsafe partial class DbFeed
{
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbFeed.SelfCheck");
        ResetForSelfCheck();
        // This check drives the REAL draw recipes, so it acquires real texture slots and arena ranges: its
        // fixtures (cx_white, cx_selfcheck_*) must be retired at the end or they report themselves as a
        // FAILED white stand-in on every [alloy] line. Its deliberate refusals are kept out of the log too.
        _inSelfCheck = true;
        try
        {
            ViewOrder(check);
            Records(check);
            Recycle(check);
            VisibleSet(check);
            DrawOrder(check, log);
            RecipeReuse(check);
        }
        finally
        {
            _inSelfCheck = false;
            ResetForSelfCheck();
            DbGrid.Reset();
            // the fixture parts let go of their recipes first, as a real part's InitPart does before its model goes
            for (int p = 0; p < SceneDb.MaxParts; p++)
                if (ReferenceEquals(SceneDb.PartModel[p], _fixtureModel)) DrawRecipe.Assign(p, -1);
            DrawRecipe.Forget(_fixtureModel!);
            _fixtureModel = null;
            DbInputs.Reset();
        }
        log.Info(check.Ok ? "DbFeed.SelfCheck: passed"
                          : "DbFeed.SelfCheck: FAILED -- the scene database's feed would lose records, "
                          + "rebuild a still frame, or draw the views out of order");
        return check.Ok;
    }

    /// <summary>The view list is the ORDER the game asked in, duplicates kept, and it empties at the present.
    /// The GUI draws over the world because the game asked for the world first; a list that sorted or deduped
    /// would put a panel behind it.</summary>
    private static void ViewOrder(Checker check)
    {
        ResetForSelfCheck();
        var a = DbId.Make(3, 1);
        var b = DbId.Make(7, 1);
        var c1 = DbId.Make(11, 2);
        var c2 = DbId.Make(12, 2);
        RequestView(a, c1);
        RequestView(b, c2);
        RequestView(a, c1);
        check.Expect(ViewCount == 3, "every view the game asked for is recorded, duplicates included");
        check.Expect(ViewScene(0).Value == a.Value && ViewScene(1).Value == b.Value
                     && ViewScene(2).Value == a.Value, "...in the order it asked");
        check.Expect(ViewCamera(1).Value == c2.Value, "each view keeps its own camera");
        // The fixture needs a present with no draw registered, to prove the list empties. It must NOT leave
        // the session's real draw unset: the order that makes that harmless today is luck, and the symptom
        // would be a client that folds and publishes every frame and draws nothing, with no line saying so.
        Action<DbId, DbId>? hadDraw = Draw;
        int drawn = 0;
        Draw = (_, _) => drawn++;
        Present();
        check.Expect(ViewCount == 0, "the present empties the list, so a view is never drawn twice");
        check.Expect(drawn == 3, "...after drawing every view it held, once each");
        Draw = hadDraw;
        // ...and the surplus past the cap is refused rather than overwriting the list's head.
        ResetForSelfCheck();
        for (int k = 0; k < MaxViews + 4; k++) RequestView(a, c1);
        check.Expect(ViewCount == MaxViews, "the view list refuses the surplus instead of wrapping");
        ResetForSelfCheck();
    }

    /// <summary>Create, move and destroy on a synthetic database: exactly the right record writes, and a
    /// still frame writes NOTHING.</summary>
    private static void Records(Checker check)
    {
        AlloyRenderer.DbSnapshot snap = AlloyRenderer.DbTableSnapshot();
        try
        {
            ModelDef m = TwoPartModel();
            DbId obj = SceneDb.Objects.Alloc();
            check.Expect(!obj.IsNull, "the synthetic object got a record");
            int o = obj.Index;
            InitObject(o);
            SceneDb.ObjModel[o] = m;
            SceneDb.ObjScene[o] = DbId.Make(1, 1);
            SceneDb.ObjFlags[o] = ObjFlag.Static | ObjFlag.Tile;
            SceneDb.ObjSphere[o] = new Vector4(5f, 5f, 0f, 2f);
            SceneDb.ObjWorld[o] = Matrix4x4.Identity;

            DbId h0 = SceneDb.Parts.Alloc(), h1 = SceneDb.Parts.Alloc();
            int p0 = h0.Index, p1 = h1.Index;
            for (int k = 0; k < 2; k++)
            {
                int p = k == 0 ? p0 : p1;
                InitPart(p);
                SceneDb.PartObj[p] = obj;
                SceneDb.PartModel[p] = m;
                SceneDb.PartParent[p] = -1;
                SceneDb.PartEnabled[p] = true;
                SceneDb.PartMerged[p] = false;
                SceneDb.PartWorld[p] = Matrix4x4.Identity;
                SceneDb.PartScale[p] = 1f;
            }
            SceneDb.PartNode[p0] = 1;
            SceneDb.PartNode[p1] = 2;
            SceneDb.ObjPartFirst[o] = Math.Min(p0, p1);
            SceneDb.ObjPartCount[o] = Math.Abs(p1 - p0) + 1;

            // CREATE: two parts seated, two records written, nothing released.
            int writes = TakeFrame();
            check.Expect(LastSeated == 2 && LastReleased == 0,
                         $"a created object seats one slot per drawable part (seated {LastSeated})");
            check.Expect(writes == 2, "...and writes exactly one record per seated part");
            check.Expect(SceneDb.PartSlot[p0] >= 0 && SceneDb.PartSlot[p1] >= 0
                         && SceneDb.PartSlot[p0] != SceneDb.PartSlot[p1],
                         "each part holds its own ownership slot");
            check.Expect(AlloyRenderer.DbSlot(SceneDb.PartSlot[p0]),
                         "the slot reads as the database's, not the engine's");

            // STILL: nothing dirty, nothing written. This is the property the whole build rests on.
            writes = TakeFrame();
            check.Expect(writes == 0 && LastSeated == 0 && LastRewritten == 0 && LastReleased == 0,
                         "a still frame writes no records at all");

            // MOVE: the object's transform marks its parts, and only its parts, for a rewrite.
            SceneDb.ObjPos[o] = new Vector3(3f, 0f, 0f);
            SceneDb.PartWorld[p0] = Matrix4x4.CreateTranslation(3f, 0f, 0f);
            SceneDb.PartWorld[p1] = Matrix4x4.CreateTranslation(3f, 0f, 0f);
            SceneDb.Touch(obj, Dirty.Xform);
            // A STATIC object's fold does not fan Xform out: the compose marks the parts whose world moved.
            SceneDb.Parts.Mark(p0, Dirty.Anim);
            SceneDb.Parts.Mark(p1, Dirty.Anim);
            writes = TakeFrame();
            check.Expect(LastRewritten == 2 && LastSeated == 0 && LastReleased == 0,
                         $"a moved object rewrites its own records and seats nothing (rewritten {LastRewritten})");
            check.Expect(writes == 2, "...and writes exactly two records");

            // ...and the move is in the index: the object's cell followed it.
            SceneDb.ObjSphere[o] = new Vector4(305f, 305f, 0f, 2f);
            SceneDb.Touch(obj, Dirty.Xform);
            TakeFrame();
            check.Expect(DbGrid.Filed == 1, "a static that moves is filed once, in its new cell");

            // DESTROY: both slots released, and the index forgets the object.
            int s0 = SceneDb.PartSlot[p0];
            SceneDb.Parts.Free(h0);
            SceneDb.Parts.Free(h1);
            SceneDb.Objects.Free(obj);
            writes = TakeFrame();
            check.Expect(LastReleased >= 2 && LastSeated == 0,
                         $"a destroyed object releases every slot it held (released {LastReleased})");
            check.Expect(SceneDb.PartSlot[p0] < 0 && SceneDb.PartSlot[p1] < 0,
                         "...and the parts no longer name a slot");
            check.Expect(!AlloyRenderer.DbSlot(s0), "the released slot is no longer the database's");
            check.Expect(DbGrid.Filed == 0, "...and the visible-set index has forgotten it");

            // A handle taken before the free is dead: the contract that deletes every death notice.
            check.Expect(!SceneDb.Objects.Valid(obj) && !SceneDb.Parts.Valid(h0),
                         "a handle to a freed record resolves to nothing");

            writes = TakeFrame();
            check.Expect(writes == 0, "and the frame after a destroy is still again");
        }
        finally
        {
            AlloyRenderer.DbTableRestore(snap);
        }
    }

    /// <summary>A record freed and re-allocated inside ONE frame. The table's dirty list holds one entry per
    /// slot, so both marks arrive OR'd together, and the old tenant must be released against the range it was
    /// seated over before the new one is seated, in both orders. Otherwise the new object is never seated and
    /// a stale range lets a later tree change walk across another object's parts.</summary>
    private static void Recycle(Checker check)
    {
        AlloyRenderer.DbSnapshot snap = AlloyRenderer.DbTableSnapshot();
        try
        {
            ModelDef m = TwoPartModel();
            // Round 1: an object with one drawable part, seated.
            DbId o1 = SceneDb.Objects.Alloc();
            DbId h1 = SceneDb.Parts.Alloc();
            int oi = o1.Index, p1 = h1.Index;
            SetUp(o1, h1, m, 1);
            TakeFrame();
            check.Expect(SceneDb.PartSlot[p1] >= 0, "the first tenant is seated");
            int slot1 = SceneDb.PartSlot[p1];

            // A column the first tenant lowered must not be inherited. The reveal is the measured one: an
            // interior tile part that is UNEXPLORED sits at 0, and a creature part that took its slot after
            // an area transition drew as a flat fog-coloured silhouette. Every column a recycled part starts
            // with is InitPart's business, so that is what this asks.
            SceneDb.PartReveal[p1] = 0f;
            SceneDb.PartFade[p1] = 0.25f;
            SceneDb.PartAlpha[p1] = 0.5f;
            SceneDb.ObjCrossScene[oi] = SceneDb.ObjScene[oi];
            SceneDb.ObjSceneKinds[oi] = Seam.SeamShell.InGui;
            SceneDb.ObjSeamAppendHead[oi] = o1;
            SceneDb.ObjSeamAppendNext[oi] = o1;

            // Round 2: free it and re-allocate the SAME slots in one frame, which is what the table does
            // when a destroy and a create land between two folds.
            SceneDb.Parts.Free(h1);
            SceneDb.Objects.Free(o1);
            DbId o2 = SceneDb.Objects.Alloc();
            DbId h2 = SceneDb.Parts.Alloc();
            check.Expect(o2.Index == oi && h2.Index == p1,
                         "the test really does recycle the same table slots");
            InitObject(o2.Index);
            check.Expect(SceneDb.ObjScene[oi].IsNull && SceneDb.ObjCrossScene[oi].IsNull,
                         "a recycled object starts in no scene -- not in the last tenant's");
            check.Expect(SceneDb.ObjSceneKinds[oi] == 0, "a recycled object has never been in any scene");
            check.Expect(SceneDb.ObjSeamAppendHead[oi].IsNull && SceneDb.ObjSeamAppendNext[oi].IsNull,
                         "a recycled object owns nothing and is in no owner's list -- not the last tenant's children");
            SetUp(o2, h2, m, 2);
            check.Expect(SceneDb.PartReveal[p1] == 1f && SceneDb.PartFade[p1] == 1f
                         && SceneDb.PartAlpha[p1] == 1f,
                         "a recycled part starts EXPLORED, unfaded and opaque -- not on the last tenant's");
            int before = Recycled;
            TakeFrame();
            check.Expect(Recycled > before, "the fold noticed a record recycled inside one frame");
            check.Expect(SceneDb.PartSlot[p1] >= 0,
                         "the NEW tenant is seated -- a destroy in the same entry must not swallow it");
            check.Expect(AlloyRenderer.DbSlot(SceneDb.PartSlot[p1]),
                         "...in a slot that reads as the database's");

            // The old object's seated range must not survive: a later tree change has to walk the new one.
            SceneDb.ObjPartCount[oi] = 1;
            SceneDb.Touch(o2, Dirty.Tree);
            TakeFrame();
            check.Expect(SceneDb.PartSlot[p1] >= 0,
                         "a tree change after a recycle keeps the live part rather than releasing it");

            SceneDb.Parts.Free(h2);
            SceneDb.Objects.Free(o2);
            TakeFrame();
            check.Expect(SceneDb.PartSlot[p1] < 0, "...and the recycled tenant releases cleanly in the end");

            // R1-3: a DB slot is REUSED. Without the free-list fix the ownership table only ever grows.
            int high = AlloyRenderer.OwnHighForSelfCheck;
            for (int round = 0; round < 4; round++)
            {
                DbId o = SceneDb.Objects.Alloc();
                DbId h = SceneDb.Parts.Alloc();
                SetUp(o, h, m, 1);
                TakeFrame();
                SceneDb.Parts.Free(h);
                SceneDb.Objects.Free(o);
                TakeFrame();
            }
            check.Expect(AlloyRenderer.OwnHighForSelfCheck <= high + 1,
                         $"a released DB slot is handed back out (high water {high} -> {AlloyRenderer.OwnHighForSelfCheck})");
        }
        finally
        {
            AlloyRenderer.DbTableRestore(snap);
        }
    }

    /// <summary>One object with one drawable part on node <paramref name="node"/>, ready to fold.</summary>
    private static void SetUp(DbId obj, DbId part, ModelDef m, int node)
    {
        int o = obj.Index, p = part.Index;
        InitObject(o);
        SceneDb.ObjModel[o] = m;
        SceneDb.ObjScene[o] = DbId.Make(1, 1);
        SceneDb.ObjFlags[o] = ObjFlag.Static;
        SceneDb.ObjSphere[o] = new Vector4(5f, 5f, 0f, 2f);
        SceneDb.ObjWorld[o] = Matrix4x4.Identity;
        SceneDb.ObjPartFirst[o] = p;
        SceneDb.ObjPartCount[o] = 1;
        InitPart(p);
        SceneDb.PartObj[p] = obj;
        SceneDb.PartModel[p] = m;
        SceneDb.PartNode[p] = node;
        SceneDb.PartParent[p] = -1;
        SceneDb.PartEnabled[p] = true;
        SceneDb.PartMerged[p] = false;
        SceneDb.PartWorld[p] = Matrix4x4.Identity;
        SceneDb.PartScale[p] = 1f;
    }

    /// <summary>The visible-set query yields a visible static's slots, skips a part of an alternate the object
    /// is not showing, and skips an invisible object and another scene's.</summary>
    private static void VisibleSet(Checker check)
    {
        AlloyRenderer.DbSnapshot snap = AlloyRenderer.DbTableSnapshot();
        try
        {
            DbGrid.Reset();
            ModelDef m = TwoPartModel();
            DbId scene = DbId.Make(1, 1), other = DbId.Make(2, 1);
            DbId obj = SceneDb.Objects.Alloc();
            int o = obj.Index;
            InitObject(o);
            SceneDb.ObjModel[o] = m;
            SceneDb.ObjScene[o] = scene;
            SceneDb.ObjFlags[o] = ObjFlag.Static;
            SceneDb.ObjSphere[o] = new Vector4(305f, 305f, 0f, 2f);
            SceneDb.ObjLodShown[o] = 0;
            DbId h0 = SceneDb.Parts.Alloc(), h1 = SceneDb.Parts.Alloc();
            int p0 = h0.Index, p1 = h1.Index;
            for (int k = 0; k < 2; k++)
            {
                int p = k == 0 ? p0 : p1;
                InitPart(p);
                SceneDb.PartObj[p] = obj;
                SceneDb.PartModel[p] = m;
                SceneDb.PartParent[p] = -1;
                SceneDb.PartEnabled[p] = true;
                SceneDb.PartWorld[p] = Matrix4x4.Identity;
            }
            SceneDb.PartNode[p0] = 1; SceneDb.PartNode[p1] = 2;
            SceneDb.PartLodTree[p0] = 1;   // level 0's tree, the one the object shows
            SceneDb.PartLodTree[p1] = 2;   // ...and level 1's, which it does not
            SceneDb.ObjPartFirst[o] = Math.Min(p0, p1);
            SceneDb.ObjPartCount[o] = Math.Abs(p1 - p0) + 1;
            TakeFrame();

            float* planes = stackalloc float[24];
            for (int f = 0; f < 6; f++)
            { planes[f * 4] = 0f; planes[f * 4 + 1] = 0f; planes[f * 4 + 2] = 0f; planes[f * 4 + 3] = 1f; }
            planes[0] = 1f; planes[3] = -100f;   // keep x >= 100; the object sits at 305

            int n = Visible(scene, planes, 0f);
            check.Expect(n == 1, $"only the part of the shown LOD alternate is visible (got {n})");
            check.Expect(n == 1 && VisibleSlot(0) == SceneDb.PartSlot[p0],
                         "...and it is the shown alternate's slot");
            check.Expect(Visible(other, planes, 0f) == 0, "another scene's view yields none of this scene");

            SceneDb.ObjVisible[o] = false;
            check.Expect(Visible(scene, planes, 0f) == 0, "an invisible object yields nothing");
            SceneDb.ObjVisible[o] = true;
            SceneDb.PartEnabled[p0] = false;
            check.Expect(Visible(scene, planes, 0f) == 0, "a disabled subtree yields nothing");
            SceneDb.PartEnabled[p0] = true;
            SceneDb.ObjLodShown[o] = 1;
            check.Expect(Visible(scene, planes, 0f) == 1 && VisibleSlot(0) == SceneDb.PartSlot[p1],
                         "switching the shown alternate switches which slot is visible, with no reseat");

            planes[3] = -1e9f;
            check.Expect(Visible(scene, planes, 0f) == 0, "a frustum that reaches nothing yields nothing");

            SceneDb.Parts.Free(h0);
            SceneDb.Parts.Free(h1);
            SceneDb.Objects.Free(obj);
            TakeFrame();
        }
        finally
        {
            AlloyRenderer.DbTableRestore(snap);
        }
    }

    /// <summary>The draw order over seated DB slots: every slot once, grouped by class, class ranges exact.
    /// Runs the real <c>DbOrder</c> over a synthetic database, which is the only off-device way to exercise
    /// the radix and the area index on DB tenants at all.</summary>
    private static void DrawOrder(Checker check, ExtenderLog log)
    {
        AlloyRenderer.DbSnapshot snap = AlloyRenderer.DbTableSnapshot();
        try
        {
            ModelDef m = TwoPartModel();
            var objs = new DbId[4];
            var parts = new DbId[4];
            var slots = new int[4];
            var cls = new int[4];
            for (int k = 0; k < 4; k++)
            {
                objs[k] = SceneDb.Objects.Alloc();
                parts[k] = SceneDb.Parts.Alloc();
                SetUp(objs[k], parts[k], m, 1 + (k & 1));
                SceneDb.ObjSphere[objs[k].Index] = new Vector4(k * 12f, 0f, 0f, 1f);
            }
            TakeFrame();
            int seated = 0;
            for (int k = 0; k < 4; k++)
            {
                slots[k] = SceneDb.PartSlot[parts[k].Index];
                if (slots[k] >= 0) seated++;
                cls[k] = slots[k] >= 0 ? AlloyRenderer.ClsOfForSelfCheck(slots[k]) : 0;
            }
            check.Expect(seated == 4, $"four synthetic parts seated ({seated})");
            check.Expect(AlloyRenderer.DbOrderSelfCheck(log, slots, cls),
                         "the DB draw order holds every seated slot once, grouped by class");
            for (int k = 0; k < 4; k++)
            {
                SceneDb.Parts.Free(parts[k]);
                SceneDb.Objects.Free(objs[k]);
            }
            TakeFrame();
        }
        finally
        {
            AlloyRenderer.DbTableRestore(snap);
        }
    }

    /// <summary>A retired recipe slot is held out for one <see cref="DrawRecipe.Pump"/> and handed out after the
    /// second; one a live part still names is not handed out at all; a late dynamic drop leaves the slot's next
    /// tenant alone; a per-instance range is never handed to another mesh. Oracle: the indices the first calls returned.</summary>
    private static void RecipeReuse(Checker check)
    {
        ModelDef a = OneMeshModel("cx_selfcheck_ra"), b = OneMeshModel("cx_selfcheck_rb"), c = OneMeshModel("cx_selfcheck_rc");
        int ra = DrawRecipe.Intern(a, 1, plan: false);
        check.Expect(ra >= 0, "the fixture node interned a recipe");
        DrawRecipe.Forget(a);
        DrawRecipe.Pump();
        int rb = DrawRecipe.Intern(b, 1, plan: false);
        check.Expect(rb >= 0 && rb != ra, "a retired slot is not reused before the fold that releases its draw slots");
        DrawRecipe.Pump();
        int rc = DrawRecipe.Intern(c, 1, plan: false);
        check.Expect(rc == ra, $"a retired slot is reused after it (got {rc}, retired {ra})");
        DrawRecipe.Forget(b);
        DrawRecipe.Forget(c);

        // the fixture's own eviction-in-use is on purpose: kept off the log and the period line
        var log = DrawRecipe.Log;
        int inUse = DrawRecipe.RetiredInUse;
        DrawRecipe.Log = null;
        ModelDef d = OneMeshModel("cx_selfcheck_rd"), e = OneMeshModel("cx_selfcheck_re"), f = OneMeshModel("cx_selfcheck_rf");
        DbId h = SceneDb.Parts.Alloc();
        int rd = DrawRecipe.Intern(d, 1, plan: false);
        DrawRecipe.Assign(h.Index, rd);
        DrawRecipe.Forget(d);
        DrawRecipe.Pump(); DrawRecipe.Pump();
        int re = DrawRecipe.Intern(e, 1, plan: false);
        check.Expect(rd >= 0 && re != rd, "a recipe a live part still names is not handed to another model");
        DrawRecipe.Assign(h.Index, -1);
        DrawRecipe.Pump(); DrawRecipe.Pump();
        int rf = DrawRecipe.Intern(f, 1, plan: false);
        check.Expect(rf == rd, $"...and is once the part lets go (got {rf}, held {rd})");
        SceneDb.Parts.Free(h);
        DrawRecipe.Forget(e); DrawRecipe.Forget(f);
        DrawRecipe.Log = log;
        DrawRecipe.RetiredInUse = inUse;

        ModelDef g = OneMeshModel("cx_selfcheck_rg"), k = OneMeshModel("cx_selfcheck_rk");
        int range = MeshArena.AcquireDynamic(g.Nodes[1].Mesh!, 0x5E1F_C4EC_0000_0001UL);
        int rg = DrawRecipe.InternDynamic(g, 1, range);
        uint gen = DrawRecipe.Gen(rg);
        DrawRecipe.Forget(g);   // the model goes first, and its eviction retires the private recipe
        DrawRecipe.Pump(); DrawRecipe.Pump();
        int rk = DrawRecipe.Intern(k, 1, plan: false);
        check.Expect(rg >= 0 && rk == rg, $"the evicted private recipe's slot is reused (got {rk}, retired {rg})");
        DrawRecipe.RetireDynamic(rg, gen);   // ...and then its part's drop arrives late
        check.Expect(DrawRecipe.Range(rk) >= 0, "a late dynamic drop leaves the slot's new tenant alone");
        DrawRecipe.Forget(k);

        // A part index re-tenanted by another model asks for the same per-instance key with a different mesh.
        MeshDef mp = OneMeshModel("cx_selfcheck_rp").Nodes[1].Mesh!, mq = OneMeshModel("cx_selfcheck_rq").Nodes[1].Mesh!;
        const ulong key = 0x5E1F_C4EC_0000_0002UL;   // not-an-offset: a fixture key, distinct from the one above
        int rp = MeshArena.AcquireDynamic(mp, key), rq = MeshArena.AcquireDynamic(mq, key);
        check.Expect(rp >= 0 && rq >= 0 && rq != rp, $"a per-instance key asked for by another mesh gets a range of its own (got {rq}, held {rp})");
        int rq2 = MeshArena.AcquireDynamic(mq, key);
        check.Expect(rq2 == rq, $"...and the same mesh on that key keeps it (got {rq2}, held {rq})");
        MeshArena.Release(rp); MeshArena.Release(rq); MeshArena.Release(rq2);
    }

    private static ModelDef OneMeshModel(string name)
    {
        var m = new ModelDef { Name = name };
        m.Nodes = new[] { new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy }, MeshNode("a", 1) };
        return m;
    }

    /// <summary>The one model every case here uses, so the teardown has exactly one thing to retire.</summary>
    private static ModelDef? _fixtureModel;

    /// <summary>A synthetic two-mesh model: a root dummy and two drawable nodes, each with a unit cube's worth
    /// of bounds and one triangle, which is all a recipe needs.</summary>
    private static ModelDef TwoPartModel()
    {
        if (_fixtureModel != null) return _fixtureModel;
        var m = new ModelDef { Name = "cx_selfcheck", ContentHash = 0xC0FFEEUL };
        var root = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy };
        m.Nodes = new[] { root, MeshNode("a", 1), MeshNode("b", 2) };
        m.NodeByName["root"] = 0; m.NodeByName["a"] = 1; m.NodeByName["b"] = 2;
        _fixtureModel = m;
        return m;
    }

    private static NodeDef MeshNode(string name, int index) => new()
    {
        Name = name,
        Index = index,
        Parent = 0,
        Kind = NodeKind.TriMesh,
        Mesh = new MeshDef
        {
            Positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            Indices = new uint[] { 0, 1, 2 },
            BMin = new Vector3(-0.5f, -0.5f, -0.5f),
            BMax = new Vector3(0.5f, 0.5f, 0.5f),
            Radius = 0.87f,
            // Deliberately textureless: a fixture that named one would ACQUIRE it, and a check's fixture has
            // no business in the resident set a live session reports on.
            Textures = new[] { "", "", "", "" },
        },
    };
}
