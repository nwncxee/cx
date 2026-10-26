using System;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// The seam's partial transfers on Vulkan: a sub-rectangle uploaded into one layer of an image, and an image block
/// copied into a host-readable buffer that the CPU reads once the submit that copied it has finished -- polled, never
/// waited on.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>Copy <paramref name="bytes"/> into a <paramref name="w"/> x <paramref name="h"/> block of
    /// <paramref name="i"/>'s mip 0 at (<paramref name="x"/>, <paramref name="y"/>) on <paramref name="layer"/>, at the
    /// head of the next submit. Rows are <paramref name="rowTexels"/> texels apart in the source (0 = tight); a
    /// compressed image takes whole blocks. False (logged) when the block is outside the image or staging is refused.</summary>
    internal bool UploadRegion(ImageId i, int layer, int x, int y, int w, int h, int rowTexels, ReadOnlySpan<byte> bytes)
    {
        if (!OnThread("UploadRegion") || !_images.TryGet(i.Value, out VkImg img)) return Fault("UploadRegion: stale image");
        if (!Inside(img, new ImageRegion(0, x, y, layer), w, h, 1, out string why))
            return Fault($"UploadRegion into {img.Desc.Label}: the block is {why}");
        if (!Stage(bytes, out VkBuffer src, out ulong at)) return false;
        _upload.Copies.Add(new PendingCopy(i, default, 0, layer, 0, src, at, (ulong)bytes.Length, false, x, y, w, h, rowTexels));
        return true;
    }

    /// <summary>The submits made so far: a copy recorded into the next submit completes with submit number
    /// <see cref="Submitted"/> as it reads now.</summary>
    internal long Submitted => _submits;

    /// <summary>Has submit number <paramref name="n"/> finished on the GPU? Never blocks.</summary>
    internal bool Finished(long n)
    {
        if (n >= _submits) return false;
        if (_submits - n > Slots) return true;
        VkFence f = _slots[(int)(n % Slots)].Fence;
        return _vkd.vkWaitForFences(1, &f, true, 0) == VkResult.Success;
    }

    /// <summary><paramref name="dst"/>.Length bytes of the readback buffer <paramref name="b"/> from
    /// <paramref name="offset"/>, made visible to the host. Only after <see cref="Finished"/> said so for the submit
    /// that wrote them.</summary>
    internal bool ReadMapped(BufferId b, long offset, Span<byte> dst)
    {
        if (!_buffers.TryGet(b.Value, out VkBuf buf) || buf.Desc.Memory != MemoryKind.Readback || offset + dst.Length > buf.Desc.Bytes)
            return Fault("ReadMapped: stale, short or not a readback buffer");
        fixed (byte* p = dst)
            return Vma.vmaCopyAllocationToMemory(_vma, buf.Alloc, (ulong)offset, p, (ulong)dst.Length) == VkResult.Success
                || Fault("ReadMapped: the copy out of the mapped allocation was refused");
    }

    /// <summary><see cref="Op.CopyImageToBuffer"/>: the block in its source's texels (whole blocks for a compressed
    /// format), tightly packed, then made visible to the host.</summary>
    private bool CopyToBuf(in CopyToBufferCmd c)
    {
        if (_inPass) return Fault("CopyImageToBuffer inside a render pass");
        if (!_images.TryGet(c.Src.Value, out VkImg src)) return Fault("CopyImageToBuffer: stale image");
        if (!_buffers.TryGet(c.Dst.Value, out VkBuf dst)) return Fault("CopyImageToBuffer: stale buffer");
        if (!Inside(src, c.From, c.W, c.H, 1, out string why)) return Fault($"CopyImageToBuffer: the block is {why} of {src.Desc.Label}");
        long bytes = LevelBytes(src.Desc with { W = c.W, H = c.H, D = 1 }, 0);
        if (c.Offset < 0 || c.Offset + bytes > dst.Desc.Bytes) return Fault($"CopyImageToBuffer: {bytes} bytes at {c.Offset} of {dst.Desc.Label}'s {dst.Desc.Bytes}");
        Transition(src, VkImageLayout.TransferSrcOptimal);
        var r = new VkBufferImageCopy
        {
            bufferOffset = (ulong)c.Offset, imageSubresource = Sub(src, c.From), imageOffset = At(src, c.From),
            imageExtent = new VkExtent3D { width = (uint)c.W, height = (uint)c.H, depth = 1 },
        };
        _vkd.vkCmdCopyImageToBuffer(_cmd, src.Image, VkImageLayout.TransferSrcOptimal, dst.Buffer, 1, &r);
        var mb = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.AllCommands, srcAccessMask = VkAccessFlags2.MemoryWrite,
            dstStageMask = VkPipelineStageFlags2.Host, dstAccessMask = VkAccessFlags2.HostRead,
        };
        var di = new VkDependencyInfo { memoryBarrierCount = 1, pMemoryBarriers = &mb };
        _vkd.vkCmdPipelineBarrier2(_cmd, &di);
        return true;
    }
}
