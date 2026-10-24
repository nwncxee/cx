using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CX.Framework;

/// <summary>Where the frame goes on the CPU, end to end: a fixed set of accumulators, one <c>Stopwatch</c> read per
/// bracket, and a residual of frame time minus everything claimed. <see cref="Zone.Nested"/> splits the enum --
/// before it, mutually exclusive spans the residual subtracts; after it, breakdowns INSIDE one of those, each parent
/// passing the child's ticks as <c>minus</c>. The period line also names the WORST frame. Hot: no allocation.</summary>
public static class FrameZones
{
    /// <summary>The bracket set. Order matters: everything before <see cref="Nested"/> is a top-level,
    /// non-overlapping span of the frame and counts against the residual.</summary>
    public enum Zone
    {
        /// <summary>Alloy's ownership scan (<c>AlloyRenderer.Enroll</c>), every <c>EnrollEvery</c> passes.</summary>
        Enroll,
        /// <summary>The ADR 0007 pass slot: light walk, shadow focus, and the world-caster bake.</summary>
        Shadow,
        /// <summary>The RSP shim's own work -- the bookkeeping vcalls RenderSinglePass braided into its
        /// drawing, with the Alloy bracket that runs inside its anchor subtracted out.</summary>
        Shim,
        /// <summary>The Alloy colour frame (<c>DrawOwned</c>), CPU submission side.</summary>
        Alloy,
        /// <summary>The UI window roll-call.</summary>
        Ui,
        /// <summary>The present itself. Large here means GPU- or vsync-bound, which is a different problem
        /// from every other zone on this list.</summary>
        Present,
        /// <summary>The ENGINE's frame, attributed: the four top-level steps of
        /// <c>CClientExoAppInternal::MainLoop</c> @0x658250 that are not the 3D render -- network receive,
        /// resource manager, client AI update, 2D GUI. What <c>other</c> still holds is input, sound and the
        /// camera. The scene render runs FROM the AI update, so <c>Ai</c> is minus every renderer bracket in it.</summary>
        Net, Res, Ai, Gui,
        /// <summary>The frame boundary (<c>SeamDraw.OnBoundary</c>) minus every TOP-LEVEL bracket it contains (the
        /// subtraction is <see cref="TopLevelTicks"/> over THIS row, so it is all of them). On the game thread it
        /// enters <see cref="Shadow"/> and <see cref="Alloy"/> through the world view's pass chain (single-threaded
        /// arm only) and <see cref="Ui"/> through the roll-call. Everything else it does is INSIDE this number.</summary>
        Seam,

        /// <summary>Split marker, not a bracket.</summary>
        Nested,

