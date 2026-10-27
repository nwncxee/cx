using System;
using System.Runtime.InteropServices;
using CX.Alloy;
using CX.Engine;
using CX.Framework;

namespace CX.Services;

/// <summary>The client quitting (<c>RenderInterface::Shutdown</c>, after the frame loop): the Vulkan swapchain and
/// surface are released there, so the driver's exit handler finds nothing to clean against a display SDL has closed.
/// From then on a crash ends the process at once instead of re-entering the engine's crash handler.</summary>
public sealed unsafe class ClientExit : IExtenderService
{
    /// <summary>The shutdown hook's trampoline and the log.</summary>
    private sealed class ClientExitState
    {
        public RiShutdownFn Orig;
        public ExtenderLog? Log;
    }

    private static ClientExitState _s = new();

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        ClientExitState s = _s;
        s.Log = log;
        s.Orig = hooks.InstallRiShutdown(&OnShutdown);
        if (s.Orig == null)
            log.Always("ClientExit: RenderInterface::Shutdown did not hook -- the Vulkan swapchain outlives the window at exit");
        // Linux only: the guard sits on the engine's signal handler; Windows crashes through its exception filter
        if (!OperatingSystem.IsLinux()) return;
        CrashHandlerLinuxFn orig = hooks.InstallCrashHandlerLinux((CrashHandlerLinuxFn)cx_crash_handler_thunk());
        cx_crash_handler_set_orig(orig);
        if (orig == null)
            log.Always("ClientExit: the engine's crash handler did not hook -- a crash inside it can leave the client spinning");
    }

    [UnmanagedCallersOnly]
    private static void OnShutdown(int final)
    {
        ClientExitState s = _s;
        s.Log?.Always("ClientExit: the renderer shuts down -- the client is quitting");
        if (OperatingSystem.IsLinux()) cx_crash_quitting();
        AlloyDevice.VulkanMade?.ReleaseWindow();
        s.Orig(final);
    }

    [DllImport("cxthunk")] private static extern IntPtr cx_crash_handler_thunk();
    [DllImport("cxthunk")] private static extern void cx_crash_handler_set_orig(CrashHandlerLinuxFn orig);
    [DllImport("cxthunk")] private static extern void cx_crash_quitting();
}
