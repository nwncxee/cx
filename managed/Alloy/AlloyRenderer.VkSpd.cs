using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The single-pass mip downsampler (<c>cx_spd_c</c>): one dispatch folds a 32x32 tile of its base level
/// five times, so a chain costs a dispatch and a barrier per six levels -- its base and five folds -- instead of
/// one of each per level.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The downsampler's resref. Its hi-Z variant folds by MAX over inverse view depth; the plain one
    /// by box average.</summary>
    internal const string SpdCs = "cx_spd_c";

    /// <summary>The define that picks the hi-Z variant out of the one text.</summary>
    private const string SpdHiZ = "CX_SPD_HIZ 1";

    /// <summary>Levels one dispatch writes, counting its base: the tile folded five times.</summary>
    private const int SpdLevels = 6;

    /// <summary>Base-level texels one group covers per axis.</summary>
    private const int SpdTile = 32;

    /// <summary><c>cx_spd_c</c>'s push block, member for member.</summary>
    private struct SpdPush
    {
        public Vector2 Nf;
        public int Levels, Gen;
    }

    /// <summary>The downsampler on one device, the hi-Z variant with <paramref name="hiZ"/>: a content override
    /// that does not build falls back, logged, to the embedded stage. A null id is the backend's own refusal,
    /// already logged.</summary>
    internal static PipelineId VkSpdPipe(VkBackend vk, bool hiZ, string label, ExtenderLog log)
    {
        string embedded = ShaderStages.Source(SpdCs);
        string over = ShaderResource.Override(SpdCs, log) ?? embedded;
        PipelineId pipe = vk.CreateCompute(hiZ ? ShaderStages.WithDefine(over, SpdHiZ) : over, label);
        if (!pipe.IsNull || ReferenceEquals(over, embedded)) return pipe;
        log.Always($"AlloyRenderer: {label}'s {SpdCs} override did not build (logged above) -- building the embedded stage");
        return vk.CreateCompute(hiZ ? ShaderStages.WithDefine(embedded, SpdHiZ) : embedded, label);
    }

    /// <summary><paramref name="img"/>'s whole <paramref name="levels"/>-level chain: the first dispatch makes the
    /// base level from <paramref name="src"/> and folds it down, and each next one starts at the last level the one
    /// before it wrote. <paramref name="nf"/> is the frame's near and far, which only the hi-Z reads.</summary>
    internal static void VkSpdChain(CommandList cl, PipelineId pipe, ImageId src, SamplerId smp, ImageId img,
                                   int w, int h, int levels, Vector2 nf)
    {
        if (pipe.IsNull || levels < 1) return;
        cl.SetPipeline(pipe);
        Span<ImageBinding> store = stackalloc ImageBinding[SpdLevels];
        Span<ImageBinding> sampled = stackalloc ImageBinding[1] { new(0, src, smp) };
        for (int at = 0; ;)
        {
            int n = Math.Min(SpdLevels, levels - at);
            // a level the pass does not write still needs a bound view: the last one it does write stands in
            for (int i = 0; i < SpdLevels; i++) store[i] = new ImageBinding((uint)i, img, default, at + Math.Min(i, n - 1));
            VkBindTally.Count(VkBindBy.Compute);
            cl.BindSet(0, new BindSet(default, default, sampled, store));
            var p = new SpdPush { Nf = nf, Levels = n, Gen = at == 0 ? 1 : 0 };
            cl.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<SpdPush>(in p)));
            int bw = Math.Max(w >> at, 1), bh = Math.Max(h >> at, 1);
            cl.Dispatch((bw + SpdTile - 1) / SpdTile, (bh + SpdTile - 1) / SpdTile);
            at += n - 1;
            if (at >= levels - 1) return;
        }
    }

    /// <summary>Dispatches <see cref="VkSpdChain"/> spends on a <paramref name="levels"/>-level chain.</summary>
    internal static int SpdPasses(int levels)
    {
        if (levels < 1) return 0;
        int at = 0, n = 0;
        while (true)
        {
            n++;
            at += Math.Min(SpdLevels, levels - at) - 1;
            if (at >= levels - 1) return n;
        }
    }
}
