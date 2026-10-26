using System;
using System.Runtime.InteropServices;
using CX.Framework;


namespace CX.Graphics;

/// <summary>
/// Owns the GL 4.6 core context, share-listed with the engine's and on the engine's own drawable -- which
/// Alloy never draws into: its frame goes to <c>PresentTarget</c>'s image, which the engine's context blits.
/// Created on the first presented frame, and any failure disables the service and leaves the engine untouched.
/// Brackets the present: fence, switch, wait, drain. Buffers and fences share; VAOs do not. <c>alloy.gl46</c>.
/// </summary>
public sealed unsafe class GlContextService : IExtenderService
{
    private const int LogEvery = 900;
    private const ulong TimeoutIgnored = 0xFFFFFFFFFFFFFFFFUL;
    private const uint GL_SYNC_GPU_COMMANDS_COMPLETE = 0x9117, GL_VERSION = 0x1F02, GL_RENDERER = 0x1F01,
                       GL_MAJOR_VERSION = 0x821B, GL_MINOR_VERSION = 0x821C;
    /// <summary>The 4.6 context's GL_RENDERER names zink (Mesa's GL-on-Vulkan): the test vehicle whose bindless
    /// teardown faults on a worker thread at texture delete; OwnedTextures reads it to keep such names alive.</summary>
    internal static bool Zink => _zink;
    private static bool _zink;
    /// <summary>GL_RENDERER|GL_VERSION of the 4.6 context, empty until it is live: the driver half of every
    /// <see cref="ProgramBinary"/> key, so a driver update misses instead of offering a stale blob.</summary>
    internal static string Driver { get; private set; } = "";
    /// <summary>The 4.6 context builds programs on driver threads (KHR/ARB_parallel_shader_compile), so
    /// <see cref="GlProgram.Prefetch"/> issues a set at once; false = every program compiles in turn.</summary>
    internal static bool ParallelCompile { get; private set; }
    private const int GLX_FBCONFIG_ID = 0x8013;
    private const int GLX_CONTEXT_MAJOR_VERSION_ARB = 0x2091, GLX_CONTEXT_MINOR_VERSION_ARB = 0x2092;
    private const int GLX_CONTEXT_PROFILE_MASK_ARB = 0x9126, GLX_CONTEXT_CORE_PROFILE_BIT_ARB = 0x1;
    private const int GLX_CONTEXT_FLAGS_ARB = 0x2094, GLX_CONTEXT_DEBUG_BIT_ARB = 0x1;
    private const uint GL_DEBUG_OUTPUT = 0x92E0, GL_DEBUG_OUTPUT_SYNCHRONOUS = 0x8242, GL_DONT_CARE = 0x1100,
                       GL_DEBUG_SEVERITY_NOTIFICATION = 0x826B, GL_DEBUG_TYPE_ERROR = 0x824C,
                       GL_DEBUG_TYPE_DEPRECATED_BEHAVIOR = 0x824D, GL_DEBUG_TYPE_UNDEFINED_BEHAVIOR = 0x824E,
                       GL_DEBUG_TYPE_PORTABILITY = 0x824F, GL_DEBUG_TYPE_PERFORMANCE = 0x8250;
    private const int DebugIds = 64, DebugLines = 200;

    /// <summary><c>alloy.gldebug=1</c>: create the 4.6 context as a DEBUG context and route KHR_debug messages
    /// into the extender log -- the only channel undefined behaviour reaches, as <c>glGetError</c> has no code
    /// for it. Synchronous: the callback runs inside the offending call. ERROR and UNDEFINED_BEHAVIOR log every
    /// time (Mesa files every API error under ONE id) up to <see cref="DebugLines"/>; other types once per id.</summary>
    private static bool _debug;
    /// <summary><c>alloy.gldebug=1</c>: the passes may spend on per-call validation the release path skips.</summary>
    internal static bool Debug => _debug;
    private static readonly uint[] _dbgId = new uint[DebugIds];
    private static int _dbgN, _dbgLogged, _dbgTotal, _dbgDropped;

    /// <summary>True once the 4.6 context is live; consumers (tracer draw) check this per frame.</summary>
    public static bool Available;
    /// <summary>The GLX handle of the 4.6 core context (valid while <see cref="Available"/>).</summary>
    public static IntPtr Context;

    /// <summary><c>alloy.gl46=0</c> -- a CHOICE, not a verdict about the device. Kept
    /// apart from <c>_disabled</c>, which <see cref="Fail"/> also sets, or the frame verdict would report
    /// every driver failure as a config opt-out.</summary>
    public static bool OptedOut { get; private set; }

    private static ExtenderLog? _log;
    private static bool _disabled, _tried;
    private static IntPtr _dpy, _drw, _engCtx;

    private static delegate* unmanaged<byte*, IntPtr> _glxGetProc;
    private static delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> _makeCur;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _destroy;
    private static delegate* unmanaged<IntPtr, IntPtr, void> _glxSwap;
    private static delegate* unmanaged<uint, uint, IntPtr> _fenceSync;
    private static delegate* unmanaged<IntPtr, uint, ulong, void> _waitSync;
    private static delegate* unmanaged<IntPtr, void> _deleteSync;
    private static delegate* unmanaged<void> _flush;
    /// <summary>The <c>alloy.glsync=1</c> ordering primitive. <c>glFinish</c> is the only barrier that is
    /// unconditionally correct across two contexts sharing one drawable -- it returns when the commands have
    /// actually EXECUTED, where the fence pair only promises the GPU will order them. Costs a full pipeline
    /// stall per switch, so it is the diagnosis and, if it is also the cure, a decision to take deliberately.</summary>
    private static delegate* unmanaged<void> _finish;