        /// <summary>Nested in <see cref="Alloy"/>: revalidate + route + stage every owned part.</summary>
        Build,
        /// <summary>Nested in <see cref="Build"/>: the routing walk (identity, live alpha, tile fade,
        /// blended sort) -- the half an incremental build keeps O(world).</summary>
        Scan,
        /// <summary>Nested in <see cref="Build"/>: the staging walk (records, textures, draw args, batch cuts)
        /// -- the half that should stay proportional to the changed set.</summary>
        Restage,
        /// <summary>Inside <see cref="Restage"/> (NOT subtracted): the two spans of the staging walk holding its
        /// engine-pointer derefs, plus the owned bookkeeping interleaved with them. An UPPER BOUND on the unmovable
        /// half, so Restage minus Chase is a lower bound on what a GPU pass could take. SAMPLED
        /// 1-in-<c>AlloyRenderer.ChaseEvery</c> parts and extrapolated: an estimate, not a bracket sum.</summary>
        Chase,
        /// <summary>Inside <see cref="Scan"/> (NOT subtracted): the route walk's O(world) remainder -- the
        /// vacated-slot and frustum tests on every slot the gate REJECTS, i.e. the cost of visiting the off-screen
        /// world at all. SAMPLED 1-in-<c>AlloyRenderer.ChaseEvery</c> slots and extrapolated.</summary>
        Route,
        /// <summary>Inside <see cref="Scan"/> (NOT subtracted), on the slots the gate ACCEPTS: the identity
        /// and LOD chase (Ident) and the tile-fade, alpha and dissolve chase (Fade). SAMPLED
        /// 1-in-<c>AlloyRenderer.ChaseEvery</c> and extrapolated -- which of the route's per-part reads
        /// carries Scan.</summary>
        Ident, Fade,
        /// <summary>Inside <see cref="Prepass"/> (NOT subtracted): the hi-Z pyramid build's CPU side --
        /// the prepass zone read 0,6ms at a render scale where its GPU work was nothing.</summary>
        Hiz,
        /// <summary>Inside <see cref="Ui"/> (NOT subtracted): the nameplate window's draw.</summary>
        Plates,
        /// <summary>Inside <see cref="Prepass"/> and <see cref="Passes"/> (NOT subtracted): the grass pass CPU
        /// -- the bin-bucket walk and instance staging in Prepare, and both draws.</summary>
        Grass,
        /// <summary>Nested in <see cref="Shadow"/>: the LightManager master-list walk.</summary>
        Lwalk,
        /// <summary>Nested in <see cref="Shadow"/>: <c>CasterPass.OnPassSlot</c> -- cube ranking,
        /// mover scans and the view bakes. Net of the <see cref="Pose"/> the bake runs inside itself (SL3);
        /// every other zone nested in it is reported without being subtracted.</summary>
        Bake,
        /// <summary>Nested in <see cref="Alloy"/>: the light walk upload + cluster build.</summary>
        Lights,
        /// <summary>Nested in <see cref="Alloy"/>: the colour-pass loop's own CPU work, minus the
        /// nested Issue/Cull/Post/Probe brackets inside it -- prepass, hi-Z, grass, water, beams,
        /// particles, state churn. Added because the Alloy zone measured ~6ms MORE than its children
        /// claimed, and an unclaimed tower cannot be cut.</summary>
        Passes,
        /// <summary>Nested in <see cref="Alloy"/>: the post chain -- Begin (target setup) plus
        /// bloom/composite/highlight at the PostProcess slot.</summary>
        Post,
        /// <summary>Nested in <see cref="Passes"/>: the depth prepass submission loop + the hi-Z
        /// pyramid build.</summary>
        Prepass,
        /// <summary>Nested in <see cref="Passes"/>: the full-screen copies -- the water snapshot and
        /// the soft-particle depth take. Small numbers here are honest; a large one is a GPU sync stall
        /// wearing a memcpy's name.</summary>
        Blit,
        /// <summary>Nested in <see cref="Passes"/>: the particle pass CPU (build + upload + submit),
        /// minus its Blit child.</summary>
        Parts,
        /// <summary>Nested in <see cref="Alloy"/>: the wholesale per-draw SSBO / handle / bone uploads.</summary>
        Upload,
        /// <summary>Nested in <see cref="Alloy"/>: the multidraw submission itself.</summary>
        Issue,
        /// <summary>Nested in <see cref="Alloy"/>: force-solving every owned skinmesh pose through the engine.</summary>
        Pose,
        /// <summary>Nested in <see cref="Alloy"/>: owned-texture/buffer drains and re-copies.</summary>
        Own,
        /// <summary>Nested in <see cref="Alloy"/>: the six-face IBL probe capture.</summary>
        Probe,
        /// <summary>Nested in <see cref="Alloy"/>: the pass preamble ahead of the 4.6 bracket (target latch,
        /// post precheck, present target, camera), minus its <see cref="Pose"/> child.</summary>
        Setup,
        /// <summary>Nested in <see cref="Alloy"/>: making the 4.6 context current.</summary>
        Enter,
        /// <summary>Nested in <see cref="Alloy"/>: program ensure, target bind, GL state, water program.</summary>
        Prep,
        /// <summary>Nested in <see cref="Alloy"/>: GPU frame start, cycle textures, the LOD/chart arena pumps.</summary>
        Pump,
        /// <summary>Nested in <see cref="Alloy"/>: staging the cull's source list and the two GPU dispatches over
        /// it. CPU-side only -- what the dispatches cost is GPU time, and shows up in <see cref="Present"/> or
        /// nowhere at all.</summary>
        Cull,
        /// <summary>Nested in <see cref="Ai"/> (reported for shape, not subtracted): the engine texture upload
        /// funnel -- the engine's own build plus Alloy's owned copy and BCn transcode, first-sight work that lands
        /// inside the engine's step. The UPLOAD term of the read / decode / upload split, whose other two are
        /// <see cref="TexRead"/> and <see cref="TexDecode"/>; no second bracket spans the same call.</summary>
        Tex,
        /// <summary>Nested in <see cref="Ai"/>: <c>CExoResMan::Demand</c>, the engine's on-demand resource
        /// load (models, textures, animations on first sight). Outermost call only.</summary>
        Demand,
        /// <summary><see cref="Demand"/> split by the outermost resource's restype -- texture
        /// read vs model parse. <c>DemOther</c> is every restype not named.</summary>
        DemDds, DemTga, DemPlt, DemMdl, DemOther,
        /// <summary><c>AurGetImageWrapper</c>, one texture's read + decode. Minus the texture
        /// demands above it is the decode alone.</summary>
        Image,
        /// <summary>A texture build's READ -- the bytes off disk, hak or NWSync with nothing
        /// decoded. Two brackets, neither inside the other: <c>CExoResMan::Demand</c> on a DDS/TGA/PLT
        /// resource minus the <see cref="TexDecode"/> it contains (a TGA is decoded inside its own demand),
        /// and the engine's compressed-image getter under <see cref="Alloy.DdsReader"/>.</summary>
        TexRead,
        /// <summary>A texture build's DECODE -- <c>CResTGA::OnResourceServiced</c>, the PLT
        /// composite, the DDS re-parse and pad, and the forced-uncompressed BCn unpack. Alloy's and the
        /// engine's alike, whichever ran.</summary>
        TexDecode,
        /// <summary>Nested in <see cref="Ai"/>, world scene only: <c>ManageSceneBSP</c> and
        /// <c>Scene::AddPartsToDrawBuckets</c> -- the engine's own render preparation for a 3D frame Alloy
        /// draws. Their sum against Ai is the ceiling on bypassing it (engine-scene-walk-bypass.md).</summary>
        Bsp, Buckets,
        /// <summary>The BCn transcode's main-thread work: the compressed uploads (mips and encode are on their own
        /// thread, fed straight from the upload funnel). Runs on the Enroll slot inside Ai and is subtracted from
        /// BOTH, so it reads on its own.</summary>
        Bcn,
        /// <summary>Inside <see cref="Bcn"/>: the compressed uploads alone.</summary>
        BcnUp,
        /// <summary>The lightmap bake's frame step (adopt, pack, the light steps, the readback), inside
        /// <see cref="Passes"/> and subtracted from it.</summary>
        Lightmap,
        /// <summary>Nested in <see cref="Passes"/>: the stream ring's region fence (<c>GpuRing.Acquire</c>) --
        /// the CPU waiting for the GPU to drain the frame before last. Large here means GPU-bound.</summary>
        Wait,
        /// <summary>Hit testing against CX's scene -- every <c>HitTest.Cast</c> the frame ran, hover pick
        /// included. Nested in <see cref="Ai"/> (which issues the picks) and NOT subtracted, so it reads as what
        /// picking cost on its own. The engine's bill for the same work is invisible to every bracket here, so a
        /// vanilla comparison is a live A/B on <c>counthitcheckscenebspnode</c>, not a zone difference.</summary>
        Pick,
        /// <summary>Nested in <see cref="DbFold"/> (so in <see cref="Seam"/>) -- <c>DbFeed.FoldFrame</c>: the three
        /// pumps plus the fold of the scene database's dirty lists into the renderer's records. The record fold
        /// reads about 0 in a still scene, so a non-zero number with nothing moving is the PUMPS, which
        /// <see cref="ArenaPump"/>, <see cref="ChartDrip"/> and <see cref="RecipePump"/> name.</summary>
        DbDirty,
        /// <summary>Inside <see cref="DbDirty"/> (NOT subtracted): <c>MeshArena.Pump</c> -- collecting whatever
        /// the mesh workers finished.</summary>
        ArenaPump,
        /// <summary>Inside <see cref="DbDirty"/> (NOT subtracted): <c>MeshChart.DripDb</c>, the chart offer drip.</summary>
        ChartDrip,
        /// <summary>Inside <see cref="DbDirty"/> (NOT subtracted): <c>DrawRecipe.Pump</c> plus
        /// <c>DrawRecipe.Reclassify</c> -- the recipe intern drain and the coverage re-ask.</summary>
        RecipePump,
        /// <summary>Nested in <see cref="Seam"/> -- <c>RenderSnapshot.Publish</c> net of the
        /// <see cref="DbFold"/> it invokes. What is left is the dirty-count copy, the four <c>TakeDirty</c>
        /// calls and the handshake wait for a frame the renderer still holds.</summary>
        Publish,
        /// <summary>Nested in <see cref="Publish"/> and subtracted from it -- the whole fold the
        /// publisher drives on the game thread: <see cref="Settle"/>, <c>DbFeed.FoldFrame</c>
        /// (<see cref="DbDirty"/>), the trigger-highlight enrolment and the engine reads hoisted into the
        /// fold (<c>SceneFeed.FoldWind</c>). Its own number, minus those children, is those last two.</summary>
        DbFold,
        /// <summary>Nested in <see cref="DbFold"/> -- <c>DbAnim.Settle</c>, the boundary's last
        /// compose, net of the <see cref="HitFold"/> it runs at its end.</summary>
        Settle,
        /// <summary>Nested in <see cref="Settle"/> and subtracted from it -- <c>HitFeed.Fold</c> at the
        /// FRAME BOUNDARY (the hit bounds and the hit index re-derived from the frame's change set).</summary>
        HitFold,
        /// <summary>Nested in <see cref="Ai"/> (NOT subtracted) -- the OTHER <c>HitFeed.Fold</c> site,
        /// the once-per-frame fold at the end of <c>DbAnim.Animate</c>. Separate from <see cref="HitFold"/>
        /// because the two sit in different zones and only one of them is the boundary's.</summary>
        HitFoldAi,
        /// <summary>Nested in <see cref="Seam"/> -- <c>DbGui.Draw</c> summed over the frame's GUI panel
        /// views (the member walk, the streaming prescan and the quad issue).</summary>
        DbGuiDraw,
        /// <summary>Nested in <see cref="Seam"/> -- <c>GuiCapture.Lead</c> + <c>GuiPass.FlushWithWindows</c>:
        /// issuing the frame's captured GUI layers and CX's recorded windows after the last view.</summary>
        GuiFlush,
        /// <summary>Nested in <see cref="Alloy"/> (NOT subtracted) -- <c>DbKeyhole.Write</c>, the
        /// dissolve column over the pass's draw order.</summary>
        Keyhole,
        /// <summary>Nested in <see cref="Ui"/>'s caller and NOT in <see cref="Ui"/> itself -- what the window roll-call's
        /// record excludes: the session gate, the UI-scale sync and font re-bake, the viewport sample, and with Alloy
        /// off the engine-context replay and state hand-back.</summary>
        UiEdge,
        /// <summary>Nested in <see cref="Alloy"/> (NOT subtracted) -- <c>EmitterSim.StepDb</c>, the whole owned
        /// particle walk: the registry walk, the cull, the per-emitter step, the draw sort and the enrolment. Worth
        /// its own zone because a still scene once spent ~3,5 ms of a 17,5 ms frame here, inside the Alloy tower.</summary>
        Emit,
        /// <summary>Nested in <see cref="Alloy"/> -- one view's visible-set query over the database index
        /// (<c>DbFeed.Visible</c>). The one cost a still frame still pays.</summary>
        DbVis,
        /// <summary>Nested in <see cref="Alloy"/> -- the draw order and the area index rebuilt after a slot-set
        /// edge. Reads 0 on a frame in which nothing was created or destroyed.</summary>
        DbOrder,
        /// <summary>Nested in <see cref="Ai"/> -- <c>DbAnim.Animate</c> summed over the frame's scene animates and
        /// its out-of-band ones (member walk, queue advance, pose, dangly solve, part walk, compose), net of the
        /// <see cref="HitFoldAi"/> it runs at its end. The <c>Scene::Animate</c> slot's whole cost, which
        /// <see cref="Ai"/> otherwise reports mixed in with the SP server's own AI step.</summary>
        Anim,

