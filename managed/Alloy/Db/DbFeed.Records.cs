using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary><b>The change-driven record build:</b> one fold of the two dirty lists per frame, and the
/// visible-set query per view. Nothing here scans a table (ADR 0028 section 4). Order matters -- objects fold
/// FIRST, because an object's edge is what marks its parts; parts fold second and see every mark the object fold
/// just made, because <c>DbTable.Mark</c> appends to the list the fold is walking.</summary>
public static unsafe partial class DbFeed
{
    /// <summary>The ownership slots a view's query yielded, and the objects it yielded them from. Sized for
    /// the tables, allocated once: the per-frame path allocates nothing.</summary>
    private static readonly int[] _visObj = new int[SceneDb.MaxObjects];
    private static readonly int[] _visSlot = new int[SceneDb.MaxParts];
    private static int _visSlotN;

    /// <summary>Slots the last visible-set query yielded.</summary>
    internal static int LastVisible => _visSlotN;

    /// <summary>Parts seated, released and rewritten by the last fold -- the numbers the self-check asserts
    /// and the period line reports.</summary>
    internal static int LastSeated { get; private set; }
    internal static int LastReleased { get; private set; }
    internal static int LastRewritten { get; private set; }
    /// <summary>Parts whose model could not be reached, so they have no recipe and draw nothing. Counted, not
    /// swallowed: a part that never gets a recipe is a permanent hole and this is the number that says so.</summary>
    internal static int NoRecipe { get; private set; }

    /// <summary>Fold this frame's dirty lists into the renderer's records. Returns the number of record writes
    /// -- 0 on a frame in which nothing changed, which is the property the whole scheme rests on.</summary>
    internal static int TakeFrame()
    {
        LastSeated = 0; LastReleased = 0; LastRewritten = 0;
        FoldObjects();
        FoldParts();
        return LastSeated + LastReleased + LastRewritten;
    }

    private static void FoldObjects()
    {
        DbTable t = SceneDb.Objects;
        int n = t.DirtyCount;
        for (int k = 0; k < n; k++)
        {
            int o = t.DirtyIndex(k);
            Dirty d = t.DirtyAt(o);
            // R1-2: Destroyed and Created can BOTH be set -- a slot freed and re-allocated inside one frame
            // ORs its two marks into one dirty entry, and the table's dirty list holds one entry per slot.
            // The old tenant has to go FIRST, against the range it was seated over (_seatedFirst/Count, not
            // the new tenant's ObjPartFirst/Count), and then the new one is filed like any newcomer. Taking
            // the destroy and skipping the rest lost the new object entirely and left the stale range behind
            // for a later Dirty.Tree to walk across other objects' parts.
            if ((d & Dirty.Destroyed) != 0)
            {
                DbGui.Left(o, _seatedScene[o]);
                ObjectDied(o);
                if ((d & Dirty.Created) == 0) continue;
                Recycled++;
            }
            if (!t.LiveAt(o)) continue;
            int first = SceneDb.ObjPartFirst[o], count = ClampRange(o, first, SceneDb.ObjPartCount[o]);
            if ((d & Dirty.Created) != 0)
            {
                _seatedFirst[o] = first; _seatedCount[o] = count;
                _seatedScene[o] = SceneDb.ObjScene[o];
                _filed[o] = false;
                DbGui.Joined(o, SceneDb.ObjScene[o]);
            }
            // R1-13: a re-parented object leaves its old panel's list and joins the new one. Nothing else
            // notices a scene change, and a control left in the old list paints in the wrong panel.
            else if (_seatedScene[o].Value != SceneDb.ObjScene[o].Value)
            {
                DbGui.Left(o, _seatedScene[o]);
                _seatedScene[o] = SceneDb.ObjScene[o];
                DbGui.Joined(o, SceneDb.ObjScene[o]);
            }
            // R1-16: an object created before its transform step has run has a ZERO sphere, which files it
            // unbounded -- and a static never moves again, so it would stay unbounded for the session. The
            // filing is retried until the sphere is real.
            if ((d & (Dirty.Created | Dirty.Xform)) != 0 || !_filed[o]) FileObject(o);
            // A tree change replaces the part range wholesale, so every slot the OLD range held has to go
            // before the new one is seated -- a part index that left the range would otherwise keep drawing.
            if ((d & Dirty.Tree) != 0) ReleaseRangeOutside(o, first, count);
            // R1-37: Visible is NOT passed down. The visible-set query reads ObjVisible directly, so a
            // visibility flip changes which records are DRAWN and none of what any record SAYS; marking the
            // parts would rewrite a creature's forty records to change nothing in them.
            // A static object's anim mark is not passed down: its compose marks the parts whose world moved and
            // its pose the ones whose material did (DbXform, DbAnim.Pose), and an animated tile replaying a still
            // pose restaged its whole range every frame.
            // ...nor its Xform: a posed static tile marks its object Xform every frame (DbAnim.Moved), and the
            // compose already marked exactly the parts whose world changed -- fanning it out restaged 52 animated
            // tiles' 2054 records a frame in a settled area.
            Dirty pass = d & (Dirty.Created | Dirty.Material | Dirty.Texture | Dirty.Tree
                              | ((SceneDb.ObjFlags[o] & ObjFlag.Static) != 0 ? Dirty.None : Dirty.Anim | Dirty.Xform));
            if (pass == Dirty.None) continue;
            // CX-116: a STATIC object never moves once placed, so after its first frame it has no business
            // being folded again. Anything that does is a per-frame mark on something change-driven, and the
            // cause is named rather than measured by its cost -- a settled area must read 0 here.
            if ((SceneDb.ObjFlags[o] & ObjFlag.Static) != 0 && (pass & ~Dirty.Created) != Dirty.None)
            {
                StaticFolds++;
                StaticFoldBits |= pass & ~Dirty.Created;
            }
            for (int p = first; p < first + count; p++)
                if (SceneDb.Parts.LiveAt(p)) SceneDb.Parts.Mark(p, pass);
        }
        t.TakeDirty();
    }

