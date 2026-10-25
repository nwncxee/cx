using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The viewport rectangle and depth bounds in force, so either may be set without the other: GL keeps
    /// them apart (<c>glViewport</c> and <c>glDepthRange</c>) and Vulkan carries both in one state.</summary>
    private Rect _viewport;
    private float _minDepth, _maxDepth = 1f;

    /// <summary>GL's <c>glDepthRange</c>: where the clip depth range lands in the depth buffer, re-emitted over the
    /// rectangle in force. A rectangle has not been set yet at the head of a pass, where the range is only stored.</summary>
    private bool DepthRange(DepthRangeCmd c)
    {
        _minDepth = c.Min; _maxDepth = c.Max;
        return _viewport.W == 0 && _viewport.H == 0 || SetViewport();
    }

    /// <summary>The whole depth buffer, which every pass starts with: a pass that stacks views in one buffer says so
    /// itself, and nothing inherits the last one's range.</summary>
    private void ResetDepthRange() { _minDepth = 0f; _maxDepth = 1f; }

    private bool SetViewport()
    {
        // same numbers as GL's: both put framebuffer row 0 at y 0 and NDC -1 on it, so an imported image
        // reads back the same way up in either API
        var vp = new VkViewport
        {
            x = _viewport.X, y = _viewport.Y, width = _viewport.W, height = _viewport.H,
            minDepth = _minDepth, maxDepth = _maxDepth,
        };
        _vkd.vkCmdSetViewport(_cmd, 0, 1, &vp);
        return true;
    }
}
