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
/// finished screen, as GL draws them after the post stack.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The mask target and both programs on one device.</summary>
    private sealed class VkHlState
    {
        public VkBackend? Dev;
        public VkProg? Mask;
        public GuiVkProg? Comp, Resolve;
        public int Color = -1, Fill = -1, Radius = -1;
        public int RUnjitter = -1, RInvVp = -1, RPrevVp = -1, RKeep = -1;
        public bool Tried, Live;
        public ImageId Target;
        /// <summary>The resolved mask, two frames' worth: <see cref="Cur"/> is this frame's, the other last frame's.</summary>
        public ImageId Hist0, Hist1;
        public int Cur;
        /// <summary>Last frame's resolve ran, so its history and <see cref="PrevVp"/> are this frame's to reproject.</summary>
        public bool HadHist;
        public Matrix4x4 PrevVp;
        public int W, H;
        public CommandList? Side;
        /// <summary>This frame's mask holds at least one draw, so the composite runs.</summary>
        public bool Drawn;
        /// <summary>The root colour a content pair's depth twin writes, as <c>uHlColor</c>, at its bind.</summary>
        public Vector3 TwinColour;
    }

    private static VkHlState _vkHl = new();

    /// <summary>The mask, recorded after the colour passes against the frame's depth. Nothing when nothing is highlighted.</summary>
    private static bool VkHighlightMask(VkBackend vk, CommandList cl, VkColourState c, in Matrix4x4 vp, int* viewport,
                                        float near, float far, int groups, ExtenderLog log)
    {
        VkHlState s = VkHlOwner(vk);
        s.Drawn = false;
        if (_capturing) return true;
        int n = Highlight.GatherVk(groups);
        if (n == 0) return true;
        if (!VkHlEnsure(vk, s, viewport[2], viewport[3], log)) { Highlight.UnmaskVk(); return true; }
        VkProg plain = s.Mask!;
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        VkPassStart(side, viewport[2], viewport[3]);
        // GL's offset pulls toward the eye by going down; the world's reversed depth goes up
        side.SetDepthBias(-Highlight.VkBiasFactor, -Highlight.VkBiasUnits);
        int fmt = -1, cut = -1;
        VkProg? prog = null;
        Vector3 bound = new(float.NaN);
        var push = new VkBatchPush { AlphaCut = -1f };
        for (int k = 0; k < n; k++)
        {
            (int j, int g, Vector3 colour, bool cutout) = Highlight.VkDraw(k);
            int f = _gVao[g] & ~DbArenaVaoBit;
            if ((uint)f >= MeshArena.Formats || MeshArena.VkVbo((MeshArena.Fmt)f).IsNull) continue;
            VkProg? p = ShaderFeatures.OutOfPrepass(_gFeat[g]) ? VkContent(_gFeat[g], ContentVariant.Depth) : plain;
            if (p == null) continue;
            PipelineId pipe = VkPipeline(p, f, VkTargets.Mask, 0);
            if (pipe.IsNull) continue;
            if (f != fmt || p != prog)
            {
                if (f != fmt) side.BindVertexBuffer(MeshArena.VkVbo((MeshArena.Fmt)f));
                if (p != prog) bound = new(float.NaN);
                fmt = f; prog = p;
                side.SetPipeline(pipe);
                cut = -1;
            }
            int wantCut = cutout ? 1 : 0;
            if (wantCut != cut)
            {
                cut = wantCut;
                side.SetDepthState(new DepthState(true, false, cutout ? CompareOp.Equal : WorldDepth.NearerOrEqual));
                side.SetRasterState(new RasterState(CullMode.None, false, !cutout));
            }
            if (colour != bound)
            {
                bound = colour;
                if (p == plain)
                {
                    Array.Clear(p.Block);
                    LooseBlock.Capture(p.Members, p.Block);
                    LooseBlock.Set(p.Vp, vp);
                    LooseBlock.Set(s.Color, colour);
                    LooseBlock.EndCapture();
                    int loose = VkArena(p.Block);
                    if (loose < 0) return false;
                    VkBindSet(side, p, c, loose, -1, c.Slots, 0);
                }
                else
                {
                    s.TwinColour = colour;
                    if (!VkBindProgram(side, p, in vp, viewport, near, far, c.Slots, 0)) return false;
                }
            }
            push.AlphaCut = p == plain ? -1f : AlloyClasses.Discard(_gCls[g]);
            push.DrawBase = j;
            side.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<VkBatchPush>(in push)));
            side.DrawIndexed(_dCount[j], 1, (int)(_dOff[j] / sizeof(ushort)), _dBase[j]);
            s.Drawn = true;
        }
        // a twin may bind the depth copy, where no barrier can move it once the pass is open
        if (c.Snapshot.Filled) cl.ReadImages([c.DepthRead]);
        ReadOnlySpan<ColorAttachment> colors = [new(s.Target, 0, 0, LoadOp.Clear, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, WorldDepth.Clear)));
        return true;
    }

    /// <summary>Can a part of content pair <paramref name="feat"/>, whose depth is its own, draw into the mask?</summary>
    internal static bool VkMaskTwinReady(int feat) => VkContent(feat, ContentVariant.Depth) != null;

    /// <summary>The mask resolved over frames, then the fill and outline over the screen, after the post stack,
    /// when this frame's mask drew. A frame with no mask drops the history, so the next highlight starts clean.</summary>
    internal static void VkHighlightComposite(CommandList cl, ImageId screen, ImageId depth, in Matrix4x4 vpClean, int* display,
                                              ExtenderLog log)
    {
        VkHlState s = _vkHl;
        if (!s.Drawn || s.Comp == null || s.Resolve == null) { s.HadHist = false; return; }
        ImageId hist = VkHighlightResolve(cl, s, depth, in vpClean, log);
        if (hist.IsNull) return;
        PostChainVk.Open(s.Comp);
        LooseBlock.Set(s.Fill, Highlight.VkFill);
        LooseBlock.Set(s.Radius, Highlight.OutlineTexels(s.W, display[2]));
        int at = PostChainVk.Close(cl, s.Comp);
        PostChainVk.Draw(cl, s.Comp, PostChainVk.FormatOf(screen), PostChainVk.Blend.Over, screen, LoadOp.Load,
                         new Rect(display[0], display[1], display[2], display[3]), at, new PostChainVk.Inputs(default, Mask: hist), log);
        if (!s.Live)
        {
            s.Live = true;
            log.Always("AlloyRenderer: the Vulkan outline highlight is LIVE -- the mask against the frame's depth, then the fill "
                     + "and dilated outline over the screen (a root with no masked draw takes the vanilla glow)");
        }
    }

    /// <summary>This frame's mask into the history it keeps, unjittered: last frame's resolve reprojected through the
    /// camera and clamped to this frame's neighbourhood. The resolved image, or none when the pass was refused.</summary>
    private static ImageId VkHighlightResolve(CommandList cl, VkHlState s, ImageId depth, in Matrix4x4 vpClean, ExtenderLog log)
    {
        GuiVkProg p = s.Resolve!;
        ImageId into = s.Cur == 0 ? s.Hist0 : s.Hist1, last = s.Cur == 0 ? s.Hist1 : s.Hist0;
        Matrix4x4.Invert(vpClean, out Matrix4x4 inv);
        PostChainVk.Open(p);
        // NDC offset j is uv offset j/2; the mask holds clean + j, so clean reads at uv + j/2
        LooseBlock.Set(s.RUnjitter, [PostChain.JitterX * 0.5f, PostChain.JitterY * 0.5f]);
        LooseBlock.Set(s.RInvVp, inv);
        LooseBlock.Set(s.RPrevVp, s.HadHist ? s.PrevVp : vpClean);
        LooseBlock.Set(s.RKeep, s.HadHist ? Highlight.VkHistoryKeep : 0f);
        int at = PostChainVk.Close(cl, p);
        bool drew = PostChainVk.Draw(cl, p, Format.Rgba8, PostChainVk.Blend.None, into, LoadOp.DontCare, new Rect(0, 0, s.W, s.H), at,
                                     new PostChainVk.Inputs(default, Depth: depth, Hist: last, HistLinear: true, Mask: s.Target), log);
        s.HadHist = drew;
        s.PrevVp = vpClean;
        if (!drew) return default;
        s.Cur ^= 1;
        return into;
    }

    /// <summary>The highlight's state on <paramref name="vk"/>, replaced whole when the device is another.</summary>
    private static VkHlState VkHlOwner(VkBackend vk)
    {
        VkHlState s = _vkHl;
        if (s.Dev != vk) _vkHl = s = new VkHlState { Dev = vk };
        return s;
    }

    /// <summary>The programs, the RGBA8 mask and its two history images at the render size, made once per device and
    /// resized with the view; a resize drops the history.</summary>
    private static bool VkHlEnsure(VkBackend vk, VkHlState s, int w, int h, ExtenderLog log)
    {
        if (!VkHlPrograms(vk, s, log)) return false;
        if (!s.Target.IsNull && s.W == w && s.H == h) return true;
        vk.Destroy(s.Target);
        vk.Destroy(s.Hist0);
        vk.Destroy(s.Hist1);
        s.Target = PostChainVk.Target(vk, w, h, Format.Rgba8, "vk highlight mask");
        s.Hist0 = PostChainVk.Target(vk, w, h, Format.Rgba8, "vk highlight history a");
        s.Hist1 = PostChainVk.Target(vk, w, h, Format.Rgba8, "vk highlight history b");
        s.W = w; s.H = h;
        s.HadHist = false;
        return !s.Target.IsNull && !s.Hist0.IsNull && !s.Hist1.IsNull;
    }

    /// <summary>The mask and composite programs, made once per device, by the loading hold's prewarm or the first
    /// highlight; false when either refused (logged once), which leaves every highlight to the vanilla glow.</summary>
    private static bool VkHlPrograms(VkBackend vk, VkHlState s, ExtenderLog log)
    {
        if (!s.Tried)
        {
            s.Tried = true;
            s.Side = new CommandList("vulkan highlight", 1 << 16, log);
            s.Mask = VkBuild(vk, VkPrograms.Id.HighlightMask, log);
            if (s.Mask != null) LooseBlock.Resolve(s.Mask.Names, () => s.Color = LooseBlock.Loc("uColor"));
            s.Comp = PostChainVk.Program(vk, PostChain.FullscreenVs, Highlight.HighlightCompFs, "highlight composite", q =>
            {
                s.Fill = q.Loc("uFill"); s.Radius = q.Loc("uRadius");
            }, log);
            s.Resolve = PostChainVk.Program(vk, PostChain.FullscreenVs, Highlight.HighlightResolveFs, "highlight resolve", q =>
            {
                s.RUnjitter = q.Loc("uUnjitter"); s.RInvVp = q.Loc("uInvVP"); s.RPrevVp = q.Loc("uPrevVP"); s.RKeep = q.Loc("uKeep");
            }, log);
            if (s.Mask == null || s.Comp == null || s.Resolve == null)
            {
                s.Mask = null;
                log.Always("AlloyRenderer: the Vulkan highlight mask or composite was REFUSED (logged above) -- every "
                         + "highlight takes the vanilla glow on Vulkan this session");
            }
        }
        return s.Mask != null;
    }
}
