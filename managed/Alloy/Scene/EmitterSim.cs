using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The particle simulation, Alloy's: the walk, the per-emitter step and the camera-distance sort that replace
/// <c>Scene::DoEmitterBucket</c> @0x476f00 (ADR 0018 states what the engine still owns). Zero allocation per
/// frame -- arenas grown at load edges, spawn and death moving POINTERS inside an emitter's own slice -- and
/// the live slice stays oldest-first, because index-domain fades and the linked types read neighbours.
/// </summary>
internal static unsafe partial class EmitterSim
{
    /// <summary>Emitters tracked at once. Past this an emitter is not stepped and draws nothing, which is
    /// the same cost the draw table's own cap has (<c>Particles</c> MaxEmitters).</summary>
    private const int MaxEmitters = 4096;
    /// <summary>Particles one emitter may hold. The engine has NO ceiling: every Update births from the recycle
    /// list at <c>emitter+0x128</c> and calls <c>operator new</c> when it is empty, so <c>birthRate x life</c>
    /// is an emergent steady state there, not a bound -- every birth this refuses is a particle vanilla would
    /// have drawn, so it is loud once and counted. Derived as <see cref="MaxSlots"/>/64, 480KB of 30MB.</summary>
    private const int MaxPerEmitter = MaxSlots / 64;
    /// <summary>Pool growth, in particles: start here, double until the frame fits, stop at the cap (0x78
    /// bytes each, so the cap is 30 MiB) and start the arena over rather than truncating one emitter.</summary>
    private const int MinSlots = 4096, MaxSlots = 1 << 18;

    /// <summary>Two turns, the wrap every roll and every random angle is taken modulo. <c>6.2831855f</c> is
    /// the engine's own literal, not <c>MathF.Tau</c> rounded differently.</summary>
    private const float TwoPi = 6.2831855f;
    /// <summary>Gravity, as the engine spells it: <c>mass * -9.81 * dt</c> added to the Z velocity every
    /// step, in every one of the three movers.</summary>
    private const float Gravity = -9.81f;
    /// <summary>The engine's two random quantisations: an angle or a distance is drawn as
    /// <c>rand() % (int)(x * 100)</c> hundredths, and a spawn offset as ten-thousandths.</summary>
    private const float Centi = 0.01f, Deci4 = 0.0001f;
    /// <summary>The floor under a "does this value do anything" test: the engine compares against 0.01
    /// before it divides a range into hundredths, because <c>rand() % 0</c> would trap.</summary>
    private const float Epsilon = 0.01f;
    /// <summary>The full circle, in the hundredths of a radian the spawn's azimuth draw works in
    /// (<c>rand() % 0x274</c> in <c>Particle::randomDirection</c> @0x4ab160 -- 628, i.e. 2π).</summary>
    private const int AzimuthTicks = 628;
    /// <summary>Quarter turn. <c>randomDirection</c>'s cone clamp measures against
    /// <c>cos(HalfPi - coneCap)</c> and folds an out-of-cone direction back through it.</summary>
    private const float HalfPi = 1.5707964f;
    /// <summary>The fallback interpolation rate for an emitter with no lifetime, and the cone clamp's own
    /// iteration ceiling -- the engine's loop is unbounded and can only terminate on float luck.</summary>
    private const int MaxConeFolds = 8;

    // ---- the emitter's update mode, as MdlNodeEmitter::InternalCreateInstance @0x4af5c0 picks it ----
    private const int ModeFountain = 0, ModeSingle = 1, ModeExplosion = 2, ModeLightning = 3;
    // ---- the mover, as PartEmitter::Initialize @0x4aaaa0 picks it ----
    private const int MoveStandard = 0, MoveBezier = 1, MoveGrav = 2;

    /// <summary>The <c>MdlNodeEmitter</c> flag bits, each one an <c>Is*</c> accessor in the binary
    /// (<see cref="EngineLayout.EmitterFlags"/> lists them). Read as a word once per emitter per frame
    /// instead of through eleven virtual calls.</summary>
    private const int FP2P = 1 << 0, FP2PSel = 1 << 1, FWind = 1 << 2,
                      FBounce = 1 << 4, FRandom = 1 << 5, FInherit = 1 << 6, FInheritVel = 1 << 7,
                      FInheritLocal = 1 << 8, FInheritPart = 1 << 10;

    /// <summary>One tracked emitter. The mode, the mover and the chunk verdict are the node's and never
    /// change, so they are resolved once when the slot is taken rather than per frame.</summary>
    private struct Slot
    {
        /// <summary>The database part this slot belongs to, or -1 for a free slot. <see cref="Gen"/> is what
        /// makes it safe across a recycle.</summary>
        public int Part;
        /// <summary>The <see cref="DbId"/> generation of the object that owned <see cref="Part"/> when the slot
        /// was taken. A part index the database re-tenanted is a different emitter, and inheriting its
        /// population would show a stranger's particles at the new one's place.</summary>
        public uint Gen;
        /// <summary>The simulation state. The slot IS the emitter's memory, so it survives between frames
        /// rather than being stored back into an engine object.</summary>
        public EmitMut Mut;
        /// <summary>DB mode: the emitter texture, acquired when the slot opens and released when it closes.
        /// </summary>
        public uint Tex;
        /// <summary>This emitter's block: <see cref="Base"/> records in the pool and the same number of
        /// entries in the pointer table, <see cref="Live"/> of them live and in order.</summary>
        public int Base, Cap, Live;
        /// <summary>Has <see cref="EmitterSim.Open"/> run for this tenant? A chunk emitter takes no pool block,
        /// so a zero <see cref="Cap"/> cannot stand in for "not resolved yet".</summary>
        public bool Opened;
        public int Mode, Mover;
        /// <summary>The particles are <c>CAurObject</c>s, so this emitter stays the engine's (ADR 0018):
        /// Alloy calls <c>AnimateParticles</c> and <c>RenderNothing</c> on it instead.</summary>
        public bool Chunk;
        /// <summary>Frame last seen in the bucket, so a slot an area walked away from can be reclaimed --
        /// an emitter that is merely out of view never destructs.</summary>
        public int Seen;
        /// <summary>Squared camera distance, the sort's primary key.</summary>
        public float Dist;
        /// <summary>The sort's TIEBREAK, both halves of it: the owning Gob (two emitters only tie-break
        /// against each other when they share one) and the model's declared render order. Both are fixed for
        /// the emitter's life, so they are read once when the slot is taken.</summary>
        public IntPtr Gob;
        public short Order;
        /// <summary>Per-emitter random state. Seeded from the pointer so two copies of one effect do not
        /// spawn in lockstep, and advanced only here so a SelfCheck can drive the whole simulation
        /// deterministically.</summary>
        public uint Rng;
    }