    private static int _frames, _err46, _errEng, _switchFails;
    /// <summary>Real <c>MakeCurrent</c> calls and presents in this period: the pair is the "switches/frame"
    /// figure on the <c>[gl46]</c> line, which is what makes a bracket merge measurable from the log.</summary>
    private static int _switches, _periodFrames;
    /// <summary>Frames that reached the present with a game-thread bracket still open.</summary>
    private static int _leaks;

    /// <summary>GPU span of the frame's Alloy work -- t0 at the first Enter, t1 re-stamped at every Leave, so
    /// the pair brackets the shadow bake through the colour frame on the GPU timeline. NOT the frame's GPU
    /// time: engine GL outside the brackets (2D UI, present) is excluded and idle gaps inside them are
    /// included. Span far above the [alloy] elapsed sum means the GPU idled on the CPU.</summary>
    private static GpuStamp _span;
    private static bool _stamped;

    /// <summary>The period's GPU span, formatted for the [zones] line; resets the period.</summary>
    public static string GpuSpanLine()
    {
        string s = _span.Measured ? $" || gpu-span avg {_span.AvgMs:F1}ms max {_span.MaxMs:F1}ms" : " || gpu-span n/a";
        _span.Reset();
        return s;
    }

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // the 4.6-context bracket wraps the present, so it is the LAST pre-present participant.
        FrameRegistry.Global.Register(FramePhase.PrePresent, 10, OnFrame, "GlContextService.bracket", log);
        // Restart knob: Boot, not V -- the context decision is this session's.
        _disabled = !CX.Alloy.Renderer.Gl46.Boot;
        OptedOut = _disabled;
        _debug = CX.Alloy.Renderer.GlDebug.Boot;
        if (_debug) ExtenderLog.Synchronous = true;   // a debug session's log must end where the crash is
        // how the bracket orders the two contexts against one drawable. 0 = the fence pair
        // (glFenceSync/glWaitSync, a GPU-side wait that costs nothing on the CPU). 1 = glFinish, which
        // stalls until the work is done. If the black frames go away at 1, the frames were racing the
        // present and the fence pair is not the barrier this drawable needs on this platform.
        _syncMode = CX.Alloy.Renderer.GlSync.Boot;
        log.Info(_disabled
            ? "GlContextService: stood down (alloy.gl46=0 in cx.cfg)"
            : "GlContextService: armed -- 4.6 share-listed context on first presented frame");
        SelfCheck(log);
    }

    [DllImport("libdl.so.2")] private static extern IntPtr dlsym(IntPtr handle, string name);
    private static IntPtr Dl(string n) => dlsym(IntPtr.Zero, n);

    /// <summary>The WGL mirror of the GLX handles above. Lazy-bound like <c>libdl.so.2</c> is on
    /// Windows, so neither import is resolved on the platform that never calls it. There is no FBConfig
    /// step: the window's pixel format is already set by the engine.</summary>
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentDC();
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
    [DllImport("opengl32.dll")] private static extern int wglMakeCurrent(IntPtr hdc, IntPtr ctx);
    [DllImport("opengl32.dll")] private static extern int wglDeleteContext(IntPtr ctx);
    [DllImport("opengl32.dll")] private static extern IntPtr wglGetProcAddress(string name);
    [DllImport("gdi32.dll")] private static extern int SwapBuffers(IntPtr hdc);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromDC(IntPtr hdc);

    /// <summary>The window both contexts draw to, as a Vulkan surface names it: the X display and window, or on Windows
    /// the device context's window (no display). Zeros before the 4.6 context exists.</summary>
    internal static (IntPtr Display, IntPtr Window) Window
        => Win ? (IntPtr.Zero, _drw == IntPtr.Zero ? IntPtr.Zero : WindowFromDC(_drw)) : (_dpy, _drw);

    private static readonly bool Win = OperatingSystem.IsWindows();

    /// <summary>Resolve a GL entry point. Windows goes through the client's SDL loader rather than
    /// <c>wglGetProcAddress</c>, which returns null for the GL 1.1 names (<c>glGetString</c>, <c>glFlush</c>,
    /// <c>glEnable</c>) that live in opengl32 itself.</summary>
    private static IntPtr GP(string name)
    {
        if (Win) return Gl.P(name);
        Span<byte> b = stackalloc byte[name.Length + 1];
        Gl.AsciiZ(name, b);
        fixed (byte* p = b) return _glxGetProc(p);
    }

    /// <summary>Make <paramref name="ctx"/> current on the engine's drawable; 0 = refused. The one place
    /// the two windowing APIs differ in the hot bracket.</summary>
    private static int MakeCur(IntPtr ctx) => Win ? wglMakeCurrent(_drw, ctx) : _makeCur(_dpy, _drw, ctx);

    private static void Destroy(IntPtr ctx) { if (Win) wglDeleteContext(ctx); else _destroy(_dpy, ctx); }

    [UnmanagedCallersOnly]
    private static int XErrorNoop(IntPtr dpy, IntPtr ev) => 0;   // default handler exits the client

    /// <summary><see cref="InBracket"/>'s work has already run this frame, in some other bracket -- the only
    /// reason the health bracket may stand down. It tracks the WORK, not the bracket: <c>GuiPass</c>'s GUI
    /// flush enters the context every frame (main menu included) without doing the work, so a flag set by
    /// mere entry would stand the health bracket down for the session and never issue the deferred deletes.</summary>
    private static bool _workRan;
    /// <summary>Some bracket closed this frame (the old <c>_worked</c>). Diagnostic only -- it is what
    /// separates "the health bracket had the frame to itself" from "a foreign bracket ran and left the work
    /// undone", which is the population the old flag starved.</summary>
    private static bool _entered;
    private static long _healthAlone, _healthAfterForeign, _workElsewhere;

    /// <summary>Is <see cref="InBracket"/>'s work still owed this frame? The health bracket's whole
    /// condition bar the context's own availability, so <see cref="SelfCheck"/> can assert it with no GL.</summary>
    internal static bool WorkDue => !_workRan;

    /// <summary>Per-frame entry from the frame owner's present. Creates the context on first call, runs the
    /// health bracket unless the deferred work already ran this frame, and is the ONE point the bracket depth
    /// must be zero at -- a bracket crosses methods (BeginOwnedFrame enters, its caller leaves), so no
    /// analyzer can pair them and this is the check. Near-free when disabled/failed.</summary>
    public static void OnFrame()
    {
        Presents++;
        if (_disabled) return;
        if (!_tried) { _tried = true; try { Create(); } catch (Exception e) { Fail($"EXCEPTION {e.Message}"); } }
        _periodFrames++;
        bool foreign = _entered, done = _workRan;
        if (Available && !_heldElsewhere && WorkDue && Enter()) { RunInBracketHere(); Leave(); }
        if (Inside)
        {
            if (++_leaks == 1)
                _log?.Always($"GlContextService: the frame reached the present with {_depth} 4.6 bracket(s) still "
                           + "open -- a pass entered and did not leave. Closed here so the engine presents on its "
                           + "own context; the pass that leaked is a bug.");
            _depth = 1;
            Leave();
        }
        if (done) _workElsewhere++; else if (foreign) _healthAfterForeign++; else _healthAlone++;
        _workRan = false; _entered = false;
        _span.Rotate(); _stamped = false;
    }

    /// <summary>The flag's contract, asserted with no context and no GL: the per-frame flag means THE WORK
    /// RAN, not "a bracket was entered". A foreign bracket must leave the work owed, running it must not run
    /// it twice, and the frame boundary must owe it again.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        Action? savedWork = InBracket;
        bool savedRan = _workRan, savedEntered = _entered;
        int ran = 0;
        InBracket = () => ran++;
        _workRan = false; _entered = false;
        bool fresh = WorkDue;
        _entered = true;                 // a GUI flush entered the context and left, doing none of the work
        bool afterForeign = WorkDue;
        RunInBracketHere();
        bool afterWork = WorkDue;
        _workRan = false; _entered = false;   // the frame boundary OnFrame draws
        bool nextFrame = WorkDue;
        InBracket = savedWork; _workRan = savedRan; _entered = savedEntered;
        bool ok = fresh && afterForeign && ran == 1 && !afterWork && nextFrame;
        log.Info(ok
            ? "GlContextService.SelfCheck: passed -- the deferred work is owed until it RUNS, not until someone enters the context"
            : $"GlContextService.SelfCheck: FAILED -- fresh={fresh} afterForeignBracket={afterForeign} ran={ran} "
            + $"afterWork={afterWork} nextFrame={nextFrame}");
        return ok;
    }

    /// <summary>Work that needs A context but not the Alloy pass: the health bracket runs every in-play frame
    /// whether or not anything drew, so it is the one place a resource retired while Alloy is stood down can
    /// still be freed. The owned texture deletes hang here -- a module unload back to the main menu, or the
    /// permanent disarm latch, would otherwise hold the whole owned texture set for the session.</summary>
    public static Action? InBracket;

    private static void Fail(string why)
    {
        _disabled = true;
        Available = false;
        _log?.Info($"GlContextService: FAIL -- {why}; service disabled, engine context untouched");
    }

    /// <summary>Create the 4.6 core context share-listed with the engine's, on the engine's own
    /// drawable. Cold, one-shot; any missing piece is a clean disable.</summary>
    private static void Create()
    {
        var log = _log!;
        if (!Gl.Init(log)) { Fail("Gl.Init"); return; }
        if (!(Win ? ResolveWin() : ResolveGlx())) return;
        if (_engCtx == IntPtr.Zero) { _tried = false; return; }   // no context yet -- retry next frame

        _fenceSync = (delegate* unmanaged<uint, uint, IntPtr>)GP("glFenceSync");
        _waitSync = (delegate* unmanaged<IntPtr, uint, ulong, void>)GP("glWaitSync");
        _deleteSync = (delegate* unmanaged<IntPtr, void>)GP("glDeleteSync");
        _flush = (delegate* unmanaged<void>)GP("glFlush");
        _finish = (delegate* unmanaged<void>)GP("glFinish");
        var getString = (delegate* unmanaged<uint, byte*>)GP("glGetString");
        if (_fenceSync == null || _waitSync == null || _deleteSync == null || _flush == null)
        { Fail("a GL entry point did not resolve"); return; }
        // the one DSA stand-down (docs/alloy-refusals.md). The ENGINE's context is current here and is
        // asked too, since the decode/drip edits run in it and GLX pointers alone prove nothing. It is asked for
        // the extension, not the version: NVIDIA reports a requested 3.3 context as 3.3 yet exposes DSA in it.
        int major = 0, minor = 0;
        Gl.GetIntegerv(GL_MAJOR_VERSION, &major); Gl.GetIntegerv(GL_MINOR_VERSION, &minor);
        bool engDsa = major * 10 + minor >= 45 || Gl.HasExt("GL_ARB_direct_state_access"u8);
        if (!engDsa || Gl.CreateBuffers == null || Gl.CreateTextures == null
            || Gl.CreateVertexArrays == null || Gl.NamedBufferData == null || Gl.TextureStorage2D == null
            || Gl.VertexArrayVertexBuffer == null)
        { Fail($"the engine's context is GL {major}.{minor} or lacks direct state access (4.5 required)"); return; }

        // WGL_CONTEXT_* are numerically identical to the GLX_CONTEXT_* values above (wglext.h), so one
        // attribute list serves both.
        int* attribs = stackalloc int[9] {
            GLX_CONTEXT_MAJOR_VERSION_ARB, 4, GLX_CONTEXT_MINOR_VERSION_ARB, 6,
            GLX_CONTEXT_PROFILE_MASK_ARB, GLX_CONTEXT_CORE_PROFILE_BIT_ARB,
            _debug ? GLX_CONTEXT_FLAGS_ARB : 0, _debug ? GLX_CONTEXT_DEBUG_BIT_ARB : 0, 0 };
        IntPtr ctx = Win ? CreateWin(attribs) : CreateGlx(attribs);
        if (ctx == IntPtr.Zero) { Fail("4.6 core share-listed context creation returned null"); return; }

        if (MakeCur(ctx) == 0)
        {
            Destroy(ctx);
            MakeCur(_engCtx);
            Fail("MakeCurrent(4.6 ctx, engine drawable) refused");
            return;
        }
        string ver = getString != null ? CStr(getString(GL_VERSION)) : "?";
        string renderer = getString != null ? CStr(getString(GL_RENDERER)) : "?";
        _zink = renderer.Contains("zink", StringComparison.OrdinalIgnoreCase);
        Driver = renderer + "|" + ver;
        ParallelCompile = ArmParallelCompile(log);
        bool dbg = _debug && ArmDebug();
        Gl.DrainErrors();   // fresh-context error queue starts clean
        MakeCur(_engCtx);

        Context = ctx;
        Available = true;
        log.Always($"GlContextService: 4.6 core context LIVE (0x{ctx:x}, GL_RENDERER='{renderer}', GL_VERSION='{ver}', engine context {major}.{minor} with DSA) -- bracket every frame" +
                 (dbg ? "; DEBUG context, KHR_debug messages go to this log (alloy.gldebug=1)" : ""));
    }

    /// <summary>GLX side: the display/drawable/context the engine is currently on, plus the entry points
    /// only this path uses. False = a clean disable (<see cref="Fail"/> already called).</summary>
    private static bool ResolveGlx()
    {
        _glxGetProc = (delegate* unmanaged<byte*, IntPtr>)Dl("glXGetProcAddressARB");
        var getCtx = (delegate* unmanaged<IntPtr>)Dl("glXGetCurrentContext");
        var getDpy = (delegate* unmanaged<IntPtr>)Dl("glXGetCurrentDisplay");
        var getDrw = (delegate* unmanaged<IntPtr>)Dl("glXGetCurrentDrawable");
        _makeCur = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Dl("glXMakeCurrent");
        _destroy = (delegate* unmanaged<IntPtr, IntPtr, void>)Dl("glXDestroyContext");
        _glxSwap = (delegate* unmanaged<IntPtr, IntPtr, void>)Dl("glXSwapBuffers");
        if (_glxGetProc == null || getCtx == null || getDpy == null || getDrw == null || _makeCur == null
            || _destroy == null || Dl("glXQueryDrawable") == IntPtr.Zero || Dl("glXChooseFBConfig") == IntPtr.Zero
            || Dl("XDefaultScreen") == IntPtr.Zero || Dl("XSetErrorHandler") == IntPtr.Zero)
        { Fail("a GLX/X11 entry point did not resolve"); return false; }

        _dpy = getDpy(); _drw = getDrw(); _engCtx = getCtx();
        return true;
    }

    /// <summary>WGL side: the engine's current DC and context. No FBConfig equivalent -- the window's
    /// pixel format is already set by the engine, and the share context is an argument to
    /// <c>wglCreateContextAttribsARB</c> rather than a separate <c>wglShareLists</c> step.</summary>
    private static bool ResolveWin()
    {
        _drw = wglGetCurrentDC(); _engCtx = wglGetCurrentContext();
        if (_drw == IntPtr.Zero) { _tried = false; return false; }   // no window yet -- retry next frame
        return true;
    }

    private static IntPtr CreateGlx(int* attribs)
    {
        var queryDrw = (delegate* unmanaged<IntPtr, IntPtr, int, uint*, void>)Dl("glXQueryDrawable");
        var chooseFbc = (delegate* unmanaged<IntPtr, int, int*, int*, IntPtr>)Dl("glXChooseFBConfig");
        var xDefScreen = (delegate* unmanaged<IntPtr, int>)Dl("XDefaultScreen");
        var xSetErr = (delegate* unmanaged<IntPtr, IntPtr>)Dl("XSetErrorHandler");
        var xSync = (delegate* unmanaged<IntPtr, int, int>)Dl("XSync");
        var xFree = (delegate* unmanaged<IntPtr, int>)Dl("XFree");
        var createCtx = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int, int*, IntPtr>)GP("glXCreateContextAttribsARB");
        if (createCtx == null) { Fail("glXCreateContextAttribsARB did not resolve"); return IntPtr.Zero; }

        uint fbcId = 0;
        queryDrw(_dpy, _drw, GLX_FBCONFIG_ID, &fbcId);
        int n = 0;
        int* pick = stackalloc int[3] { GLX_FBCONFIG_ID, (int)fbcId, 0 };
        IntPtr fbcArr = chooseFbc(_dpy, xDefScreen(_dpy), pick, &n);
        if (fbcArr == IntPtr.Zero || n == 0) { Fail($"FBConfig id {fbcId} not found"); return IntPtr.Zero; }
        IntPtr fbc = *(IntPtr*)fbcArr;
        if (xFree != null) xFree(fbcArr);

        IntPtr oldHandler = xSetErr((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&XErrorNoop);
        IntPtr ctx = createCtx(_dpy, fbc, _engCtx, 1, attribs);
        if (xSync != null) xSync(_dpy, 0);
        xSetErr(oldHandler);
        return ctx;
    }

    private static IntPtr CreateWin(int* attribs)
    {
        IntPtr p = wglGetProcAddress("wglCreateContextAttribsARB");
        // WGL reports "unsupported" as 0, 1, 2, 3 or -1, not null alone.
        if ((long)p is >= -1 and <= 3) { Fail("wglCreateContextAttribsARB did not resolve"); return IntPtr.Zero; }
        var createCtx = (delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr>)p;
        return createCtx(_drw, _engCtx, attribs);
    }

    /// <summary>Open the context-switch bracket (hot, zero-alloc): fence the engine's queued commands, switch
    /// to the 4.6 context, server-wait the fence (glWaitSync -- no CPU stall). True = the 4.6 context is
    /// current and GL may be issued; every successful Enter MUST be paired with <see cref="Leave"/> before
    /// returning to engine code. Reentrant: a nested pass pays a depth bump, not a second round trip.</summary>
    public static bool Enter()
    {
        // the thread that HOLDS the context has it current for the whole session, so its bracket is
        // already open and there is nothing to switch. Said true so every pass site -- BeginOwnedFrame, the
        // GUI flush, the atlas pump -- works unchanged on the render thread; without it Enter refuses on
        // _heldElsewhere and the GUI reads "a flush could not enter the 4.6 context" on every frame.
        // Does NOT touch Inside: that flag is the GAME thread's bracket, read by MakeCurrentHere and
        // ReserveForOtherThread to refuse the hand-over mid-bracket, and setting it from here would make a
        // stand-down's re-reservation refuse for ever.
        if (HeldHere) { _heldHereDepth++; return true; }
        if (!Available || _heldElsewhere) return false;
        if (Inside) { _depth++; return true; }
        if (_syncMode == 1)
        {
            if (_finish != null) _finish();
            if (MakeCur(Context) == 0) { _switchFails++; return false; }
        }
        else
        {
            IntPtr fIn = _fenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0);
            _flush();
            if (MakeCur(Context) == 0) { _switchFails++; if (fIn != IntPtr.Zero) _deleteSync(fIn); return false; }
            if (fIn != IntPtr.Zero) { _waitSync(fIn, 0, TimeoutIgnored); _deleteSync(fIn); }
        }
        _switches++;
        if (!_stamped) { _span.LastMs = -1; _stamped = _span.Begin(); if (_span.LastMs >= 0) FrameZones.Gpu(_span.LastMs, GpuStamp.Age); }
        _depth = 1;
        return true;
    }

    /// <summary>Open brackets on the GAME thread (0 = the engine's context is current). The outermost pair
    /// does the switching; every nested one is a counter. Backs <see cref="Inside"/>.</summary>
    private static int _depth;

    /// <summary>Presents since boot, bumped in <see cref="OnFrame"/>'s slot: a frame identity the passes can compare
    /// without a present slot of their own.</summary>
    internal static int Presents { get; private set; }

    /// <summary>The game thread's 4.6 bracket is open: <see cref="Enter"/> succeeded and the matching
    /// <see cref="Leave"/> has not run. The depth IS this fact, so there is no second flag to disagree.</summary>
    internal static bool Inside => _depth > 0;

    /// <summary>Is Alloy's 4.6 context current on THIS thread: the holder thread, or the game thread inside its
    /// bracket. A bindless residency call anywhere else lands in the wrong context.</summary>
    internal static bool Current46 => HeldHere || (Inside && !_heldElsewhere);

    /// <summary>Present the engine's drawable now, from the engine's context, and wait until the GPU has taken it:
    /// how a step that blocks the render thread (a shader compile) gets a frame on screen before it runs. Not the
    /// engine's <c>GLRender::SwapBuffers</c>, which also resets its render states and runs its frame callbacks.</summary>
    internal static void SwapNow()
    {
        if (Win) SwapBuffers(_drw); else if (_glxSwap != null) _glxSwap(_dpy, _drw);
        if (_finish != null) _finish();
    }

    private static int _syncMode;

    /// <summary>Close the bracket: drain glGetError on the 4.6 side (an error can never leak across the
    /// boundary unnoticed), fence, switch back to the engine context, server-wait, drain again. A nested close
    /// drains the errors and nothing else, so this is NOT how a pass hands the engine its context back
    /// mid-bracket -- that is <see cref="Suspend"/>, the only legal way.</summary>
    public static void Leave()
    {
        // the holder thread's bracket never closes -- its context stays current until the session or
        // the stand-down ends. Switching back to the ENGINE context from here would hand the game thread's
        // context to the render thread, which is the GLX BadAccess (and therefore the process exit) this
        // whole hand-over is written around.
        if (HeldHere)
        {
            if (_heldHereDepth > 0) _heldHereDepth--;
            // Errors still drain: an error raised inside a pass must not be attributed to the next one.
            _err46 += Gl.DrainErrors();
            // What this early return deliberately does NOT do, each because the holder path never did the
            // other half of it:
            //   Inside   -- never set true here (see Enter), so there is nothing to clear, and clearing it
            //               would be a write to the GAME thread's flag from this thread.
            //   _span    -- Enter never called Begin, so _stamped cannot be this thread's and End would close
            //               a span somebody else opened.
            // ponytail: the cost is that [gl46]'s gpu-span -- the one discriminator for "GPU-bound or not"
            // -- reads n/a for the whole session with a render thread live, because the only Begin/End pair
            // is on the path this return skips. The upgrade is a GpuStamp owned by the render thread, and
            // what it needs first is for _span's Rotate/Reset (OnFrame and the status line, both the game
            // thread, and the status line is OUTSIDE the frame fence) to stop being able to run while this
            // thread is mid-span. Not stamped from here blind: a half-rotated stamp reports a GPU time that
            // is a measurement of nothing.
            return;
        }
        // Errors drain per bracket whether or not this one switches: an error raised inside a nested pass
        // must not be attributed to the pass that follows it.
        if (_depth > 1) { _depth--; _err46 += Gl.DrainErrors(); return; }
        _depth = 0;   // ...which clears Inside, before the switch: a Fail below must not leave it standing
        _err46 += Gl.DrainErrors();
        if (_stamped) _span.End();

        IntPtr fOut = IntPtr.Zero;
        if (_syncMode == 1) { if (_finish != null) _finish(); }
        else { fOut = _fenceSync(GL_SYNC_GPU_COMMANDS_COMPLETE, 0); _flush(); }
        if (MakeCur(_engCtx) == 0)
        {
            // cannot leave the engine on the wrong context -- disable rather than risk the present
            Fail("MakeCurrent back to the engine context refused");
            return;
        }
        _switches++;
        if (fOut != IntPtr.Zero) { _waitSync(fOut, 0, TimeoutIgnored); _deleteSync(fOut); }
        _errEng += Gl.DrainErrors();
        _entered = true;

        if (++_frames >= LogEvery)
        {
            float perFrame = _periodFrames > 0 ? _switches / (float)_periodFrames : 0f;
            _log?.Info($"GlContextService: [gl46] {_frames} brackets over {_periodFrames} frames (nested passes " +
                       $"share one), ctx switches {_switches} ({perFrame:F1}/frame), " +
                       $"errors 4.6={_err46} engine={_errEng}, switch-fails={_switchFails}, " +
                       $"deferred work since boot: {_healthAlone} health-only frames + {_healthAfterForeign} " +
                       $"after a foreign bracket, {_workElsewhere} already done elsewhere; " +
                       GpuGraveyard.Note() +
                       (_leaks > 0 ? $", {_leaks} LEAKED brackets since boot" : "") +
                       (_debug ? $", debug msgs={_dbgTotal} this period ({_dbgLogged} logged, {_dbgDropped} dropped since boot)" : ""));
            _dbgTotal = 0;
            _frames = 0; _err46 = 0; _errEng = 0; _switchFails = 0; _switches = 0; _periodFrames = 0;
        }
    }

    /// <summary>The only legal mid-bracket hand-back: give the engine its context whatever the depth, for a
    /// site that must issue ENGINE GL inside a pass (the shader-compile notice and its present). Returns the
    /// depth <see cref="Resume"/> restores; 0 = nothing was suspended.</summary>
    public static int Suspend()
    {
        if (HeldHere || !Inside) return 0;
        int depth = _depth;
        _depth = 1;   // make the close below the outermost one, whatever nested it
        Leave();
        return depth;
    }

    /// <summary>Re-open the bracket <see cref="Suspend"/> closed, at the depth it held. False = re-entry was
    /// refused and the caller's remaining GL would land in the engine's context.</summary>
    public static bool Resume(int depth)
    {
        if (depth <= 0) return true;
        if (!Enter()) return false;
        _depth = depth;
        return true;
    }

    /// <summary>The <c>glMaxShaderCompilerThreads</c> count that means the implementation's own maximum.</summary>
    private const uint AllCompilerThreads = uint.MaxValue;

    /// <summary>Give the (current) 4.6 context every compiler thread it has. Logged either way: without the
    /// extension a shader cache miss costs the sum of its programs, not the longest one.</summary>
    private static bool ArmParallelCompile(ExtenderLog log)
    {
        string ext = Gl.HasExt("GL_KHR_parallel_shader_compile"u8) ? "KHR" : Gl.HasExt("GL_ARB_parallel_shader_compile"u8) ? "ARB" : "";
        var threads = ext.Length > 0 ? (delegate* unmanaged<uint, void>)GP("glMaxShaderCompilerThreads" + ext) : null;
        if (threads == null)
        {
            log.Info("GlContextService: the driver has no parallel shader compile (KHR/ARB_parallel_shader_compile) "
                   + "-- a shader cache miss builds its programs one after another");
            return false;
        }
        threads(AllCompilerThreads);
        log.Info($"GlContextService: parallel shader compile armed ({ext}_parallel_shader_compile) -- "
               + "a shader cache miss builds its programs side by side");
        return true;
    }

    /// <summary>Wire the KHR_debug callback on the (current) 4.6 context. Everything enabled, then the
    /// NOTIFICATION severity muted -- Mesa files shader statistics there on every build.</summary>
    private static bool ArmDebug()
    {
        var callback = (delegate* unmanaged<delegate* unmanaged<uint, uint, uint, uint, int, byte*, void*, void>, void*, void>)GP("glDebugMessageCallback");
        var control = (delegate* unmanaged<uint, uint, uint, int, uint*, byte, void>)GP("glDebugMessageControl");
        var enable = (delegate* unmanaged<uint, void>)GP("glEnable");
        if (callback == null || control == null || enable == null)
        { _log?.Info("GlContextService: alloy.gldebug=1 but KHR_debug entry points did not resolve"); return false; }
        enable(GL_DEBUG_OUTPUT);
        enable(GL_DEBUG_OUTPUT_SYNCHRONOUS);
        control(GL_DONT_CARE, GL_DONT_CARE, GL_DONT_CARE, 0, null, 1);
        control(GL_DONT_CARE, GL_DONT_CARE, GL_DEBUG_SEVERITY_NOTIFICATION, 0, null, 0);
        callback(&OnDebugMessage, null);
        return true;
    }

    [UnmanagedCallersOnly]
    private static void OnDebugMessage(uint source, uint type, uint id, uint severity, int length, byte* msg, void* user)
    {
        _dbgTotal++;
        if (id == NvShadowSamplerUb) DescribeShadowSamplers();   // its own once-per-program budget
        bool always = type == GL_DEBUG_TYPE_ERROR || type == GL_DEBUG_TYPE_UNDEFINED_BEHAVIOR;
        if (!always)
        {
            for (int i = 0; i < _dbgN; i++) if (_dbgId[i] == id) return;
            if (_dbgN >= DebugIds) { _dbgDropped++; return; }
            _dbgId[_dbgN++] = id;
        }
        if (_dbgLogged >= DebugLines) { _dbgDropped++; return; }
        _dbgLogged++;
        string kind = type switch
        {
            GL_DEBUG_TYPE_ERROR => "ERROR", GL_DEBUG_TYPE_UNDEFINED_BEHAVIOR => "UNDEFINED BEHAVIOUR",
            GL_DEBUG_TYPE_DEPRECATED_BEHAVIOR => "deprecated", GL_DEBUG_TYPE_PORTABILITY => "PORTABILITY",
            GL_DEBUG_TYPE_PERFORMANCE => "performance", _ => "other",
        };
        _log?.Always($"GlContextService: [gldebug] {kind} id {id} (source 0x{source:x}, severity 0x{severity:x}): {CStr(msg, 1024)}");
    }

    /// <summary>NVIDIA's "shadow sampler on a texture that is not a compare-on depth texture" warning.</summary>
    private const uint NvShadowSamplerUb = 131222;
    private const uint SamplerShadow2D = 0x8B62, Sampler2DArrayShadow = 0x8DC4, SamplerCubeShadow = 0x8DC5,
                       SamplerCubeArrayShadow = 0x900D;
    private static readonly uint[] _shadowDescribed = new uint[8];
    private static int _shadowDescribedN;

    /// <summary><c>alloy.gldebug</c> only: the warning names no program or unit, so log the program's label
    /// and, per shadow sampler, the unit and the texture bound there. Once per program, inside the draw.
    /// ponytail: GL queries (and an ActiveTexture round trip) from a KHR_debug callback are outside the
    /// spec's promises; NVIDIA tolerates them, and this is debug-context-only.</summary>
    private static void DescribeShadowSamplers()
    {
        if (Gl.GetIntegerv == null || Gl.GetProgramiv == null || Gl.GetActiveUniform == null || Gl.GetUniformiv == null) return;
        int prog = 0; Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prog);
        for (int i = 0; i < _shadowDescribedN; i++) if (_shadowDescribed[i] == (uint)prog) return;
        if (_shadowDescribedN >= _shadowDescribed.Length) return;
        _shadowDescribed[_shadowDescribedN++] = (uint)prog;
        GlProgram.Labels.TryGetValue((uint)prog, out string? label);
        int active = 0, count = 0; Gl.GetProgramiv((uint)prog, 0x8B86 /* ACTIVE_UNIFORMS */, &count);
        Gl.GetIntegerv(0x84E0 /* ACTIVE_TEXTURE */, &active);
        var sb = new System.Text.StringBuilder($"GlContextService: [gldebug] shadow samplers of program {prog} ({label ?? "unlabelled"}):");
        byte* name = stackalloc byte[128];
        for (uint u = 0; u < (uint)count; u++)
        {
            int len, size; uint type;
            Gl.GetActiveUniform((uint)prog, u, 128, &len, &size, &type, name);
            uint bindingQuery = type switch
            {
                Sampler2DArrayShadow => Gl.TEXTURE_BINDING_2D_ARRAY, SamplerCubeArrayShadow => Gl.TEXTURE_BINDING_CUBE_MAP_ARRAY,
                SamplerShadow2D => 0x8069 /* TEXTURE_BINDING_2D */, SamplerCubeShadow => 0x8514 /* TEXTURE_BINDING_CUBE_MAP */, _ => 0,
            };
            if (bindingQuery == 0) continue;
            int unit = -1, tex = 0;
            Gl.GetUniformiv((uint)prog, GlProgram.Loc((uint)prog, CStr(name)), &unit);
            Gl.ActiveTexture(Gl.TEXTURE0 + (uint)unit);
            Gl.GetIntegerv(bindingQuery, &tex);
            sb.Append($" {CStr(name)}@unit{unit}=tex{tex}");
        }
        Gl.ActiveTexture((uint)active);
        _log?.Info(sb.ToString());
    }

    // ---- the 4.6 context on a render thread ----

    /// <summary>Make the 4.6 context current on the CALLING thread and leave it current -- what
    /// <c>CX.Alloy.Db.RenderThread</c> does once instead of the per-frame Enter/Leave bracket. A GL context is
    /// current on at most one thread, so this is valid only while the game thread holds no bracket
    /// (<see cref="Inside"/> false) and never calls <see cref="Enter"/> again; the engine's is untouched.</summary>
    public static bool MakeCurrentHere()
    {
        // _heldElsewhere is expected to be TRUE here: the game thread reserved it before starting this
        // thread (ReserveForOtherThread). What must not be true is that the game thread is mid-bracket.
        if (!Available || Inside) return false;
        if (MakeCur(Context) == 0) { _switchFails++; return false; }
        Gl.DrainErrors();
        // Before _heldElsewhere, so a reader that sees the latch also sees who holds it.
        _holderTid = Environment.CurrentManagedThreadId;
        _heldHereDepth = 0;
        // Latch BEFORE returning: from here the game thread's Enter must refuse, or its MakeCurrent would
        // target a context current on this thread and GLX answers BadAccess -- which Xlib's default handler
        // turns into process exit, not an error code.
        _heldElsewhere = true;
        return true;
    }

    /// <summary>The 4.6 context is current on a thread that is not the game thread, so the game thread's
    /// per-frame bracket must stand aside entirely. Read by <see cref="Enter"/> and <see cref="OnFrame"/>.
    /// <para>Volatile: it is written on the render thread at start and stand-down and read on the game thread
    /// every frame, and a stale read either way is the BadAccess this flag exists to prevent.</para></summary>
    private static volatile bool _heldElsewhere;

    /// <summary>The managed thread id of the thread the 4.6 context is current on, or 0 while the game
    /// thread's per-frame bracket owns it. Written by that thread in <see cref="MakeCurrentHere"/> and
    /// cleared in <see cref="ReleaseCurrentHere"/>; volatile because the game thread reads it every frame.</summary>
    private static volatile int _holderTid;

    /// <summary>Is the 4.6 context current on THIS thread for the session -- i.e. am I the render thread?
    /// <see cref="Enter"/>/<see cref="Leave"/> become no-ops here, which is what lets every pass site run
    /// unchanged on either thread.</summary>
    public static bool HeldHere => _holderTid != 0 && _holderTid == Environment.CurrentManagedThreadId;

    /// <summary>Open brackets on the holder thread. Reported, not enforced: an unbalanced pair is a bug in a
    /// pass and a count that never returns to zero names it, where a no-op Enter/Leave otherwise hides it.</summary>
    private static int _heldHereDepth;

    /// <summary>Brackets the holder thread currently has open (0 = balanced).</summary>
    internal static int HeldHereDepth => _heldHereDepth;

    /// <summary>Claim the context for a thread that is about to take it, from the GAME thread, before that
    /// thread runs. Without this there is a window between "the render thread was started" and "it called
    /// MakeCurrentHere" in which the game thread's own bracket can still open -- and then the render thread
    /// finds the context busy and stands down for the session over a race. False = someone already holds it.</summary>
    public static bool ReserveForOtherThread()
    {
        if (!Available || Inside || _heldElsewhere) return false;
        _heldElsewhere = true;
        return true;
    }

    /// <summary>Give the reservation back without ever having taken the context (the thread failed to
    /// start).</summary>
    public static void CancelReservation() => _heldElsewhere = false;

    /// <summary>Drop the 4.6 context from the calling thread (session end, or a stand-down), so the game
    /// thread's bracket can own it again.</summary>
    public static void ReleaseCurrentHere()
    {
        if (!Available) return;
        _holderTid = 0;
        // With the holder gone the count has no owner, and a stand-down that left it non-zero would put
        // "N UNBALANCED 4.6 brackets" on /alloy for the rest of the session -- a counter reporting a thread
        // that no longer exists. Cleared beside the id that gave it meaning.
        _heldHereDepth = 0;
        MakeCur(IntPtr.Zero);
        // Cleared LAST: the game thread may Enter the moment this is false, and it must not do so until the
        // context is genuinely free.
        _heldElsewhere = false;
    }

    /// <summary>Run <see cref="InBracket"/> on the thread that already holds the 4.6 context -- the work that
    /// needs A context but not the Alloy pass (texture retires, the texture registry's drain, the arena pump,
    /// the palette and glyph-atlas builds). With a render thread the game thread's bracket never opens, so
    /// this is the ONE declaration that the frame's deferred work ran. A caller must hold the context.</summary>
    public static void RunInBracketHere()
    {
        Action? work = InBracket;
        if (work == null) return;
        work();
        _workRan = true;
    }

    private static string CStr(byte* p, int max)
    {
        if (p == null) return "<null>";
        int len = 0; while (p[len] != 0 && len < max) len++;
        return System.Text.Encoding.UTF8.GetString(p, len).Replace('\n', ' ');
    }

    private static string CStr(byte* p)
    {
        if (p == null) return "<null>";
        int len = 0; while (p[len] != 0 && len < 256) len++;
        return System.Text.Encoding.ASCII.GetString(p, len);
    }
}