    /// <summary>Static objects folded since boot and the bits they carried. A settled area restages no
    /// static record, so the bits name the cause when one is.</summary>
    internal static long StaticFolds { get; private set; }
    /// <inheritdoc cref="StaticFolds"/>
    internal static Dirty StaticFoldBits { get; private set; }

    /// <inheritdoc cref="StaticFolds"/>
    internal static long StaticParts { get; private set; }
    /// <inheritdoc cref="StaticFolds"/>
    internal static Dirty StaticPartBits { get; private set; }
    /// <inheritdoc cref="StaticFolds"/>
    internal static int StaticPartsLastFrame { get; private set; }
    private static int _staticPartFrame;

    /// <summary>Close the frame's static-PART tally (after the part pass).</summary>
    private static void CloseStaticParts() { StaticPartsLastFrame = _staticPartFrame; _staticPartFrame = 0; }

    private static void FoldParts()
    {
        DbTable t = SceneDb.Parts;
        int n = t.DirtyCount;
        {
            for (int k = 0; k < n; k++)
            {
                int p = t.DirtyIndex(k);
                Dirty d = t.DirtyAt(p);
                // R1-1: the same recycle the object fold handles, one level down.
                if ((d & Dirty.Destroyed) != 0)
                {
                    // R1-28: counted once. An object destroy already released this part's slot in the object
                    // fold, so the count is of slots actually given up, not of marks seen.
                    bool held = SceneDb.PartSlot[p] >= 0;
                    AlloyRenderer.DbRelease(p);
                    DbInputs.PartDestroyed(p);
                    if (held) LastReleased++;
                    if ((d & Dirty.Created) == 0) continue;
                    Recycled++;
                }
                if (!t.LiveAt(p)) continue;
                // CX-116: a part of a STATIC object, rewritten after its first frame. The object fold's
                // tally cannot see a part marked directly (TouchPart), which is how a per-frame mark on a
                // tile reaches a record without its object ever being dirty.
                if ((d & ~Dirty.Created) != Dirty.None)
                {
                    DbId po = SceneDb.PartObj[p];
                    if (SceneDb.Objects.Valid(po) && (SceneDb.ObjFlags[po.Index] & ObjFlag.Static) != 0)
                    {
                        StaticParts++;
                        StaticPartBits |= d & ~Dirty.Created;
                        _staticPartFrame++;
                    }
                }
                FoldOnePart(p, d);
            }
            // R1-10: the parts whose geometry was not resident when they were staged. DbStage's refusal is a
            // DEFERRAL, not a drop, so they are re-asked here every frame until they take -- which is what
            // the doc always claimed and the code did not do.
            DrainRetry();
        }
        t.TakeDirty();
        CloseStaticParts();
    }