        Count,
    }

    private const int N = (int)Zone.Count;

    /// <summary>Accumulator ROWS, one per thread that brackets independently -- the game thread and, with
    /// <c>alloy.renderthread=1</c>, Alloy's render thread; every array is row-major (<c>row * N + zone</c>) and
    /// <see cref="Add"/> credits the CALLING thread. One shared set would make the residual read negative, so row 0
    /// keeps it and row 1 is the render thread's own wall. Each cell is one aligned <c>long</c>, never torn.</summary>
    public const int Rows = 2;
    /// <summary>The game thread's row -- the default for every thread, so a thread that never claims a row of
    /// its own is attributed here and nothing goes missing.</summary>
    public const int RowGame = 0;
    /// <summary>Alloy's render thread's row.</summary>
    public const int RowRender = 1;

    /// <summary>Which row this thread's brackets land in. Thread-static and zero by default, so the game
    /// thread needs no call and <see cref="Add"/> stays two array writes.</summary>
    [ThreadStatic] private static int _row;

    /// <summary>Attribute this thread's brackets to <paramref name="row"/> from now on. Called once, by the
    /// thread itself, as the first thing it does -- a row claimed from another thread would set that
    /// thread's.</summary>
    public static void UseRow(int row) => _row = (uint)row < Rows ? row : RowGame;

    /// <summary>The row the calling thread accumulates into.</summary>
    public static int CurrentRow => _row;

    private static readonly long[] _frame = new long[Rows * N];    // this frame's ticks, per row per zone
    private static readonly long[] _sum = new long[Rows * N];      // the period's
    private static readonly long[] _worst = new long[Rows * N];    // ...and the worst single frame's, snapshotted
    private static readonly long[] _worstDrew = new long[Rows * N]; // ...and the worst that reached the scene render
    private static long _dtSum, _dtWorst, _dtWorstDrew;
    private static int _frames;

    /// <summary>The HITCH LEDGER: frames past the 60Hz budget but short of a load stall -- the stutter a player
    /// feels while turning or walking -- summed separately, so the hitch row minus the average row names what a
    /// hitch spends on. Beside it, per-frame EVENT counts averaged over all frames and over hitch frames alone: an
    /// event elevated in the hitch column is the structural suspect. CPU-side only; a GPU hitch reads as Present.</summary>
    public enum Ev
    {
        /// <summary>Casters admitted for the first time (record landed, or back from off-list).</summary>
        Appeared,
        /// <summary>Static cube slots re-baked (six faces of the still world each).</summary>
        CubeStatic,
        /// <summary>Dynamic cube pool pages baked.</summary>
        CubeDyn,
        /// <summary>Sun cascade static layers re-baked.</summary>
        SunStatic,
        /// <summary>Sun cascade fit centres re-latched (a focus drift past the slack -- the camera turned).</summary>
        SunLatch,
        /// <summary>Stage parts the workers deferred to the main thread (a handle to mint, a VAO to wait on).</summary>
        Deferred,
        /// <summary>Enrolment walk steps.</summary>
        Enroll,
        /// <summary>Bindless texture handles minted (GetTextureHandle + MakeResident).</summary>
        Mint,
        /// <summary>Standing caster list re-collected.</summary>
        Collect,
        /// <summary>Textures through the engine upload funnel (<see cref="Zone.Tex"/> is their time).</summary>
        TexUpload,
        /// <summary>Outermost engine resource demands (<see cref="Zone.Demand"/> is their time).</summary>
        Demands,
        /// <summary>Of <see cref="Demands"/>, the DDS / TGA / PLT / MDL ones.</summary>
        DdsDemands, TgaDemands, PltDemands, MdlDemands,
        /// <summary>Textures transcoded by the drip (<see cref="Zone.Bcn"/> is their time).</summary>
        Transcode,
        /// <summary>ADR 0023: engine texture builds answered from the cache (no read, decode or upload).</summary>
        TexWarm,
        /// <summary>ADR 0023: cached textures evicted for the budget.</summary>
        TexEvict,
        /// <summary>Casters whose dynamic-set membership flipped (each one invalidates the static cube it stands in).</summary>
        DynFlip,
        /// <summary>Static cube slots held back by <c>/shadow staticcap</c> this frame.</summary>
        CubeHeld,
        /// <summary>Why the standing caster list was re-collected: a CasterGen bump (a cast-admission flip in the
        /// stage, a delta re-seat), a record-epoch bump, a skipped bracket, or a Refresh that found a moved bone slot.</summary>
        CollectGen, CollectEpoch, CollectSkip, CollectRefresh,
        /// <summary>The route's visible set was rebuilt (cadence, enrolment generation, epoch or a camera move).</summary>
        VisRebuild,
        Count,
    }
    private const int NE = (int)Ev.Count;
    private static readonly int[] _ev = new int[NE];
    private static readonly long[] _evSum = new long[NE], _evHitch = new long[NE];
    private static readonly long[] _hitch = new long[Rows * N];
    private static long _dtHitch, _dtNoStall;
    /// <summary>GPU span per frame, attributed by age: the stamp result for a frame arrives some frames later,
    /// so a ring of hitch verdicts pairs it with the frame it measured. ponytail: a declined stamp slips the
    /// pairing by a frame; the columns are a discriminator (GPU-bound hitch or not), not a per-frame ledger.</summary>
    private static readonly bool[] _hitchRing = new bool[16];
    private static double _gpuSum, _gpuHitch;
    private static int _gpuN, _gpuHitchN;
    public static void Gpu(double ms, int framesAgo)
    {
        _gpuSum += ms; _gpuN++;
        if (_hitchRing[(_frames - framesAgo) & 15]) { _gpuHitch += ms; _gpuHitchN++; }
    }
    private static int _hitchN, _framesNoStall;
    /// <summary>A hitch is a rendered frame longer than <see cref="HitchFactor"/> times the PREVIOUS period's
    /// mean frame (bootstrapped at the 60Hz budget) and short of a load stall. Relative on purpose: a plaza at
    /// 10ms dips to 14ms and a fixed 16.7 would report no hitch at all, and a weaker PC needs no retuning.</summary>
    public const float HitchFactor = 1.5f, StallMs = 100f;
    private static float _hitchMs = 16.7f;

