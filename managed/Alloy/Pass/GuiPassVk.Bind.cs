using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static unsafe partial class GuiPassVk
{
    /// <summary>Program <paramref name="p"/>'s set 0 for one draw: its loose block out of the arena, the records
    /// storage buffer, and each sampler it declares bound to the image that name means -- the draw's texture, the
    /// environment map it reflects, the master palette -- or the stand-in texel.</summary>
    private static void BindSet(CommandList side, GuiVkProg p, int loose, int tex, int envTex)
    {
        GuiVkState s = _s;
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[4];
        Span<ImageBinding> sampled = stackalloc ImageBinding[8];
        int nu = 0, ns = 0, nt = 0;
        foreach (VkOpaque o in p.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                case VkBindKind.UniformBlock when o.Binding == UboSlots.Loose && loose >= 0 && nu < ubo.Length:
                    ubo[nu++] = new BufferBinding(o.Binding, s.Loose, loose, p.Block.Length);
                    break;
                case VkBindKind.StorageBlock when o.Binding == SsboSlots.Gui && ns < ssbo.Length:
                    ssbo[ns++] = new BufferBinding(o.Binding, s.Recs);
                    break;
                case VkBindKind.Sampled when nt < sampled.Length:
                    sampled[nt++] = Sampled(o, s, tex, envTex);
                    break;
            }
        }
        side.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], default));
    }

    /// <summary>One sampler declaration's image: the draw's own texture for the GUI program's <c>uTex</c> and a
    /// content stage's <c>texUnit0</c>, the part's environment map, the master palette, else the stand-in.</summary>
    private static ImageBinding Sampled(in VkOpaque o, GuiVkState s, int tex, int envTex)
        => o.Name switch
        {
            "uTex" or "texUnit0" => Texture(s, tex),
            "uEnvMap" => Texture(s, envTex),
            "uPltPal" when !s.Palette.IsNull => new ImageBinding(o.Binding, s.Palette, s.Nearest),
            _ => new ImageBinding(o.Binding, s.StandIn, s.Nearest),
        } with { Slot = o.Binding };

    /// <summary>GL texture name <paramref name="tex"/>'s Vulkan image: a CX font atlas, a model view's target, or an
    /// owned texture's mirror. A name with none takes the stand-in, counted -- its part then draws untextured rather
    /// than against whatever descriptor the slot held.</summary>
    private static ImageBinding Texture(GuiVkState s, int tex)
    {
        if (tex == 0) return new ImageBinding(0, s.StandIn, s.Nearest);
        if (TextAtlas.VkImage(tex) is { IsNull: false } atlas) return new ImageBinding(0, atlas, TextAtlas.VkSampler);
        if (ViewImage(tex) is { IsNull: false } view) return new ImageBinding(0, view, s.Linear);
        if (VkTextures.Image(tex) is { IsNull: false } own) return new ImageBinding(0, own, VkTextures.SamplerId(tex));
        s.NoTexture++;
        return new ImageBinding(0, s.StandIn, s.Nearest);
    }

    /// <summary>Alloy's GUI program's loose block for this flush: the two terms it reads are per-flush, so the block
    /// is appended once and every draw through the program names the same offset.</summary>
    private static int GuiBlock(GuiVkProg p, bool gamma, ExtenderLog log)
    {
        GuiVkState s = _s;
        if (s.GuiBlockAt >= 0) return s.GuiBlockAt;
        s.GuiBlockAt = Block(p, () =>
        {
            VkUniformShim.Set(s.BoneStart, GuiCapture.DrawN * (GuiCapture.RecFloats / 4));
            VkUniformShim.Set(s.Gamma, gamma ? 1 : 0);
        }, log);
        return s.GuiBlockAt;
    }

    /// <summary>A content program's loose block for draw <paramref name="di"/>: the engine's own terms, written by
    /// the same <see cref="GuiContent.Feed"/> GL calls, captured into the block at its reflected offsets.</summary>
    private static int ContentBlock(GuiContent.VkPair pair, int di, int w, int h, bool gamma, ExtenderLog log)
        => Block(pair.Program, () => GuiContent.Feed(pair.Terms, di, w, h, gamma), log);

    /// <summary>Capture <paramref name="fill"/>'s writes into <paramref name="p"/>'s block and append it to the
    /// flush's arena; its offset, or -1 (logged) when the arena is full.</summary>
    private static int Block(GuiVkProg p, Action fill, ExtenderLog log)
    {
        if (p.Block.Length == 0) return 0;
        Array.Clear(p.Block);
        VkUniformShim.Capture(p.Members, p.Block);
        try { fill(); }
        finally { VkUniformShim.EndCapture(); }
        return Arena(p.Block, log);
    }

    /// <summary>Append <paramref name="bytes"/> to the flush's arena at the device's uniform alignment; its offset,
    /// or -1 (logged) when it is full.</summary>
    private static int Arena(ReadOnlySpan<byte> bytes, ExtenderLog log)
    {
        GuiVkState s = _s;
        int align = Math.Max(s.Dev!.UniformAlign, 16);
        int at = (s.ArenaUsed + align - 1) & ~(align - 1);
        if (at + bytes.Length > LooseCap)
        {
            log.Always($"GuiPassVk: the GUI's loose arena is full at {LooseCap} bytes -- this frame's GUI is NOT DRAWN");
            return -1;
        }
        bytes.CopyTo(s.Arena.AsSpan(at));
        s.ArenaUsed = at + bytes.Length;
        return at;
    }

    /// <summary>The pipeline for <paramref name="p"/> drawing <paramref name="layout"/> under blend key
    /// <paramref name="blend"/> and <paramref name="topo"/>, made on first ask. Null id when the device refused it.
    /// Depth, culling, winding, the viewport and the depth range are dynamic, so one pipeline covers a layer's
    /// per-draw state.</summary>
    private static PipelineId Pipeline(GuiVkProg p, VertexLayoutId layout, int blend, Topology topo, ExtenderLog log)
    {
        long key = layout.Value | (long)blend << 32 | (long)topo << 40;
        if (p.Pipes.TryGetValue(key, out PipelineId id)) return id;
        var d = new PipelineDesc
        {
            Vs = p.Vs, Fs = p.Fs, Layout = layout, Topo = topo,
            Raster = new RasterState(CullMode.None), Depth = new DepthState(true, true, CompareOp.LessEqual),
            Label = $"vk {p.Label} layout {layout.Value} blend {blend} {topo}",
        };
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = CompositeFormat;
        d.Targets.Depth = DepthFormat;
        d.Targets.Samples = 1;
        if (blend != 0)
        {
            BlendFactor src = (BlendFactor)((blend - 1) >> 4), dst = (BlendFactor)((blend - 1) & 15);
            d.Blend[0] = new AttachmentBlend(true, src, dst, src, dst);
        }
        id = _s.Dev!.CreatePipeline(in d);
        if (id.IsNull) log.Always($"GuiPassVk: the device REFUSED pipeline {d.Label} (logged above) -- those draws sit out");
        p.Pipes[key] = id;
        return id;
    }

    /// <summary>A draw's blend as a pipeline key: 0 for blending off, else 1 + src * 16 + dst over the seam's
    /// factors. Null (said once, counted) for a GL factor the seam has no twin for -- drawing it under another
    /// factor would be a quiet wrong colour.</summary>
    private static int? Blend(in GuiCapture.Draw d, ExtenderLog log)
    {
        if (!d.Blend) return 0;
        if (Factor(d.Src) is not { } src || Factor(d.Dst) is not { } dst)
        {
            if (!_s.BlendSaid)
            {
                _s.BlendSaid = true;
                log.Always($"GuiPassVk: a GUI draw blends with GL factors 0x{d.Src:x}/0x{d.Dst:x}, which the render "
                         + "seam has no twin for -- such draws are NOT DRAWN on Vulkan");
            }
            return null;
        }
        return 1 + (int)src * 16 + (int)dst;
    }

    /// <summary>A GL blend factor as the seam's -- the set <c>Issue.BlendGl</c>'s table resolves to. Null for any
    /// other.</summary>
    private static BlendFactor? Factor(uint gl) => gl switch
    {
        AlloyRenderer.GlZero => BlendFactor.Zero,
        AlloyRenderer.GlOne => BlendFactor.One,
        AlloyRenderer.GlSrcColor => BlendFactor.SrcColor,
        CX.Graphics.Gl.ONE_MINUS_SRC_COLOR => BlendFactor.OneMinusSrcColor,
        AlloyRenderer.GlSrcAlpha => BlendFactor.SrcAlpha,
        AlloyRenderer.GlOneMinusSrcAlpha => BlendFactor.OneMinusSrcAlpha,
        AlloyRenderer.GlDstColor => BlendFactor.DstColor,
        AlloyRenderer.GlOneMinusDstAlpha => BlendFactor.OneMinusDstAlpha,
        _ => null,
    };

    /// <summary>A GL primitive mode as the seam's topology. Null (said once, counted) for any other: a GUI run the
    /// seam cannot assemble is not drawn under the wrong one.</summary>
    private static Topology? Topo(uint prim, ExtenderLog log)
    {
        if (prim == CX.Graphics.Gl.TRIANGLES) return Topology.Triangles;
        if (prim == CX.Graphics.Gl.TRIANGLE_STRIP) return Topology.TriangleStrip;
        if (!_s.PrimSaid)
        {
            _s.PrimSaid = true;
            log.Always($"GuiPassVk: a GUI draw assembles primitive mode 0x{prim:x}, which the render seam has no "
                     + "topology for -- such draws are NOT DRAWN on Vulkan");
        }
        return null;
    }

    /// <summary>The vertex layout of engine stream format <paramref name="fmt"/> at <paramref name="stride"/> bytes,
    /// made on first ask. Every location Alloy's GUI stage declares is fed in every format, because Vulkan has no
    /// constant attribute: a stream without a UV, a normal, a colour or bones feeds those from the position, which
    /// the stage reads only behind a flag such a draw does not carry.</summary>
    private static VertexLayoutId StreamLayout(VkBackend vk, uint fmt, int stride, ExtenderLog log)
    {
        GuiVkState s = _s;
        long key = fmt | (long)stride << 32;
        if (s.Stream.TryGetValue(key, out VertexLayoutId id)) return id;
        if (!GuiStream.Layout(fmt, stride, out GuiStream.Offsets o))
        {
            log.Always($"GuiPassVk: the GUI stream format 0x{fmt:x} does not lay out at {stride} bytes -- those draws are NOT DRAWN");
            s.Stream[key] = default;
            return default;
        }
        VertexAttrib[] attribs =
        [
            new(AlloyRenderer.LocPos, o.PosN, VertexType.Float, o.Pos),
            new(AlloyRenderer.LocUv, 2, VertexType.Float, o.Uv >= 0 ? o.Uv : o.Pos),
            new(AlloyRenderer.LocNrm, 3, VertexType.Float, o.Normal >= 0 ? o.Normal : o.Pos),
            new(GuiPass.LocColor, 4, VertexType.U8Norm, o.Color >= 0 ? o.Color : o.Pos),
            new(AlloyRenderer.LocBoneIdx, 4, VertexType.U8, o.Pos),
            new(AlloyRenderer.LocBoneW, 4, VertexType.Float, o.Pos),
        ];
        id = vk.CreateVertexLayout(new VertexLayoutDesc(attribs, stride, $"gui stream 0x{fmt:x}"));
        s.Stream[key] = id;
        return id;
    }

    /// <summary>Content key <paramref name="key"/>'s Vulkan program, built on its first draw; null = no program of
    /// its own (said once by the build), and those parts draw through Alloy's GUI program as GL's do.</summary>
    private static GuiContent.VkPair? ContentProgram(VkBackend vk, int key, ExtenderLog log)
    {
        GuiVkState s = _s;
        if (s.Content.TryGetValue(key, out GuiContent.VkPair? pair)) return pair;
        pair = GuiContent.VkBuild(vk, key, log);
        s.Content[key] = pair;
        return pair;
    }
}