    private static readonly Slot[] _tab = new Slot[MaxEmitters];
    private static int _tabN;
    /// <summary>Slot index by part, plus one -- zero for "this part has no slot", replacing a linear scan of
    /// the table per in-view emitter. Written in exactly three places so it cannot drift from
    /// <see cref="Slot.Part"/>: <see cref="FindDb"/>, <see cref="Unmap"/> and <see cref="Reset()"/>. An index,
    /// never the authority -- a stale entry reads as no entry and a missing one costs one frame's scan.</summary>
    private static readonly int[] _slotOfPart = new int[SceneDb.MaxParts];
    /// <summary>Bucket indices in DRAW order -- back to front, which is what the blended class needs.</summary>
    private static readonly int[] _order = new int[MaxEmitters];
    private static int _orderN;

    private static byte* _pool;
    private static IntPtr* _ptrs;
    private static int _slotCap, _slotTop;

    private static ExtenderLog? _log;
    private static EngineList _bucket;
    private static float* _dt;
    private static PartGetWorldFn _getWorld;
    private static AnimateParticlesFn _animate;
    private static RenderNothingFn _renderNothing;
    private static IntPtr* _currentCamera;

    /// <summary>Resolved and ready to take the walk? <see cref="RspShim"/> chains <see cref="Missing"/> into
    /// its own stand-down list, so a false here takes the WHOLE Alloy path with it rather than leaving an
    /// Alloy frame with the engine's emitter code running inside it.</summary>
    internal static bool Armed { get; private set; }
    /// <summary>The first symbol that did not resolve, or null. Read by <see cref="RspShim.Install"/>, the
    /// way <see cref="DoorFade"/> is.</summary>
    internal static string? Missing { get; private set; }
    private static bool _poolWarned;

    private static int _resets;
    private static int _frame;
    private static int _emitters, _particles, _spawned, _died, _chunk, _p2pNoTarget,
                       _capped, _poolResets, _reclaimed, _full, _foreign;
    /// <summary>The largest block any emitter's own parameters asked for this period, and births actually
    /// LOST to a full block -- the pair that says whether our ceiling costs a visible particle.</summary>
    private static int _capPeak, _capLost;
    private static bool _capSaid;

    /// <summary>Births a full block refused. EVERY one of them: the engine has no per-emitter ceiling
    /// (<c>operator new</c> on an empty recycle list, <c>FountainEmitter::Update</c> @0x4b1c70), so a refusal
    /// here is always a particle vanilla would have drawn, whether the block was sized by the emitter's own
    /// parameters or clamped by <see cref="MaxPerEmitter"/>. Loud once, counted after.</summary>
    private static void CapLost(int n)
    {
        if (n <= 0) return;
        _capLost += n;
        if (_capSaid) return;
        _capSaid = true;
        _log?.Always($"EmitterSim: an emitter's block is full and it is losing births -- {n} on this step. Its "
                   + $"parameters asked for {_capPeak} particles against our {MaxPerEmitter} ceiling; vanilla "
                   + "has no ceiling at all. Raise MaxPerEmitter to the peak the [emitsim] line reports, or "
                   + "widen Want if the burst is what overran it. Counted, not repeated, from here on");
    }

    /// <summary>Clamp a birth burst to what the block can still hold, counting what that costs.</summary>
    private static int Room(ref Slot slot, int births)
    {
        int room = slot.Cap - slot.Live;
        if (room < 0) room = 0;
        if (births <= room) return births;
        CapLost(births - room);
        return room;
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _bucket = new EngineList(Native.Sym(SymbolMap.EmitterBucket));
        _dt = (float*)Native.Sym(SymbolMap.DeltaT);
        _currentCamera = (IntPtr*)Native.Sym(SymbolMap.CurrentCamera);
        _getWorld = EngineFn.PartGetWorld;
        _animate = EngineFn.AnimateParticles;
        _renderNothing = EngineFn.RenderNothing;
        AurXform.Resolve();

        // Item 7 of the standards review: this used to name CurrentScene for an unresolved CAMERA.
        // CX-104: emitterbucket is NOT on this list any more. The population is SceneParts' registry, and the
        // bucket is kept resolved only for the /buckets emitters 0 A/B and the validator -- so a global that
        // does not resolve no longer stands the whole Alloy path down over a list nothing reads.
        Missing = _dt == null ? SymbolMap.DeltaT
                : _currentCamera == null ? SymbolMap.CurrentCamera
                : _getWorld == null ? SymbolMap.PartGetWorld
                : _animate == null ? SymbolMap.AnimateParticles
                : _renderNothing == null ? SymbolMap.RenderNothing
                : !AurXform.Ready ? AurXform.Missing : null;
        if (Missing != null)
        {
            log.Always($"EmitterSim: {Missing} did not resolve -- the owned simulation cannot run, so the "
                     + "whole Alloy path stands down (RspShim chains this) and the engine renders the frame. "
                     + "Handing the walk back mid-frame is not on offer: that would put the engine's emitter "
                     + "code back inside an Alloy frame, which is what CX-81 tier 1 removes");
            return;
        }
        Armed = true;
        log.Info($"EmitterSim: armed and {(Enabled.V ? "ON" : "IDLE")} -- Scene::DoEmitterBucket replaced "
               + "(/emitsim enabled 0 freezes every particle; nothing else steps them)");
    }

    // ---- the walk ------------------------------------------------------------------------------------

