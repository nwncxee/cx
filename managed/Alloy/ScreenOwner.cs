using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>Alloy owns the screen's clears and its present once the GUI is Alloy's and no engine-drawn world
/// needs the engine's own. The engine's screen clears are swallowed at <c>RenderInterface::Clear</c> when they
/// target the default framebuffer -- one aimed at an engine framebuffer object passes; the present is
/// <c>SDL_GL_SwapWindow</c> from here, or under <c>alloy.backend vk</c> Vulkan's own, then <c>ResetRenderStates</c>.</summary>
internal static unsafe class ScreenOwner
{
    private static ExtenderLog? _log;
    private static RiClearFn _origClear;
    private static RiSetClearColorFn _origClearColor;
    private static SdlGlSwapWindowFn _swap;
    private static SdlGlGetSwapIntervalFn _interval;
    private static RiResetRenderStatesFn _reset;
    private static uint* _defaultFbo;
    /// <summary><c>GLRender::BindFramebuffer</c>'s two binding caches, resolved at install; null where one did not.</summary>
    private sealed unsafe class FboCacheState { public uint* Draw, Read; }

    private static readonly FboCacheState _cache = new();

    /// <summary>This frame's screen is GL's under <c>alloy.backend vk</c> -- the engine drew a movie frame itself -- so
    /// the GL swap presents it; and the Vulkan present count.</summary>
    private sealed class VkPresentState { public bool GlScreen; public long Presents, GlFrames; }

    private static readonly VkPresentState _vk = new();

    /// <summary>The engine drew this frame's screen in GL (a movie Alloy did not take): the GL swap presents it, even on Vulkan.</summary>
    internal static void GlDrewScreen() => _vk.GlScreen = true;
    private static long _swallowed, _passed, _presents;
    private static bool _said;

    /// <summary>An engine draw that owns the whole screen is running (a movie frame handed back to the engine's quad):
    /// its clears are its own, and so is its draw -- <see cref="RspShim"/> lets that one through.</summary>
    internal static bool EngineFrame;

    /// <summary>Put the bindings <c>GLRender::BindFramebuffer</c>'s two caches name back into real GL, after Alloy's own
    /// framebuffer binds in the ENGINE context: the cache early-outs on a match, so a stale real binding would take the
    /// engine's next draw. An unresolved read cache is left alone; Install refuses without the draw one.</summary>
    internal static void RestoreEngineFbo()
    {
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _cache.Draw != null ? *_cache.Draw : 0u);
        if (_cache.Read != null) Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, *_cache.Read);
    }

    /// <summary>The engine's last <c>glClearColor</c>: the fog colour in an area, black at the menus.</summary>
    internal static readonly float[] ClearRgba = { 0f, 0f, 0f, 1f };

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _swap = EngineFn.SdlGlSwapWindow;
        _interval = EngineFn.SdlGlGetSwapInterval;
        _reset = EngineFn.RiResetRenderStates;
        _defaultFbo = (uint*)Native.Sym(SymbolMap.GlDefaultFramebuffer);
        _cache.Draw = (uint*)Native.Sym(SymbolMap.GlDrawFramebufferBound);
        _cache.Read = (uint*)Native.Sym(SymbolMap.GlReadFramebufferBound);
        if (_swap == null || _reset == null || _defaultFbo == null || _cache.Draw == null)
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
        if (Owned && !EngineFrame && *_cache.Draw == *_defaultFbo) { _swallowed++; return; }
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
        bool glScreen = _vk.GlScreen;
        _vk.GlScreen = false;
        if (Renderer.OnVulkan && !glScreen) return PresentVulkan();
        if (glScreen) _vk.GlFrames++;
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

    /// <summary>Vulkan's present of the composite, then the engine's state reset the swap was wrapped in. A refusal
    /// fails Alloy loudly and the engine's swap shows this frame.</summary>
    private static bool PresentVulkan()
    {
        if (_log == null || AlloyDevice.Vulkan(_log) is not VkBackend vk || !vk.Present(_interval != null ? _interval() : 1, _log))
        {
            AlloyGate.Fail("Vulkan could not present the frame to the window (logged above)");
            return false;
        }
        _reset();
        _vk.Presents++;
        if (!_said)
        {
            _said = true;
            _log.Info("ScreenOwner: LIVE -- Vulkan presents the frame and Alloy swallows the screen clears; the engine's "
                    + "GLRender::SwapBuffers, its default-framebuffer clears and the GL swap no longer run");
        }
        return true;
    }

    /// <summary>Vulkan presents the composite now, outside the frame's own present: a notice put up ahead of a blocking
    /// compile. False (logged by the device) when it could not.</summary>
    internal static bool PresentVulkanNow()
        => _log != null && AlloyDevice.Vulkan(_log) is VkBackend vk && vk.Present(_interval != null ? _interval() : 1, _log);

    internal static string Stats() => _origClear == null ? "screen: engine (not armed)"
        // The clear colour is on the line because it IS the void: every pixel the world does not cover ends up
        // this colour, and nothing else in a session reports it.
        : $"screen: {(Owned ? "Alloy" : "engine")} -- {_presents} Alloy GL presents, {_vk.Presents} Vulkan presents"
        + (_vk.GlFrames == 0 ? "" : $" ({_vk.GlFrames} engine-drawn frames on GL's swap)") + $", {_swallowed} engine clears swallowed, "
        + $"{_passed} passed, clear=({ClearRgba[0]:F3},{ClearRgba[1]:F3},{ClearRgba[2]:F3})";
}
