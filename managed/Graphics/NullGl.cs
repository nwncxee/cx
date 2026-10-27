using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Graphics;

/// <summary>
/// A Vulkan session runs the engine with no GL context. Decided once at <c>SDL_CreateWindow</c>: the window becomes a
/// Vulkan window, <c>SDL_GL_CreateContext</c> answers with a token, and every GL entry nwmain imports or GLEW resolves
/// is a CX stub (<see cref="Patch"/>). The engine keeps running its GL-shaped code against nothing.
/// </summary>
public sealed unsafe partial class NullGl : IExtenderService
{
    /// <summary>The session's decision, the SDL originals, and what the stubs remember.</summary>
    private sealed class NullGlState
    {
        public ExtenderLog? Log;
        public bool Decided, Active;
        public SdlCreateWindowFn OrigWindow;
        public SdlGlCreateContextFn OrigContext;
        public SdlGlSetSwapIntervalFn OrigSetInterval;
        public SdlGlGetSwapIntervalFn OrigGetInterval;
        public SdlGlGetDrawableSizeFn OrigDrawable;
        public RiReadPixelsFromBufferFn OrigReadPixels;
        public int SwapInterval;
        public int Patched, Generic;
        public readonly StubState Gl = new();
        public readonly HashSet<long> Said = new();
    }

    private static NullGlState _s = new();

    /// <summary>SDL's window flags: the GL one the engine asks for, and the Vulkan one a session with no GL takes instead.</summary>
    private const uint WindowOpenGl = 0x2, WindowVulkan = 0x10000000;
    /// <summary>What <c>SDL_GL_CreateContext</c> hands the engine when there is no context: non-null, never dereferenced.</summary>
    private static readonly IntPtr ContextToken = 0xC0DE;

    /// <summary>This session has no GL context: nothing may issue GL, and the engine's GL is CX's stubs.</summary>
    internal static bool Active => _s.Active;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        NullGlState s = _s;
        s.Log = log;
        s.OrigWindow = hooks.InstallSdlCreateWindow(&OnCreateWindow);
        s.OrigContext = hooks.InstallSdlGlCreateContext(&OnCreateContext);
        s.OrigSetInterval = hooks.InstallSdlGlSetSwapInterval(&OnSetSwapInterval);
        s.OrigGetInterval = hooks.InstallSdlGlGetSwapInterval(&OnGetSwapInterval);
        s.OrigDrawable = hooks.InstallSdlGlGetDrawableSize(&OnGetDrawableSize);
        s.OrigReadPixels = hooks.InstallRiReadPixelsFromBuffer(&OnReadPixels);
        if (s.OrigWindow == null || s.OrigContext == null || s.OrigSetInterval == null || s.OrigGetInterval == null
            || s.OrigDrawable == null)
            log.Always("NullGl: SDL_CreateWindow / SDL_GL_CreateContext / the swap-interval or drawable calls did not hook -- "
                     + "a Vulkan session keeps the engine's GL context");
        SelfCheck(log);
    }

    /// <summary>The one decision: Vulkan with Alloy on and a device that passed the probe takes the engine's GL away.
    /// Anything else keeps the engine's context, and says why.</summary>
    private static bool Decide(NullGlState s)
    {
        ExtenderLog log = s.Log!;
        s.Decided = true;
        if (!CX.Alloy.Renderer.OnVulkan) return false;
        if (s.OrigContext == null || s.OrigSetInterval == null || s.OrigGetInterval == null || s.OrigDrawable == null)
            return false;
        if (!CX.Alloy.Renderer.On)
        {
            log.Always("NullGl: Alloy is off this session (alloy.enabled=0, or Vulkan refused) -- the engine draws, so it keeps its GL context");
            return false;
        }
        if (VkBackend.Refusal(log) is string why)
        {
            log.Always($"NullGl: the Vulkan probe refused ({why}) -- the engine keeps its GL context this session");
            return false;
        }
        string? patched = Patch(s);
        if (patched != null)
        {
            log.Always($"NullGl: the engine's GL imports could not be redirected ({patched}) -- it keeps its GL context this session");
            return false;
        }
        s.Gl.Arm(VkBackend.ProbedVendor, VkBackend.ProbedDevice);
        log.Always($"NullGl: NO GL CONTEXT this session -- the window is a Vulkan window, {s.Patched} GL import(s) of nwmain and "
                 + $"every GLEW entry answer from CX's stubs (vendor '{VkBackend.ProbedVendor}', '{VkBackend.ProbedDevice}')");
        return true;
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnCreateWindow(byte* title, int x, int y, int w, int h, uint flags)
    {
        NullGlState s = _s;
        if (!s.Decided) s.Active = Decide(s);
        if (s.Active) flags = (flags & ~WindowOpenGl) | WindowVulkan;
        IntPtr window = s.OrigWindow(title, x, y, w, h, flags);
        if (s.Active && window == IntPtr.Zero)
            s.Log?.Always("NullGl: SDL_CreateWindow refused the Vulkan window -- the client has no window to draw to");
        return window;
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnCreateContext(IntPtr window)
    {
        NullGlState s = _s;
        return s.Active ? ContextToken : s.OrigContext(window);
    }

    [UnmanagedCallersOnly]
    private static int OnSetSwapInterval(int interval)
    {
        NullGlState s = _s;
        if (!s.Active) return s.OrigSetInterval(interval);
        s.SwapInterval = interval;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int OnGetSwapInterval()
    {
        NullGlState s = _s;
        return s.Active ? s.SwapInterval : s.OrigGetInterval();
    }

    [UnmanagedCallersOnly]
    private static void OnGetDrawableSize(IntPtr window, int* w, int* h)
    {
        NullGlState s = _s;
        if (!s.Active) { s.OrigDrawable(window, w, h); return; }
        SdlVulkanGetDrawableSizeFn vk = EngineFn.SdlVulkanGetDrawableSize;
        if (vk != null) vk(window, w, h);
        else { if (w != null) *w = 0; if (h != null) *h = 0; }
    }

    /// <summary>First time for <paramref name="key"/>: each non-trivial stub says its first answer, and only that.</summary>
    private static bool Fresh(long key)
    {
        NullGlState s = _s;
        lock (s.Said) return s.Said.Add(key);
    }

    private static void Say(string msg) => _s.Log?.Always("NullGl: " + msg);

    /// <summary>The <c>/alloy</c> line: whether the session has a GL context and what the stubs have held.</summary>
    internal static string Status()
    {
        NullGlState s = _s;
        return !s.Decided ? "NullGl: undecided (no window yet)"
            : !s.Active ? "NullGl: the engine has its GL context"
            : $"NullGl: no GL context; {s.Patched} import(s) redirected, {s.Gl.Textures.Count} engine texture(s) held, "
            + $"{s.Gl.Buffers.Count} engine buffer(s) in host memory";
    }

    /// <summary>The swap interval the engine asked for, for the Vulkan present.</summary>
    internal static int SwapInterval => _s.SwapInterval;
}
