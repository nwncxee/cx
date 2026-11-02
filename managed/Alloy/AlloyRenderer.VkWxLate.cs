using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The precipitation after the temporal resolve: this frame's cull records and crowns drawn onto the display-size frame
/// after FSR 3.1's upscale, or into an overlay the taa stage composites after its resolve, unjittered, with no depth
/// target. A drop behind the scene fades out against the render-size depth copy the way every streak meets geometry, and
/// a crown behind it is discarded against the same copy, so neither resolve holds them in the history it reconstructs from.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The fall, the drips and the crowns the weather pass held back, onto <paramref name="into"/> at
    /// <paramref name="w"/> by <paramref name="h"/>: the upscaled frame, the taa overlay (<paramref name="clear"/>), or the
    /// scene's own frame when neither resolve ran after the scene held them. False only when the arena is full.</summary>
    internal static bool VkWeatherLate(VkBackend vk, CommandList cl, ImageId into, bool clear, int w, int h, float near, float far,
                                       ExtenderLog log)
    {
        VkWxState s = _vkWx;
        if (!VkWxHeld(vk, s)) return true;
        VkColourState c = _vkc;
        s.LateSide ??= new CommandList("vulkan weather after the resolve", 1 << 12, log);
        CommandList side = s.LateSide;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, w, h);
        int* full = stackalloc int[4] { 0, 0, w, h };
        Matrix4x4 view = s.View, proj = s.Proj;
        // the resolved frame is unjittered, so the drops draw through the camera with the jitter taken back out
        proj.M31 -= PostChain.JitterX * proj.M34; proj.M32 -= PostChain.JitterY * proj.M34;
        int k = s.Kind == 2 ? 1 : 0;
        if (s.Fell && !VkWxLateDraw(vk, side, s, c, k, s.Kind, 0, &view, &proj, full, near, far, log)) return false;
        if (s.Dripped && !VkWxLateDraw(vk, side, s, c, 0, 3, 1, &view, &proj, full, near, far, log)) return false;
        if (s.Splashed && !VkSplashLateDraw(vk, side, s, c, &view, &proj, full, near, far, log)) return false;
        ReadOnlySpan<ColorAttachment> colors = [new(into, 0, 0, clear ? LoadOp.Clear : LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors));
        // the taa stage samples the overlay inside its own render pass, where no barrier can be placed
        if (clear) cl.ReadImages([into]);
        int site = clear ? 2 : 1;
        if (s.LateSaid != site)
        {
            s.LateSaid = site;
            log.Always($"AlloyRenderer: the Vulkan precipitation draws after the {(clear ? "TAA resolve" : "FSR upscale")} -- {w}x{h}, "
                     + "unjittered, out of its history");
        }
        return true;
    }

    /// <summary>The display-size overlay the precipitation draws into for the taa stage to composite, made with the device and
    /// resized with the display; null when nothing was held back this frame, or when the image was refused, which is said once
    /// and draws the precipitation in the scene from the next frame.</summary>
    internal static ImageId VkWeatherOverlay(VkBackend vk, int w, int h, ExtenderLog log)
    {
        VkWxState s = _vkWx;
        if (!VkWxHeld(vk, s)) return default;
        if (!s.Over.IsNull && s.OverW == w && s.OverH == h) return s.Over;
        vk.Destroy(s.Over);
        s.Over = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1,
                                              ImageUse.Sampled | ImageUse.ColorTarget, "vk precipitation after taa"));
        s.OverW = w; s.OverH = h;
        if (!s.Over.IsNull) return s.Over;
        log.Always($"AlloyRenderer: the Vulkan precipitation's {w}x{h} overlay after the TAA resolve was REFUSED (logged above) -- "
                 + "from the next frame the rain draws in the scene, where TAA smears it over the ground");
        s.LateFailed = true;
        return default;
    }

    /// <summary>Whether this frame's weather pass held anything back for after the resolve.</summary>
    private static bool VkWxHeld(VkBackend vk, VkWxState s)
        => s.Dev == vk && s.Late && (s.Fell || s.Dripped || s.Splashed) && !_capturing;

    /// <summary>Program <paramref name="k"/>'s depth-less pipeline, made once per device; null when it was refused, which is
    /// said once and draws the precipitation in the scene from the next frame.</summary>
    private static PipelineId VkWxLatePipe(VkBackend vk, VkWxState s, int k, ExtenderLog log)
    {
        if (!s.LatePipe[k].IsNull) return s.LatePipe[k];
        VkProg p = s.Prog[k]!;
        string label = k == VkSplashProg ? "splash" : k == 1 ? "snow" : "rain";
        var d = new PipelineDesc
        {
            Vs = p.Vs, Fs = p.Fs, Topo = Topology.TriangleStrip, Raster = new RasterState(CullMode.None),
            Label = $"vk {label} after the resolve",
        };
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = Format.Rgba16F;
        d.Targets.Samples = 1;
        d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha);
        s.LatePipe[k] = vk.CreatePipeline(in d);
        if (s.LatePipe[k].IsNull)
        {
            log.Always($"AlloyRenderer: the Vulkan {label} pipeline after the resolve was REFUSED (logged above) -- from the next "
                     + "frame the precipitation draws in the scene, where FSR and TAA smear it over the ground");
            s.LateFailed = true;
        }
        return s.LatePipe[k];
    }

    /// <summary>One kind through program <paramref name="k"/> on its depth-less pipeline, indirect from the cull's command
    /// <paramref name="slot"/>; false only when the arena is full.</summary>
    private static bool VkWxLateDraw(VkBackend vk, CommandList side, VkWxState s, VkColourState c, int k, int kind, int slot,
                                     Matrix4x4* view, Matrix4x4* proj, int* full, float near, float far, ExtenderLog log)
    {
        VkProg? p = s.Prog[k];
        if (p == null) return true;
        PipelineId pipe = VkWxLatePipe(vk, s, k, log);
        if (pipe.IsNull) return true;
        side.SetPipeline(pipe);
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            BindPass(p.L.CubeCount, full, near, far);
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

    /// <summary>The rain and footfall crowns on the splash program's depth-less pipeline, each part tested against the depth
    /// copy by its own stage; false only when the arena is full.</summary>
    private static bool VkSplashLateDraw(VkBackend vk, CommandList side, VkWxState s, VkColourState c, Matrix4x4* view,
                                         Matrix4x4* proj, int* full, float near, float far, ExtenderLog log)
    {
        VkProg p = s.Prog[VkSplashProg]!;
        PipelineId pipe = VkWxLatePipe(vk, s, VkSplashProg, log);
        if (pipe.IsNull) return true;
        int n;
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            BindPass(p.L.CubeCount, full, near, far);
            n = Weather.WriteSplashVk(in s.Sp, view, proj, full, _eyePos, s.Lit);
            LooseBlock.Set(s.Sp.Late, 1);
            LooseBlock.Set(s.Sp.Screen, full[2], (float)full[3]);
        }
        finally { LooseBlock.EndCapture(); }
        if (n == 0) return true;
        int loose = VkArena(p.Block), pass = VkArenaPass();
        if (loose < 0 || pass < 0) return false;
        side.SetPipeline(pipe);
        VkBindSet(side, p, c, loose, pass, c.Slots, 0);
        side.Draw(4, n, 0, 0);
        return true;
    }
}
