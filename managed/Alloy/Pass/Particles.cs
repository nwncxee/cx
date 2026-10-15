using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Particles (ADR 0011): the vertex build, which is the only part of an emitter that was ever a draw -- a
/// REBUILD, not a replay, since vanilla's own build survives nothing past its draw. Punch-through and
/// additive commute, so those batch by texture across the whole screen while the alpha-blended remainder
/// keeps the engine's order. Suppression is per EMITTER, because the engine's routine IS the cost.
/// </summary>
internal static unsafe partial class Particles
{
    // Every emitter, particle, node and material field this pass reads is a member of its CX.Engine view
    // (PartEmitter, Particle, MdlNodeEmitter, Material); the RE provenance lives on the EngineLayout rows
    // those are generated from, and PartEmitter::RenderEmitter @0x4ad4c0 is the function they all came off.

    /// <summary>A list longer than this is a bad pointer, not a .mtr.</summary>
    private const int MaxParams = 64;
    /// <summary>The .mtr parameter an effect declares to say how bright it really is. Named the way
    /// a content repo already names its own (<c>noirExempt</c> on <c>fsparticle</c>); absent means 1.0.</summary>
    private const string EmissiveParam = "fEmissive";
    /// <summary>Refuse an absurd declaration rather than hand the bloom a value that blows the whole frame
    /// out. Ten is already four stops over white.</summary>
    private const float MaxEmissive = 10f;

    // ---- the engine's own literals, read out of .rodata rather than guessed ----

    /// <summary>Colour scale: <c>DAT_013f3780</c> = 255.99, not 255. The build multiplies a clamped 0..1
    /// channel by it and truncates, so 255.99 is what makes a 1.0 channel reach 255 instead of stopping one
    /// short of it on every particle in the game.</summary>
    private const float ByteScale = 255.99f;
    /// <summary>Half, in the two places the engine uses it: the quaternion half-angle on the roll, and the
    /// half-extent every size term is scaled by.</summary>
    private const float Half = 0.5f;
    /// <summary>The interpolation rate an age-domain emitter with no lifetime falls back to
    /// (<c>DAT_013eee18</c> = 0.01).</summary>
    private const float NoLifeRate = 0.01f;

    /// <summary>Aurora blend factors: normal alpha is <c>SetBlendFunc(0, 1)</c>, additive
    /// <c>SetBlendFunc(0, 2)</c>. Both int arguments come from the DISASSEMBLY -- the decompiler drops the
    /// second and renders both arms as <c>SetBlendFunc(0)</c>, the int-valued twin of the trap
    /// <see cref="Beams"/>'s <c>projectionWorldZClip</c> fell into.</summary>
    private const uint GlSrcAlpha = 0x0302, GlOneMinusSrcAlpha = 0x0303, GlOne = 1;   // not-an-offset: GL blend-factor enums
    /// <summary>The alpha-discard threshold a punch-through emitter draws under. It is
    /// <c>fAlphaDiscardValue</c> under <c>RenderInterface::Enable(1)</c>, the same 0.2 every other cutout
    /// class in the Alloy pass uses (<see cref="AlloyClasses.Discard"/>); the other two blend classes draw
    /// under <c>Disable(1)</c> and get -1, the engine's own "no discard" sentinel.</summary>
    private const float PunchDiscard = 0.2f, NoDiscard = -1f;

    /// <summary>The engine's own <c>particleSoftness</c>, from the disassembly at 0x4ae641 (the decompiler
    /// renders it as a bare <c>SetShaderParamf(0)</c> -- the float is invisible there). It is <c>5.0</c> when
    /// <c>0.2 * S &gt; 0.2</c> and <c>1 / (0.2 * S)</c> otherwise over the largest half-extent S the emitter
    /// reaches, so the whole of it is <c>5 / min(S, 1)</c> -- a RECIPROCAL fade distance.</summary>
    private const float SoftScale = 5f;
    /// <summary>Softness is suppressed outright on splat emitters and on render types 1 and 2 -- the engine
    /// does the same, and the reason is geometric: those quads are pinned to a plane rather than facing the
    /// camera, so the depth gap under them is their own orientation and fading on it would eat the decal.
    /// </summary>
    private static bool SoftSuppressed(bool splat, int type) => splat || type == 1 || type == 2;

    /// <summary>The softness the stage pushes for one emitter: the collapsed <c>5 / min(S, 1)</c>, or 0 where
    /// it is suppressed or the emitter reaches no size at all. The one definition, so the SelfCheck can call
    /// it rather than restate it.</summary>
    internal static float SoftnessFor(bool splat, int type, float sMax)
        => SoftSuppressed(splat, type) || sMax <= 0f ? 0f : SoftScale / MathF.Min(sMax, 1f);

    /// <summary>Blend classes, in the order the Alloy pass draws them -- which is NOT the engine's single
    /// back-to-front order. Punch-through first is free: it writes depth and discards, so the blended classes
    /// get to depth-test against it. Additive last IS a real change -- addition does not commute with
    /// alpha-over, so a glow behind a dust cloud brightens; <c>particles.batch=0</c> is the A/B.</summary>
    private const int ClsPunch = 0, ClsBlend = 1, ClsAdd = 2, ClsCount = 3;

    /// <summary>One particle RECORD, in floats: seven <c>vec4</c>s, std430, <c>{pos.xyz, col} {far.xyz,
    /// colFar} {right.xyz, soft} {up.xyz, type} {centre.xyz, -} {u0,u1,v0,v1} {prev.xyz, -}</c>, the two
    /// colours as <c>uintBitsToFloat</c>. All WORLD space -- the build transforms the centre and the two
    /// axes, cheaper than the corners -- and <c>prev</c> is where that centre was LAST frame.</summary>
    private static readonly int RecFloats = sizeof(ParticleRec) / sizeof(float);
    /// <summary>The record and index buffers the vertex stage names, above the light rig it also reads
    /// (<see cref="SsboSlots"/>).</summary>
    internal const uint BindRecs = SsboSlots.ParticleRecs, BindIdx = SsboSlots.ParticleIdx;
    /// <summary>The compute stage's inputs. <see cref="BindSpans"/> is one entry per emitter the pass
    /// placed, <see cref="BindGroups"/> one per draw group.</summary>
    private const uint BindSpans = SsboSlots.ParticleSpans, BindGroups = SsboSlots.ParticleGroups;

    /// <summary>The index-build workgroup size, and the largest group the compute sort will order: one
    /// workgroup per draw group, one shared-memory bitonic network per blended group. ponytail: the cap is
    /// what the shared arrays hold (2048 * (float + uint) = 16KB, half the 32KB GL guarantees), and a bigger
    /// group keeps fill order -- a global key sort is the upgrade, and <c>sort-skipped=</c> the trip-wire.</summary>
    private const int SortLocal = 256, SortCap = 2048;
    /// <summary>The top bit of a group descriptor's particle count, set by the CPU on exactly the groups the
    /// sort may reorder. The count itself is bounded by <see cref="MaxRecs"/>, so the bit is free -- and it
    /// is the ONLY thing that decides a reorder, which is what keeps the classes separable.</summary>
    private const uint SortFlag = 0x80000000u;   // not-an-offset: a sort-key bit of ours

    /// <summary>Emitters tracked at once. Past this an emitter is declined and, with the engine walk gone,
    /// does not draw at all -- which is the whole cost of the cap.</summary>
    private const int MaxEmitters = 8192;
    /// <summary>Draw groups per pass. One group is one <c>glDrawArrays</c>; overflowing it declines the
    /// remaining emitters rather than merging groups that must not merge.</summary>
    private const int MaxGroups = 4096;
    /// <summary>Record-buffer growth, in records: start here, double until the frame fits, stop at the cap
    /// (112 bytes each, so the cap is 56MB) and stand the pass down rather than truncating.</summary>
    private const int MinRecs = 1024, MaxRecs = 1 << 19;

    /// <summary>One captured emitter. Only the identity and the transform are held; everything the build
    /// reads is re-read off the live emitter at draw time.</summary>
    private struct Emit
    {
        public IntPtr Ptr;
        /// <summary>The database part this emitter is, or -1 when it is an engine <c>PartEmitter</c>.
        /// The discriminant for every read the build makes.</summary>
        public int Part;
        /// <summary>The GL texture name the simulation resolved for this emitter, which is both the batch key
        /// and what the draw binds. There is no <c>CAurTexture</c> to bind through here.</summary>
        public uint DbTex;
        public Matrix4x4 Model;
        public bool Ident;
        public int CapFrame;
        /// <summary><see cref="AlloyRenderer.PassGen"/> at the pass this emitter was actually ISSUED.
        /// The engine-side hook suppresses on exactly this, so an emitter the Alloy pass declined -- or one
        /// that spawned after the last capture and has therefore never been built -- keeps its vanilla
        /// draw.</summary>
        public int IssuedGen;
        /// <summary>The material's <c>fEmissive</c>, read ONCE when the slot is allocated. The walk is
        /// a string compare over up to 64 records across two lists, which is cold work -- doing it in the
        /// per-frame resolve would put it on the hot path for every emitter on screen, every frame.</summary>
        public float Emissive;
        /// <summary>This emitter's slice of the persistent record buffer, in RECORDS. Stable across
        /// frames -- that is what makes the upload a delta rather than a rebuild -- and reassigned only when
        /// the emitter needs more room than it holds.</summary>
        public int RecBase, RecCap;
        /// <summary>This emitter's particle array and its count, as the enroller handed them in. The pass is
        /// indifferent to who stepped them -- <see cref="EmitterSim"/>'s pool records are laid out as engine
        /// <c>Particle</c>s -- which is what lets one build loop serve the owned simulation and the engine's
        /// own walk.</summary>
        public IntPtr* Parts;
        public int PartCount;
        /// <summary>The pass counter at the frame this emitter's block was last BUILT. Last frame's centres
        /// are only in the mirror while this says the mirror was written last frame.</summary>
        public int BuiltFrame;
        /// <summary>FNV-1a over the block as it was last UPLOADED. Equal means the engine's simulation
        /// produced the same records this frame, so the block is not re-sent. Zero means "never uploaded",
        /// which is why the allocator sets it to zero on every reassignment.</summary>
        public ulong Hash;
    }

    private static readonly Emit[] _tab = new Emit[MaxEmitters];
    private static int _tabN;
    /// <summary>Capture order = the engine's own back-to-front sort, which is the order the blended class
    /// has to keep. Rebuilt each capture window.</summary>
    private static readonly int[] _order = new int[MaxEmitters];
    private static int _orderN, _orderGen = -1;