    /// <summary>The same walk over the scene database: the population is <see cref="DbInputs"/>'s emitter
    /// table, the parameters are the parsed model's blended with the animation channels, and the per-emitter
    /// state lives in the slot. Runs once per FRAME, not once per view -- <paramref name="frame"/> is the
    /// feed's own counter, so a second world view re-enrols the same particles instead of stepping them twice.
    /// </summary>
    internal static void StepDb(DbId scene, int frame)
    {
        if (!Armed || !Enabled.V || _dt == null) return;
        long tEmit = FrameZones.Now;
        bool stepping = frame != _dbFrame;
        _dbFrame = frame;
        if (stepping) _frame++;
        float dt = stepping ? *_dt : 0f;
        if (stepping) { _dtSum += dt; _steps++; }
        // The engine's gate is Scene::IsAnimating @0x55e000, whose whole body is the cached byte at
        // Scene+0xb9 that Scene::Animate writes. Its DB twin is SceneDb.SceneAnimating, and beside it sits
        // SceneAnimOn -- the Enable/DisableAnimations switch, which is a different flag. TODAY NEITHER IS
        // TRUE for a DB world scene: nothing writes the first at all, and the second reads false (measured,
        // see UngatedOnce). Holding on that would freeze every particle in the world for the whole session,
        // so the step runs and the period line names which gate answered.
        bool animating = DbScene.IsAnimating(scene) || DbAnim.IsSceneAnimating(scene);
        if (!animating && stepping) { animating = true; _ungated++; UngatedOnce(scene); }
        Vector3 eye = FrameInputs.HasCamera ? FrameInputs.CameraPos : Vector3.Zero;
        _eye = eye; _hasEye = FrameInputs.HasCamera;
        bool cull = BuildViewPlanes();

        _orderN = 0;
        _inView = 0; _outOfView = 0; _selN = 0;
        _dbAnim = animating; _dbScene = scene.Value;
        _dbDead = 0; _dbDisabled = 0; _dbOtherScene = 0; _dbNoScene = 0; _dbNoDef = 0;
        _dbRMin = float.MaxValue; _dbRMax = 0f;
        int count = DbInputs.EmitterCount;
        Emitter e = default;
        for (int k = 0; k < count; k++)
        {
            if (!DbInputs.Emitter(k, out DbEmitter de)) { _dbDead++; continue; }
            if (!de.Enabled) { _dbDisabled++; Census(de.Part, "disabled"); continue; }
            // An object in another scene is another view's; the world pass is per scene (CX-120's per-view
            // filter) and a stranger's sparks must not land in this one.
            if (!SceneDb.Objects.Valid(de.Obj) || SceneDb.ObjScene[de.Obj.Index].Value != scene.Value)
            {
                // Split, because the two verdicts mean opposite things. "No scene at all" is the game's own
                // prototype stock -- a creature's eight `vwp_*_chunk_*` gore models and the spare
                // `fx_flame01`s are made by NewCAurObject and only attached when something dies or lights up,
                // and vanilla's walk never sees them because it walks a SCENE. "Another scene" is a real
                // cross-view leak. Measured in a settled inn: ~78 no-scene, 0 other-scene.
                if (SceneDb.Objects.Valid(de.Obj) && SceneDb.ObjScene[de.Obj.Index].IsNull) _dbNoScene++;
                else _dbOtherScene++;
                // The game's unattached prototype stock is dozens of models' worth of gore emitters and
                // would be the whole census on its own; a handful names the class, the counter has the rest.
                if (_cenNoScene < 8 && Census(de.Part, SceneDb.Objects.Valid(de.Obj)
                        && SceneDb.ObjScene[de.Obj.Index].IsNull ? "no-scene" : "other-scene")) _cenNoScene++;
                DumpSkipped(de.Part, de.Obj, scene);
                continue;
            }
            // CX-188: the CULL first, the full snapshot after. An out-of-view emitter is not stepped -- it
            // `continue`s below, and did before this change -- so filling it was pure waste on the whole
            // off-screen population: thirty-odd channel reads, the p2p target walk and an engine scale call
            // per emitter per frame. The cull wants the world position and the view radius, which is ten
            // channel reads, and CullDb derives both exactly as FillDb does.
            if (!CullDb(de.Part, out Vector3 pos, out float radius, out int mode))
            { _dbNoDef++; Census(de.Part, "no-def"); continue; }
            if (radius < _dbRMin) _dbRMin = radius;
            if (radius > _dbRMax) _dbRMax = radius;
            bool inView = !cull || InView(pos, radius);
            // A lightning emitter is the p2p edge's one visible consumer, so it is dumped on its own
            // budget rather than losing its line to the first dozen torches.
            bool dump = _dbDumped < DbDumpMax || (mode == ModeLightning && _dbBoltDumped++ < DbDumpMax);
            if (!inView)
            {
                _outOfView++;
                // Both diagnostics want the parameters, and both are one-shots under a budget -- so a culled
                // emitter is filled on the frames it has a line to say, and on no others.
                bool told = dump && FillDb(de.Part, ref e);
                if (told) DumpDb(in e, false, eye);
                if (Census(de.Part, "culled") && (told || FillDb(de.Part, ref e))) CensusParams(in e);
                continue;
            }
            // Unreachable as the two stand: CullDb accepted this emitter on the same two conditions FillDb
            // refuses on. Kept, and counted the same way, because the alternative is trusting that they stay
            // in step -- and a snapshot silently skipped would draw last frame's particles.
            if (!FillDb(de.Part, ref e)) { _dbNoDef++; Census(de.Part, "no-def"); continue; }
            if (dump) DumpDb(in e, true, eye);
            _inView++;
            int s = FindDb(de.Part, de.Obj.Gen);
            if (s < 0) { _full++; continue; }
            ref Slot slot = ref _tab[s];
            if (!slot.Opened && !Open(ref slot, in e)) { Unmap(slot.Part, s); slot.Part = -1; _full++; continue; }
            slot.Seen = _frame;
            e.M = slot.Mut;

            if (slot.Chunk)
            {
                // A chunk emitter's particles are OBJECTS (a shattering crate is gobs), but they are still
                // PARTICLES: the same Update births, moves and retires them (ChunkyParticle is a Particle), and
                // the object only rides along. So the simulation is the ordinary one and ChunkSync binds one
                // object to each live record -- spawned once, placed every frame, pooled out of the scene on
                // death -- instead of minting an object per birth that nothing ever moved or removed.
                if (stepping && animating) Advance(ref slot, ref e, IntPtr.Zero, dt);
                else if (stepping) _held++;
                slot.Mut = e.M;
                ChunkSync(ref slot, in e);
                _chunk++;
                continue;
            }
            int fade0 = _fadeExit, rate0 = _rate0;
            if (stepping && animating) Advance(ref slot, ref e, IntPtr.Zero, dt);
            else if (stepping) _held++;
            slot.Mut = e.M;
            if (slot.Live <= 0)
            {
                CensusE(in e, _fadeExit != fade0 ? "faded" : _rate0 != rate0 ? "rate0" : "empty");
                if (stepping && animating && _fadeExit == fade0 && _rate0 == rate0) _idle++;
                continue;
            }
            CensusE(in e, "live");

            slot.Dist = (eye - e.OwnerPos).LengthSquared();
            if (_orderN < MaxEmitters) _order[_orderN++] = s;
            _emitters++;
            _particles += slot.Live;
        }
        SortDrawOrder();
        for (int i = 0; i < _orderN; i++)
        {
            ref Slot slot = ref _tab[_order[i]];
            Particles.Enroll(IntPtr.Zero, slot.Part, TexNameOf(ref slot), IntPtr.Zero,
                             _ptrs + slot.Base, slot.Live);
        }
        if (stepping) Reclaim();
        FrameZones.Add(FrameZones.Zone.Emit, tEmit);
    }

    /// <summary>The feed frame the DB walk last stepped on, so two world views of one frame do not advance the
    /// simulation twice.</summary>
    private static int _dbFrame = -1;

    /// <summary>The DB walk's own verdict counts, for the period line. "No particles in DB mode" has five
    /// different causes and only the split says which: a dead table entry, a disabled part or invisible
    /// object, an emitter belonging to another scene's view, a part whose model has no emitter declaration,
    /// and the cull.</summary>
    private static int _dbDead, _dbDisabled, _dbOtherScene, _dbNoScene, _dbNoDef;
    private static bool _dbAnim;
    private static uint _dbScene;
    private static float _dbRMin, _dbRMax;
    private static bool _saidHeld;

    /// <summary>Steps taken with NEITHER scene animate column answering. Counted every frame and named once:
    /// this is a real departure from the engine's rule, and the moment seam-anim writes
    /// <see cref="SceneDb.SceneAnimating"/> it stops happening by itself.</summary>
    private static int _ungated;

    /// <summary>The DB twin of <c>/emitsim dump</c>, taken once at boot: every emitter this scene holds, with
    /// the parameters that decide whether it draws anything at all. "No flames" has a different fix for each
    /// of mode, birth rate, lifespan and texture, and no counter can tell them apart.</summary>
    private const int DbDumpMax = 12;
    private static int _dbDumped;

