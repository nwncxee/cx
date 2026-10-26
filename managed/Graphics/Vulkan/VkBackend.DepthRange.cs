using CX.Rhi;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The window-depth range the viewport carries, and the rectangle it was last set with: Vulkan puts
    /// both in one structure, so narrowing the range re-issues the viewport.</summary>
    private float _depthMin = 0f, _depthMax = 1f;
    private Rect _viewport = new(0, 0, 1, 1);

    /// <summary>Collapse or restore the window-depth range for the rest of the pass, by re-issuing the pass's
    /// viewport with it. <c>Begin</c> puts <c>0, 1</c> back for every new pass.</summary>
    private bool DepthRange(DepthRangeCmd c)
    {
        _depthMin = c.Min;
        _depthMax = c.Max;
        return Viewport(_viewport);
    }
}