    /// <summary>Count an event this frame. Hot: one array add.</summary>
    public static void Count(Ev e, int n = 1) => _ev[(int)e] += n;

    /// <summary>Ticks this zone has accumulated so far THIS frame ON THIS THREAD -- the value a parent
    /// bracket samples before and after its child so it can subtract it. Per row, because a parent must
    /// subtract the child IT contains and not one another thread opened at the same moment.</summary>
    public static long Ticks(Zone z) => _frame[_row * N + (int)z];

    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>Every top-level zone's ticks this frame ON THIS THREAD, for a bracket that must subtract the
    /// ones it contains.</summary>
    public static long TopLevelTicks()
    {
        long s = 0;
        int b = _row * N;
        for (int i = 0; i < (int)Zone.Nested; i++) s += _frame[b + i];
        return s;
    }

    /// <summary>Close a bracket opened at <paramref name="t0"/>, optionally minus ticks a nested bracket already
    /// claimed. Never accumulates negative: a child measured across a boundary this parent did not span would
    /// otherwise credit the parent time it did not spend.</summary>
    public static void Add(Zone z, long t0, long minus = 0)
    {
        long dt = Stopwatch.GetTimestamp() - t0 - minus;
        if (dt > 0) _frame[_row * N + (int)z] += dt;
    }

    /// <summary>Close a bracket taken on 1-in-<paramref name="every"/> iterations of a walk, credited times
    /// <paramref name="every"/>. Timestamping every part of a 14k walk would cost more than the half being
    /// measured, so the sample is the instrument.</summary>
    public static void AddSampled(Zone z, long t0, int every)
    {
        long dt = Stopwatch.GetTimestamp() - t0;
        if (dt > 0) _frame[_row * N + (int)z] += dt * every;
    }

    /// <summary>Drop this frame's brackets without folding them into the period -- the loading-screen and
    /// main-menu case. Without it the first in-module frame after a menu would carry the menu's ticks.
    /// Every row: a discarded frame discards the renderer's half too.</summary>
    public static void Discard() { Array.Clear(_frame, 0, Rows * N); ReadSched(); Array.Copy(_scNow, _scLast, (int)Sc.Count); }

    /// <summary>Did this frame reach the scene render at all? A frame with every renderer zone at zero spent
    /// its time somewhere no bracket here can see -- in practice engine asset loading.
    /// <para>Every row, not the game thread's: with a render thread live the renderer's zones are row 1, and
    /// asking row 0 alone would call every drawn frame a stall.</para></summary>
    private static bool Drew()
    {
        for (int r = 0; r < Rows; r++)
            for (int i = 0; i < (int)Zone.Nested; i++)
                if (i != (int)Zone.Ui && i != (int)Zone.Present && i < (int)Zone.Net && _frame[r * N + i] != 0)
                    return true;
        return false;
    }

    /// <summary>The SCHEDULER column: what the main thread got from the kernel this frame, which separates a slow
    /// thread from a slow zone. cpu ≈ wall with no switches = the core itself was slow (SMT sibling, frequency,
    /// cache); cpu &lt;&lt; wall with involuntary switches = preempted; cpu &lt;&lt; wall with voluntary switches =
    /// blocked in a syscall. One <c>getrusage(RUSAGE_THREAD)</c> per frame, no allocation.</summary>
    [DllImport("libc.so.6", EntryPoint = "getrusage")] private static extern unsafe int GetRUsage(int who, byte* ru);
    private const int RusageThread = 1;
    /// <summary>x86_64 glibc <c>struct rusage</c>: utime/stime timevals at 0/16, minflt 64, majflt 72, nvcsw 128, nivcsw 136.</summary>
    private enum Sc { Cpu, MinFlt, MajFlt, Vol, Invol, Sys, Wait, Count }
    [DllImport("libc.so.6", EntryPoint = "open")] private static extern unsafe int Open(byte* path, int flags);
    [DllImport("libc.so.6", EntryPoint = "pread")] private static extern unsafe nint Pread(int fd, byte* buf, nint n, long off);
    /// <summary>/proc/thread-self/schedstat, opened once by the main thread (the path binds to the OPENER): run ns,
    /// RUNQUEUE WAIT ns, timeslices. The wait is the preemption's size; rusage only counts its occurrences.</summary>
    private static int _schedFd = -1;
    private static readonly long[] _scLast = new long[(int)Sc.Count], _scNow = new long[(int)Sc.Count];
    private static readonly long[] _scAll = new long[(int)Sc.Count], _scHitch = new long[(int)Sc.Count];
    private static long _wallAll, _wallHitch;
    private static bool _scPrimed;

    private static unsafe void ReadSched()
    {
        if (OperatingSystem.IsWindows()) return;   // the SCHEDULER column is Linux-only (glibc + /proc)
        byte* ru = stackalloc byte[160];
        if (GetRUsage(RusageThread, ru) != 0) return;
        long* q = (long*)ru;
        _scNow[(int)Sc.Cpu] = (q[0] + q[2]) * 1_000_000 + q[1] + q[3];   // µs
        _scNow[(int)Sc.Sys] = q[2] * 1_000_000 + q[3];
        if (_schedFd == -1)
        {
            byte* path = stackalloc byte[32];
            int k = 0; foreach (char c in "/proc/thread-self/schedstat") path[k++] = (byte)c; path[k] = 0;
            _schedFd = Open(path, 0);
            if (_schedFd < 0) _schedFd = -2;
        }
        if (_schedFd >= 0)
        {
            byte* buf = stackalloc byte[64];
            nint n = Pread(_schedFd, buf, 63, 0);
            long a = 0, b = 0; int i = 0;
            for (; i < n && buf[i] != ' '; i++) a = a * 10 + (buf[i] - '0');
            for (i++; i < n && buf[i] != ' '; i++) b = b * 10 + (buf[i] - '0');
            _scNow[(int)Sc.Wait] = b / 1000;   // µs
        }
        _scNow[(int)Sc.MinFlt] = q[8]; _scNow[(int)Sc.MajFlt] = q[9];
        _scNow[(int)Sc.Vol] = q[16]; _scNow[(int)Sc.Invol] = q[17];
    }

