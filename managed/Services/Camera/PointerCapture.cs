using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>The cursor capture mouse-look and the free camera turn by: one owner for hiding the cursor, grabbing the
/// pointer and measuring its motion, so both cameras capture the same way on each OS.</summary>
internal static unsafe class PointerCapture
{
    /// <summary>Linux measures the pointer itself (<see cref="PointerTrack"/>): the game's SDL 2.0.8 has no
    /// XInput2 there, so its relative mode is warp emulation whose lost or late echoes jump the view.
    /// Windows keeps SDL's relative mode on purpose: SDL 2.0.8 runs it on raw input there, with no warp.</summary>
    private static readonly bool OwnTrack = !OperatingSystem.IsWindows();
    private static readonly CaptureState _s = new();

    /// <summary>One capture's state: the track, the engine's cursor wish held back, and the grab to restore.</summary>
    private sealed class CaptureState
    {
        public readonly PointerTrack Track = new();
        public readonly CursorHold Hold = new();
        public SdlShowCursorFn* Next;
        public ExtenderLog? Log;
        public IntPtr Window;
        public int Grab;
        public bool Active, Chained;
    }

    /// <summary>Chains <c>SDL_ShowCursor</c> once, so the engine's cursor changes mid-look are held until release.
    /// Linux only, on purpose: Windows captures through relative mode, which keeps the cursor hidden itself.</summary>
    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _s.Log = log;
        if (!OwnTrack || _s.Chained) return;
        _s.Chained = true;
        _s.Next = hooks.ChainSdlShowCursor(&OnShowCursor, 0);
        if (_s.Next == null) log.Info("PointerCapture: SDL_ShowCursor chain refused -- the engine's cursor can show mid-look");
    }

    /// <summary>Hide the cursor and start measuring from (<paramref name="x"/>, <paramref name="y"/>);
    /// <paramref name="warped"/> warps the cursor there first, as the action camera's centre.</summary>
    internal static void Begin(int x, int y, bool warped)
    {
        if (!OwnTrack) { EngineFn.SdlSetRelativeMouseMode(1); return; }
        if (!_s.Active)
        {
            _s.Active = true;
            int shown = ShowRaw(-1);
            ShowRaw(0);
            _s.Hold.Begin(shown);
            _s.Window = EngineFn.SdlGetKeyboardFocus();
            if (_s.Window != IntPtr.Zero)
            {
                _s.Grab = EngineFn.SdlGetWindowGrab(_s.Window);
                EngineFn.SdlSetWindowGrab(_s.Window, 1);
            }
        }
        WindowSize(out int w, out int h);
        _s.Track.Begin(x, y, w, h, warped);
        if (warped) EngineFn.SdlWarpMouseInWindow(IntPtr.Zero, x, y);
    }

    /// <summary>Give the cursor back: shown or hidden as the engine last asked, the window's grab as it was.</summary>
    internal static void End()
    {
        if (!OwnTrack) { EngineFn.SdlSetRelativeMouseMode(0); return; }
        if (!_s.Active) return;
        _s.Active = false;
        ShowRaw(_s.Hold.End());
        if (_s.Window != IntPtr.Zero) EngineFn.SdlSetWindowGrab(_s.Window, _s.Grab);
        _s.Window = IntPtr.Zero;
    }

    /// <summary>One captured motion's delta: SDL's xrel on Windows, the pointer track on Linux. False when it
    /// turns nothing.</summary>
    internal static bool Delta(IntPtr ev, out int dx, out int dy)
    {
        var e = (SdlEvent*)ev;
        if (!OwnTrack) { dx = e->XRel; dy = e->YRel; return true; }
        WindowSize(out int w, out int h);
        _s.Track.Size(w, h);
        bool moved = _s.Track.Feed(e->X, e->Y, out dx, out dy, out bool warp, out int wx, out int wy);
        if (warp) EngineFn.SdlWarpMouseInWindow(IntPtr.Zero, wx, wy);
        if (_s.Track.WarpsIgnored && !_s.Track.Reported)
        {
            _s.Track.Reported = true;
            _s.Log?.Info("PointerCapture: the window system ignored cursor warps; a look now stops at the window edge");
        }
        return moved;
    }

    /// <summary>The capture's line for a report.</summary>
    internal static string Report() => OwnTrack
        ? $"own pointer track ({_s.Track.Warps} warp(s), {_s.Track.Dropped} stale, {_s.Track.Lost} lost)"
        : "SDL relative mode";

    private static void WindowSize(out int w, out int h)
    {
        w = h = 0;
        IntPtr win = _s.Window != IntPtr.Zero ? _s.Window : EngineFn.SdlGetKeyboardFocus();
        if (win != IntPtr.Zero) { int a, b; EngineFn.SdlGetWindowSize(win, &a, &b); w = a; h = b; }
        if (w <= 0 || h <= 0) MouseLookService.ViewportSize(out w, out h);
    }

    private static int ShowRaw(int toggle) => _s.Next != null ? (*_s.Next)(toggle) : EngineFn.SdlShowCursor(toggle);

    [UnmanagedCallersOnly]
    private static int OnShowCursor(int toggle) => _s.Hold.Held ? _s.Hold.Ask(toggle) : (*_s.Next)(toggle);
}

/// <summary>The engine's cursor wish while a capture hides the cursor: each show or hide is recorded, not done,
/// and the release gives back the last one asked for, or the state the capture began in.</summary>
internal sealed class CursorHold
{
    private int _want;
    /// <summary>A capture is hiding the cursor now.</summary>
    internal bool Held;

    /// <summary>The capture begins; <paramref name="shown"/> is the cursor state it found.</summary>
    internal void Begin(int shown) { Held = true; _want = shown > 0 ? 1 : 0; }

    /// <summary>An <c>SDL_ShowCursor</c> call while held: a show or hide is kept for the release, and every call
    /// answers hidden, as the cursor is.</summary>
    internal int Ask(int toggle)
    {
        if (toggle >= 0) _want = toggle > 0 ? 1 : 0;
        return 0;
    }

    /// <summary>The capture ends: the cursor state to restore.</summary>
    internal int End() { Held = false; return _want; }
}