    private static void FoldOnePart(int p, Dirty d)
    {
        // A light, an emitter or any other non-mesh node has no record and no slot; it files itself with its
        // own consumer instead, on the same edge, so the two cannot drift apart.
        if ((d & Dirty.Created) != 0) DbInputs.PartCreated(p);
        int recipe = RecipeOf(p);
        if (recipe < 0) { AlloyRenderer.DbRelease(p); return; }
        bool fresh = SceneDb.PartSlot[p] < 0;
        int slot = AlloyRenderer.DbSeat(p, recipe);
        if (slot < 0) return;
        // CX-188: the slot's texture memo's own edge. The draw-side resolve is skipped while the registry
        // serial holds still, and what that serial cannot see is this part's OVERRIDE column
        // (SceneDb.PartTexId, written by Gob::ReplaceTexture, RestoreTexture and the PLT swaps) and its
        // object's env map (SceneDb.ObjEnvMap, from Gob::SetEnvironmentMap) -- each of
        // which ends in SceneDb.Touch(obj, Dirty.Texture). Material and Tree are in because either can
        // re-point the recipe the resolve reads its slots from; Created is covered by the seat's unstamp and
        // listed for the same reason.
        if ((d & (Dirty.Created | Dirty.Tree | Dirty.Texture | Dirty.Material)) != Dirty.None)
            AlloyRenderer.DbTexDirty(slot);
        if (fresh) LastSeated++; else LastRewritten++;
        if (!AlloyRenderer.DbStage(slot)) Retry(p);
    }

    /// <summary>R1-10: parts whose last stage could not resolve geometry or a texture. A bounded set, drained
    /// every frame; a part that lands leaves it, a part that is destroyed leaves it at its release.</summary>
    private static readonly int[] _retry = new int[MaxRetry];
    private static readonly bool[] _retrying = new bool[SceneDb.MaxParts];
    private static int _retryN;
    /// <summary>Parts deferred at once. Past this the overflow is COUNTED and the part is re-marked dirty
    /// instead, which is slower and never a silent hole.</summary>
    internal const int MaxRetry = 1 << 14;
    /// <summary>Deferrals the retry list could not hold, and records recycled inside one frame.</summary>
    internal static int RetryOverflow { get; private set; }
    internal static int Recycled { get; private set; }
    internal static int Retrying => _retryN;

    private static void Retry(int p)
    {
        if (_retrying[p]) return;
        if (_retryN >= _retry.Length) { RetryOverflow++; SceneDb.Parts.Mark(p, Dirty.Material); return; }
        _retry[_retryN++] = p;
        _retrying[p] = true;
    }

    private static void DrainRetry()
    {
        int w = 0;
        for (int k = 0; k < _retryN; k++)
        {
            int p = _retry[k];
            int slot = SceneDb.Parts.LiveAt(p) ? SceneDb.PartSlot[p] : -1;
            if (slot < 0) { _retrying[p] = false; continue; }
            if (AlloyRenderer.DbStage(slot)) { _retrying[p] = false; continue; }
            _retry[w++] = p;
        }
        _retryN = w;
    }

