using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// <see cref="DecalLayer"/> on Vulkan: the frame's ground decals over-blended into an albedo layer and a surface
/// depth pair ahead of the colour pass, which folds that layer into the surfaces under them. The same writer stage
/// GL compiles, the same two targets and the same pair of blends; a refusal stands the pass down, leaving the
/// blended-pass draw Vulkan made before it existed.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The decal layer's resources on one device, replaced whole with the device.</summary>
    private sealed class VkDecalState
    {
        public VkBackend? Dev;
        public VkProg? Prog;
        public ImageId Layer, Surf;
        public int W, H;
        public bool Failed, Live, Logged;
        /// <summary>The writer is recording: its binds take the stand-ins, because a colour attachment of the pass
        /// being recorded cannot also be one of its storage images.</summary>
        public bool Writing;
        /// <summary>This frame's scene depth, which the writer's <c>uSceneDepth</c> reads and nothing else on
        /// Vulkan does.</summary>
        public ImageId Depth;
        public CommandList? Side;
    }

    private static VkDecalState _vkDecals = new();

    /// <summary>The decal pass's two attachments.</summary>
    private const int DecalTargets = 2;

    /// <summary>The albedo layer (<paramref name="surf"/> false) or the surface pair as a bind sees it: the real
    /// image once the writer has filled it, and the one-texel stand-in while the writer is drawing into it or when
    /// the pass stood down -- both of which the stage reads as "no decal here".</summary>
    private static ImageId VkDecalImage(bool surf, VkColourState s)
    {
        VkDecalState d = _vkDecals;
        if (!d.Live || d.Writing) return surf ? s.DecalSurf : s.DecalLayer;
        return surf ? d.Surf : d.Layer;
    }

    /// <summary>The scene depth the writer samples, the null id outside its pass.</summary>
    private static ImageId VkDecalDepth => _vkDecals.Writing ? _vkDecals.Depth : default;

    /// <summary>Hand the pair the colour pass binds as storage images over, outside every render pass. Its first
    /// bind of one lands wherever the first program that DECLARES it is bound, which may be after a draw by one
    /// that does not -- and there a barrier cannot go. Unconditional: the stand-ins a frame without decals binds
    /// are never written at all, so they reach their first bind undefined.</summary>
    private static void VkDecalHandOver(CommandList cl, VkColourState s)
    {
        ReadOnlySpan<ImageId> pair = [VkDecalImage(false, s), VkDecalImage(true, s)];
        cl.ReadImages(pair, Access.StorageRead);
    }

    /// <summary>What a surface pass's lit draws push: fold the layer in, or nothing when there is no layer.</summary>
    private static int VkDecalFold => _vkDecals.Live ? DecalLayer.ModeFold : DecalLayer.ModeNone;

    /// <summary>What the blended pass's draws push: drop the fragments the layer already took, or nothing.</summary>
    private static int VkDecalSkip => _vkDecals.Live ? DecalLayer.ModeSkip : DecalLayer.ModeNone;

    /// <summary>Batches [<paramref name="from"/>, <paramref name="to"/>) -- the frame's ground decals, off the
    /// colour list the cull just compacted -- into the layer, recorded ahead of the colour pass that folds it.
    /// <paramref name="can"/> is the frame's own verdict, as GL's: a lit, perspective main view with a prepass
    /// depth at an unshifted viewport. False (logged) only when the arena refused a bind.</summary>
    private static bool VkDecalPass(VkBackend vk, CommandList cl, int from, int to, in Matrix4x4 vp, int* viewport,
                                    float near, float far, bool can, bool indirect, ExtenderLog log)
    {
        VkDecalState d = _vkDecals;
        if (d.Dev != vk) _vkDecals = d = new VkDecalState { Dev = vk };
        d.Live = false;
        if (!can || to <= from || d.Failed) return true;
        VkColourState s = _vkc;
        if (!VkDecalEnsure(vk, d, s, viewport[2], viewport[3], log)) return true;
        d.Depth = s.Depth;
        CommandList side = d.Side!;
        side.Reset();
        int arena0 = s.ArenaUsed;
        var b = new VkBound { Fmt = -1, Tex = -1 };
        VkPassStart(side, viewport[2], viewport[3]);
        d.Live = true;
        d.Writing = true;
        // the writer's own CxDecalOn against the prepass depth is the visibility test: no depth attachment, so
        // neither the test nor the write has anything to reach
        var depth = new DepthState(false, false);
        var push = new VkBatchPush { AlphaCut = -1f, Decal = DecalLayer.ModeWrite };
        BufferId slots = indirect ? CullPassVk.Slots(CullPassVk.PhaseColour) : s.Slots;
        for (int g = from; g < to; g++)
        {
            // every ground decal is lit by construction (AlloyRenderer.DbDecal); an unlit one has no writer here
            if (!_gLit[g]) { s.Skipped += _gLen[g]; continue; }
            if (!VkBatch(side, ref b, d.Prog!, g, VkTargets.Decal, 0, depth, VkRaster(g, false), slots, 0, ref push,
                         vp, viewport, near, far, CullPassVk.PhaseColour, indirect)) continue;
            // counted as GL's own decal pass counts them, so the A/B line's two batch tallies are of one thing
            Counters._batches++;
            Counters._draws += _gLen[g];
        }
        d.Writing = false;
        Span<ColorAttachment> colors = stackalloc ColorAttachment[DecalTargets];
        colors[0] = new ColorAttachment(d.Layer, 0, 0, LoadOp.Clear, StoreOp.Store, new Vector4(0f, 0f, 0f, 1f));
        colors[1] = new ColorAttachment(d.Surf, 0, 0, LoadOp.Clear, StoreOp.Store,
                                        new Vector4(1f, DecalLayer.NoDecal, 0f, 0f));
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors));
        if (!d.Logged)
        {
            d.Logged = true;
            log.Always($"AlloyRenderer: the Vulkan decal layer is LIVE at {d.W}x{d.H} -- ground decals fold into "
                     + "the surfaces under them and drop out of the blended pass");
        }
        return true;
    }

    /// <summary>The writer and the two targets, made once per device and resized with the view. False = the pass
    /// stood down (logged), for the session when the writer refused and until the next size when a target did.</summary>
    private static bool VkDecalEnsure(VkBackend vk, VkDecalState d, VkColourState s, int w, int h, ExtenderLog log)
    {
        if (w <= 0 || h <= 0) return false;
        if (d.Prog == null)
        {
            d.Side = new CommandList("vulkan decal layer", DecalArenaBytes, log);
            d.Prog = VkBuild(vk, VkPrograms.Id.Decal, log);
            if (d.Prog == null)
            {
                d.Failed = true;
                log.Always("AlloyRenderer: the Vulkan decal layer's writer did not build (logged above) -- ground "
                         + "decals draw in the blended pass for the session, as they did before the layer existed");
                return false;
            }
        }
        if (d.W == w && d.H == h && !d.Layer.IsNull) return true;
        vk.Destroy(d.Layer);
        vk.Destroy(d.Surf);
        d.Layer = VkDecalTarget(vk, Format.Rgba16F, w, h, "vk decal albedo layer");
        d.Surf = VkDecalTarget(vk, Format.Rg32F, w, h, "vk decal surface depths");
        d.W = w; d.H = h;
        if (d.Layer.IsNull || d.Surf.IsNull)
        {
            log.Always($"AlloyRenderer: the Vulkan decal layer's {w}x{h} targets were REFUSED (logged above) -- "
                     + "ground decals draw in the blended pass at this size");
            return false;
        }
        return true;
    }

    /// <summary>Room for the pass's binds and draws; a growth is one allocation and one log line.</summary>
    private const int DecalArenaBytes = 1 << 18;

    private static ImageId VkDecalTarget(VkBackend vk, Format f, int w, int h, string label)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, f, w, h, 1, 1, 1, 1,
                                        ImageUse.ColorTarget | ImageUse.Storage, label));

    /// <summary>The decal pass's blends, as GL sets them: the albedo over-blends into the layer, and the surface
    /// pair takes a MIN, which keeps the nearest decal's depth beside the one prepass depth every writer stamps.</summary>
    private static void VkDecalBlend(ref PipelineDesc d)
    {
        d.Targets.ColorCount = DecalTargets;
        d.Targets.Colors[0] = Format.Rgba16F;
        d.Targets.Colors[1] = Format.Rg32F;
        d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha,
                                         BlendFactor.Zero, BlendFactor.OneMinusSrcAlpha);
        // MIN ignores its factors in both APIs, so the pair carries the one GL left set
        d.Blend[1] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.One, BlendFactor.One, BlendFactor.One,
                                         BlendOp.Min);
    }

    /// <summary>The Vulkan pass's targets and blends are the pair <see cref="DecalLayer"/> sets on GL, and its
    /// second attachment is the output the writer stage declares. Oracles: the factors, formats and clear
    /// transcribed by hand from <see cref="DecalLayer"/>'s GL calls, and the embedded GLSL text.</summary>
    internal static void VkDecalSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.VkDecalSelfCheck");
        var d = new PipelineDesc();
        VkDecalBlend(ref d);
        check.Expect(d.Targets.ColorCount == DecalTargets && d.Targets.Colors[0] == Format.Rgba16F
                     && d.Targets.Colors[1] == Format.Rg32F,
            "the pass draws into an RGBA16F albedo layer and an RG32F surface pair, as DecalLayer.Ensure makes them");
        check.Expect(d.Blend[0] is { Enable: true, SrcRgb: BlendFactor.One, DstRgb: BlendFactor.OneMinusSrcAlpha,
                                     SrcA: BlendFactor.Zero, DstA: BlendFactor.OneMinusSrcAlpha, Op: BlendOp.Add },
            "the albedo layer over-blends: ONE, ONE_MINUS_SRC_ALPHA, with ZERO, ONE_MINUS_SRC_ALPHA on alpha");
        check.Expect(d.Blend[1].Enable && d.Blend[1].Op == BlendOp.Min,
            "the surface pair takes a MIN, so every writer's shared prepass depth and the nearest decal's depth survive");
        check.Expect(DecalLayer.NoDecal > 1e20f,
            $"the surface pair clears its decal depth past any surface ({DecalLayer.NoDecal})");
        check.Expect(ShaderSource.Get("cx_oitout.glsl").Contains("layout(location = 1) out vec2 oDecalSurf;"),
            "the writer's second output is the surface pair on location 1, the attachment this pass puts there");
        log.Info(check.Ok ? "AlloyRenderer.VkDecalSelfCheck: passed" : "AlloyRenderer.VkDecalSelfCheck: FAILED");
    }
}