    // per-pass staging: resolved once in Classify, consumed by the group builder and the writer.
    private static readonly int[] _sSlot = new int[MaxEmitters], _sTex = new int[MaxEmitters],
                                  _sCls = new int[MaxEmitters], _sCull = new int[MaxEmitters],
                                  _sCount = new int[MaxEmitters];
    private static readonly float[] _sEmis = new float[MaxEmitters];
    /// <summary>The <c>CAurTexture*</c> itself, not its GL id. It is the batch key AND what gets bound --
    /// see <see cref="SymbolMap.AurTextureBindInUnit"/> for why the id the Material hands out is not the id
    /// the engine draws with.</summary>
    private static readonly IntPtr[] _sTexObj = new IntPtr[MaxEmitters];
    private static readonly bool[] _sTaken = new bool[MaxEmitters];
    /// <summary>Is this staged emitter's texture a <see cref="TexRegistry"/> GL name rather than a
    /// <c>CAurTexture*</c>? The batch key is the same word either way; what differs is the bind.</summary>
    private static readonly bool[] _sDb = new bool[MaxEmitters];
    // _gFirst/_gCount index into _draw (emitters); _gStart/_gParts are the range of the draw-order INDEX
    // list the writer actually produced, in particles. Two pairs on purpose: a particle array can hand back
    // a null slot, so what is written is a result, not the product of the counts read at classify time.
    private static readonly int[] _gFirst = new int[MaxGroups], _gCount = new int[MaxGroups],
                                  _gStart = new int[MaxGroups], _gParts = new int[MaxGroups],
                                  _gTex = new int[MaxGroups], _gCls = new int[MaxGroups],
                                  _gCull = new int[MaxGroups];
    private static readonly float[] _gEmis = new float[MaxGroups];
    private static readonly IntPtr[] _gTexObj = new IntPtr[MaxGroups];
    private static readonly bool[] _gDb = new bool[MaxGroups];
    /// <summary>Vertices each entry of <see cref="_draw"/> actually wrote.</summary>
    private static readonly int[] _wrote = new int[MaxEmitters];
    /// <summary>One span descriptor per entry of <see cref="_draw"/> -- (record base, particles, index
    /// offset, unused), std430 <c>uvec4</c>. This is the whole of what replaced the per-particle CPU index
    /// loop: three numbers an emitter instead of four bytes a particle.</summary>
    private static readonly uint[] _span = new uint[MaxEmitters * 4];
    /// <summary>One descriptor per draw group -- (first span, spans, index offset, particles with
    /// <see cref="SortFlag"/>). One workgroup consumes one of these, and a group is single-class by
    /// construction, so no workgroup can ever move a particle across a class.</summary>
    private static readonly uint[] _grp = new uint[MaxGroups * 4];
    /// <summary>Emitters in draw order, grouped -- the permutation of <see cref="_order"/> the writer walks.
    /// </summary>
    private static readonly int[] _draw = new int[MaxEmitters];
    /// <summary>How many of the classified emitters <see cref="Group"/> actually placed. It can be short of
    /// the classified count when the group cap bites, and the difference matters: an emitter that never got
    /// into a group is one the engine still has to draw, so it must not be stamped as issued.</summary>
    private static int _drawN;

    private static ExtenderLog? _log;
    private static MatGetTextureFn _getTexture;
    private static AurTextureBindInUnitFn _bindInUnit;
    /// <summary>The engine's bound-texture cache, snapshot-restored across the draw loop. See
    /// <see cref="SymbolMap.GlBoundTextures"/> for why a pass that draws in its own context has to.</summary>
    private static ulong* _engBoundTex;
    private static uint* _engActiveStage, _engStageEnabled;
    /// <summary>Entries in <c>GLRender::m_boundTextures</c>, from the 0x10-iteration clear loop in
    /// <c>GLRender::ResetRenderStates</c> @0x4d1810.</summary>
    private const int EngTexStages = 16;
    private static EmitterGetTileAlphaFn _tileAlpha;
    private static EmitterGetGobAlphaFn _gobAlpha;
    private static EmitterIsSplatFn _isSplat;
    private static EmitterIsTintedFn _isTinted;

    /// <summary><see cref="_off"/> is the RESOLUTION latch -- a helper, a global or a hook that did not
    /// come up, so this client cannot run the Alloy pass at all. It is not the user toggle
    /// (<see cref="Enabled"/>) and <c>particles.enabled</c> must never clear it.</summary>
    private static bool _off;
    /// <summary>Every GL name and location the context owns, in one object: <see cref="ContextLost"/> replaces it.</summary>
    private sealed class PartGlState
    {
        public bool Failed;
        public uint Prog, Vao, RecBuf, IdxBuf, Samp;
        /// <summary>The index-build/sort compute program and its two descriptor buffers.</summary>
        public uint CsProg, SpanBuf, GrpBuf;
        public int CsEye = -1;
        public bool CsFailed;
        public int SpanCap, GrpCap;
        public int LocEye = -1;
        /// <summary>The fog block's locations: the stage fogs through the world pass's own uploader, so it
        /// takes the world pass's own location set.</summary>
        public AlloyRenderer.FeatLoc FogLoc;
        public int LocView = -1, LocProj = -1, LocTex = -1, LocCut = -1, LocAdditive = -1,
                   LocSoftOn = -1, LocDepthTex = -1, LocEmissive = -1, LocLit = -1, LocViz = -1;
        /// <summary>The velocity arm: the same two stages with CX_MOTION compiled in. 0 = the class keeps
        /// the camera's own vectors.</summary>
        public uint MotionProg;
        public int MLocView = -1, MLocProj = -1, MLocPrevVp = -1, MLocJitter = -1, MLocSize = -1, MLocTex = -1;
        /// <summary>The light rig's own uniform locations, in the order <see cref="AlloyLights"/> and
        /// <see cref="AlloyRenderer"/> want them handed back.</summary>
        public int LSunTex = -1, LSunDynTex = -1, LCubeTex = -1, LCubeCount = -1, LCubeDynTex = -1,
                   LSunMedia = -1, LLocalMedia = -1;
    }

    private static PartGlState _gl = new();
    /// <summary>The CPU shadow of the record buffer, addressed by <see cref="Emit.RecBase"/>. The build
    /// writes here; only the blocks whose hash moved are sent to GL.</summary>
    private static ParticleRec* _recs;
    private static int _recsCap, _recTop;
    /// <summary>Record indices in DRAW order, one per particle. The WRITE of this list is the GPU's, so the
    /// CPU shadow is only the <c>/particles sort 0</c> path's staging area -- kept because that dial is the
    /// escape hatch for a driver where the compute program does not build.</summary>
    private static uint* _idx;
    private static int _idxCap;

    private static int _frame;
    private static int _captured, _declined, _emitters, _groups, _suppressed, _overflow;
    /// <summary>Record blocks re-sent this period, blocks the hash said were unchanged, and times the block
    /// allocator ran out and started over (which costs one frame of vanilla emitters).</summary>
    private static int _blockUploads, _blockSkips, _recResets;
    /// <summary>Groups the compute pass ordered, and blended groups it left in fill order because they were
    /// larger than <see cref="SortCap"/>.</summary>
    private static int _sorted, _sortSkipped;
    // Why an emitter was declined, split -- "particles are still vanilla" has five different fixes behind
    // it and only the split says which.
    private static int _decFade, _decEmpty, _decShader, _decTex, _decFull, _reclaimed, _softPasses,
                       _emissive, _litGroups;
    /// <summary>Tinted emitters drawn WITHOUT the scene tint, because there is no engine light list to sample
    /// at the emitter's origin. A named difference, on the period line.</summary>
    private static int _dbNoTint;
    /// <summary>How far inside its own cell a quad's UV range is pulled, as a fraction of the cell. An atlas
    /// cell has no border, so bilinear filtering at its edge reaches into the NEIGHBOURING cell and a mip
    /// averages cells outright. ponytail: a fraction of the cell rather than a true half-texel, which would
    /// mean querying and caching every texture's dimensions; 1/128 is half a pixel at NWN's 64px cells.</summary>
    private const float CellInset = 1f / 128f;
    private static readonly int[] _cType = new int[7], _cCls = new int[ClsCount];
    private static int _cSplat, _cTinted, _cGrid;
    /// <summary>The engine's own names for the render types, from <c>PartEmitter::Initialize</c> @0x4aaaa0 --
    /// the same strings the model's emitter node carries, so a census line names something greppable in the
    /// model data rather than an index.</summary>
    private static readonly string[] TypeNames =
    {
        "Normal", "Billboard_to_Local_Z", "Billboard_to_World_Z", "Linked",
        "Aligned_to_World_Z", "Aligned_to_Particle_Dir", "Motion_Blur",
    };

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        SceneDepth.Attach(log);

        _getTexture = EngineFn.MatGetTexture;
        _bindInUnit = EngineFn.AurTextureBindInUnit;
        _engBoundTex = (ulong*)Native.Sym(SymbolMap.GlBoundTextures);
        _engActiveStage = (uint*)Native.Sym(SymbolMap.GlActiveTextureStage);
        _engStageEnabled = (uint*)Native.Sym(SymbolMap.GlEnabledTextureStages);
        _tileAlpha = EngineFn.EmitterGetTileAlpha;
        _gobAlpha = EngineFn.EmitterGetGobAlpha;
        _isSplat = EngineFn.EmitterIsSplat;
        _isTinted = EngineFn.EmitterIsTinted;

        if (_getTexture == null || _bindInUnit == null || _tileAlpha == null || _gobAlpha == null || _isSplat == null
            || _isTinted == null
            || !AurTex.Ready)
        {
            _off = true;
            log.Info("Particles: an emitter helper, a global or AurTex did not resolve -- no particles are drawn");
            return;
        }