    /// <summary>Fold this frame's scheduler deltas in; <paramref name="dt"/> is the wall it is judged against.</summary>
    private static void Sched(long dt, bool hitch)
    {
        ReadSched();
        if (_scPrimed)
        {
            _wallAll += dt; if (hitch) _wallHitch += dt;
            for (int i = 0; i < (int)Sc.Count; i++)
            {
                long d = _scNow[i] - _scLast[i];
                _scAll[i] += d; if (hitch) _scHitch[i] += d;
            }
        }
        Array.Copy(_scNow, _scLast, (int)Sc.Count); _scPrimed = true;
    }

    /// <summary>The BOX column, once per period: swap-ins/outs and major faults system-wide (/proc/vmstat) and the
    /// PSI stall totals for cpu and memory (/proc/pressure). Cold path. A hitch count that tracks pswpin/pswpout
    /// is the machine reclaiming (zram compresses on the CPU), not the frame.</summary>
    private static long _swpIn, _swpOut, _majF, _psiCpu, _psiMem;
    public static string BoxLine()
    {
        long si = 0, so = 0, mf = 0, pc = 0, pm = 0;
        try
        {
            foreach (var l in System.IO.File.ReadLines("/proc/vmstat"))
            {
                if (l.StartsWith("pswpin ")) si = long.Parse(l.AsSpan(7));
                else if (l.StartsWith("pswpout ")) so = long.Parse(l.AsSpan(8));
                else if (l.StartsWith("pgmajfault ")) mf = long.Parse(l.AsSpan(11));
            }
            pc = PsiTotal("/proc/pressure/cpu"); pm = PsiTotal("/proc/pressure/memory");
        }
        catch { return " box: n/a"; }
        // this process's resident set, so a period that swaps can say whether it is OUR footprint growing
        long rss = 0;
        try { foreach (var l in System.IO.File.ReadLines("/proc/self/status")) if (l.StartsWith("VmRSS:")) { rss = long.Parse(l.AsSpan(6).Trim().TrimEnd(" kB".AsSpan())); break; } } catch { }
        string r = $" box: rss {rss / 1024}MiB, swap in/out {si - _swpIn}/{so - _swpOut} pages, majflt {mf - _majF}, psi some cpu {(pc - _psiCpu) / 1000}ms mem {(pm - _psiMem) / 1000}ms";
        _swpIn = si; _swpOut = so; _majF = mf; _psiCpu = pc; _psiMem = pm;
        return r;
    }
    /// <summary>The THREAD table, once per period: every thread of this process that burned CPU or was preempted --
    /// name, CPU ms, involuntary switches, last CPU it ran on. Names the competitor the sched column only counts.
    /// Cold path; allocates freely.</summary>
    private static readonly System.Collections.Generic.Dictionary<int, (long cpu, long inv)> _thrLast = new();
    public static string ThreadsLine()
    {
        var rows = new System.Collections.Generic.List<(string name, double cpuMs, long inv, int cpu, int nice)>();
        long tck = 100;   // sysconf(_SC_CLK_TCK) is 100 on every Linux we run on
        try
        {
            foreach (var dir in System.IO.Directory.EnumerateDirectories("/proc/self/task"))
            {
                int tid = int.Parse(System.IO.Path.GetFileName(dir));
                string stat = System.IO.File.ReadAllText(dir + "/stat");
                int a = stat.IndexOf('('), b = stat.LastIndexOf(')');
                string name = stat.Substring(a + 1, b - a - 1);
                string[] f = stat.Substring(b + 2).Split(' ');   // f[0] = state = field 3
                long cpu = long.Parse(f[11]) + long.Parse(f[12]);   // utime, stime
                int proc = int.Parse(f[36]), nice = int.Parse(f[16]);
                long inv = 0;
                foreach (var l in System.IO.File.ReadLines(dir + "/status"))
                    if (l.StartsWith("nonvoluntary_ctxt_switches:")) { inv = long.Parse(l.AsSpan(27).Trim()); break; }
                _thrLast.TryGetValue(tid, out var last);
                _thrLast[tid] = (cpu, inv);
                if (last.cpu == 0 && last.inv == 0) continue;   // first sight: no delta yet
                rows.Add((name, (cpu - last.cpu) * 1000.0 / tck, inv - last.inv, proc, nice));
            }
        }
        catch (Exception e) { return " || threads: n/a (" + e.GetType().Name + ")"; }
        rows.Sort((x, y) => y.cpuMs.CompareTo(x.cpuMs));
        var sb = new System.Text.StringBuilder(" || threads (cpu ms, invol, last cpu, nice):");
        int n = 0;
        foreach (var r in rows)
        {
            if (r.cpuMs < 5 && r.inv < 20) continue;
            sb.Append($" {r.name} {r.cpuMs:F0}/{r.inv}/c{r.cpu}/n{r.nice}");
            if (++n == 14) break;
        }
        return sb.ToString();
    }
    /// <summary>Burn CPU on THIS thread until its own kernel tick counter has advanced by
    /// <paramref name="ticks"/>, or <paramref name="capMs"/> of wall has gone by. For the self-check only: the
    /// counter has 10ms granularity, so a fixed WALL spin can leave a contended box with less than one whole
    /// tick -- an empty table and a check that failed about nothing.</summary>
    private static void BurnCpuTicks(long ticks, int capMs)
    {
        long t0 = OwnTicks();
        long cap = Stopwatch.GetTimestamp() + Stopwatch.Frequency * capMs / 1000;
        if (t0 < 0)
        {
            long to = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 50; while (Stopwatch.GetTimestamp() < to) { }
            return;
        }
        while (OwnTicks() - t0 < ticks && Stopwatch.GetTimestamp() < cap)
        {
            long to = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 200;
            while (Stopwatch.GetTimestamp() < to) { }
        }
    }
    /// <summary>utime+stime of the calling thread, in kernel ticks; -1 when /proc cannot say.</summary>
    private static long OwnTicks()
    {
        try
        {
            string s = System.IO.File.ReadAllText("/proc/thread-self/stat");
            string[] f = s.Substring(s.LastIndexOf(')') + 2).Split(' ');
            return long.Parse(f[11]) + long.Parse(f[12]);
        }
        catch { return -1; }
    }