    /// <summary>Distinct (model node, verdict) pairs the walk names once each, so an emitter that appears
    /// LATER -- a torch equipped into a hand, a VFX played on a creature -- still leaves a line after the boot
    /// dump's budget is spent. Keyed by the parsed model's identity and the verdict's literal, so a repeat
    /// costs two reference compares and no allocation.</summary>
    private const int CensusMax = 256;
    private static readonly object?[] _cenModel = new object?[CensusMax];
    private static readonly int[] _cenNode = new int[CensusMax];
    private static readonly string?[] _cenVerdict = new string?[CensusMax];
    private static int _cenN, _cenNoScene;

    /// <summary>Say once what the walk decided about this part. <paramref name="verdict"/> must be a literal,
    /// so the repeat test is a reference compare.</summary>
    private static bool Census(int part, string verdict)
    {
        if (_cenN >= CensusMax || !SceneDb.Parts.LiveAt(part)) return false;
        ModelDef? m = SceneDb.PartModel[part];
        int n = SceneDb.PartNode[part];
        for (int k = 0; k < _cenN; k++)
            if (ReferenceEquals(_cenModel[k], m) && _cenNode[k] == n && ReferenceEquals(_cenVerdict[k], verdict))
                return false;
        _cenModel[_cenN] = m; _cenNode[_cenN] = n; _cenVerdict[_cenN] = verdict; _cenN++;
        string node = m != null && (uint)n < (uint)m.Nodes.Length ? m.Nodes[n].Name : "?";
        _log?.Info($"EmitterSim: [db census] {m?.Name ?? "?"}/{node} part={part} obj={SceneDb.PartObj[part].Value} -> {verdict}");
        return true;
    }

    /// <summary>As <see cref="Census"/>, with the parameters that decide whether a walked emitter spawns
    /// anything -- the question a bare verdict leaves open.</summary>
    private static void CensusE(in Emitter e, string verdict)
    {
        if (!Census(e.Part, verdict)) return;
        CensusParams(in e);
    }

    /// <summary>The parameter half of a census line, for a verdict <see cref="Census"/> has already accepted --
    /// the split exists because the CULLED verdict now fills its snapshot only once the verdict is known to be
    /// worth a line.</summary>
    private static void CensusParams(in Emitter e)
    {
        _log?.Info($"EmitterSim: [db census] ...rate={e.BirthRate:F2} life={e.Life:F2} mode={e.Mode} "
                   + $"type={e.Type} size={e.SizeStartX:F2} vel={e.Velocity:F2} faded={e.Faded} "
                   + $"loop={e.Loop} chunk={e.Chunk}");
    }

    /// <summary>The first few DISTINCT objects the per-view scene filter drops, with the attachment chain
    /// each one hangs from and every hop's scene. The count alone cannot say whether a dropped emitter is the
    /// game's unattached prototype stock or a VFX whose scene never propagated down an attach chain; the
    /// chain says which.</summary>
    private static void DumpSkipped(int part, DbId obj, DbId scene)
    {
        if (_dbSkipDumped >= DbDumpMax) return;
        for (int k = 0; k < _dbSkipSeenN; k++) if (_dbSkipSeen[k] == obj.Value) return;
        if (_dbSkipSeenN < _dbSkipSeen.Length) _dbSkipSeen[_dbSkipSeenN++] = obj.Value;
        _dbSkipDumped++;
        ModelDef? m = SceneDb.PartModel[part];
        string chain = "";
        DbId at = obj;
        for (int hop = 0; hop < 6 && SceneDb.Objects.Valid(at); hop++)
        {
            chain += $" <- {SceneDb.ObjSeamModelName[at.Index] ?? "?"}#{at.Value}"
                   + $"[scene={SceneDb.ObjScene[at.Index].Value}]";
            DbId up = SceneDb.ObjAttachTo[at.Index];
            if (up.IsNull || up.Value == at.Value) break;
            at = up;
        }
        _log?.Info($"EmitterSim: [db skip] {m?.Name ?? "?"} part={part} walkscene={scene.Value}{chain}");
    }

    private static readonly uint[] _dbSkipSeen = new uint[DbDumpMax];
    private static int _dbSkipSeenN;
    private static int _dbSkipDumped, _dbBoltDumped;

    private static void DumpDb(in Emitter e, bool inView, Vector3 eye)
    {
        _dbDumped++;
        ModelDef? m = SceneDb.PartModel[e.Part];
        int n = SceneDb.PartNode[e.Part];
        string node = m != null && (uint)n < (uint)m.Nodes.Length ? m.Nodes[n].Name : "?";
        string tex = m != null && (uint)n < (uint)m.Nodes.Length ? m.Nodes[n].Emitter?.Texture ?? "" : "";
        _log?.Info($"EmitterSim: [db] {m?.Name ?? "?"}/{node} part={e.Part} obj={e.Obj.Value} "
                   + $"mode={e.Mode} type={e.Type} blend={e.Blend} rate={e.BirthRate:F2} life={e.Life:F2} "
                   + $"vel={e.Velocity:F2} size={e.SizeStartX:F2} tex=\"{tex}\" r={e.ViewRadius:F2} "
                   + $"at=({e.WorldPos.X:F1},{e.WorldPos.Y:F1},{e.WorldPos.Z:F1}) "
                   + $"d={(eye - e.WorldPos).Length():F1} inview={inView} "
                   + $"target={(e.HasTarget ? $"({e.TargetPos.X:F1},{e.TargetPos.Y:F1},{e.TargetPos.Z:F1})" : "NONE")}");
    }

    private static void UngatedOnce(DbId scene)
    {
        if (_saidHeld) return;
        _saidHeld = true;
        _log?.Info($"EmitterSim: scene {scene.Value} says it is NOT animating -- SceneDb.SceneAnimating["
                   + $"{scene.Index}] is false (nothing in the client writes it yet) and SceneAnimOn is "
                   + $"{SceneDb.SceneAnimOn[scene.Index]} (valid={SceneDb.Scenes.Valid(scene)}). The engine's "
                   + "rule would HOLD every particle in this scene, which would freeze the world's flames for "
                   + "the whole session, so the simulation steps anyway and counts it (UNGATED= on the "
                   + "[emitsim] line). It stops counting the moment the scene's Animate step writes the "
                   + "column -- DbScene.SetAnimating exists and has no caller.");
    }

    /// <summary>DB mode: the chunk object bound to each pool record (by record index), and whether it is in the
    /// scene. The engine's <c>ChunkyParticle+0x78</c>: the record keeps its gob across death and rebirth.</summary>
    private static DbId[] _chunkObj = Array.Empty<DbId>();
    private static bool[] _chunkIn = Array.Empty<bool>();

    private static int RecIndex(IntPtr rec) => (int)(((byte*)rec - _pool) / EngineLayout.ParticleStride);