        log.Info($"Particles: armed and {(Enabled.V ? "ON" : "IDLE")} -- {Describe()} (/particles enabled 1 arms it)");
    }

    /// <summary>Enrolment from <see cref="EmitterSim"/>, which walks <c>emitterbucket</c> itself now that
    /// <c>Scene::DoEmitterBucket</c> is not called. The same two jobs the render hook had -- learn the
    /// population and its transform -- minus the suppression, because there is no original left to decline.
    /// The caller walks in its own back-to-front order, so capture order still IS draw order.</summary>
    internal static void Enroll(IntPtr em, int part, uint dbTex, IntPtr scene, IntPtr* parts, int count)
    {
        // CX-188: `part` is always a database part index. The engine arm this used to carry (an engine
        // PartEmitter*, its Scene checked for the world BSP and the table dropped on an area change) had no
        // caller: EmitterSim's only Enroll passes slot.Part, and nothing fills a snapshot from a PartEmitter.
        if (_off || !Enabled.V || parts == null || count <= 0 || part < 0) return;
        Capture(em, part, dbTex, parts, count);
    }

    /// <summary>Record this emitter and its place in the sort. Returns its table slot, or -1 when the table
    /// is full -- which is not a failure, it just leaves the emitter vanilla.</summary>
    private static int Capture(IntPtr em, int part, uint dbTex, IntPtr* parts, int count)
    {
        // The capture window opens on the first emitter of a new pass: DoEmitterBucket walks the sorted list
        // in one go, so "the pass changed" is the same event as "a new walk started" without a second hook.
        if (_orderGen != AlloyRenderer.PassGen) { _orderGen = AlloyRenderer.PassGen; _orderN = 0; }

        // One scan finds the emitter, the first tombstone, and the first slot whose emitter is alive but has
        // not been walked for two windows. That last one is what keeps the cap from being permanent: an
        // emitter left behind (walked away from, culled) never destructs, so without reclaiming, one busy
        // area would fill the table and every emitter after it would stay vanilla until a transition -- the
        // feature silently degrading to nothing with only the decline counter to say so.
        int s = -1, free = -1, stale = -1;
        for (int i = 0; i < _tabN; i++)
        {
            if (part >= 0 ? _tab[i].Part == part : _tab[i].Ptr == em && _tab[i].Part < 0) { s = i; break; }
            if (_tab[i].Ptr == IntPtr.Zero && _tab[i].Part < 0) { if (free < 0) free = i; }
            else if (stale < 0 && _tab[i].CapFrame < _frame - 1) stale = i;
        }
        if (s < 0)
        {
            if (free >= 0) s = free;
            else if (_tabN < MaxEmitters) s = _tabN++;
            else if (stale >= 0) { s = stale; _reclaimed++; }
            else { _declined++; _decFull++; return -1; }
            _tab[s].IssuedGen = -1;
            // #261: a slot is reused after a tombstone, a stale reclaim or a Forget(), and the block
            // allocator's bump counter is reset by the last of those. Carrying the old RecBase/RecCap into
            // a new emitter would hand it a slice the allocator has already handed to someone else -- two
            // emitters writing one block, with the hash layer making the wrong one sticky.
            _tab[s].RecBase = 0; _tab[s].RecCap = 0; _tab[s].Hash = 0;
            // CX-120: a DB emitter has no Material, so no .mtr parameter list to walk. fEmissive stays 1.0
            // there -- named on the period line rather than silently assumed (see Census).
            _tab[s].Emissive = part >= 0 ? 1f : ReadEmissive(new PartEmitter(em).Material);
            _captured++;
        }
        ref Emit e = ref _tab[s];
        e.Ptr = em;
        e.Part = part;
        e.DbTex = dbTex;
        e.CapFrame = _frame;
        e.Parts = parts; e.PartCount = count;
        // RenderEmitter pushes nothing and Scene::DoEmitterBucket does no matrix work either, so an emitter
        // draws on whatever base the frame left in the model stack -- which CX-109 established is the identity
        // (AlloyCamera.ModelBase, CX-109 §2). Particles simulate in world space, so this
        // is also the only base that would place them correctly.
        e.Model = AlloyCamera.ModelBase;
        e.Ident = e.Model.IsIdentity;
        if (_orderN < MaxEmitters) _order[_orderN++] = s;
        return s;
    }

    // ---- the Alloy pass -------------------------------------------------------------------------------

    /// <summary>The Alloy emitter pass, after the beams -- vanilla's own placement (step 28, straight after
    /// <c>Scene::RenderBeams</c>). Leaves GL_BLEND off, the depth mask on and culling off, which is the state
    /// the beams handed over and the probe capture after this needs.</summary>
    internal static void Draw(Matrix4x4* view, Matrix4x4* proj, int* viewport, float near, float far,
                              bool lightingOn)
    {
        _frame++;
        if (_off || !Enabled.V || _gl.Failed || _orderN == 0 || Gl.DrawArrays == null) return;
        if (!EnsureProgram()) return;

        // The camera the billboards face: the frame's own camera block, where the seam published this view's pose.
        if (!FrameInputs.HasCamera) return;
        Quaternion camRot = FrameInputs.CameraRot;
        Basis(camRot, out Vector3 camFwd, out Vector4 camQ, out Vector3 axisY, out Vector3 axisX);
        // The eye, from the view matrix rather than from the camera object: the sphere normal has to bulge
        // TOWARDS the viewer and the engine's quaternion does not say by itself which way its own Z points.
        // Inverting the matrix we are about to upload cannot disagree with the matrix we are about to upload.
        Vector3 eye = default;
        if (Matrix4x4.Invert(*view, out Matrix4x4 vi)) eye = new Vector3(vi.M41, vi.M42, vi.M43);

        int n = Classify();
        if (n == 0) return;
        int groups = Group(n);
        if (groups == 0 || _drawN == 0) return;

        int parts = 0;
        for (int i = 0; i < _drawN; i++) parts += _sCount[_draw[i]];
        if (!EnsureBuffers(parts)) return;

        // #148: only the alpha-blended class is lit. Additive is self-illuminated by definition, and
        // punch-through in this game's content is Turn Undead auras and a holy strike -- lighting those
        // would dim the effect in exactly the dark crypt it is cast in. Decided BEFORE the build, because
        // the build reads it: an emitter drawn lit must not also get the CPU tint.
        bool lit = Lit.V && lightingOn && AlloyLights.Ready;

        // #261: build each emitter's records into its OWN persistent block. #288: what this loop stages for
        // the index list is one SPAN an emitter -- the list itself is written by the compute dispatch below.
        int at = 0;
        for (int i = 0; i < _drawN; i++)
        {
            int k = _draw[i];
            ref Emit e = ref _tab[_sSlot[k]];
            // Every entry gets a span, so an emitter that produced nothing is an EMPTY span rather than a
            // stale one -- the compute pass walks spans by group membership, not by what was written.
            _span[i * 4] = 0; _span[i * 4 + 1] = 0; _span[i * 4 + 2] = (uint)at; _span[i * 4 + 3] = 0;
            if (!EnsureBlock(ref e, _sCount[k])) { _wrote[i] = 0; continue; }
            Span<ParticleRec> block = new Span<ParticleRec>(_recs, _recsCap).Slice(e.RecBase, e.RecCap);
            int wrote = BuildEmitter(ref e, block, _sCount[k],
                                     camFwd, camQ, axisY, axisX, eye,
                                     lit && _sCls[k] == ClsBlend);
            _wrote[i] = wrote;
            if (wrote == 0) continue;
            // The delta. An emitter whose particles did not move -- a frozen splat field, an emitter the
            // simulation skipped -- costs a hash of its own memory and no bus traffic at all.
            // ponytail: a camera-derived basis in record slots 8..14 can never match across a rotation.
            // Upgrade: build the basis in cx_particle_v instead. See Fnv for the measurement that parks it.
            ulong h = Fnv(block.Slice(0, wrote));
            if (h != e.Hash)
            {
                e.Hash = h;
                fixed (ParticleRec* up = block)
                    Gl.NamedBufferSubData(_gl.RecBuf,
                                          (IntPtr)((long)e.RecBase * sizeof(ParticleRec)),
                                          (IntPtr)((long)wrote * sizeof(ParticleRec)), up);
                _blockUploads++;
            }
            else _blockSkips++;
            // #288: the span, not the particles. The index list itself is filled by the compute pass below
            // -- or, under /particles sort 0, from this same descriptor on the CPU.
            _span[i * 4] = (uint)e.RecBase; _span[i * 4 + 1] = (uint)wrote; _span[i * 4 + 2] = (uint)at;
            at += wrote;
        }
        if (at == 0) return;

        // groups are contiguous in _draw and written in that order, so their PARTICLE ranges follow directly
        int cursor = 0;
        for (int gi = 0; gi < groups; gi++)
        {
            int gn = 0;
            for (int j = 0; j < _gCount[gi]; j++) gn += _wrote[_gFirst[gi] + j];
            _gStart[gi] = cursor; _gParts[gi] = gn;
            cursor += gn;
        }

        // #148: the depth snapshot, here rather than at a pass slot of its own. It is not an ordering
        // decision -- nothing else in the frame reads it and nothing draws between it and the particles --
        // so it does not belong in the graph, where it would only make FrameGraph.SelfCheck churn.
        // Taken after the build so the frame's cost is paid only when there is something to soften.
        bool soft = Soft.V && SceneDepth.Take(viewport);

        if (!Order(groups, eye))
        {
            for (int i = 0; i < _drawN; i++)
                for (int j = 0; j < _wrote[i]; j++)
                    _idx[_span[i * 4 + 2] + j] = _span[i * 4] + (uint)j;
            Gl.NamedBufferSubData(_gl.IdxBuf, 0, (IntPtr)((long)at * sizeof(uint)), _idx);
        }
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindRecs, _gl.RecBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIdx, _gl.IdxBuf);
        Gl.UseProgram(_gl.Prog);
        Gl.UniformMatrix4fv(_gl.LocView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_gl.LocProj, 1, 0, (float*)proj);
        Gl.Uniform3fv(_gl.LocEye, 1, (float*)&eye);
        Gl.Uniform1i(_gl.LocTex, 0);
        AlloyRenderer.UploadFog(_gl.FogLoc);
        if (lit)
        {
            AlloyLights.BindForDraw();
            AlloyRenderer.BindPass(_gl.LCubeCount, viewport, near, far);
            // #336: /particles shadows 0 -- no cube taps on particle fragments (uLocalCubes 0 makes the
            // receiver answer lit at once). The pass read 1,2-1,8ms GPU at overdraw x0,6-0,9, so the term
            // is per-fragment cost, and the lit path's cube taps are the one heavy thing in it.
            if (!Shadows.V && _gl.LCubeCount >= 0) Gl.Uniform1i(_gl.LCubeCount, 0);
            // A particle is a point in a volume, not a point on a surface: the shadow lookups' normal
            // offset is a surface remedy and applying it to a camera-facing billboard normal is what put
            // bands across the smoke. Both lookups keep their constant biases; only the offset goes.
            Gl.Uniform1f(_gl.LSunMedia, 1f);
            Gl.Uniform1f(_gl.LLocalMedia, 1f);
        }
        Gl.Uniform1i(_gl.LocViz, Viz.V);
        Gl.Uniform1i(_gl.LocSoftOn, soft ? 1 : 0);
        Gl.Uniform1i(_gl.LocDepthTex, (int)SceneDepth.Unit);
        if (soft) { SceneDepth.Bind(); _softPasses++; }
        Gl.ActiveTexture(Gl.TEXTURE0);
        if (Sampler.Boot && EnsureSampler()) Gl.BindSampler(0, _gl.Samp);
        Gl.BindVertexArray(_gl.Vao);

        // #237: _bindInUnit below is the ENGINE's bind wrapper, and it writes the engine's per-stage cache
        // while the glBindTexture it performs lands in ALLOY's context. Bindings are per-context, so every
        // entry it leaves is a lie about the engine's own context -- and the engine's next matching bind
        // early-outs on it (SetActiveTextureAndBind @0x45ce10 returns before glActiveTexture/glBindTexture on
        // a hit), drawing the 2D UI with whatever its context still held. Snapshot the three globals and put
        // them back: Alloy never touched the engine's context, so the values found here stay true across the
        // pass. The alternative -- binding raw, as OwnedTextures does -- would skip the resolve and the
        // animation-controller tick this pass needs.
        bool engCache = _engBoundTex != null && _engActiveStage != null && _engStageEnabled != null;
        ulong* engSaved = stackalloc ulong[EngTexStages];
        uint engStage = 0, engEnabled = 0;
        if (engCache)
        {
            new ReadOnlySpan<ulong>(_engBoundTex, EngTexStages).CopyTo(new Span<ulong>(engSaved, EngTexStages));
            engStage = *_engActiveStage; engEnabled = *_engStageEnabled;
        }

        int cls = -1, cull = -1;
        uint texName = 0;
        IntPtr texObj = IntPtr.Zero;
        bool texDb = false;
        float emis = float.NaN;
        for (int gi = 0; gi < groups; gi++)
        {
            if (_gParts[gi] == 0) continue;
            if (_gCls[gi] != cls)
            {
                cls = _gCls[gi];
                if (cls == ClsPunch)
                {
                    Gl.Disable(Gl.BLEND); Gl.DepthMask(1);
                    SceneDepth.Invalidate();   // cutouts write depth: halos/weather must not reuse the pre-particle copy
                    Gl.Uniform1f(_gl.LocCut, PunchDiscard); Gl.Uniform1i(_gl.LocAdditive, 0);
                }
                else
                {
                    Gl.Enable(Gl.BLEND); Gl.DepthMask(0);
                    Gl.BlendFunc(GlSrcAlpha, cls == ClsAdd ? GlOne : GlOneMinusSrcAlpha);
                    Gl.Uniform1f(_gl.LocCut, NoDiscard);
                    Gl.Uniform1i(_gl.LocAdditive, cls == ClsAdd ? 1 : 0);
                }
                Gl.Uniform1i(_gl.LocLit, lit && cls == ClsBlend ? 1 : 0);
                if (lit && cls == ClsBlend) _litGroups++;
            }
            if (_gCull[gi] != cull)
            { cull = _gCull[gi]; if (cull != 0) Gl.Enable(Gl.CULL_FACE); else Gl.Disable(Gl.CULL_FACE); }
            if (_gTexObj[gi] != texObj || _gDb[gi] != texDb)
            {
                texObj = _gTexObj[gi];
                texDb = _gDb[gi];
                // CX-120: a DB emitter's texture is Alloy's own -- a GL name from TexRegistry, bound
                // directly. There is no CAurTexture to resolve, no animation controller to tick and no
                // engine bind cache to keep honest.
                if (texDb) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)(nint)texObj); texName = (uint)(nint)texObj; }
                // The engine's own bind, on unit 0, frame -1 ("whatever the controller says") -- exactly the
                // call Material::BindAllStandardTextures makes. It resolves the referenced texture, takes the
                // id off the RESOLVED object, and ticks the animation controller. A raw glBindTexture of the
                // Material's own id skips all three, which is how a smoke emitter ended up drawing another
                // effect's artwork.
                else
                {
                    _bindInUnit(texObj, 0, -1);
                    // CX-48: only the engine's resolve hides the name it landed on. Asked once per texture
                    // CHANGE and only when there is a velocity sweep to use it -- client state, no stall --
                    // and it is the one way to bind that texture again without ticking its controller twice.
                    texName = 0;
                    if (_gl.MotionProg != 0)
                    {
                        int nm = 0;
                        Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &nm);
                        texName = (uint)nm;
                    }
                }
            }
            _motion.TexName[gi] = texName;
            if (_gEmis[gi] != emis) { emis = _gEmis[gi]; Gl.Uniform1f(_gl.LocEmissive, emis); }
            // Six vertices a particle, expanded from gl_VertexID -- there is no vertex buffer any more.
            Gl.DrawArrays(Gl.TRIANGLES, _gStart[gi] * 6, _gParts[gi] * 6);
        }

        if (engCache)
        {
            new ReadOnlySpan<ulong>(engSaved, EngTexStages).CopyTo(new Span<ulong>(_engBoundTex, EngTexStages));
            *_engActiveStage = engStage; *_engStageEnabled = engEnabled;
        }

        if (_gl.Samp != 0) Gl.BindSampler(0, 0);
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.BLEND);
        Gl.Disable(Gl.CULL_FACE);
        Gl.DepthMask(1);

        // Stamped only on emitters whose records actually reached the GPU -- the engine-side hook reads
        // this to decide whether to skip its own draw, so an emitter that produced nothing (every particle
        // slot null, or a linked pair with no partner) has to keep its vanilla one rather than vanish.
        for (int i = 0; i < _drawN; i++)
            if (_wrote[i] > 0) { _tab[_sSlot[_draw[i]]].IssuedGen = AlloyRenderer.PassGen; _emitters++; }
        _groups += groups;
        // CX-48: what was drawn, for the velocity sweep that re-issues it later in the frame.
        NoteMotionGroups(groups);
    }

    /// <summary>Resolve every captured emitter into (texture, class, cull, particle count), dropping the
    /// ones this pass cannot build. Returns how many made it into the staging arrays.</summary>
    private static int Classify()
    {
        bool hdr = PostChain.Armed;
        int n = 0;
        for (int i = 0; i < _orderN; i++)
        {
            int s = _order[i];
            if (s >= _tabN) continue;
            ref Emit e = ref _tab[s];
            // A dead capture: the emitter stopped being walked (culled, finished, or its Gob went away
            // between the walk and here). Replaying it would read a particle array nobody maintains.
            if ((e.Ptr == IntPtr.Zero && e.Part < 0) || e.CapFrame < _frame - 1) continue;
            if (!Resolve(ref e, out int tex, out IntPtr texObj, out int cls,
                         out int cull, out int count))
            { _declined++; continue; }
            _sSlot[n] = s; _sTex[n] = tex; _sCls[n] = cls; _sCull[n] = cull; _sCount[n] = count;
            // Emissive means nothing without headroom to put it in: on an 8-bit target everything above 1.0
            // clips to white, so a declared 4.0 would turn a warm flame into a flat disc. #139 off, or
            // refusing to arm, forces the neutral value.
            _sTexObj[n] = texObj;
            _sDb[n] = e.Part >= 0;
            _sEmis[n] = hdr ? _tab[s].Emissive : 1f;
            if (_sEmis[n] != 1f) _emissive++;
            _sTaken[n] = false;
            n++;
        }
        return n;
    }

    /// <summary>Everything about an emitter that decides whether -- and how -- the Alloy pass draws it.
    /// False means DECLINE, which costs one vanilla emitter draw and nothing else.</summary>
    private static bool Resolve(ref Emit slot, out int tex, out IntPtr texObj,
                                out int cls, out int cull, out int count)
    {
        tex = 0; texObj = IntPtr.Zero; cls = 0; cull = 0; count = 0;
        if (!Snapshot(in slot, out Emitter e)) { _decEmpty++; return false; }

        // RenderEmitter's own opening early-out: a fading Gob or tile draws NO particles at all. Declining
        // is the cheapest reproduction -- the engine takes the same exit.
        if (e.Faded) { _decFade++; return false; }

        int type = e.Type;
        // Type 3 pairs each particle with the NEXT one, so the last has no partner and the loop runs one
        // short. The engine does the same subtraction.
        count = slot.PartCount - (type == 3 ? 1 : 0);
        if (count < 1 || e.Life == 0f) { _decEmpty++; return false; }
        if (slot.Parts == null) { _decEmpty++; return false; }

        // CX-120: the texture is a TexRegistry GL name, resolved by the simulation when it took the
        // emitter's slot. Zero means the atlas has not landed yet -- the registry marks its dependents
        // dirty at arrival, so the emitter draws from the frame it does.
        if (slot.DbTex == 0) { _decTex++; return false; }
        tex = (int)slot.DbTex;
        texObj = (IntPtr)slot.DbTex;

        cls = e.Blend == 1 ? ClsPunch : e.Blend == 0 ? ClsBlend : ClsAdd;
        cull = !e.NoCull ? 1 : 0;
        return true;
    }

    /// <summary>This captured emitter's parameters, from whichever source owns it. One call, and every
    /// read after it is a plain field -- which is what lets one build loop serve the engine's objects and the
    /// scene database.</summary>
    private static bool Snapshot(in Emit slot, out Emitter e)
    {
        e = default;
        return slot.Part >= 0 && EmitterSim.FillDb(slot.Part, ref e);
    }

    /// <summary>Can these two staged emitters share a draw? Texture and cull are GL state; emissive is a
    /// uniform, and a uniform that varies inside a draw is one that has to cut it. Exact float compare is
    /// right here -- both values came out of the same parsed record or are both the literal 1.0.</summary>
    private static bool SameGroup(int a, int b)
        => _sTexObj[a] == _sTexObj[b] && _sCull[a] == _sCull[b] && _sEmis[a] == _sEmis[b]
        && _sDb[a] == _sDb[b];

    /// <summary>Build the draw order and its groups. Punch-through and additive gather by (texture, cull)
    /// across the whole screen, which is legal because neither class depends on order; the blended remainder
    /// keeps the engine's back-to-front sequence and merges only ADJACENT runs, the most that can be done
    /// without changing what the frame looks like.</summary>
    private static int Group(int n)
    {
        int g = 0, at = 0;
        _drawN = 0;
        // particles.batch=0 walks the capture order ONCE, merging only adjacent runs -- so the three classes
        // interleave exactly as the engine sorted them and the additive-over-blended reorder is genuinely
        // gone. Splitting by class here regardless of the dial would have left that reorder in place and
        // made the A/B answer "no change" to a question it never asked.
        if (!Batch.Boot)
        {
            for (int i = 0; i < n && g < MaxGroups; i++)
            {
                if (_sTaken[i]) continue;
                int first0 = at;
                _draw[at++] = i; _sTaken[i] = true;
                for (int j = i + 1; j < n && !_sTaken[j] && _sCls[j] == _sCls[i] && SameGroup(j, i); j++)
                { _draw[at++] = j; _sTaken[j] = true; }
                _gFirst[g] = first0; _gCount[g] = at - first0;
                _gTex[g] = _sTex[i]; _gTexObj[g] = _sTexObj[i]; _gDb[g] = _sDb[i];
                _gCls[g] = _sCls[i]; _gCull[g] = _sCull[i]; _gEmis[g] = _sEmis[i];
                g++;
            }
            if (at < n) _overflow++;
            _drawN = at;
            return g;
        }
        for (int cls = 0; cls < ClsCount && g < MaxGroups; cls++)
        {
            for (int i = 0; i < n && g < MaxGroups; i++)
            {
                if (_sTaken[i] || _sCls[i] != cls) continue;
                int first = at;
                _draw[at++] = i; _sTaken[i] = true;
                if (cls == ClsBlend)
                {
                    // adjacent-run merge, in capture order
                    for (int j = i + 1; j < n && _sCls[j] == cls && !_sTaken[j]
                                        && SameGroup(j, i); j++)
                    { _draw[at++] = j; _sTaken[j] = true; }
                }
                else
                {
                    // ponytail: O(n^2) gather over a list capped at 512, run once a frame. A key-sorted
                    // pass is the upgrade if an area ever puts hundreds of emitters on screen at once.
                    for (int j = i + 1; j < n; j++)
                        if (!_sTaken[j] && _sCls[j] == cls && SameGroup(j, i))
                        { _draw[at++] = j; _sTaken[j] = true; }
                }
                _gFirst[g] = first; _gCount[g] = at - first;
                _gTex[g] = _sTex[i]; _gTexObj[g] = _sTexObj[i]; _gDb[g] = _sDb[i];
                _gCls[g] = cls; _gCull[g] = _sCull[i]; _gEmis[g] = _sEmis[i];
                g++;
            }
        }
        // Only a real drop counts. Landing exactly ON the cap with every emitter placed is a full table,
        // not an overflow, and counting it would put a permanent warning on a frame that lost nothing.
        if (at < n) _overflow++;
        _drawN = at;
        return g;
    }

    // ---- the vertex build -----------------------------------------------------------------------------

    /// <summary>The camera-derived bases <c>RenderEmitter</c> computes once per emitter, hoisted to once per
    /// pass because every term in them is the camera's. The quaternion at <c>CurrentCamera+0xbc</c> is stored
    /// (w, x, y, z), which is what makes these the standard rotations of the unit axes. The caller hands the
    /// pose over rather than a camera pointer, because the database path has no camera object.</summary>
    private static void Basis(in Quaternion c, out Vector3 fwd, out Vector4 q, out Vector3 axisY, out Vector3 axisX)
    {
        float w = c.W, x = c.X, y = c.Y, z = c.Z;
        q = new Vector4(w, x, y, z);
        fwd = AxisZ(w, x, y, z);
        axisY = new Vector3(2f * (x * y - z * w), 1f - 2f * (x * x + z * z), 2f * (y * z + x * w));
        axisX = AxisX(w, x, y, z);
    }

    private static Vector3 AxisX(float w, float x, float y, float z)
        => new(1f - 2f * (y * y + z * z), 2f * (x * y + w * z), 2f * (x * z - w * y));

    private static Vector3 AxisZ(float w, float x, float y, float z)
        => new(2f * (x * z + w * y), 2f * (y * z - w * x), 1f - 2f * (x * x + y * y));

    /// <summary>Hamilton product, (w, x, y, z) in and out -- the engine's <c>Quaternion::operator*</c>.
    /// </summary>
    private static Vector4 QMul(in Vector4 a, in Vector4 b)
        => new(a.X * b.X - a.Y * b.Y - a.Z * b.Z - a.W * b.W,
               a.X * b.Y + a.Y * b.X + a.Z * b.W - a.W * b.Z,
               a.X * b.Z - a.Y * b.W + a.Z * b.X + a.W * b.Y,
               a.X * b.W + a.Y * b.Z - a.Z * b.Y + a.W * b.X);

    /// <summary>Port of <c>PartEmitter::RenderEmitter</c>'s build loop: six vertices per particle, written
    /// in the engine's own order and layout. Returns the vertex count written.
    /// <para>Allocation-free by construction -- this runs inside the colour bracket.</para></summary>
    private static int BuildEmitter(ref Emit e, Span<ParticleRec> dst, int count,
                                    Vector3 camFwd, Vector4 camQ, Vector3 axisY, Vector3 axisX,
                                    Vector3 eye, bool drawsLit)
    {
        if (!Snapshot(in e, out Emitter emv)) return 0;
        IntPtr em = e.Ptr;
        int type = emv.Type;
        float life = emv.Life;
        bool splat = emv.Splat;
        // #148: the CPU tint and the fragment's clustered lighting are the SAME quantity computed two ways --
        // one sample at the emitter's origin against the whole scene's lights, versus a per-fragment walk of
        // the cluster this fragment sits in. Applying both multiplies the scene lighting into itself, which
        // reads as a smoke plume that goes dark the moment it is properly lit. The fragment wins; the tint
        // stays for every path the fragment does not cover.
        bool tinted = !drawsLit && emv.Tinted;

        // scale: the Gob's, through the vtable slot RenderEmitter calls. No Gob means 1; in DB mode it is the
        // object's adjusted scale, which is the same quantity through ObjectBridge.
        float scale = emv.Scale;

        // ---- per-emitter terms, all hoisted exactly as the engine hoists them ----

        // the interpolation parameter's rate: over the particle's INDEX, or over its age
        bool byAge = emv.LifeInterp != 0;
        float rate = !byAge ? 1f / MathF.Max(1, e.PartCount)
                            : life > 0f ? 1f / life : NoLifeRate;

        // colour, optionally run through the scene lighting at the emitter's position first
        Vector3 cStart = emv.ColorStart, cEnd = emv.ColorEnd;
        // CX-188: a `m_isTinted` emitter draws its declared colour. The CPU tint sampled the ENGINE's
        // light list at the emitter's origin, and the database has no such list to sample -- every emitter
        // here is a database emitter (nothing fills a snapshot from a PartEmitter any more), so the engine
        // arm was dead. Counted, not assumed.
        if (tinted) { tinted = false; _dbNoTint++; }
        cStart = Vector3.Clamp(cStart, Vector3.Zero, Vector3.One) * ByteScale;
        cEnd = Vector3.Clamp(cEnd, Vector3.Zero, Vector3.One) * ByteScale;
        bool lerpColor = cStart != cEnd;

        // alpha: the emitter's own pair, folded with the tile's and the Gob's.
        // DB mode reads the two columns that carry those: PartFade is the tile-fade/reveal product the tiles
        // stream writes (PartEmitter::GetTileAlpha @0x4ac300 reads TileData+0x70), and the object's tint
        // alpha is what GetGobAlpha @0x4ac2d0 answers (it is Gob+0x108 and nothing else).
        // The part's OWN alpha controller is deliberately NOT in this product: the engine folds it through
        // PartTriMesh::Mat, which an emitter part never reaches. Folding it here multiplied every VFX whose
        // emitter node fades in from `alpha 0` by zero -- a lightning beam drew a perfectly placed bolt of
        // fully transparent quads.
        float aScene = SceneDb.PartFade[emv.Part] * SceneDb.ObjTint[emv.Obj.Index].W;
        float aStart = Sat(emv.AlphaStart * aScene) * ByteScale;
        float aEnd = Sat(emv.AlphaEnd * aScene) * ByteScale;
        bool lerpAlpha = emv.AlphaStart != emv.AlphaEnd;

        // half-extents; the engine only interpolates a dimension whose two ends actually differ
        float sx0 = emv.SizeStartX, sx1 = emv.SizeEndX;
        float sy0 = emv.SizeStartY, sy1 = emv.SizeEndY;
        float wStart = sx0 * scale * Half, wEnd = sx1 * scale * Half;
        float hStart = sy0 * scale * Half, hEnd = sy1 * scale * Half;
        bool lerpW = !splat && sx0 != sx1;
        bool lerpH = !splat && sy0 != sy1 && (sy0 != 0f || sy1 != 0f);

        // UV grid
        int gx = emv.GridX, gy = emv.GridY;
        bool grid = Grid.V && (gx > 1 || gy > 1);
        if (Diag.V)
        {
            if ((uint)type < 7) _cType[type]++;
            int blend = emv.Blend;
            _cCls[blend == 1 ? ClsPunch : blend == 0 ? ClsBlend : ClsAdd]++;
            if (splat) _cSplat++;
            if (tinted) _cTinted++;
            if (grid) _cGrid++;
        }
        if (gx == 0) gx = 1;
        if (gy == 0) gy = 1;
        float du = 1f / gx, dv = 1f / gy;
        float fps = emv.Fps;
        float period = fps > 0f ? 1f / fps : 0f;
        float frameStart = emv.FrameStart, frameEnd = emv.FrameEnd;

        // #148: softness, hoisted with everything else the engine hoists. S is the largest half-extent the
        // emitter can REACH, so an end size only counts when the engine would actually interpolate to it --
        // the same two predicates the size lerp uses, not a blind max over all four.
        float sMax = MathF.Max(wStart, hStart);
        if (splat || sx0 != sx1) sMax = MathF.Max(sMax, wEnd);
        if (splat || sy0 != 0f || sy1 != 0f) sMax = MathF.Max(sMax, hEnd);
        float soft = SoftnessFor(splat, type, sMax);

        // the world-Z flip render type 4 stands its quads up with: the engine picks it off the sign of the
        // camera's own Y axis in Z, once per emitter
        float flip = axisY.Z >= 0f ? 1f : -1f;

        // types 1 and 2 replace the camera basis with one of their own
        Vector3 fwd = camFwd; Vector4 q = camQ;
        if (type == 1)
        {
            Quat o = emv.M.Orient;
            q = new Vector4(o.W, o.X, o.Y, o.Z);
            fwd = AxisZ(o.W, o.X, o.Y, o.Z);
        }
        else if (type == 2)
        {
            q = new Vector4(1f, 0f, 0f, 0f);
            fwd = new Vector3(0f, 0f, 1f);
        }

        // The render type, carried per vertex in the one byte the packed normal leaves spare. It costs
        // nothing when the viz dial is off and it is the difference between "particles are wrong" and
        // "Motion_Blur is wrong", which are questions of very different size.
        float vizType = type;
        IntPtr* parts = e.Parts;
        float u0 = 0f, u1 = 1f, v0 = 0f, v1 = 1f;
        Span<ParticleRec> recs = dst;
        int written = 0;
        // Is the mirror's own content last frame's? A slice reassigned (Hash 0) or an emitter the pass did
        // not build last frame holds some other frame's particles, which is no history at all.
        bool fresh = e.Hash == 0 || e.BuiltFrame != _frame - 1;
        e.BuiltFrame = _frame;

        for (int i = 0; i < count; i++)
        {
            var pt = new Particle(parts[i]);
            if (pt.IsNull) continue;
            bool pSplat = pt.Splat != 0;

            float t = 0f;
            float halfW = wStart, halfH = hStart;
            float af = aStart;
            uint cr = (uint)(long)cStart.X, cg = (uint)(long)cStart.Y, cb = (uint)(long)cStart.Z;
            if (life > 0f)
            {
                t = MathF.Min(1f, (byAge ? pt.Age : i) * rate);
                float it = 1f - t;
                if (lerpColor)
                {
                    cr = (uint)(long)(cEnd.X * t + cStart.X * it);
                    cg = (uint)(long)(cEnd.Y * t + cStart.Y * it);
                    cb = (uint)(long)(cEnd.Z * t + cStart.Z * it);
                }
                if (lerpAlpha) af = aStart * it + aEnd * t;
                if (lerpW) halfW = wStart * it + wEnd * t;
                if (lerpH) halfH = hStart * it + hEnd * t;
            }
            // An emitter that declared no Y size at all is SQUARE, not flat: the engine copies the X extent
            // across. Miss this and every single-size emitter -- which is most of them -- collapses to a
            // zero-height line.
            if (!lerpH && sy0 == 0f) halfH = halfW;
            // A landed splat freezes at the END size in both dimensions, and takes the X end for Y when no
            // Y end was declared.
            if (splat && pSplat) { halfW = wEnd; halfH = sy1 == 0f ? wEnd : hEnd; }

            uint ca = (uint)(long)af;
            uint col = (cr & 0xff) | ((cg & 0xff) << 8) | ((cb & 0xff) << 16) | ((ca & 0xff) << 24);
            uint colFar = col;

            Vector3 pos = pt.Pos;
            // The particle's OWN position, kept aside: the linked type reassigns `pos` to the next particle
            // in the chain, and taking the view direction from that would anchor the normal one segment
            // along the chain from the quad it shades.
            Vector3 centre = pos;
            Vector3 far = pos;
            Vector3 right, up;

            if (type == 6 || type == 3)
            {
                // The stretched pair. Type 6 (motion-blurred) runs from the particle back to where the
                // previous simulation step left it; type 3 (linked) runs from the NEXT particle in the array
                // back to this one -- so the near edge of a linked quad is the next particle, not this one,
                // and reading it the other way round shifts a lightning bolt one segment along itself.
                Vector3 d;
                if (type == 6) { far = pt.PrevPos; d = pos - far; }
                else
                {
                    var nxt = new Particle(parts[i + 1]);
                    if (nxt.IsNull) continue;
                    far = pos; pos = nxt.Pos; d = pos - far;
                }
                float s = Vector3.Dot(axisX, d), c = Vector3.Dot(axisY, d);
                float inv = Rsqrt(s * s + c * c);
                s *= inv; c *= inv;
                right = axisX * c - axisY * s;
                up = axisY * c + axisX * s;
                halfW *= 2f; halfH *= Half;
                // ...and on the motion-blurred type only, the far end fades with how far the streak is
                // stretched. The linked type carries the same colour end to end.
                if (type == 6)
                {
                    float stretch = inv * halfW;
                    float fa = stretch <= 1f ? stretch * af : af;
                    colFar = (col & 0x00ffffffu) | (((uint)(long)fa & 0xff) << 24);
                }
            }
            else if (type == 5)
            {
                Quat pq = pt.Quat;
                right = AxisX(pq.W, pq.X, pq.Y, pq.Z);
                up = AxisZ(pq.W, pq.X, pq.Y, pq.Z);
            }
            else
            {
                // The ordinary billboard: roll the quad about its normal, compose that onto the reference
                // orientation, and take the rotated X axis.
                // The two axes come from DIFFERENT flags and it matters: the ROLL follows the particle's own
                // splat state (a landed splat rolls about its own contact normal, against its own
                // quaternion), while the CROSS follows the emitter's, because a splat emitter's quads lie in
                // the ground plane whether or not this particular particle has landed yet.
                float roll = pt.Roll * Half;
                float sn = MathF.Sin(roll), cs = MathF.Cos(roll);
                Vector3 spinAxis = pSplat ? pt.SplatNormal : fwd;
                // Through Quat, not one Vector4 load: the quaternion sits at a 4-byte-aligned engine offset
                // and Vector4's 16-byte alignment is a fault waiting for the wrong allocation.
                Quat pq2 = pt.Quat;
                Vector4 spinOn = pSplat ? new Vector4(pq2.W, pq2.X, pq2.Y, pq2.Z) : q;
                Vector4 spin = QMul(new Vector4(cs, sn * spinAxis.X, sn * spinAxis.Y, sn * spinAxis.Z), spinOn);
                right = AxisX(spin.X, spin.Y, spin.Z, spin.W);
                // type 4 stands the quad up against world Z instead of against the camera
                up = type == 4 ? new Vector3(0f, 0f, flip)
                               : Vector3.Cross(splat ? pt.SplatNormal : fwd, right);
            }

            right *= halfW;
            up *= halfH;

            if (grid) Frame(pt, period, frameStart, frameEnd, du, dv, gx, gx * gy,
                            out u0, out u1, out v0, out v1);

            // #261: the record is WORLD space. Transforming the centre and the two axes is exactly
            // equivalent to transforming the four corners for an affine matrix -- and the vertex stage's
            // hemispherical normal, which used to be four CPU normalisations per particle, falls out of the
            // same two axes there. See RecFloats for the layout.
            if (!e.Ident)
            {
                pos = Vector3.Transform(pos, e.Model);
                far = Vector3.Transform(far, e.Model);
                centre = Vector3.Transform(centre, e.Model);
                right = Vector3.TransformNormal(right, e.Model);
                up = Vector3.TransformNormal(up, e.Model);
            }
            // Last frame's centre for THIS record slot, out of the mirror before the write overwrites it.
            ref ParticleRec rec = ref recs[written++];
            Put(ref rec, pos, col, far, colFar, right, soft, up, vizType, centre, u0, u1, v0, v1,
                PrevCentre(fresh, rec.Centre, centre));
        }
        return written;
    }

    /// <summary>One record, in the <see cref="RecFloats"/> layout. The two colours ride the <c>w</c> slots
    /// as raw bits, which is what <c>uintBitsToFloat</c> undoes in the stage.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Put(ref ParticleRec p, in Vector3 pos, uint col, in Vector3 far, uint colFar,
                            in Vector3 right, float soft, in Vector3 up, float type,
                            in Vector3 centre, float u0, float u1, float v0, float v1, in Vector3 prev)
    {
        p.Pos = pos; p.Col = BitConverter.UInt32BitsToSingle(col);
        p.Far = far; p.ColFar = BitConverter.UInt32BitsToSingle(colFar);
        p.Right = right; p.Soft = soft;
        p.Up = up; p.Type = type;
        p.Centre = centre; p.CentreW = 0f;
        p.U0 = u0; p.U1 = u1; p.V0 = v0; p.V1 = v1;
        p.Prev = prev; p.PrevW = 0f;
    }

    /// <summary>FNV-1a over a record block, eight bytes at a time. The delta's whole change detector: the
    /// engine simulates particles and tells nobody, so "did this emitter move" is a question about its own
    /// bytes and nothing else. It cannot match for an emitter whose billboard basis is the CAMERA's, since a
    /// rotation dirties the stored right and up -- measured at about 8KB a frame, and left that way.</summary>
    private static ulong Fnv(ReadOnlySpan<ParticleRec> recs)
    {
        ulong h = 14695981039346656037UL;
        foreach (ulong q in MemoryMarshal.Cast<ParticleRec, ulong>(recs)) { h ^= q; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>This material's <c>fEmissive</c>, or 1.0. Cold -- once per emitter per table slot. Both
    /// lists, shared first then instance: the .mtr's declared values are parsed onto the SHARED material's
    /// list while the instance list is merely sized to match, so reading the instance alone answers 1.0 for
    /// every effect this parameter exists for. ponytail: a DECLINED emitter draws without it.</summary>
    private static float ReadEmissive(CX.Engine.Material mat)
    {
        if (mat.IsNull) return 1f;
        float v = 1f;
        var mr = new MaterialRef(mat.Ptr);
        SharedRef shared = mr.Shared;
        if (!shared.IsNull) Scan(shared.Params, shared.ParamCount, ref v);
        Scan(mr.Params, mr.ParamCount, ref v);
        return v <= 0f ? 1f : MathF.Min(v, MaxEmissive);
    }

    private static void Scan(IntPtr list, int count, ref float v)
    {
        if (list == IntPtr.Zero || count <= 0 || count > MaxParams) return;
        var first = new MaterialParameter(list);
        for (int k = 0; k < count; k++)
        {
            MaterialParameter mp = first[k];
            if (mp.Type != 0) continue;                          // 1 = int-valued; this one is a float
            if (mp.Comps < 1) continue;
            if (AlloyRenderer.NameIs((byte*)mp.Ptr, EmissiveParam)) v = mp.Value;
        }
    }

    private static float Sat(float x) => x < 0f ? 0f : x > 1f ? 1f : x;

    /// <summary>Normalise, or hand back zero. A degenerate axis (a zero-size emitter, a quad whose up-vector
    /// came out parallel to its right) would otherwise put a NaN in the packed normal and, through the
    /// shading, in the fragment.</summary>
    private static Vector3 Norm(Vector3 v)
    {
        float len = v.LengthSquared();
        return len > 0f ? v * (1f / MathF.Sqrt(len)) : Vector3.Zero;
    }

    /// <summary>Guard on the motion normalisation: a zero-length delta would otherwise divide by zero and
    /// make every corner of the quad NaN, which takes the WHOLE emitter's draw with it rather than one
    /// particle. Zero instead collapses that one quad to a point -- degenerate, invisible, and harmless, and
    /// the engine's own <c>fast_rsqrt</c> path is degenerate at zero too.</summary>
    private static float Rsqrt(float x) => x > 0f ? 1f / MathF.Sqrt(x) : 0f;

    /// <summary>UV-grid animation, including the frame ADVANCE -- which is a WRITE back into the particle
    /// (<c>+0x44</c> frame index, <c>+0x48</c> the age it last stepped at). Owning the vertex build means
    /// owning that write: suppress the engine's draw without it and every animated particle freezes on
    /// whatever frame it was showing. Both wrap branches come straight off the engine's.</summary>
    private static void Frame(Particle pt, float period, float frameStart, float frameEnd,
                              float du, float dv, int gx, int cells,
                              out float u0, out float u1, out float v0, out float v1)
    {
        int idx = pt.Frame;
        float f = idx;
        if (period != 0f && frameStart != frameEnd)
        {
            float elapsed = pt.Age - pt.FrameTime;
            if (elapsed >= period)
            {
                if (frameStart <= frameEnd)
                {
                    idx += (int)(elapsed / period);
                    if (idx > frameEnd) idx = (int)frameStart;
                }
                else
                {
                    idx += (int)(-elapsed / period);
                    if (idx < frameEnd) idx = (int)frameStart;
                }
                pt.Frame = idx;
                pt.FrameTime = pt.Age;
                f = idx;
            }
        }
        if (Guard.V)
        {
            // CONTAINMENT, and the engine does not do it. The engine does not have to: it advances this
            // index every frame of every emitter's life, from the frame the particle is born. This pass
            // starts advancing an index it inherited mid-life, from a capture, after an area change or a
            // table reclaim -- so an index outside the frames the emitter actually declared is reachable
            // here and unreachable there, and the cell it lands on is some other effect's artwork.
            // The atlas bound always; the DECLARED range only when the emitter declared one. Equal ends
            // mean "this emitter does not animate", and its per-particle index is then a free choice the
            // model made -- a random frame per particle is a real authoring idiom. Clamping to the range
            // there would collapse every particle onto one cell, which is a different bug of my own making.
            float lo = 0f, hi = cells - 1;
            if (frameStart != frameEnd)
            {
                lo = MathF.Max(lo, MathF.Min(frameStart, frameEnd));
                hi = MathF.Min(hi, MathF.Max(frameStart, frameEnd));
            }
            if (hi < lo) hi = lo;
            f = f < lo ? lo : f > hi ? hi : f;
        }
        float row = MathF.Floor(f * du);
        u0 = (f - gx * row) * du;
        u1 = du + u0;
        v1 = 1f - row * dv;
        v0 = v1 - dv;
        if (Guard.V)
        {
            float iu = du * CellInset, iv = dv * CellInset;
            u0 += iu; u1 -= iu; v0 += iv; v1 -= iv;
        }
    }

    // ---- GL resources ---------------------------------------------------------------------------------

    private static bool EnsureProgram()
    {
        if (_gl.Prog != 0) return true;
        if (_gl.Failed) return false;
        _gl.Prog = GlProgram.Graphics(ShaderSource.Get("cx_part_v.glsl"),
            ShaderSource.Get("cx_part_f.glsl"), _log, "Particles");
        if (_gl.Prog == 0)
        { _gl.Failed = true; _log?.Info("Particles: program build failed -- no particles are drawn"); return false; }
        _gl.LocView = GlProgram.Loc(_gl.Prog, "uView");
        _gl.LocProj = GlProgram.Loc(_gl.Prog, "uProj");
        _gl.LocEye = GlProgram.Loc(_gl.Prog, "uEye");
        _gl.LocTex = GlProgram.Loc(_gl.Prog, "uTex");
        _gl.LocCut = GlProgram.Loc(_gl.Prog, "uAlphaCut");
        _gl.FogLoc = Programs.FeatLocs(_gl.Prog);
        _gl.LocAdditive = GlProgram.Loc(_gl.Prog, "uAdditive");
        _gl.LocSoftOn = GlProgram.Loc(_gl.Prog, "uSoftOn");
        _gl.LocDepthTex = GlProgram.Loc(_gl.Prog, "uSceneDepth");
        _gl.LocEmissive = GlProgram.Loc(_gl.Prog, "uEmissive");
        _gl.LocLit = GlProgram.Loc(_gl.Prog, "uLit");
        _gl.LocViz = GlProgram.Loc(_gl.Prog, "uViz");
        _gl.LSunTex = GlProgram.Loc(_gl.Prog, "uSunShadow"); _gl.LSunDynTex = GlProgram.Loc(_gl.Prog, "uSunDynShadow");
        _gl.LCubeTex = GlProgram.Loc(_gl.Prog, "uLocalShadow"); _gl.LCubeDynTex = GlProgram.Loc(_gl.Prog, "uLocalDynShadow");
        _gl.LCubeCount = GlProgram.Loc(_gl.Prog, "uLocalCubes");
        _gl.LSunMedia = GlProgram.Loc(_gl.Prog, "uSunMedia");
        _gl.LLocalMedia = GlProgram.Loc(_gl.Prog, "uLocalMedia");
        // The sampler UNITS, once, at link time rather than per pass. uSunShadow and uLocalShadow are a
        // sampler2DArrayShadow and a samplerCubeArrayShadow; leaving them at their default unit 0 puts three
        // different sampler types on one unit, which GL 4.6 makes an INVALID_OPERATION that discards the
        // whole draw, so `/particles lit 0` would otherwise mean "no particles at all" on a strict driver.
        Gl.UseProgram(_gl.Prog);
        Gl.Uniform1i(_gl.LSunTex, (int)AlloyRenderer.SunUnit);
        Gl.Uniform1i(_gl.LSunDynTex, (int)AlloyRenderer.SunDynUnit);
        Gl.Uniform1i(_gl.LCubeTex, (int)AlloyRenderer.CubeUnit);
        Gl.Uniform1i(_gl.LCubeDynTex, (int)AlloyRenderer.CubeDynUnit);
        BuildMotionProgram();
        return true;
    }

    /// <summary>The sampler this pass reads every particle diffuse through. Deliberately un-mipmapped:
    /// every emitter on screen animates through a UV ATLAS, and a mip level of an atlas averages neighbouring
    /// CELLS together -- so the frame a particle is meant to show gets blended with the frames beside it.
    /// LINEAR without mips also makes the texture's completeness irrelevant.</summary>
    private static bool EnsureSampler()
    {
        if (_gl.Samp != 0) return true;
        if (Gl.BindSampler == null) return false;
        _gl.Samp = Gl.LinearClampSampler();
        return _gl.Samp != 0;
    }

    /// <summary>Build the index-build/sort program. Not an overridable stage -- ADR 0012's table is the set a
    /// content author can restyle, and there is no look here to restyle. A driver whose loader never produced
    /// the compute entry points latches <see cref="PartGlState.CsFailed"/> and the CPU path stands in.</summary>
    private static bool EnsureSort()
    {
        if (_gl.CsProg != 0) return true;
        if (_gl.CsFailed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindBufferBase == null)
        { _gl.CsFailed = true; return false; }
        _gl.CsProg = GlProgram.Compute(ParticleCs, _log, "Particles index build + depth sort");
        if (_gl.CsProg == 0) { _gl.CsFailed = true; return false; }
        _gl.CsEye = GlProgram.Loc(_gl.CsProg, "uEye");
        _log?.Info($"Particles: #288 armed -- index build and depth sort in compute, "
                   + $"{SortLocal} threads a group, sort cap {SortCap} particles");
        return true;
    }

    /// <summary>May the compute pass reorder this group? The whole of the ordering rule, in one place
    /// so that <see cref="SelfCheck"/> pins the SAME predicate <see cref="Order"/> stages rather than a
    /// restatement of it that can drift away from it.</summary>
    private static bool WillSort(int cls, int parts) => cls == ClsBlend && parts > 1 && parts <= SortCap;

    /// <summary>Fill the draw-order index list, and order the blended groups back-to-front, on the GPU.
    /// False means the caller does it on the CPU instead, unsorted. The separability guarantee is structural:
    /// one workgroup owns one draw group, a group is single-class by construction, and the groups' index
    /// ranges are disjoint and issued in group order, so no dispatch can move a particle out of its class.</summary>
    private static bool Order(int groups, Vector3 eye)
    {
        if (!Sort.V || !EnsureSort()) return false;

        // Only the alpha-blended class gets ordered. Punch-through writes depth and discards, so it is
        // already order-independent; addition commutes with itself, which is exactly why the additive class
        // could be batched across the screen in the first place -- reordering it would cost a sort and buy
        // nothing.
        for (int gi = 0; gi < groups; gi++)
        {
            uint count = (uint)_gParts[gi];
            bool sort = WillSort(_gCls[gi], _gParts[gi]);
            if (_gCls[gi] == ClsBlend && count > SortCap) _sortSkipped++;
            if (sort) _sorted++;
            _grp[gi * 4] = (uint)_gFirst[gi];
            _grp[gi * 4 + 1] = (uint)_gCount[gi];
            _grp[gi * 4 + 2] = (uint)_gStart[gi];
            _grp[gi * 4 + 3] = sort ? count | SortFlag : count;
        }

        Stage(ref _gl.SpanBuf, ref _gl.SpanCap, _span, _drawN * 4, BindSpans);
        Stage(ref _gl.GrpBuf, ref _gl.GrpCap, _grp, groups * 4, BindGroups);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindRecs, _gl.RecBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIdx, _gl.IdxBuf);
        Gl.UseProgram(_gl.CsProg);
        Gl.Uniform3fv(_gl.CsEye, 1, (float*)&eye);
        Gl.DispatchCompute((uint)groups, 1, 1);
        // The vertex stage reads the index list as shader storage; BUFFER_UPDATE is for the other consumer,
        // which is a later frame's glBufferSubData onto the same buffer the moment /particles sort 0 is
        // typed. An incoherent shader write is not ordered against a client update without it.
        Gl.MemoryBarrier(Gl.SHADER_STORAGE_BARRIER_BIT | Gl.BUFFER_UPDATE_BARRIER_BIT);
        return true;
    }

    /// <summary>Upload one descriptor array, growing its store by doubling. Both are small enough that the
    /// whole array goes up every pass: the spans are three numbers an emitter and the groups four, against
    /// the four bytes a PARTICLE this replaced.</summary>
    private static void Stage(ref uint buf, ref int cap, uint[] src, int uints, uint binding)
    {
        if (buf == 0) { uint b; Gl.CreateBuffers(1, &b); buf = b; }
        if (cap < uints)
        {
            int want = cap > 0 ? cap : 256;
            while (want < uints) want *= 2;
            Gl.NamedBufferData(buf, (IntPtr)((long)want * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
            cap = want;
        }
        fixed (uint* p = src)
            Gl.NamedBufferSubData(buf, IntPtr.Zero, (IntPtr)((long)uints * sizeof(uint)), p);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, binding, buf);
    }

    /// <summary>The persistent record buffer and the per-frame index list. Growth is by doubling and a grow
    /// REBUILDS the GL store, so every block's hash is void -- <see cref="EnsureBlock"/> re-sends them, one
    /// emitter at a time, on the next frame each is drawn. There is no vertex buffer and no vertex format:
    /// the stage expands six vertices a particle, so the VAO is empty and exists only because GL wants one.</summary>
    private static bool EnsureBuffers(int parts)
    {
        if (parts <= 0 || parts > MaxRecs) { if (parts > MaxRecs) _overflow++; return false; }
        if (_gl.Vao == 0) { uint v; Gl.CreateVertexArrays(1, &v); _gl.Vao = v; }
        if (_idxCap < parts)
        {
            int want = _idxCap > 0 ? _idxCap : MinRecs;
            while (want < parts) want *= 2;
            if (_idx != null) NativeMemory.Free(_idx);
            _idx = (uint*)NativeMemory.Alloc((nuint)((long)want * sizeof(uint)));
            _idxCap = want;
            if (_gl.IdxBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _gl.IdxBuf = b; }
            Gl.NamedBufferData(_gl.IdxBuf, (IntPtr)((long)want * sizeof(uint)), null, Gl.DYNAMIC_DRAW);
        }
        // The record store is sized off the ALLOCATOR's high-water mark, not off this frame's particle
        // count: blocks are persistent, so an emitter that went off screen still owns its slice.
        if (_recsCap < _recTop + parts)
        {
            int want = _recsCap > 0 ? _recsCap : MinRecs;
            while (want < _recTop + parts && want < MaxRecs) want *= 2;
            if (want < _recTop + parts) { ResetBlocks(); return false; }
            ParticleRec* grown = (ParticleRec*)NativeMemory.Alloc((nuint)((long)want * sizeof(ParticleRec)));
            if (_recs != null) NativeMemory.Free(_recs);
            _recs = grown;
            _recsCap = want;
            if (_gl.RecBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _gl.RecBuf = b; }
            Gl.NamedBufferData(_gl.RecBuf,
                               (IntPtr)((long)want * sizeof(ParticleRec)), null, Gl.DYNAMIC_DRAW);
            // The new store holds nothing, so every block is stale by construction.
            for (int i = 0; i < _tabN; i++) _tab[i].Hash = 0;
        }
        return _gl.RecBuf != 0 && _gl.IdxBuf != 0;
    }

    /// <summary>Give this emitter a block big enough for <paramref name="count"/> records, bump-allocated and
    /// then left alone; reassignment voids the hash, which is what re-sends the block. ponytail: growing a
    /// block LEAKS the old one -- an emitter's particle count is declared in its model and effectively never
    /// grows -- and <see cref="ResetBlocks"/> is the backstop, at one frame of vanilla emitters.</summary>
    private static bool EnsureBlock(ref Emit e, int count)
    {
        if (count <= 0) return false;
        if (e.RecCap >= count) return true;
        // Unreachable in practice -- EnsureBuffers already grew the store to _recTop + this frame's whole
        // particle count before the loop, which bounds every bump below. A decline rather than a reset
        // because a reset here would rebase blocks the loop has already written into the index list.
        if (_recTop + count > _recsCap) { _declined++; _decFull++; return false; }
        e.RecBase = _recTop; e.RecCap = count; e.Hash = 0;
        _recTop += count;
        return true;
    }

    /// <summary>The block allocator's only recovery: hand every slice back at once. Every emitter re-bumps
    /// on its next draw, so the cost is bounded by one frame.</summary>
    private static void ResetBlocks()
    {
        _recTop = 0;
        _recResets++;
        for (int i = 0; i < _tabN; i++) { _tab[i].RecBase = 0; _tab[i].RecCap = 0; _tab[i].Hash = 0; }
    }

    /// <summary>Every captured identity is void: the emitters belong to a Scene whose unload freed them.
    /// The GL objects are deliberately untouched -- this fires on every area transition, and rebuilding a
    /// program per area would be a stall for nothing.</summary>
    internal static void Forget()
    {
        _tabN = 0; _orderN = 0; _orderGen = -1;
        // The table went, so every block it named went with it. Not a reset: nothing is left to re-bump.
        _recTop = 0;
    }

    /// <summary>The 4.6 context went away: every GL NAME this class holds belongs to it, so the owner is
    /// replaced and there is nothing to delete. Separate from <see cref="Forget"/> with teeth -- <c>EnsureProgram</c>
    /// returns true on <c>_gl.Prog != 0</c>, so a table-only reset would leave the draw failing silently while
    /// the suppression stamp (which keys on vertices STAGED) went on declining the engine's own draw.</summary>
    internal static void ContextLost()
    {
        Forget();
        _gl = new PartGlState();
        _motion = new PartMotionState();   // its texture names are that context's
        if (_recs != null) { NativeMemory.Free(_recs); _recs = null; }
        if (_idx != null) { NativeMemory.Free(_idx); _idx = null; }
        _recsCap = 0; _idxCap = 0;
    }

    /// <summary>One line for the <c>/particles</c> status and the arm-time log.</summary>
    internal static string Describe()
        => $"{(_off ? "UNAVAILABLE" : Enabled.V ? "on" : "off")} soft {(Soft.V ? 1 : 0)} lit {(Lit.V ? 1 : 0)} shadows {(Shadows.V ? 1 : 0)} " +
           $"viz {Viz.V} grid {(Grid.V ? 1 : 0)} guard {(Guard.V ? 1 : 0)} diag {(Diag.V ? 1 : 0)} " +
           $"sort {(Sort.V ? 1 : 0)}{(_gl.CsFailed ? " (NO COMPUTE -- CPU index build)" : "")} " +
           $"(batch {(Batch.Boot ? 1 : 0)}, sampler {(Sampler.Boot ? "owned" : "inherited")} -- both restart)";

    internal static void ResetPeriod()
    {
        _captured = 0; _declined = 0; _emitters = 0; _groups = 0; _suppressed = 0;
        _decFade = 0; _decEmpty = 0; _decShader = 0; _decTex = 0; _decFull = 0;
        _reclaimed = 0; _overflow = 0; _softPasses = 0; _emissive = 0; _litGroups = 0;
        for (int i = 0; i < 7; i++) _cType[i] = 0;
        for (int i = 0; i < ClsCount; i++) _cCls[i] = 0;
        _cSplat = 0; _cTinted = 0; _cGrid = 0;
        _blockUploads = 0; _blockSkips = 0; _recResets = 0; _sorted = 0; _sortSkipped = 0;
        _dbNoTint = 0;
    }

    /// <summary>AC 4's number: emitters owned against draws issued. Vanilla's ratio is 1:1 by construction --
    /// it depth-sorts emitters individually and cannot merge two -- so anything above 1.0 here is a
    /// draw-call reduction the engine could not have made.</summary>
    internal static string Stats(int frames) => _off ? "no particles (Alloy pass unavailable)"
        : !Enabled.V ? "no particles (/particles enabled 0)"
        : $"particles {_tabN} emitters ({_captured} captured), {_emitters} owned in {_groups} draws"
          + (_emitters == 0 ? "" : $" ({(float)_emitters / MathF.Max(1, _groups):F1}x batched)")
          + $", {_suppressed} engine draws suppressed"
          + (_declined == 0 ? "" : $", {_declined} declined ({_decFade} fading, {_decEmpty} empty, " +
                                    $"{_decShader} custom shader, {_decTex} no texture, {_decFull} table full)")
          + (!Soft.V ? ", soft OFF"
                    : $", {_softPasses} passes softened" + (SceneDepth.Ready || _softPasses > 0 ? "" : " (NO DEPTH COPY)"))
          + (!Lit.V ? ", unlit" : $", {_litGroups} lit draws")
          + (_emissive == 0 ? "" : $", {_emissive} emissive")
          + (_reclaimed == 0 ? "" : $", {_reclaimed} table slots reclaimed from unwalked emitters")
          // Per FRAME, like everything around it: as a period total this read "1927/1927 record blocks
          // re-sent" for what is two blocks and 8KB a frame, which is an alarm where there is no bus traffic.
          + $", {(float)_blockUploads / MathF.Max(1, frames):F1} of {(float)(_blockUploads + _blockSkips) / MathF.Max(1, frames):F1} record blocks re-sent per frame"
          + (!Sort.V ? ", index built on the CPU, unsorted (/particles sort 0)"
             : _gl.CsFailed ? ", index built on the CPU, unsorted (NO COMPUTE)"
             : $", {_sorted} groups depth-sorted in compute"
               + (_sortSkipped == 0 ? "" : $", {_sortSkipped} sort-skipped (over {SortCap})"))
          + (_recResets == 0 ? "" : $", {_recResets} BLOCK-ALLOCATOR RESETS (see EnsureBlock)")
          + (_overflow == 0 ? "" : $", {_overflow} passes OVERFLOWED (group or record cap)")
          + (_dbNoTint == 0 ? "" : $", {_dbNoTint} tinted emitters drawn UNTINTED (DB mode has no engine light list)")
          + Census();

    /// <summary><c>/particles diag 1</c> only: which of the seven billboard bases and which blend classes this period
    /// actually exercised. A zero against a render type is the useful reading -- it says that base is
    /// UNTESTED, not that it works.</summary>
    private static string Census()
    {
        if (!Diag.V) return "";
        var sb = new System.Text.StringBuilder(" [census");
        for (int i = 0; i < 7; i++) sb.Append($" {TypeNames[i]}={_cType[i]}");
        sb.Append($" | punch={_cCls[ClsPunch]} blended={_cCls[ClsBlend]} additive={_cCls[ClsAdd]}");
        sb.Append($" | splat={_cSplat} tinted={_cTinted} uvgrid={_cGrid}]");
        return sb.ToString();
    }

    /// <summary>The compute stage's bitonic network, transcribed. It exists ONLY for
    /// <see cref="SelfCheck"/> -- the shader is the one that runs -- and it is written line for line against
    /// <see cref="ParticleCs"/> so that a change to one and not the other shows up as a failing check
    /// rather than as reordered smoke on somebody's screen.</summary>
    private static void SortModel(float[] key, uint[] val)
    {
        int count = key.Length;
        int n = 1;
        while (n < count) n <<= 1;
        var k = new float[n];
        var v = new uint[n];
        for (int i = 0; i < n; i++) { k[i] = i < count ? key[i] : 3.0e38f; v[i] = i < count ? val[i] : 0u; }
        for (int step = 2; step <= n; step <<= 1)
            for (int j = step >> 1; j > 0; j >>= 1)
                for (int i = 0; i < n; i++)
                {
                    int l = i ^ j;
                    if (l > i && (k[i] > k[l]) == ((i & step) == 0))
                    { (k[i], k[l]) = (k[l], k[i]); (v[i], v[l]) = (v[l], v[i]); }
                }
        for (int i = 0; i < count; i++) { key[i] = k[i]; val[i] = v[i]; }
    }

    // ---- shaders ---------------------------------------------------------------------------------------

    private static string ParticleCs => ShaderSource.Get("cx_part_c.glsl");

    /// <summary>The vertex stage, attributeless: six vertices a particle expanded from <c>gl_VertexID</c>
    /// against the persistent record buffer, through the draw-order index list, with the CPU build's own
    /// winding -- (A, B, C) then (C, B, D), near centre then far, so a motion-blurred streak fades along its
    /// length rather than across it. The light rig runs HERE, per vertex, not per fragment.</summary>
    private static string ParticleVsHead => ShaderSource.Get("cx_part_vhead.glsl");

    /// <summary>The vertex main, composed after <see cref="AlloyLights"/>'s three libraries.</summary>
    private static string ParticleVsMain => ShaderSource.Get("cx_part_vmain.glsl");

    /// <summary>The fragment stage, ported from the client's <c>fsparticle</c>: modulate, discard, soft fade,
    /// fog. The fog split is the stock one -- an additive fragment cannot be lerped towards the fog colour,
    /// so it loses alpha instead -- and the soft fade sits where the stock stage puts it, after the discard,
    /// which is why punch-through needs no special case. The stage writes LINEAR; the resolve encodes.</summary>
    internal static string ParticleFsHead => ShaderSource.Get("cx_part_fhead.glsl");

    /// <summary>The body, composed after <see cref="AlloyLights"/>'s three libraries.</summary>
    private static string ParticleFsMain => ShaderSource.Get("cx_part_fmain.glsl");
}