    private static long PsiTotal(string path)
    {
        foreach (var l in System.IO.File.ReadLines(path))
            if (l.StartsWith("some ")) { int k = l.IndexOf("total="); return long.Parse(l.AsSpan(k + 6)); }
        return 0;
    }

    /// <summary>The BLAME column: per hitch frame, the top-level zone with the largest excess over the previous
    /// period's mean frame -- or <c>diffuse</c> when no zone holds half the excess, the slow-thread signature the
    /// hitch row's average blurs away. Row 0 only, deliberately: blame answers "where did the GAME thread's frame
    /// go", and a render thread's spike reaches that frame through the publish wait, i.e. row 0's <c>Seam</c>.</summary>
    private static readonly long[] _mean = new long[N];
    private static readonly int[] _blame = new int[(int)Zone.Nested + 1];
    private static void Blame()
    {
        long total = 0, best = 0; int who = (int)Zone.Nested;
        for (int i = 0; i < (int)Zone.Nested; i++)
        {
            long x = _frame[i] - _mean[i];
            if (x <= 0) continue;
            total += x;
            if (x > best) { best = x; who = i; }
        }
        _blame[best * 2 > total ? who : (int)Zone.Nested]++;
    }

    /// <summary>The frame boundary: fold this frame into the period, keep it if it is the worst one, and
    /// reset. <paramref name="dt"/> is the present-to-present time the zones are measured against.</summary>
    public static void EndFrame(long dt)
    {
        _dtSum += dt; _frames++;
        double msNow = dt * 1000.0 / Stopwatch.Frequency;
        if (msNow <= StallMs) { _dtNoStall += dt; _framesNoStall++; }
        if (dt > _dtWorst) { _dtWorst = dt; Array.Copy(_frame, _worst, Rows * N); }
        if (dt > _dtWorstDrew && Drew()) { _dtWorstDrew = dt; Array.Copy(_frame, _worstDrew, Rows * N); }
        if (_enrollMarked && _enrollN < _enrollTicks.Length) _enrollTicks[_enrollN++] = _frame[(int)Zone.Enroll];
        _enrollMarked = false;
        double ms = dt * 1000.0 / Stopwatch.Frequency;
        bool hitch = ms > _hitchMs && ms <= StallMs && Drew();
        if (hitch) { _hitchN++; _dtHitch += dt; Blame(); }
        if (ExtenderLog.Enabled) Sched(dt, hitch);
        _hitchRing[(_frames - 1) & 15] = hitch;
        for (int i = 0; i < Rows * N; i++) { _sum[i] += _frame[i]; if (hitch) _hitch[i] += _frame[i]; _frame[i] = 0; }
        for (int e = 0; e < NE; e++) { _evSum[e] += _ev[e]; if (hitch) _evHitch[e] += _ev[e]; _ev[e] = 0; }
    }

    /// <summary>The hitch row and the event columns; resets both.</summary>
    private static string HitchLine()
    {
        int frames = Math.Max(_frames, 1);
        var sb = new System.Text.StringBuilder();
        if (_hitchN == 0) sb.Append(" || hitch: none");
        else
        {
            sb.Append($" || hitch {_hitchN} frames (over {_hitchMs:F1}ms, under {StallMs:F0}ms, rendered) ").Append(Row(_hitch, _dtHitch, _hitchN));
        }
        // From the frames under the stall cap: one 1,7s transition frame in a period would otherwise lift the
        // next period's threshold past every hitch in it -- and transitions are where the stutter is looked for.
        if (_gpuN > 0) sb.Append($" || gpu-span all|hitch: {_gpuSum / _gpuN:F1}|{(_gpuHitchN > 0 ? _gpuHitch / _gpuHitchN : 0):F1}ms ({_gpuHitchN} paired)");
        if (_hitchN > 0)
        {
            sb.Append(" || blame:");
            for (int i = 0; i <= (int)Zone.Nested; i++)
                if (_blame[i] > 0) sb.Append(i == (int)Zone.Nested ? $" diffuse {_blame[i]}" : $" {(Zone)i} {_blame[i]}");
        }
        double usAll = _wallAll * 1e6 / Stopwatch.Frequency, usHitch = _wallHitch * 1e6 / Stopwatch.Frequency;
        if (_wallAll > 0)
            sb.Append($" || sched all|hitch: cpu {100 * _scAll[0] / Math.Max(usAll, 1):F0}%|{100 * _scHitch[0] / Math.Max(usHitch, 1):F0}%")
              .Append($" (sys {100 * _scAll[5] / Math.Max(usAll, 1):F0}%|{100 * _scHitch[5] / Math.Max(usHitch, 1):F0}%)")
              .Append($" rqwait {_scAll[6] / 1000.0 / frames:F2}|{(_hitchN > 0 ? _scHitch[6] / 1000.0 / _hitchN : 0):F2}ms")
              .Append($" invol {(double)_scAll[4] / frames:F2}|{(_hitchN > 0 ? (double)_scHitch[4] / _hitchN : 0):F2}")
              .Append($" vol {(double)_scAll[3] / frames:F2}|{(_hitchN > 0 ? (double)_scHitch[3] / _hitchN : 0):F2}")
              .Append($" minflt {(double)_scAll[1] / frames:F1}|{(_hitchN > 0 ? (double)_scHitch[1] / _hitchN : 0):F1}")
              .Append($" majflt {_scAll[2]}|{_scHitch[2]}");
        sb.Append(" || events/frame all|hitch:");
        for (int e = 0; e < NE; e++)
            sb.Append($" {(Ev)e} {(double)_evSum[e] / frames:F1}|{(_hitchN > 0 ? (double)_evHitch[e] / _hitchN : 0):F1}");
        return sb.ToString();
    }

    /// <summary>The Enroll ticks of every CADENCE frame this period -- a frame the enrolment walk ran on, marked
    /// by <see cref="MarkEnrollFrame"/>. A p99 over that population is what an enrolment slot budget is judged
    /// by; over all frames it would only be the walk's mean. The walk runs every 12-20 frames, so a 900-frame
    /// period never fills the array.</summary>
    private static readonly long[] _enrollTicks = new long[1024];
    private static int _enrollN;
    private static bool _enrollMarked;

    /// <summary>Call on a frame the enrolment walk ran, before <see cref="EndFrame"/>.</summary>
    public static void MarkEnrollFrame() { _enrollMarked = true; Count(Ev.Enroll); }

    private static string EnrollP99()
    {
        int n = _enrollN;
        if (n == 0) return " || Enroll: no walk this period";
        var s = new long[n];
        Array.Copy(_enrollTicks, s, n);
        Array.Sort(s);
        double k = 1000.0 / Stopwatch.Frequency;
        return $" || Enroll walks {n}: p99 {s[(int)(n * 0.99)] * k:F1}ms p50 {s[n / 2] * k:F1}ms";
    }

