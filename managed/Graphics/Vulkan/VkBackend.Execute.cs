using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The queue family index that means "no ownership transfer" (vulkan_core.h).</summary>
    private const uint QueueFamilyIgnored = ~0u;
    /// <summary>vkCmdUpdateBuffer's ceiling on one write.</summary>
    private const int MaxInlineUpdate = 65536;
    /// <summary>A slot's first staging buffer: past every block of constants, so only a scene-sized table grows it.</summary>
    private const ulong MinStage = 1 << 20;
    /// <summary>Each staged copy starts on this boundary: past every device's optimalBufferCopyOffsetAlignment floor.</summary>
    private const ulong StageAlign = 16;

    /// <summary>The render pass recorded but not begun: Vulkan forbids barriers inside one, so it begins at its first
    /// draw, after the pass's bind set has moved every sampled image into its read layout.</summary>
    private readonly ColorAttachment[] _pend = new ColorAttachment[RenderPassDesc.MaxColors];
    private int _pendCount;
    private bool _pendHasDepth;
    private DepthAttachment _pendDepth;
    private ImageId _pendRate;
    /// <summary>A multi-draw's commands, built here and staged for the indirect draw that gives each its draw index.</summary>
    private VkDrawIndexedIndirectCommand[] _multi = new VkDrawIndexedIndirectCommand[256];
    private bool _inPass, _rendering, _scissorSet;
    /// <summary>The bind set being recorded serves draws: its uses apply now, not at each dispatch.</summary>
    private bool _setDraws;
    private VkPipelineBindPoint _bindPoint = VkPipelineBindPoint.Graphics;
    private VkCommandBuffer _cmd;
    private Slot? _slot;

    /// <summary>Record <paramref name="list"/> into one command buffer and run it: an <see cref="CommandList.Async"/>
    /// one on the async queue, after every graphics submit before it.</summary>
    public bool Submit(CommandList list) => Submit(list, -1, false);

    /// <summary><see cref="Submit(CommandList)"/>, and with <paramref name="swap"/> at or past 0 the composite's blit
    /// onto that acquired swapchain image after the list: the submit waits on the frame's acquire and signals the
    /// image's ready semaphore, which the present waits on.</summary>
    private bool Submit(CommandList list, int swap, bool beside = false)
    {
        if (!OnThread("Submit")) return false;
        if (_inWhenDone) return Fault($"list {list.Name} was submitted from WhenDone work, which must not submit");
        bool ran = RecordAndQueue(list, swap, beside);
        CarryWhenDone(list, ran, list.Async && _async != null);
        RunWhenDone();
        return ran;
    }

    /// <summary><see cref="Submit(CommandList, int, bool)"/>'s recording and queue submit, on the device's thread.</summary>
    private bool RecordAndQueue(CommandList list, int swap, bool beside)
    {
        bool lane = list.Async && _async != null;
        if (lane && !UploadsAhead()) return false;
        if ((lane ? TakeSlot(_async!.Slots, _async.Pool, _async.Timeline, ref _async.Completed) : TakeSlot()) is not Slot s) return false;
        if (!lane) ShrinkGroups();
        if (_sets.Stale && !RetireSets()) return false;
        s.StageUsed = 0;
        _recording++;
        _undo.Clear();
        _vkd.vkResetCommandBuffer(s.Cmd, 0);
        var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        _vkd.vkBeginCommandBuffer(s.Cmd, &bi);
        _cmd = s.Cmd;
        _onAsync = lane;
        _besideNow = beside && !lane && _unjoined != 0 && !_beside;
        _crossed = null;
        Drop();
        _cBufN = _cImgN = 0;
        if (lane ? _resyncAsync : _resync) { FullBarrier(); if (lane) _resyncAsync = false; else _resync = false; }
        if (_labels) OpenLabel(list.NameUtf8);
        _slot = s;
        _inPass = _rendering = false;
        // the bindless upkeep, the uploads and the frame's timestamps stay on the graphics queue
        if (!lane)
        {
            TimeBegin(s);
            RecycleBindless();
        }
        BindBindless();
        if (!lane) FlushUploads();

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool ok = Record(list.TakePending());
        _translateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        _translateN++;
        if (ok && _crossed != null) ok = Fault($"list {list.Name}: {_crossed}");
        if (_rendering) _vkd.vkCmdEndRendering(_cmd);
        _inPass = _rendering = false;
        if (ok && swap >= 0) ok = RecordPresent(swap);
        Flush();
        if (!lane) TimeEnd(s, ok);
        if (ok && _labelDepth > 1 && !_labelLeakLogged)
        {
            _labelLeakLogged = true;
            _log.Always($"VkBackend: list {list.Name} left {_labelDepth - 1} debug label(s) "
                      + "open -- closed at its submit; a capture nests the passes after it wrongly (logged once)");
        }
        while (_labelDepth > 0) CloseLabel();
        _vkd.vkEndCommandBuffer(_cmd);
        if (!ok)
        {
            // nothing ran, so no recorded barrier did: every image is back in the layout it entered with, and the
            // uploads this recording flushed wait for the next one
            for (int i = _undo.Count - 1; i >= 0; i--)
            {
                _undo[i].Img.Layout = _undo[i].Layout;
                _undo[i].Img.Live = false;
                if (_undo[i].Img.Group is AliasGroup g) g.Owner = null;
            }
            _undo.Clear();
            if (lane) _resyncAsync = true;
            else _resync = true;
            _onAsync = _besideNow = false;
            if (!lane) _upload.Touched.Clear();
            return false;
        }
        if (!lane) CommitUploads();

        VkResult sent = lane ? AsyncSubmit(_cmd)
                      : QueueSubmit(_cmd, swap >= 0 ? Acquired : default, swap >= 0 ? _swap.Ready[swap] : default, besideAsked: beside);
        if (lane && sent == VkResult.Success) _asyncRec = _recording;
        _onAsync = _besideNow = false;
        if (sent != VkResult.Success) return Fault("vkQueueSubmit2 refused the frame -- the device is unusable");
        s.Done = lane ? _async!.Submits : _submits;
        DrainDead();
        return true;
    }


    private bool Record(CommandReader r)
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        while (r.Next(out Op op))
        {
            bool ok = op switch
            {
                Op.BeginRenderPass => Begin(r.Read<RenderPassCmd>(), ref r),
                Op.EndRenderPass => End(),
                Op.SetPipeline => Bind(r.Read<PipelineId>()),
                Op.SetViewport => Viewport(r.Read<Rect>()),
                Op.SetDepthRange => DepthRange(r.Read<DepthRangeCmd>()),
                Op.SetScissor => Scissor(r.Read<Rect>()),
                Op.SetDepthBias => DepthBias(r.Read<DepthBiasCmd>()),
                Op.BindSet => BindSet(r.Read<BindSetCmd>(), ref r),
                Op.BindIndexBuffer => IndexBuffer(r.Read<IndexBufferCmd>()),
                Op.Draw => Draw(r.Read<DrawCmd>()),
                Op.DrawIndexed => DrawIndexed(r.Read<DrawIndexedCmd>()),
                Op.Dispatch => Dispatch(r.Read<DispatchCmd>()),
                Op.UpdateBuffer => Update(r.Read<UpdateBufferCmd>(), ref r),
                Op.ZeroBuffer => Zero(r.Read<ZeroBufferCmd>()),
                Op.PushConstants => Push(r.Read<PushConstantsCmd>(), ref r),
                Op.BindVertexBuffer => VertexBuffer(r.Read<VertexBufferCmd>()),
                Op.DrawIndexedMulti => DrawMulti(r.Read<MultiDrawCmd>(), ref r),
                Op.DrawIndexedIndirect => Indirect(r.Read<IndirectCmd>()),
                Op.DrawIndirect => IndirectArrays(r.Read<IndirectCmd>()),
                Op.SetDepthState => Depth(r.Read<DepthState>()),
                Op.SetRasterState => Raster(r.Read<RasterState>()),
                Op.ReadImages => Readable(r.Read<ReadImagesCmd>(), ref r),
                Op.CopyImage => CopyImg(r.Read<CopyImageCmd>()),
                Op.BlitImage => BlitImg(r.Read<BlitImageCmd>()),
                Op.CopyImageToBuffer => CopyToBuf(r.Read<CopyToBufferCmd>()),
                Op.ClearImage => ClearImg(r.Read<ImageId>()),
                Op.GenerateMips => MipsImg(r.Read<ImageId>()),
                Op.BeginLabel => Label(r.Read<LabelCmd>(), ref r),
                Op.EndLabel => Unlabel(),
                Op.SetShadingRate => ShadingRate(r.Read<ShadingRateCmd>().Coarse),
                _ => Fault($"{op} is not on the Vulkan backend yet"),
            };
            if (!ok) return false;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            _opTicks[(int)op] += now - t;
            _opN[(int)op]++;
            t = now;
        }
        return true;
    }

    private bool Begin(RenderPassCmd h, ref CommandReader r)
    {
        ReadOnlySpan<ColorAttachment> colors = r.Span<ColorAttachment>(h.Colors);
        if (_inPass) return Fault("a render pass began inside another");
        if (_onAsync) return Fault("a render pass on the async compute queue, which cannot draw");
        if (colors.Length + (h.HasDepth ? 1 : 0) == 0 || colors.Length > RenderPassDesc.MaxColors) return Fault($"render pass with {colors.Length} colour attachments");
        colors.CopyTo(_pend);
        _pendCount = colors.Length;
        _pendHasDepth = h.HasDepth;
        _pendDepth = h.Depth;
        _pendRate = h.ShadingRate;
        _inPass = true;
        _scissorSet = false;
        // every pass starts at the whole window-depth range, so a pass that collapses it (the sky dome) or stacks
        // views in it (the GUI) does not leave the next one drawing where it left off
        _depthMin = 0f; _depthMax = 1f;
        return true;
    }

    /// <summary>A pass with no draw still begins rendering, so its clears and layout moves happen.</summary>
    private bool End()
    {
        if (!_inPass) return Fault("a render pass ended that never began");
        if (!EnsureRendering()) return false;
        _vkd.vkCmdEndRendering(_cmd);
        _inPass = _rendering = false;
        _pendRate = default;
        return true;
    }

    /// <summary>Begin the pending pass now: its attachments into their write layout, then rendering. The scissor is
    /// dynamic and Vulkan has no "off", so a pass that set none gets the whole target.</summary>
    private bool EnsureRendering()
    {
        if (_rendering) return true;
        if (!_inPass) return Fault("a draw outside a render pass");
        VkRenderingAttachmentInfo* att = stackalloc VkRenderingAttachmentInfo[Math.Max(_pendCount, 1)];
        int w = 0, h = 0, layers = 1;
        for (int i = 0; i < _pendCount; i++)
        {
            ColorAttachment c = _pend[i];
            // a null image routes the slot nowhere, so the slots after it keep their indices
            if (c.Image.IsNull) { att[i] = new VkRenderingAttachmentInfo { imageView = default, imageLayout = VkImageLayout.Undefined }; continue; }
            if (!_images.TryGet(c.Image.Value, out VkImg img)) return Fault($"render pass colour {i}: stale image");
            if (c.Mip != 0) return Fault($"render pass colour {i}: mip {c.Mip} -- only mip 0 on the Vulkan backend yet");
            VkImageView cv = AttachView(img, c.Layer);
            if (cv.IsNull) return Fault($"render pass colour {i}: layer {c.Layer} is past {img.Desc.Label}");
            if (c.Layer < 0) layers = Math.Max(layers, Layers(img.Desc));
            Transition(img, VkImageLayout.ColorAttachmentOptimal);
            img.Written = true;
            var clear = new VkClearValue();
            clear.color.float32[0] = c.Clear.X; clear.color.float32[1] = c.Clear.Y;
            clear.color.float32[2] = c.Clear.Z; clear.color.float32[3] = c.Clear.W;
            att[i] = new VkRenderingAttachmentInfo
            {
                imageView = cv, imageLayout = VkImageLayout.ColorAttachmentOptimal,
                loadOp = c.Load switch { LoadOp.Load => VkAttachmentLoadOp.Load, LoadOp.Clear => VkAttachmentLoadOp.Clear, _ => VkAttachmentLoadOp.DontCare },
                storeOp = c.Store == StoreOp.Store ? VkAttachmentStoreOp.Store : VkAttachmentStoreOp.DontCare,
                clearValue = clear,
            };
            if (w == 0) { w = img.Desc.W; h = img.Desc.H; }
        }
        var depth = new VkRenderingAttachmentInfo();
        bool stencil = false;
        if (_pendHasDepth)
        {
            DepthAttachment d = _pendDepth;
            if (!_images.TryGet(d.Image.Value, out VkImg img)) return Fault("render pass depth: stale image");
            if (d.Mip != 0) return Fault($"render pass depth: mip {d.Mip} -- only mip 0 on the Vulkan backend yet");
            if (!img.Desc.Fmt.IsDepth()) return Fault($"render pass depth: {img.Desc.Label} is {img.Desc.Fmt}, not a depth format");
            VkImageView dv = AttachView(img, d.Layer);
            if (dv.IsNull) return Fault($"render pass depth: layer {d.Layer} is past {img.Desc.Label}");
            if (d.Layer < 0) layers = Math.Max(layers, Layers(img.Desc));
            Transition(img, VkImageLayout.DepthStencilAttachmentOptimal);
            img.Written = true;
            var clear = new VkClearValue();
            clear.depthStencil = new VkClearDepthStencilValue(d.Depth, 0);
            depth = new VkRenderingAttachmentInfo
            {
                imageView = dv, imageLayout = VkImageLayout.DepthStencilAttachmentOptimal,
                loadOp = d.Load switch { LoadOp.Load => VkAttachmentLoadOp.Load, LoadOp.Clear => VkAttachmentLoadOp.Clear, _ => VkAttachmentLoadOp.DontCare },
                storeOp = d.Store == StoreOp.Store ? VkAttachmentStoreOp.Store : VkAttachmentStoreOp.DontCare,
                clearValue = clear,
            };
            stencil = img.Desc.Fmt.HasStencil();
            if (w == 0) { w = img.Desc.W; h = img.Desc.H; }
        }
        var area = new VkRect2D { extent = new VkExtent2D { width = (uint)w, height = (uint)h } };
        var rate = new VkRenderingFragmentShadingRateAttachmentInfoKHR
        {
            imageLayout = VkImageLayout.FragmentShadingRateAttachmentOptimalKHR,
            shadingRateAttachmentTexelSize = new VkExtent2D(_rateTexel, _rateTexel),
        };
        if (!_pendRate.IsNull && _rateTexel > 0)
        {
            if (!_images.TryGet(_pendRate.Value, out VkImg ri0)) return Fault("render pass shading rate: stale image");
            if (ri0.Desc.W * _rateTexel < w || ri0.Desc.H * _rateTexel < h)
                return Fault($"render pass shading rate: {ri0.Desc.Label} is {ri0.Desc.W}x{ri0.Desc.H} tiles of {_rateTexel} px, short of {w}x{h}");
            Transition(ri0, VkImageLayout.FragmentShadingRateAttachmentOptimalKHR);
            rate.imageView = ri0.View;
        }
        var ri = new VkRenderingInfo
        {
            pNext = rate.imageView.IsNull ? null : &rate,
            renderArea = area, layerCount = (uint)layers, colorAttachmentCount = (uint)_pendCount, pColorAttachments = att,
            pDepthAttachment = _pendHasDepth ? &depth : null, pStencilAttachment = stencil ? &depth : null,
        };
        // a bind after the first draw can place no barrier, so every buffer access before the pass is ordered now
        Order();
        Flush();
        _vkd.vkCmdBeginRendering(_cmd, &ri);
        if (!_scissorSet) _vkd.vkCmdSetScissor(_cmd, 0, 1, &area);
        _rendering = true;
        return true;
    }

    private bool ShadingRate(bool coarse)
    {
        if (_rateTexel == 0) return true;
        var one = new VkExtent2D(1, 1);
        VkFragmentShadingRateCombinerOpKHR* ops = stackalloc VkFragmentShadingRateCombinerOpKHR[2]
        {
            VkFragmentShadingRateCombinerOpKHR.Keep,
            coarse && !_pendRate.IsNull ? VkFragmentShadingRateCombinerOpKHR.Replace : VkFragmentShadingRateCombinerOpKHR.Keep,
        };
        _vkd.vkCmdSetFragmentShadingRateKHR(_cmd, &one, ops);
        return true;
    }

    private bool Bind(PipelineId id)
    {
        if (!_pipelines.TryGet(id.Value, out VkPipe p)) return Fault("SetPipeline: stale pipeline");
        if (!p.Compute && p.Rated != (_inPass && !_pendRate.IsNull && _rateTexel > 0))
            return Fault($"SetPipeline: a pipeline {(p.Rated ? "made for" : "not made for")} a shading-rate image, in a pass {(p.Rated ? "without" : "with")} one");
        if (_onAsync && !p.Compute) return Fault("a graphics pipeline on the async compute queue, which cannot draw");
        _bindPoint = p.Compute ? VkPipelineBindPoint.Compute : VkPipelineBindPoint.Graphics;
        _vkd.vkCmdBindPipeline(_cmd, _bindPoint, p.Pipeline);
        if (!p.Compute) { Depth(p.Depth); Raster(p.Raster); }
        return true;
    }

    private bool Depth(DepthState d)
    {
        _vkd.vkCmdSetDepthTestEnable(_cmd, d.Test);
        _vkd.vkCmdSetDepthWriteEnable(_cmd, d.Write);
        _vkd.vkCmdSetDepthCompareOp(_cmd, Compare(d.Compare));
        return true;
    }

    private bool Raster(RasterState r)
    {
        _vkd.vkCmdSetCullMode(_cmd, Cull(r.Cull));
        _vkd.vkCmdSetFrontFace(_cmd, Winding(r.FrontCw));
        _vkd.vkCmdSetDepthBiasEnable(_cmd, r.DepthBias);
        return true;
    }

    private bool Push(PushConstantsCmd c, ref CommandReader r)
    {
        ReadOnlySpan<byte> bytes = r.Span<byte>(c.Bytes);
        fixed (byte* p = bytes) _vkd.vkCmdPushConstants(_cmd, _pipeLayout, VkShaderStageFlags.All, (uint)c.Offset, (uint)c.Bytes, p);
        return true;
    }

    private bool VertexBuffer(VertexBufferCmd c)
    {
        if (!_buffers.TryGet(c.Buffer.Value, out VkBuf? b)) return Fault("BindVertexBuffer: stale buffer");
        Use(b, VkPipelineStageFlags2.VertexAttributeInput, VkAccessFlags2.VertexAttributeRead);
        VkBuffer vb = b.Buffer;
        ulong off = (ulong)c.Offset;
        _vkd.vkCmdBindVertexBuffers(_cmd, 0, 1, &vb, &off);
        return true;
    }

    /// <summary>A CPU multi-draw as one indirect draw out of this submit's staging, so each draw's index is its place
    /// in the spans -- the draw index GL's multi-draw hands the stage.</summary>
    private bool DrawMulti(MultiDrawCmd c, ref CommandReader r)
    {
        ReadOnlySpan<int> counts = r.Span<int>(c.Draws), firsts = r.Span<int>(c.Draws), bases = r.Span<int>(c.Draws);
        if (!EnsureRendering()) return false;
        if ((uint)c.Draws > _maxDrawIndirect) return Fault($"a multi-draw of {c.Draws}, past the device's {_maxDrawIndirect}");
        if (_multi.Length < c.Draws) _multi = new VkDrawIndexedIndirectCommand[Math.Max(c.Draws, _multi.Length * 2)];
        for (int i = 0; i < c.Draws; i++)
            _multi[i] = new VkDrawIndexedIndirectCommand
            {
                indexCount = (uint)counts[i], instanceCount = 1, firstIndex = (uint)firsts[i], vertexOffset = bases[i], firstInstance = 0,
            };
        if (!Staged(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_multi.AsSpan(0, c.Draws)), out ulong at)) return false;
        _vkd.vkCmdDrawIndexedIndirect(_cmd, _slot!.Stage, at, (uint)c.Draws, IndirectStride);
        _drawN += c.Draws;
        return true;
    }

    /// <summary>Bytes between two indirect commands: five words, GL's and Vulkan's layout alike.</summary>
    private const uint IndirectStride = 20;

    private bool Indirect(IndirectCmd c)
    {
        if (!EnsureRendering()) return false;
        if (!_buffers.TryGet(c.Commands.Value, out VkBuf? cmds)) return Fault("DrawIndexedIndirect: stale command buffer");
        Use(cmds, VkPipelineStageFlags2.DrawIndirect, VkAccessFlags2.IndirectCommandRead);
        _drawN++;
        if (c.MaxDraws <= 0) return true;
        if (c.Count.IsNull)
        {
            _vkd.vkCmdDrawIndexedIndirect(_cmd, cmds.Buffer, (ulong)c.Offset, (uint)c.MaxDraws, IndirectStride);
            return true;
        }
        if (!_buffers.TryGet(c.Count.Value, out VkBuf? count)) return Fault("DrawIndexedIndirect: stale count buffer");
        Use(count, VkPipelineStageFlags2.DrawIndirect, VkAccessFlags2.IndirectCommandRead);
        _vkd.vkCmdDrawIndexedIndirectCount(_cmd, cmds.Buffer, (ulong)c.Offset, count.Buffer, (ulong)c.CountOffset, (uint)c.MaxDraws, IndirectStride);
        return true;
    }

    private bool IndirectArrays(IndirectCmd c)
    {
        if (!EnsureRendering()) return false;
        if (!_buffers.TryGet(c.Commands.Value, out VkBuf? cmds)) return Fault("DrawIndirect: stale command buffer");
        Use(cmds, VkPipelineStageFlags2.DrawIndirect, VkAccessFlags2.IndirectCommandRead);
        _drawN++;
        if (c.MaxDraws > 0) _vkd.vkCmdDrawIndirect(_cmd, cmds.Buffer, (ulong)c.Offset, (uint)c.MaxDraws, ArraysStride);
        return true;
    }

    /// <summary>A <c>VkDrawIndirectCommand</c>'s size: four words.</summary>
    private const uint ArraysStride = 16;

    private bool Viewport(Rect v)
    {
        _viewport = v;
        // same numbers as GL's: both put framebuffer row 0 at y 0 and NDC -1 on it, so an imported image
        // reads back the same way up in either API
        var vp = new VkViewport { x = v.X, y = v.Y, width = v.W, height = v.H, minDepth = _depthMin, maxDepth = _depthMax };
        _vkd.vkCmdSetViewport(_cmd, 0, 1, &vp);
        return true;
    }

    private bool Scissor(Rect s)
    {
        var r = new VkRect2D { offset = new VkOffset2D { x = s.X, y = s.Y }, extent = new VkExtent2D { width = (uint)s.W, height = (uint)s.H } };
        _vkd.vkCmdSetScissor(_cmd, 0, 1, &r);
        _scissorSet = true;
        return true;
    }

    private bool DepthBias(DepthBiasCmd c) { _vkd.vkCmdSetDepthBias(_cmd, c.Units, 0, c.Factor); return true; }

    /// <summary>The descriptor set already written with these bindings, or a new one from the pool: the
    /// images it samples moved into their read layout first, which is why a bind set must come ahead of the pass's
    /// first draw. A dynamic uniform slot's offset is the bind's, not the set's.</summary>
    private bool BindSet(BindSetCmd h, ref CommandReader r)
    {
        ReadOnlySpan<BufferBinding> ubo = r.Span<BufferBinding>(h.Uniforms), ssbo = r.Span<BufferBinding>(h.Storage);
        ReadOnlySpan<ImageBinding> tex = r.Span<ImageBinding>(h.Sampled), img = r.Span<ImageBinding>(h.StorageImages);
        int n = ubo.Length + ssbo.Length + tex.Length + img.Length;
        if (ubo.Length > UniformSlots || ssbo.Length > StorageSlots || tex.Length > SampledSlots || img.Length > ImageSlots)
            return Fault($"bind set {h.Set}: more bindings than the set has slots");
        VkWriteDescriptorSet* w = stackalloc VkWriteDescriptorSet[Math.Max(n, 1)];
        Span<VkDescriptorBufferInfo> bi = stackalloc VkDescriptorBufferInfo[Math.Max(ubo.Length + ssbo.Length, 1)];
        Span<VkDescriptorImageInfo> ii = stackalloc VkDescriptorImageInfo[Math.Max(tex.Length + img.Length, 1)];
        Span<BindingKey> key = stackalloc BindingKey[Math.Max(n, 1)];
        Span<uint> dyn = stackalloc uint[(int)_dynUbos];
        int k = 0, b = 0, t = 0;
        _setDraws = _inPass || _bindPoint == VkPipelineBindPoint.Graphics;
        if (!_setDraws) _cBufN = _cImgN = 0;
        foreach (BufferBinding u in ubo) if (!Buf(u, VulkanToolchain.UniformBase, VkDescriptorType.UniformBuffer, w, bi, key, dyn, ref k, ref b)) return false;
        foreach (BufferBinding u in ssbo) if (!Buf(u, VulkanToolchain.StorageBase, VkDescriptorType.StorageBuffer, w, bi, key, dyn, ref k, ref b)) return false;
        foreach (ImageBinding s in tex) if (!Img(s, VulkanToolchain.SampledBase, VkDescriptorType.CombinedImageSampler, w, ii, key, ref k, ref t)) return false;
        foreach (ImageBinding s in img) if (!Img(s, VulkanToolchain.ImageBase, VkDescriptorType.StorageImage, w, ii, key, ref k, ref t)) return false;

        ReadOnlySpan<BindingKey> written = key[..k];
        VkDescriptorSet set;
        if (_sets.Cache.TryGet(written, out ulong held)) set = new VkDescriptorSet(held);
        else
        {
            if (_sets.Left == 0 && !RetireSets()) return false;
            VkDescriptorSetLayout layout = _setLayout;
            var ai = new VkDescriptorSetAllocateInfo { descriptorPool = _sets.Pool, descriptorSetCount = 1, pSetLayouts = &layout };
            if (_vkd.vkAllocateDescriptorSets(&ai, &set) != VkResult.Success) return Fault($"the descriptor pool refused a set with {_sets.Left} of {SetsPerPool} left");
            _sets.Left--;
            for (int i = 0; i < k; i++) w[i].dstSet = set;
            _vkd.vkUpdateDescriptorSets((uint)k, w, 0, null);
            _sets.Cache.Add(written, set.Handle);
            _setMadeN++;
        }
        // ponytail: every bind set lands on set 0, the one GLSL without a set qualifier reads
        fixed (uint* d = dyn) _vkd.vkCmdBindDescriptorSets(_cmd, _inPass ? VkPipelineBindPoint.Graphics : _bindPoint, _pipeLayout, 0, 1, &set, _dynUbos, d);
        _setN++;
        return true;
    }

    private bool Buf(BufferBinding u, uint at, VkDescriptorType type, VkWriteDescriptorSet* w, Span<VkDescriptorBufferInfo> bi,
                     Span<BindingKey> key, Span<uint> dyn, ref int k, ref int b)
    {
        if (!_buffers.TryGet(u.Buffer.Value, out VkBuf? buf)) return Fault($"bind set: stale buffer on slot {u.Slot}");
        VkAccessFlags2 access = type == VkDescriptorType.StorageBuffer ? StorageAccess : VkAccessFlags2.UniformRead;
        if (_setDraws) Use(buf, DrawStages, access);
        else { _cBufs[_cBufN] = buf; _cAccess[_cBufN++] = access; }
        ulong offset = (ulong)u.Offset, range = u.Bytes == 0 ? Vortice.Vulkan.Vulkan.VK_WHOLE_SIZE : (ulong)u.Bytes;
        if (type == VkDescriptorType.UniformBuffer && u.Slot < _dynUbos)
        {
            type = VkDescriptorType.UniformBufferDynamic;
            // a whole-buffer range leaves no room past a dynamic offset, so that one keeps its offset in the set
            if (u.Bytes != 0)
            {
                if (offset > uint.MaxValue) return Fault($"bind set: uniform slot {u.Slot}'s offset {offset} is past a dynamic offset's 32 bits");
                dyn[(int)u.Slot] = (uint)offset;
                offset = 0;
            }
        }
        bi[b] = new VkDescriptorBufferInfo { buffer = buf.Buffer, offset = offset, range = range };
        key[k] = new BindingKey(at + u.Slot, type, default, buf.Buffer.Handle, 0, offset, range);
        w[k++] = new VkWriteDescriptorSet { dstBinding = at + u.Slot, descriptorCount = 1, descriptorType = type, pBufferInfo = (VkDescriptorBufferInfo*)Unsafe.AsPointer(ref bi[b++]) };
        return true;
    }

    /// <summary>Hand the named images over for sampling, outside any render pass: a bind of one inside a pass
    /// after its first draw can place no barrier, and a target the frame just wrote is in its write layout.</summary>
    private bool Readable(ReadImagesCmd h, ref CommandReader r)
    {
        ReadOnlySpan<ImageId> ids = r.Span<ImageId>(h.Images);
        if (_inPass) return Fault("ReadImages inside a render pass -- the hand-over is a barrier, which a pass forbids");
        // the layout a bind of that kind wants, so the bind itself finds the image already in it
        VkImageLayout to = h.Access is Access.StorageRead or Access.StorageWrite
            ? VkImageLayout.General : VkImageLayout.ShaderReadOnlyOptimal;
        foreach (ImageId id in ids)
            if (!id.IsNull && _images.TryGet(id.Value, out VkImg img)) Transition(img, to, h.Access != Access.StorageRead);
        return true;
    }

    private bool Img(ImageBinding s, uint at, VkDescriptorType type, VkWriteDescriptorSet* w, Span<VkDescriptorImageInfo> ii,
                     Span<BindingKey> key, ref int k, ref int t)
    {
        if (!_images.TryGet(s.Image.Value, out VkImg img)) return Fault($"bind set: stale image on slot {s.Slot}");
        bool storage = type == VkDescriptorType.StorageImage;
        if (UsageMissing(img.Desc.Use, storage) is { } why) return Fault($"bind set: slot {s.Slot}'s image {img.Desc.Label} {why}");
        VkImageLayout want = storage ? VkImageLayout.General : VkImageLayout.ShaderReadOnlyOptimal;
        if (!_rendering) Transition(img, want);
        else if (img.Layout != want)
            return Fault($"bind set: slot {s.Slot}'s image needs a barrier inside a begun render pass -- bind before the first draw");
        if (storage && _setDraws) img.Written = true;
        else if (storage) _cImgs[_cImgN++] = img;
        VkSampler sampler = _nearest;
        if (!storage && !s.Sampler.IsNull && !_samplers.TryGet(s.Sampler.Value, out sampler)) return Fault($"bind set: stale sampler on slot {s.Slot}");
        VkImageView view = ViewOf(img, storage ? s.Mip : -1);
        if (view.IsNull)
            return Fault($"bind set: slot {s.Slot}'s image {img.Desc.Label} has no view -- its alias group dropped it (logged above)");
        ii[t] = new VkDescriptorImageInfo { sampler = storage ? default : sampler, imageView = view, imageLayout = want };
        key[k] = new BindingKey(at + s.Slot, type, want, ii[t].imageView.Handle, ii[t].sampler.Handle, 0, 0);
        w[k++] = new VkWriteDescriptorSet { dstBinding = at + s.Slot, descriptorCount = 1, descriptorType = type, pImageInfo = (VkDescriptorImageInfo*)Unsafe.AsPointer(ref ii[t++]) };
        return true;
    }

    /// <summary>What an image made for <paramref name="use"/> lacks to be bound as a storage image or, without
    /// <paramref name="storage"/>, sampled; null when it was made for that bind.</summary>
    internal static string? UsageMissing(ImageUse use, bool storage)
        => storage ? (use & ImageUse.Storage) == 0 ? "was not created for storage" : null
                   : (use & ImageUse.Sampled) == 0 ? "was not created for sampling" : null;

    private bool IndexBuffer(IndexBufferCmd c)
    {
        if (!_buffers.TryGet(c.Buffer.Value, out VkBuf? b)) return Fault("BindIndexBuffer: stale buffer");
        Use(b, VkPipelineStageFlags2.IndexInput, VkAccessFlags2.IndexRead);
        _vkd.vkCmdBindIndexBuffer(_cmd, b.Buffer, (ulong)c.Offset, c.Type == IndexType.U16 ? VkIndexType.Uint16 : VkIndexType.Uint32);
        return true;
    }

    private bool Draw(DrawCmd c)
    {
        if (!EnsureRendering()) return false;
        _vkd.vkCmdDraw(_cmd, (uint)c.Verts, (uint)c.Insts, (uint)c.FirstVert, (uint)c.FirstInst);
        _drawN++;
        return true;
    }

    private bool DrawIndexed(DrawIndexedCmd c)
    {
        if (!EnsureRendering()) return false;
        _vkd.vkCmdDrawIndexed(_cmd, (uint)c.Indices, (uint)c.Insts, (uint)c.FirstIndex, c.BaseVertex, (uint)c.FirstInst);
        _drawN++;
        return true;
    }

    private bool Dispatch(DispatchCmd c)
    {
        if (_inPass) return Fault("a dispatch inside a render pass");
        // the set's storage images and buffers count as written: a dispatch after one that wrote them waits on it
        for (int i = 0; i < _cImgN; i++) Transition(_cImgs[i], VkImageLayout.General);
        for (int i = 0; i < _cBufN; i++) Use(_cBufs[i], VkPipelineStageFlags2.ComputeShader, _cAccess[i]);
        Flush();
        _vkd.vkCmdDispatch(_cmd, (uint)c.X, (uint)c.Y, (uint)c.Z);
        for (int i = 0; i < _cImgN; i++) _cImgs[i].Written = true;
        return true;
    }

    private bool Update(UpdateBufferCmd c, ref CommandReader r)
    {
        ReadOnlySpan<byte> bytes = r.Span<byte>(c.Bytes);
        if (_inPass) return Fault("UpdateBuffer inside a render pass");
        if (!_buffers.TryGet(c.Buffer.Value, out VkBuf b)) return Fault("UpdateBuffer: stale buffer");
        if (c.Offset < 0 || c.Offset + c.Bytes > b.Desc.Bytes || (c.Bytes & 3) != 0 || (c.Offset & 3) != 0)
            return Fault($"UpdateBuffer of {c.Bytes} bytes at {c.Offset} into {b.Desc.Label}'s {b.Desc.Bytes}, in whole words");
        Use(b, VkPipelineStageFlags2.AllTransfer, VkAccessFlags2.TransferWrite);
        Flush();
        if (c.Bytes <= MaxInlineUpdate)
        {
            fixed (byte* p = bytes) _vkd.vkCmdUpdateBuffer(_cmd, b.Buffer, (ulong)c.Offset, (ulong)c.Bytes, p);
            _inlineBytes += c.Bytes;
        }
        else
        {
            if (!Staged(bytes, out ulong at)) return false;
            var region = new VkBufferCopy { srcOffset = at, dstOffset = (ulong)c.Offset, size = (ulong)c.Bytes };
            _vkd.vkCmdCopyBuffer(_cmd, _slot!.Stage, b.Buffer, 1, &region);
        }
        if (b.UpCalls++ == 0) _upTouched.Add(b);
        b.UpBytes += c.Bytes;
        return true;
    }

    /// <summary>Copy <paramref name="bytes"/> into this submit's staging and say where. A slot that outgrows its
    /// staging gets a larger one; the old one, which earlier copies in this submit still read, dies later.</summary>
    private bool Staged(ReadOnlySpan<byte> bytes, out ulong at)
    {
        Slot s = _slot!;
        at = 0;
        ulong len = (ulong)bytes.Length;
        if (s.StageUsed + len > s.StageBytes)
        {
            if (s.Stage.IsNotNull)
            {
                VkBuffer ob = s.Stage;
                VmaAllocation oa = s.StageAlloc;
                Later(() => Vma.vmaDestroyBuffer(_vma, ob, oa));
            }
            ulong size = Math.Max(MinStage, (s.StageUsed + len) * 2);
            if (!HostBuffer(size, VkBufferUsageFlags.TransferSrc | VkBufferUsageFlags.IndirectBuffer, VmaAllocationCreateFlags.HostAccessSequentialWrite, out s.Stage, out s.StageAlloc))
            {
                s.StageBytes = s.StageUsed = 0;
                return Fault($"staging: VMA refused {size} bytes");
            }
            s.StageBytes = size;
            s.StageUsed = 0;
            _log.Info($"VkBackend: staging grew to {size} bytes -- one allocation per slot, at the frame's high water");
        }
        at = s.StageUsed;
        fixed (byte* p = bytes)
            if (Vma.vmaCopyMemoryToAllocation(_vma, p, s.StageAlloc, at, len) != VkResult.Success)
                return Fault("staging: the copy into the mapped allocation was refused");
        _stagedBytes += (long)len;
        s.StageUsed = (at + len + StageAlign - 1) & ~(StageAlign - 1);
        return true;
    }

    /// <summary>A host-visible buffer VMA maps for each copy; false when VMA refuses it.</summary>
    private bool HostBuffer(ulong size, VkBufferUsageFlags use, VmaAllocationCreateFlags access, out VkBuffer b, out VmaAllocation a)
    {
        var bci = new VkBufferCreateInfo { size = size, usage = use, sharingMode = VkSharingMode.Exclusive };
        var aci = new VmaAllocationCreateInfo { usage = VmaMemoryUsage.Auto, flags = access };
        VkBuffer nb;
        VmaAllocation na;
        bool ok = Vma.vmaCreateBuffer(_vma, &bci, &aci, &nb, &na, null) == VkResult.Success;
        b = ok ? nb : default;
        a = ok ? na : default;
        return ok;
    }

    private bool Zero(ZeroBufferCmd c)
    {
        if (_inPass) return Fault("ZeroBuffer inside a render pass");
        if (!_buffers.TryGet(c.Buffer.Value, out VkBuf b)) return Fault("ZeroBuffer: stale buffer");
        long bytes = c.Bytes == 0 ? b.Desc.Bytes - c.Offset : c.Bytes;
        if (c.Offset < 0 || bytes < 0 || c.Offset + bytes > b.Desc.Bytes || ((c.Offset | bytes) & 3) != 0)
            return Fault($"ZeroBuffer: {bytes} bytes at {c.Offset} of {b.Desc.Label}'s {b.Desc.Bytes}, in whole words");
        if (bytes == 0) return true;
        Use(b, VkPipelineStageFlags2.AllTransfer, VkAccessFlags2.TransferWrite);
        Flush();
        _vkd.vkCmdFillBuffer(_cmd, b.Buffer, (ulong)c.Offset, (ulong)bytes, 0);
        return true;
    }

    /// <summary>The first <paramref name="dst"/>.Length bytes of <paramref name="b"/>, copied back with the queue
    /// waited out. Stalls the whole queue: a pass's on-device check calls it once per device, at the first frame.
    /// A copy that never finishes leaks its scratch rather than freeing what the GPU may still be using.</summary>
    internal bool ReadBack(BufferId b, Span<byte> dst)
    {
        if (!OnThread("ReadBack")) return false;
        if (!_buffers.TryGet(b.Value, out VkBuf src) || dst.Length > src.Desc.Bytes) return Fault("ReadBack: stale or short buffer");
        if (!HostBuffer((ulong)Math.Max(dst.Length, 4), VkBufferUsageFlags.TransferDst, VmaAllocationCreateFlags.HostAccessRandom, out VkBuffer hb, out VmaAllocation ha))
            return Fault($"ReadBack: VMA refused {dst.Length} bytes");
        var ai = new VkCommandBufferAllocateInfo { commandPool = _cmdPool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
        VkCommandBuffer cb;
        if (_vkd.vkAllocateCommandBuffers(&ai, &cb) != VkResult.Success)
        {
            Vma.vmaDestroyBuffer(_vma, hb, ha);
            return Fault("ReadBack: no command buffer");
        }
        var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        _vkd.vkBeginCommandBuffer(cb, &bi);
        // the frame leaves its last writes unordered until a use asks; this copy is one
        var pre = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.AllCommands, srcAccessMask = VkAccessFlags2.MemoryWrite,
            dstStageMask = VkPipelineStageFlags2.AllTransfer, dstAccessMask = VkAccessFlags2.TransferRead,
        };
        var pdi = new VkDependencyInfo { memoryBarrierCount = 1, pMemoryBarriers = &pre };
        _vkd.vkCmdPipelineBarrier2(cb, &pdi);
        var region = new VkBufferCopy { size = (ulong)dst.Length };
        _vkd.vkCmdCopyBuffer(cb, src.Buffer, hb, 1, &region);
        var mb = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.AllTransfer, srcAccessMask = VkAccessFlags2.TransferWrite,
            dstStageMask = VkPipelineStageFlags2.Host, dstAccessMask = VkAccessFlags2.HostRead,
        };
        var di = new VkDependencyInfo { memoryBarrierCount = 1, pMemoryBarriers = &mb };
        _vkd.vkCmdPipelineBarrier2(cb, &di);
        _vkd.vkEndCommandBuffer(cb);
        VkResult run = SubmitAndIdle(cb, out bool queued);
        if (queued && run != VkResult.Success)
            return Fault("ReadBack: the copy never finished -- its scratch is leaked, not freed under the GPU");
        bool ok = queued;
        fixed (byte* p = dst)
            ok = ok && Vma.vmaCopyAllocationToMemory(_vma, ha, 0, p, (ulong)dst.Length) == VkResult.Success;
        _vkd.vkFreeCommandBuffers(_cmdPool, 1, &cb);
        Vma.vmaDestroyBuffer(_vma, hb, ha);
        return ok || Fault("ReadBack: the copy was refused");
    }
}
