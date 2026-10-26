using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The owned highlight on Vulkan: <see cref="Highlight"/>'s mask -- each highlighted record redrawn in its root's
/// colour, depth-tested against the frame's own depth -- then the one full-screen fill and dilated outline over the
/// finished composite, as GL draws them after the post stack.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The mask target and both programs on one device.</summary>
    private sealed class VkHlState
    {
        public VkBackend? Dev;
        public VkProg? Mask;
        public GuiVkProg? Comp;
        public int Color = -1, Fill = -1, Radius = -1, Unjitter = -1;
        public bool Tried, Live;
        public ImageId Target;
        public int W, H;
        public CommandList? Side;
        /// <summary>This frame's mask holds at least one draw, so the composite runs.</summary>
        public bool Drawn;
    }

    private static VkHlState _vkHl = new();

    /// <summary>The mask, recorded after the colour passes against the frame's depth. Nothing when nothing is highlighted.</summary>
    private static bool VkHighlightMask(VkBackend vk, CommandList cl, VkColourState c, in Matrix4x4 vp, int* viewport,
                                        float near, float far, int groups, ExtenderLog log)
    {
        VkHlState s = _vkHl;
        if (s.Dev != vk) _vkHl = s = new VkHlState { Dev = vk };
        s.Drawn = false;
        if (_capturing) return true;
        int n = Highlight.GatherVk(groups);
        if (n == 0 || !VkHlEnsure(vk, s, viewport[2], viewport[3], log)) return true;
        VkProg p = s.Mask!;
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        VkPassStart(side, viewport[2], viewport[3]);
        side.SetDepthBias(Highlight.VkBiasFactor, Highlight.VkBiasUnits);
        int fmt = -1, cut = -1;
        Vector3 bound = new(float.NaN);
        var push = new VkBatchPush { AlphaCut = -1f };
        for (int k = 0; k < n; k++)
        {
            (int j, int g, Vector3 colour, bool cutout) = Highlight.VkDraw(k);
            int f = _gVao[g] & ~DbArenaVaoBit;
            if ((uint)f >= MeshArena.Formats || MeshArena.VkVbo((MeshArena.Fmt)f).IsNull) continue;
            PipelineId pipe = VkPipeline(p, f, VkTargets.Mask, 0);
            if (pipe.IsNull) continue;
            if (f != fmt) { fmt = f; side.SetPipeline(pipe); side.BindVertexBuffer(MeshArena.VkVbo((MeshArena.Fmt)f)); cut = -1; }
            int wantCut = cutout ? 1 : 0;
            if (wantCut != cut)
            {
                cut = wantCut;
                side.SetDepthState(new DepthState(true, false, cutout ? CompareOp.Equal : CompareOp.LessEqual));
                side.SetRasterState(new RasterState(CullMode.None, false, !cutout));
            }
            if (colour != bound)
            {
                bound = colour;
                Array.Clear(p.Block);
                VkUniformShim.Capture(p.Members, p.Block);
                fixed (Matrix4x4* m = &vp) VkUniformShim.Set(p.Vp, new ReadOnlySpan<float>(m, 16));
                VkUniformShim.Set(s.Color, [colour.X, colour.Y, colour.Z]);
                VkUniformShim.EndCapture();
                int loose = VkArena(p.Block);
                if (loose < 0) return false;
                VkBindSet(side, p, c, loose, -1, c.Slots, 0);
            }
            push.DrawBase = j;
            side.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<VkBatchPush>(in push)));
            side.DrawIndexed(_dCount[j], 1, (int)(_dOff[j] / sizeof(ushort)), _dBase[j]);
            s.Drawn = true;
        }
        ReadOnlySpan<ColorAttachment> colors = [new(s.Target, 0, 0, LoadOp.Clear, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, 1f)));
        return true;
    }

    /// <summary>The fill and outline over the composite, after the post stack, when this frame's mask drew.</summary>
    internal static void VkHighlightComposite(CommandList cl, ImageId composite, int* display, ExtenderLog log)
    {
        VkHlState s = _vkHl;
        if (!s.Drawn || s.Comp == null) return;
        PostChainVk.Open(s.Comp);
        VkUniformShim.Set(s.Fill, Highlight.VkFill);
        VkUniformShim.Set(s.Radius, Highlight.VkRadius);
        // NDC offset j is uv offset j/2; the mask holds clean + j, so clean reads at uv + j/2
        VkUniformShim.Set(s.Unjitter, [PostChain.JitterX * 0.5f, PostChain.JitterY * 0.5f]);
        int at = PostChainVk.Close(cl, s.Comp);
        PostChainVk.Draw(cl, s.Comp, Format.Rgba8, PostChainVk.Blend.Over, composite, LoadOp.Load,
                         new Rect(display[0], display[1], display[2], display[3]), at, new PostChainVk.Inputs(default, Mask: s.Target), log);
        if (!s.Live)
        {
            s.Live = true;
            log.Always("AlloyRenderer: the Vulkan outline highlight is LIVE -- the mask against the frame's depth, then the fill "
                     + "and dilated outline over the composite (a root with no masked draw takes no glow on Vulkan)");
        }
    }

    /// <summary>Both programs and the RGBA8 mask at the render size, made once per device and resized with the view.</summary>
    private static bool VkHlEnsure(VkBackend vk, VkHlState s, int w, int h, ExtenderLog log)
    {
        if (!s.Tried)
        {
            s.Tried = true;
            s.Side = new CommandList("vulkan highlight", 1 << 16, log);
            s.Mask = VkBuild(vk, VkPrograms.Id.HighlightMask, log);
            const uint name = VkUniformShim.ProgramBase + (uint)VkPrograms.Id.HighlightMask;
            if (s.Mask != null) VkUniformShim.Resolve(s.Mask.Names, () => s.Color = GlProgram.Loc(name, "uColor"));
            s.Comp = PostChainVk.Program(vk, PostChain.FullscreenVs, Highlight.HighlightCompFs, "highlight composite", q =>
            {
                s.Fill = q.Loc("uFill"); s.Radius = q.Loc("uRadius"); s.Unjitter = q.Loc("uUnjitter");
            }, log);
            if (s.Mask == null || s.Comp == null)
            {
                s.Mask = null;
                log.Always("AlloyRenderer: the Vulkan highlight mask or composite was REFUSED (logged above) -- nothing is "
                         + "highlighted on Vulkan this session");
            }
        }
        if (s.Mask == null) return false;
        if (!s.Target.IsNull && s.W == w && s.H == h) return true;
        vk.Destroy(s.Target);
        s.Target = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, w, h, 1, 1, 1, 1,
                                                ImageUse.Sampled | ImageUse.ColorTarget, "vk highlight mask"));
        s.W = w; s.H = h;
        return !s.Target.IsNull;
    }
}