    /// <summary>The period line, and the reset with it: the average frame's split, the worst frame's, and the worst
    /// frame that actually rendered (dropped when it IS the worst frame, so its presence means the worst frame never
    /// reached the scene render). <c>other</c> is the residual, and a large one is itself the finding. With a render
    /// thread a fourth clause appears, its own row -- no residual, and a frame should cost <c>max</c> of the two.</summary>
    public static string Period()
    {
        int frames = Math.Max(_frames, 1);
        string line = "FrameZones: [zones] " + Row(_sum, _dtSum, frames)
                    + RenderClause(_sum, frames)
                    + " || worst frame " + Row(_worst, _dtWorst, 1) + RenderClause(_worst, 1);
        if (_dtWorstDrew != _dtWorst)
            line += " || worst that RENDERED " + Row(_worstDrew, _dtWorstDrew, 1) + RenderClause(_worstDrew, 1);
        line += EnrollP99();
        line += HitchLine();
        line += $" || over {_frames} presents";
        ResetPeriod();
        return line;
    }

    /// <summary>Close the period without building its line: the adaptive hitch threshold and the blame baseline
    /// roll forward from it, every period accumulator clears. <see cref="Period"/> is this plus the line.</summary>
    public static void ResetPeriod()
    {
        int frames = Math.Max(_frames, 1);
        if (_framesNoStall > 0) _hitchMs = MathF.Max(2f, (float)(_dtNoStall * 1000.0 / Stopwatch.Frequency / _framesNoStall) * HitchFactor);
        _dtNoStall = 0; _framesNoStall = 0;
        _gpuSum = 0; _gpuHitch = 0; _gpuN = 0; _gpuHitchN = 0;
        Array.Clear(_scAll, 0, (int)Sc.Count); Array.Clear(_scHitch, 0, (int)Sc.Count); _wallAll = _wallHitch = 0;
        Array.Clear(_blame, 0, _blame.Length);
        for (int i = 0; i < N; i++) _mean[i] = _sum[i] / frames;
        Array.Clear(_hitch, 0, Rows * N); Array.Clear(_evSum, 0, NE); Array.Clear(_evHitch, 0, NE);
        _dtHitch = 0; _hitchN = 0;
        _enrollN = 0;
        Array.Clear(_sum, 0, Rows * N); Array.Clear(_worst, 0, Rows * N); Array.Clear(_worstDrew, 0, Rows * N);
        _dtSum = 0; _dtWorst = 0; _dtWorstDrew = 0; _frames = 0;
    }

    /// <summary>The render thread's row, or nothing at all when it never ran -- an empty clause is how the
    /// single-threaded reading stays byte-identical to what it was before rows existed.</summary>
    private static string RenderClause(long[] t, int frames)
    {
        // The TOTAL is the top-level zones, because that is the row's wall. Whether to print at all asks the
        // WHOLE row: a render thread whose brackets are all nested ones (every zone under Alloy, with the
        // Alloy bracket itself opened on the game thread by some future arrangement) would sum to zero
        // top-level and vanish from the line while plainly having run.
        long total = 0, any = 0;
        for (int i = 0; i < (int)Zone.Nested; i++) total += t[N + i];
        for (int i = 0; i < N; i++) if (i != (int)Zone.Nested) any += t[N + i];
        if (any == 0) return "";
        double k = 1000.0 / Stopwatch.Frequency / frames;
        var sb = new System.Text.StringBuilder($" || render thread {total * k:F1}ms =");
        for (int i = 0; i < N; i++)
        {
            if (i == (int)Zone.Nested || t[N + i] == 0) continue;
            sb.Append($" {(Zone)i}:{t[N + i] * k:F1}");
        }
        return sb.ToString();
    }

    /// <summary>The GAME row of <paramref name="t"/> -- row 0, which is the one the frame time partitions and
    /// therefore the only one with a residual. The render row is <see cref="RenderClause"/>'s.</summary>
    private static string Row(long[] t, long dt, int frames)
    {
        double k = 1000.0 / Stopwatch.Frequency / frames;
        long claimed = 0;
        for (int i = 0; i < (int)Zone.Nested; i++) claimed += t[i];
        var sb = new System.Text.StringBuilder();
        sb.Append($"{dt * k:F1}ms =");
        for (int i = 0; i < (int)Zone.Nested; i++) sb.Append($" {(Zone)i}:{t[i] * k:F1}");
        sb.Append($" other:{(dt - claimed) * k:F1}  (");
        for (int i = (int)Zone.Nested + 1; i < N; i++)
        {
            if (i > (int)Zone.Nested + 1) sb.Append(' ');
            sb.Append($"{(Zone)i}:{t[i] * k:F1}");
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>Every accumulator back to boot state -- both zone rows, the period, the worst-frame latches, the
    /// hitch ledger and its ring, the blame baseline, the event counters, the GPU-span pairing, the scheduler
    /// deltas and the enrolment population. For the self-checks, which synthesise frames, and in one place so it
    /// cannot fall out of step with the fields.</summary>
    private static void ResetAll()
    {
        Array.Clear(_frame); Array.Clear(_sum); Array.Clear(_worst); Array.Clear(_worstDrew);
        Array.Clear(_hitch); Array.Clear(_mean); Array.Clear(_blame);
        Array.Clear(_ev); Array.Clear(_evSum); Array.Clear(_evHitch);
        Array.Clear(_hitchRing); Array.Clear(_scAll); Array.Clear(_scHitch);
        _dtSum = 0; _dtWorst = 0; _dtWorstDrew = 0; _dtHitch = 0; _dtNoStall = 0;
        _frames = 0; _hitchN = 0; _framesNoStall = 0;
        _wallAll = 0; _wallHitch = 0;
        _gpuSum = 0; _gpuHitch = 0; _gpuN = 0; _gpuHitchN = 0;
        _enrollN = 0; _enrollMarked = false;
        _hitchMs = 16.7f;
    }

    /// <summary>Off-device: the per-thread rows, on a REAL second thread (a row claimed on the calling thread
    /// would prove nothing about thread-statics). What must hold: the second thread's bracket lands in row 1 and
    /// NOT row 0, the game row's residual is untouched, a parent on one thread does not subtract a child opened on
    /// the other, and the period line grows a render clause only when row 1 ran.</summary>
    private static void RowSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "FrameZones.RowSelfCheck");
        long f = Stopwatch.Frequency;
        long t0 = Stopwatch.GetTimestamp() - f;
        Add(Zone.Alloy, t0);                        // the game thread's row
        long gameAlloy = _frame[(int)Zone.Alloy];
        check.Expect(gameAlloy >= f, "the calling thread accumulates into row 0 with no claim of its own");

        long childSeen = -1, rowSeen = -1;
        var t = new Thread(() =>
        {
            UseRow(RowRender);
            rowSeen = CurrentRow;
            // The parent/child subtraction must be per row: Zone.Alloy is a second away on ROW 0, and a
            // parent here that saw it would subtract a second it never spent.
            childSeen = Ticks(Zone.Alloy);
            Add(Zone.Alloy, Stopwatch.GetTimestamp() - f);
        });
        t.Start(); t.Join();
        check.Expect(rowSeen == RowRender, "a thread that claims a row keeps it");
        check.Expect(childSeen == 0, "Ticks() is the CALLING thread's row, so a parent subtracts only its own child");
        check.Expect(_frame[(int)Zone.Alloy] == gameAlloy, "the render thread's bracket did not land in the game row");
        check.Expect(_frame[N + (int)Zone.Alloy] >= f, "...it landed in row 1");

        EndFrame(f);
        check.Expect(_sum[(int)Zone.Alloy] >= f && _sum[N + (int)Zone.Alloy] >= f,
                     "EndFrame folds BOTH rows into the period");
        string line = Period();
        check.Expect(line.Contains("render thread ") && line.Contains("other:"),
                     "the period line carries the render clause beside the game row's residual");
        // ...and with row 1 empty it must read exactly as it did before rows existed.
        Add(Zone.Alloy, Stopwatch.GetTimestamp() - f);
        EndFrame(f);
        check.Expect(!Period().Contains("render thread "), "no render clause on a single-threaded period");
        Array.Clear(_frame, 0, Rows * N); Array.Clear(_sum, 0, Rows * N);
        Array.Clear(_worst, 0, Rows * N); Array.Clear(_worstDrew, 0, Rows * N);
        // ONE place, not a list to keep in step: a term this check leaves behind is one the suite below reads
        // as a finding. The two Period() calls above re-key the BLAME baseline off this check's own synthetic
        // frames, which is how the first version of it made the real suite report its deliberate Alloy spike
        // as `diffuse`; the hitch ring and the GPU-span pairing are the same hazard with no assertion on them
        // yet, which is exactly why they belong in a reset nobody has to remember.
        ResetAll();
        log.Info(check.Ok ? "FrameZones.RowSelfCheck: passed"
                          : "FrameZones.RowSelfCheck: FAILED -- zone rows are not per thread, so with "
                          + "alloy.renderthread=1 the residual and the blame column would both be nonsense");
    }

