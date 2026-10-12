using CX.Alloy.Db;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The SHADOW pass's view of the ownership table: the matrix, bone base, index range, VAO and world
/// sphere a depth bake needs, read from the records rather than a parallel copy (ADR 0019).
/// <para>Read-only and NON-CONSUMING -- the shadow bracket runs before the colour build, so a consuming read
/// would spend a mover signal the build needs. One frame late but coherent, so no bake is missed.</para>
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The frame this slot's record last CHANGED, stamped beside <see cref="RouteGate.RecFrame"/> (which is stamped
    /// every build, changed or not). The two being equal is the mover signal: the slot's most recent build
    /// was also a build that moved something.</summary>
    private static readonly int[] _recMovedAt = new int[MaxOwned];
    /// <summary>The caster's world sphere, written on the same edge as <c>_dSph</c> but always GEOMETRIC:
    /// centred on the part's own vertex box (<c>_oB*</c>), never <see cref="NeverCull"/>. A re-bake predicate
    /// needs bounds that name the views they dirty, which "somewhere" cannot.</summary>
    private static readonly float[] _dCastSph = new float[MaxRecords * 4];
    /// <summary>The pose slack <c>_dCastSph</c>'s radius carries for a skinned part (0 otherwise): what a cull
    /// needs and a shadow FRAMING must not take, or a body's tile frames a 6 m ball round a 2 m creature.</summary>
    private static readonly float[] _dCastSlack = new float[MaxRecords];
    /// <summary>A <c>PartDanglyMesh</c>: its vertices move while its node matrix does not, so no record
    /// compare can infer motion for it. Captured at enrollment, as the caster pass captured it before.</summary>
    private static readonly bool[] _oDangly = new bool[MaxOwned];
    /// <summary>The frame the dangly solve last DISPLACED this slot's vertices past its still threshold
    /// (<see cref="NoteDanglyMoved"/>). Alloy owns the solve, so "is this banner moving" is a measurement
    /// and not a declaration: a settled cloak is a caster, not a mover. Stamp 0 = never moved.</summary>
    private static readonly int[] _oDanglyMovedAt = new int[MaxOwned];
    /// <summary>Path length the solve has displaced this slot's vertices since its last reported move. A
    /// one-frame test would miss slow continuous deformation (a banner's lean following the wandering breeze
    /// at well under the threshold per frame) and freeze its static shadow at the pose it was baked in.</summary>
    private static readonly float[] _oDanglyAcc = new float[MaxOwned];
    /// <summary>Frames a dangly slot stays a mover after its last measured displacement. Two covers either
    /// order of the Animate hook against the frame edge; the caster pass's own 8-frame hold sits on top.</summary>
    private const int DanglyMoveHold = 2;
    /// <summary>The enrolment the table currently holds, which each slot's
    /// <see cref="RouteGate.EnrollGen"/> is stamped from (that field says why tenancy is not the record epoch).
    /// Starts at 1 so a record never built (stamp 0) disagrees with every real enrolment.</summary>
    private static int _enrollGen = 1;
    /// <summary>Casters admitted with an unknown (degenerate) sphere this frame -- each is a PERMANENT MOVER
    /// with infinite reach, so a nonzero steady value here means the re-bake predicate is being defeated and
    /// says by how much. Read and cleared by the caster period line.</summary>
    internal static int CasterDegenerate;

    /// <summary>Casters refused this frame for want of a record built for the slot's current tenant. Read
    /// and cleared by the caster period line; a number that stays high after an area settles means the colour
    /// build is skipping slots the bake wants.</summary>
    internal static int CasterNoRecord;

    /// <summary>May the shadow pass cast this slot? A LEVEL maintained on the record write edge -- true when a
    /// record landed for the slot's current tenant on a non-fading tile in a depth-writing class, false on
    /// every refusal. Every FLIP bumps <see cref="CasterGen"/>. One frame late like the record it rides
    /// with.</summary>
    private static readonly bool[] _castOk = new bool[MaxOwned];
    /// <summary>The slot's tile is mid-fade this frame, written by <c>RouteParts</c> where the fade is
    /// chased for the colour route anyway. Read on the record write edge into <see cref="_castOk"/>.</summary>
    private static readonly bool[] _oFading = new bool[MaxOwned];

    /// <summary>The caster WORLD generation -- bumped whenever the set of castable slots, or a fact the bake
    /// caches about one (its class, material slots, VDO item, tenant), changes. <see cref="CasterPass"/> keeps
    /// its collected list across frames while this holds and walks only movers in between. Compared, never
    /// consumed (ADR 0017).</summary>
    internal static int CasterGen = 1;

    /// <summary>The record epoch, for the shadow side's insurance re-collect: a bump retires texture copies
    /// (an animated diffuse cycling, a dial), which is the one input the collect caches that no slot fact
    /// names. Roughly once a second in a torch-lit area.</summary>
    internal static int RecordEpoch => _recEpoch;

    /// <summary>The slots whose record CHANGED in the latest colour build -- the only slots whose mover level
    /// or caster sphere can differ from the frame before. Reset by the first note of a new frame, so the
    /// shadow bracket (which runs BEFORE the build) reads the previous build's list intact; compared, never
    /// consumed. Overflow sets the flag and the refresh falls back to the full walk for that frame.</summary>
    private static readonly int[] _castTouched = new int[MaxOwned];
    private static int _castTouchedN, _castTouchedFrame = -1;
    private static bool _castTouchedOverflow;

    internal static void NoteCasterTouched(int i)
    {
        BeginCasterTouched();
        // reserve-then-write: the stage's workers append concurrently, after the main thread's Begin
        int n = System.Threading.Interlocked.Increment(ref _castTouchedN) - 1;
        if (n < _castTouched.Length) _castTouched[n] = i; else _castTouchedOverflow = true;
    }
    /// <summary>The per-frame reset, hoisted so it runs on the main thread before any worker can append.</summary>
    private static void BeginCasterTouched()
    {
        if (_castTouchedFrame != _frameSeq) { _castTouchedFrame = _frameSeq; _castTouchedN = 0; _castTouchedOverflow = false; }
    }
    internal static int CasterTouchedCount => _castTouchedN;
    internal static int CasterTouchedSlot(int k) => _castTouched[k];
    internal static bool CasterTouchedOverflow => _castTouchedOverflow;
    /// <summary>Off-device only: the self-check seeds the list and clears it again.</summary>
    internal static void ResetCasterTouched() { _castTouchedN = 0; _castTouchedFrame = -1; _castTouchedOverflow = false; }

    /// <summary>Publish the slot's cast admission, bumping <see cref="CasterGen"/> only on a change.</summary>
    private static void SetCastOk(int i, bool ok)
    {
        if (_castOk[i] == ok) return;
        _castOk[i] = ok;
        // Off (a freed slot, an inactive LOD) is handed to the standing list's off queue: its entry turns into
        // a ghost and a zero-count draw at the next refresh. Only an on-flip the list cannot absorb re-collects.
        if (!ok) { CasterPass.NoteSlotOff(i); return; }
        if (!CasterPass.InList(i)) CasterGen++;
    }
    /// <summary>Turns a slot's cast permission on or off. The generation bump is deferred to the fold, one per
    /// frame, so workers never touch the shared counter; consumers compare generations.
    /// <para>A flip is NOT a membership change while the caster list already holds this tenant -- its refresh
    /// turns the entry off or on. Only a slot turning on that the list never collected needs a re-collect.</para></summary>
    private static void SetCastOk(int i, bool ok, ref StageCounters c)
    {
        if (_castOk[i] == ok) return;
        _castOk[i] = ok;
        NoteCasterTouched(i);
        if (CasterPass.FlipCollect || (ok && !CasterPass.InList(i))) c.CastChanged = true;
    }
    internal static bool CastOk(int slot) => _castOk[slot];

    /// <summary>Cold diagnostic: what a caster slot IS -- its DB object's model, flags, scene (and whether that
    /// scene is a world one), position and the stored caster sphere. Engine slots name their texture.</summary>
    internal static string DescribeCasterSlot(int slot)
    {
        CasterSphere(slot, out float cx, out float cy, out float cz, out float r);
        string sph = $"sphere=({cx:F1},{cy:F1},{cz:F1}) r={r:F1}";
        int part = _oDbPart[slot];
        if (part < 0) return $"slot {slot} engine tex={SlotTexName(slot)} {sph}";
        DbId o = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(o)) return $"slot {slot} part {part} ORPHAN {sph}";
        int i = o.Index;
        DbId sc = SceneDb.ObjScene[i];
        bool world = SceneDb.Scenes.Valid(sc) && SceneDb.SceneIsWorld[sc.Index];
        return $"slot {slot} part {part} '{PartTree.NameOf(part)}' model='{SceneDb.ObjSeamModelName[i]}' flags={SceneDb.ObjFlags[i]} "
             + $"scene={sc.Value}{(world ? "(world)" : "(NOT world)")} visible={SceneDb.ObjVisible[i]} cls={(AlloyClass)_oCls[slot]} "
             + $"pos=({SceneDb.ObjPos[i].X:F1},{SceneDb.ObjPos[i].Y:F1},{SceneDb.ObjPos[i].Z:F1}) {sph}";
    }

    /// <summary>Cold diagnostic: is this slot's DB object in a scene that is not a world scene (a GUI panel's)?</summary>
    internal static bool CasterNotWorld(int slot)
    {
        int part = _oDbPart[slot];
        if (part < 0) return false;
        DbId o = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(o)) return true;
        DbId sc = SceneDb.ObjScene[o.Index];
        return !SceneDb.Scenes.Valid(sc) || !SceneDb.SceneIsWorld[sc.Index];
    }
    /// <summary>Off-device only: the caster self-check seeds admission for its synthetic slots.</summary>
    internal static void SelfCheckCastOk(int slot, bool ok) => _castOk[slot] = ok;
    /// <summary>Off-device only: seeds a slot's stored caster sphere, which the vanish sweep reads as a
    /// departing caster's last known bounds.</summary>
    internal static void SelfCheckCasterSphere(int slot, float cx, float cy, float cz, float r)
    {
        fixed (float* sp = _dCastSph)
        {
            float* s = sp + (long)slot * 4;
            s[0] = cx; s[1] = cy; s[2] = cz; s[3] = r;
        }
    }
    internal static int SlotEnrollGen(int slot) => _gate[slot].EnrollGen;

    /// <summary>The mover LEVEL for one slot: its most recent build was also a build that moved something,
    /// and that build was last frame's or this frame's. The route walk no longer stamps slots it skips, so
    /// without the frame test a slot that moved on its last build would read as a mover for as long as it
    /// stays out of view; the fold publishes a moved record ahead of the shadow bracket, hence "this".</summary>
    internal static bool SlotRecMoved(int slot)
        => _recMovedAt[slot] == _gate[slot].RecFrame
           && (_gate[slot].RecFrame == _frameSeq - 1 || _gate[slot].RecFrame == _frameSeq);
    /// <summary>A dangly slot whose solve displaced it within <see cref="DanglyMoveHold"/> frames. A LEVEL:
    /// the stamp is re-read, never consumed.</summary>
    internal static bool SlotDangly(int slot) => _oDangly[slot] && _frameSeq - _oDanglyMovedAt[slot] <= DanglyMoveHold;
    /// <summary>Is the slot a <c>PartDanglyMesh</c> at all, moving or not. Its VDO is committed every frame
    /// the solve runs, so the vertex-dirty mover term must not speak for it -- the measured verdict does.</summary>
    internal static bool SlotIsDangly(int slot) => _oDangly[slot];

    /// <summary>The dangly solve's report for <paramref name="part"/>: this frame's largest per-vertex
    /// displacement, accumulated against <paramref name="still"/> (infinity forces a move). True when the
    /// slot became a mover. Hot (per dangly part per frame), allocation-free. A part not (yet) seated is not a
    /// caster and needs no stamp.</summary>
    internal static bool NoteDanglyMoved(int part, float dist, float still)
    {
        int i = SlotOfPart(part);
        if (i < 0 || !Accumulate(ref _oDanglyAcc[i], dist, still)) return false;
        _oDanglyMovedAt[i] = _frameSeq;
        return true;
    }

    /// <summary>The ownership slot database part <paramref name="part"/> is seated in, or -1.</summary>
    private static int SlotOfPart(int part)
    {
        int i = (uint)part < SceneDb.MaxParts ? SceneDb.PartSlot[part] : -1;
        return i >= 0 && _oDbPart[i] == part ? i : -1;
    }

    /// <summary>The mover verdict: the displacement path length since the last report crosses the threshold.
    /// A <paramref name="still"/> of 0 reports every frame with any motion.</summary>
    internal static bool Accumulate(ref float acc, float dist, float still)
    {
        acc += dist;
        if (!(acc > still)) return false;
        acc = 0f;
        return true;
    }

    /// <summary>Entries in the draw order -- the caster walk's own bound.</summary>
    internal static int OrderCount => _orderCount;
    /// <summary>The ownership slot at draw-order position <paramref name="k"/>. Grouped by class then VAO,
    /// which is the order a bake wants to batch in anyway.</summary>
    internal static int OrderSlot(int k) => _order[k];

    /// <summary>Everything one shadow caster is, resolved out of the ownership table and the records. Values,
    /// not references: the bake stages an index range and a slot number, and reads the rest through
    /// <c>uDraw[slot]</c> on the GPU.</summary>
    internal struct CasterFacts
    {
        public IntPtr Vdo, Mat;
        public int Item, Prim, Vao;
        /// <summary>The ownership slot these facts came from. A DB slot has no VDO, so the bake resolves its
        /// index range from the arena draw arguments instead (<c>AlloyRenderer.DbCasterRange</c>).</summary>
        public int Slot;
        /// <summary>World sphere: centre and radius, both from <see cref="_dCastSph"/>.</summary>
        public float Cx, Cy, Cz, R;
        /// <summary>The material's diffuse alpha is REFLECTIVITY rather than coverage (env-mapped), so its
        /// cutout runs at the threshold that only drops empty texels.</summary>
        public bool EnvAlpha;
        /// <summary>A blended part -- it casts at its record alpha (<c>rot.w</c>), so it takes the masked
        /// stage whatever its diffuse says.</summary>
        public bool Blended;
        /// <summary>A declared coverage part whose coverage the mask stage can READ (the diffuse alpha): it
        /// casts at the layer's own cut, dithered, so the shadow follows the coverage the draw uses.</summary>
        public bool Coverage;
        /// <summary>The record changed on its last build -- the matrix, the fade, a material colour.</summary>
        public bool RecMoved;
        /// <summary>A <c>PartDanglyMesh</c> the solve displaced this frame or last -- a mover no record compare
        /// can see. Still danglies are casters, not movers (<see cref="SlotDangly"/>).</summary>
        public bool Dangly;
        /// <summary>Posed by bones, so it is exempt from the sub-texel LOD cull for the same reason the
        /// colour cull exempts it: the player's own shadow must not flicker out in a far cascade.</summary>
        public bool Skinned;
    }

    /// <summary>Resolve one ownership slot as a shadow caster. False = this slot casts nothing: it is not an
    /// opaque or cutout part, its ownership was revoked, or the record write refused it (see
    /// <see cref="_castOk"/>). Never false for a slot whose record is merely epoch-stale: with agreeing
    /// tenancy it still carries the right geometry, and geometry is all the depth stages read.</summary>
    internal static bool TryCaster(int slot, out CasterFacts f)
    {
        f = default;
        // Opaque, cutout and (#363) blended -- the last at its alpha, through the masked stage's dither. An
        // additive part emits light and water is its own pass. Same rule the caster census applied.
        if (_oTri[slot] == IntPtr.Zero || _oCls[slot] > (byte)AlloyClass.Blended) return false;
        // #328: tenancy and tile fade, as the level the record write left -- deliberately NOT the record
        // epoch, see RouteGate.EnrollGen for the once-a-second flicker asking the wider question produced.
        if (!_castOk[slot]) { CasterNoRecord++; return false; }
        return TryCasterGeom(slot, out f);
    }
    /// <summary><see cref="TryCaster"/> without the admission gate: the geometry facts of a freshly seated
    /// slot whose record has not landed yet, so the standing caster list can take it as an OFF entry that the
    /// stage's admission turns on.</summary>
    internal static bool TryCasterGeom(int slot, out CasterFacts f)
    {
        f = default;
        if (_oTri[slot] == IntPtr.Zero || _oCls[slot] > (byte)AlloyClass.Blended) return false;
        f.Blended = _oCls[slot] == (byte)AlloyClass.Blended;
        if (f.Blended && _oBone[slot] >= 0) return false;   // posed: no mask stage, see SetCastOk
        f.Slot = slot;
        f.Vdo = _oVdo[slot]; f.Mat = _oMat[slot];
        // CX-120: the DENSE caster VAO id (see DbVaoBase), not _oVao -- a DB slot has no VDO slot, and its
        // arena section is what the bake must group and bind by. Every engine table this value still indexes
        // range-tests it first.
        f.Item = _oItem[slot]; f.Prim = _oPrim[slot]; f.Vao = CasterVaoId(slot);
        f.EnvAlpha = (_oSlots[slot] & EnvSlot) != 0;
        f.Skinned = _oBone[slot] >= 0;
        // CX-227: a part whose coverage source the depth stage can also sample; one whose cannot keeps the
        // engine cut rather than casting a 0.01 silhouette off a texture it does not read. Under the POSE
        // override, so a skinned palette part is judged as the palette part it is.
        f.Coverage = _oA2c[slot]
                  && CoverageLayer.HasCoverageSource(ShaderFeatures.WithPose((int)_oFeat[slot * 4], f.Skinned));
        // Read as a LEVEL, never as an edge: the gate row's RecFrame is stamped on every build and _recMovedAt only on a
        // build that changed something, so the two agreeing is "the last build moved it" and asking twice
        // gives the same answer. ADR 0017's hazard was a signal the first pass to ask consumed.
        f.Dangly = SlotDangly(slot);
        f.RecMoved = SlotRecMoved(slot);
        MoverSphere(slot, out f.Cx, out f.Cy, out f.Cz, out f.R);
        return true;
    }

    /// <summary>A caster's sphere as the bake ADMITS it: unknown BOUNDS mean cast (an unknown TENANT does not
    /// -- that is <see cref="_castOk"/>), and a degenerate sphere becomes an infinite radius, which passes
    /// every reach test and fails every sub-texel one, so such a caster is neither culled nor LOD-dropped.
    /// COUNTED: an infinite mover defeats the re-bake predicate in every view it reaches (ADR 0019 S3).</summary>
    internal static void MoverSphere(int slot, out float cx, out float cy, out float cz, out float r)
    {
        CasterSphere(slot, out cx, out cy, out cz, out r);
        if (r > 0f) return;
        cx = 0f; cy = 0f; cz = 0f; r = NeverCull;
        CasterDegenerate++;
    }

    /// <summary>Cold, for the <c>/shadow movers</c> dump: the slot's diffuse texture name, the one label a
    /// human can read. A DB slot has no Material, so its diffuse is a registry slot. Borrowed pointers,
    /// resolved inside the pass slot.</summary>
    internal static string SlotTexNameDb(int slot)
        => _oDbPart[slot] >= 0
            ? TexRegistry.ResrefOf(DrawRecipe.Tex(_oDbRecipe[slot], 0))
            : SlotTexName(slot);

    internal static string SlotTexName(int slot)
        => _oTri[slot] == IntPtr.Zero ? "-" : AlloyCensus.TexName(new MaterialRef(_oMat[slot]).Texture(0).Ptr);

    /// <summary>Is this ownership slot posed by bones? The cheap gate the caster walk uses to put the
    /// skinned casters at the head of its list without resolving every slot twice.</summary>
    internal static bool SlotSkinned(int slot) => _oBone[slot] >= 0;

    /// <summary>The engine wrote vertices into this VDO's position pool this frame, so every caster on it
    /// moved without its matrix moving. Resolved per VDO slot, not per caster.</summary>
    internal static bool VdoDirty(int v)
        => !IsArenaVao(v) && (uint)v < MaxVdos && _vVdo[v] != IntPtr.Zero && OwnedBuffers.DirtySince(_vPos[v].Buf);

    /// <summary>The ITEM-level vertex-dirty signal. <see cref="VdoDirty"/> is per pool BUFFER and the engine
    /// pools a whole area into a handful of them, so one dangly commit would mark every caster on the pool a
    /// mover; <c>CommitItemDynamicData</c> names the item, so a caster is dirty only if ITS item was
    /// committed. The motion writers that bypass the commit are covered by the dangly and skinned flags.</summary>
    internal static bool ItemDirty(int v, int item) => VdoDirty(v) && ItemMoved(v, item);
    /// <summary>The item half alone, for a caller that already holds the buffer verdict per VDO.</summary>
    internal static bool ItemMoved(int v, int item) => _ciOverflow || ItemCommitted(v, item);

    private const int CiBits = 14;
    private static readonly ulong[] _ciKey = new ulong[1 << CiBits];
    private static readonly int[] _ciGen = new int[1 << CiBits];
    private static readonly int[] _ciPerVdo = new int[MaxVdos];
    private static int _ciGeneration = 1, _ciCount;
    private static bool _ciOverflow;
    private static VdoCommitItemDynamicDataFn* _commitSlot;
    private const int CommitPriority = 10;
    private static long _ciCommits, _ciOverflows;

    private static int CiHash(ulong key) => (int)(key * 0x9E3779B97F4A7C15UL >> (64 - CiBits));

    private static bool ItemCommitted(int v, int item)
    {
        ulong key = ((ulong)(uint)v << 32) | (uint)item;
        int mask = (1 << CiBits) - 1;
        for (int j = CiHash(key); _ciGen[j] == _ciGeneration; j = (j + 1) & mask)
            if (_ciKey[j] == key) return true;
        return false;
    }

    /// <summary>Record one item commit. Engine thread, hot (every dangly/morph item per frame): a slot scan
    /// over ~10 VDOs and one hash insert, nothing allocated. Past the table's load the frame falls back to
    /// the buffer-level signal rather than dropping a commit.</summary>
    internal static void NoteItemCommit(IntPtr vdo, uint item)
    {
        _ciCommits++;
        int v = -1;
        for (int k = 0; k < _vdoCount; k++) if (_vVdo[k] == vdo) { v = k; break; }
        if (v < 0) return;
        if (_ciCount >= (1 << CiBits) * 3 / 4) { if (!_ciOverflow) _ciOverflows++; _ciOverflow = true; return; }
        ulong key = ((ulong)(uint)v << 32) | item;
        int mask = (1 << CiBits) - 1;
        int j = CiHash(key);
        for (; _ciGen[j] == _ciGeneration; j = (j + 1) & mask) if (_ciKey[j] == key) return;
        _ciGen[j] = _ciGeneration; _ciKey[j] = key; _ciCount++; _ciPerVdo[v]++;
    }

    /// <summary>Close the item window with the buffer one (<c>OwnedBuffers.ClearDirty</c>): the readers ran.</summary>
    internal static void ClearItemCommits()
    {
        _ciGeneration++; _ciCount = 0; _ciOverflow = false;
        Array.Clear(_ciPerVdo, 0, MaxVdos);
    }

    [UnmanagedCallersOnly]
    private static void OnCommitItemDynamicData(IntPtr vdo, uint item, uint flags)
    {
        NoteItemCommit(vdo, item);
        if (_commitSlot != null && *_commitSlot != null)
            (*_commitSlot)(vdo, item, flags);
    }

    /// <summary>The item table: a committed item is dirty, its pool-mates are not, a pool with no commit
    /// stays dirty as a whole, and the window closes.</summary>
    internal static void SelfCheckItemDirty(ExtenderLog log)
    {
        int sv = _vdoCount; var svVdo = new IntPtr[MaxVdos]; Array.Copy(_vVdo, svVdo, MaxVdos);
        ClearItemCommits();
        _vdoCount = 2; _vVdo[0] = (IntPtr)0x10; _vVdo[1] = (IntPtr)0x20;
        NoteItemCommit((IntPtr)0x10, 7); NoteItemCommit((IntPtr)0x10, 7);
        bool ok = ItemCommitted(0, 7) && !ItemCommitted(0, 8) && !ItemCommitted(1, 7) && _ciPerVdo[0] == 1 && _ciCount == 1;
        ok &= _ciPerVdo[1] == 0 && !ItemMoved(1, 7) && !ItemMoved(0, 8) && ItemMoved(0, 7);   // no commit = nothing moved
        NoteItemCommit((IntPtr)0x30, 1); ok &= _ciCount == 1;   // an unknown VDO is not booked
        ClearItemCommits();
        ok &= !ItemCommitted(0, 7) && _ciPerVdo[0] == 0;
        _vdoCount = sv; Array.Copy(svVdo, _vVdo, MaxVdos);
        log.Info(ok ? "AlloyRenderer.SelfCheckItemDirty: passed" : "AlloyRenderer.SelfCheckItemDirty: FAILED");
    }

    /// <summary>The last sphere written for a slot, live or revoked. A caster that VANISHED leaves no live
    /// entry to name the views it was standing in, so its last known sphere is what invalidates them.</summary>
    /// <summary>The caster sphere's radius without its pose slack: the bound a shadow view frames on.</summary>
    internal static float CasterFrameRadius(int slot)
    {
        CasterSphere(slot, out _, out _, out _, out float r);
        return r - _dCastSlack[slot];
    }

    internal static void CasterSphere(int slot, out float cx, out float cy, out float cz, out float r)
    {
        fixed (float* sp = _dCastSph)
        {
            float* s = sp + (long)slot * 4;
            cx = s[0]; cy = s[1]; cz = s[2]; r = s[3];
        }
    }

    /// <summary>The caster VAO id is DENSE across both geometry sources: an engine slot keeps its VDO slot
    /// number, a DB slot (no VDO at all) takes <c>MaxVdos + </c><see cref="MeshArena.Fmt"/>, so the caster
    /// grouping cuts the arena's static and skinned sections apart as it cuts two VDOs apart and every
    /// VDO-indexed table still works after one range test.</summary>
    internal const int DbVaoBase = MaxVdos;

    /// <summary>Is this caster VAO id the mesh arena's rather than a VDO slot's?</summary>
    internal static bool IsArenaVao(int v) => v >= DbVaoBase;

    /// <summary>How many distinct caster VAO ids exist: every VDO slot, then one per arena section.</summary>
    internal const int CasterVaoIds = DbVaoBase + 2;

    /// <summary>The caster VAO id for an ownership slot, from whichever source it draws out of.</summary>
    internal static int CasterVaoId(int slot)
        => DbSlot(slot) ? DbVaoBase + (int)MeshArena.FormatOf(DrawRecipe.Range(_oDbRecipe[slot])) : _oVao[slot];

    /// <summary>The arena's vertex format always carries UVs (pos3 nrm3 uv2 ...), so a DB caster can always be
    /// alpha-masked.</summary>
    internal static bool VdoHasUv(int v) => IsArenaVao(v) || ((uint)v < MaxVdos && _vUv[v].Present);

    /// <summary>The 4.6 VAO for a VDO slot, built on demand. Shadow and colour draw in the SAME context, so
    /// there is one VAO for both -- the whole reason the caster pass no longer keeps its own cache. 0 = the
    /// harvest could not build one and this caster does not cast this frame.</summary>
    internal static uint CasterVao(int v)
        => IsArenaVao(v) ? MeshArena.Vao((MeshArena.Fmt)(v - DbVaoBase)) : 0u;

    /// <summary>Triage: what the bake will transform this slot by, beside what the engine says it is right
    /// now. The record is the shadow pass's ONE input for placement, so a disagreement here is the whole bug.
    /// <para><paramref name="lag"/> is the record's translation, <paramref name="live"/> the node's this
    /// instant: one frame's motion apart on a mover, identical on a still part, wildly apart if never built.</para></summary>
    internal static void CasterProbe(int slot, out Vector3 lag, out Vector3 live,
                                     out bool mx, out bool known, out int feat)
    {
        fixed (float* d = _dData)
        {
            var m = *(Matrix4x4*)(d + (long)slot * DrawFloats);
            lag = new Vector3(m.M41, m.M42, m.M43);
            feat = (int)d[(long)slot * DrawFloats + 40];
        }
        mx = _oMx[slot];
        known = _gate[slot].RecGen == _recEpoch;
        // CX-103: "live" is what Alloy composes for the slot THIS instant, not what the engine's draw-bucket
        // build left at vp+0x18 -- the record is built from the composition, so that is the pair that has to
        // agree. alloy.xformcheck is where the engine's matrix is compared.
        // CX-120 (R1-35): a DB slot's live matrix is its column -- there is nothing to compose and the
        // _oTri below is a sentinel that would fault if this diagnostic dereferenced it.
        live = _oDbPart[slot] >= 0 ? SceneDb.PartWorld[_oDbPart[slot]].Translation : Vector3.Zero;
    }

    /// <summary>The shadow pass's own copy of the buffer names it binds: the persistent per-draw records and
    /// the bone block, both of which the bake reads through the SAME bindings the colour path uses.</summary>
    internal static uint DrawRecordBuf => _drawBuf;
    internal static uint BoneBlockBuf => _boneBuf;
    /// <summary>The bindless handle block the lit stages sample through; 0 until the first bindless upload.</summary>
    internal static uint TexHandleBuf => _texHBuf;
    /// <summary>The slot's diffuse handle as the block holds it; 0 = never minted (unlit, unmerged, or
    /// refused), which a sampler reads as black.</summary>
    internal static ulong RecordHandle0(int slot) => _dTexH[(long)slot * 8];
}
