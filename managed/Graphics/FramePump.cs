using System;
using CX.Framework;

namespace CX.Graphics;

/// <summary>
/// The present's own slot: it counts presented frames and runs the frame's deferred work, which needs a
/// point in the frame rather than a draw -- so a resource retired while Alloy is stood down is still freed.
/// Alloy renders on Vulkan and presents itself, so nothing here enters a context or pays for a bracket.
/// </summary>
public sealed class FramePump : IExtenderService
{
    /// <summary>True once the render device is up; consumers check this per frame. It never goes false in a
    /// Vulkan session -- a lost device is a fault on the submit, not an edge reported here.</summary>
    public static bool Available;

    /// <summary><c>alloy.gpudebug=1</c>: the passes may spend on per-call validation the release path skips,
    /// and every log line is on disk before its call returns.</summary>
    internal static bool Debug => _debug;
    private static bool _debug;

    private static ExtenderLog? _log;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // the deferred work lands after every other pre-present participant has had its say
        FrameRegistry.Global.Register(FramePhase.PrePresent, 10, OnFrame, "FramePump.deferred", log);
        // Restart knob: Boot, not V -- the decision is this session's.
        _debug = CX.Alloy.Renderer.GpuDebug.Boot;
        if (_debug) ExtenderLog.Synchronous = true;   // a debug session's log must end where the crash is
        log.Info("FramePump: armed -- the frame's deferred work runs at the present");
        SelfCheck(log);
    }

    /// <summary>Is <see cref="Deferred"/>'s work still owed this frame? It tracks the WORK, not a visit: a
    /// flag set by mere entry would stand the frame's run down for the session and never issue the deferred
    /// deletes.</summary>
    internal static bool WorkDue => !_workRan;
    private static bool _workRan;

    /// <summary>Presents since boot, bumped in <see cref="OnFrame"/>'s slot: a frame identity the passes can
    /// compare without a present slot of their own.</summary>
    internal static int Presents { get; private set; }

    /// <summary>Per-frame entry from the frame owner's present: counts the frame and runs the deferred work
    /// unless some other point in the frame already did.</summary>
    public static void OnFrame()
    {
        Presents++;
        if (!Available)
        {
            Available = true;
            _log?.Always("FramePump: the render device is up -- the frame's deferred work runs from here");
        }
        if (WorkDue) RunDeferred();
        _workRan = false;
    }

    /// <summary>The flag's contract: the per-frame flag means THE WORK RAN. Running it must not run it twice,
    /// and the frame boundary must owe it again. Oracle: the call count this check installs itself.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        Action? savedWork = Deferred;
        bool savedRan = _workRan;
        int ran = 0;
        Deferred = () => ran++;
        _workRan = false;
        bool fresh = WorkDue;
        RunDeferred();
        bool afterWork = WorkDue;
        _workRan = false;   // the frame boundary OnFrame draws
        bool nextFrame = WorkDue;
        Deferred = savedWork; _workRan = savedRan;
        bool ok = fresh && ran == 1 && !afterWork && nextFrame;
        log.Info(ok
            ? "FramePump.SelfCheck: passed -- the deferred work is owed until it RUNS, and owed again next frame"
            : $"FramePump.SelfCheck: FAILED -- fresh={fresh} ran={ran} afterWork={afterWork} nextFrame={nextFrame}");
        return ok;
    }

    /// <summary>Work that needs a point in the frame but not a draw: it runs every in-play frame whether or
    /// not anything drew, so it is the one place a resource retired while Alloy is stood down can still be
    /// freed. The owned texture deletes hang here -- a module unload back to the main menu, or the permanent
    /// disarm latch, would otherwise hold the whole owned texture set for the session.</summary>
    public static Action? Deferred;

    /// <summary>Run <see cref="Deferred"/>, the frame's deferred work (texture retires, the registry's drain,
    /// the palette and glyph-atlas builds), and declare it ran -- the ONE such declaration.</summary>
    public static void RunDeferred()
    {
        Action? work = Deferred;
        if (work == null) return;
        work();
        _workRan = true;
    }
}
