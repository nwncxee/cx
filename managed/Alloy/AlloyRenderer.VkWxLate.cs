using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The precipitation after FSR 3.1's upscale: this frame's cull records drawn onto the display-size frame before bloom
/// and the tonemap, unjittered, with no depth target. A drop behind the scene fades out against the render-size depth
/// copy the way every streak meets geometry, so FSR never holds a streak in the history it reconstructs from.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The fall and the drips the weather pass held back, onto <paramref name="hdr"/> at
    /// <paramref name="w"/> by <paramref name="h"/>. Nothing when none were held back; false only when the arena is full.</summary>
    internal static bool VkWeatherLate(VkBackend vk, CommandList cl, ImageId hdr, int w, int h, ExtenderLog log)
    {
        VkWxState s = _vkWx;
        if (s.Dev != vk || !s.Late || (!s.Fell && !s.Dripped) || _capturing) return true;
        VkColourState c = _vkc;
        s.LateSide ??= new CommandList("vulkan weather after the upscale", 1 << 12, log);
        CommandList side = s.LateSide;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, w, h);
        int* full = stackalloc int[4] { 0, 0, w, h };
        Matrix4x4 view = s.View, proj = s.Proj;
        proj.M31 -= PostChain.JitterX * proj.M34; proj.M32 -= PostChain.JitterY * proj.M34;
        int k = s.Kind == 2 ? 1 : 0;
        if (s.Fell && !VkWxLateDraw(vk, side, s, c, k, s.Kind, 0, &view, &proj, full, log)) return false;
        if (s.Dripped && !VkWxLateDraw(vk, side, s, c, 0, 3, 1, &view, &proj, full, log)) return false;
        ReadOnlySpan<ColorAttachment> colors = [new(hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors));
        if (!s.LateLive)
        {
            s.LateLive = true;
            log.Always($"AlloyRenderer: the Vulkan precipitation draws after the FSR upscale -- {w}x{h}, unjittered, out of FSR's history");
        }
        return true;
    }

    /// <summary>One kind through program <paramref name="k"/> on its depth-less pipeline, indirect from the cull's command
    /// <paramref name="slot"/>; false only when the arena is full.</summary>
    private static bool VkWxLateDraw(VkBackend vk, CommandList side, VkWxState s, VkColourState c, int k, int kind, int slot,
                                     Matrix4x4* view, Matrix4x4* proj, int* full, ExtenderLog log)
    {
        VkProg? p = s.Prog[k];
        if (p == null) return true;
        if (s.LatePipe[k].IsNull)
        {
            var d = new PipelineDesc
            {
                Vs = p.Vs, Fs = p.Fs, Topo = Topology.TriangleStrip, Raster = new RasterState(CullMode.None),
                Label = k == 1 ? "vk snow after the upscale" : "vk rain after the upscale",
            };
            d.Targets.ColorCount = 1;
            d.Targets.Colors[0] = Format.Rgba16F;
            d.Targets.Samples = 1;
            d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha);
            s.LatePipe[k] = vk.CreatePipeline(in d);
            if (s.LatePipe[k].IsNull)
            {
                log.Always("AlloyRenderer: the Vulkan precipitation's after-upscale pipeline was REFUSED (logged above) -- no rain under FSR");
                s.Prog[k] = null;
                return true;
            }
        }
        side.SetPipeline(s.LatePipe[k]);
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            BindPass(p.L.CubeCount, full, s.Near, s.Far);
            Weather.WriteVk(in s.L[k], kind, view, proj, _eyePos, s.Lit);
            LooseBlock.Set(s.L[k].Screen, [(float)full[2], full[3]]);
        }
        finally { LooseBlock.EndCapture(); }
        int loose = VkArena(p.Block), pass = VkArenaPass();
        if (loose < 0 || pass < 0) return false;
        ReadOnlySpan<BufferBinding> drops = [new(SsboSlots.WxDrops, s.Drops)];
        VkBindSet(side, p, c, loose, pass, c.Slots, 0, default, drops);
        side.DrawIndirect(s.Cmd, slot * 4 * sizeof(uint), 1);
        return true;
    }
}