    /// <summary>R1-36: an object's part range as far as the part table actually goes. A range that runs past
    /// <see cref="SceneDb.MaxParts"/> is a caller's bug; it is clamped and counted rather than walked.</summary>
    private static int ClampRange(int o, int first, int count)
    {
        if (first < 0 || count < 0 || (long)first + count > SceneDb.MaxParts)
        {
            BadRange++;
            if (first < 0 || first >= SceneDb.MaxParts) return 0;
            return Math.Min(count < 0 ? 0 : count, SceneDb.MaxParts - first);
        }
        return count;
    }

    /// <summary>Part ranges that ran off the table.</summary>
    internal static int BadRange { get; private set; }

    /// <summary>Seated parts whose join was cut by a re-init rather than by a destroy. Not an error -- the
    /// release above makes it correct -- but it is the number that says a tree is being rebuilt under
    /// records, which is worth seeing if it is ever large.</summary>
    internal static int LeakedJoins { get; private set; }

    /// <summary>The part's interned draw recipe, building it on first ask. -1 for a part with no geometry (a
    /// dummy, a light, an emitter: those have their own consumers) or whose model is gone.</summary>
    private static int RecipeOf(int p)
    {
        int r = SceneDb.PartRecipe[p];
        if (r >= 0) return r;
        ModelDef? m = SceneDb.PartModel[p];
        if (m == null) { NoRecipe++; return -1; }
        int node = SceneDb.PartNode[p];
        // a part draws plan geometry when the merge swallowed it or handed it the merged mesh in place of its own
        bool plan = SceneDb.PartMerged[p] || (SceneDb.PartMeshDef[p] is MeshDef pm && (uint)node < (uint)m.Nodes.Length
                                              && !ReferenceEquals(pm, m.Nodes[node].Mesh));
        r = DrawRecipe.Intern(m, node, plan);
        DrawRecipe.Assign(p, r);
        return r;
    }

    /// <summary>The object died: release every slot its part range held and take it out of the index. The one
    /// destruction edge -- told by the game, never inferred, so there is no sweep and no tombstone.</summary>
    private static void ObjectDied(int o)
    {
        // R1-2: the range the object's slots were SEATED over, not the columns -- on a recycle those already
        // describe the new tenant. R1-11: every part of the range is told it died, so the light and emitter
        // tables drop their entries too; without it a reused part index revived a dead light.
        int first = _seatedFirst[o], count = ClampRange(o, first, _seatedCount[o]);
        for (int p = first; p < first + count; p++)
        {
            if (SceneDb.PartSlot[p] >= 0) { AlloyRenderer.DbRelease(p); LastReleased++; }
            DbInputs.PartDestroyed(p);
        }
        _seatedFirst[o] = 0; _seatedCount[o] = 0; _filed[o] = false;
        DbGrid.Remove(o);
    }

    /// <summary>The range each object's slots were last seated over. Feed-private: it exists so a tree change
    /// is bounded by the range it REPLACES rather than by a scan of the part table.</summary>
    private static readonly int[] _seatedFirst = new int[SceneDb.MaxObjects];
    private static readonly int[] _seatedCount = new int[SceneDb.MaxObjects];
    /// <summary>The scene each object was last listed under, so a re-parent is an edge the GUI list sees.</summary>
    private static readonly DbId[] _seatedScene = new DbId[SceneDb.MaxObjects];
    /// <summary>Has this object been filed in the visible-set index with a REAL sphere yet? (R1-16.)</summary>
    private static readonly bool[] _filed = new bool[SceneDb.MaxObjects];

    private static void ReleaseRangeOutside(int o, int first, int count)
    {
        int wasFirst = _seatedFirst[o], wasCount = _seatedCount[o];
        for (int p = wasFirst; p < wasFirst + wasCount; p++)
        {
            if (p >= first && p < first + count) continue;
            if (SceneDb.PartSlot[p] < 0) continue;
            AlloyRenderer.DbRelease(p);
            LastReleased++;
        }
        _seatedFirst[o] = first; _seatedCount[o] = count;
    }

