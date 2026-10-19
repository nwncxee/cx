using CX.Alloy.Db;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Services.Perf;

namespace CX.Alloy;

/// <summary>Owns the engine's two 3D draw funnels so a vanilla draw never reaches GL: nwmain issues all its
/// 3D geometry through <c>GLRender::RenderVertexData</c> and <c>RenderVertexStreamData</c>, and once
/// <see cref="Armed"/> both are swallowed here -- a GUI scene records the call instead, an engine-owned frame
/// lets it through (<see cref="EngineOwnsDraw"/>), and <see cref="_passed"/> counts what still got past.</summary>
internal static unsafe class RspShim
{

    private static ExtenderLog? _log;
    private static GlRenderVertexDataFn* _vdoSlot, _vdoBottomSlot;
    private static int* _fullDynamicLighting;
    private static GlRenderVertexStreamDataFn _origStream;
    private static RiEnableFn _riEnable, _riDisable;
    /// <summary><c>GLRender::m_pCurrentCustomProgram</c> -- the latch both draw funnels open with (shader type
    /// 0xe means "use whatever that global points at"). The swallow leaves it the one stale field of the
    /// engine's write set, so a program latched by an emitter or a shared material would capture the next
    /// engine draw; <see cref="HandBackEngineState"/> zeroes it.</summary>
    private static IntPtr* _curCustom;

    /// <summary>Chain priority on <c>GLRender::RenderVertexData</c>: strictly between
    /// <see cref="DrawFunnelService"/>'s 50 and DrawStatsService's 10. The funnel service sits ABOVE and calls
    /// the chain before its own <c>Observe</c>, so the bone capture still happens on a swallowed call;
    /// DrawStats sits BELOW, so it does not count a draw that never issued.</summary>
    private const int SwallowPriority = 20;
    /// <summary>...and the honest counter, at the bottom of the same chain. Anything that reaches the
    /// original during the window passes through here, whatever route it took.</summary>
    private const int BottomPriority = -100;


    /// <summary>Did every ENGINE symbol resolve and both draw funnels hook? False is an
    /// <see cref="AlloyGate"/> refusal: Alloy stands down whole rather than leave the engine's draws
    /// suppressed with nothing of ours replacing them.</summary>
    internal static bool Armed { get; private set; }

    /// <summary>...and is GL up yet? <see cref="Gl"/>'s entry points are resolved lazily on the first frame
    /// that needs them, so at <see cref="Install"/> time -- service init, no context -- every one is null, and
    /// asking there would stand the cutover down for the session over a condition that is false only before
    /// the first frame. <see cref="Gl.Init"/> is one bool from the second call on, so asking costs a branch.</summary>
    internal static bool Ready
    {
        get
        {
            if (Gl.BindFramebuffer != null) return true;
            if (_log == null || !Gl.Init(_log) || Gl.BindFramebuffer == null) return false;
            _log.Info("RspShim: glBindFramebuffer resolved -- the cutover takes the frame from here");
            return true;
        }
    }

    private static bool _checkOk, _barLogged;

    /// <summary>Last period's refusal totals, so the period line can report the CHANGE. See <see cref="Period"/>.</summary>
    private static int _lastRefBuf, _lastRefTex;
    private static long _swallowed, _passed, _ticks, _frames;
    /// <summary>Per-step tick sums over the period, indexed by <see cref="_stepNames"/>; the index order is
    /// the period line's. What is left against <see cref="_ticks"/> is the shim's own bookkeeping.</summary>
    private static readonly long[] _stepTicks = new long[6];
    private static readonly string[] _stepNames = { "emit", "anchor", "sky", "bin", "present", "handback" };

