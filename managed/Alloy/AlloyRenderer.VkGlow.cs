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
/// The vanilla highlight glow on Vulkan: <see cref="GobGlow"/>'s staged quads, one instanced strip each, added over the
/// finished screen after the post stack -- where the engine's <c>Scene::DoHighlightGob</c> drew them -- and
/// depth-tested in the fragment stage against the frame's depth.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The pass's program, pipeline and quad buffer on one device.</summary>
    private sealed class VkGlowState
    {
        public VkBackend? Dev;
        public VkProg? Prog;
        public PipelineId Pipe;
        public bool Tried, Live, TexSaid;
        public BufferId Recs;
        public long RecCap;
        public CommandList? Side;
        public int View = -1, Proj = -1, Rect = -1;
    }

    private static VkGlowState _vkGlow = new();

    /// <summary>The glow over <paramref name="screen"/> at <paramref name="display"/>, for every highlighted root the
    /// outline mask did not cover. False only when the device refused an upload or the uniform arena is full.</summary>
    private static bool VkGlowPass(VkBackend vk, CommandList cl, VkColourState c, ImageId screen, in Matrix4x4 view,
                                   in Matrix4x4 proj, int* display, ExtenderLog log)
    {
        VkGlowState s = _vkGlow;
        if (s.Dev != vk) _vkGlow = s = new VkGlowState { Dev = vk };
        if (_capturing || Highlight.StageGlowVk(EyePos) == 0 || !VkGlowEnsure(vk, s, PostChainVk.FormatOf(screen), log)) return true;
        uint tex = GobGlow.TexName(out bool final, out string resref);
        ImageId img = tex != 0 ? VkTextures.Image((int)tex) : default;
        if (img.IsNull)
        {
            if (final && !s.TexSaid)
            {
                s.TexSaid = true;
                log.Always($"GobGlow: the scene's highlight texture '{resref}' is " + (resref.Length == 0 ? "unset" : "unreadable")
                         + " -- the vanilla highlight glow is NOT DRAWN");
            }
            return true;
        }
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(GobGlow.Staged());
        int n = bytes.Length / sizeof(GlowRec);
        if (!VkGrow(vk, ref s.Recs, ref s.RecCap, bytes.Length, "vk highlight glow")) return false;
        cl.UpdateBuffer(s.Recs, 0, bytes);
        ReadOnlySpan<ImageId> read = [c.Depth];
        cl.ReadImages(read);
        // the screen is resolved, so the quads draw through the camera with the TAA jitter taken back out
        Matrix4x4 clean = proj;
        clean.M31 -= PostChain.JitterX * clean.M34; clean.M32 -= PostChain.JitterY * clean.M34;
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(display[0], display[1], display[2], display[3]);
        side.SetPipeline(s.Pipe);
        VkProg p = s.Prog!;
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        LooseBlock.Set(s.View, view);
        LooseBlock.Set(s.Proj, clean);
        // a screen's rows run from the top, so its fragments' y comes back to the scene depth's rows through a flipped origin
        float flipH = vk.FlipHeight(screen), sy = 1f / Math.Max(display[3], 1);
        LooseBlock.Set(s.Rect, [display[0], flipH == 0f ? display[1] : flipH - display[1], 1f / Math.Max(display[2], 1), flipH == 0f ? sy : -sy]);
        LooseBlock.EndCapture();
        int loose = VkArena(p.Block);
        if (loose < 0) return false;
        Span<ImageBinding> sampled = stackalloc ImageBinding[8];
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[4];
        int nt = 0, nu = 0, ns = 0;
        foreach (VkOpaque o in p.Opaques)
        {
            if (o.Set != 0) continue;
            if (o.Kind == VkBindKind.UniformBlock && nu < ubo.Length)
                ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, loose, p.Block.Length) : new BufferBinding(o.Binding, c.ZeroUbo);
            else if (o.Kind == VkBindKind.StorageBlock && ns < ssbo.Length)
                ssbo[ns++] = new BufferBinding(o.Binding, o.Binding == SsboSlots.Glow ? s.Recs : c.ZeroSsbo);
            else if (o.Kind == VkBindKind.Sampled && nt < sampled.Length)
                sampled[nt++] = o.Name == "uSceneDepth" ? new ImageBinding(o.Binding, c.Depth)
                              : o.Name == "uTex" ? new ImageBinding(o.Binding, img, VkTextures.SamplerId((int)tex))
                              : VkSampled(o, c, 0);
        }
        VkBindTally.Count(VkBindBy.Post);
        side.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], default));
        side.Draw(4, n, 0, 0);
        ReadOnlySpan<ColorAttachment> colors = [new(screen, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors));
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan highlight glow is LIVE -- {n} quad(s), three per visible mesh part of each "
                     + "highlighted root the outline does not cover, over the screen");
        }
        return true;
    }

    /// <summary>The program and its additive, depth-less pipeline into the screen, made once per device; a refusal
    /// is said once and the glow sits out.</summary>
    private static bool VkGlowEnsure(VkBackend vk, VkGlowState s, Format target, ExtenderLog log)
    {
        if (s.Tried) return s.Prog != null;
        s.Tried = true;
        s.Side = new CommandList("vulkan highlight glow", 1 << 12, log);
        VkProgramText t = WorldDepth.Translate(ShaderResource.Tokens(ShaderSource.Get("cx_gobglow_v.glsl")),
                                               ShaderResource.Tokens(ShaderSource.Get("cx_gobglow_f.glsl")), VkPrograms.Units);
        VkProg? p = VkBuild(vk, t, "highlight glow", false, log);
        if (p != null)
        {
            LooseBlock.Resolve(p.Names, () =>
            {
                s.View = LooseBlock.Loc("uView"); s.Proj = LooseBlock.Loc("uProj"); s.Rect = LooseBlock.Loc("uViewRect");
            });
            var d = new PipelineDesc
            {
                Vs = p.Vs, Fs = p.Fs, Topo = Topology.TriangleStrip, Raster = new RasterState(CullMode.None),
                Depth = new DepthState(false, false, CompareOp.Always), Label = "vk highlight glow",
            };
            d.Targets.ColorCount = 1;
            d.Targets.Colors[0] = target;
            d.Targets.Samples = 1;
            d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.One, BlendFactor.One, BlendFactor.One);
            s.Pipe = vk.CreatePipeline(in d);
            if (s.Pipe.IsNull) p = null;
        }
        s.Prog = p;
        if (p == null) log.Always("AlloyRenderer: the Vulkan highlight glow program was REFUSED (logged above) -- a highlight the "
                                + "outline does not cover shows nothing on Vulkan this session");
        return p != null;
    }
}
