using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static unsafe partial class SmaaStage
{
    /// <summary>The stage on one device: the two tables, the edge and weight targets, and the edge and weight programs per
    /// preset level, each built on first use of its level.</summary>
    private sealed class SmaaVkState
    {
        public VkBackend? Dev;
        public bool Tried, Failed;
        public ImageId Area, Search, Edges, Weights;
        public int W, H;
        public readonly GuiVkProg?[] Edge = new GuiVkProg?[4], Weight = new GuiVkProg?[4];
        public readonly int[] EdgeRt = [-1, -1, -1, -1], WeightRt = [-1, -1, -1, -1];
        public readonly bool[] Built = new bool[4];
    }

    private static SmaaVkState _vk = new();

    /// <summary>The weights the blend stage reads this frame.</summary>
    internal static ImageId VkWeights => _vk.Weights;

    /// <summary><see cref="Ready"/> on Vulkan: the tables, this preset's two programs and the targets at
    /// <paramref name="w"/> x <paramref name="h"/>. False sits the stage out; a refusal is said once and holds for the device.</summary>
    internal static bool ReadyVk(VkBackend vk, int w, int h, ExtenderLog log)
    {
        SmaaVkState s = _vk;
        if (s.Dev != vk) _vk = s = new SmaaVkState { Dev = vk };
        if (s.Failed) return false;
        if (!s.Tried && !VkTables(vk, s, log)) return Fail(s, log, "the area/search tables were missing or REFUSED");
        int level = LevelFor(Samples);
        if (!s.Built[level])
        {
            s.Built[level] = true;
            Level = level;
            string vs = ShaderStages.Source(PostChain.ShdPostVs);
            int l = level;
            s.Edge[l] = Make(vk, vs, ShdEdgeFs, "SMAA edges", p => s.EdgeRt[l] = p.Loc("uRt"), log);
            s.Weight[l] = Make(vk, vs, ShdWeightFs, "SMAA weights", p => s.WeightRt[l] = p.Loc("uRt"), log);
            if (s.Edge[l] == null || s.Weight[l] == null) return Fail(s, log, $"a SMAA {PresetName(l)} pass did not build (logged above)");
        }
        if (s.W == w && s.H == h) return true;
        vk.Destroy(s.Edges); vk.Destroy(s.Weights);
        const ImageUse use = ImageUse.Sampled | ImageUse.ColorTarget;
        s.Edges = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, w, h, 1, 1, 1, 1, use, "vk smaa edges"));
        s.Weights = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, w, h, 1, 1, 1, 1, use, "vk smaa weights"));
        if (s.Edges.IsNull || s.Weights.IsNull) return Fail(s, log, $"the {w}x{h} edge/weight targets were REFUSED");
        s.W = w; s.H = h;
        log.Info($"SmaaStage: Vulkan edge + weight targets {w}x{h} at SMAA {PresetName(level)}");
        return true;
    }

    /// <summary>Edges of <paramref name="src"/>, then the weights off them, ahead of the blend stage's draw.</summary>
    internal static void PassesVk(CommandList cl, ImageId src, ExtenderLog log)
    {
        SmaaVkState s = _vk;
        int l = LevelFor(Samples);
        GuiVkProg e = s.Edge[l]!, wt = s.Weight[l]!;
        var rect = new Rect(0, 0, s.W, s.H);
        PostChainVk.Open(e);
        WriteVk(s.EdgeRt[l]);
        int at = PostChainVk.Close(cl, e);
        // the edge pass discards non-edges, so last frame's must go: the clear
        PostChainVk.Draw(cl, e, Format.Rgba8, PostChainVk.Blend.None, s.Edges, LoadOp.Clear, rect, at, new PostChainVk.Inputs(src), log);
        PostChainVk.Open(wt);
        WriteVk(s.WeightRt[l]);
        at = PostChainVk.Close(cl, wt);
        PostChainVk.Draw(cl, wt, Format.Rgba8, PostChainVk.Blend.None, s.Weights, LoadOp.DontCare, rect, at,
                         new PostChainVk.Inputs(default, Edges: s.Edges, Area: s.Area, Search: s.Search), log);
    }

    /// <summary><c>uRt</c>, SMAA's (1/w, 1/h, w, h), into the open capture.</summary>
    internal static void WriteVk(int loc) => VkUniformShim.Set(loc, [1f / _vk.W, 1f / _vk.H, _vk.W, _vk.H]);

    private static GuiVkProg? Make(VkBackend vk, string vs, string fsRef, string label, System.Action<GuiVkProg> locate, ExtenderLog log)
    {
        GlProgram.Texts(PostChain.ShdPostVs, vs, fsRef, ShaderStages.Source(fsRef), log, label, out string v, out string f);
        return PostChainVk.Program(vk, v, f, label, locate, log);
    }

    /// <summary>The two embedded tables, uploaded once per device.</summary>
    private static bool VkTables(VkBackend vk, SmaaVkState s, ExtenderLog log)
    {
        s.Tried = true;
        byte[]? area = TableBytes("SmaaArea", AreaW * AreaH * 2), search = TableBytes("SmaaSearch", SearchW * SearchH);
        if (area == null || search == null) return false;
        const ImageUse use = ImageUse.Sampled | ImageUse.CopyDst;
        s.Area = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg8, AreaW, AreaH, 1, 1, 1, 1, use, "vk smaa area"));
        s.Search = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8, SearchW, SearchH, 1, 1, 1, 1, use, "vk smaa search"));
        return !s.Area.IsNull && !s.Search.IsNull && vk.Upload(s.Area, 0, 0, area) && vk.Upload(s.Search, 0, 0, search);
    }

    private static bool Fail(SmaaVkState s, ExtenderLog log, string why)
    {
        s.Failed = true;
        log.Always($"SmaaStage: {why} on Vulkan -- the aa stage sits out on this device; the frame resolves without anti-aliasing");
        return false;
    }
}