    /// <summary>The invariants the shim cannot discover at runtime and would fail silently on:
    /// <see cref="SwallowPriority"/> must sit above DrawStatsService's, or the draw histogram counts draws that
    /// never issued, and the Scene vtable displacements must be distinct. Neither shows up as an error; both
    /// read as a wrong-looking session.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = SwallowPriority > DrawStatsService.DrawPriority;
        // A duplicated displacement is a silent wrong virtual, which reads as a rendering oddity, not a typo.
        ReadOnlySpan<int> slots = stackalloc int[] { EngineLayout.SceneDoBinBucketSlot, EngineLayout.SceneDoEmitterBucketSlot };
        for (int i = 0; i < slots.Length && ok; i++)
            for (int j = i + 1; j < slots.Length; j++)
                if (slots[i] == slots[j]) { ok = false; break; }
        _checkOk = ok;
        log.Info(ok ? $"RspShim.SelfCheck: passed (swallow at {SwallowPriority}, "
                    + $"above {DrawStatsService.DrawPriority}; "
                    + $"{slots.Length} distinct Scene vtable slots)"
                    : "RspShim.SelfCheck: FAILED -- chain placement or a vtable displacement is wrong; the "
                    + "cutover would miscount or call the wrong virtual");
        return ok;
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!_checkOk) return;   // SelfCheck runs unconditionally, early in AlloyRenderer.Initialize
        // The engine's frame delta: every ported integrator steps on it, so a gap shows up in the one
        // refusal line below.
        FrameDelta.Resolve(log);
        _fullDynamicLighting = (int*)Native.Sym(SymbolMap.FullDynamicLighting);
        // The state hand-back's two entry points, deliberately NOT in the `missing` list below: standing the
        // cutover down over its epilogue would be the worse trade.
        _riEnable = EngineFn.RiEnable;
        _riDisable = EngineFn.RiDisable;
        _curCustom = (IntPtr*)Native.Sym(SymbolMap.GlCurrentCustomProgram);
        // Named, not counted: "an engine symbol did not resolve" is unactionable, and this list is the only
        // place that knows which one. glBindFramebuffer is deliberately NOT here -- see Ready.
        string? missing = FrameDelta.Missing
                        ?? (_fullDynamicLighting == null ? SymbolMap.FullDynamicLighting : null)
                        // The particle simulation cannot stand itself down: the only thing behind it is
                        // Scene::DoEmitterBucket, whose draws are the engine's, so the choice is an owned
                        // simulation or a vanilla frame.
                        ?? EmitterSim.Missing;
        if (missing != null)
        {
            log.Info($"RspShim: {missing} did not resolve -- the shim stands down and with it the whole "
                   + "Alloy path (AlloyGate refuses an unarmed shim); the engine renders");
            return;
        }

        _vdoSlot = hooks.ChainGlRenderVertexData(&OnVdoDraw, SwallowPriority);
        _vdoBottomSlot = hooks.ChainGlRenderVertexData(&OnVdoBottom, BottomPriority);
        _origStream = hooks.InstallGlRenderVertexStreamData(&OnStreamDraw);
        if (_vdoSlot == null || _vdoBottomSlot == null || _origStream == null)
        {
            log.Info("RspShim: a draw-funnel hook failed -- the cutover stands down rather than replace "
                   + "RenderSinglePass with a shim that cannot prove it drew nothing");
            return;
        }

        Armed = true;
        // "ARMED" is the engine half; the frame does not become Alloy's until Ready as well, which is a
        // first-frame away and logs its own line.
        FrameRegistry.Global.Register(FramePhase.PostPresent, 30, Period, "RspShim.period", log);
        log.Info("RspShim: ARMED -- Scene::RenderSinglePass replaced by the mutate-only shim on "
               + "the Alloy path ([rspshim] every 900 frames; alloy.enabled=0 restores it)");
    }

    /// <summary>The two <c>RenderInterface</c> caps of the hand-back that reach GL. <c>GLRender::Enable</c>
    /// returns before touching GL when the bit in <c>m_bEnabledStates</c> already agrees, so a plain replay
    /// short-circuits in exactly the desynced case: disabling each first forces the cache to disagree and the
    /// enable to issue. Caps 5, 1 and 2 are cache-only writes, so for those the cache IS the state.</summary>
    private const int CapBlend = 0, CapDepthTest = 0xb;   // not-an-offset: engine RenderInterface cap enum, an argument to Enable/Disable

    /// <summary>Replays <c>Scene::RenderSinglePass</c>'s trailing enable block plus the custom-program latch.
    /// The engine's GUI scenes draw after us through the CACHED <c>RenderInterface</c> wrappers, and Alloy has
    /// driven raw GL for a whole frame without writing that cache, so without a resync a panel that asks for
    /// blending can be told it already has it. No call site on the Alloy path.</summary>
    private static void HandBackEngineState()
    {
        if (_curCustom != null) *_curCustom = IntPtr.Zero;
        if (_riEnable == null || _riDisable == null) return;
        _riDisable(5);
        _riEnable(1);
        _riDisable(CapDepthTest); _riEnable(CapDepthTest);
        _riDisable(CapBlend); _riEnable(CapBlend);
        _riDisable(2);
    }

    /// <summary>Engine draws that reached GL while the GUI was Alloy's -- outside every window and every GUI
    /// scene. The claim is that this stays 0; the first one is said, the total is on the status line.</summary>
    internal static long StrayDraws;

    private static void Stray()
    {
        if (!GuiPass.Capturing || GuiCapture.Open) return;   // inside a GUI scene it is a counted custom-shader hand-off
        if (StrayDraws++ == 0)
            _log?.Always("RspShim: an ENGINE draw reached GL while the GUI is Alloy's (outside any GUI scene) -- a tier-2 hole; "
                       + "the count is on /alloy");
    }

    /// <summary>An engine draw the shim must let through: the frame belongs to the engine whole
    /// (<see cref="ScreenOwner.EngineFrame"/> -- a movie frame Alloy handed back), so its one quad is the
    /// only picture there is and swallowing it leaves the screen on the clear alone.</summary>
    private static bool EngineOwnsDraw => ScreenOwner.EngineFrame;

    /// <summary>The swallow, layer 20 of the <c>RenderVertexData</c> chain. Returning here skips every lower
    /// subscriber AND the original, while <see cref="DrawFunnelService"/> at 50 still runs its bone
    /// observation after this returns -- which is how the bone capture survives a swallowed draw. Inside a GUI
    /// scene the call is recorded instead (<see cref="GuiCapture.Vdo"/>).</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoDraw(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        if (GuiCapture.Open && GuiCapture.Vdo(vdo, item, prim, size, offset)) return;
        if (EngineOwnsDraw)
        {
            GlRenderVertexDataFn next = *_vdoSlot;
            if (next != null) next(vdo, item, prim, size, offset);
            return;
        }
        _swallowed++;
    }

    /// <summary>The acceptance counter, at the bottom of the same chain: a call reaching here reached the
    /// ORIGINAL, by whatever route. Zero is the claim, bar the movie quad of a handed-back frame.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoBottom(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        _passed++;
        GlRenderVertexDataFn next = *_vdoBottomSlot;
        if (next != null) next(vdo, item, prim, size, offset);
    }

    /// <summary>The second funnel. Sole owner (nothing else hooks it), so entries are swallowed here and
    /// anything that reaches the trampoline counts as a pass-through.</summary>
    [UnmanagedCallersOnly]
    private static void OnStreamDraw(IntPtr vsdo, uint prim, uint size, uint offset)
    {
        if (GuiCapture.Open && (vsdo == IntPtr.Zero || GuiCapture.Stream(vsdo, prim, size, offset))) return;
        if (EngineOwnsDraw && _origStream != null) { _origStream(vsdo, prim, size, offset); return; }
        _swallowed++;
    }

    /// <summary>Asserted once, on the first frame Alloy actually RESOLVED: are the render target, the depth
    /// buffer, the vertex/index buffers and the textures all ALLOY-CREATED? Not the first shim frame --
    /// enrollment needs a few walks and <c>PostChain.Fbo</c> is generated lazily, so asked then it would latch
    /// FAILED on every clean start. ponytail: a log verdict, re-read by the period line, not a stand-down.</summary>
    private static void Bar()
    {
        _barLogged = true;
        string? bad = PostChain.Fbo == 0 ? "the render target is not Alloy-created (PostChain.Fbo == 0)"
                    : PostChain.DepthTex == 0 ? "the depth buffer is not Alloy-created (PostChain.DepthTex == 0)"
                    : !OwnedBuffers.Armed ? "owned geometry stood down -- the engine uploads its own vertex/index pools"
                    : !OwnedTextures.Armed ? "owned textures stood down -- engine texture names are being sampled"
                    : OwnedBuffers.Borrowed != 0 ? $"{OwnedBuffers.Borrowed} engine buffers are drawn from but were never uploaded by Alloy"
                    : OwnedTextures.Borrowed != 0 ? $"{OwnedTextures.Borrowed} engine textures never came through the upload funnel and are borrowed"
                    : null;
        _log?.Info(bad == null
            ? $"RspShim: [bar] resources OWNED -- target {PostChain.Fbo}, depth {PostChain.DepthTex}, "
            + "geometry and textures Alloy-created with zero refusals"
            : $"RspShim: [bar] FAILED -- {bad}; the frame is Alloy's but its resources are not entirely");
    }

    /// <summary>The acceptance line: <c>passed</c> is the exit bar's draw counter and must read 0. The borrowed
    /// counts are a DELTA over the period, not lifetime totals -- a lifetime figure would let one transient
    /// refusal in one area taint every later line, and telling a new regression from a historic one is the only
    /// thing this column exists for. The texture delta is signed, and a negative reads as recovery.</summary>
    private static void Period()
    {
        if (_frames < AlloyRenderer.LogEvery) return;
        double ms = _ticks * 1000.0 / Stopwatch.Frequency / _frames;
        if (ExtenderLog.Enabled)
            _log?.Info($"RspShim: [rspshim] {_frames} shim frames, {_swallowed / _frames} engine draws swallowed/frame, "
                 + $"PASSED {_passed} (must be 0 unless a movie frame was handed back), {ms:F2}ms/frame in the shim ({StepLine()}), "
                 + $"borrowed {OwnedBuffers.Borrowed - _lastRefBuf} buffers / "
                 + $"{OwnedTextures.Borrowed - _lastRefTex} textures this period (must be 0/0)"
                 // The ported light tick has no other visible surface: `moved` counting down to a handful
                 // once an area settles, and `focus` tracking day/night, is what says it is running.
                 + $"; {FrameDelta.Describe()}");
        _lastRefBuf = OwnedBuffers.Borrowed; _lastRefTex = OwnedTextures.Borrowed;
        _swallowed = 0; _passed = 0; _ticks = 0; _frames = 0;
        Array.Clear(_stepTicks);
    }

    /// <summary>The per-vcall halves of the shim number, ms/frame each.</summary>
    private static string StepLine()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _stepTicks.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(_stepNames[i]).Append(':')
              .Append((_stepTicks[i] * 1000.0 / Stopwatch.Frequency / _frames).ToString("F2"));
        }
        return sb.ToString();
    }
}
