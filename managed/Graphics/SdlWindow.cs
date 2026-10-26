using System;
using CX.Engine;

namespace CX.Graphics;

/// <summary>The game's SDL window, as the engine's swap names it, and its native handles for a Vulkan surface --
/// asked of SDL, so no GL context is needed to find the window.</summary>
internal static unsafe class SdlWindow
{
    /// <summary>The window the engine last presented.</summary>
    private sealed class SdlWindowState { public IntPtr Window; }

    private static SdlWindowState _s = new();

    /// <summary><c>SDL_SYSWM_WINDOWS</c> and <c>SDL_SYSWM_X11</c>, <c>SDL_SysWMinfo.subsystem</c>'s values.</summary>
    private const int SysWmWindows = 1, SysWmX11 = 2;

    /// <summary><c>SDL_SysWMinfo</c>: the version SDL checks, the subsystem, and the union of handles (X11: display,
    /// window; Windows: HWND first), padded past the union's 64 bytes.</summary>
    private struct SysWmInfo
    {
        public byte Major, Minor, Patch;
        public int Subsystem;
        public IntPtr H0, H1;
        public UnionTail Rest;
    }

    /// <summary>The union's remaining 48 bytes past the two handles read here, which SDL may write.</summary>
    [System.Runtime.CompilerServices.InlineArray(6)]
    private struct UnionTail { private long _e; }

    /// <summary>The engine's swap names its window: the one every present goes to.</summary>
    internal static void Note(IntPtr window) { if (window != IntPtr.Zero) _s.Window = window; }

    /// <summary>The X display and window (Windows: no display, the HWND), or why not.</summary>
    internal static string? Native(out IntPtr display, out IntPtr window)
    {
        display = window = IntPtr.Zero;
        if (_s.Window == IntPtr.Zero) return "the engine has presented no window yet";
        SdlGetVersionFn ver = EngineFn.SdlGetVersion;
        SdlGetWindowWmInfoFn info = EngineFn.SdlGetWindowWmInfo;
        if (ver == null || info == null) return "SDL_GetVersion/SDL_GetWindowWMInfo did not resolve";
        SysWmInfo w = default;
        ver(&w.Major);
        if (info(_s.Window, (byte*)&w) == 0) return "SDL_GetWindowWMInfo refused the window";
        if (w.Subsystem != (OperatingSystem.IsWindows() ? SysWmWindows : SysWmX11))
            return $"the window is on SDL subsystem {w.Subsystem}, not {(OperatingSystem.IsWindows() ? "Windows" : "X11")}";
        if (OperatingSystem.IsWindows()) window = w.H0;
        else { display = w.H0; window = w.H1; }
        return window == IntPtr.Zero ? "SDL named a null window handle" : null;
    }
}
