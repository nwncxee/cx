using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Rhi;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class GlDevice
{
    private readonly record struct Attach(ImageId Image, int Mip, int Layer);

    /// <summary>A framebuffer's attachments: the colours first, the depth attachment in the last element.</summary>
    [InlineArray(RenderPassDesc.MaxColors + 1)]
    private struct AttachSet { private Attach _e; }

    private struct FboEntry
    {
        public AttachSet Key;
        public int Colors;
        public bool HasDepth;
        public uint Fbo;
    }

    /// <summary>One framebuffer per distinct attachment set, made on first use. ponytail: a linear scan, a hash
    /// when a frame holds more than a few dozen sets.</summary>
    private FboEntry[] _fbos = new FboEntry[16];
    private int _fboCount;

    /// <summary>Units this render pass bound a sampler object on, one bit each, unbound at its end.</summary>
    private ulong _samplerUnits;

    private uint _topo = Gl.TRIANGLES, _indexType = Gl.UNSIGNED_SHORT;
    private long _indexOffset;

    public bool Submit(CommandList list)
    {
        if (!OnThread("Submit")) return false;
        CommandReader r = list.TakePending();
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
                Op.DrawIndexedIndirect => Indirect(r.Read<IndirectCmd>()),
                Op.Dispatch => Dispatch(r.Read<DispatchCmd>()),
                Op.CopyImage => Copy(r.Read<CopyImageCmd>()),
                Op.BlitImage => Blit(r.Read<BlitImageCmd>()),
                Op.UpdateBuffer => Update(r.Read<UpdateBufferCmd>(), ref r),
                _ => Fault($"unknown opcode {op}"),
            };
            if (!ok) return false;
        }
        return true;
    }

    private bool Begin(RenderPassCmd h, ref CommandReader r)
    {
        ReadOnlySpan<ColorAttachment> colors = r.Span<ColorAttachment>(h.Colors);
        if (colors.Length > RenderPassDesc.MaxColors) return Fault($"render pass with {colors.Length} colour attachments");
        var key = new AttachSet();
        bool srgb = false;
        for (int i = 0; i < colors.Length; i++)
        {
            key[i] = new Attach(colors[i].Image, colors[i].Mip, colors[i].Layer);
            if (!_images.TryGet(colors[i].Image.Value, out GlImage img)) return Fault($"render pass colour {i}: stale image");
            srgb |= img.Desc.Fmt.IsSrgb();
        }
        if (h.HasDepth) key[RenderPassDesc.MaxColors] = new Attach(h.Depth.Image, h.Depth.Mip, h.Depth.Layer);
        uint fbo = Framebuffer(in key, colors.Length, h.HasDepth);
        if (fbo == 0) return false;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
        if (srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
        // Clears obey the scissor and the write masks; the pipeline bound after this sets both again.
        Gl.Disable(Gl.SCISSOR_TEST);
        float* c = stackalloc float[4];
        uint* u = stackalloc uint[4];
        for (int i = 0; i < colors.Length; i++)
        {
            if (colors[i].Load != LoadOp.Clear) continue;
            Vector4 v = colors[i].Clear;
            Gl.ColorMaski((uint)i, 1, 1, 1, 1);
            _images.TryGet(colors[i].Image.Value, out GlImage img);
            if (img.Desc.Fmt.IsInteger())
            {
                u[0] = (uint)v.X; u[1] = (uint)v.Y; u[2] = (uint)v.Z; u[3] = (uint)v.W;
                Gl.ClearBufferuiv(Gl.COLOR, i, u);
            }
            else
            {
                c[0] = v.X; c[1] = v.Y; c[2] = v.Z; c[3] = v.W;
                Gl.ClearBufferfv(Gl.COLOR, i, c);
            }
        }
        if (h.HasDepth && h.Depth.Load == LoadOp.Clear)
        {
            float z = h.Depth.Depth;
            Gl.DepthMask(1);
            Gl.ClearBufferfv(Gl.DEPTH, 0, &z);
        }
        return true;
    }

    /// <summary>Hand the context back clean for the direct-GL passes still around a migrated one: no sampler object
    /// left on a unit they sample through texture parameters, no scissor, no blend.</summary>
    private bool End()
    {
        for (uint unit = 0; _samplerUnits != 0; unit++, _samplerUnits >>= 1)
            if ((_samplerUnits & 1) != 0) Gl.BindSampler(unit, 0);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.Disable(Gl.BLEND);
        return true;
    }

    /// <summary>The framebuffer for <paramref name="key"/>, made and checked complete on first use; 0 is refused.</summary>
    private uint Framebuffer(in AttachSet key, int colors, bool depth)
    {
        for (int i = 0; i < _fboCount; i++)
            if (Same(in _fbos[i], in key, colors, depth)) return _fbos[i].Fbo;
        uint f;
        Gl.GenFramebuffers(1, &f);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, f);
        uint* bufs = stackalloc uint[RenderPassDesc.MaxColors];
        for (int i = 0; i < colors; i++)
        {
            if (!AttachTo(Gl.COLOR_ATTACHMENT0 + (uint)i, key[i])) return Drop(f, $"colour attachment {i}: stale image");
            bufs[i] = Gl.COLOR_ATTACHMENT0 + (uint)i;
        }
        Gl.DrawBuffers(colors, bufs);
        if (depth)
        {
            Attach d = key[RenderPassDesc.MaxColors];
            bool stencil = _images.TryGet(d.Image.Value, out GlImage img) && img.Desc.Fmt.HasStencil();
            if (!AttachTo(stencil ? Gl.DEPTH_STENCIL_ATTACHMENT : Gl.DEPTH_ATTACHMENT, d)) return Drop(f, "depth attachment: stale image");
        }
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE) return Drop(f, $"framebuffer incomplete (0x{status:x})");
        if (_fboCount == _fbos.Length) Array.Resize(ref _fbos, _fbos.Length * 2);
        _fbos[_fboCount++] = new FboEntry { Key = key, Colors = colors, HasDepth = depth, Fbo = f };
        return f;
    }

    private bool AttachTo(uint point, Attach a)
    {
        if (!_images.TryGet(a.Image.Value, out GlImage img)) return false;
        if (img.Desc.Kind == ImageKind.Tex2D || a.Layer < 0) Gl.FramebufferTexture(Gl.FRAMEBUFFER, point, img.Name, a.Mip);
        else Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, point, img.Name, a.Mip, a.Layer);
        return true;
    }

    private uint Drop(uint fbo, string why)
    {
        Gl.DeleteFramebuffers(1, &fbo);
        Fault($"render pass: {why}");
        return 0;
    }

    private static bool Same(in FboEntry e, in AttachSet key, int colors, bool depth)
    {
        if (e.Colors != colors || e.HasDepth != depth) return false;
        for (int i = 0; i < colors; i++) if (e.Key[i] != key[i]) return false;
        return !depth || e.Key[RenderPassDesc.MaxColors] == key[RenderPassDesc.MaxColors];
    }

    /// <summary>Delete every framebuffer <paramref name="img"/> is attached to, before its name can be reused.</summary>
    private void Evict(ImageId img)
    {
        for (int i = _fboCount - 1; i >= 0; i--)
        {
            ref FboEntry e = ref _fbos[i];
            bool hit = e.HasDepth && e.Key[RenderPassDesc.MaxColors].Image == img;
            for (int c = 0; c < e.Colors; c++) hit |= e.Key[c].Image == img;
            if (!hit) continue;
            uint f = e.Fbo;
            Gl.DeleteFramebuffers(1, &f);
            _fbos[i] = _fbos[--_fboCount];
        }
    }

    private bool Bind(PipelineId id)
    {
        if (!_pipelines.TryGet(id.Value, out GlPipeline p)) return Fault("SetPipeline: stale or missing pipeline");
        Gl.UseProgram(p.Program);
        if (!p.Desc.Cs.IsNull) return true;
        Gl.BindVertexArray(_emptyVao);
        _topo = p.Desc.Topo == Topology.TriangleStrip ? Gl.TRIANGLE_STRIP : Gl.TRIANGLES;
        DepthState d = p.Desc.Depth;
        if (d.Test) Gl.Enable(Gl.DEPTH_TEST); else Gl.Disable(Gl.DEPTH_TEST);
        Gl.DepthFunc(Compare(d.Compare));
        Gl.DepthMask(d.Write ? (byte)1 : (byte)0);
        RasterState rs = p.Desc.Raster;
        if (rs.Cull == CullMode.None) Gl.Disable(Gl.CULL_FACE);
        else { Gl.Enable(Gl.CULL_FACE); Gl.CullFace(rs.Cull == CullMode.Front ? Gl.FRONT : Gl.BACK); }
        Gl.FrontFace(rs.FrontCw ? Gl.CW : Gl.CCW);
        if (rs.DepthBias) Gl.Enable(Gl.POLYGON_OFFSET_FILL); else Gl.Disable(Gl.POLYGON_OFFSET_FILL);
        if (rs.DepthClamp) Gl.Enable(Gl.DEPTH_CLAMP); else Gl.Disable(Gl.DEPTH_CLAMP);
        for (uint i = 0; i < RenderPassDesc.MaxColors; i++)
        {
            AttachmentBlend b = p.Desc.Blend[(int)i];
            if (b.Enable)
            {
                Gl.Enablei(Gl.BLEND, i);
                Gl.BlendFuncSeparatei(i, Factor(b.SrcRgb), Factor(b.DstRgb), Factor(b.SrcA), Factor(b.DstA));
                Gl.BlendEquationi(i, b.Op == BlendOp.Min ? Gl.MIN : Gl.FUNC_ADD);
            }
            else Gl.Disablei(Gl.BLEND, i);
            Gl.ColorMaski(i, Bit(b.Mask, ColorMask.R), Bit(b.Mask, ColorMask.G), Bit(b.Mask, ColorMask.B), Bit(b.Mask, ColorMask.A));
        }
        return true;
    }

    private static byte Bit(ColorMask m, ColorMask c) => (m & c) != 0 ? (byte)1 : (byte)0;

    private static uint Factor(BlendFactor f) => f switch
    {
        BlendFactor.Zero => Gl.ZERO,
        BlendFactor.SrcAlpha => Gl.SRC_ALPHA,
        BlendFactor.OneMinusSrcAlpha => Gl.ONE_MINUS_SRC_ALPHA,
        BlendFactor.OneMinusSrcColor => Gl.ONE_MINUS_SRC_COLOR,
        _ => Gl.ONE,
    };

    private static bool Viewport(Rect v) { Gl.Viewport(v.X, v.Y, v.W, v.H); return true; }

    private static bool Scissor(Rect s) { Gl.Enable(Gl.SCISSOR_TEST); Gl.Scissor(s.X, s.Y, s.W, s.H); return true; }

    private static bool DepthBias(DepthBiasCmd b) { Gl.PolygonOffset(b.Factor, b.Units); return true; }

    private bool BindSet(BindSetCmd h, ref CommandReader r)
    {
        ReadOnlySpan<BufferBinding> uniforms = r.Span<BufferBinding>(h.Uniforms), storage = r.Span<BufferBinding>(h.Storage);
        ReadOnlySpan<ImageBinding> sampled = r.Span<ImageBinding>(h.Sampled), images = r.Span<ImageBinding>(h.StorageImages);
        foreach (BufferBinding b in uniforms) if (!BindBuffer(Gl.UNIFORM_BUFFER, b)) return false;
        foreach (BufferBinding b in storage) if (!BindBuffer(Gl.SHADER_STORAGE_BUFFER, b)) return false;
        foreach (ImageBinding s in sampled)
        {
            if (!_images.TryGet(s.Image.Value, out GlImage img)) return Fault($"BindSet: stale sampled image on slot {s.Slot}");
            uint smp = 0;
            if (!s.Sampler.IsNull && !_samplers.TryGet(s.Sampler.Value, out smp)) return Fault($"BindSet: stale sampler on slot {s.Slot}");
            if (s.Slot >= Caps.MaxCombinedTextureUnits || s.Slot >= 64) return Fault($"BindSet: slot {s.Slot} past the texture units");
            // ponytail: a slot is its texture unit; the backend's own unit assignment lands with the first pass that needs more.
            Gl.BindTextureUnit(s.Slot, img.Name);
            Gl.BindSampler(s.Slot, smp);
            if (smp != 0) _samplerUnits |= 1ul << (int)s.Slot;
        }
        foreach (ImageBinding s in images)
        {
            if (!_images.TryGet(s.Image.Value, out GlImage img)) return Fault($"BindSet: stale storage image on slot {s.Slot}");
            byte layered = img.Desc.Kind == ImageKind.Tex2D ? (byte)0 : (byte)1;
            Gl.BindImageTexture(s.Slot, img.Name, s.Mip, layered, 0, Gl.READ_WRITE, GlFormats.Of(img.Desc.Fmt).Internal);
        }
        return true;
    }

    private bool BindBuffer(uint target, BufferBinding b)
    {
        if (!_buffers.TryGet(b.Buffer.Value, out GlBuffer g)) return Fault($"BindSet: stale buffer on slot {b.Slot}");
        if (b.Bytes == 0 && b.Offset == 0) Gl.BindBufferBase(target, b.Slot, g.Name);
        else Gl.BindBufferRange(target, b.Slot, g.Name, (IntPtr)b.Offset, (IntPtr)(b.Bytes == 0 ? g.Desc.Bytes - b.Offset : b.Bytes));
        return true;
    }

    private bool IndexBuffer(IndexBufferCmd c)
    {
        if (!_buffers.TryGet(c.Buffer.Value, out GlBuffer g)) return Fault("BindIndexBuffer: stale buffer");
        Gl.VertexArrayElementBuffer(_emptyVao, g.Name);
        _indexType = c.Type == IndexType.U32 ? Gl.UNSIGNED_INT : Gl.UNSIGNED_SHORT;
        _indexOffset = c.Offset;
        return true;
    }

    private bool Draw(DrawCmd d)
    {
        Gl.DrawArraysInstancedBaseInstance(_topo, d.FirstVert, d.Verts, d.Insts, (uint)d.FirstInst);
        return true;
    }

    private bool DrawIndexed(DrawIndexedCmd d)
    {
        long at = _indexOffset + (long)d.FirstIndex * (_indexType == Gl.UNSIGNED_INT ? 4 : 2);
        Gl.DrawElementsInstancedBaseVertexBaseInstance(_topo, d.Indices, _indexType, (void*)at, d.Insts, d.BaseVertex, (uint)d.FirstInst);
        return true;
    }

    private bool Indirect(IndirectCmd c)
    {
        if (!_buffers.TryGet(c.Commands.Value, out GlBuffer cmds)) return Fault("DrawIndexedIndirect: stale command buffer");
        Gl.BindBuffer(Gl.DRAW_INDIRECT_BUFFER, cmds.Name);
        if (c.Count.IsNull)
        {
            Gl.MultiDrawElementsIndirect(_topo, _indexType, (void*)c.Offset, c.MaxDraws, 0);
            return true;
        }
        if (!_buffers.TryGet(c.Count.Value, out GlBuffer count)) return Fault("DrawIndexedIndirect: stale count buffer");
        Gl.BindBuffer(Gl.PARAMETER_BUFFER, count.Name);
        Gl.MultiDrawElementsIndirectCount(_topo, _indexType, (void*)c.Offset, (IntPtr)c.CountOffset, c.MaxDraws, 0);
        return true;
    }

    private static bool Dispatch(DispatchCmd d) { Gl.DispatchCompute((uint)d.X, (uint)d.Y, (uint)d.Z); return true; }

    private bool Copy(CopyImageCmd c)
    {
        if (!_images.TryGet(c.Src.Value, out GlImage s) || !_images.TryGet(c.Dst.Value, out GlImage d))
            return Fault("CopyImage: stale image");
        Gl.CopyImageSubData(s.Name, s.Target, c.From.Mip, c.From.X, c.From.Y, c.From.Z,
                            d.Name, d.Target, c.To.Mip, c.To.X, c.To.Y, c.To.Z, c.W, c.H, c.D);
        return true;
    }

    private bool Blit(BlitImageCmd c)
    {
        if (!_images.TryGet(c.Src.Value, out GlImage s) || !_images.TryGet(c.Dst.Value, out GlImage d))
            return Fault("BlitImage: stale image");
        bool depth = s.Desc.Fmt.IsDepth();
        if (s.Desc.Samples > 1 && (c.From.W != c.To.W || c.From.H != c.To.H || c.Filter != Filter.Nearest || d.Desc.Samples > 1))
            return Fault("BlitImage: a multisample resolve is same-size, nearest, into a single-sample image");
        var src = new AttachSet();
        var dst = new AttachSet();
        int slot = depth ? RenderPassDesc.MaxColors : 0, colors = depth ? 0 : 1;
        src[slot] = new Attach(c.Src, 0, 0);
        dst[slot] = new Attach(c.Dst, 0, 0);
        uint read = Framebuffer(in src, colors, depth), draw = Framebuffer(in dst, colors, depth);
        if (read == 0 || draw == 0) return false;
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, read);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, draw);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.BlitFramebuffer(c.From.X, c.From.Y, c.From.X + c.From.W, c.From.Y + c.From.H,
                           c.To.X, c.To.Y, c.To.X + c.To.W, c.To.Y + c.To.H,
                           depth ? Gl.DEPTH_BUFFER_BIT : Gl.COLOR_BUFFER_BIT,
                           depth || c.Filter == Filter.Nearest ? (uint)Gl.NEAREST : (uint)Gl.LINEAR);
        return true;
    }

    private bool Update(UpdateBufferCmd c, ref CommandReader r)
    {
        ReadOnlySpan<byte> bytes = r.Span<byte>(c.Bytes);
        if (!_buffers.TryGet(c.Buffer.Value, out GlBuffer g)) return Fault("UpdateBuffer: stale buffer");
        if (g.Desc.Memory != MemoryKind.Upload) return Fault($"UpdateBuffer: {g.Desc.Label} is device memory");
        fixed (byte* p = bytes) Gl.NamedBufferSubData(g.Name, (IntPtr)c.Offset, (IntPtr)c.Bytes, p);
        return true;
    }
}
