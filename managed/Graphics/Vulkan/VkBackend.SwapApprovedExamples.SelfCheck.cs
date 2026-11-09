using CX.Framework;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Approved-spec: which chain the surface's extent leaves standing, and the extent a new one takes.
    /// Oracles: two hand-written capability tables -- one dictating 2544x1378, one leaving its extent open between
    /// 640x480 and 1600x900 -- with the frame sizes and present modes written out beside them.</summary>
    internal static void SwapSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var tiled = new VkSurfaceCapabilitiesKHR
        {
            currentExtent = new VkExtent2D(2544, 1378),
            minImageExtent = new VkExtent2D(1, 1), maxImageExtent = new VkExtent2D(4096, 4096),
        };
        var open = new VkSurfaceCapabilitiesKHR
        {
            currentExtent = new VkExtent2D(uint.MaxValue, uint.MaxValue),
            minImageExtent = new VkExtent2D(640, 480), maxImageExtent = new VkExtent2D(1600, 900),
        };

        VkExtent2D e = SurfaceExtent(tiled, 2560, 1440, out bool dictated);
        check.Expect(dictated && e.width == 2544 && e.height == 1378,
            $"a surface reporting 2544x1378 dictates it, whatever the frame asks for (got {e.width}x{e.height})");

        // Given a surface that leaves its extent open with min 640x480 and max 1600x900
        // When the frame asks for 1920x1080
        // Then the chain is made at 1600x900
        e = SurfaceExtent(open, 1920, 1080, out bool openDictated);
        check.Expect(!openDictated && e.width == 1600 && e.height == 900,
            $"E5: an open extent takes the frame's size clamped into the surface's bounds (got {e.width}x{e.height})");
        e = SurfaceExtent(open, 800, 600, out _);
        check.Expect(e.width == 800 && e.height == 600,
            $"...and a frame inside those bounds is taken as it is (got {e.width}x{e.height})");

        var held = new ChainShape(true, false, true, VkPresentModeKHR.Fifo, 2544, 1378);
        var openChain = new ChainShape(true, false, false, VkPresentModeKHR.Fifo, 1280, 720);

        // Given a surface that dictates its extent and a live chain made at it
        // When the frame asks for a swapchain at a different size
        // Then the chain is kept, and the frame reaches the window through the spare screen's blit
        check.Expect(ChainFits(held, VkPresentModeKHR.Fifo, 2560, 1440),
            "E1: a chain over a dictated 2544x1378 surface still answers a 2560x1440 frame, so nothing is remade");

        // Given the same dictated chain, and the window has reported out of date or suboptimal
        // When the frame asks
        // Then the chain is remade
        check.Expect(!ChainFits(held with { Stale = true }, VkPresentModeKHR.Fifo, 2560, 1440),
            "E2: ...but a stale window remakes it, which is the one signal that a dictated extent has moved");

        // Given the same dictated chain in FIFO
        // When the swap interval asks for immediate
        // Then the chain is remade, because the mode is not the surface's to dictate
        check.Expect(!ChainFits(held, VkPresentModeKHR.Immediate, 2544, 1378),
            "E3: a mode change remakes a dictated chain at its own size");

        // Given a surface that leaves its extent open and a chain made at 1280x720
        // When the frame asks for 1920x1080
        // Then the chain is remade at the frame's size
        check.Expect(!ChainFits(openChain, VkPresentModeKHR.Fifo, 1920, 1080),
            "E4: a chain over an open surface does not answer a frame of another size");

        // Given a live chain at 2544x1378 in FIFO, over either kind of surface
        // When the frame asks for 2544x1378 in FIFO
        // Then the chain is kept
        check.Expect(ChainFits(held, VkPresentModeKHR.Fifo, 2544, 1378)
                  && ChainFits(held with { Dictated = false }, VkPresentModeKHR.Fifo, 2544, 1378),
            "E6: the frame's own size and mode is never a remake, dictated or not");
        check.Expect(!ChainFits(held with { Live = false }, VkPresentModeKHR.Fifo, 2544, 1378),
            "...and with no chain there is one to make, whatever the surface dictates");
        log.Info(check.Ok ? "VkBackend.SwapSelfCheck: passed" : "VkBackend.SwapSelfCheck: FAILED");
    }
}
