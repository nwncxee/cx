using System;
using System.Collections.Generic;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// Barriers on Vulkan. Each is scoped by the layouts or uses on its two sides, never a whole-queue drain, and is
/// queued as transitions are asked for; the queue records as one <c>vkCmdPipelineBarrier2</c> ahead of the next
/// command that needs it. Images order by layout and a written flag, buffers by an epoch of uses.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>Every shader stage a pipeline here runs.</summary>
    private const VkPipelineStageFlags2 ShaderStages = VkPipelineStageFlags2.VertexShader | VkPipelineStageFlags2.GeometryShader
                                                     | VkPipelineStageFlags2.FragmentShader | VkPipelineStageFlags2.ComputeShader;
    /// <summary>The shader stages a draw runs.</summary>
    private const VkPipelineStageFlags2 DrawStages = VkPipelineStageFlags2.VertexShader | VkPipelineStageFlags2.GeometryShader
                                                   | VkPipelineStageFlags2.FragmentShader;
    /// <summary>Every stage that reads or writes a buffer, and the accesses it may make: a buffer barrier's far side.</summary>
    private const VkPipelineStageFlags2 BufferStages = ShaderStages | VkPipelineStageFlags2.DrawIndirect | VkPipelineStageFlags2.IndexInput
                                                     | VkPipelineStageFlags2.VertexAttributeInput | VkPipelineStageFlags2.AllTransfer;
    private const VkAccessFlags2 BufferAccess = VkAccessFlags2.IndirectCommandRead | VkAccessFlags2.IndexRead | VkAccessFlags2.VertexAttributeRead
                                              | VkAccessFlags2.UniformRead | VkAccessFlags2.ShaderStorageRead | VkAccessFlags2.ShaderStorageWrite
                                              | VkAccessFlags2.TransferRead | VkAccessFlags2.TransferWrite;
    /// <summary>A storage buffer's access: read and, for all the backend knows, written. ponytail: a <c>readonly</c>
    /// block counts as written too; the pipeline's SPIR-V NonWritable decorations would spare its readers the barrier.</summary>
    private const VkAccessFlags2 StorageAccess = VkAccessFlags2.ShaderStorageRead | VkAccessFlags2.ShaderStorageWrite;
    /// <summary>The accesses that write: the only ones a barrier's source side has to make available.</summary>
    private const VkAccessFlags2 WriteAccess = VkAccessFlags2.ShaderWrite | VkAccessFlags2.ShaderStorageWrite | VkAccessFlags2.ColorAttachmentWrite
                                             | VkAccessFlags2.DepthStencilAttachmentWrite | VkAccessFlags2.TransferWrite
                                             | VkAccessFlags2.HostWrite | VkAccessFlags2.MemoryWrite;

    /// <summary>The stages a compute-only queue's barrier may name: a graphics stage's half of a barrier there is the
    /// cross-queue semaphore's, which orders everything the graphics queue did before it.</summary>
    private const VkPipelineStageFlags2 AsyncStages = VkPipelineStageFlags2.ComputeShader | VkPipelineStageFlags2.AllTransfer
                                                    | VkPipelineStageFlags2.AllCommands;
    private const VkAccessFlags2 ShaderAccess = VkAccessFlags2.UniformRead | VkAccessFlags2.ShaderSampledRead
                                              | VkAccessFlags2.ShaderStorageRead | VkAccessFlags2.ShaderStorageWrite;

    /// <summary>The queued image barriers; an image's <see cref="VkImg.BarAt"/> indexes this while its
    /// <see cref="VkImg.BarBatch"/> is <see cref="_batch"/>.</summary>
    private VkImageMemoryBarrier2[] _imgBars = new VkImageMemoryBarrier2[32];
    private int _imgBarN;
    private long _batch;
    /// <summary>The queued global memory barrier; a zero far side queues none.</summary>
    private VkPipelineStageFlags2 _memSrc, _memDst;
    private VkAccessFlags2 _memSrcAccess, _memDstAccess;
    /// <summary>The buffer epoch: the stages that touched a buffer since the last buffer barrier, and the writes among
    /// them. A buffer used twice in one epoch with a write on either side waits for a barrier.</summary>
    private long _epoch;
    private VkPipelineStageFlags2 _epochStages;
    private VkAccessFlags2 _epochWrites;
    /// <summary>The buffer uses since the last flush: they run after the barrier queued now, so <see cref="Order"/>
    /// stamps them into the epoch it opens rather than the one it closes.</summary>
    private readonly List<(VkBuf Buf, VkPipelineStageFlags2 Stages, VkAccessFlags2 Writes)> _batchUses = new(64);
    /// <summary>A refused recording queued barriers that never ran: the next one opens with a full barrier.</summary>
    private bool _resync;
    /// <summary><c>vkCmdPipelineBarrier2</c> calls in the submits' recordings since the last take.</summary>
    private int _barrierN;

    /// <summary>The compute bind set's buffers (with their access) and storage images, applied at each dispatch.</summary>
    private readonly VkBuf[] _cBufs = new VkBuf[UniformSlots + StorageSlots];
    private readonly VkAccessFlags2[] _cAccess = new VkAccessFlags2[UniformSlots + StorageSlots];
    private readonly VkImg[] _cImgs = new VkImg[ImageSlots];
    private int _cBufN, _cImgN;

    /// <summary>The stages that touch an image in <paramref name="l"/> and the accesses they make there.</summary>
    private static (VkPipelineStageFlags2 Stages, VkAccessFlags2 Access) Scope(VkImageLayout l) => l switch
    {
        VkImageLayout.Undefined or VkImageLayout.PresentSrcKHR => (VkPipelineStageFlags2.None, VkAccessFlags2.None),
        VkImageLayout.ColorAttachmentOptimal => (VkPipelineStageFlags2.ColorAttachmentOutput,
                                                 VkAccessFlags2.ColorAttachmentRead | VkAccessFlags2.ColorAttachmentWrite),
        VkImageLayout.DepthStencilAttachmentOptimal => (VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests,
                                                        VkAccessFlags2.DepthStencilAttachmentRead | VkAccessFlags2.DepthStencilAttachmentWrite),
        VkImageLayout.ShaderReadOnlyOptimal => (ShaderStages, VkAccessFlags2.ShaderSampledRead),
        VkImageLayout.General => (ShaderStages, StorageAccess),
        VkImageLayout.TransferSrcOptimal => (VkPipelineStageFlags2.AllTransfer, VkAccessFlags2.TransferRead),
        VkImageLayout.TransferDstOptimal => (VkPipelineStageFlags2.AllTransfer, VkAccessFlags2.TransferWrite),
        VkImageLayout.FragmentShadingRateAttachmentOptimalKHR => (VkPipelineStageFlags2.FragmentShadingRateAttachmentKHR,
                                                                  VkAccessFlags2.FragmentShadingRateAttachmentReadKHR),
        _ => (VkPipelineStageFlags2.AllCommands, VkAccessFlags2.MemoryRead | VkAccessFlags2.MemoryWrite),
    };

    /// <summary>Move <paramref name="img"/> into <paramref name="to"/>, or order it against a write made in that layout
    /// already (a storage write read next, an attachment drawn again). Queued: <see cref="Flush"/> records it.</summary>
    private void Transition(VkImg img, VkImageLayout to)
    {
        Remember(img);
        if (_onAsync) img.AsyncRec = _recording;
        if (img.Layout == to && !img.Written) return;
        if (_besideNow && img.AsyncRec == _asyncRec)
            _crossed ??= $"moved {img.Desc.Label}, which the async submit it runs beside reached -- hand it over ahead of both";
        (VkPipelineStageFlags2 ds, VkAccessFlags2 da) = Scope(to);
        if (img.BarBatch == _batch)
        {
            ref VkImageMemoryBarrier2 q = ref _imgBars[img.BarAt];
            q.newLayout = to; q.dstStageMask = ds; q.dstAccessMask = da;
        }
        else
        {
            (VkPipelineStageFlags2 ss, VkAccessFlags2 sa) = Scope(img.Layout);
            img.BarBatch = _batch;
            VkImageMemoryBarrier2 b = Whole(img);
            b.srcStageMask = ss; b.srcAccessMask = sa & WriteAccess; b.dstStageMask = ds; b.dstAccessMask = da;
            b.oldLayout = img.Layout; b.newLayout = to;
            img.BarAt = Queue(b);
        }
        img.Layout = to;
        img.Written = false;
    }

    /// <summary>A barrier over every mip and layer of <paramref name="img"/>, no stage, access, layout or ownership named.</summary>
    private static VkImageMemoryBarrier2 Whole(VkImg img) => new()
    {
        srcQueueFamilyIndex = QueueFamilyIgnored, dstQueueFamilyIndex = QueueFamilyIgnored, image = img.Image,
        subresourceRange = new VkImageSubresourceRange
        {
            aspectMask = Aspect(img.Desc.Fmt), levelCount = Vortice.Vulkan.Vulkan.VK_REMAINING_MIP_LEVELS,
            layerCount = Vortice.Vulkan.Vulkan.VK_REMAINING_ARRAY_LAYERS,
        },
    };

    private int Queue(in VkImageMemoryBarrier2 b)
    {
        if (_imgBarN == _imgBars.Length) Array.Resize(ref _imgBars, _imgBarN * 2);
        _imgBars[_imgBarN] = b;
        return _imgBarN++;
    }

    /// <summary>Note <paramref name="b"/> used by <paramref name="stages"/> with <paramref name="access"/> by the next
    /// command, queueing the buffer barrier a hazard on it needs. Inside a begun pass none can be placed and none is
    /// needed against earlier work, which the pass's begin ordered; two of the pass's own draws stay unordered.</summary>
    private void Use(VkBuf b, VkPipelineStageFlags2 stages, VkAccessFlags2 access)
    {
        VkAccessFlags2 writes = access & WriteAccess;
        if (_rendering) { Stamp(b, stages, writes); return; }
        if (b.Epoch == _epoch && (writes != VkAccessFlags2.None || b.Written)) Order();
        Stamp(b, stages, writes);
        _batchUses.Add((b, stages, writes));
    }

    private void Stamp(VkBuf b, VkPipelineStageFlags2 stages, VkAccessFlags2 writes)
    {
        if (b.Epoch != _epoch) { b.Epoch = _epoch; b.Written = false; }
        b.Written |= writes != VkAccessFlags2.None;
        _epochStages |= stages;
        _epochWrites |= writes;
    }

    /// <summary>Order every buffer access of the epoch before every one after it, and open the next epoch. Queued.</summary>
    private void Order()
    {
        if (_epochStages == VkPipelineStageFlags2.None) return;
        _memSrc |= _epochStages; _memSrcAccess |= _epochWrites;
        _memDst |= BufferStages; _memDstAccess |= BufferAccess;
        _epoch++;
        _epochStages = VkPipelineStageFlags2.None;
        _epochWrites = VkAccessFlags2.None;
        foreach ((VkBuf buf, VkPipelineStageFlags2 stages, VkAccessFlags2 writes) in _batchUses) Stamp(buf, stages, writes);
    }

    /// <summary>Queue the host's read of what the transfers before it wrote.</summary>
    private void HostRead()
    {
        _memSrc |= VkPipelineStageFlags2.AllTransfer; _memSrcAccess |= VkAccessFlags2.TransferWrite;
        _memDst |= VkPipelineStageFlags2.Host; _memDstAccess |= VkAccessFlags2.HostRead;
    }

    /// <summary>Record the queued barriers as one call; nothing when none are queued.</summary>
    private void Flush()
    {
        if (_imgBarN == 0 && _memDst == VkPipelineStageFlags2.None) return;
        var mb = new VkMemoryBarrier2 { srcStageMask = _memSrc, srcAccessMask = _memSrcAccess, dstStageMask = _memDst, dstAccessMask = _memDstAccess };
        bool mem = _memDst != VkPipelineStageFlags2.None;
        if (_onAsync)
        {
            ToAsync(ref mb.srcStageMask, ref mb.srcAccessMask);
            ToAsync(ref mb.dstStageMask, ref mb.dstAccessMask);
            for (int i = 0; i < _imgBarN; i++)
            {
                ToAsync(ref _imgBars[i].srcStageMask, ref _imgBars[i].srcAccessMask);
                ToAsync(ref _imgBars[i].dstStageMask, ref _imgBars[i].dstAccessMask);
            }
            mem = mb.dstStageMask != VkPipelineStageFlags2.None;
        }
        fixed (VkImageMemoryBarrier2* ib = _imgBars)
        {
            var di = new VkDependencyInfo
            {
                memoryBarrierCount = mem ? 1u : 0u, pMemoryBarriers = &mb,
                imageMemoryBarrierCount = (uint)_imgBarN, pImageMemoryBarriers = ib,
            };
            _vkd.vkCmdPipelineBarrier2(_cmd, &di);
        }
        _barrierN++;
        Drop();
    }

    /// <summary>One side of a barrier cut to what the async queue's family runs: each access only beside a stage that makes it.</summary>
    private static void ToAsync(ref VkPipelineStageFlags2 stages, ref VkAccessFlags2 access)
    {
        stages &= AsyncStages;
        if ((stages & VkPipelineStageFlags2.AllCommands) != 0) return;
        VkAccessFlags2 keep = VkAccessFlags2.None;
        if ((stages & VkPipelineStageFlags2.ComputeShader) != 0) keep |= ShaderAccess;
        if ((stages & VkPipelineStageFlags2.AllTransfer) != 0) keep |= VkAccessFlags2.TransferRead | VkAccessFlags2.TransferWrite;
        access &= keep;
    }

    /// <summary>Forget the queued barriers: recorded, or dropped with a refused recording.</summary>
    private void Drop()
    {
        _imgBarN = 0;
        _batch++;
        _batchUses.Clear();
        _memSrc = _memDst = VkPipelineStageFlags2.None;
        _memSrcAccess = _memDstAccess = VkAccessFlags2.None;
    }

    /// <summary>Every write before it visible to every access after it: a refused recording's barriers never ran, so
    /// the next recording opens with this instead of trusting the layouts' written flags and the buffer epoch.</summary>
    private void FullBarrier()
    {
        var mb = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.AllCommands, srcAccessMask = VkAccessFlags2.MemoryWrite,
            dstStageMask = VkPipelineStageFlags2.AllCommands, dstAccessMask = VkAccessFlags2.MemoryRead | VkAccessFlags2.MemoryWrite,
        };
        var di = new VkDependencyInfo { memoryBarrierCount = 1, pMemoryBarriers = &mb };
        _vkd.vkCmdPipelineBarrier2(_cmd, &di);
        _barrierN++;
    }

    /// <summary>Note <paramref name="img"/>'s layout as this recording found it, once per recording, so a refused
    /// recording -- whose barriers never run -- puts it back.</summary>
    private void Remember(VkImg img)
    {
        if (img.UndoAt == _recording) return;
        img.UndoAt = _recording;
        if (_onAsync && (img.Desc.Use & ImageUse.Async) == 0) _crossed ??= $"reached {img.Desc.Label} on the async queue, which was not made ImageUse.Async";
        _undo.Add((img, img.Layout));
    }

    /// <summary>The current recording's count, and each image it changed with the layout it found.</summary>
    private long _recording;
    private readonly List<(VkImg Img, VkImageLayout Layout)> _undo = new();
}
