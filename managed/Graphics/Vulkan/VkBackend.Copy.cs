using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>A texel-for-texel block copy between two images of one format, mip and layer each side's own. The
    /// frame's readable copy of the scene depth comes through here: a render pass's own depth attachment cannot be
    /// sampled by the draws inside it. Both sides move into their transfer layout first, which is also what orders
    /// the copy against the writes before it.</summary>
    private bool CopyImage(CopyImageCmd c)
    {
        if (_inPass) return Fault("CopyImage inside a render pass");
        if (!_images.TryGet(c.Src.Value, out VkImg src)) return Fault("CopyImage: stale source image");
        if (!_images.TryGet(c.Dst.Value, out VkImg dst)) return Fault("CopyImage: stale destination image");
        if (src.Desc.Fmt != dst.Desc.Fmt)
            return Fault($"CopyImage: {src.Desc.Label} is {src.Desc.Fmt} and {dst.Desc.Label} is {dst.Desc.Fmt} -- a copy is texel for texel");
        if (c.W <= 0 || c.H <= 0 || c.D <= 0) return true;
        Transition(src, VkImageLayout.TransferSrcOptimal);
        Transition(dst, VkImageLayout.TransferDstOptimal);
        var region = new VkImageCopy
        {
            srcSubresource = Layers(src, c.From),
            srcOffset = Offset(src, c.From),
            dstSubresource = Layers(dst, c.To),
            dstOffset = Offset(dst, c.To),
            extent = new VkExtent3D { width = (uint)c.W, height = (uint)c.H, depth = (uint)c.D },
        };
        _vkd.vkCmdCopyImage(_cmd, src.Image, VkImageLayout.TransferSrcOptimal, dst.Image,
                            VkImageLayout.TransferDstOptimal, 1, &region);
        return true;
    }

    /// <summary>One region's subresource: a 3D image's slices live in its depth, every other kind's in its layers.</summary>
    private static VkImageSubresourceLayers Layers(VkImg img, ImageRegion r) => new()
    {
        aspectMask = Aspect(img.Desc.Fmt), mipLevel = (uint)r.Mip, layerCount = 1,
        baseArrayLayer = img.Desc.Kind == ImageKind.Tex3D ? 0u : (uint)r.Z,
    };

    private static VkOffset3D Offset(VkImg img, ImageRegion r)
        => new() { x = r.X, y = r.Y, z = img.Desc.Kind == ImageKind.Tex3D ? r.Z : 0 };
}
