using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>Alloy owns the screen's clears and its present once the window is known: Vulkan presents every frame and
/// GL's swap never runs. The engine's screen clears are swallowed at <c>RenderInterface::Clear</c> when they
/// target the default framebuffer; the present is <c>SDL_GL_SwapWindow</c> or Vulkan's, then <c>ResetRenderStates</c>.</summary>
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

    /// <summary>The Vulkan present count, and whether a refused present has been said.</summary>
    private sealed class VkPresentState { public long Presents, Refused, Blank; }

    private static readonly VkPresentState _vk = new();
    private static long _swallowed, _passed;
    private static bool _said;

    /// <summary>An engine draw that owns the whole screen is running (a movie frame handed back to the engine's quad):
    /// its clears are its own, and so is its draw -- <see cref="RspShim"/> lets that one through.</summary>
    internal static bool EngineFrame;

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

    /// <summary>Is the screen Alloy's this frame? Whenever Alloy draws and the window is known: nothing the engine
    /// draws in GL reaches the screen.</summary>
    internal static bool Owned => _origClear != null && _swap != null && Renderer.On && FramePump.Available;

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

    /// <summary>The present, at the engine's own swap point -- where the GUI hand-back and every engine draw
    /// landed. False = not Alloy's to present; the caller runs the engine's <c>GLRender::SwapBuffers</c>.</summary>
    internal static bool Present(IntPtr window)
    {
        return Owned && PresentVulkan();
    }

    /// <summary>Vulkan's present of the composite, then the engine's state reset the swap was wrapped in. A refusal
    /// fails Alloy loudly and nothing is presented: GL's swap would show a back buffer nothing on Vulkan drew.</summary>
    private static bool PresentVulkan()
    {
        // the frames before the first flush sizes the composite: nothing to show yet, and nothing GL drew may stand in
        if (_log != null && AlloyDevice.Vulkan(_log) is VkBackend v && v.Composite.IsNull)
        {
            if (_vk.Blank++ == 0) _log.Info("ScreenOwner: a frame came before Vulkan's composite was sized -- nothing is presented until it is");
            _reset();
            return true;
        }
        if (_log == null || AlloyDevice.Vulkan(_log) is not VkBackend vk || !vk.Present(_interval != null ? _interval() : 1, _log))
        {
            AlloyGate.Fail("Vulkan could not present the frame to the window (logged above) -- the window keeps its last frame");
            _vk.Refused++;
            _reset();
            return true;
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
        => _log != null && AlloyDevice.Vulkan(_log) is VkBackend vk && !vk.Composite.IsNull && vk.Present(_interval != null ? _interval() : 1, _log);

    internal static string Stats() => _origClear == null ? "screen: engine (not armed)"
        // The clear colour is on the line because it IS the void: every pixel the world does not cover ends up
        // this colour, and nothing else in a session reports it.
        : $"screen: {(Owned ? "Alloy" : "engine")} -- {_vk.Presents} Vulkan presents"
        + (_vk.Refused == 0 ? "" : $" ({_vk.Refused} REFUSED, nothing shown)") + (_vk.Blank == 0 ? "" : $" ({_vk.Blank} before the composite, nothing shown)") + $", {_swallowed} engine clears swallowed, "
        + $"{_passed} passed, clear=({ClearRgba[0]:F3},{ClearRgba[1]:F3},{ClearRgba[2]:F3})";
}
