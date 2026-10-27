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
        public VkImage[] Images = [];
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
    /// <summary>One acquire semaphore per submit slot: the slot's fence proves the submit that waited on it is done.</summary>
    private readonly VkSemaphore[] _acquired = new VkSemaphore[Slots];
    /// <summary>The present's own submit: no pass, only the composite's blit onto the acquired image.</summary>
    private CommandList? _presentList;

    /// <summary>The image the frame renders into and the GUI draws over, and what <see cref="Present"/> shows. Null id
    /// before the present target sized it.</summary>
    private ImageId _composite;

    /// <inheritdoc cref="_composite"/>
    internal ImageId Composite => _composite;

    /// <summary>Is the caller on the thread this device belongs to? A caller outside the frame's draw asks before
    /// recording, rather than taking a refusal per call.</summary>
    internal bool OnOwnThread => Environment.CurrentManagedThreadId == _thread;

    /// <summary>The composite at <paramref name="w"/> x <paramref name="h"/>, RGBA8 display-encoded as the window shows
    /// it; false when the device refused it. The old image dies after the frames in flight.</summary>
    internal bool Resize(int w, int h)
    {
        if (!OnThread("Resize")) return false;
        Destroy(_composite);
        _composite = CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, w, h, 1, 1, 1, 1,
                                               ImageUse.ColorTarget | ImageUse.Sampled | ImageUse.CopySrc | ImageUse.CopyDst, "composite"));
        return !_composite.IsNull;
    }

    /// <summary>Put the composite on the window: acquire a swapchain image, blit the composite onto it in a submit of
    /// its own and present it. <paramref name="interval"/> is the engine's swap interval (0 off, 1 vsync, -1 adaptive),
    /// which picks the present mode. False (logged) when there is no composite or the window refused the frame.</summary>
    internal bool Present(int interval, ExtenderLog log)
    {
        if (!OnThread("Present")) return false;
        if (!_images.TryGet(_composite.Value, out VkImg src)) return Fault("Present: there is no composite image");
        if (!EnsureSwapchain(Mode(interval), src.Desc.W, src.Desc.H, log)) return _swap.Empty && !_swap.Refused;
        int slot = (int)(_submits % Slots);
        VkFence fence = _slots[slot].Fence;
        // the acquire below signals this slot's semaphore, which the slot's last submit may still be waiting on
        if (_vkd.vkWaitForFences(1, &fence, true, FenceTimeoutNs) != VkResult.Success)
            return Fault("Present: the previous submit on this slot never finished");
        uint idx = 0;
        VkResult r = _vkd.vkAcquireNextImageKHR(_swap.Chain, FenceTimeoutNs, _acquired[slot], default, &idx);
        if (r == VkResult.ErrorOutOfDateKHR)
        {
            _swap.Stale = true;
            if (!EnsureSwapchain(_swap.Mode, src.Desc.W, src.Desc.H, log)) return _swap.Empty && !_swap.Refused;
            r = _vkd.vkAcquireNextImageKHR(_swap.Chain, FenceTimeoutNs, _acquired[slot], default, &idx);
        }
        if (r != VkResult.Success && r != VkResult.SuboptimalKHR) return Fault($"Present: vkAcquireNextImageKHR returned {r}");
        CommandList cl = _presentList ??= new CommandList("vulkan present", 256, log);
        cl.Reset();
        if (!Submit(cl, (int)idx)) return false;
        VkSemaphore ready = _swap.Ready[idx];
        VkSwapchainKHR chain = _swap.Chain;
        var pi = new VkPresentInfoKHR
        {
            waitSemaphoreCount = 1, pWaitSemaphores = &ready, swapchainCount = 1, pSwapchains = &chain, pImageIndices = &idx,
        };
        r = _vkd.vkQueuePresentKHR(_queue, &pi);
        if (r == VkResult.ErrorOutOfDateKHR || r == VkResult.SuboptimalKHR) _swap.Stale = true;
        else if (r != VkResult.Success) return Fault($"Present: vkQueuePresentKHR returned {r}");
        _swap.Presents++;
        return true;
    }

    /// <summary>The composite's blit onto acquired swapchain image <paramref name="idx"/>, recorded last in the present's
    /// submit: the image from undefined to a copy target, the blit (which flips GL's rows, converts RGBA to the
    /// window's order and scales across a resize), and the image on to presentable.</summary>
    private bool RecordPresent(int idx)
    {
        if (!_images.TryGet(_composite.Value, out VkImg src)) return Fault("Present: the composite died before its blit");
        Transition(src, VkImageLayout.TransferSrcOptimal);
        VkImage dst = _swap.Images[idx];
        SwapBarrier(dst, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal);
        var b = new VkImageBlit
        {
            srcSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, layerCount = 1 },
            dstSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.Color, layerCount = 1 },
        };
        // the composite holds GL's rows, bottom row first; the window shows row 0 at the top, so the blit flips
        b.srcOffsets[0] = new VkOffset3D { x = 0, y = src.Desc.H, z = 0 };
        b.srcOffsets[1] = new VkOffset3D { x = src.Desc.W, y = 0, z = 1 };
        b.dstOffsets[1] = new VkOffset3D { x = _swap.W, y = _swap.H, z = 1 };
        bool same = src.Desc.W == _swap.W && src.Desc.H == _swap.H;
        _vkd.vkCmdBlitImage(_cmd, src.Image, VkImageLayout.TransferSrcOptimal, dst, VkImageLayout.TransferDstOptimal, 1, &b,
                            same ? VkFilter.Nearest : VkFilter.Linear);
        SwapBarrier(dst, VkImageLayout.TransferDstOptimal, VkImageLayout.PresentSrcKHR);
        return true;
    }

    private void SwapBarrier(VkImage image, VkImageLayout from, VkImageLayout to)
    {
        var b = new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.AllCommands, srcAccessMask = VkAccessFlags2.MemoryWrite,
            dstStageMask = VkPipelineStageFlags2.AllCommands, dstAccessMask = VkAccessFlags2.MemoryRead | VkAccessFlags2.MemoryWrite,
            oldLayout = from, newLayout = to, srcQueueFamilyIndex = QueueFamilyIgnored, dstQueueFamilyIndex = QueueFamilyIgnored,
            image = image,
            subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.Color, levelCount = 1, layerCount = 1 },
        };
        var di = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &b };
        _vkd.vkCmdPipelineBarrier2(_cmd, &di);
    }

    /// <summary>The engine's swap interval as a present mode: off tears (immediate, else mailbox), adaptive relaxes, any
    /// other waits for the blank. A mode the surface lacks falls to FIFO, which every surface has.</summary>
    private static VkPresentModeKHR Mode(int interval) => interval switch
    {
        0 => VkPresentModeKHR.Immediate,
        < 0 => VkPresentModeKHR.FifoRelaxed,
        _ => VkPresentModeKHR.Fifo,
    };

    /// <summary>The surface on first ask, and a swapchain for <paramref name="mode"/> at the composite's size, remade
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

    /// <summary>A swapchain for <paramref name="want"/> at the surface's size (the composite's, where the surface
    /// leaves it open), replacing the old one; null when made, else why not. Cold: a resize or a mode change.</summary>
    private string? MakeSwapchain(VkPresentModeKHR want, int w, int h, ExtenderLog log)
    {
        SwapState s = _swap;
        _vkd.vkDeviceWaitIdle();
        VkSurfaceCapabilitiesKHR caps;
        if (_vki.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(_phys, s.Surface, &caps) != VkResult.Success)
            return "the surface's capabilities did not read";
        if ((caps.supportedUsageFlags & VkImageUsageFlags.TransferDst) == 0) return "the surface's images cannot be blitted into";
        VkSurfaceFormatKHR fmt = PickFormat();
        if (fmt.format == VkFormat.Undefined) return "the surface offers no 8-bit UNORM format";
        VkPresentModeKHR mode = HasMode(want) ? want : want == VkPresentModeKHR.Immediate && HasMode(VkPresentModeKHR.Mailbox)
                                                     ? VkPresentModeKHR.Mailbox : VkPresentModeKHR.Fifo;
        VkExtent2D ext = caps.currentExtent.width != uint.MaxValue ? caps.currentExtent
            : new VkExtent2D((uint)Math.Clamp(w, (int)caps.minImageExtent.width, (int)caps.maxImageExtent.width),
                             (uint)Math.Clamp(h, (int)caps.minImageExtent.height, (int)caps.maxImageExtent.height));
        s.Empty = ext.width == 0 || ext.height == 0;
        if (s.Empty) { DropChain(); return null; }
        uint count = caps.minImageCount + 1;
        if (caps.maxImageCount != 0 && count > caps.maxImageCount) count = caps.maxImageCount;
        VkSwapchainKHR old = s.Chain;
        var ci = new VkSwapchainCreateInfoKHR
        {
            surface = s.Surface, minImageCount = count, imageFormat = fmt.format, imageColorSpace = fmt.colorSpace,
            imageExtent = ext, imageArrayLayers = 1, imageUsage = VkImageUsageFlags.TransferDst,
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
        s.Images = images;
        s.Ready = new VkSemaphore[n];
        for (int i = 0; i < n; i++) s.Ready[i] = MakeSemaphore();
        s.Format = fmt.format;
        s.Mode = want;
        s.W = (int)ext.width; s.H = (int)ext.height;
        s.Stale = false;
        log.Info($"VkBackend: swapchain {s.W}x{s.H} {fmt.format}, {n} images, {mode} (asked {want}) -- Vulkan presents the frame");
        return null;
    }

    /// <summary>The surface's 8-bit UNORM format, BGRA or RGBA, in the sRGB-nonlinear space the composite is already
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
        foreach (VkSemaphore r in s.Ready) _vkd.vkDestroySemaphore(r);
        if (s.Chain.IsNotNull) _vkd.vkDestroySwapchainKHR(s.Chain, null);
        s.Chain = default;
        s.Images = [];
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

    /// <summary>Presents so far, for the status line.</summary>
    internal long Presents => _swap.Presents;
}