    /// <summary>File (or re-file) object <paramref name="o"/> in the visible-set index from its world sphere.
    /// A static object is filed in a cell; everything else joins the mover list.</summary>
    private static void FileObject(int o)
    {
        Vector4 s = SceneDb.ObjSphere[o];
        bool stat = (SceneDb.ObjFlags[o] & ObjFlag.Static) != 0;
        DbGrid.File(o, s.X, s.Y, s.Z, s.W, stat);
        // Only a real sphere counts as filed: a zero one is the window between the record's creation and the
        // transform step that places it, and a static filed in that window never moves again (R1-16).
        _filed[o] = s.W > 0f;
    }

    /// <summary>The visible set for one view: every ownership slot the index yields for <paramref name="planes"/>
    /// with an enabled part and a visible object in <paramref name="scene"/>. <see cref="VisibleSlot"/>(0..count);
    /// allocation-free, O(visible).</summary><remarks>Workers stage from these columns unlocked, sound only because
    /// the frame boundary freezes them: fold and publish precede the draw, and no seam setter runs inside it.</remarks>
    internal static int Visible(DbId scene, ReadOnlySpan<Vector4> planes, float slack)
    {
        long t0 = FrameZones.Now;
        _visSlotN = 0;
        int n = DbGrid.Query(planes, slack, _visObj, 0);
        for (int k = 0; k < n; k++)
        {
            int o = _visObj[k];
            if (!SceneDb.Objects.LiveAt(o)) continue;
            if (!SceneDb.ObjVisible[o]) continue;
            if (SceneDb.ObjScene[o].Value != scene.Value) continue;
            byte shown = SceneDb.ObjLodShown[o];
            int first = SceneDb.ObjPartFirst[o], count = SceneDb.ObjPartCount[o];
            for (int p = first; p < first + count && _visSlotN < _visSlot.Length; p++)
            {
                if (!SceneDb.Parts.LiveAt(p)) continue;
                if (!SceneDb.PartEnabled[p] || SceneDb.PartMerged[p]) continue;
                // The LOD question, as a column compare: the part belongs to an alternate the object is not
                // showing. The engine path read Gob+0x90's active tree through a pointer, per part, per pass.
                byte tree = SceneDb.PartLodTree[p];
                if (SceneDb.LodHidden(tree, shown)) continue;
                int slot = SceneDb.PartSlot[p];
                if (slot < 0) continue;
                _visSlot[_visSlotN++] = slot;
            }
        }
        FrameZones.Add(FrameZones.Zone.DbVis, t0);
        return _visSlotN;
    }

    internal static int VisibleSlot(int k) => _visSlot[k];

    /// <summary>Seed the columns a part needs before its first record write. Called by whoever builds a part
    /// range (seam-obj through <c>PartTree.Build</c>); spelled here because the feed is what reads them, and a
    /// zeroed <see cref="SceneDb.PartSlot"/> would otherwise read as "slot 0".</summary>
    public static void InitPart(int p)
    {
        // The join is cut here, so whatever held it has to go FIRST. PartTree.Write/Clear call this on part
        // indices that may still be seated -- a rebuilt tree, a recycled chunk -- and a slot whose part
        // stopped naming it can never be found by DbRelease again: it would hold the dead tenant's record,
        // keep drawing it, and never return to the free list. Releasing here makes the order of these two
        // calls stop mattering, which is the only way a contract between two streams stays true.
        if (SceneDb.PartSlot[p] >= 0) { AlloyRenderer.DbRelease(p); LeakedJoins++; }
        DbInputs.PartDestroyed(p);
        SceneDb.PartSlot[p] = -1;
        DrawRecipe.Assign(p, -1);
        SceneDb.PartFade[p] = 1f;
        SceneDb.PartDissolve[p] = 1f;
        DbAnim.DanglyPartRecycled(p);
        SceneDb.PartBoneBase[p] = -1;
        SceneDb.PartAlpha[p] = 1f;
        // EXPLORED. The fog-of-war reveal belongs to a TILE and to nothing else -- vanilla fogs an unexplored
        // interior tile toward the fog colour in RenderDrawBucket @0x46d060 and has no such term for a
        // creature -- so every part that is not a tile part starts at 1 and only TileBuild lowers it. Left
        // out of this reset, an interior area's unexplored tile parts handed their 0 on to whatever took
        // their slots next: after the Inn-to-Ruins transition the player character drew as a flat
        // fog-coloured silhouette inside its own hover outline, every piece reporting a recipe, a range, a
        // seat and a non-zero index count.
        SceneDb.PartReveal[p] = 1f;
        // A dead light's latch would hand its animated multiplier (0 at the end of a fade) to the next light here.
        SceneDb.PartLitAnim[p] = 0;
    }

