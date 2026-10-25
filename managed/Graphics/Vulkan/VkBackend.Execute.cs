using System;
using System.Runtime.CompilerServices;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Queue family indices with a meaning of their own (vulkan_core.h).</summary>
    private const uint QueueFamilyIgnored = ~0u, QueueFamilyExternal = ~1u;
    /// <summary>vkCmdUpdateBuffer's ceiling on one write.</summary>
    private const int MaxInlineUpdate = 65536;

    /// <summary>The render pass recorded but not begun: Vulkan forbids barriers inside one, so it begins at its first
    /// draw, after the pass's bind set has moved every sampled image into its read layout.</summary>
    private readonly ColorAttachment[] _pend = new ColorAttachment[RenderPassDesc.MaxColors];
    private int _pendCount;
    private bool _inPass, _rendering, _scissorSet;
    private VkPipelineBindPoint _bindPoint = VkPipelineBindPoint.Graphics;
    private VkCommandBuffer _cmd;
    private Slot? _slot;

    /// <summary>Record <paramref name="list"/> into one command buffer and run it between GL's two semaphore
    /// operations: GL finishes everything it queued (the mirrors included) and signals, Vulkan waits, renders and
    /// signals, GL waits before anything later reads an output. No submit waits on a semaphore nobody signalled.</summary>
    public bool Submit(CommandList list)
    {
        if (!OnThread("Submit")) return false;
        Slot s = _slots[_submits % Slots];
        VkFence fence = s.Fence;
        if (_vkd.vkWaitForFences(1, &fence, true, FenceTimeoutNs) != VkResult.Success)
            return Fault("the previous submit on this slot never finished");
        _vkd.vkResetDescriptorPool(s.Pool, 0);
        _vkd.vkResetCommandBuffer(s.Cmd, 0);
        var bi = new VkCommandBufferBeginInfo { flags = VkCommandBufferUsageFlags.OneTimeSubmit };
        _vkd.vkBeginCommandBuffer(s.Cmd, &bi);
        _cmd = s.Cmd;
        _slot = s;
        foreach (VkImg g in _shared) { g.Layout = VkImageLayout.ShaderReadOnlyOptimal; g.Acquired = false; }
        _inPass = _rendering = false;

        bool ok = Record(list.TakePending());
        if (_rendering) _vkd.vkCmdEndRendering(_cmd);
        _inPass = _rendering = false;
        foreach (VkImg g in _shared) if (g.Acquired) Release(g);
        _vkd.vkEndCommandBuffer(_cmd);
        if (!ok) return false;

        GlInterop.Signal(_glGlDone, _glTex, _glLayouts);
        VkSemaphore wait = _glDone, signal = _vkDone;
        VkPipelineStageFlags stage = VkPipelineStageFlags.AllCommands;
        VkCommandBuffer cb = _cmd;
        var si = new VkSubmitInfo
        {
            waitSemaphoreCount = 1, pWaitSemaphores = &wait, pWaitDstStageMask = &stage,
            commandBufferCount = 1, pCommandBuffers = &cb, signalSemaphoreCount = 1, pSignalSemaphores = &signal,
        };
        _vkd.vkResetFences(1, &fence);
        if (_vkd.vkQueueSubmit(_queue, 1, &si, fence) != VkResult.Success)
            return Fault("vkQueueSubmit refused the pass -- GL's signal is now unmatched and the device is unusable");
        GlInterop.Wait(_glVkDone, _glTex, _glLayouts);
        _submits++;
        DrainDead();
        return true;
    }

    private bool Record(CommandReader r)
    {
        while (r.Next(out Op op))
        {
            bool ok = op switch
            {
                Op.BeginRenderPass => Begin(r.Read<RenderPassCmd>(), ref r),
                Op.EndRenderPass => End(),
                Op.SetPipeline => Bind(r.Read<PipelineId>()),
                Op.SetViewport => Viewport(r.Read<Rect>()),
                Op.SetScissor => Scissor(r.Read<Rect>()),
                Op.SetDepthBias => DepthBias(r.Read<DepthBiasCmd>()),
                Op.BindSet => BindSet(r.Read<BindSetCmd>(), ref r),
                Op.BindIndexBuffer => IndexBuffer(r.Read<IndexBufferCmd>()),
                Op.Draw => Draw(r.Read<DrawCmd>()),
                Op.DrawIndexed => DrawIndexed(r.Read<DrawIndexedCmd>()),
                Op.Dispatch => Dispatch(r.Read<DispatchCmd>()),
                Op.UpdateBuffer => Update(r.Read<UpdateBufferCmd>(), ref r),
                // ponytail: indirect draws, image copies and blits join with the first pass that records them
                _ => Fault($"{op} is not on the Vulkan backend yet"),
            };
            if (!ok) return false;
        }
        return true;
    }

    private bool Begin(RenderPassCmd h, ref CommandReader r)
    {
        ReadOnlySpan<ColorAttachment> colors = r.Span<ColorAttachment>(h.Colors);
        if (_inPass) return Fault("a render pass began inside another");
        if (h.HasDepth) return Fault("a depth attachment is not on the Vulkan backend yet");
        if (colors.Length == 0 || colors.Length > RenderPassDesc.MaxColors) return Fault($"render pass with {colors.Length} colour attachments");
        colors.CopyTo(_pend);
        _pendCount = colors.Length;
        _inPass = true;
        _scissorSet = false;
        return true;
    }

    private bool End()
    {
        if (!_inPass) return Fault("a render pass ended that never began");
        if (_rendering) _vkd.vkCmdEndRendering(_cmd);
        _inPass = _rendering = false;
        return true;
    }

    /// <summary>Begin the pending pass now: its attachments into their write layout, then rendering. The scissor is
    /// dynamic and Vulkan has no "off", so a pass that set none gets the whole target.</summary>
    private bool EnsureRendering()
    {
        if (_rendering) return true;
        if (!_inPass) return Fault("a draw outside a render pass");
        VkRenderingAttachmentInfo* att = stackalloc VkRenderingAttachmentInfo[_pendCount];
        int w = 0, h = 0;
        for (int i = 0; i < _pendCount; i++)
        {
            ColorAttachment c = _pend[i];
            if (!_images.TryGet(c.Image.Value, out VkImg img)) return Fault($"render pass colour {i}: stale image");
            if (c.Mip != 0 || c.Layer > 0) return Fault($"render pass colour {i}: mip {c.Mip} layer {c.Layer} -- only mip 0 of a 2D image on the Vulkan backend yet");
            Transition(img, VkImageLayout.ColorAttachmentOptimal);
            var clear = new VkClearValue();
            clear.color.float32[0] = c.Clear.X; clear.color.float32[1] = c.Clear.Y;
            clear.color.float32[2] = c.Clear.Z; clear.color.float32[3] = c.Clear.W;
            att[i] = new VkRenderingAttachmentInfo
            {
                imageView = img.View, imageLayout = VkImageLayout.ColorAttachmentOptimal,
                loadOp = c.Load switch { LoadOp.Load => VkAttachmentLoadOp.Load, LoadOp.Clear => VkAttachmentLoadOp.Clear, _ => VkAttachmentLoadOp.DontCare },
                storeOp = c.Store == StoreOp.Store ? VkAttachmentStoreOp.Store : VkAttachmentStoreOp.DontCare,
                clearValue = clear,
            };
            if (i == 0) { w = img.Desc.W; h = img.Desc.H; }
        }
        var area = new VkRect2D { extent = new VkExtent2D { width = (uint)w, height = (uint)h } };
        var ri = new VkRenderingInfo { renderArea = area, layerCount = 1, colorAttachmentCount = (uint)_pendCount, pColorAttachments = att };
        _vkd.vkCmdBeginRendering(_cmd, &ri);
        if (!_scissorSet) _vkd.vkCmdSetScissor(_cmd, 0, 1, &area);
        _rendering = true;
        return true;
    }

    private bool Bind(PipelineId id)
    {
        if (!_pipelines.TryGet(id.Value, out VkPipe p)) return Fault("SetPipeline: stale pipeline");
        _bindPoint = p.Compute ? VkPipelineBindPoint.Compute : VkPipelineBindPoint.Graphics;
        _vkd.vkCmdBindPipeline(_cmd, _bindPoint, p.Pipeline);
        return true;
    }

    private bool Viewport(Rect v)
    {
        // same numbers as GL's: both put framebuffer row 0 at y 0 and NDC -1 on it, so an imported image
        // reads back the same way up in either API
        var vp = new VkViewport { x = v.X, y = v.Y, width = v.W, height = v.H, minDepth = 0, maxDepth = 1 };
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

    /// <summary>One descriptor set from this submit's pool: the images it samples mirrored and moved into their read
    /// layout first, which is why a bind set must come ahead of the pass's first draw.</summary>
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
        int k = 0, b = 0, t = 0;
        foreach (BufferBinding u in ubo) if (!Buf(u, VulkanToolchain.UniformBase, VkDescriptorType.UniformBuffer, w, bi, ref k, ref b)) return false;
        foreach (BufferBinding u in ssbo) if (!Buf(u, VulkanToolchain.StorageBase, VkDescriptorType.StorageBuffer, w, bi, ref k, ref b)) return false;
        foreach (ImageBinding s in tex) if (!Img(s, VulkanToolchain.SampledBase, VkDescriptorType.CombinedImageSampler, w, ii, ref k, ref t)) return false;
        foreach (ImageBinding s in img) if (!Img(s, VulkanToolchain.ImageBase, VkDescriptorType.StorageImage, w, ii, ref k, ref t)) return false;

        VkDescriptorSetLayout layout = _setLayout;
        var ai = new VkDescriptorSetAllocateInfo { descriptorPool = _slot!.Pool, descriptorSetCount = 1, pSetLayouts = &layout };
        VkDescriptorSet set;
        if (_vkd.vkAllocateDescriptorSets(&ai, &set) != VkResult.Success) return Fault($"the submit's {SetsPerSubmit} descriptor sets are spent");
        for (int i = 0; i < k; i++) w[i].dstSet = set;
        _vkd.vkUpdateDescriptorSets((uint)k, w, 0, null);
        // ponytail: every bind set lands on set 0, the one GLSL without a set qualifier reads
        _vkd.vkCmdBindDescriptorSets(_cmd, _inPass ? VkPipelineBindPoint.Graphics : _bindPoint, _pipeLayout, 0, 1, &set, 0, null);
        return true;
    }

    private bool Buf(BufferBinding u, uint at, VkDescriptorType type, VkWriteDescriptorSet* w, Span<VkDescriptorBufferInfo> bi, ref int k, ref int b)
    {
        if (!_buffers.TryGet(u.Buffer.Value, out VkBuf buf)) return Fault($"bind set: stale buffer on slot {u.Slot}");
        bi[b] = new VkDescriptorBufferInfo { buffer = buf.Buffer, offset = (ulong)u.Offset, range = u.Bytes == 0 ? Vortice.Vulkan.Vulkan.VK_WHOLE_SIZE : (ulong)u.Bytes };
        w[k++] = new VkWriteDescriptorSet { dstBinding = at + u.Slot, descriptorCount = 1, descriptorType = type, pBufferInfo = (VkDescriptorBufferInfo*)Unsafe.AsPointer(ref bi[b++]) };
        return true;
    }

    private bool Img(ImageBinding s, uint at, VkDescriptorType type, VkWriteDescriptorSet* w, Span<VkDescriptorImageInfo> ii, ref int k, ref int t)
    {
        if (!_images.TryGet(s.Image.Value, out VkImg img)) return Fault($"bind set: stale image on slot {s.Slot}");
        if (img.MirrorOf != 0 && img.MirroredAt != _submits)
        {
            GlInterop.Mirror(img.MirrorOf, img.GlTex, img.Desc.W, img.Desc.H, img.MirrorDepth, _depthCopy);
            img.MirroredAt = _submits;
        }
        bool storage = type == VkDescriptorType.StorageImage;
        VkImageLayout want = storage ? VkImageLayout.General : VkImageLayout.ShaderReadOnlyOptimal;
        if (_rendering && (img.Layout != want || !img.Acquired))
            return Fault($"bind set: slot {s.Slot}'s image needs a barrier inside a begun render pass -- bind before the first draw");
        Transition(img, want);
        VkSampler sampler = _nearest;
        if (!storage && !s.Sampler.IsNull && !_samplers.TryGet(s.Sampler.Value, out sampler)) return Fault($"bind set: stale sampler on slot {s.Slot}");
        ii[t] = new VkDescriptorImageInfo { sampler = storage ? default : sampler, imageView = img.View, imageLayout = want };
        w[k++] = new VkWriteDescriptorSet { dstBinding = at + s.Slot, descriptorCount = 1, descriptorType = type, pImageInfo = (VkDescriptorImageInfo*)Unsafe.AsPointer(ref ii[t++]) };
        return true;
    }

    private bool IndexBuffer(IndexBufferCmd c)
    {
        if (!_buffers.TryGet(c.Buffer.Value, out VkBuf b)) return Fault("BindIndexBuffer: stale buffer");
        _vkd.vkCmdBindIndexBuffer(_cmd, b.Buffer, (ulong)c.Offset, c.Type == IndexType.U16 ? VkIndexType.Uint16 : VkIndexType.Uint32);
        return true;
    }

    private bool Draw(DrawCmd c)
    {
        if (!EnsureRendering()) return false;
        _vkd.vkCmdDraw(_cmd, (uint)c.Verts, (uint)c.Insts, (uint)c.FirstVert, (uint)c.FirstInst);
        return true;
    }

    private bool DrawIndexed(DrawIndexedCmd c)
    {
        if (!EnsureRendering()) return false;
        _vkd.vkCmdDrawIndexed(_cmd, (uint)c.Indices, (uint)c.Insts, (uint)c.FirstIndex, c.BaseVertex, (uint)c.FirstInst);
        return true;
    }

    private bool Dispatch(DispatchCmd c)
    {
        if (_inPass) return Fault("a dispatch inside a render pass");
        _vkd.vkCmdDispatch(_cmd, (uint)c.X, (uint)c.Y, (uint)c.Z);
        FullBarrier();
        return true;
    }

    private bool Update(UpdateBufferCmd c, ref CommandReader r)
    {
        ReadOnlySpan<byte> bytes = r.Span<byte>(c.Bytes);
        if (_inPass) return Fault("UpdateBuffer inside a render pass");
        if (!_buffers.TryGet(c.Buffer.Value, out VkBuf b)) return Fault("UpdateBuffer: stale buffer");
        if (c.Bytes > MaxInlineUpdate || (c.Bytes & 3) != 0 || (c.Offset & 3) != 0)
            return Fault($"UpdateBuffer of {c.Bytes} bytes at {c.Offset}: Vulkan's inline update takes up to 64 KiB in whole words");
        fixed (byte* p = bytes) _vkd.vkCmdUpdateBuffer(_cmd, b.Buffer, (ulong)c.Offset, (ulong)c.Bytes, p);
        FullBarrier();
        return true;
    }

    /// <summary>Every write before it visible to every access after it. ponytail: one coarse barrier per write,
    /// the frame graph derives narrow ones once a pass with real hazards moves over.</summary>
    private void FullBarrier()
    {
        var mb = new VkMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.AllCommands, srcAccessMask = VkAccessFlags2.MemoryWrite,
            dstStageMask = VkPipelineStageFlags2.AllCommands, dstAccessMask = VkAccessFlags2.MemoryRead | VkAccessFlags2.MemoryWrite,
        };
        var di = new VkDependencyInfo { memoryBarrierCount = 1, pMemoryBarriers = &mb };
        _vkd.vkCmdPipelineBarrier2(_cmd, &di);
    }

    /// <summary>Move <paramref name="img"/> into <paramref name="to"/>; its first use in a submit also takes it from
    /// GL (the external queue family), which handed it over in the shader-read layout.</summary>
    private void Transition(VkImg img, VkImageLayout to)
    {
        if (img.Acquired && img.Layout == to && to == VkImageLayout.ShaderReadOnlyOptimal) return;
        Barrier(img, to, img.Acquired ? QueueFamilyIgnored : QueueFamilyExternal, img.Acquired ? QueueFamilyIgnored : _family);
        img.Layout = to;
        img.Acquired = true;
    }

    /// <summary>Hand <paramref name="img"/> back to GL in the layout GL's wait names.</summary>
    private void Release(VkImg img)
    {
        Barrier(img, VkImageLayout.ShaderReadOnlyOptimal, _family, QueueFamilyExternal);
        img.Layout = VkImageLayout.ShaderReadOnlyOptimal;
        img.Acquired = false;
    }

    private void Barrier(VkImg img, VkImageLayout to, uint srcFamily, uint dstFamily)
    {
        var b = new VkImageMemoryBarrier2
        {
            srcStageMask = VkPipelineStageFlags2.AllCommands, srcAccessMask = VkAccessFlags2.MemoryWrite,
            dstStageMask = VkPipelineStageFlags2.AllCommands, dstAccessMask = VkAccessFlags2.MemoryRead | VkAccessFlags2.MemoryWrite,
            oldLayout = img.Layout, newLayout = to, srcQueueFamilyIndex = srcFamily, dstQueueFamilyIndex = dstFamily,
            image = img.Image,
            subresourceRange = new VkImageSubresourceRange { aspectMask = VkImageAspectFlags.Color, levelCount = 1, layerCount = 1 },
        };
        var di = new VkDependencyInfo { imageMemoryBarrierCount = 1, pImageMemoryBarriers = &b };
        _vkd.vkCmdPipelineBarrier2(_cmd, &di);
    }
}
