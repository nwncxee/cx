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
        fixed (float* sph = _dSph)
        {
        for (int c = (int)AlloyClass.Opaque; c <= (int)AlloyClass.Cutout; c++)
        {
            int nd = 0;   // _orderTmp is the enrollment radix's scratch, idle during a pass
            int[] list = RouteRange(c, out int from, out int to);
            for (int k = from; k < to; k++)
            {
                if (!RouteLive(list[k], k, sph, out int i, out IntPtr tri)) continue;
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
        AuditVisible(n, sph);
        {
        int[] list = RouteRange((int)AlloyClass.Blended, out int from, out int to);
        for (int k = from; k < to; k++)
        {
            if (!RouteLive(list[k], k, sph, out int i, out IntPtr tri)) continue;
            float alpha = LiveAlpha(i, tri);
            if (alpha <= 0f && _oTile[i] != IntPtr.Zero) continue;   // #546
            _blend[nb] = i;
            _blendAlpha[nb] = alpha;
            _blendKey[nb] = BlendKey(i); nb++;
        }
        }
        SortBlended(_blend, _blendKey, _blendAlpha, nb);
        for (int a = 0; a < nb; a++)
        { _pass[n] = _blend[a]; _passCls[n] = (byte)AlloyClass.Blended; _passAlpha[n] = _blendAlpha[a]; n++; }
        // #129: water, unsorted. ponytail: a per-part back-to-front sort is skipped because water planes are
        // per-tile, coplanar and depth-writing -- they cannot overlap each other in a way a sort would fix.
        // Add one if a map ever stacks a waterfall sheet in front of a pool.
        {
        int[] list = RouteRange((int)AlloyClass.Water, out int from, out int to);
        for (int k = from; k < to; k++)
        {
            if (!RouteLive(list[k], k, sph, out int i, out IntPtr tri)) continue;
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
            if (!RouteLive(list[k], k, sph, out int i, out IntPtr tri)) continue;
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
    private static bool RouteLive(int slot, int k, float* sph, out int i, out IntPtr tri)
    {
        // The Route sample -- taken on the slots the gate rejects, which is the walk's O(world) half
        bool samp = (k & (ChaseEvery - 1)) == 0;
        long t0 = samp ? FrameZones.Now : 0;
        i = slot;
        tri = _oTri[i];
        if (tri == IntPtr.Zero || !RouteWanted(i, sph))
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
        { Counters._skipForeignScene++; return false; }
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
    private static readonly uint[] _dSrc = new uint[MaxOwned * CullPass.SrcUints];

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
        if (!ok) log.Info("AlloyRenderer.SortSelfCheck: FAILED -- the blended order is not back-to-front");
        return ok;
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
        ok &= (int)FeatCeiling == ShaderFeatures.VsNames.Length - 1
              && FeatPrimKeyOf((int)FeatCeiling, PrimTypes - 1) == _featPrimCount.Length - 1
              && FeatPrimKeyOf((int)FeatCeiling, PrimTypes + 5) == _featPrimCount.Length - 1
              && FeatPrimKeyOf((int)FeatCeiling + 3, 0) == _featPrimCount.Length - PrimTypes
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
    /// <summary><see cref="ShaderFeature"/>'s last member, named here because the sort table below is sized
    /// from it. A feature appended above this ceiling walks <c>_featPrimCount</c> off its end and throws
    /// mid-frame, so <see cref="CullSortSelfCheck"/> ties it to <see cref="ShaderFeatures.VsNames"/>, which
    /// any appended feature has to extend anyway.</summary>
    private const ShaderFeature FeatCeiling = ShaderFeature.PaletteTint;
    /// <summary>Buckets for the (feature, primitive) digit.</summary>
    private const int FeatPrimKeys = ((int)FeatCeiling + 1) * PrimTypes;
    /// <summary>Both indices are CLAMPED, not trusted: this is a sort digit, so a feature past the ceiling
    /// merely shares a bucket (one extra batch cut, never a wrong draw) where an unclamped one takes the
    /// frame down. The self-check is what makes the drift loud; the clamp is what makes it survivable.</summary>
    private static int FeatPrimKeyOf(int feat, int prim)
        => Math.Clamp(feat, 0, (int)FeatCeiling) * PrimTypes + Math.Clamp(prim, 0, PrimTypes - 1);

    /// <summary>Order the blended run back to front -- the farthest key first, so nearer surfaces composite
    /// over what is already behind them. Stable, which keeps parts at equal distance in their texture-grouped
    /// enrollment order rather than shuffling them from pass to pass.</summary>
    private static void SortBlended(int[] idx, float[] key, float[] alpha, int n)
    {
        for (int a = 1; a < n; a++)
        {
            int i = idx[a]; float k = key[a], al = alpha[a];
            int b = a - 1;
            for (; b >= 0 && key[b] < k; b--)
            { idx[b + 1] = idx[b]; key[b + 1] = key[b]; alpha[b + 1] = alpha[b]; }
            idx[b + 1] = i; key[b + 1] = k; alpha[b + 1] = al;
        }
    }

    /// <summary>The part's own alpha multiplier (part+0xa0), live: it is animated state, not enrollment
    /// state.</summary>
    private static float PartAlpha(IntPtr tri) => new PartRef(tri).Alpha;

    /// <summary>The scene this pass is drawing. Set by the world pass before the route walk; a slot of any
    /// other scene is skipped, which is what keeps one view from drawing another's objects.</summary>
    internal static DbId _dbPassScene;

    /// <summary>The object-wide alpha multiplier, which the engine reaches by chasing the gob's fade owner.
    /// One column load: the tint's w.</summary>
    private static float DbObjAlpha(int part) => SceneDb.ObjTint[SceneDb.PartObj[part].Index].W;

    /// <summary>The part's own alpha, read off the part every frame for a gob or matrix-class part (the
    /// animated ones) and every <see cref="ColourCadence"/> frames for a still gobless part -- the tile
    /// geometry that is the routed majority, whose <c>+0xa0</c> only an MDL alpha controller writes.
    /// <c>/alloy poll 1</c> reads and audits the cache every frame.</summary>
    private static float PartAlphaCached(int i, IntPtr tri) => SceneDb.PartAlpha[_oDbPart[i]];

    /// <summary>This frame's whole alpha multiplier for a part -- its own animated alpha, the fade factor of
    /// the tile it stands on, and the instance's two multiplies. Every term read live, none cached at
    /// capture: a gob can fade a whole creature into the transparent run for a single frame and out again.</summary>
    private static float LiveAlpha(int i, IntPtr tri)
        => SceneDb.PartAlpha[_oDbPart[i]] * SceneDb.PartFade[_oDbPart[i]] * DbObjAlpha(_oDbPart[i]);

    /// <summary>The blended sort key: a part sorts by its RUN ROOT's distance, so every part of one creature
    /// carries the same key and the stable sort lands them contiguous -- one run the frame can depth-prepass
    /// and colour as a unit. A part with no run root keeps its own centre.</summary>
    private static float BlendKey(int i)
    {
        // CX-120: a part sorts by its RUN ROOT's world position -- the object a fade and a blended run apply to,
        // resolved when attachment changed rather than climbed per part per pass.
        DbId root = SceneDb.ObjRunRoot[SceneDb.PartObj[_oDbPart[i]].Index];
        if (root.IsNull) return EyeDist2(i);
        Matrix4x4 w = SceneDb.ObjWorld[root.Index];
        float dx = w.M41 - _eyePos.X, dy = w.M42 - _eyePos.Y, dz = w.M43 - _eyePos.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>The blended batch key's run identity -- the run root as an opaque value, compared and never
    /// dereferenced by every reader. A creature's body parts and held items share one root, so one ghosted
    /// creature is one layer rather than one per part.</summary>
    private static IntPtr RunKey(int i)
        => (IntPtr)(long)SceneDb.ObjRunRoot[SceneDb.PartObj[_oDbPart[i]].Index].Value;

    /// <summary>Is this part a creature's? Its own object, or any object up its attachment chain, carries the
    /// creature flag -- a hat or a held weapon is its own object hanging off the body's. A player character is
    /// RIGID parts under bone-driven nodes, not a skinmesh, so nothing else names it a creature.</summary>
    internal static bool SlotCreature(int i) => SlotCreatureObj(i) >= 0;
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

    /// <summary>Store <paramref name="v"/> and say whether that changed anything. The whole persistent-record
    /// scheme is this compare: a field written every frame with the value it already held is not a mover, and
    /// the record it sits in does not need re-sending.</summary>
    private static bool St(float* p, float v)
    {
        if (*p == v) return false;
        *p = v;
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
    /// (<see cref="WriteStable"/>) and it reads as a frozen texture; <see cref="SplitSelfCheck"/> goes red.</summary>
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

    /// <summary>This record's world bounding sphere, for the cull. The local radius is enrolment-stable; what
    /// moves is the model matrix, so a matrix-class sphere is centred on its translation and its radius scaled
    /// by the WHOLE scale chain, or a placeable scaled k times culls as one k times smaller about its pivot.
    /// A posed skinmesh, a dangly mesh and a displacing feature are <see cref="NeverCull"/>: they leave it.</summary>
    private static void WriteSphere(int i, in RecordFrameState f, IntPtr tri, int feat)
    {
        fixed (float* sp = _dSph)
        {
            float* s = sp + (long)i * 4;
            if (_oBone[i] >= 0 || ShaderFeatures.OutOfPrepass(feat) || PartTable.IsDangly(tri)
                || !(_oR[i] > 0f))
            { s[0] = 0f; s[1] = 0f; s[2] = 0f; s[3] = NeverCull; return; }
            // The centre is the model TRANSLATION, not _oC* through the matrix: that bbox centre is not
            // object-space (EyeDist2, CasterPass), and transforming it would add the node translation twice.
            float cx = _oCx[i], cy = _oCy[i], cz = _oCz[i], r = _oR[i];
            if (f.HasModel) { cx = f.Model.M41; cy = f.Model.M42; cz = f.Model.M43; r *= DbAnim.ScaleOf(f.Model); }
            s[0] = cx; s[1] = cy; s[2] = cz; s[3] = r;
        }
    }

    /// <summary>Stand-in radius for a caster whose engine bbox is degenerate: one NWN tile's horizontal
    /// diagonal (tiles are 10u square; WaterBodies.TileHalf is the same convention's other half).</summary>
    private const float DegenerateCasterRadius = 7.1f;

    /// <summary>The same sphere, GEOMETRIC even for the three exempted kinds, and scaled the same way.
    /// See <c>_dCastSph</c> for why the shadow pass cannot use a <see cref="NeverCull"/> one.</summary>
    private static void WriteCasterSphere(int i, in RecordFrameState f)
    {
        fixed (float* sp = _dCastSph)
        {
            float* s = sp + (long)i * 4;
            float cx = _oCx[i], cy = _oCy[i], cz = _oCz[i], r = _oR[i];
            // a posed skin and a dangly move their vertices off the bind box; the origin bound is the looser fit for them
            if (f.HasModel && _oBr[i] > 0f && !_vSkin[_oVao[i]] && !_oDangly[i])
            {
                var c = Vector3.Transform(new Vector3(_oBx[i], _oBy[i], _oBz[i]), f.Model);
                cx = c.X; cy = c.Y; cz = c.Z; r = _oBr[i] * DbAnim.ScaleOf(f.Model);
            }
            else if (f.HasModel) { cx = f.Model.M41; cy = f.Model.M42; cz = f.Model.M43; r *= DbAnim.ScaleOf(f.Model); }
            // Review, restoring the deleted MinSkinRadius insurance: a skinmesh's bbox is its BIND pose,
            // and an animated pose (an extended arm, a swinging cloak) reaches past it. The slack keeps the
            // reach culls and the staleness volumes honest about where the pose can actually be; keyed on
            // the VDO's bone streams because that is enrollment-stable where the per-frame bone slot is not.
            float bare = r;
            if (_vSkin[_oVao[i]]) r = MathF.Max(r + 2f, 3f);
            // A part with an unpopulated engine bbox (min == max -- measured live on tile parts, the same
            // defect WaterBodies stands in for with TileHalf) must NOT store radius 0: a zero sphere makes
            // TryCaster admit it at infinite reach (defeating every view's re-bake predicate) and makes the
            // vanish sweep take the whole-frame blanket when it blinks out. One tile's diagonal is the
            // honest bound for the population this actually is.
            if (!(r > 0f)) r = DegenerateCasterRadius;
            s[0] = cx; s[1] = cy; s[2] = cz; s[3] = r;
            _dCastSlack[i] = bare > 0f ? r - bare : 0f;
        }
    }

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
    private static bool WritePerFrame(float* r, in RecordFrameState f, bool roll = true, bool fresh = false)
    {
        bool m = false;
        if (f.HasModel)
        {
            Matrix4x4* model = (Matrix4x4*)r, prev = (Matrix4x4*)(r + PrevFloat);
            if (Moved(in *model, in f.Model, MoveStep))
            {
                if (roll) *prev = *model;
                *model = f.Model; m = true;
            }
            else if (roll && *prev != *model) { *prev = *model; m = true; }
            if (fresh) *prev = f.Model;
        }
        m |= St(r + 18, f.Dissolve) | St(r + 19, f.Alpha);
        if (f.Lit)
        {
            m |= St(r + 20, f.AmbR) | St(r + 21, f.AmbG) | St(r + 22, f.AmbB)
                 | St(r + 28, f.EmiR) | St(r + 29, f.EmiG) | St(r + 30, f.EmiB)
                 | St(r + 35, f.Slots);
            m |= St(r + 24, f.DifR) | St(r + 25, f.DifG) | St(r + 26, f.DifB);
        }
        m |= St(r + 40, f.FeatX) | St(r + 41, f.FeatY);
        return m;
    }

    /// <summary>Stable-record writes by emi.w class since arming (creature / object / tileset), for the weather line.</summary>
    internal static int RecCreature, RecObject, RecTile;

    /// <summary>The stable half -- written when the slot is built and never again while it holds the same
    /// part. The texture rotation cannot change without failing <c>IdentityOk</c> (which revokes), a .mtr does
    /// not animate, and the alpha ones are literal constants. An identity model matrix is here too: a part
    /// whose class does not stage a node has no live matrix to re-read.</summary>
    private static void WriteStable(float* r, int i, bool lit)
    {
        if (!_oMx[i]) { *(Matrix4x4*)r = Matrix4x4.Identity; *(Matrix4x4*)(r + PrevFloat) = Matrix4x4.Identity; }
        r[16] = _oCos[i]; r[17] = _oSin[i];
        int fo = i * 4;
        r[42] = _oFeat[fo + 2];
        // #308 follow-up: -1 marks a DANGLY part for the receiver's creature gate (skinmesh is already
        // feat.x). Safe reuse: feat.w is only otherwise read inside the wind/roof feature branches, and no
        // dangly mesh names one of those forks.
        r[43] = PartTable.IsDangly(_oTri[i]) ? -1f : _oFeat[fo + 3];
        if (!lit) return;
        r[23] = 1f; r[27] = 1f;
        // emi.w: the contact-shadow RECEIVER flag -- 1 on tileset geometry, 0 on an object. A depth march
        // on a low-poly creature or cloak reads its own facets as blockers; the ground under it does not.
        // Static geometry (no gob at all) IS the tileset: only an object instances off a gob, and the gob'd
        // half a SpawnTile split leaves is caught by the tile-gob test.
        // #618: -1 marks a CREATURE's part (the root of its attachment chain is a creature gob), for the snow
        // gate -- snow does not settle on a moving body. Safe reuse: every other reader tests > 0.5.
        r[31] = _oGob[i] == IntPtr.Zero || _oTileGob[i] ? 1f
              : CreaturePart(i) ? -1f : 0f;
        if (r[31] < 0f) RecCreature++; else if (r[31] > 0f) RecTile++; else RecObject++;
        int o = i * ParamFloats;
        r[32] = _oParam[o]; r[33] = _oParam[o + 1]; r[34] = _oParam[o + 2];
        r[36] = _oParam[o + 4]; r[37] = _oParam[o + 5]; r[38] = _oParam[o + 6]; r[39] = _oParam[o + 7];
        for (int k = 0; k < ParamFloats - ParamExt; k++) r[ExtFloat + k] = _oParam[o + ParamExt + k];
    }

    /// <summary>The caster sphere off a part's own-space box: every box corner through a rotated, scaled, translated
    /// model lands inside it, and the centre follows the box rather than the node origin. Scratch slot 0, restored.</summary>
    private static bool CasterBoundSelfCheck(ExtenderLog log)
    {
        const int i = 0;
        // CX-120 (R1-35): scratch slot 0 must not be a live DB tenant while this forges a box into it.
        int wasDb = _oDbPart[i]; _oDbPart[i] = -1;
        float bx = _oBx[i], by = _oBy[i], bz = _oBz[i], br = _oBr[i], ccx = _oCx[i], ccy = _oCy[i], ccz = _oCz[i], cr = _oR[i];
        int vao = _oVao[i];
        Span<float> was = stackalloc float[4];
        for (int k = 0; k < 4; k++) was[k] = _dCastSph[k];
        float wasSlack = _dCastSlack[i];
        var lo = new Vector3(-0.5f, -2.5f, 3.2f); var hi = new Vector3(0.5f, -1.7f, 12.3f);   // the harbour ship's mast
        Vector3 c = 0.5f * (lo + hi);
        _oBx[i] = c.X; _oBy[i] = c.Y; _oBz[i] = c.Z; _oBr[i] = 0.5f * Vector3.Distance(lo, hi);
        _oCx[i] = 0f; _oCy[i] = 0f; _oCz[i] = 0f; _oR[i] = 12.21f; _oVao[i] = 0;
        var f = default(RecordFrameState);
        f.HasModel = true;
        f.Model = Matrix4x4.CreateScale(1.5f) * Matrix4x4.CreateFromYawPitchRoll(0.7f, 0.2f, 0.4f) * Matrix4x4.CreateTranslation(40f, 7f, -3f);
        bool skin = _vSkin[0], dangly = _oDangly[i];
        _vSkin[0] = false; _oDangly[i] = false;
        WriteCasterSphere(i, in f);
        CasterSphere(i, out float sx, out float sy, out float sz, out float sr);
        bool inside = true;
        for (int k = 0; k < 8; k++)
        {
            var p = new Vector3((k & 1) != 0 ? hi.X : lo.X, (k & 2) != 0 ? hi.Y : lo.Y, (k & 4) != 0 ? hi.Z : lo.Z);
            inside &= Vector3.Distance(Vector3.Transform(p, f.Model), new Vector3(sx, sy, sz)) <= sr * 1.0001f;
        }
        bool tight = sr < 12.21f * 1.5f * 0.5f;
        // hand-worked: a skinned part takes the origin bound, 12.21 x 1.5 = 18.315, culled at +2 and framed at it
        _vSkin[0] = true;
        WriteCasterSphere(i, in f);
        CasterSphere(i, out _, out _, out _, out float skinR);
        bool framed = MathF.Abs(skinR - 20.315f) < 1e-3f && MathF.Abs(CasterFrameRadius(i) - 18.315f) < 1e-3f;
        _vSkin[0] = skin; _oDangly[i] = dangly;
        _oDbPart[i] = wasDb;
        _oBx[i] = bx; _oBy[i] = by; _oBz[i] = bz; _oBr[i] = br; _oCx[i] = ccx; _oCy[i] = ccy; _oCz[i] = ccz; _oR[i] = cr; _oVao[i] = vao;
        for (int k = 0; k < 4; k++) _dCastSph[k] = was[k];
        _dCastSlack[i] = wasSlack;
        if (!framed) log.Info($"AlloyRenderer.CasterBoundSelfCheck: FAILED -- a skinned part culls at {skinR:F3} (want 20.315) "
                            + $"and frames at {CasterFrameRadius(i):F3} (want 18.315): the pose slack leaks into the framing");
        if (!framed) return false;
        if (!(inside && tight)) log.Info("AlloyRenderer.CasterBoundSelfCheck: FAILED -- the caster sphere does not hold its own vertex box "
                                       + "through the model, or still sits about the node origin");
        return inside && tight;
    }

    /// <summary>Off-device, the split itself: every field of <see cref="RecordFrameState"/> is perturbed on
    /// its own and must make the record a mover, and a record re-written with the values it already holds must
    /// not. Move one out of <see cref="WritePerFrame"/> into <see cref="WriteStable"/> and its perturbation
    /// stops being seen -- a fading tile stuck at full opacity, with nothing else to say so.</summary>
    private static bool SplitSelfCheck(ExtenderLog log)
    {
        float[] rec = new float[DrawFloats];
        var f = default(RecordFrameState);
        f.HasModel = true; f.Model = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        f.Dissolve = 1f; f.Alpha = 0.5f; f.Lit = true;
        f.AmbR = 0.1f; f.AmbG = 0.2f; f.AmbB = 0.3f;
        f.DifR = 0.4f; f.DifG = 0.5f; f.DifB = 0.6f;
        f.EmiR = 0.7f; f.EmiG = 0.8f; f.EmiB = 0.9f;
        f.Slots = 3f; f.FeatX = 5f; f.FeatY = 6f;
        bool ok;
        fixed (float* r = rec)
        {
            ok = WritePerFrame(r, in f, fresh: true) && !WritePerFrame(r, in f);
            if (!ok) log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- a record is a mover every frame, or "
                              + "never one; either way the persistent-record scheme is not running");
            // one field at a time, restored after each, so a miss names itself rather than being masked by
            // the next perturbation
            ok &= Perturb(r, ref f, ref f.Dissolve, log, "rot.z dissolve")
                  & Perturb(r, ref f, ref f.Alpha, log, "rot.w alpha")
                  & Perturb(r, ref f, ref f.AmbR, log, "amb.r")
                  & Perturb(r, ref f, ref f.AmbG, log, "amb.g")
                  & Perturb(r, ref f, ref f.AmbB, log, "amb.b")
                  & Perturb(r, ref f, ref f.DifR, log, "dif.r")
                  & Perturb(r, ref f, ref f.DifG, log, "dif.g")
                  & Perturb(r, ref f, ref f.DifB, log, "dif.b")
                  & Perturb(r, ref f, ref f.EmiR, log, "emi.r")
                  & Perturb(r, ref f, ref f.EmiG, log, "emi.g")
                  & Perturb(r, ref f, ref f.EmiB, log, "emi.b")
                  & Perturb(r, ref f, ref f.Slots, log, "pbr.w live slot mask")
                  & Perturb(r, ref f, ref f.FeatX, log, "feat.x")
                  & Perturb(r, ref f, ref f.FeatY, log, "feat.y bone base");
            // ...and the matrix, which is the one the ticket names first: a mover the split does not know
            // about is a frozen record, and it reads as correct until the thing moves.
            Matrix4x4 was = f.Model;
            f.Model = Matrix4x4.CreateTranslation(9f, 9f, 9f);
            bool mm = WritePerFrame(r, in f);
            // #392: the frame the matrix moved, prev holds where it WAS; the next build (nothing moved) rolls
            // prev up to the model and re-sends once; the one after that is quiet.
            bool prevLag = *(Matrix4x4*)(r + PrevFloat) == was && *(Matrix4x4*)r == f.Model;
            bool catchUp = WritePerFrame(r, in f) && *(Matrix4x4*)(r + PrevFloat) == f.Model && !WritePerFrame(r, in f);
            // ...and a second build in the SAME frame (roll: false) leaves prev where the first put it.
            f.Model = Matrix4x4.CreateTranslation(3f, 3f, 3f);
            WritePerFrame(r, in f);
            Matrix4x4 held = *(Matrix4x4*)(r + PrevFloat);
            bool sameFrame = !WritePerFrame(r, in f, roll: false) && *(Matrix4x4*)(r + PrevFloat) == held;
            f.Model = was;
            WritePerFrame(r, in f, fresh: true);
            if (!mm) log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- a moved model matrix does not "
                              + "re-send its record; every creature and door would freeze where it stood");
            if (!prevLag || !catchUp || !sameFrame)
                log.Info("AlloyRenderer.SplitSelfCheck: FAILED -- prev is not last frame's model (#392): a stopped "
                         + "door would report motion forever, or a mover would lose its vectors");
            ok &= mm && prevLag && catchUp && sameFrame;
        }
        return ok;
    }

    private static bool Perturb(float* r, ref RecordFrameState f, ref float field, ExtenderLog log, string name)
    {
        float was = field;
        field = was + 1f;
        bool seen = WritePerFrame(r, in f);
        field = was;
        WritePerFrame(r, in f);
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

    /// <summary>Advance every cycle texture the engine is animating. Main thread, before the batch build. No
    /// record holds a controller any more -- a part resolves its textures through <see cref="TexRegistry"/> --
    /// so a dropped one is said out loud rather than swallowed: whatever re-arms
    /// <c>CycleTextures.Register</c> must restore the re-resolve funnel, or the textures behind it freeze.</summary>
    private static void TickCycleTextures()
    {
        CycleTextures.Tick();
        if (CycleTextures.Dropped.Length == 0 || _cycleDropSaid) return;
        _cycleDropSaid = true;
        _log?.Always("AlloyRenderer: CycleTextures dropped a controller, but no record holds one -- the "
                   + "per-record controller column went with the engine-path stage (CX-188), so nothing "
                   + "re-resolved. Whatever re-armed CycleTextures.Register must restore that funnel, or the "
                   + "textures behind it freeze");
    }
    /// <summary>Said once: a dropped cycle controller with no record column to act on.</summary>
    private static bool _cycleDropSaid;
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

    /// <summary>How many frames apart each slot re-reads its slow-drift colour state (mdl colour triples,
    /// self-illum, gob colour folds, non-animated slot-map pointers). Power of two; the stagger by slot index
    /// spreads the reads evenly, so the per-frame cost is 1/N of the population and the worst staleness is N
    /// frames of a drift that moves over seconds. Fast-visible state never rides this.</summary>
    private const int ColourCadence = 8;

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
        bool keyLit = false, keyDis = false, keyNoCull = false, keyA2c = false;
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
        fixed (float* data = _dData)
        {
            if (chunks > 1 && StagePool.Run(chunks, _log))
            {
                for (int k = 0; k < routed; k++)
                    if (_pkVerdict[k] == Deferred)
                    { _stageDeferred++; FrameZones.Count(FrameZones.Ev.Deferred); _pkVerdict[k] = StagePart(k, data, slotTid, lightingOn, ref _stageCounters[0], main: true); }
            }
            else
            {
                // A plain field write, not a Set: no Changed, no cfg write -- the stand-down is this session's.
                if (chunks > 1) { Renderer.Threads.V = 0; _log?.Info("AlloyRenderer: /alloy threads stood down by a StagePool fault; the frame re-staged serially (set /alloy threads again to re-arm)"); }
                for (int k = 0; k < routed; k++) _pkVerdict[k] = StagePart(k, data, slotTid, lightingOn, ref _stageCounters[0], main: true);
            }
        }
        _stageFrames++;
        for (int w = 0; w < _stageCounters.Length; w++) FoldCounters(ref _stageCounters[w]);
        if (chunks > 1) FrameZones.Add(FrameZones.Zone.Chase, tStage);   // threaded: the whole stage phase stands in for the sampled chase
        // BW2: on a cadence (and every frame under /alloy poll 1), rebuild every staged row the way the cut
        // used to gather it and count the disagreements. The rows decide what is drawn, so this is the check
        // that the move was a move.
        if (_fullPoll || (_frameSeq & (BatchAuditEvery - 1)) == 0) AuditBatchRows(routed);
        fixed (float* data = _dData)
        fixed (float* sphAll = _dSph)
        fixed (uint* srcAll = _dSrc)
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
            float* r = data + (long)i * DrawFloats;
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
            // A blended batch never straddles two creatures: the frame prepasses and colours each run of
            // one gob's batches as a unit, so the gob is a key term here and a tag on the batch.
            IntPtr gobKey = row.GobKey;
            // CX-189: an alpha-to-coverage cutout draws in its own layer, so it never shares a batch
            bool a2c = cls == (int)AlloyClass.Cutout && _oA2c[i];
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
                || noCull != keyNoCull || gobKey != keyGob || a2c != keyA2c)
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
                if (!merge) for (int s = 0; s < MapSlots; s++) _gTexN[groups * MapSlots + s] = slotTid[s];
                groups++;
                keyLit = lit; keyVao = vKey; keyTex = tidKey; keyMode = mode; keyFeat = feat; keyDis = dis;
                keyCls = cls; keyBlend = blend; keyNoCull = noCull; keyGob = gobKey; keyA2c = a2c;
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
            CullPass.Pack(srcAll + (long)n * CullPass.SrcUints, idxCount, _dOff[n], _dBase[n], i,
                          groups - 1, _gStart[groups - 1]);
            // #287: ...and after the record block, so the extent is measured on the sphere this frame wrote.
            if (cls <= (int)AlloyClass.Cutout)
            {
                float* sp = sphAll + (long)i * 4;
                if (sp[3] < NeverCull)
                {
                    lox = MathF.Min(lox, sp[0]); hix = MathF.Max(hix, sp[0]);
                    loy = MathF.Min(loy, sp[1]); hiy = MathF.Max(hiy, sp[1]);
                    loz = MathF.Min(loz, sp[2]); hiz = MathF.Max(hiz, sp[2]);
                    maxR = MathF.Max(maxR, sp[3]);
                }
            }
            if (FrameDump.Capturing)
                FrameDump.Row_(cls, lit, v, _oMat[i], _oDbRecipe[i], mode, feat, dis, blend, noCull, slots, merge,
                               idxCount, _dOff[n], _dBase[n], r);
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
        public bool Lit, Dis, NoCull, HasGob, Moved, Posed, Dangly;
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
        // #126: the class and the blend pair are GL state, which one multidraw cannot vary inside.
        row.Blend = AlloyClasses.Blends(cls) ? (_oWash[i] << 16) | (_oSrc[i] << 8) | _oDst[i] : 0;
        // A blended batch never straddles two creatures: the frame prepasses and colours each run of one gob's
        // batches as a unit, so the gob is a key term and a tag on the batch.
        row.GobKey = cls == (int)AlloyClass.Blended ? RunKey(i) : IntPtr.Zero;
        // "has an instance behind it", which in DB mode is "is not tileset geometry".
        row.HasGob = db ? (SceneDb.ObjFlags[SceneDb.PartObj[_oDbPart[i]].Index] & ObjFlag.Tile) == 0
                        : _oGob[i] != IntPtr.Zero;
        // Automatic mesh LOD: the screen radius of the sphere the cull already needs. The RANGE lookup stays on
        // the main thread (a dictionary and a shared counter); only the level is arithmetic.
        int lvl = 0;
        if (MeshLod.On && _lodScale > 0f)
            fixed (float* sphAll = _dSph)
            {
                float* lsp = sphAll + (long)i * 4;
                if (lsp[3] < NeverCull)
                {
                    float dx = lsp[0] - _eyePos.X, dy = lsp[1] - _eyePos.Y, dz = lsp[2] - _eyePos.Z;
                    lvl = MeshLod.Level(lsp[3], MathF.Sqrt(dx * dx + dy * dy + dz * dz), _lodScale);
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
        fixed (float* sph = _dSph)
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
            int blend = AlloyClasses.Blends(_passCls[k]) ? (_oWash[i] << 16) | (_oSrc[i] << 8) | _oDst[i] : 0;
            IntPtr gobKey = _passCls[k] == (int)AlloyClass.Blended ? RunKey(i) : IntPtr.Zero;
            bool posed = _oBone[i] >= 0, dangly = !posed && DanglyPrevNow(i);
            bool recMoved = _recMovedAt[i] == _frameSeq;
            bool moved = posed ? recMoved || _poseMovedAt[i] == _frameSeq : dangly || recMoved && _oMx[i];
            // The LOD level is the one field the row DERIVES rather than copies, and it derives it on a worker
            // from two pass-wide statics -- so it is the field this audit exists for: a wrong level picks the
            // wrong MeshLod.Range, sets the wrong LodVaoBit and cuts the batch in the wrong place.
            int lvl = 0;
            if (MeshLod.On && _lodScale > 0f)
            {
                float* lsp = sph + (long)i * 4;
                if (lsp[3] < NeverCull)
                {
                    float dx = lsp[0] - _eyePos.X, dy = lsp[1] - _eyePos.Y, dz = lsp[2] - _eyePos.Z;
                    lvl = MeshLod.Level(lsp[3], MathF.Sqrt(dx * dx + dy * dy + dz * dz), _lodScale);
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
        public long MxChased, MxCached, DisAsked, DisNoFeat, DisNoLit;
        public bool CastChanged;
    }
    private static readonly StageCounters[] _stageCounters = new StageCounters[StagePool.MaxChunks];
    private static void FoldCounters(ref StageCounters c)
    {
        Counters._ownDissolve += c.OwnDissolve; Counters._nrmDeclared += c.NrmDeclared; Counters._nrmBound += c.NrmBound;
        Counters._recStable += c.RecStable; Counters._recWritten += c.RecWritten;
        _mxChased += c.MxChased; _mxCached += c.MxCached; _disAsked += c.DisAsked;
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
        fixed (float* data = _dData)
            for (int k = from; k < to; k++)
                _pkVerdict[k] = StagePart(k, data, slotTid, _stageLighting, ref _stageCounters[w], main: false);
    }
    private static int _stageRouted;
    private static bool _stageLighting;
    /// <summary><c>/alloy threads N</c>: worker threads beside the main thread for the stage. 0 = serial.</summary>
    private static int _stageThreads => Renderer.Threads.V;
    private static long _stageDeferred, _stageFrames;
    private static readonly byte[] _pkVerdict = new byte[MaxOwned], _pkDis = new byte[MaxOwned];
    /// <summary>The per-part half of <see cref="BuildBatches"/>: chase the state a routed part depends on and
    /// write its record, sphere and cast admission -- everything that touches ONLY this slot's arrays, so it
    /// is order-free where the batch walk is not. The worker rule (ADR 0022): off the main thread, a path that
    /// mints a GL object, logs, allocates or touches a shared collection returns <see cref="Deferred"/>.</summary>
    private static byte StagePart(int k, float* data, int* slotTid, bool lightingOn, ref StageCounters c, bool main)
        => StageDbPart(k, _pass[k], data, lightingOn, ref c, main);

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
    /// (<see cref="PostChain"/>): a far-depth pixel below the band is fog, not sky. Default = none this view.</summary>
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
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        fixed (float* t3 = _skTint)
        fixed (int* texAll = _skTex)
        for (int i = 0; i < _skyN; i++)
        {
            uint prog = _skProg[i] != 0 ? _skProg[i] : Programs._prog46;
            if (prog == 0) continue;
            uint src = Issue.BlendGl(_skSrc[i]), dst = Issue.BlendGl(_skDst[i]);
            if (src != boundSrc || dst != boundDst) { Gl.BlendFunc(src, dst); boundSrc = src; boundDst = dst; }
            if (prog != boundProg)
            {
                if (_skProg[i] != 0) SkyStage.Prepare(prog, vp, viewport);
                else { PrimeSkyPlain(vp, viewport); usedPlain = true; }
                boundProg = prog;
                // sampler units are per PROGRAM state but the bindings are the CONTEXT's, so only the
                // program-side cache resets here; the texture cache below is still valid.
            }
            // The draw carries the arena's VAO name.
            uint vaoName = _skVaoName[i];
            if (vaoName != boundVaoName) { Gl.BindVertexArray(vaoName); boundVaoName = vaoName; }
            int* tex = texAll + i * MaxEngTex;
            // Unit 0 for either stage; the rest only where a hosted stage could declare them. Units 1 and 2
            // are the shadow ARRAYS (TextureSlots.SunShadow/LocalShadow) and 3 is the material map block, so a
            // plain sky draw has no business touching them -- and the next BindPass is what puts a hosted
            // draw's clobber back, exactly as it did for #136's engine-program pass.
            int units = _skProg[i] != 0 ? MaxEngTex : 1;
            for (int k = 0; k < units; k++)
                if (tex[k] != boundTex[k]) { Gl.BindTextureUnit((uint)k, (uint)tex[k]); boundTex[k] = tex[k]; }
            int s = _skyFirst + i;
            if (_skProg[i] != 0) SkyStage.Draw(prog, s, t3 + i * 3, _skAlpha[i], SceneFog.SkyBand);
            else { Gl.Uniform3fv(Programs._locTint, 1, t3 + i * 3); Gl.Uniform1i(Programs._locBase, s); }
            Gl.MultiDrawElementsBaseVertex(_skMode[i], counts + s, GlUnsignedShort, (void**)(offs + s), 1, bases + s);
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