    /// <summary>Off-device: subtraction, worst-frame capture and the period reset. All three are load-bearing
    /// -- a parent that failed to subtract its child would double-count the Alloy frame into the shim (which
    /// is exactly the reading this ticket is correcting), and an averaged-away worst frame is the spike the
    /// whole instrument exists to name.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        ResetAll();
        RowSelfCheck(log);

        long f = Stopwatch.Frequency;
        // A bracket whose child claimed more than the parent spent contributes NOTHING -- never a negative,
        // which would credit the parent's siblings with time nobody spent. The unsubtracted arm must still
        // record something, or the subtraction would be indistinguishable from a dead bracket.
        long t0 = Stopwatch.GetTimestamp() - f;   // a synthetic bracket one second wide
        _frame[(int)Zone.Alloy] = 100;
        Add(Zone.Shim, t0, f * 2);
        bool ok = _frame[(int)Zone.Shim] == 0;
        Add(Zone.Shim, t0);
        ok &= _frame[(int)Zone.Shim] >= f;
        if (!ok) log.Info("FrameZones.SelfCheck: FAILED -- a parent bracket did not subtract its child");
        // a sampled bracket is credited `every` times over, so the split reads as a whole-walk estimate.
        AddSampled(Zone.Chase, t0, 16);
        bool s = _frame[(int)Zone.Chase] >= f * 16 && _frame[(int)Zone.Chase] < f * 17;
        if (!s) log.Info("FrameZones.SelfCheck: FAILED -- the sampled bracket was not extrapolated");

        EndFrame(f);                      // a 1000ms frame that rendered -- the worst so far, on both latches
        _frame[(int)Zone.Enroll] = f;
        MarkEnrollFrame();                // the one cadence frame -- the p99 population is this alone
        EndFrame(f / 2);                  // ...and a faster one, which must not displace it
        bool w = _worst[(int)Zone.Alloy] == 100 && _worst[(int)Zone.Enroll] == 0;
        if (!w) log.Info("FrameZones.SelfCheck: FAILED -- the worst frame is not the one that was kept");

        // The transition stall: longer than anything else in the period, and every renderer zone at zero.
        // It must take the overall latch and must NOT take the rendered one, or one area load per period
        // blinds the instrument to the gameplay spike for the rest of it.
        _frame[(int)Zone.Ui] = 7;
        EndFrame(f * 4);
        bool t = _dtWorst == f * 4 && _worst[(int)Zone.Ui] == 7
                 && _dtWorstDrew == f && _worstDrew[(int)Zone.Alloy] == 100;
        if (!t) log.Info("FrameZones.SelfCheck: FAILED -- a non-rendering stall claimed the rendered-frame latch");

        // The hitch ledger: the 1000ms, 500ms and 4000ms frames above are stalls, not hitches. One 20ms
        // rendered frame is, and only its events land in the hitch column.
        bool h = _hitchN == 0;
        _hitchMs = 16.7f;
        _frame[(int)Zone.Alloy] = 100; Count(Ev.Mint, 3);
        EndFrame(f / 50);
        h &= _hitchN == 1 && _hitch[(int)Zone.Alloy] == 100 && _evHitch[(int)Ev.Mint] == 3 && _evSum[(int)Ev.Mint] == 3;
        if (!h) log.Info("FrameZones.SelfCheck: FAILED -- the hitch ledger took a stall or missed the hitch");

        string line = Period();
        bool r = line.Contains("Enroll:") && line.Contains("other:") && line.Contains("worst frame")
                 && line.Contains("worst that RENDERED") && line.Contains("Enroll walks 1: p99 1000")
                 && line.Contains("hitch 1 frames") && line.Contains("events/frame all|hitch:")
                 && _frames == 0 && _dtWorst == 0 && _dtWorstDrew == 0 && _hitchN == 0 && _evSum[(int)Ev.Mint] == 0
                 && _hitchMs > 25f && _hitchMs < 35f;   // the next threshold follows the NON-STALL mean: the one 20ms frame, x1,5
        if (!r) log.Info("FrameZones.SelfCheck: FAILED -- the period line or its reset drifted");
        // the scheduler column: getrusage must resolve and this thread must have run, and the hitch frame above
        // must have been blamed on the one zone it spent (Alloy) rather than read as diffuse. The scheduler,
        // thread and box columns are Linux-only (glibc + /proc); Windows reads them as n/a.
        bool sc = line.Contains("sched all|hitch: cpu") && line.Contains("blame: Alloy 1")
                  && (OperatingSystem.IsWindows() || _scNow[(int)Sc.Cpu] > 0);
        if (!sc) log.Info($"FrameZones.SelfCheck: FAILED -- sched/blame columns ({line.Substring(Math.Max(0, line.IndexOf("|| blame")))})");
        bool tb = true;
        if (!OperatingSystem.IsWindows())
        {
            // the thread table: two reads (the first only primes), the second must list this thread by name
            ThreadsLine();
            BurnCpuTicks(2, 1000);
            string thr = ThreadsLine() + BoxLine();
            tb = !thr.Contains("n/a") && thr.Contains("/c");
            if (!tb) log.Info("FrameZones.SelfCheck: FAILED -- thread/box table: " + thr);
        }
        r &= sc && tb;
        log.Info(ok && s && w && t && h && r ? "FrameZones.SelfCheck: passed"
                                   : "FrameZones.SelfCheck: see failures above");
    }
}
