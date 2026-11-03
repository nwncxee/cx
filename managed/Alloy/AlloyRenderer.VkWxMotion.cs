using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The precipitation's own motion on Vulkan: this frame's cull records drawn again into the velocity field after the camera,
/// the movers and the grass, each streak or flake writing its fall only in its core, so TAA moves a drop's history with
/// the drop and the ground's with the ground. It runs only when TAA resolves a frame the rain was drawn into: the rain
/// otherwise draws after the resolve and writes no motion.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The fall and the drips this frame's weather pass kept into <paramref name="vel"/>, over what the frame's
    /// other velocity writers left there. Nothing when nothing fell or no temporal resolve reads it.</summary>
    internal static void VkWeatherMotion(VkBackend vk, CommandList cl, ImageId vel, int w, int h, Matrix4x4* vp,
                                         in Matrix4x4 prevVp, ExtenderLog log)
    {
        VkWxState s = _vkWx;
        if (s.Dev != vk || (!s.Fell && !s.Dripped) || _capturing || !Weather.MotionWanted(Fsr.On, PostChain.TaaOn, s.Late)) return;
        if (!VkWxMotionEnsure(vk, s, log)) return;
        VkColourState c = _vkc;
        CommandList side = s.MotionSide!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, w, h);
        side.SetPipeline(s.MotionPipe);
        if (s.Fell && !VkWxMotionDraw(side, s, c, s.Kind, 0, w, h, vp, prevVp)) return;
        if (s.Dripped && !VkWxMotionDraw(side, s, c, 3, 1, w, h, vp, prevVp)) return;
        ReadOnlySpan<ColorAttachment> colors = [new(vel, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, WorldDepth.Clear)));
        if (!s.MotionLive)
        {
            s.MotionLive = true;
            log.Always("AlloyRenderer: the Vulkan precipitation velocity is LIVE -- each streak's core carries its own fall");
        }
    }

    /// <summary>One kind's records again, indirect from the cull's command <paramref name="slot"/>, through the velocity
    /// program; false only when the arena is full.</summary>
    private static bool VkWxMotionDraw(CommandList side, VkWxState s, VkColourState c, int kind, int slot, int w, int h,
                                       Matrix4x4* vp, in Matrix4x4 prevVp)
    {
        VkProg p = s.Motion!;
        Matrix4x4 view = s.View, proj = s.Proj;
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            Weather.WriteVk(in s.ML, kind, &view, &proj, _eyePos, false, h);
            LooseBlock.Set(s.MVp, *vp);
            LooseBlock.Set(s.MPrevVp, prevVp);
            LooseBlock.Set(s.MJitter, [PostChain.JitterX, PostChain.JitterY]);
            LooseBlock.Set(s.MSize, [(float)w, h]);
        }
        finally { LooseBlock.EndCapture(); }
        int loose = VkArena(p.Block);
        if (loose < 0) return false;
        ReadOnlySpan<BufferBinding> drops = [new(SsboSlots.WxDrops, s.Drops)];
        VkBindSet(side, p, c, loose, -1, c.Slots, 0, default, drops);
        side.DrawIndirect(s.Cmd, slot * 4 * sizeof(uint), 1);
        return true;
    }

    /// <summary>The velocity program and its pipeline, made once per device; refused, the rain keeps the ground's vectors
    /// this session, said once.</summary>
    private static bool VkWxMotionEnsure(VkBackend vk, VkWxState s, ExtenderLog log)
    {
        if (s.MotionTried) return s.Motion != null;
        s.MotionTried = true;
        s.MotionSide = new CommandList("vulkan weather velocity", 1 << 12, log);
        (string vs, _) = Weather.VkTexts(false, log);
        vs = ShaderStages.AfterVersion(vs, "#define CX_MOTION 1\n");
        VkProgramText t = WorldDepth.Translate(ShaderResource.Tokens(vs), ShaderResource.Tokens(Weather.MotionFs), VkPrograms.Units);
        VkProg? p = VkBuild(vk, t, "weather velocity", true, log);
        if (p != null)
        {
            VkWxState w = s;
            LooseBlock.Resolve(p.Names, () =>
            {
                w.ML = Weather.VkLocs();
                w.MVp = LooseBlock.Loc("uVp"); w.MPrevVp = LooseBlock.Loc("uPrevVP");
                w.MJitter = LooseBlock.Loc("uJitter"); w.MSize = LooseBlock.Loc("uSize");
            });
            var d = new PipelineDesc
            {
                Vs = p.Vs, Fs = p.Fs, Topo = Topology.TriangleStrip, Raster = new RasterState(CullMode.None),
                Depth = new DepthState(true, false, WorldDepth.NearerOrEqual), Label = "vk weather velocity",
            };
            d.Targets.ColorCount = 1;
            d.Targets.Colors[0] = Format.Rg16F;
            d.Targets.Depth = Format.D32F;
            d.Targets.Samples = 1;
            s.MotionPipe = vk.CreatePipeline(in d);
            if (s.MotionPipe.IsNull) p = null;
        }
        s.Motion = p;
        if (p == null)
            log.Always("AlloyRenderer: the Vulkan precipitation velocity was REFUSED (logged above) -- rain keeps the ground's vectors this session");
        return p != null;
    }
}
