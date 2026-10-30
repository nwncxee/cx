using System;
using System.Collections.Generic;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// The transfer queue: a first fill -- an image no recording has moved, a device buffer none has used -- goes out on a
/// copy-only family ahead of the graphics submit, which waits on it; nothing in flight reads it, so the copy waits on
/// nothing. Other uploads and mip chains stay at the graphics head. Exclusive images are released here and acquired
/// there; device buffers are shared by every family. Its barriers record here, outside the one-recording batch.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>Null on a device with no copy-only family: every upload then rides the graphics submit's head.</summary>
    private readonly QueueLane? _transfer;
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
        if (_transfer is not QueueLane t || !u.Pending) return;
        u.Sent.Clear(); u.Left.Clear(); u.Mipped.Clear(); u.Seen.Clear(); u.Released.Clear();
        foreach (PendingCopy p in u.Copies)
        {
            if (!FirstFill(p, u.Mipped, out VkImg? img)) { u.Left.Add(p); continue; }
            u.Sent.Add(p);
            if (img != null && u.Seen.Add(p.Image.Value)) u.Released.Add((p.Image, img));
        }
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
        Barriers(cb, release: false);
        u.Seen.Clear();
        foreach (PendingCopy p in u.Sent)
        {
            if (!p.Buffer.IsNull)
            {
                if (!_buffers.TryGet(p.Buffer.Value, out VkBuf? b)) continue;
                if (!u.Seen.Add(p.Buffer.Value)) { CopyOrder(cb); u.Seen.Clear(); u.Seen.Add(p.Buffer.Value); }
                var region = new VkBufferCopy { srcOffset = p.SrcOffset, dstOffset = (ulong)p.DstOffset, size = p.Bytes };
                _vkd.vkCmdCopyBuffer(cb, p.Src, b.Buffer, 1, &region);
                continue;
            }
            if (!_images.TryGet(p.Image.Value, out VkImg img)) continue;
            VkBufferImageCopy r = Region(p, img);
            _vkd.vkCmdCopyBufferToImage(cb, p.Src, img.Image, VkImageLayout.TransferDstOptimal, 1, &r);
        }
        Barriers(cb, release: true);
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

    /// <summary>Is <paramref name="p"/> a first fill the transfer queue may make: into a live device buffer no
    /// recording has used, or a colour image none has moved (<paramref name="img"/>) that is not shared with the async
    /// queue and has no mip chain asked for before it in <paramref name="mipped"/>?</summary>
    private bool FirstFill(in PendingCopy p, HashSet<uint> mipped, out VkImg? img)
    {
        img = null;
        if (p.Mips) { mipped.Add(p.Image.Value); return false; }
        if (!p.Buffer.IsNull)
            return _buffers.TryGet(p.Buffer.Value, out VkBuf? b) && b.Epoch < 0 && b.Desc.Memory != MemoryKind.Readback;
        if (mipped.Contains(p.Image.Value) || !_images.TryGet(p.Image.Value, out VkImg i) || i.Layout != VkImageLayout.Undefined
            || (i.Desc.Use & ImageUse.Async) != 0 || i.Desc.Fmt.IsDepth())
            return false;
        img = i;
        return true;
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
        _barrierN++;
    }

    /// <summary>Every released image on <paramref name="cb"/>, as one call: out of undefined into the copy layout
    /// ahead of the copies, or with <paramref name="release"/> handed to the graphics family after them.</summary>
    private void Barriers(VkCommandBuffer cb, bool release)
    {
        UploadState u = _upload;
        if (u.Released.Count == 0) return;
        if (u.Bars.Length < u.Released.Count) u.Bars = new VkImageMemoryBarrier2[u.Released.Count * 2];
        for (int i = 0; i < u.Released.Count; i++)
        {
            VkImg img = u.Released[i].Img;
            if (release) { u.Bars[i] = Handover(img, acquire: false); continue; }
            u.Bars[i] = Whole(img);
            u.Bars[i].dstStageMask = VkPipelineStageFlags2.AllTransfer;
            u.Bars[i].dstAccessMask = VkAccessFlags2.TransferWrite;
            u.Bars[i].oldLayout = VkImageLayout.Undefined;
            u.Bars[i].newLayout = VkImageLayout.TransferDstOptimal;
        }
        fixed (VkImageMemoryBarrier2* ib = u.Bars)
        {
            var di = new VkDependencyInfo { imageMemoryBarrierCount = (uint)u.Released.Count, pImageMemoryBarriers = ib };
            _vkd.vkCmdPipelineBarrier2(cb, &di);
        }
        _barrierN++;
    }

    /// <summary>The whole of <paramref name="img"/> handed from the transfer family to the graphics one, out of the copy
    /// layout into the sampled one: the release's half (on the transfer queue) or the <paramref name="acquire"/>'s.</summary>
    private VkImageMemoryBarrier2 Handover(VkImg img, bool acquire)
    {
        VkImageMemoryBarrier2 b = Whole(img);
        b.oldLayout = VkImageLayout.TransferDstOptimal;
        b.newLayout = VkImageLayout.ShaderReadOnlyOptimal;
        b.srcQueueFamilyIndex = _transfer!.Family;
        b.dstQueueFamilyIndex = _family;
        if (acquire) { b.dstStageMask = ShaderStages; b.dstAccessMask = VkAccessFlags2.ShaderSampledRead; }
        else { b.srcStageMask = VkPipelineStageFlags2.AllTransfer; b.srcAccessMask = VkAccessFlags2.TransferWrite; }
        return b;
    }
}
