using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The taa stage on Vulkan as one compute dispatch: the accumulate into the new history and the
/// <c>post.taasharp</c> copy-out into the stage's target, which the stack otherwise draws as two passes.</summary>
internal static class TaaFusedVk
{
    /// <summary>One frame's inputs: the stack's current image, the frame depth and velocity, the history pair, and
    /// the target with the display rect it is written at. <see cref="W"/> x <see cref="H"/> is the history's size.</summary>
    internal readonly record struct Frame(ImageId Cur, ImageId Depth, ImageId Vel, ImageId Hist, ImageId HistOut,
                                          ImageId Into, Format IntoFormat, int X, int Y, int W, int H,
                                          float Blend, Vector2 JitterPx, bool Upsample, float Sharp, float Dither);

    /// <summary>The dispatch into <paramref name="cl"/>; false (logged) when its stage was refused.</summary>
    internal static bool Record(VkBackend vk, CommandList cl, in Frame f, ExtenderLog log) => false;
}
