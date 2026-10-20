using CX.Alloy.Db;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Services.Camera;

namespace CX.Alloy;

/// <summary>BATCH BUILDING. Revalidating every Alloy part against the live scene, routing it to this pass's
/// class, sorting the blended remainder back-to-front, and staging the per-draw records and multidraw
/// commands the issue phase submits. Hot: nothing on this path allocates.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>This pass's draw ORDER, ahead of the staging walk: a tile that started fading leaves the
    /// opaque set for the blended one, the blended run is sorted back-to-front from THIS pass's eye, each
    /// entry carries its alpha, and each opaque class is PARTITIONED on the keyhole dissolve bit -- camera
    /// state the enrollment radix cannot sort. ponytail: insertion sort, O(n^2) in the small blended count.</summary>
    private static int RouteParts()
    {
        int n = 0, nb = 0, na = 0;
        PrepareVisible();
        OccludeFrame();   // #537: the hit fan this walk tags occluders from
        {
        for (int c = (int)AlloyClass.Opaque; c <= (int)AlloyClass.Cutout; c++)
        {
            int nd = 0;   // _orderTmp is the enrollment radix's scratch, idle during a pass
            int[] list = RouteRange(c, out int from, out int to);
            for (int k = from; k < to; k++)
            {
                if (!RouteLive(list[k], k, out int i, out IntPtr tri)) continue;
                bool samp = (k & (ChaseEvery - 1)) == 0;
                long tFade = samp ? FrameZones.Now : 0;
                // The fade overlay, and the part's own animated alpha with it: while its tile fades, an opaque
                // or cutout part IS a bucket-8 part -- the engine folds the tile's factor into the part's own
                // alpha for the length of the draw (RenderDrawBucket @0x46d060, bucket 8 only) and draws it
                // blended. So does this pass. Reading the part alpha live here (rather than trusting the class
                // from the last enrollment) is also what keeps a part whose alpha animated below 1 from standing
                // solid for up to an EnrollEvery cadence.
                // #328: the tile term is kept apart -- it is the one alpha term the shadow side refuses on (a
                // part fading by its OWN alpha still casts), and this is the frame's one chase of it.
                float fade = TileFade(i, tri);
                // #546: an unexplored tile (fog-of-war ramp at 0) is not drawn at all, as vanilla skips a
                // tile whose fade reached 0 (ProcessTriMeshParts @0x480d40 draws only a positive fade).
                if (fade <= 0f)
                {
                    Counters._skipFogTile++;
                    _oFading[i] = true; continue;
                }
                _oFading[i] = fade < 1f;
                // #537: a faded occluder is a translucent part like any other, so it takes the blended run too.
                // ...and the object's own fade (CNWCObject::FadeObject through SetColorShifting): a creature or
                // placeable the server adds or drops in perception range fades over 2 s, as vanilla's does
                float objAlpha = DbObjAlpha(_oDbPart[i]);
                if (objAlpha <= 0f) continue;
                float alpha = PartAlphaCached(i, tri) * fade * (1f - OccludeVerdict(i)) * objAlpha;
                bool dis = alpha >= 1f && Dissolves(i, tri, ref _stageCounters[0]);
                if (samp) FrameZones.AddSampled(FrameZones.Zone.Fade, tFade, ChaseEvery);
                if (alpha < 1f)
                { Counters._ownFade++; _blend[nb] = i; _blendAlpha[nb] = alpha; _blendKey[nb] = BlendKey(i); nb++; continue; }
                if (_oA2c[i] && c == (int)AlloyClass.Cutout) { _a2cTmp[na] = i; _a2cDis[na] = dis; na++; continue; }
                if (dis) { _orderTmp[nd++] = i; continue; }
                _pass[n] = i; _passCls[n] = _oCls[i]; _passAlpha[n] = 1f; _passDis[n] = false; n++;
            }
            for (int d = 0; d < nd; d++)
            { int i = _orderTmp[d]; _pass[n] = i; _passCls[n] = _oCls[i]; _passAlpha[n] = 1f; _passDis[n] = true; n++; }
        }
        // CX-189: the alpha-to-coverage cutouts close the cutout run, after its keyhole tail
        for (int a = 0; a < na; a++)
        { int i = _a2cTmp[a]; _pass[n] = i; _passCls[n] = (byte)AlloyClass.Cutout; _passAlpha[n] = 1f; _passDis[n] = _a2cDis[a]; n++; }
        AuditVisible(n);
        {
        int[] list = RouteRange((int)AlloyClass.Blended, out int from, out int to);
        for (int k = from; k < to; k++)
        {
            if (!RouteLive(list[k], k, out int i, out IntPtr tri)) continue;
            float alpha = LiveAlpha(i, tri);
            if (alpha <= 0f && _oTile[i] != IntPtr.Zero) continue;   // #546
            _blend[nb] = i;
            _blendAlpha[nb] = alpha;
            _blendKey[nb] = BlendKey(i); nb++;
        }
        }
        SortBlended(_blend, _blendKey, _blendAlpha, nb);
        // CX-273: the ground decals open the run, back to front among themselves, so the layer's writer draws one
        // contiguous range. CX-190: under OIT the rest is ordered by state, so it batches like the opaque range.
        bool free = OitPass.OrderFree;
        for (int a = 0; a < nb; a++)
        {
            int i = _blend[a];
            bool dis = Dissolves(i, _oTri[i], ref _stageCounters[0]);
            _sortComp[a] = BlendComp(BlendRank(i, dis, free), a, dis);
        }
        _sortComp.AsSpan(0, nb).Sort();
        for (int a = 0; a < nb; a++)
        {
            int from = CompPos(_sortComp[a]);
            _pass[n] = _blend[from]; _passCls[n] = (byte)AlloyClass.Blended; _passAlpha[n] = _blendAlpha[from];
            _passDis[n] = CompDis(_sortComp[a]); n++;
        }
        // #129: water, unsorted. ponytail: a per-part back-to-front sort is skipped because water planes are
        // per-tile, coplanar and depth-writing -- they cannot overlap each other in a way a sort would fix.
        // Add one if a map ever stacks a waterfall sheet in front of a pool.
        {
        int[] list = RouteRange((int)AlloyClass.Water, out int from, out int to);
        for (int k = from; k < to; k++)
        {
            if (!RouteLive(list[k], k, out int i, out IntPtr tri)) continue;
            float alpha = LiveAlpha(i, tri);
            if (alpha <= 0f && _oTile[i] != IntPtr.Zero) continue;   // #546
            _pass[n] = i; _passCls[n] = (byte)AlloyClass.Water;
            _passAlpha[n] = alpha;
            n++;
        }
        }
        {
        int[] list = RouteRange((int)AlloyClass.Additive, out int from, out int to);
        for (int k = from; k < to; k++)
        {
            if (!RouteLive(list[k], k, out int i, out IntPtr tri)) continue;
            // Additive fades too: the engine routes an additive part on a fading tile into bucket 8 like any
            // other, and IterSight suppresses it there, so a glow left at full brightness through a fade
            // would be ours to explain.
            float alpha = LiveAlpha(i, tri);
            if (alpha <= 0f && _oTile[i] != IntPtr.Zero) continue;   // #546
            _pass[n] = i; _passCls[n] = (byte)AlloyClass.Additive;
            _passAlpha[n] = alpha;
            n++;
        }
        }
        }
        return n;
    }

    /// <summary>The route-time validation contract, shared by all four class runs -- resolve the ordered slot,
    /// skip a vacated one, and REVOKE a failed identity, so every dereference the frame makes from here on
    /// sits behind one verdict and the staging walk does not re-check. Behind the FRUSTUM GATE too: a slot
    /// this pass cannot see keeps last frame's record untouched (see <see cref="RouteWanted"/>).</summary>
    private static bool RouteLive(int slot, int k, out int i, out IntPtr tri)
    {
        // The Route sample -- taken on the slots the gate rejects, which is the walk's O(world) half
        bool samp = (k & (ChaseEvery - 1)) == 0;
        long t0 = samp ? FrameZones.Now : 0;
        i = slot;
        tri = _oTri[i];
        if (tri == IntPtr.Zero || !RouteWanted(i))
        {
            if (samp) FrameZones.AddSampled(FrameZones.Zone.Route, t0, ChaseEvery);
            
            return false;
        }
        // The draw order is one table over every slot, so a world pass would otherwise walk every GUI panel's
        // controls too (measured: 405 of 1830). They all fail the frustum, so this costs work rather than
        // correctness -- but it is work every frame, and the scene is the cheapest possible test. Read live
        // rather than cached at seat time: an object can be re-parented between scenes.
        // CX-188: both verdicts off ONE object chase. They were a function each, and each re-read
        // _oDbPart[i], re-loaded SceneDb.PartObj[part] and re-asked DbTable.Valid for the same object: six
        // dependent loads at a scattered index per routed candidate where four answer the pair.
        int part = _oDbPart[i];
        DbId obj = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(obj) || SceneDb.ObjScene[obj.Index].Value != _dbPassScene.Value)
        { Counters._skipForeignScene++; SetCastOk(i, false); return false; }
        // CX-120: ...and the part's LOD ALTERNATE, 0 meaning "in every alternate".
        byte tree = SceneDb.PartLodTree[part];
        if (SceneDb.LodHidden(tree, SceneDb.ObjLodShown[obj.Index]))
        { Counters._skipLod++; SetCastOk(i, false); return false; }
        if (samp) FrameZones.AddSampled(FrameZones.Zone.Ident, t0, ChaseEvery);
        return true;
    }

    /// <summary>The cull's source list, one <see cref="CullPass.SrcUints"/>-uint record per DRAW of this pass,
    /// staged beside <c>_dCount/_dOff/_dBase/_dSlot</c> by the walk that already writes them: the draw's batch
    /// and that batch's output base are both known at this store, because the cut has just placed the draw.
    /// The four draw-args columns stay on the CPU -- <c>/alloy cull 0</c> falls back to CPU pointers.</summary>
    private static readonly CullSrcRec[] _dSrc = new CullSrcRec[MaxOwned];

    /// <summary>Off-device: the sort really is back-to-front, really is stable, and really carries each
    /// entry's alpha with its index -- a permutation that lost the alpha pairing would draw every blended
    /// part at its neighbour's transparency, which reads as "the sort is wrong" and is not.</summary>
    private static bool SortSelfCheck(ExtenderLog log)
    {
        int[] idx = { 10, 11, 12, 13 };
        float[] key = { 1f, 9f, 5f, 9f }, alpha = { 0.1f, 0.2f, 0.3f, 0.4f };
        SortBlended(idx, key, alpha, 4);
        bool ok = idx[0] == 11 && idx[1] == 13 && idx[2] == 12 && idx[3] == 10   // stable within the 9f tie
                  && key[0] == 9f && key[1] == 9f && key[2] == 5f && key[3] == 1f
                  && alpha[0] == 0.2f && alpha[1] == 0.4f && alpha[2] == 0.3f && alpha[3] == 0.1f;
        // ...and past InsertionMax, the keyed path: 40 entries keyed a % 5, so the expected order is key 4's
        // entries 4, 9, .. 39, then key 3's 3, 8, .. 38, and so on -- each tie in its original order.
        const int N = 40;
        int[] bi = new int[N]; float[] bk = new float[N], ba = new float[N];
        for (int a = 0; a < N; a++) { bi[a] = 100 + a; bk[a] = a % 5; ba[a] = a * 0.01f; }
        SortBlended(bi, bk, ba, N);
        for (int r = 0; r < N; r++)
        {
            int want = (4 - r / 8) + 5 * (r % 8);
            ok &= bi[r] == 100 + want && bk[r] == want % 5 && ba[r] == want * 0.01f;
        }
        if (!ok) log.Info("AlloyRenderer.SortSelfCheck: FAILED -- the blended order is not back-to-front");
        return ok;
    }

    /// <summary>Off-device: which blended draws carry an OIT object id, the run's order above distance in both modes,
    /// and the ghost curve. Oracle: hand-written rows, and the expected permutation of a run spelled out by hand.</summary>
    private static bool OitOrderSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.OitOrderSelfCheck");
        check.Expect(OitGrouped(objAlpha: 0.5f, decal: false, wash: 0), "a ghosted creature's part carries its object");
        check.Expect(OitGrouped(objAlpha: 0.999f, decal: false, wash: 0), "...however slightly it is faded");
        check.Expect(!OitGrouped(objAlpha: 1f, decal: false, wash: 0),
                     "a blended material on an unfaded object, creature or static, composites every layer");
        check.Expect(OitGrouped(objAlpha: 1f, decal: true, wash: WashWhole), "a ground decal keeps its object");
        check.Expect(OitGrouped(objAlpha: 1f, decal: false, wash: WashSplit), "a split wash's rest keeps its object");
        check.Expect(!OitGrouped(objAlpha: 1f, decal: false, wash: WashWhole), "a whole wash on a fading tile does not");
        check.Expect(GhostCurve(0.5f, 2f) == 0.75f && GhostCurve(0.25f, 1f) == 0.25f,
                     "a ghost draws as n layers of itself: half at two layers is three quarters, one layer is unchanged");
        check.Expect(GhostCurve(0f, 1.5f) == 0f && GhostCurve(1f, 1.5f) == 1f,
                     "fully invisible stays invisible and opaque stays opaque, so a fade has no step");

        // Positions are the back-to-front order; gob is an object index + 1. Entry 9 holds the widest terms.
        (bool decal, bool oitOut, int gob, bool dis, int rank)[] run =
        {
            (false, false, 0, false, 50), (true, false, 0, false, 900), (false, true, 0, false, 10),
            (false, false, 0, false, 20), (false, false, 7, false, 5), (true, false, 0, false, 3),
            (false, false, 0, true, 1), (false, true, 0, false, 400), (false, false, 7, false, 2),
            (false, false, SceneDb.MaxObjects, true, MaxOwned - 1),
        };
        string Order(bool free)
        {
            var comp = new ulong[run.Length];
            for (int a = 0; a < run.Length; a++)
                comp[a] = BlendComp(BlendRankOf(run[a].decal, free, run[a].oitOut, run[a].gob, run[a].dis, run[a].rank),
                                    a, run[a].dis);
            Array.Sort(comp);
            var s = new System.Text.StringBuilder();
            foreach (ulong c in comp) s.Append(CompPos(c)).Append(CompDis(c) ? "d " : " ");
            return s.ToString().TrimEnd();
        }
        string oit = Order(free: true), sorted = Order(free: false);
        check.Expect(oit == "1 5 3 0 6d 8 4 9d 2 7",
                     $"order-free: decals back to front, then object, keyhole bit and rank, then OIT's sit-outs back to front (got {oit})");
        check.Expect(sorted == "1 5 0 2 3 4 6d 7 8 9d", $"sorted: decals back to front, then the rest back to front (got {sorted})");
        ulong last = BlendComp(BlendRankOf(false, true, false, 0, false, 5), MaxOwned - 1, true);
        check.Expect(CompPos(last) == MaxOwned - 1 && CompDis(last)
                     && last < BlendComp(BlendRankOf(false, true, false, 0, false, 6), 0, false),
                     "the last run position and its keyhole bit fit under the rank");
        return check.Ok;
    }

    /// <summary>Off-device: the middle radix digit's buckets and the rank of its two bits. A key wider than
    /// <see cref="_sortCount"/> throws out of the enrollment sweep; a wrong RANK costs nothing visible, only
    /// one more lighting-rig re-send per class forever. The width is asserted against the array itself rather
    /// than against the constant it was sized from.</summary>
    private static bool CullSortSelfCheck(ExtenderLog log)
    {
        int unlitCulled = SortKeyOf(false, false, 0), unlitFree = SortKeyOf(true, false, MaxVdos - 1);
        int litCulled = SortKeyOf(false, true, 0), litMax = SortKeyOf(true, true, MaxVdos - 1);
        bool ok = litMax == _sortCount.Length - 1 && unlitCulled == 0 && unlitFree < litCulled;
        // #327: the (feature, primitive) digit fills its buckets exactly, and an out-of-table prim clamps.
        ok &= FeatPrimKeyOf(FeatCeiling, PrimTypes - 1) == _featPrimCount.Length - 1
              && FeatPrimKeyOf(FeatCeiling, PrimTypes + 5) == _featPrimCount.Length - 1
              && FeatPrimKeyOf(FeatCeiling + 3, 0) == _featPrimCount.Length - PrimTypes
              && FeatPrimKeyOf(0, -1) == 0 && FeatPrimKeyOf(0, 0) == 0;
        if (!ok) log.Info("AlloyRenderer.CullSortSelfCheck: FAILED -- a radix digit does not fit its " +
                          "buckets, or the cull bit outranks the lit one");
        return ok;
    }

    /// <summary>The packing <see cref="SortKey"/> applies, spelled without the tables so the check above can
    /// exercise it. LIT is the senior bit -- a program switch re-sends the whole lighting rig where face
    /// culling is one <c>glEnable</c> -- so a class orders (unlit culled, unlit two-sided, lit culled, lit
    /// two-sided): one program bind per class, at most two cull toggles.</summary>
    private static int SortKeyOf(bool noCull, bool lit, int vao)
        => ((lit ? 2 : 0) + (noCull ? 1 : 0)) * MaxVdos + vao;

    /// <summary>Entries in the engine's <c>primTypeTranslationTable</c> -- 0x1c bytes of uint per
    /// <c>nm -S</c>, indexed by the mdl node's prim type. The key clamps rather than trusts the index: the
    /// draw path reads the table unguarded, but a sort bucket must not.</summary>
    private const int PrimTypes = 7;
    /// <summary>The highest feature id a record carries -- the last content pair's -- named here because the sort table
    /// below is sized from it. A feature past this ceiling walks <c>_featPrimCount</c> off its end and throws
    /// mid-frame, so <see cref="CullSortSelfCheck"/> ties the table to it.</summary>
    private const int FeatCeiling = ShaderFeatures.FeatureCount - 1;
    /// <summary>Buckets for the (feature, primitive) digit.</summary>
    private const int FeatPrimKeys = (FeatCeiling + 1) * PrimTypes;
    /// <summary>Both indices are CLAMPED, not trusted: this is a sort digit, so a feature past the ceiling
    /// merely shares a bucket (one extra batch cut, never a wrong draw) where an unclamped one takes the
    /// frame down. The self-check is what makes the drift loud; the clamp is what makes it survivable.</summary>
    private static int FeatPrimKeyOf(int feat, int prim)
        => Math.Clamp(feat, 0, FeatCeiling) * PrimTypes + Math.Clamp(prim, 0, PrimTypes - 1);

    /// <summary>Order the blended run back to front -- the farthest key first, so nearer surfaces composite
    /// over what is already behind them. Stable, which keeps parts at equal distance in their texture-grouped
    /// enrollment order rather than shuffling them from pass to pass.</summary>
    private static void SortBlended(int[] idx, float[] key, float[] alpha, int n)
    {
        if (n <= InsertionMax)
        {
            for (int a = 1; a < n; a++)
            {
                int i = idx[a]; float k = key[a], al = alpha[a];
                int b = a - 1;
                for (; b >= 0 && key[b] < k; b--)
                { idx[b + 1] = idx[b]; key[b + 1] = key[b]; alpha[b + 1] = alpha[b]; }
                idx[b + 1] = i; key[b + 1] = k; alpha[b + 1] = al;
            }
            return;
        }
        // One ulong per entry: the key's bits flipped so an ascending sort runs far to near, over the entry's
        // position, which makes an unstable sort stable.
        for (int a = 0; a < n; a++)
        {
            uint u = BitConverter.SingleToUInt32Bits(key[a]);
            u = (u & 0x80000000u) != 0 ? u : ~u & 0x7FFFFFFFu;   // not-an-offset: float sign bit
            _sortComp[a] = ((ulong)u << 32) | (uint)a;
        }
        _sortComp.AsSpan(0, n).Sort();
        Array.Copy(idx, _sortIdx, n); Array.Copy(key, _sortKey, n); Array.Copy(alpha, _sortAlpha, n);
        for (int a = 0; a < n; a++)
        {
            int from = (int)(uint)_sortComp[a];
            idx[a] = _sortIdx[from]; key[a] = _sortKey[from]; alpha[a] = _sortAlpha[from];
        }
    }

    /// <summary>Bits of an enrollment rank or a run position, both below <see cref="MaxOwned"/>; an object id
    /// (index + 1) takes 17 more, for <see cref="SceneDb.MaxObjects"/>.</summary>
    private const int SlotBits = 18, GobBits = 17;
    private const int GroupShift = SlotBits + 1 + GobBits;
    private const ulong SlotMask = (1ul << SlotBits) - 1;

    /// <summary>The blended run's order above its back-to-front one. The ground decals open it. While OIT is
    /// order-free (<paramref name="free"/>) the rest goes by state -- object, keyhole bit, enrollment rank, the
    /// terms the batch cut keys on -- and what sits OIT out closes it, back to front for the sweep after the resolve.</summary>
    private static ulong BlendRankOf(bool decal, bool free, bool oitOut, int gob, bool dis, int rank)
        => decal ? 0ul
         : !free ? 1ul << GroupShift
         : oitOut ? 2ul << GroupShift
         : 1ul << GroupShift | (ulong)gob << (SlotBits + 1) | (dis ? 1ul << SlotBits : 0ul) | (ulong)rank;

    /// <summary><see cref="BlendRankOf"/> off slot <paramref name="i"/>'s columns.</summary>
    private static ulong BlendRank(int i, bool dis, bool free)
    {
        int part = _oDbPart[i];
        DbId root = RunRoot(i);
        int gob = SceneDb.Objects.Valid(root) && OitGrouped(DbObjAlpha(part), false, _oWash[i]) ? root.Index + 1 : 0;
        return BlendRankOf(_oDecal[i] != 0, free, SlotSitsOitOut(i), gob, dis, _orderRank[i]);
    }

    /// <summary><see cref="Issue.SitsOitOut"/> off slot <paramref name="i"/>'s columns.</summary>
    private static bool SlotSitsOitOut(int i)
        => Issue.SitsOitOut(Issue.HostedBody(_oLit[i], (int)_oFeat[i * 4]), _oSrc[i], _oDst[i]);

    /// <summary>One sort word: the rank, then the back-to-front position (unique, so the sort is stable), then the
    /// keyhole bit, which rides below the position and so never orders anything.</summary>
    private static ulong BlendComp(ulong rank, int pos, bool dis) => rank << (SlotBits + 1) | (ulong)pos << 1 | (dis ? 1ul : 0ul);
    private static int CompPos(ulong comp) => (int)(comp >> 1 & SlotMask);
    private static bool CompDis(ulong comp) => (comp & 1) != 0;

    /// <summary>Does a blended draw carry its run root as its OIT object id? When its object is faded as a whole --
    /// ghosting, the one case where an object must not show its own interior -- and for a ground decal or a split
    /// wash's rest, whose stacked sheets composite as one layer per object.</summary>
    internal static bool OitGrouped(float objAlpha, bool decal, byte wash) => objAlpha < 1f || decal || wash == WashSplit;

    /// <summary>The object alpha slot <paramref name="i"/> draws with. Where the list resolve composites a faded object
    /// once, its overlapping parts no longer thicken it, so it draws as <c>alloy.ghost</c> layers of itself.</summary>
    private static float GhostAlpha(int i, int cls, float objAlpha)
        => objAlpha < 1f && cls == (int)AlloyClass.Blended && OitPass.Dedupes && !SlotSitsOitOut(i)
           ? GhostCurve(objAlpha, Renderer.Ghost.V) : objAlpha;

    /// <summary><paramref name="a"/> as <paramref name="layers"/> layers of itself, 1 - (1 - a)^layers. 0 and 1 are
    /// fixed points, so a fade in or out stays continuous and a fully invisible object stays invisible.</summary>
    internal static float GhostCurve(float a, float layers) => 1f - MathF.Pow(1f - a, layers);

    /// <summary>Up to this many blended entries the insertion sort is cheaper than the keyed one; past it the
    /// insertion sort's quadratic walk over an unsorted route order cost milliseconds a frame.</summary>
    private const int InsertionMax = 32;
    private static readonly ulong[] _sortComp = new ulong[MaxOwned];
    private static readonly int[] _sortIdx = new int[MaxOwned];
    private static readonly float[] _sortKey = new float[MaxOwned], _sortAlpha = new float[MaxOwned];

    /// <summary>The scene this pass is drawing. Set by the world pass before the route walk; a slot of any
    /// other scene is skipped, which is what keeps one view from drawing another's objects.</summary>
    internal static DbId _dbPassScene;

    /// <summary>The object-wide alpha multiplier, which the engine reaches by chasing the gob's fade owner.
    /// One column load: the tint's w.</summary>
    private static float DbObjAlpha(int part) => SceneDb.ObjTint[SceneDb.PartObj[part].Index].W;

    /// <summary>The part's own alpha, off the scene DB's column: the part's <c>+0xa0</c>, which only an MDL
    /// alpha controller writes.</summary>
    private static float PartAlphaCached(int i, IntPtr tri) => SceneDb.PartAlpha[_oDbPart[i]];

    /// <summary>This frame's whole alpha multiplier for a part -- its own animated alpha, the fade factor of
    /// the tile it stands on, and the instance's two multiplies. Every term read live, none cached at
    /// capture: a gob can fade a whole creature into the transparent run for a single frame and out again.</summary>
    private static float LiveAlpha(int i, IntPtr tri)
        => SceneDb.PartAlpha[_oDbPart[i]] * SceneDb.PartFade[_oDbPart[i]] * DbObjAlpha(_oDbPart[i]);

    /// <summary>The blended sort key: a part sorts by its RUN ROOT's distance, so every part of one creature
    /// carries the same key and the stable sort lands them contiguous. A part whose root is gone (a rider
    /// outliving its creature) keeps its own centre.</summary>
    private static float BlendKey(int i)
    {
        // CX-120: a part sorts by its RUN ROOT's world position -- the object a fade and a blended run apply to,
        // resolved when attachment changed rather than climbed per part per pass.
        DbId root = RunRoot(i);
        if (!SceneDb.Objects.Valid(root)) return EyeDist2(i);
        Matrix4x4 w = SceneDb.ObjWorld[root.Index];
        float dx = w.M41 - _eyePos.X, dy = w.M42 - _eyePos.Y, dz = w.M43 - _eyePos.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>The blended batch key's run identity -- the run root as an opaque value, compared and never
    /// dereferenced by every reader. A creature's body parts and held items share one root, so one ghosted
    /// creature is one layer rather than one per part.</summary>
    private static IntPtr RunKey(int i) => (IntPtr)(long)RunRoot(i).Value;

    /// <summary>Slot <paramref name="i"/>'s object's run root (<see cref="SceneDb.ObjRunRoot"/>); stale once a
    /// rider outlives its creature, so a reader that dereferences it checks it first.</summary>
    private static DbId RunRoot(int i) => SceneDb.ObjRunRoot[SceneDb.PartObj[_oDbPart[i]].Index];

    private static bool CreaturePart(int i) => CreatureObj(i) >= 0;
    /// <summary>The creature object slot <paramref name="i"/> is a part of (its <see cref="SceneDb"/> index), or
    /// -1 when it is no creature's: what lets every part of one body be treated as one caster.</summary>
    internal static int SlotCreatureObj(int i) => (uint)i < (uint)_oGob.Length ? CreatureObj(i) : -1;
    private static int CreatureObj(int i)
    {
        // Up the ATTACHMENT chain: a creature's body parts, its hat and its held weapon are each their own object
        // and only the one the game called SetExternalDataType on carries the kind. Eight hops.
        DbId o = SceneDb.PartObj[_oDbPart[i]];
        for (int hop = 0; hop < 8 && SceneDb.Objects.Valid(o); hop++)
        {
            if ((SceneDb.ObjFlags[o.Index] & ObjFlag.Creature) != 0) return o.Index;
            o = SceneDb.ObjAttachTo[o.Index];
        }
        return -1;
    }

    private static float EyeDist2(int i)
    {
        // The recipe's centre is MODEL space (it comes off the mesh's own bounds), so it goes through the part's
        // world matrix -- the composed column, never a node read.
        Vector3 wc = Vector3.Transform(new Vector3(_oCx[i], _oCy[i], _oCz[i]), SceneDb.PartWorld[_oDbPart[i]]);
        float dx = wc.X - _eyePos.X, dy = wc.Y - _eyePos.Y, dz = wc.Z - _eyePos.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>One multidraw over draws <paramref name="first"/> .. +<paramref name="count"/> of the per-draw
    /// count, offset and base-vertex tables.</summary>
    internal static void MultiDrawRange(uint mode, int first, int count)
    {
        if (count <= 0) return;
        fixed (int* c = _dCount.AsSpan(first, count))
        fixed (nint* o = _dOff.AsSpan(first, count))
        fixed (int* b = _dBase.AsSpan(first, count))
            Gl.MultiDrawElementsBaseVertex(mode, c, GlUnsignedShort, (void**)o, count, b);
    }

    /// <summary>Store <paramref name="v"/> and say whether that changed anything. The whole persistent-record
    /// scheme is this compare: a field written every frame with the value it already held is not a mover, and
    /// the record it sits in does not need re-sending.</summary>
    private static bool St(ref float p, float v)
    {
        if (p == v) return false;
        p = v;
        return true;
    }

    /// <summary>The cached slot tids of a record that did not move. Still needed every frame -- a bind-path
    /// batch cuts on its textures, so the batch key reads them even when the record does not.</summary>
    private static void LoadSlotTids(int i, int* slotTid)
    {
        for (int s = 0; s < MapSlots; s++) slotTid[s] = _recTidS[i * MapSlots + s];
    }

    /// <summary>The record fields ADR 0017's split table calls PER-FRAME, gathered off the live scene by the
    /// staging walk and handed to <see cref="WritePerFrame"/>. A plain value struct, so no engine pointer
    /// reaches this far and the split is checkable off-device. Move a field into the stable half
    /// (<c>DbWriteStable</c>) and it reads as a frozen texture; <see cref="SplitSelfCheck"/> goes red.</summary>
    internal struct RecordFrameState
    {
        /// <summary>Matrix-class parts only -- creatures, doors, dangly, anything on a node. A part whose
        /// class stages identity has no live matrix to re-read, so it is stable by construction.</summary>
        internal bool HasModel;
        internal Matrix4x4 Model;
        internal float Dissolve, Alpha;
        internal bool Lit;
        internal float AmbR, AmbG, AmbB, DifR, DifG, DifB, EmiR, EmiG, EmiB, Slots;
        internal float FeatX, FeatY;
    }

    /// <summary>Stand-in radius for a caster whose engine bbox is degenerate: one NWN tile's horizontal
    /// diagonal (tiles are 10u square; WaterBodies.TileHalf is the same convention's other half).</summary>
    private const float DegenerateCasterRadius = 7.1f;

    /// <summary>Did a model matrix move, to a tenth of a millimetre? The engine rebuilds a placed gob's matrix
    /// from its quaternion, so bit-exact inequality reports a static area's parts as movers every frame --
    /// each a record re-send, a moving-caster page under its light, a hot light on every baked fragment in
    /// reach. ponytail: a slower drift accumulates and is written once it crosses the step, never lost.</summary>
    private const float MoveStep = 1e-4f;
    /// <summary>The caster's step: 1 cm of translation, or 0.01 of a rotation element (0.6 degrees). ponytail: the
    /// compare is against the record, which follows at the finer step, so a creep under 1 cm a frame never
    /// re-bakes its shadow; a last-baked matrix per caster is the upgrade if a pushed crate ever needs it.</summary>
    private const float CasterStep = 1e-2f;
    private static bool Moved(in Matrix4x4 a, in Matrix4x4 b, float step)
    {
        fixed (Matrix4x4* pa = &a, pb = &b)
        {
            float* x = (float*)pa, y = (float*)pb;
            for (int k = 0; k < 16; k++) if (MathF.Abs(x[k] - y[k]) > step) return true;
        }
        return false;
    }

    /// <summary>The per-frame half of the record, compared into place; returns whether anything actually
    /// moved, which is the whole scheme. <c>prev</c> (floats 44..59) is LAST FRAME's model:
    /// <paramref name="roll"/> says this is the record's first build this frame, and <paramref name="fresh"/>
    /// seeds prev = model so a new tenant never carries the last tenant's matrix as its history.</summary>
    private static bool WritePerFrame(ref DrawRecordBlock rec, in RecordFrameState f, bool roll = true, bool fresh = false)
    {
        bool m = false;
        if (f.HasModel)
        {
            ref Matrix4x4 model = ref rec.Model;
            ref Matrix4x4 prev = ref rec.Prev;
            if (Moved(in model, in f.Model, MoveStep))
            {
                if (roll) prev = model;
                model = f.Model; m = true;
            }
            else if (roll && prev != model) { prev = model; m = true; }
            if (fresh) prev = f.Model;
        }
        m |= St(ref rec.Dissolve, f.Dissolve) | St(ref rec.Alpha, f.Alpha);
        if (f.Lit)
        {
            m |= St(ref rec.AmbR, f.AmbR) | St(ref rec.AmbG, f.AmbG) | St(ref rec.AmbB, f.AmbB)
                 | St(ref rec.EmiR, f.EmiR) | St(ref rec.EmiG, f.EmiG) | St(ref rec.EmiB, f.EmiB)
                 | St(ref rec.Slots, f.Slots);
            m |= St(ref rec.DifR, f.DifR) | St(ref rec.DifG, f.DifG) | St(ref rec.DifB, f.DifB);
        }
        m |= St(ref rec.FeatX, f.FeatX) | St(ref rec.FeatY, f.FeatY);
        return m;
    }

    /// <summary>Stable-record writes by emi.w class since arming (creature / object / tileset), for the weather line.</summary>
    internal static int RecCreature, RecObject, RecTile;

    /// <summary>Off-device, the split itself: every field of <see cref="RecordFrameState"/> is perturbed on
    /// its own and must make the record a mover, and a record re-written with the values it already holds must
    /// not. Move one out of <see cref="WritePerFrame"/> into <c>DbWriteStable</c> and its perturbation
    /// stops being seen -- a fading tile stuck at full opacity, with nothing else to say so.</summary>
    private static bool SplitSelfCheck(ExtenderLog log)
    {
        var rec = new DrawRecordBlock();
        var f = default(RecordFrameState);
        f.HasModel = true; f.Model = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        f.Dissolve = 1f; f.Alpha = 0.5f; f.Lit = true;
        f.AmbR = 0.1f; f.AmbG = 0.2f; f.AmbB = 0.3f;
        f.DifR = 0.4f; f.DifG = 0.5f; f.DifB = 0.6f;
        f.EmiR = 0.7f; f.EmiG = 0.8f; f.EmiB = 0.9f;
        f.Slots = 3f; f.FeatX = 5f; f.FeatY = 6f;
        bool ok;
        {
            ok = WritePerFrame(ref rec, in f, fresh: true) && !WritePerFrame(ref rec, in f);
            if (!ok) log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- a record is a mover every frame, or "
                              + "never one; either way the persistent-record scheme is not running");
            // one field at a time, restored after each, so a miss names itself rather than being masked by
            // the next perturbation
            ok &= Perturb(ref rec, ref f, ref f.Dissolve, log, "rot.z dissolve")
                  & Perturb(ref rec, ref f, ref f.Alpha, log, "rot.w alpha")
                  & Perturb(ref rec, ref f, ref f.AmbR, log, "amb.r")
                  & Perturb(ref rec, ref f, ref f.AmbG, log, "amb.g")
                  & Perturb(ref rec, ref f, ref f.AmbB, log, "amb.b")
                  & Perturb(ref rec, ref f, ref f.DifR, log, "dif.r")
                  & Perturb(ref rec, ref f, ref f.DifG, log, "dif.g")
                  & Perturb(ref rec, ref f, ref f.DifB, log, "dif.b")
                  & Perturb(ref rec, ref f, ref f.EmiR, log, "emi.r")
                  & Perturb(ref rec, ref f, ref f.EmiG, log, "emi.g")
                  & Perturb(ref rec, ref f, ref f.EmiB, log, "emi.b")
                  & Perturb(ref rec, ref f, ref f.Slots, log, "pbr.w live slot mask")
                  & Perturb(ref rec, ref f, ref f.FeatX, log, "feat.x")
                  & Perturb(ref rec, ref f, ref f.FeatY, log, "feat.y bone base");
            // ...and the matrix, which is the one the ticket names first: a mover the split does not know
            // about is a frozen record, and it reads as correct until the thing moves.
            Matrix4x4 was = f.Model;
            f.Model = Matrix4x4.CreateTranslation(9f, 9f, 9f);
            bool mm = WritePerFrame(ref rec, in f);
            // #392: the frame the matrix moved, prev holds where it WAS; the next build (nothing moved) rolls
            // prev up to the model and re-sends once; the one after that is quiet.
            bool prevLag = rec.Prev == was && rec.Model == f.Model;
            bool catchUp = WritePerFrame(ref rec, in f) && rec.Prev == f.Model && !WritePerFrame(ref rec, in f);
            // ...and a second build in the SAME frame (roll: false) leaves prev where the first put it.
            f.Model = Matrix4x4.CreateTranslation(3f, 3f, 3f);
            WritePerFrame(ref rec, in f);
            Matrix4x4 held = rec.Prev;
            bool sameFrame = !WritePerFrame(ref rec, in f, roll: false) && rec.Prev == held;
            f.Model = was;
            WritePerFrame(ref rec, in f, fresh: true);
            if (!mm) log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- a moved model matrix does not "
                              + "re-send its record; every creature and door would freeze where it stood");
            if (!prevLag || !catchUp || !sameFrame)
                log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- prev is not last frame's model (#392): a stopped "
                         + "door would report motion forever, or a mover would lose its vectors");
            ok &= mm && prevLag && catchUp && sameFrame;
        }
        return ok;
    }

    private static bool Perturb(ref DrawRecordBlock rec, ref RecordFrameState f, ref float field, ExtenderLog log, string name)
    {
        float was = field;
        field = was + 1f;
        bool seen = WritePerFrame(ref rec, in f);
        field = was;
        WritePerFrame(ref rec, in f);
        if (!seen)
            log.Info($"AlloyRenderer.SplitSelfCheck: FAILED -- {name} is not a per-frame field any more; "
                     + "a record that stops carrying it renders stale state and says nothing");
        return seen;
    }

    /// <summary>This slot's texture caches advanced but its record never did -- the walk skipped out between
    /// the two. Un-stamp it, so the part rebuilds from scratch when it comes back: otherwise the diffuse
    /// pointer compares EQUAL to the cache on its return and the record keeps the previous texture's handles
    /// for as long as the part is owned, with nothing in the log.</summary>
    private static void UnstampRecord(int i)
    {
        _gate[i].RecGen = 0;   // 0 = never built; the epoch starts at 1
        // CX-188: and the texture memo with it -- a re-tenanted slot's handles and tids describe its
        // predecessor, and this is the one signal that _oDbRecipe may have been re-pointed under them.
        _recTexStamp[i] = 0;
        UnstampChase(i);
    }

    /// <summary>This record moved and has to reach the GPU again. Overflow is not a silent drop -- it promotes
    /// the frame to a wholesale upload, which is correct and merely more expensive.</summary>
    private static void MarkRecordDirty(int slot) => MarkRecordDirty(slot, ref _stageCounters[0]);

    private static void MarkRecordDirty(int slot, ref StageCounters c)
    {
        c.RecWritten++;
        // reserve-then-write: workers append concurrently
        int n = System.Threading.Interlocked.Increment(ref _recDirtyN) - 1;
        if (n < _recDirty.Length) _recDirty[n] = slot;
        else _recDirtyAll = true;
    }

    /// <summary>Every persistent record is suspect -- rebuild them all on the next frame that draws. The edges
    /// that need it are the ones no per-part compare can see: a bindless handle wipe or context loss (a record
    /// holding a dead handle samples whatever took its name), and the debug toggles that change what a record
    /// means. Enrolment is NOT one of them: <see cref="UnstampRecord"/> invalidates a re-tenanted slot.</summary>
    internal static void InvalidateRecords() => System.Threading.Interlocked.Increment(ref _recEpoch);
                                              // Interlocked: /alloy runs on the input thread

    /// <summary>One routed part in this many carries the <c>Zone.Chase</c> timestamps. Power of two.</summary>
    internal const int ChaseEvery = 16;

    /// <summary>Phase A of the Alloy block: walk this pass's order (<see cref="RouteParts"/>), revalidate every
    /// part, and stage the survivors into the multidraw argument arrays plus one per-draw SSBO record each,
    /// cutting a batch whenever the (class, program, VDO, texture, primitive, blend) key changes. Primitive
    /// mode is in the key because one multidraw carries a single mode. Returns the batch count.</summary>
    private static int BuildBatches(bool lightingOn)
    {
        int groups = 0, n = 0;
        int keyVao = -1, keyTex = -1, keyFeat = -1, keyCls = -1, keyBlend = -1;
        uint keyMode = 0;
        bool keyLit = false, keyDis = false, keyNoCull = false, keyA2c = false, keyDecal = false;
        IntPtr keyGob = IntPtr.Zero;
        // #322 step 1: attribute the Build monolith before drawing any phase boundary -- Scan is the
        // routing walk, Restage everything after it. No behaviour change; the [zones] line gains the split.
        long tScan = FrameZones.Now;
        if (_stageThreads > 0) StagePool.Prewake(Math.Min(_stageThreads, StagePool.MaxChunks - 1) + 1);
        int routed = RouteParts();
        FrameZones.Add(FrameZones.Zone.Scan, tScan);
        long tRestage = FrameZones.Now;
        bool dumping = FrameDump.Begin();
        _gCutoutStart = _gOpaqueEnd = _gBlendEnd = _gWaterEnd = _gA2cStart = 0;
        _mvN = 0; _mvSkinned = false; _mvDangly = false;   // #392: this pass's mover list, whether or not a velocity pass consumes it
        // #242: the persistent buffer grows to the high-water ownership count and never shrinks. A growth
        // reallocates it, so everything in it goes back up on this frame rather than only the movers.
        if (_ownCount > _recOwnCap) { _recOwnCap = _ownCount; _recDirtyAll = true; }
        // #120: this part's live slot-1..5 tids. Declared ONCE, outside the loop -- a stackalloc per
        // iteration grows the frame by the part count.
        int* slotTid = stackalloc int[MapSlots];
        // #287: the cull's bounds diagnostic, measured by the walk that already touches every sphere it
        // covers, rather than by a second walk of the staged list.
        float lox = float.MaxValue, loy = float.MaxValue, loz = float.MaxValue;
        float hix = float.MinValue, hiy = float.MinValue, hiz = float.MinValue, maxR = 0f;
        long tStage = FrameZones.Now;
        BeginCasterTouched();
        int chunks = Math.Min(_stageThreads, StagePool.MaxChunks - 1) + 1;
        _stageRouted = routed; _stageLighting = lightingOn;
        // CX-188: one read for the phase -- see _stageTexSerial.
        _stageTexSerial = TexRegistry.TableSerial;
        // ...and the instrument for the skip it gates: a serial that moves every frame re-resolves the whole
        // table every frame, which is the skip bought back. The period line carries both numbers.
        if (_stageTexSerial != _texSerialWas) { _texSerialMoves++; _texSerialWas = _stageTexSerial; }
        {
            if (chunks > 1 && StagePool.Run(chunks, _log))
            {
                for (int k = 0; k < routed; k++)
                    if (_pkVerdict[k] == Deferred)
                    { FrameZones.Count(FrameZones.Ev.Deferred); _pkVerdict[k] = StagePart(k, slotTid, lightingOn, ref _stageCounters[0], main: true); }
            }
            else
            {
                // A plain field write, not a Set: no Changed, no cfg write -- the stand-down is this session's.
                if (chunks > 1) { Renderer.Threads.V = 0; _log?.Info("AlloyRenderer: /alloy threads stood down by a StagePool fault; the frame re-staged serially (set /alloy threads again to re-arm)"); }
                for (int k = 0; k < routed; k++) _pkVerdict[k] = StagePart(k, slotTid, lightingOn, ref _stageCounters[0], main: true);
            }
        }
        for (int w = 0; w < _stageCounters.Length; w++) FoldCounters(ref _stageCounters[w]);
        if (chunks > 1) FrameZones.Add(FrameZones.Zone.Chase, tStage);   // threaded: the whole stage phase stands in for the sampled chase
        // BW2: on a cadence (and every frame under /alloy poll 1), rebuild every staged row the way the cut
        // used to gather it and count the disagreements. The rows decide what is drawn, so this is the check
        // that the move was a move.
        if (_fullPoll || (_frameSeq & (BatchAuditEvery - 1)) == 0) AuditBatchRows(routed);
        for (int k = 0; k < routed; k++)
        {
            if (_pkVerdict[k] != Staged) continue;
            // BW2: one dense row per staged draw, written by the (parallel) stage walk. Every key term and
            // every resolved draw argument below used to be gathered here from ~20 columns at a scattered
            // ownership index.
            ref BatchRow row = ref _bRow[k];
            int i = row.Slot;
            int cls = row.Cls;
            bool lit = row.Lit;
            bool dis = row.Dis;
            int tid = row.Tid, slots = row.Slots;
            int idxCount = row.IdxCount;
            int v = row.Vao;
            uint mode = row.Mode;
            // Automatic mesh LOD: the level came off the row (arithmetic on the cull's own sphere); the RANGE
            // is looked up here because a generated run lives in MeshLod's own element buffer, so the level
            // rides the slot index (LodVaoBit) -- that is what cuts the batch, because one multidraw cannot
            // straddle two element buffers -- and because both tables are main-thread only.
            nint idxOff = row.IdxOff;
            int baseVertex = row.BaseVertex;
            int vKey = v;
            int lvl = row.Lvl;
            // #673: a charted mesh is the whole geometry (split vertices), picked at every level -- see MeshChart.Range.
            bool chartArmed = MeshChart.Armed;
            int drawn = 0;
            // CX-120: the arena IS the chart and the LOD table. StageDbPart already asked it for this
            // level's runs, so there is nothing to look up here -- and both lookups below are keyed on a
            // (VDO slot, item) pair a DB slot does not have.
            vKey = v | DbArenaVaoBit;
            // `drawn` is the LEVEL the arena actually served, not a count: MeshLod.Drew indexes its
            // per-level tally with it. Handing it the index count walked off that array and took the
            // first DB frame that got this far down with it.
            drawn = _oDbLevel[i];
            // CX-120: ...and the CHARTED copy of that mesh when one exists, which is what lets the bake
            // enrol the slot. It belongs here rather than in the stage walk for the reason the comment
            // above gives about the other two lookups: this loop is main-thread, the stage walk is not,
            // and both the chart table and the bake's rect tables are main-thread only.
            // CX-50: ...and the plain range's own coarser run where no chart answered. The stage resolved the
            // BASE run (it runs on a worker, before the record block makes this frame's sphere current), so
            // the level the row earned is applied HERE, where the charted copy's already was -- which is why
            // every uncharted DB part drew full detail at every distance while the tally said level 0.
            // DrawRecipe.Range is a raw array index, so the recipe is read and guarded once for both.
            int dbRecipe = _oDbRecipe[i];
            DbRuns(i, dbRecipe >= 0 ? Db.DrawRecipe.Range(dbRecipe) : -1, lvl, chartArmed,
                   ref idxOff, ref baseVertex, ref idxCount, ref drawn);
            if (MeshLod.On) MeshLod.Drew(drawn, idxCount);
            _oLodBit[i] = DbArenaVaoBit;
            // #123: the feature joins the batch key. Not for binding -- it rides in the per-draw record --
            // but so the depth prepass can drop a whole batch: a displacing feature must not write its
            // undisplaced depth, and the prepass has no per-draw granularity. #137: a posed skinmesh overrides
            // the content feature (the row applies that), which is what lets a whole batch be skinned or not.
            int feat = row.Feat;
            // #124: ...and the dissolve bit (dis, above) joins it for the same reason -- the keyhole stage discards.
            // #126: the class and the blend pair are GL state, which one multidraw cannot vary inside.
            int blend = row.Blend;
            // #292: ...and so is face culling, for exactly the same reason. Sorted under the lit bit
            // (SortKeyOf), so this cuts at most twice per class rather than once per interleaved part.
            bool noCull = row.NoCull;
            // The OIT object id is one uniform per batch, so it is a key term here and a tag on the batch.
            IntPtr gobKey = row.GobKey;
            // CX-189: an alpha-to-coverage cutout draws in its own layer, so it never shares a batch
            bool a2c = cls == (int)AlloyClass.Cutout && _oA2c[i];
            // CX-273: ...and a ground decal draws in the layer's own pass, so it is a key term too
            bool decal = row.Decal;
            // #153: under bindless a LIT batch no longer splits on its textures -- the handles ride the
            // record, so one multidraw spans every texture in the class. Feat/dis stay in the key only for
            // the depth prepass's whole-batch drop; unlit parts keep the bind path and the full key.
            bool merge = _bindless && lit && cls != (int)AlloyClass.Water;
            int tidKey = merge ? -2 : tid;
            bool sameSlots = groups != 0 && merge;
            if (!merge)
            {
                // CX-188: the map tids are gathered only for the batches that can CUT on them. A merged lit
                // batch samples its maps off the handle block per draw, so Issue binds none of them
                // (`handles` there is this same predicate) and _gTexN's row is dead state -- where the gather
                // was six scattered loads and the compare six more, per draw, for every lit draw in the frame.
                LoadSlotTids(i, slotTid);
                sameSlots = groups != 0;
                for (int s = 0; sameSlots && s < MapSlots; s++)
                    sameSlots = _gTexN[(groups - 1) * MapSlots + s] == slotTid[s];
            }
            if (!sameSlots || lit != keyLit || vKey != keyVao || tidKey != keyTex || mode != keyMode
                || feat != keyFeat || dis != keyDis || cls != keyCls || blend != keyBlend
                || noCull != keyNoCull || gobKey != keyGob || a2c != keyA2c || decal != keyDecal)
            {
                // #292 (AC6): what the cull term cost. Counted as the batches it OPENED on its own -- every
                // other key term agreed and only the culling state differed -- so the line reports the split
                // this ticket added rather than the whole batch count, which no baseline would separate.
                if (groups != 0 && noCull != keyNoCull && sameSlots && lit == keyLit && vKey == keyVao
                    && tidKey == keyTex && mode == keyMode && feat == keyFeat && dis == keyDis
                    && cls == keyCls && blend == keyBlend) Counters._cullCuts++;
                // #327: which term opened an opaque-range cut -- one counter per term, a cut that flipped
                // several charged to each. The instrument the sort above is judged by.
                if (groups != 0 && cls == keyCls && cls <= (int)AlloyClass.Cutout)
                {
                    if (vKey != keyVao) Counters._cutBy[0]++;
                    if (feat != keyFeat) Counters._cutBy[1]++;
                    if (dis != keyDis) Counters._cutBy[2]++;
                    if (blend != keyBlend) Counters._cutBy[3]++;
                    if (noCull != keyNoCull) Counters._cutBy[4]++;
                    if (mode != keyMode) Counters._cutBy[5]++;
                    if (lit != keyLit) Counters._cutBy[6]++;
                    if (tidKey != keyTex || !sameSlots) Counters._cutBy[7]++;
                }
                _gStart[groups] = n; _gLen[groups] = 0;
                _gLit[groups] = lit; _gVao[groups] = vKey; _gTex[groups] = tid; _gMode[groups] = mode;
                _gFeat[groups] = feat; _gDis[groups] = dis;
                _gCls[groups] = (byte)cls; _gSrc[groups] = row.Src; _gDst[groups] = row.Dst; _gWash[groups] = row.Wash;
                _gNoCull[groups] = noCull; _gGob[groups] = gobKey; _gA2c[groups] = a2c;
                _gDecal[groups] = decal;
                if (!merge) for (int s = 0; s < MapSlots; s++) _gTexN[groups * MapSlots + s] = slotTid[s];
                groups++;
                keyLit = lit; keyVao = vKey; keyTex = tidKey; keyMode = mode; keyFeat = feat; keyDis = dis;
                keyCls = cls; keyBlend = blend; keyNoCull = noCull; keyGob = gobKey; keyA2c = a2c;
                keyDecal = decal;
            }
            
            _dCount[n] = idxCount;
            _dOff[n] = idxOff;
            _dBase[n] = baseVertex;
            // #242: the record this draw reads lives at the OWNERSHIP slot and outlives the frame; the draw
            // order does not, so the draw carries the slot rather than the record.
            _dSlot[n] = i;
            // #392: a matrix mover this frame -- its record's prev lags its model by one frame, and the velocity
            // stage redraws it with both. CX-26: a posed skinmesh is one too when its palette changed, and the
            // stage skins it with both palettes. Opaque only: a displaced mesh moves under a matrix that does
            // not, and a cutout mover would paint its own velocity over its holes.
            if (row.Moved && cls == (int)AlloyClass.Opaque && !ShaderFeatures.OutOfPrepass(feat))
                NoteMoverDraw(n, row.Posed, row.Dangly);
            // #287: and the same draw as the cull's source record -- the batch is the one the cut just
            // opened or extended, and its output base is where its compacted run starts.
            CullPass.Pack(ref _dSrc[n], idxCount, _dOff[n], _dBase[n], i, groups - 1, _gStart[groups - 1]);
            // #287: ...and after the record block, so the extent is measured on the sphere this frame wrote.
            if (cls <= (int)AlloyClass.Cutout)
            {
                ref Sphere sp = ref _dSph[i];
                if (sp.Radius < NeverCull)
                {
                    lox = MathF.Min(lox, sp.Center.X); hix = MathF.Max(hix, sp.Center.X);
                    loy = MathF.Min(loy, sp.Center.Y); hiy = MathF.Max(hiy, sp.Center.Y);
                    loz = MathF.Min(loz, sp.Center.Z); hiz = MathF.Max(hiz, sp.Center.Z);
                    maxR = MathF.Max(maxR, sp.Radius);
                }
            }
            if (FrameDump.Capturing)
                FrameDump.Row_(cls, lit, v, _oDbRecipe[i], mode, feat, dis, blend, noCull, slots, merge,
                               idxCount, _dOff[n], _dBase[n], in _dData[i]);
            _gLen[groups - 1]++;
            n++;
            if (noCull) Counters._twoSidedDraws++;
            if (row.HasGob) Counters._drawsGob++;
            // #302: no shadow feed here any more. An instance part IS an ownership slot, and the bake walks
            // the ownership table -- so there is nothing to hand over and nothing that can be double-recorded.
        }
        // #126: where each pass's batch range ends. Read off the batches themselves rather than tracked
        // through the cut above -- the routed order is class-ordered by construction, so the first batch of a
        // later class IS the previous range's end, and a class that staged nothing leaves an empty range
        // rather than someone else's batches.
        if (hix >= lox) CullPass.SetSpread(hix - lox, hiy - loy, hiz - loz, maxR);
        _gCutoutStart = _gOpaqueEnd = _gBlendEnd = _gWaterEnd = groups;
        for (int g = 0; g < groups; g++)
        {
            if (_gCls[g] > (byte)AlloyClass.Opaque && g < _gCutoutStart) _gCutoutStart = g;
            if (_gCls[g] > (byte)AlloyClass.Cutout && g < _gOpaqueEnd) _gOpaqueEnd = g;
            if (_gCls[g] > (byte)AlloyClass.Blended && g < _gBlendEnd) _gBlendEnd = g;
            if (_gCls[g] > (byte)AlloyClass.Water && g < _gWaterEnd) _gWaterEnd = g;
        }
        _gA2cStart = _gOpaqueEnd;
        while (_gA2cStart > _gCutoutStart && _gA2c[_gA2cStart - 1]) _gA2cStart--;
        _drawN = n;
        if (dumping) FrameDump.Finish(groups, _log);
        FrameZones.Add(FrameZones.Zone.Restage, tRestage);
        return groups;
    }

    /// <summary>Everything the serial batch-cut walk needs about ONE staged draw, in rank order:
    /// <see cref="StagePart"/> already visits the slot (and in parallel), so it writes the row and the cut
    /// streams it rather than gathering ~20 columns at a scattered index. Written at the END of StagePart,
    /// after the record block; <see cref="AuditBatchRows"/> rebuilds every field the old way on a cadence.</summary>
    private struct BatchRow
    {
        public int Slot, Entry, IdxCount, BaseVertex, Vao, Item, Tid, Slots, Feat, Blend, Lvl;
        public nint IdxOff;
        public uint Mode;
        public IntPtr GobKey;
        public byte Cls, Src, Dst, Wash;
        public bool Lit, Dis, NoCull, HasGob, Moved, Posed, Dangly, Decal;
    }
    /// <summary>One row per ROUTED rank, so the ownership table's size is the bound (a pass with no visible set
    /// walks the whole order). ~80 bytes x <see cref="MaxOwned"/> = 13 MB, which is a quarter of what the record
    /// block beside it already holds (<c>_dData</c> is 72 floats per slot, 47 MB) and is the same trade every
    /// other per-slot column here makes: the walk must not allocate, so its worst case is resident.</summary>
    private static readonly BatchRow[] _bRow = new BatchRow[MaxOwned];

    /// <summary>BW2: fill rank <paramref name="k"/>'s row. Per-slot reads only, so it runs on whichever worker
    /// staged the part; the terms it derives are the cut's own expressions, moved not changed.</summary>
    private static void WriteBatchRow(int k, int i, int cls, bool lit, bool dis, int tid, int slots,
                                      int idxCount, int feat)
    {
        ref BatchRow row = ref _bRow[k];
        row.Slot = i; row.Entry = _oEntry[i]; row.Cls = (byte)cls;
        row.Lit = lit; row.Dis = dis; row.Tid = tid; row.Slots = slots;
        row.IdxCount = idxCount; row.IdxOff = _cOff[i]; row.BaseVertex = _cBase[i];
        // CX-120 (R1-18): a DB slot's VAO key is its ARENA SECTION, so the two vertex formats cut apart and
        // every range of one format batches together -- where _oVao = 0 for all of them would have collapsed
        // the key and _oItem = -1 would have collided every (vao, item) table on one entry.
        bool db = _oDbPart[i] >= 0;
        row.Vao = db ? DbVaoKey(i) : _oVao[i];
        row.Item = db ? _oDbPart[i] : _oItem[i];
        row.Feat = feat;
        row.Mode = db ? Gl.TRIANGLES : _primTable[_oPrim[i]];
        row.NoCull = _oNoCull[i];
        row.Src = (byte)_oSrc[i]; row.Dst = (byte)_oDst[i]; row.Wash = (byte)_oWash[i];
        // #126: the class and the blend pair are GL state, which one multidraw cannot vary inside -- and so is a
        // wash's polygon offset, in any class
        row.Blend = (_oWash[i] << 16) | (AlloyClasses.Blends(cls) ? (_oSrc[i] << 8) | _oDst[i] : 0);
        // The OIT object id is one uniform per batch, so it is a key term and a tag on the batch.
        row.GobKey = cls == (int)AlloyClass.Blended && OitGrouped(DbObjAlpha(_oDbPart[i]), _oDecal[i] != 0, _oWash[i])
                   ? RunKey(i) : IntPtr.Zero;
        row.Decal = cls == (int)AlloyClass.Blended && _oDecal[i] != 0;
        // "has an instance behind it", which in DB mode is "is not tileset geometry".
        row.HasGob = db ? (SceneDb.ObjFlags[SceneDb.PartObj[_oDbPart[i]].Index] & ObjFlag.Tile) == 0
                        : _oGob[i] != IntPtr.Zero;
        // Automatic mesh LOD: the screen radius of the sphere the cull already needs. The RANGE lookup stays on
        // the main thread (a dictionary and a shared counter); only the level is arithmetic.
        int lvl = 0;
        if (MeshLod.On && _lodScale > 0f)
            {
                ref Sphere lsp = ref _dSph[i];
                if (lsp.Radius < NeverCull)
                {
                    float dx = lsp.Center.X - _eyePos.X, dy = lsp.Center.Y - _eyePos.Y, dz = lsp.Center.Z - _eyePos.Z;
                    lvl = MeshLod.Level(lsp.Radius, MathF.Sqrt(dx * dx + dy * dy + dz * dz), _lodScale);
                }
            }
        row.Lvl = lvl;
        // #392: a matrix mover this frame -- its record's prev lags its model by one frame, and the velocity
        // stage redraws it with both. CX-26: a posed skinmesh is one too when its palette changed.
        bool posed = _oBone[i] >= 0, dangly = !posed && DanglyPrevNow(i), recMoved = _recMovedAt[i] == _frameSeq;
        row.Posed = posed; row.Dangly = dangly;
        row.Moved = posed ? recMoved || _poseMovedAt[i] == _frameSeq : dangly || recMoved && _oMx[i];
    }

    /// <summary>BW2: one frame in this many rebuilds every staged <see cref="BatchRow"/> the old way and counts
    /// the disagreements. Power of two.</summary>
    private const int BatchAuditEvery = 64;
    private static long _bAudited, _bMismatch;
    private static int _bLogged;

    /// <summary>BW2: the row against the columns it was gathered from. A mismatch is a draw the cut would have
    /// issued differently -- a wrong index range, a batch that should have been cut and was not -- so the first
    /// few are named and the rest counted on the period line, which must read 0.</summary>
    private static void AuditBatchRows(int routed)
    {
        for (int k = 0; k < routed; k++)
        {
            if (_pkVerdict[k] != Staged) continue;
            BatchRow was = _bRow[k];
            int i = _pass[k];
            if (was.Slot != i) { BatchMismatch(k, i, "slot"); continue; }
            // CX-120: the audit rebuilds a row the way the ENGINE path builds one -- off _oEntry, the census
            // and the engine prim table, none of which a DB slot has. It reported a mismatch on every DB row
            // and would have gone on doing so for ever, which is worse than no audit: a check that is always
            // red is a check nobody reads. It needs a DB-mode rebuild of its own before it means anything.
            if (_oDbPart[i] >= 0) continue;
            _bAudited++;
            int feat = ShaderFeatures.WithPose((int)_oFeat[i * 4], _oBone[i] >= 0);
            int blend = (_oWash[i] << 16) | (AlloyClasses.Blends(_passCls[k]) ? (_oSrc[i] << 8) | _oDst[i] : 0);
            IntPtr gobKey = _passCls[k] == (int)AlloyClass.Blended
                            && OitGrouped(DbObjAlpha(_oDbPart[i]), _oDecal[i] != 0, _oWash[i]) ? RunKey(i) : IntPtr.Zero;
            bool posed = _oBone[i] >= 0, dangly = !posed && DanglyPrevNow(i);
            bool recMoved = _recMovedAt[i] == _frameSeq;
            bool moved = posed ? recMoved || _poseMovedAt[i] == _frameSeq : dangly || recMoved && _oMx[i];
            // The LOD level is the one field the row DERIVES rather than copies, and it derives it on a worker
            // from two pass-wide statics -- so it is the field this audit exists for: a wrong level picks the
            // wrong MeshLod.Range, sets the wrong LodVaoBit and cuts the batch in the wrong place.
            int lvl = 0;
            if (MeshLod.On && _lodScale > 0f)
            {
                ref Sphere lsp = ref _dSph[i];
                if (lsp.Radius < NeverCull)
                {
                    float dx = lsp.Center.X - _eyePos.X, dy = lsp.Center.Y - _eyePos.Y, dz = lsp.Center.Z - _eyePos.Z;
                    lvl = MeshLod.Level(lsp.Radius, MathF.Sqrt(dx * dx + dy * dy + dz * dz), _lodScale);
                }
            }
            string bad =
                  was.Cls != _passCls[k] ? "class"
                : was.Lit != _oLit[i] ? "lit"
                : was.Dis != (_pkDis[k] != 0) ? "dissolve"
                : was.Tid != _recTid[i] ? "diffuse tid"
                : was.Slots != _recSlotMask[i] ? "slot mask"
                : was.IdxCount != _cIdx[i] ? "index count"
                : was.IdxOff != _cOff[i] ? "index offset"
                : was.BaseVertex != _cBase[i] ? "base vertex"
                : was.Vao != _oVao[i] ? "vao"
                : was.Item != _oItem[i] ? "vdo item"
                : was.Mode != _primTable[_oPrim[i]] ? "primitive mode"
                : was.Feat != feat ? "feature"
                : was.Blend != blend ? "blend"
                : was.NoCull != _oNoCull[i] ? "two-sided"
                : was.GobKey != gobKey ? "run gob"
                : was.Entry != _oEntry[i] ? "census entry"
                : was.HasGob != (_oGob[i] != IntPtr.Zero) ? "instanced"
                : was.Moved != moved || was.Posed != posed || was.Dangly != dangly ? "mover"
                : was.Lvl != lvl ? "LOD level"
                : was.Src != _oSrc[i] || was.Dst != _oDst[i] || was.Wash != _oWash[i] ? "blend factors"
                : null;
            if (bad != null) BatchMismatch(k, i, bad);
        }
    }

    private static void BatchMismatch(int k, int i, string what)
    {
        _bMismatch++;
        if (_bLogged >= 6) return;
        _bLogged++;
        _log?.Always($"AlloyRenderer: [batchcheck] frame {_frameSeq} rank {k} slot {i}: the staged row's {what} "
                   + "is not what the batch cut would have gathered -- the packed batch key is wrong, so this "
                   + "frame's batches or draw arguments are too");
    }

    /// <summary>The <c>[alloy]</c> clause for the row audit.</summary>
    private static string BatchAuditNote()
        => _bAudited == 0 ? "" : $"; batchcheck {_bMismatch} mismatch(es) over {_bAudited} row(s) (must be 0)";

    private static void ResetBatchAuditPeriod() { _bAudited = 0; _bMismatch = 0; }

    private const byte Skipped = 0, Staged = 1, Deferred = 2;
    /// <summary>Per-stage counters, one set per chunk, folded into the period statics after the join -- the
    /// hot ones every part touches. Rare skip counters go through <see cref="Inc"/> instead.</summary>
    internal struct StageCounters
    {
        public long OwnDissolve, NrmDeclared, NrmBound, RecStable, RecWritten;
        public long DisAsked, DisNoFeat, DisNoLit;
        public bool CastChanged;
    }
    private static readonly StageCounters[] _stageCounters = new StageCounters[StagePool.MaxChunks];
    private static void FoldCounters(ref StageCounters c)
    {
        Counters._ownDissolve += c.OwnDissolve; Counters._nrmDeclared += c.NrmDeclared; Counters._nrmBound += c.NrmBound;
        Counters._recStable += c.RecStable; Counters._recWritten += c.RecWritten;
        _disAsked += c.DisAsked;
        _disNoFeat += c.DisNoFeat; _disNoLit += c.DisNoLit;
        if (c.CastChanged) CasterGen++;
        c = default;
    }
    private static void Inc(ref long x) => System.Threading.Interlocked.Increment(ref x);
    private static ulong Handle(int tid, bool main) => main ? HandleFor(tid) : HandleLookup(tid);
    /// <summary>One chunk of the parallel stage: a static partition of the routed range. Chunk 0 is the main
    /// thread's own share; every chunk runs as a worker (<c>main: false</c>) so the path is one path.</summary>
    internal static void StageRange(int w, int chunks)
    {
        int routed = _stageRouted;
        int from = (int)((long)routed * w / chunks), to = (int)((long)routed * (w + 1) / chunks);
        int* slotTid = stackalloc int[MapSlots];
        for (int k = from; k < to; k++)
            _pkVerdict[k] = StagePart(k, slotTid, _stageLighting, ref _stageCounters[w], main: false);
    }
    private static int _stageRouted;
    private static bool _stageLighting;
    /// <summary><c>/alloy threads N</c>: worker threads beside the main thread for the stage. 0 = serial.</summary>
    private static int _stageThreads => Renderer.Threads.V;
    private static readonly byte[] _pkVerdict = new byte[MaxOwned], _pkDis = new byte[MaxOwned];
    /// <summary>The per-part half of <see cref="BuildBatches"/>: chase the state a routed part depends on and
    /// write its record, sphere and cast admission -- everything that touches ONLY this slot's arrays, so it
    /// is order-free where the batch walk is not. The worker rule (ADR 0022): off the main thread, a path that
    /// mints a GL object, logs, allocates or touches a shared collection returns <see cref="Deferred"/>.</summary>
    private static byte StagePart(int k, int* slotTid, bool lightingOn, ref StageCounters c, bool main)
        => StageDbPart(k, _pass[k], lightingOn, ref c, main);

    /// <summary>Stage the sky's draws behind the batch records, in the engine's own order -- current sky, then
    /// the one fading in over it, then the cross-fade gob. One record per part, carrying the model matrix
    /// <see cref="SkyBox"/> composed off the engine's matrix stack and the role's alpha in <c>rot.w</c>. Every
    /// term is re-read live: a part that does not resolve this frame hands the whole sky back.</summary>
    private static void StageSky()
    {
        _skyN = 0; _skyFirst = _drawN;
        StageDbSky(_dbPassScene);
    }

    /// <summary>The main view's inverse view-projection, noted at the sky pass for the resolve's horizon band
    /// (<see cref="PostChain"/>): a far-depth pixel below the band is fog, not sky. Default = no dome drew this
    /// view, so every far-depth pixel is the fog-coloured clear and is graded like the fogged geometry.</summary>
    internal static Matrix4x4 SkyInvVp;

    /// <summary>The skybox, after the opaque set and before the blended one (ADR 0011 step 3). The dome sits AT
    /// the camera, so <c>glDepthRange(1,1)</c> plus the opaque pass's LEQUAL is what makes "after opaque" mean
    /// anything: it survives only where nothing wrote depth. Depth range and clamp are 4.6-CONTEXT state later
    /// passes share, so both are restored here. ponytail: the roles composite in LINEAR, not display space.</summary>
    private static void SkyOwnedPass(Matrix4x4* vp, int* viewport)
    {
        if (_skyN == 0 || Gl.DepthRange == null) return;
        SkyStage.BeginPass();
        // DEPTH_CLAMP because the composed matrices sit under the ENGINE's sky projection (RenderSkyBoxes
        // replaced it with its own near/far) and are drawn under the world's: the dome can cross the world
        // far plane, and clipped it reads as a hole at the horizon with nothing in the log.
        Gl.DepthMask(0); Gl.DepthRange(1.0, 1.0);
        Gl.Enable(Gl.DEPTH_CLAMP);
        // #292: STATED, not inherited, and stated as the ENGINE's. This pass has no material to read
        // `twosided` off and no batch of its own, so it used to run under the pass-wide disable and would
        // otherwise now run under whatever the pass before it left -- which is not the same state twice: the
        // grass pass returns before its own preamble when an area has no grass, and the opaque range ends
        // two-sided only when its last batch was. Vanilla draws the sky BACK-CULLED: SetupOpenGL @0x42b060
        // enables cap 3 at startup, Scene::RenderSkyBoxes @0x466910 touches caps 0/1/2/5 but never 3, and it
        // runs after a Scene::RenderDrawBucket @0x46d060 that exits Enable(3)+SetCullFace(1). The dome is
        // the engine's own geometry drawn under the engine's own winding, so the state it is drawn correctly
        // in over there is the state it is drawn correctly in here.
        Gl.Enable(Gl.CULL_FACE); Gl.CullFace(Gl.BACK);
        Gl.Enable(Gl.BLEND); _blendOn = true;
        uint boundVaoName = 0;
        uint boundProg = 0, boundSrc = 0xFFFFFFFF, boundDst = 0xFFFFFFFF;
        int* boundTex = stackalloc int[MaxEngTex];
        for (int k = 0; k < MaxEngTex; k++) boundTex[k] = -1;
        bool usedPlain = false;
        for (int i = 0; i < _skyN; i++)
        {
            uint prog = _skProg[i] != 0 ? _skProg[i] : Programs._prog46;
            if (prog == 0) continue;
            uint src = Issue.BlendGl(_skSrc[i]), dst = Issue.BlendGl(_skDst[i]);
            if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            if (prog != boundProg)
            {
                if (_skProg[i] != 0)
                {
                    // a native stage that stood down at this frame's head has no images: the dome sits the frame out
                    if (!SkyStage.Prepare(prog, vp, viewport)) { boundProg = 0; continue; }
                }
                else { PrimeSkyPlain(vp, viewport); usedPlain = true; }
                boundProg = prog;
                // sampler units are per PROGRAM state but the bindings are the CONTEXT's, so only the
                // program-side cache resets here; the texture cache below is still valid.
            }
            // The draw carries the arena's VAO name.
            uint vaoName = _skVaoName[i];
            if (vaoName != boundVaoName) { Gl.BindVertexArray(vaoName); boundVaoName = vaoName; }
            ref SkyTexUnits tex = ref _skTex[i];
            // Unit 0 for either stage; the rest only where a hosted stage could declare them. Units 1 and 2
            // are the shadow ARRAYS (TextureSlots.SunShadow/LocalShadow) and 3 is the material map block, so a
            // plain sky draw has no business touching them -- and the next BindPass is what puts a hosted
            // draw's clobber back, exactly as it did for #136's engine-program pass.
            int units = _skProg[i] != 0 ? MaxEngTex : 1;
            for (int k = 0; k < units; k++)
                if (tex[k] != boundTex[k]) { Gl.BindTextureUnit((uint)k, (uint)tex[k]); boundTex[k] = tex[k]; }
            int s = _skyFirst + i;
            fixed (float* tint = &_skTint[i].X)
            {
                if (_skProg[i] != 0) SkyStage.Draw(prog, s, tint, _skAlpha[i], SceneFog.SkyBand);
                else { Gl.Uniform3fv(Programs._locTint, 1, tint); Gl.Uniform1i(Programs._locBase, s); }
            }
            MultiDrawRange(_skMode[i], s, 1);
            Counters._batches++; Counters._draws++;
            if (_skProg[i] != 0) SkyBox.NoteHosted();
        }
        if (usedPlain)
        {
            // program state outlives the pass: no later unlit draw inherits the horizon band
            Gl.UseProgram(Programs._prog46);
            Gl.Uniform1f(Programs._locSkyFog, 0f);
        }
        Gl.Disable(Gl.DEPTH_CLAMP);
        Gl.DepthRange(0.0, 1.0);
    }

    /// <summary>The plain unlit stage, set up for a sky draw: the pass terms it shares with every other unlit
    /// draw plus the two the sky alone sets -- the discard off (the engine draws the sky with no alpha test)
    /// and the horizon band.</summary>
    private static void PrimeSkyPlain(Matrix4x4* vp, int* viewport)
    {
        Gl.UseProgram(Programs._prog46);
        Gl.Uniform1i(Programs._locTex, 0);
        Gl.Uniform1i(Programs._locViz, _lightViz);
        // uOit is program state and the OIT pass left it at 1: the dome would land as a near-zero weighted
        // accumulation write (gl_FragCoord.z = 1) -- a black sky.
        Gl.Uniform1i(Programs._locOit, 0);
        Gl.UniformMatrix4fv(Programs._locVp, 1, 0, (float*)vp);
        UploadFeatureTerms(Programs._locFeat, viewport);
        Gl.Uniform1f(Programs._locCut, -1f);
        Gl.Uniform1f(Programs._locSkyFog, SceneFog.SkyBand);
    }

    /// <summary>Pick this slot's charted range, or offer its mesh to the charter if it has none. A chart is the
    /// same arena section with the chart lane filled, so picking it is three integers, and
    /// <see cref="LightmapBake.Enrol"/> on the back of it is the whole of baked lighting -- nothing else mints
    /// a rect. Main thread; offers are budgeted, since each takes the arena lock and allocates the job.</summary>
    private static bool DbChart(int i, int range, int lvl, ref nint idxOff, ref int baseVertex, ref int idxCount, ref int drawn)
    {
        if (range < 0) return false;
        if (MeshChart.Range(ref _oChart[i], MeshChart.DbSlot, range, lvl,
                            out int cb, out nint co, out int cc, out int clvl) && cc > 0)
        {
            idxOff = co; baseVertex = cb; idxCount = cc; drawn = clvl;
            MeshChart.Drew(cc);
            LightmapBake.Enrol(i, MeshChart.DbSlot, range);
            return true;
        }
        if (_dbOfferBudget > 0 && MeshChart.HasRoom() && MeshChart.OfferDb(range)) _dbOfferBudget--;
        return false;
    }

    /// <summary>The run a DB draw takes, in order: the charted copy of this mesh where one has LANDED (the
    /// same lookup that enrols the slot for the lightmap), else the plain range's own run at the level the
    /// batch row earned. A chart that has not landed -- or was refused, which is for the session -- must
    /// fall through: most of a scene is bakeable, so an <c>else</c> here is full detail for every tile.</summary>
    internal static void DbRuns(int i, int range, int lvl, bool chartArmed,
                                ref nint idxOff, ref int baseVertex, ref int idxCount, ref int drawn)
    {
        if (chartArmed && DbBakeable(i)
            && DbChart(i, range, lvl, ref idxOff, ref baseVertex, ref idxCount, ref drawn)) return;
        if (lvl > 0) ArenaLevelRuns(range, lvl, ref idxOff, ref baseVertex, ref idxCount, ref drawn);
    }

    /// <summary>An arena range's index run at detail level <paramref name="lvl"/>, in place of the base run
    /// the stage resolved. A level the simplifier declined falls back to the finest that exists, so a part
    /// never draws nothing; <paramref name="drawn"/> carries the level actually served, which is what the
    /// per-level tally counts. False = nothing to substitute and the base run stands.</summary>
    internal static bool ArenaLevelRuns(int range, int lvl, ref nint idxOff, ref int baseVertex,
                                        ref int idxCount, ref int drawn)
    {
        if (range < 0 || !Db.MeshArena.Runs(range, lvl, out int bv, out nint off, out int cnt, out int level)
            || cnt <= 0) return false;
        idxOff = off; baseVertex = bv; idxCount = cnt; drawn = level;
        return true;
    }

}
