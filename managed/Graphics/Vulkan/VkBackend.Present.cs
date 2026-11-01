using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The game window's surface and swapchain: its images, one ready semaphore per image (a present waits on
    /// the one its image's blit signalled), and what the chain was made for, so a change remakes it.</summary>
    private sealed class SwapState
    {
        public VkSurfaceKHR Surface;
        public VkSwapchainKHR Chain;
        public VkSemaphore[] Ready = [];
        public VkFormat Format;
        public VkPresentModeKHR Mode;
        public int W, H;
        /// <summary>The window changed under the chain (out of date or suboptimal): remade at the next present.</summary>
        public bool Stale, Refused;
        /// <summary>The window has no area (minimised): there is no chain and nothing to show until it has one.</summary>
        public bool Empty;
        public long Presents;
    }

    private readonly SwapState _swap = new();
    /// <summary>The list <see cref="SubmitBlit"/> submits, empty: the present's own work is recorded after it.</summary>
    private CommandList? _presentList;

    /// <summary>A command buffer recorded by <see cref="HoldForPresent"/> and not yet queued: its slot (null when none
    /// is held), the list's name for its log lines, and the list's WhenDone work.</summary>
    private sealed class HeldState
    {
        public Slot? Slot;
        public string Name = "";
        public readonly System.Collections.Generic.List<WhenDoneCall> WhenDone = [];
    }

    private readonly HeldState _held = new();

    /// <summary>The spare screen: laid out as the window's images are, and drawn in their place while there is no
    /// window or its size is not the frame's, then blitted onto one. Its size is the frame's. Null id before the present
    /// target sized it.</summary>
    private ImageId _spare;

    /// <summary>Is the caller on the thread this device belongs to? A caller outside the frame's draw asks before
    /// recording, rather than taking a refusal per call.</summary>
    internal bool OnOwnThread => Environment.CurrentManagedThreadId == _thread;

    /// <summary>The frame at <paramref name="w"/> x <paramref name="h"/>: the spare screen remade at that size in the
    /// window's format, display-encoded as the window shows it; false when the device refused it. The old image dies
    /// after the frames in flight.</summary>
    internal bool Resize(int w, int h)
    {
        return OnThread("Resize") && MakeSpare(w, h);
    }

    private bool MakeSpare(int w, int h)
    {
        if (_scr.Now == _spare) _scr.Now = default;
        Destroy(_spare);
        _spare = CreateImage(new ImageDesc(ImageKind.Tex2D, _scr.Fmt, w, h, 1, 1, 1, 1,
                                               ImageUse.ColorTarget | ImageUse.Sampled | ImageUse.CopySrc | ImageUse.CopyDst, "spare screen"));
        if (_images.TryGet(_spare.Value, out VkImg img)) img.TopDown = true;
        return !_spare.IsNull;
    }

    /// <summary>End the frame: wait for the one <see cref="FramesInFlight"/> back, submit the commands held for it with
    /// the window image's move to presentable, and present it; a spare-screen frame is blitted onto an image acquired now,
    /// and a frame that drew no screen presents nothing. <paramref name="interval"/> (0 off, 1 vsync, -1 adaptive)
    /// picks the next acquire's present mode. False (logged) when the window refused the frame.</summary>
    internal bool Present(int interval, ExtenderLog log)
    {
        if (!OnThread("Present")) return false;
        _scr.Interval = interval;
        VkResult waited = WaitFrame();
        bool ok = waited == VkResult.Success ? ShowScreen(log) : Fault($"Present: the frame {FramesInFlight} back never finished ({waited})");
        SubmitHeld();
        Unacquire();
        EndScreen();
        EndFrame();
        return ok;
    }

    private bool ShowScreen(ExtenderLog log)
    {
        if (_scr.Now.IsNull) return true;
        int idx = _scr.Index;
        if (idx < 0)
        {
            if (!_images.TryGet(_spare.Value, out VkImg src)) return Fault("Present: the spare screen died before its blit");
            if (!EnsureSwapchain(Mode(_scr.Interval), src.Desc.W, src.Desc.H, log)) return _swap.Empty && !_swap.Refused;
            if ((idx = Acquire()) < 0) return false;
        }
        bool sent = _held.Slot != null ? SubmitHeld(idx) : SubmitBlit(idx, log);
        if (!sent) return false;
        VkResult r = QueuePresent(idx);
        if (r == VkResult.ErrorOutOfDateKHR || r == VkResult.SuboptimalKHR) _swap.Stale = true;
        else if (r != VkResult.Success) return Fault($"Present: vkQueuePresentKHR returned {r}");
        _swap.Presents++;
        return true;
    }

    /// <summary>The present's own submit when nothing is held: only the present's work on swapchain image <paramref name="idx"/>.</summary>
    private bool SubmitBlit(int idx, ExtenderLog log)
    {
        CommandList cl = _presentList ??= new CommandList("vulkan present", 256, log);
        cl.Reset();
        return Submit(cl, idx);
    }

    /// <summary>The present's work on swapchain image <paramref name="idx"/>, recorded last in its submit: the frame's
    /// window image on to presentable; or, for a frame drawn into the spare screen, the image from undefined to a copy
    /// target, the spare screen's blit (scaled across a resize; both are top row first in one format), and on to presentable.</summary>
    private bool RecordPresent(int idx)
    {
        if (!_images.TryGet(_scr.Ids[idx].Value, out VkImg dst)) return Fault("Present: the window image died before its present");
        if (idx == _scr.Index)
        {
            Transition(dst, VkImageLayout.PresentSrcKHR);
            return true;
        }
        if (!_images.TryGet(_spare.Value, out VkImg src)) return Fault("Present: the spare screen died before its blit");
        Transition(src, VkImageLayout.TransferSrcOptimal);
        Transition(dst, VkImageLayout.TransferDstOptimal);
        Flush();
        var b = new VkImageBlit
        {
            srcSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, layerCount = 1 },
            dstSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, layerCount = 1 },
        };
        b.srcOffsets[1] = new VkOffset3D { x = src.Desc.W, y = src.Desc.H, z = 1 };
        b.dstOffsets[1] = new VkOffset3D { x = _swap.W, y = _swap.H, z = 1 };
        bool same = src.Desc.W == _swap.W && src.Desc.H == _swap.H;
        _vkd.vkCmdBlitImage(_cmd, src.Image, VkImageLayout.TransferSrcOptimal, dst.Image, VkImageLayout.TransferDstOptimal, 1, &b,
                            same ? VkFilter.Nearest : VkFilter.Linear);
        dst.Written = true;
        Transition(dst, VkImageLayout.PresentSrcKHR);
        return true;
    }

    /// <summary>The engine's swap interval as a present mode: off tears (immediate, else mailbox), adaptive relaxes, any
    /// other waits for the blank. A mode the surface lacks falls to FIFO, which every surface has.</summary>
    private static VkPresentModeKHR Mode(int interval) => interval switch
    {
        0 => VkPresentModeKHR.Immediate,
        < 0 => VkPresentModeKHR.FifoRelaxed,
        _ => VkPresentModeKHR.Fifo,
    };

    /// <summary>The surface on first ask, and a swapchain for <paramref name="mode"/> at the frame's size, remade
    /// when the window went stale or the mode or size changed. A refusal is said once and holds for the session.</summary>
    private bool EnsureSwapchain(VkPresentModeKHR mode, int w, int h, ExtenderLog log)
    {
        SwapState s = _swap;
        if (s.Refused) return false;
        if (s.Chain.IsNotNull && !s.Stale && s.Mode == mode && s.W == w && s.H == h) return true;
        string? why = s.Surface.IsNull ? MakeSurface() : null;
        why ??= MakeSwapchain(mode, w, h, log);
        if (why == null) return s.Chain.IsNotNull;
        s.Refused = true;
        log.Always($"VkBackend: the game window cannot take Vulkan's present -- {why}");
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    /// <summary>The surface over the game's window; null when made, else why not.</summary>
    private string? MakeSurface()
    {
        if (SdlWindow.Native(out IntPtr dpy, out IntPtr win) is string no) return no;
        VkSurfaceKHR surface;
        VkResult r;
        if (Win)
        {
            var ci = new VkWin32SurfaceCreateInfoKHR { hinstance = GetModuleHandleW(null), hwnd = win };
            r = _vki.vkCreateWin32SurfaceKHR(&ci, null, &surface);
        }
        else
        {
            var ci = new VkXlibSurfaceCreateInfoKHR { dpy = dpy, window = (nuint)win };
            r = _vki.vkCreateXlibSurfaceKHR(&ci, null, &surface);
        }
        if (r != VkResult.Success) return $"the window surface was refused ({r})";
        VkBool32 can;
        _vki.vkGetPhysicalDeviceSurfaceSupportKHR(_phys, _family, surface, &can);
        if (!can) { _vki.vkDestroySurfaceKHR(surface, null); return $"queue family {_family} cannot present to the window"; }
        _swap.Surface = surface;
        return null;
    }

    /// <summary>A swapchain for <paramref name="want"/> at the surface's size (the frame's, where the surface
    /// leaves it open), replacing the old one; null when made, else why not. Cold: a resize or a mode change.</summary>
    private string? MakeSwapchain(VkPresentModeKHR want, int w, int h, ExtenderLog log)
    {
        SwapState s = _swap;
        _vkd.vkDeviceWaitIdle();
        VkSurfaceCapabilitiesKHR caps;
        if (_vki.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(_phys, s.Surface, &caps) != VkResult.Success)
            return "the surface's capabilities did not read";
        if (SurfaceRefusal(caps.supportedUsageFlags) is string no) return no;
        VkSurfaceFormatKHR fmt = PickFormat();
        if (fmt.format == VkFormat.Undefined) return "the surface offers no 8-bit UNORM format";
        VkPresentModeKHR mode = HasMode(want) ? want : want == VkPresentModeKHR.Immediate && HasMode(VkPresentModeKHR.Mailbox)
                                                     ? VkPresentModeKHR.Mailbox : VkPresentModeKHR.Fifo;
        VkExtent2D ext = caps.currentExtent.width != uint.MaxValue ? caps.currentExtent
            : new VkExtent2D((uint)Math.Clamp(w, (int)caps.minImageExtent.width, (int)caps.maxImageExtent.width),
                             (uint)Math.Clamp(h, (int)caps.minImageExtent.height, (int)caps.maxImageExtent.height));
        s.Empty = ext.width == 0 || ext.height == 0;
        if (s.Empty) { DropChain(); return null; }
        uint count = Math.Max(caps.minImageCount + 1, SwapImages);
        if (caps.maxImageCount != 0 && count > caps.maxImageCount) count = caps.maxImageCount;
        VkSwapchainKHR old = s.Chain;
        var ci = new VkSwapchainCreateInfoKHR
        {
            surface = s.Surface, minImageCount = count, imageFormat = fmt.format, imageColorSpace = fmt.colorSpace,
            imageExtent = ext, imageArrayLayers = 1, imageUsage = SwapUsage & caps.supportedUsageFlags,
            imageSharingMode = VkSharingMode.Exclusive, preTransform = caps.currentTransform,
            compositeAlpha = (caps.supportedCompositeAlpha & VkCompositeAlphaFlagsKHR.Opaque) != 0
                ? VkCompositeAlphaFlagsKHR.Opaque : VkCompositeAlphaFlagsKHR.Inherit,
            presentMode = mode, clipped = true, oldSwapchain = old,
        };
        VkSwapchainKHR chain;
        VkResult r = _vkd.vkCreateSwapchainKHR(&ci, null, &chain);
        DropChain();
        if (r != VkResult.Success) return $"vkCreateSwapchainKHR returned {r}";
        uint n = 0;
        _vkd.vkGetSwapchainImagesKHR(chain, &n, null);
        var images = new VkImage[n];
        fixed (VkImage* p = images) _vkd.vkGetSwapchainImagesKHR(chain, &n, p);
        s.Chain = chain;
        Format sf = ScreenFormatOf(fmt.format);
        RegisterWindowImages(images, sf, (int)ext.width, (int)ext.height, (caps.supportedUsageFlags & VkImageUsageFlags.TransferSrc) != 0);
        if (sf != _scr.Fmt && _images.TryGet(_spare.Value, out VkImg st))
        {
            _scr.Fmt = sf;
            MakeSpare(st.Desc.W, st.Desc.H);
        }
        s.Ready = new VkSemaphore[n];
        for (int i = 0; i < n; i++) s.Ready[i] = MakeSemaphore();
        s.Format = fmt.format;
        s.Mode = want;
        s.W = (int)ext.width; s.H = (int)ext.height;
        s.Stale = false;
        log.Info($"VkBackend: swapchain {s.W}x{s.H} {fmt.format}, {n} images, {mode} (asked {want}) -- Vulkan presents the frame");
        return null;
    }

    /// <summary>Why a surface whose images support <paramref name="usage"/> cannot take the present; null when it can.
    /// The frame draws into them, and a frame whose size is not the window's is blitted onto them.</summary>
    internal static string? SurfaceRefusal(VkImageUsageFlags usage)
        => (usage & VkImageUsageFlags.ColorAttachment) == 0 ? "the surface's images cannot be drawn into"
         : (usage & VkImageUsageFlags.TransferDst) == 0 ? "the surface's images cannot be blitted into" : null;

    /// <summary>What the window's images are made for: drawn, blitted into, and read back where the surface allows.</summary>
    private const VkImageUsageFlags SwapUsage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferDst | VkImageUsageFlags.TransferSrc;

    /// <summary>The window images asked for: one drawn, one shown, one queued, since the frame holds its image from its
    /// first draw.</summary>
    private const uint SwapImages = 3;

    /// <summary>The surface's 8-bit UNORM format, BGRA or RGBA, in the sRGB-nonlinear space the frame is already
    /// encoded for; an sRGB format would encode it twice. Undefined when it offers neither.</summary>
    private VkSurfaceFormatKHR PickFormat()
    {
        uint n = 0;
        _vki.vkGetPhysicalDeviceSurfaceFormatsKHR(_phys, _swap.Surface, &n, null);
        VkSurfaceFormatKHR* f = stackalloc VkSurfaceFormatKHR[(int)n];
        _vki.vkGetPhysicalDeviceSurfaceFormatsKHR(_phys, _swap.Surface, &n, f);
        for (int i = 0; i < n; i++)
            if (f[i].format is VkFormat.B8G8R8A8Unorm or VkFormat.R8G8B8A8Unorm && f[i].colorSpace == VkColorSpaceKHR.SrgbNonLinear)
                return f[i];
        return default;
    }

    private bool HasMode(VkPresentModeKHR m)
    {
        uint n = 0;
        _vki.vkGetPhysicalDeviceSurfacePresentModesKHR(_phys, _swap.Surface, &n, null);
        VkPresentModeKHR* p = stackalloc VkPresentModeKHR[(int)n];
        _vki.vkGetPhysicalDeviceSurfacePresentModesKHR(_phys, _swap.Surface, &n, p);
        for (int i = 0; i < n; i++) if (p[i] == m) return true;
        return false;
    }

    private VkSemaphore MakeSemaphore()
    {
        var ci = new VkSemaphoreCreateInfo();
        VkSemaphore s;
        Check(_vkd.vkCreateSemaphore(&ci, &s), "vkCreateSemaphore");
        return s;
    }

    /// <summary>The swapchain and its semaphores. The caller has waited the device idle.</summary>
    private void DropChain()
    {
        SwapState s = _swap;
        DropWindowImages();
        foreach (VkSemaphore r in s.Ready) _vkd.vkDestroySemaphore(r);
        if (s.Chain.IsNotNull) _vkd.vkDestroySwapchainKHR(s.Chain, null);
        s.Chain = default;
        s.Ready = [];
    }

    /// <summary>The swapchain and the surface, at disposal.</summary>
    private void DropSwapchain()
    {
        DropChain();
        if (_swap.Surface.IsNotNull) _vki.vkDestroySurfaceKHR(_swap.Surface, null);
        _swap.Surface = default;
    }

    /// <summary>The window is being destroyed: the swapchain and the surface over it go now, while its display is still
    /// open, rather than in the driver's exit handler after SDL has closed it. A later present makes them again.</summary>
    internal void ReleaseWindow()
    {
        if (!OnThread("releasing the window") || _swap.Surface.IsNull) return;
        _vkd.vkDeviceWaitIdle();
        DropSwapchain();
        _log.Always($"VkBackend: the window is going -- its swapchain and surface released after {_swap.Presents} present(s)");
    }

    /// <summary>Record <paramref name="list"/> now into a command buffer the present ends: its commands, then the
    /// present's own work, in one submit. The next submit of any other list, a wait, or a present that shows nothing queues
    /// it first on its own, so nothing recorded after it sees the screen without it. False (logged) when it did not record.</summary>
    internal bool HoldForPresent(CommandList list)
    {
        if (!OnThread("HoldForPresent")) return false;
        if (_inWhenDone) return Fault($"list {list.Name} was held from WhenDone work, which must not submit");
        SubmitHeld();
        if (list.Async) return Fault($"list {list.Name} is for the async queue; only a graphics list rides the present");
        if (Begin(list, false) is not Slot s) return false;
        if (!Body(list))
        {
            Finish(s, list.Name, lane: false, ok: false, swap: -1, beside: false, committed: false);
            CarryWhenDone(list, false, false);
            RunWhenDone();
            return false;
        }
        // the uploads at its head are its own: one queued after the hold waits for the submit after it
        CommitUploads();
        list.TakeWhenDone(_held.WhenDone);
        _held.Slot = s;
        _held.Name = list.Name;
        return true;
    }

    /// <summary>The held command buffer queued, if one is: before the present's work on swapchain image
    /// <paramref name="swap"/> at or past 0, on its own below it. Its WhenDone work goes on the submit's value, or is told
    /// it never ran. True when nothing was held.</summary>
    private bool SubmitHeld(int swap = -1)
    {
        if (_held.Slot is not Slot s) return true;
        _held.Slot = null;
        bool ran = Finish(s, _held.Name, lane: false, ok: true, swap, beside: false, committed: true);
        long at = ran ? _submits : -1;
        foreach (WhenDoneCall c in _held.WhenDone) _whenDone.Add((at, c));
        _held.WhenDone.Clear();
        RunWhenDone();
        return ran;
    }

    /// <summary>Presents so far, for the status line.</summary>
    internal long Presents => _swap.Presents;
}
