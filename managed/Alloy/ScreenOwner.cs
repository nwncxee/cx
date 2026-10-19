using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Alloy owns the screen's clears and its present once the GUI is Alloy's and no engine-drawn world
/// needs the engine's own. The engine's screen clears are swallowed at <c>RenderInterface::Clear</c> when they
/// target the default framebuffer -- one aimed at an engine framebuffer object passes; the present is
/// <c>SDL_GL_SwapWindow</c> from here, between the bind and <c>ResetRenderStates</c> the engine wraps it in.</summary>
internal static unsafe class ScreenOwner
{
    private static ExtenderLog? _log;
    private static RiClearFn _origClear;
    private static RiSetClearColorFn _origClearColor;
    private static SdlGlSwapWindowFn _swap;
    private static RiResetRenderStatesFn _reset;
    private static uint* _defaultFbo, _drawBound;
    private static long _swallowed, _passed, _presents;
    private static bool _said;

    /// <summary>An engine draw that owns the whole screen is running (a movie frame handed back to the engine's quad):
    /// its clears are its own, and so is its draw -- <see cref="RspShim"/> lets that one through.</summary>
    internal static bool EngineFrame;

    /// <summary>What <c>GLRender::BindDefaultFramebuffer</c> binds -- the screen; 0 unless the engine renders
    /// through one of its own, and 0 before the caches resolve, which is what it would have bound anyway.</summary>
    internal static uint DefaultFbo => _defaultFbo != null ? *_defaultFbo : 0u;

    /// <summary>The engine's last <c>glClearColor</c>: the fog colour in an area, black at the menus.</summary>
    internal static readonly float[] ClearRgba = { 0f, 0f, 0f, 1f };

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _swap = EngineFn.SdlGlSwapWindow;
        _reset = EngineFn.RiResetRenderStates;
        _defaultFbo = (uint*)Native.Sym(SymbolMap.GlDefaultFramebuffer);
        _drawBound = (uint*)Native.Sym(SymbolMap.GlDrawFramebufferBound);
        if (_swap == null || _reset == null || _defaultFbo == null || _drawBound == null)
        {
            log.Always("ScreenOwner: SDL_GL_SwapWindow, ResetRenderStates or the framebuffer caches did not resolve -- "
                     + "the engine keeps its clears and its present (CX-101 not armed)");
            return;
        }
        _origClearColor = hooks.InstallRiSetClearColor(&OnSetClearColor);
        _origClear = hooks.InstallRiClear(&OnClear);
        if (_origClear == null || _origClearColor == null)
            log.Always("ScreenOwner: RenderInterface::Clear/SetClearColor did not hook -- the engine keeps its clears");
    }

    /// <summary>Is the screen Alloy's this frame? The GUI is Alloy's and the world, if there is one, is too -- a
    /// vanilla world frame (an Incapable verdict) draws through the engine's own clears.</summary>
    internal static bool Owned => _origClear != null && _swap != null && GuiPass.Capturing
                                  && (AlloyGate.Alloy || AlloyGate.Stand == AlloyStand.Idle);

    [UnmanagedCallersOnly]
    private static void OnClear(uint mask)
    {
        if (Owned && !EngineFrame && *_drawBound == *_defaultFbo) { _swallowed++; return; }
        _passed++;
        _origClear(mask);
    }

    [UnmanagedCallersOnly]
    private static void OnSetClearColor(float r, float g, float b, float a)
    {
        ClearRgba[0] = r; ClearRgba[1] = g; ClearRgba[2] = b; ClearRgba[3] = a;
        _origClearColor(r, g, b, a);
    }

    /// <summary>The present, in the ENGINE context -- the one whose back buffer the GUI hand-back and every
    /// engine-context draw landed in, which on Windows is not the 4.6 context's. False = not Alloy's to present;
    /// the caller runs the engine's <c>GLRender::SwapBuffers</c>.</summary>
    internal static bool Present(IntPtr window)
    {
        if (!Owned) return false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, *_defaultFbo);
        _swap(window);
        _reset();
        _presents++;
        if (!_said)
        {
            _said = true;
            _log?.Info("ScreenOwner: LIVE -- Alloy issues the present and the screen clears; the engine's "
                     + "GLRender::SwapBuffers and its default-framebuffer clears no longer run");
        }
        return true;
    }

    internal static string Stats() => _origClear == null ? "screen: engine (not armed)"
        // The clear colour is on the line because it IS the void: every pixel the world does not cover ends up
        // this colour, and nothing else in a session reports it.
        : $"screen: {(Owned ? "Alloy" : "engine")} -- {_presents} Alloy presents, {_swallowed} engine clears swallowed, "
        + $"{_passed} passed, clear=({ClearRgba[0]:F3},{ClearRgba[1]:F3},{ClearRgba[2]:F3}), {RspShim.StrayDraws} stray engine draws";
}
