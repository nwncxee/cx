using CX.Framework;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The wiring beside the approved swap examples: a live chain's state read into the shape the fit
    /// test takes. Three adjacent flags and a size pair mean a swap compiles and answers every example, so each
    /// field is asserted in its own place. Oracle: two states written out by hand, with distinct sizes.</summary>
    internal static void SwapShapeEdgesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkBackend.SwapShapeEdgesSelfCheck");
        var stale = new SwapState { Stale = true, Dictated = false, Mode = VkPresentModeKHR.Fifo, W = 7, H = 11 };
        check.Expect(Shape(stale) == new ChainShape(false, true, false, VkPresentModeKHR.Fifo, 7, 11),
            "a stale chain over an undictated surface reads as one, and no chain is not live");

        var dictated = new SwapState
        {
            Stale = false, Dictated = true, Mode = VkPresentModeKHR.Immediate, W = 3, H = 5,
        };
        check.Expect(Shape(dictated) == new ChainShape(false, false, true, VkPresentModeKHR.Immediate, 3, 5),
            "...and a dictated chain in immediate reads as one, so stale and dictated cannot trade places");

        check.Expect(Shape(stale).Stale && !Shape(stale).Dictated
                  && Shape(dictated).Dictated && !Shape(dictated).Stale,
            "the two flags answer for themselves, each from its own field");
        check.Expect(Shape(stale).W != Shape(stale).H && Shape(stale).W == 7,
            "the width is the width, which a height in its place would not be");
        log.Info(check.Ok ? "VkBackend.SwapShapeEdgesSelfCheck: passed"
                          : "VkBackend.SwapShapeEdgesSelfCheck: FAILED");
    }
}
