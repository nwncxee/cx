using System;
using System.Collections.Generic;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// The transfer queue: a resource's first fill -- an image no recording has moved, a buffer none has used -- goes out
/// on a copy-only family ahead of the graphics submit, which waits on it. Nothing in flight reads such a resource, so
/// the copy waits on nothing. Every other upload, and every mip chain, stays at the head of the graphics submit.
/// An exclusive image is released here and acquired at the graphics head; buffers are shared by every family.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>Null on a device with no copy-only family: every upload then rides the graphics submit's head.</summary>
    private readonly Lane? _transfer;
    /// <summary>The transfer value no graphics submit has waited on yet; 0 when none.</summary>
    private long _shippedJoin;
    private long _shipped;

    /// <summary>The first family of <paramref name="families"/> with queues that copies without drawing or computing and
    /// copies a single texel, so a sub-rectangle upload is as legal there as on the graphics queue; or -1.</summary>
    internal static int TransferFamily(ReadOnlySpan<VkQueueFamilyProperties> families)
    {
        for (int k = 0; k < families.Length; k++)
        {
            ref readonly VkQueueFamilyProperties f = ref families[k];
            VkExtent3D g = f.minImageTransferGranularity;
            if (f.queueCount > 0 && (f.queueFlags & (VkQueueFlags.Graphics | VkQueueFlags.Compute | VkQueueFlags.Transfer)) == VkQueueFlags.Transfer
                && g.width == 1 && g.height == 1 && g.depth == 1)
                return k;
        }
        return -1;
    }

    /// <summary>The device has a transfer queue the first fills go out on.</summary>
    internal bool HasTransfer => _transfer != null;

    /// <summary>Copies sent on the transfer queue so far.</summary>
    internal long Shipped => _shipped;

    /// <summary>Send the pending first fills on the transfer queue, ahead of the graphics submit being recorded; the
    /// rest stay pending for its head. A refused send leaves every copy pending, where the graphics queue makes it.</summary>
    private void Ship()
    {
        UploadState u = _upload;
        if (_transfer is not Lane t || !u.Pending) return;
        u.Sent.Clear(); u.Left.Clear(); u.Mipped.Clear(); u.Seen.Clear(); u.Released.Clear();
        foreach (PendingCopy p in u.Copies) (FirstFill(p, u.Mipped) ? u.Sent : u.Left).Add(p);
        if (u.Sent.Count == 0 && u.Zero.Count == 0) return;
        if (TakeSlot(t.Slots, t.Pool, t.Timeline, ref t.Completed) is not Slot s) return;
        VkCommandBuffer cb = s.Cmd;
        _vkd.vkResetCommandBuffer(cb, 0);
        var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        _vkd.vkBeginCommandBuffer(cb, &bi);
        // the queue's earlier fills of a buffer still unused land before this one's
        CopyOrder(cb);
        foreach (BufferId z in u.Zero) if (_buffers.TryGet(z.Value, out VkBuf? b)) _vkd.vkCmdFillBuffer(cb, b.Buffer, 0, WholeSize, 0);
        if (u.Zero.Count > 0) CopyOrder(cb);
        foreach (PendingCopy p in u.Sent)
            if (p.Buffer.IsNull && _images.TryGet(p.Image.Value, out VkImg img) && u.Seen.Add(p.Image.Value)) u.Released.Add((p.Image, img));
        Barriers(cb, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, QueueFamilyIgnored, QueueFamilyIgnored);
        foreach (PendingCopy p in u.Sent)
        {
            if (!p.Buffer.IsNull)
            {
                if (!_buffers.TryGet(p.Buffer.Value, out VkBuf? b)) continue;
                var region = new VkBufferCopy { srcOffset = p.SrcOffset, dstOffset = (ulong)p.DstOffset, size = p.Bytes };
                _vkd.vkCmdCopyBuffer(cb, p.Src, b.Buffer, 1, &region);
                continue;
            }
            if (!_images.TryGet(p.Image.Value, out VkImg img)) continue;
            VkBufferImageCopy r = Region(p, img);
            _vkd.vkCmdCopyBufferToImage(cb, p.Src, img.Image, VkImageLayout.TransferDstOptimal, 1, &r);
        }
        Barriers(cb, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, t.Family, _family);
        _vkd.vkEndCommandBuffer(cb);

        VkResult sent = TransferSubmit(t, cb);
        if (sent != VkResult.Success) { Fault($"the transfer queue refused its fills ({sent}): the graphics queue makes them"); return; }
        s.Done = _shippedJoin = t.Submits;
        _shipped += u.Sent.Count;
        foreach ((ImageId id, VkImg img) in u.Released)
        {
            img.Layout = VkImageLayout.TransferDstOptimal;
            img.Written = false;
            u.Acquire.Add(id);
        }
        u.Copies.Clear();
        u.Copies.AddRange(u.Left);
        u.Zero.Clear();
    }

    /// <summary>Is <paramref name="p"/> a first fill the transfer queue may make: into a live buffer no recording has
    /// used, or a colour image none has moved that is not shared with the async queue and has no mip chain asked for
    /// before it in <paramref name="mipped"/>?</summary>
    private bool FirstFill(in PendingCopy p, HashSet<uint> mipped)
    {
        if (p.Mips) { mipped.Add(p.Image.Value); return false; }
        if (!p.Buffer.IsNull) return _buffers.TryGet(p.Buffer.Value, out VkBuf? b) && b.Epoch < 0;
        return !mipped.Contains(p.Image.Value) && _images.TryGet(p.Image.Value, out VkImg img) && img.Layout == VkImageLayout.Undefined
            && (img.Desc.Use & ImageUse.Async) == 0 && !img.Desc.Fmt.IsDepth();
    }

    /// <summary>Every transfer write before it ordered before every transfer access after it, on <paramref name="cb"/>.</summary>
    private void CopyOrder(VkCommandBuffer cb)
    {
        var mb = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.AllTransfer, srcAccessMask = VkAccessFlags2.TransferWrite,
            dstStageMask = VkPipelineStageFlags2.AllTransfer, dstAccessMask = VkAccessFlags2.TransferRead | VkAccessFlags2.TransferWrite,
        };
        var di = new VkDependencyInfo { memoryBarrierCount = 1, pMemoryBarriers = &mb };
        _vkd.vkCmdPipelineBarrier2(cb, &di);
    }

    /// <summary>Every released image moved from <paramref name="from"/> to <paramref name="to"/> on <paramref name="cb"/>,
    /// as one call, handed from family <paramref name="src"/> to <paramref name="dst"/> (ignored for neither).</summary>
    private void Barriers(VkCommandBuffer cb, VkImageLayout from, VkImageLayout to, uint src, uint dst)
    {
        UploadState u = _upload;
        if (u.Released.Count == 0) return;
        if (u.Bars.Length < u.Released.Count) u.Bars = new VkImageMemoryBarrier2[u.Released.Count * 2];
        bool release = src != dst;
        for (int i = 0; i < u.Released.Count; i++)
        {
            u.Bars[i] = Handover(u.Released[i].Img, release ? VkPipelineStageFlags2.AllTransfer : VkPipelineStageFlags2.None,
                                 release ? VkAccessFlags2.TransferWrite : VkAccessFlags2.None,
                                 release ? VkPipelineStageFlags2.None : VkPipelineStageFlags2.AllTransfer,
                                 release ? VkAccessFlags2.None : VkAccessFlags2.TransferWrite);
            u.Bars[i].oldLayout = from;
            u.Bars[i].newLayout = to;
            u.Bars[i].srcQueueFamilyIndex = src;
            u.Bars[i].dstQueueFamilyIndex = dst;
        }
        fixed (VkImageMemoryBarrier2* ib = u.Bars)
        {
            var di = new VkDependencyInfo { imageMemoryBarrierCount = (uint)u.Released.Count, pImageMemoryBarriers = ib };
            _vkd.vkCmdPipelineBarrier2(cb, &di);
        }
    }

    /// <summary>The whole of <paramref name="img"/> handed from the transfer family to the graphics one, out of the
    /// transfer layout into the sampled one: the release and the acquire name the same move, each its own stage half.</summary>
    private VkImageMemoryBarrier2 Handover(VkImg img, VkPipelineStageFlags2 ss, VkAccessFlags2 sa, VkPipelineStageFlags2 ds, VkAccessFlags2 da) => new()
    {
        srcStageMask = ss, srcAccessMask = sa, dstStageMask = ds, dstAccessMask = da,
        oldLayout = VkImageLayout.TransferDstOptimal, newLayout = VkImageLayout.ShaderReadOnlyOptimal,
        srcQueueFamilyIndex = _transfer!.Family, dstQueueFamilyIndex = _family, image = img.Image,
        subresourceRange = new VkImageSubresourceRange
        {
            aspectMask = Aspect(img.Desc.Fmt), levelCount = Vortice.Vulkan.Vulkan.VK_REMAINING_MIP_LEVELS,
            layerCount = Vortice.Vulkan.Vulkan.VK_REMAINING_ARRAY_LAYERS,
        },
    };
}