    /// <summary>Seed an object's feed columns. Same contract as <see cref="InitPart"/>.</summary>
    public static void InitObject(int o)
    {
        // A recycled slot keeps the last tenant's shell word: a record freed by the complete-object destructor
        // before the deleting one retired the shell never had it cleared, and a tile object inheriting it read a
        // dead shell's behaviour word at its first compose (crash in DbAnim.Kind on an area change).
        SceneDb.ObjFacade[o] = 0;
        // The part RANGE is the same tenancy hazard: a record whose tree is never built (an object minted with
        // no model, because CX could not read one) otherwise inherits the last tenant's first/count and poses,
        // picks and draws parts that belong to another object. PartTree writes the real range right after this,
        // so an empty one is the honest start.
        SceneDb.ObjPartFirst[o] = 0;
        SceneDb.ObjPartCount[o] = 0;
        SceneDb.ObjFlags[o] = ObjFlag.None;
        // ...and the scene: a record whose join is skipped otherwise points at the last tenant's scene.
        SceneDb.ObjScene[o] = default;
        SceneDb.ObjCrossScene[o] = default;
        SceneDb.ObjSceneKinds[o] = 0;
        SceneDb.ObjWorldJoinBy[o] = null;
        // ...and the owned-children list: inherited, the next tenant's scene moves drag the last one's pieces along.
        SceneDb.ObjSeamAppendHead[o] = default;
        SceneDb.ObjSeamAppendNext[o] = default;
        // ...and the texture rotation, for the same reason: a recycled slot that kept a quarter turn would
        // turn the next tenant's tile textures through an angle its own placement never asked for.
        SceneDb.ObjTexRotate[o] = 0;
        // ...and the run root: the last tenant's may be a creature it rode, whose OIT id the next one would share.
        SceneDb.ObjRunRoot[o] = SceneDb.Objects.IdAt(o);
        SceneDb.ObjLodShown[o] = 0;
        SceneDb.ObjSphere[o] = default;
        SceneDb.ObjTint[o] = Vector4.One;
        SceneDb.ObjEmissive[o] = Vector3.Zero;
        SceneDb.ObjVisible[o] = true;
        // The attach VARIANT is the same kind of tenancy: a recycled slot that kept a translate attach would
        // hand the next tenant's SetPositionOffset a yes the engine would have answered no to.
        SceneDb.ObjAttachMode[o] = 0;
        // ...and the hit columns, which outlive a tenancy the same way: a recycled slot kept the last
        // tenant's two fade alphas (a door that died mid-fade then reads as unpickable -- HitTest.Admits)
        // and its world box and sphere, which is a hit volume wherever the LAST object stood and a phantom
        // static over a point whose real floor is somewhere else. Degenerate is the honest start:
        // HitBounds.Refresh derives the real box off the Dirty.Created mark Alloc has just made.
        SceneDb.HitObjAlpha[o] = 1f;
        SceneDb.HitObjFadeAlpha[o] = 1f;
        SceneDb.HitObjClass[o] = 0;
        SceneDb.HitObjStatic[o] = false;
        SceneDb.HitObjBMin[o] = new Vector3(1f);
        SceneDb.HitObjBMax[o] = new Vector3(-1f);
        SceneDb.HitObjSphereC[o] = Vector3.Zero;
        SceneDb.HitObjSphereR[o] = 0f;
    }
}