    /// <summary>Bind, place and show an object for every live particle of a DB chunk emitter, and take the dead
    /// records' objects out of the scene. The live slice is <c>[0, Live)</c>; the rest of the block is the free
    /// list (<see cref="Kill"/>), so a record past <c>Live</c> is a dead particle.</summary>
    private static void ChunkSync(ref Slot slot, in Emitter e)
    {
        if (e.ChunkName == null || e.ChunkName.Length == 0 || slot.Cap <= 0) return;
        EnsureChunkTable();
        bool spin = e.Spin != 0f;
        for (int i = 0; i < slot.Cap; i++)
        {
            IntPtr rec = _ptrs[slot.Base + i];
            int r = RecIndex(rec);
            if ((uint)r >= (uint)_chunkObj.Length) continue;
            DbId id = _chunkObj[r];
            bool valid = SceneDb.Objects.Valid(id);
            if (i >= slot.Live)
            {
                if (valid && _chunkIn[r]) { DbInputs.ChunkScene?.Invoke(id, default); _chunkIn[r] = false; }
                continue;
            }
            Particle p = new(rec);
            Quaternion rot = Quaternion.Identity;
            if (spin && p.Splat == 0)
            {
                // RenderNothing's spin: axis Particle+0x4c, half-angle roll/2 -- CreateFromAxisAngle's own form.
                Vector3 axis = p.SplatNormal;
                if (axis.LengthSquared() > 1e-12f) rot = Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), p.Roll);
            }
            if (!valid)
            {
                id = DbInputs.SpawnChunk(e.Scene, e.ChunkName, Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(p.Pos));
                if (id.IsNull) { _chunkRefused++; continue; }
                _chunkObj[r] = id; _chunkIn[r] = true; _chunkSpawned++;
                continue;
            }
            if (!_chunkIn[r]) { DbInputs.ChunkScene?.Invoke(id, e.Scene); _chunkIn[r] = true; }
            DbInputs.ChunkPlace?.Invoke(id, p.Pos, rot);
        }
    }

    private static void EnsureChunkTable()
    {
        if (_chunkObj.Length >= _slotCap) return;
        Array.Resize(ref _chunkObj, _slotCap);
        Array.Resize(ref _chunkIn, _slotCap);
    }

    /// <summary>Free the chunk objects bound to the records behind pointer-table entries
    /// <c>[from, from + n)</c> -- the slot's block is being dropped, so its pooled gobs go with it.</summary>
    private static void FreeChunks(int from, int n)
    {
        if (_chunkObj.Length == 0 || _ptrs == null) return;
        for (int k = from; k < from + n && k < _slotCap; k++)
        {
            int r = RecIndex(_ptrs[k]);
            if ((uint)r >= (uint)_chunkObj.Length || _chunkObj[r].IsNull) continue;
            if (SceneDb.Objects.Valid(_chunkObj[r])) DbInputs.ChunkDestroy?.Invoke(_chunkObj[r]);
            _chunkObj[r] = default; _chunkIn[r] = false;
        }
    }

    /// <summary>Every bound chunk object freed: the whole arena is being dropped (<see cref="Empty"/>).</summary>
    private static void FreeAllChunks()
    {
        for (int r = 0; r < _chunkObj.Length; r++)
        {
            if (_chunkObj[r].IsNull) continue;
            if (SceneDb.Objects.Valid(_chunkObj[r])) DbInputs.ChunkDestroy?.Invoke(_chunkObj[r]);
            _chunkObj[r] = default; _chunkIn[r] = false;
        }
    }

    /// <summary><see cref="Grow"/> copied <paramref name="had"/> live records to the block at
    /// <paramref name="to"/>: their objects move with them, and the old block's dead records free theirs.</summary>
    private static void MoveChunks(int from, int had, int oldCap, int to)
    {
        EnsureChunkTable();
        for (int i = 0; i < had; i++)
        {
            int a = RecIndex(_ptrs[from + i]), b = RecIndex(_ptrs[to + i]);
            if ((uint)a >= (uint)_chunkObj.Length || (uint)b >= (uint)_chunkObj.Length) continue;
            _chunkObj[b] = _chunkObj[a]; _chunkIn[b] = _chunkIn[a];
            _chunkObj[a] = default; _chunkIn[a] = false;
        }
        if (oldCap > had) FreeChunks(from + had, oldCap - had);
    }

    private static int _chunkSpawned, _chunkRefused;

    /// <summary>The in-view set this frame, for the validator alone -- the walk above cannot hand the
    /// validator a span of what it kept without recording it, and recording it is one store per emitter.
    /// </summary>
    private static readonly IntPtr[] _selected = new IntPtr[MaxEmitters];
    private static int _selN;
    private static int _inView, _outOfView;
    /// <summary>The silent exits, period totals, disjoint: the tile-fade exit, a fountain whose birth rate truncates
    /// to 0, and a stepped emitter left empty for neither reason; beside them the emitters held (in view, the scene
    /// not animating -- drawn, not stepped) and the chunk emitters whose engine-side list holds anything after their
    /// step. A flame that never shows is one of these.</summary>
    private static int _held, _idle, _fadeExit, _rate0, _chunkLive;

    /// <summary>Six inward-facing camera planes, rebuilt once per <see cref="StepDb"/>. False = no camera to
    /// build them from, and every registered emitter is stepped -- an instrument that cannot see may not hide
    /// the world.</summary>
    private static readonly float[] _viewPlanes = new float[24];
    /// <summary>The viewer the emission cone clamps against (<c>Particle::randomDirection</c> @0x4ab160): the
    /// frame's own camera, as the walk resolved it.</summary>
    private static Vector3 _eye;
    private static bool _hasEye;
    private static bool BuildViewPlanes()
    {
        if (!FrameInputs.HasCamera || !FrameInputs.HasPerspective) return false;
        fixed (float* planes = _viewPlanes)
            ShadowMath.ReceiverPlanes(FrameInputs.CameraPos, FrameInputs.SceneCamFwd, FrameInputs.CamFovy,
                                      FrameInputs.CamAspect, FrameInputs.CamNear, FrameInputs.CamFar, 0f,
                                      new Span<float>(planes, 24));
        return true;
    }

    /// <summary>The engine's own emitter cull, <c>PartOutside(PartEmitter*, List&lt;Plane&gt;&amp;)</c>
    /// @0x47a290: the emitter's WORLD position (taken fresh by the snapshot rather than off last frame's cached
    /// <c>PartEmitter+0x1e8</c>) against its own view radius, one sphere-vs-plane test per plane. Finer than the
    /// engine's, which also required the emitter's whole BSP node to be in the frustum.</summary>
    private static bool InView(Vector3 pos, float r)
    {
        fixed (float* planes = _viewPlanes)
            for (int i = 0; i < 6; i++)
            {
                float d = planes[i * 4] * pos.X + planes[i * 4 + 1] * pos.Y + planes[i * 4 + 2] * pos.Z
                        + planes[i * 4 + 3];
                if (d < -r) return false;
            }
        return true;
    }

    /// <summary>The engine's own draw order, exactly: ASCENDING squared camera distance -- nearest first --
    /// and, when two emitters tie and share a Gob, ascending <see cref="EngineLayout.EmitterRenderOrder"/>.
    /// Vanilla composites near-to-far and <see cref="Particles"/> keeps this order for its alpha-blended
    /// class, so sorting the other way changes how overlapping plumes read. Insertion sort: allocation-free.
    /// </summary>
    private static void SortDrawOrder()
    {
        for (int i = 1; i < _orderN; i++)
        {
            int v = _order[i];
            int j = i - 1;
            while (j >= 0 && Before(v, _order[j])) { _order[j + 1] = _order[j]; j--; }
            _order[j + 1] = v;
        }
    }

    /// <summary>Does <paramref name="a"/> draw before <paramref name="b"/>? The engine's comparator, term for
    /// term. The Gob test is what keeps the tiebreak local: two unrelated emitters at one distance are left
    /// in bucket order, exactly as vanilla leaves them.</summary>
    private static bool Before(int a, int b)
    {
        float da = _tab[a].Dist, db = _tab[b].Dist;
        if (da != db) return da < db;
        IntPtr g = _tab[a].Gob;
        if (g == IntPtr.Zero || g != _tab[b].Gob) return false;
        return _tab[a].Order < _tab[b].Order;
    }


    /// <summary>The same search over DATABASE keys. <paramref name="gen"/> is the owning object's handle
    /// generation, so a part index the database has re-tenanted takes a fresh slot instead of inheriting the
    /// dead emitter's particles.</summary>
    private static int FindDb(int part, uint gen)
    {
        int free = -1, stale = -1, oldest = int.MaxValue;
        // CX-188: the part's own slot, in one load. The scan below is reached only when this part has none --
        // which is the frame it appears, not every frame it burns.
        int at = (uint)part < (uint)_slotOfPart.Length ? _slotOfPart[part] - 1 : -1;
        if (at >= 0 && at < _tabN && _tab[at].Part == part)
        {
            if (_tab[at].Gen == gen) return at;
            Close(at);
            free = at;
        }
        else
        {
            for (int i = 0; i < _tabN; i++)
            {
                // The part match stays IN the scan, and that is what makes a drifted map cost one scan and
                // nothing else. Without it the part would be handed a SECOND slot while the first still holds
                // its particles and its texture reference -- double the particles, a leaked registry ref, and
                // no counter on the path to say so. With it, the scan finds the slot and re-maps it.
                if (_tab[i].Part == part)
                {
                    if (_tab[i].Gen == gen)
                    {
                        if ((uint)part < (uint)_slotOfPart.Length) _slotOfPart[part] = i + 1;
                        return i;
                    }
                    Close(i);
                    free = i;
                    break;
                }
                if (_tab[i].Part < 0) { if (free < 0) free = i; }
                else if (_tab[i].Seen < oldest) { oldest = _tab[i].Seen; stale = i; }
            }
        }
        int s = Take(free, stale, oldest);
        if (s < 0) return -1;
        Reset(s);
        _tab[s].Part = part;
        _tab[s].Gen = gen;
        _tab[s].Rng = Mix((uint)part * 0x9e3779b9u + gen + 1u);
        if ((uint)part < (uint)_slotOfPart.Length) _slotOfPart[part] = s + 1;
        return s;
    }

    /// <summary>Slot <paramref name="s"/> is no longer <paramref name="part"/>'s: drop the map entry, but only
    /// if it is still the one pointing here. Two slots never hold one part, so a map entry that names another
    /// slot belongs to a later tenant and clearing it would orphan a live emitter.</summary>
    private static void Unmap(int part, int s)
    {
        if ((uint)part < (uint)_slotOfPart.Length && _slotOfPart[part] == s + 1) _slotOfPart[part] = 0;
    }

    /// <summary>Pick the slot a fresh emitter takes: a tombstone, then growth, then the one longest unseen.
    /// </summary>
    private static int Take(int free, int stale, int oldest)
    {
        if (free >= 0) return free;
        if (_tabN < MaxEmitters) return _tabN++;
        if (stale >= 0 && oldest < _frame - 1) { _reclaimed++; Close(stale); return stale; }
        return -1;
    }

    /// <summary>Blank a slot for a new tenant, keeping nothing of the old one's.</summary>
    private static void Reset(int s)
    {
        if (_tab[s].Chunk && _tab[s].Cap > 0) FreeChunks(_tab[s].Base, _tab[s].Cap);
        Close(s);
        if (_tab[s].Part >= 0) Unmap(_tab[s].Part, s);
        _tab[s] = default;
        _tab[s].Part = -1;
        _tab[s].Seen = _frame;
    }

    /// <summary>Release what a slot OWNS outside the pool -- today the DB-mode emitter texture. The particles
    /// themselves live in the arena, which is a bump allocator and reclaims only wholesale.</summary>
    private static void Close(int s)
    {
        if (_tab[s].Tex != 0) { TexRegistry.Release(new TexId(_tab[s].Tex)); _tab[s].Tex = 0; }
    }

    /// <summary>Resolve everything about the emitter that comes from its model and never changes, and take
    /// its pool block. False leaves the slot unusable and the emitter undrawn this frame.</summary>
    private static bool Open(ref Slot slot, in Emitter e)
    {
        slot.Opened = true;
        slot.Gob = (IntPtr)(nint)e.Obj.Value;
        slot.Order = e.RenderOrder;
        slot.Mode = e.Mode;
        slot.Mover = (e.Flags & FP2P) == 0 ? MoveStandard
                   : (e.Flags & FP2PSel) != 0 ? MoveBezier : MoveGrav;
        slot.Chunk = e.Chunk;
        // A chunk emitter with no part behind it has nothing to simulate from; the pool runs the rest.
        if (slot.Chunk && slot.Part < 0) return true;
        // dt 0: the steady-state size. Advance grows again with the frame's own dt before the first birth,
        // so the burst headroom is in place before anything can ask for it.
        return Grow(ref slot, in e, 0f);
    }

    private static bool Is(Span<byte> s, string lit)
    {
        for (int i = 0; i < lit.Length; i++)
            if (i >= s.Length || s[i] != (byte)lit[i]) return false;
        return s.Length <= lit.Length || s[lit.Length] == 0;
    }

    /// <summary>xorshift32. The engine uses <c>rand()</c>; this does not, on purpose -- a per-emitter stream
    /// keeps the SelfChecks deterministic and keeps the simulation off a global the engine also draws from.
    /// </summary>
    private static uint Mix(uint x)
    {
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;
        return x == 0 ? 0x1234567u : x;
    }

    /// <summary><c>rand() % n</c>, the engine's own quantiser, with the trap it does not have: <c>n</c> at or
    /// below zero yields zero rather than dividing by it.</summary>
    private static int Rnd(ref uint st, int n)
    {
        st = Mix(st);
        return n <= 0 ? 0 : (int)(st % (uint)n);
    }

    private static bool Coin(ref uint st) { st = Mix(st); return (st & 1) != 0; }

    // ---- the pool ------------------------------------------------------------------------------------

    /// <summary>Round a wanted population up to a power of two. The arena is a bump allocator and a grow
    /// ABANDONS the old block, so a controller ramping birthrate one particle at a time would burn ~N^2/2 of
    /// the arena on its way up. Powers of two make that O(log N) grows and under 2x the block in waste.</summary>
    private static int Block(int want)
        => want < 1 ? 1 : (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)want);

    /// <summary>The block one emitter's parameters can fill THIS step: its standing population plus the
    /// largest burst its mode can add in one step. Single and lightning are exact; explosion ESTIMATES a
    /// previous detonation's survivors as <c>rate x life</c> and overruns when <c>life</c> exceeds the event
    /// period; the p2p birth and a birthrate RAMP are counted by <see cref="Room"/> rather than sized for.</summary>
    private static int Want(int mode, float rate, float life, float dt)
    {
        float l = MathF.Max(life, 0f);
        int want = mode switch
        {
            ModeSingle => 1,
            ModeLightning => (int)rate,
            ModeExplosion => (int)rate + (int)(rate * l) + 1,
            _ => (int)(rate * l) + 1 + 1 + (int)(dt * rate),
        };
        return want < 1 ? 1 : want;
    }

    /// <summary>Give this emitter a block big enough for the population its parameters imply, growing the
    /// arena if it must. False means the arena is at its cap and this emitter draws nothing this frame.
    /// </summary>
    private static bool Grow(ref Slot slot, in Emitter e, float dt)
    {
        float rate = e.BirthRate, life = e.Life;
        int want = Want(slot.Mode, rate, life, dt);
        // The peak is the census MaxPerEmitter has to be sized from: nothing else in the client says what an
        // emitter's own parameters ask for, so a clamp reported without it cannot be acted on. Taken before
        // the block is rounded up, so it reports the demand and not our own quantisation.
        if (want > _capPeak) _capPeak = want;
        want = Block(want);
        if (want > MaxPerEmitter) { want = MaxPerEmitter; _capped++; }
        if (slot.Cap >= want) return true;

        int had = slot.Live, from = slot.Base;
        if (!Reserve(want)) return false;
        // Reserve may have dropped every block to grow the arena, in which case there is nothing to carry.
        if (slot.Cap == 0) had = 0;
        int at = _slotTop;
        _slotTop += want;
        // The old block is abandoned rather than freed: the arena is a bump allocator and its only reclaim
        // is the whole-arena reset below, which is exactly the trade Particles' record blocks already make.
        IntPtr* pt = _ptrs + at;
        for (int i = 0; i < want; i++) pt[i] = (IntPtr)(_pool + (long)(at + i) * EngineLayout.ParticleStride);
        // Carry the live particles across. A controller animates birthrate and lifespan, so an emitter's
        // ceiling moves while it burns -- and a grow that dropped its population would make a ramping
        // fountain visibly restart, which is the wrong kind of cheap.
        for (int i = 0; i < had; i++)
            NativeMemory.Copy((void*)_ptrs[from + i], (void*)pt[i], (nuint)EngineLayout.ParticleStride);
        if (slot.Chunk) MoveChunks(from, had, slot.Cap, at);
        slot.Base = at; slot.Cap = want; slot.Live = had;
        return true;
    }

    /// <summary>Room for <paramref name="want"/> more particles, growing or starting the arena over. A reset
    /// empties every emitter, which costs one frame of missing particles and is counted -- it is not a silent
    /// fallback, because the arena being full is our own limit and the period line names it.</summary>
    private static bool Reserve(int want)
    {
        if (_slotTop + want <= _slotCap) return true;
        int cap = _slotCap > 0 ? _slotCap : MinSlots;
        while (cap < _slotTop + want && cap < MaxSlots) cap <<= 1;
        if (cap > _slotCap && cap <= MaxSlots)
        {
            byte* pool = (byte*)NativeMemory.Alloc((nuint)((long)cap * EngineLayout.ParticleStride));
            IntPtr* ptrs = (IntPtr*)NativeMemory.Alloc((nuint)((long)cap * sizeof(IntPtr)));
            NativeMemory.Clear(pool, (nuint)((long)cap * EngineLayout.ParticleStride));
            // Every live block's records MOVE, so every pointer table entry has to be rewritten -- which is
            // what dropping the whole arena does, at the cost of one frame. Copying instead would mean
            // rebasing each emitter's pointers, for a path that runs a handful of times per session.
            if (_pool != null) { NativeMemory.Free(_pool); NativeMemory.Free(_ptrs); Empty(); }
            _pool = pool; _ptrs = ptrs; _slotCap = cap; _slotTop = 0;
            return want <= cap;
        }
        if (_slotCap == 0) return false;
        Empty();
        _slotTop = 0;
        _poolResets++;
        if (!_poolWarned)
        {
            _poolWarned = true;
            _log?.Always($"EmitterSim: the particle pool hit its {MaxSlots}-particle ceiling and started over "
                       + "-- one frame of missing particles, and it will repeat while the area stays this busy "
                       + "(pool-reset= on the [emitsim] line counts it)");
        }
        return want <= _slotCap;
    }

    /// <summary>Drop every emitter's block. The slots survive so their mode/mover verdicts do; only the
    /// particles go.</summary>
    private static void Empty()
    {
        FreeAllChunks();
        for (int i = 0; i < _tabN; i++) { _tab[i].Base = 0; _tab[i].Cap = 0; _tab[i].Live = 0; _tab[i].Opened = false; }
        _orderN = 0;
        // Particles holds a RAW Particle* array per captured emitter and draws a one-frame-stale slot, so an
        // arena that moved under it would be read after free. Dropping its table costs one frame of
        // particles; not dropping it costs a crash on whichever emitter left the bucket last frame.
        Particles.Forget();
    }

    /// <summary>Tombstone slots nothing has walked for a while, so an area the camera left stops holding pool
    /// blocks. Cheap and amortised: one slot per frame at most.</summary>
    private static void Reclaim()
    {
        if (_tabN == 0) return;
        int i = _frame % _tabN;
        if (_tab[i].Part >= 0 && _tab[i].Seen < _frame - StaleFrames) Reset(i);
    }

    /// <summary>Frames an emitter may go unseen before its slot is dropped. Long enough that turning the
    /// camera away and back does not restart an effect; short enough that an area transition's emitters do
    /// not outlive their own <c>Part</c>s by anything a person would notice.</summary>
    private const int StaleFrames = 120;

    /// <summary>A DATABASE part died (<c>DbInputs.PartDestroyed</c>, which the feed's own fold drives off
    /// <see cref="Dirty.Destroyed"/>). The slot holds this emitter's whole memory and its texture reference,
    /// so both go here rather than waiting for the stale sweep.</summary>
    internal static void ForgetDbPart(int part)
    {
        int at = (uint)part < (uint)_slotOfPart.Length ? _slotOfPart[part] - 1 : -1;
        if (at >= 0 && at < _tabN && _tab[at].Part == part) { Reset(at); return; }
        for (int i = 0; i < _tabN; i++)
            if (_tab[i].Part == part) { Reset(i); return; }
    }

    /// <summary>The DB-mode emitter texture for this slot, acquiring it on first use. <c>TexKind.Auto</c>, like
    /// every other registry acquisition: a particle atlas ships as DDS or TGA and the registry decides which.
    /// </summary>
    private static uint TexOf(ref Slot slot, int part)
    {
        if (slot.Tex != 0) return slot.Tex;
        ModelDef? m = SceneDb.PartModel[part];
        int n = SceneDb.PartNode[part];
        string? name = m != null && (uint)n < (uint)m.Nodes.Length ? m.Nodes[n].Emitter?.Texture : null;
        if (string.IsNullOrEmpty(name)) return 0;
        TexId id = TexRegistry.Acquire(name);
        slot.Tex = id.Value;
        return slot.Tex;
    }

    /// <summary>The GL name this slot's emitter draws with, or 0 -- the pass declines an emitter whose texture
    /// has not arrived rather than drawing a white quad over the scene. The registry marks its dependents
    /// dirty at arrival, so "not yet" resolves itself on a later frame.</summary>
    private static uint TexNameOf(ref Slot slot)
    {
        uint v = TexOf(ref slot, slot.Part);
        if (v == 0) return 0;
        TexRegistry.TryDrawable(new TexId(v), out uint name, out _);
        return name;
    }

    /// <summary>Area transition or context loss: every emitter pointer this table holds belongs to Parts the
    /// unload frees.</summary>
    internal static void Reset()
    {
        _resets++;
        for (int i = 0; i < _tabN; i++)
        {
            Close(i);
            if (_tab[i].Part >= 0) Unmap(_tab[i].Part, i);
            _tab[i] = default; _tab[i].Part = -1;
        }
        _tabN = 0; _orderN = 0; _slotTop = 0; _dbFrame = -1;
        // Same reason Empty() does it: Particles holds a raw Particle* array per captured emitter and draws a
        // one-frame-stale slot, so rewinding the arena under it has to drop its table. The registry happens
        // to revoke Particles too, but relying on that order would make this correct by luck.
        Particles.Forget();
    }

    /// <summary>Stats for the period line.</summary>
    internal static string Stats(int frames)
    {
        if (!Armed) return string.Empty;
        int f = frames > 0 ? frames : 1;
        // Per frame, as floats: integer division read "em=0 parts=0" for an emitter that was live on a
        // third of the frames, which is indistinguishable from a simulation that is not running.
        string s = $"[emitsim] em={(float)_emitters / f:F1} parts={(float)_particles / f:F1} "
                 + $"born={_spawned} died={_died} dt={_dtSum:F2}s over {_steps} step(s)"
                 + (_steps > 0 && _dtSum <= 0f ? " (PAUSED -- vanilla freezes its particles here too)" : "");
        if (_chunk != 0) s += $" chunk={_chunk} (with engine particles {_chunkLive})";
        s += $" silent exits/frame: fade={(float)_fadeExit / f:F1} rate0={(float)_rate0 / f:F1} other-empty={(float)_idle / f:F1}"
           + $" (held, drawn but not stepped: {(float)_held / f:F1})";
        if (_p2pNoTarget != 0) s += $" p2p-untargeted={_p2pNoTarget}";
        // CX-188: IN VIEW, and the clause says so. The target walk is part of the full snapshot, which the
        // walk now takes only for an emitter it keeps -- so this counts the targeted emitters that were
        // stepped, not every targeted emitter in the area. A counter whose population changed under it and
        // kept its old name is a counter that lies.
        if (DbTargets != 0) s += $" p2p-targeted={DbTargets} (in view; the target walk is part of the fill)";
        // Per frame, like em= and parts= beside it: a period total here read as an alarm (523) for what is
        // half an emitter a frame. The peak is what MaxPerEmitter would have to be raised to; CAP-LOST is
        // births actually dropped, which is the part that is a bug -- vanilla has no ceiling to drop them at.
        if (_capped != 0) s += $" capped={(float)_capped / f:F2}/frame (wanted up to {_capPeak}, cap {MaxPerEmitter})";
        if (_capLost != 0) s += $" CAP-LOST={_capLost} births VANILLA WOULD HAVE DRAWN";
        if (_poolResets != 0) s += $" pool-reset={_poolResets}";
        if (_reclaimed != 0) s += $" reclaimed={_reclaimed}";
        if (_full != 0) s += $" table-full={_full}";
        if (_foreign != 0) s += $" foreign-scene={_foreign}";
        // CX-104: where the population came from, and how much of it the owned cull kept. A zero culled count
        // in a big area means the cull is not running (no camera), not that everything is on screen.
        s += $" src=db({DbInputs.EmitterCount}) scene={_dbScene} anim={(_dbAnim ? 1 : 0)}"
             + $" inview={_inView} culled={_outOfView}"
             + $" (skipped: dead={_dbDead} disabled={_dbDisabled} no-scene={_dbNoScene} other-scene={_dbOtherScene} no-def={_dbNoDef};"
             + $" radius {(_dbRMin == float.MaxValue ? 0f : _dbRMin):F2}..{_dbRMax:F2})";
        // CX-120: what DB mode cannot do yet, counted rather than assumed. Each one is a visible difference.
        if (_chunkSpawned != 0 || _chunkRefused != 0)
            s += $" chunk-spawned={_chunkSpawned}"
               + (_chunkRefused != 0 ? $" CHUNK-SPAWN-REFUSED={_chunkRefused}" : "");
        if (_ungated != 0) s += $" UNGATED={(float)_ungated / f:F1}/frame (no scene animate column is written)";
        // Every revoke drops every emitter's particles AND Particles' capture table. One per area edge is
        // expected; one per frame means nothing ever accumulates and the period line would otherwise read
        // like an emitter that births and never lives.
        if (_resets != 0) s += $" TABLE-RESETS={_resets}";
        if (_windRefused != 0) s += $" NO-WIND={_windRefused} (Alloy's wind field is not armed)";
        if (_gustRefused != 0) s += $" NO-GUST={_gustRefused} (a detonation moved no air)";
        return s;
    }

    /// <summary>The period's own close. The counters this line reports belong to the PERIOD, so they are
    /// cleared where every other pass clears its own (<c>AlloyRenderer.EndFrame</c>, right after the report)
    /// rather than as a side effect of formatting the line -- which left them at the mercy of how many times,
    /// and whether, the line was built.</summary>
    internal static void ResetPeriod()
    {
        _emitters = 0; _particles = 0; _spawned = 0; _died = 0; _chunk = 0;
        DbTargets = 0;
        _p2pNoTarget = 0; _capped = 0; _poolResets = 0; _reclaimed = 0; _full = 0; _foreign = 0;
        _capPeak = 0; _capLost = 0;
        _held = 0; _idle = 0; _fadeExit = 0; _rate0 = 0; _chunkLive = 0;
        _chunkSpawned = 0; _chunkRefused = 0; _windRefused = 0; _gustRefused = 0; _ungated = 0; _resets = 0;
        _dtSum = 0f; _steps = 0;
    }

    /// <summary>Seconds the simulation actually advanced this period, and the steps it took them in. A period
    /// that births and kills nothing while particles sit on screen has no TIME in it, which no other number
    /// can tell apart from a simulation that is not running. A zero over a non-zero step count means the game
    /// is PAUSED: the engine's <c>DeltaT</c> stops advancing while <c>GetActivePauseState</c> is non-zero.</summary>
    private static float _dtSum;
    private static int _steps;
}
