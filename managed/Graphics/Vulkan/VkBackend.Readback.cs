using System;
using System.Collections.Generic;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// The seam's partial transfers on Vulkan: a sub-rectangle uploaded into one layer of an image, and an image block
/// copied into a host-readable buffer that the CPU reads from work the copy's list queued with
/// <see cref="CommandList.WhenDone"/>, run once its submit has finished.
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

    /// <summary>Each submit's <see cref="CommandList.WhenDone"/> work in submit order, under the timeline value that
    /// says it finished; -1 for work whose submit never ran.</summary>
    private readonly List<(long At, WhenDoneCall Call)> _whenDone = new();
    private readonly List<WhenDoneCall> _carried = new();
    private readonly List<(WhenDoneCall Call, bool Ran)> _firing = new();
    private bool _asyncWhenDoneSaid, _inWhenDone;

    /// <summary>Block until every submit made so far has finished; false (logged) when the GPU did not within the
    /// device's wait limit. Cold: a screenshot's own readback.</summary>
    internal bool WaitSubmitted()
    {
        VkResult r = WaitFor(_submits);
        return r == VkResult.Success || Fault($"WaitSubmitted: the GPU did not finish ({r})");
    }

    /// <summary>Run <paramref name="fn"/>(<paramref name="arg"/>, ran) once the timeline passes <paramref name="stamp"/>, a
    /// <see cref="FreeStamp"/>: memory a caller recycles itself goes back on the stamp the backend's own frees wait on.
    /// Told it never ran when the device goes first. Pass a cached delegate.</summary>
    internal void WhenPassed(long stamp, Action<int, bool> fn, int arg)
    {
        if (OnThread("WhenPassed")) _whenDone.Add((stamp, new WhenDoneCall(fn, arg)));
    }

    /// <summary>Move <paramref name="list"/>'s <see cref="CommandList.WhenDone"/> work onto the submit that just took
    /// it: under its timeline value when it ran on the graphics queue, as never-ran otherwise.</summary>
    private void CarryWhenDone(CommandList list, bool ran, bool lane)
    {
        list.TakeWhenDone(_carried);
        if (_carried.Count == 0) return;
        if (lane && !_asyncWhenDoneSaid)
        {
            _asyncWhenDoneSaid = true;
            _log.Always($"VkBackend: list {list.Name} asked for WhenDone on the async queue, which has no frame-end "
                      + "completion -- its work is told it never ran (logged once)");
        }
        long at = ran && !lane ? _submits : -1;
        foreach (WhenDoneCall c in _carried) _whenDone.Add((at, c));
        _carried.Clear();
    }

    /// <summary>Run the queued work whose submit has finished, in submit order among the submits that ran, after each
    /// submit: where the backend already retires what the GPU finished. A call that submits is refused.</summary>
    private void RunWhenDone()
    {
        if (_whenDone.Count == 0) return;
        Refresh();
        int keep = 0;
        for (int i = 0; i < _whenDone.Count; i++)
        {
            (long at, WhenDoneCall c) = _whenDone[i];
            if (at >= 0 && !Passed(at)) { _whenDone[keep++] = _whenDone[i]; continue; }
            _firing.Add((c, at >= 0));
        }
        _whenDone.RemoveRange(keep, _whenDone.Count - keep);
        _inWhenDone = true;
        try { foreach ((WhenDoneCall c, bool ran) in _firing) c.Fn(c.Arg, ran); }
        finally { _firing.Clear(); _inWhenDone = false; }
    }

    /// <summary>Device teardown: every queued work item is told its submit never ran.</summary>
    private void RefuseWhenDone()
    {
        int n = _whenDone.Count;
        for (int i = 0; i < n; i++) _whenDone[i].Call.Fn(_whenDone[i].Call.Arg, false);
        _whenDone.RemoveRange(0, n);
    }

    /// <summary><paramref name="dst"/>.Length bytes of the readback buffer <paramref name="b"/> from
    /// <paramref name="offset"/>, made visible to the host. Only from <see cref="CommandList.WhenDone"/> work of the list
    /// that wrote them, or after <see cref="WaitSubmitted"/>.</summary>
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
        if (!_buffers.TryGet(c.Dst.Value, out VkBuf? dst)) return Fault("CopyImageToBuffer: stale buffer");
        if (!Inside(src, c.From, c.W, c.H, 1, out string why)) return Fault($"CopyImageToBuffer: the block is {why} of {src.Desc.Label}");
        long bytes = LevelBytes(src.Desc with { W = c.W, H = c.H, D = 1 }, 0);
        if (c.Offset < 0 || c.Offset + bytes > dst.Desc.Bytes) return Fault($"CopyImageToBuffer: {bytes} bytes at {c.Offset} of {dst.Desc.Label}'s {dst.Desc.Bytes}");
        Transition(src, VkImageLayout.TransferSrcOptimal);
        Use(dst, VkPipelineStageFlags2.AllTransfer, VkAccessFlags2.TransferWrite);
        Flush();
        var r = new VkBufferImageCopy
        {
            bufferOffset = (ulong)c.Offset, imageSubresource = Sub(src, c.From), imageOffset = At(src, c.From),
            imageExtent = new VkExtent3D { width = (uint)c.W, height = (uint)c.H, depth = 1 },
        };
        _vkd.vkCmdCopyImageToBuffer(_cmd, src.Image, VkImageLayout.TransferSrcOptimal, dst.Buffer, 1, &r);
        HostRead();
        return true;
    }
}
