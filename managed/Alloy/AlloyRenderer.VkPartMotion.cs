using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The particles' own motion on Vulkan: the groups this frame's particle pass drew, re-issued into the velocity field
/// from each record's last-frame centre, so TAA and FSR move a drifting plume's history with the plume. A sprite
/// writes its vector only where it is most of the pixel; elsewhere the field keeps what stands behind it.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>This frame's particle groups into <paramref name="vel"/>, over what the frame's other velocity writers
    /// left there. Nothing when the particle pass drew nothing this frame.</summary>
    internal static void VkParticleMotion(VkBackend vk, CommandList cl, ImageId vel, int w, int h, in Matrix4x4 prevVp,
                                          ExtenderLog log)
    {
        VkPartState s = _vkPart;
        if (s.Dev != vk || _capturing || s.DrewSeq != FrameSeq || s.DrewGroups == 0 || s.Recs.IsNull || s.Idx.IsNull) return;
        if (!VkPartMotionEnsure(vk, s, log)) return;
        VkColourState c = _vkc;
        VkProg p = s.Motion!;
        CommandList side = s.MotionSide!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, w, h);
        side.SetPipeline(s.MotionPipe);
        side.SetDepthState(new DepthState(true, false, WorldDepth.NearerOrEqual));
        bool? cull = null;
        int drawn = 0;
        for (int gi = 0; gi < s.DrewGroups; gi++)
        {
            Particles.VkGroup g = Particles.VkGroupAt(gi);
            if (g.Parts == 0) continue;
            if (cull != g.Cull)
            {
                cull = g.Cull;
                side.SetRasterState(new RasterState(g.Cull ? CullMode.Back : CullMode.None));
            }
            int loose = VkPartMotionBlock(s, p, g.Cls == Particles.VkAdd, prevVp, w, h);
            if (loose < 0) return;
            VkPartBind(side, s, p, c, loose, -1, g.Tex, false);
            side.Draw(g.Parts * 6, 1, g.Start * 6, 0);
            drawn++;
        }
        if (drawn == 0) return;
        ReadOnlySpan<ColorAttachment> colors = [new(vel, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, WorldDepth.Clear)));
        if (!s.MotionLive)
        {
            s.MotionLive = true;
            log.Always($"AlloyRenderer: the Vulkan particle velocity is LIVE -- {drawn} group(s) carry their own step over the camera field");
        }
    }

    /// <summary>One group's loose block for the velocity arm: the camera the colour pass drew with, last frame's
    /// view-projection, the jitter and the render size, and the additive switch that picks the coverage rule.</summary>
    private static int VkPartMotionBlock(VkPartState s, VkProg p, bool additive, in Matrix4x4 prevVp, int w, int h)
    {
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            LooseBlock.Set(s.MView, s.DrewView);
            LooseBlock.Set(s.MProj, s.DrewProj);
            LooseBlock.Set(s.MEye, [s.DrewEye.X, s.DrewEye.Y, s.DrewEye.Z]);
            LooseBlock.Set(s.MAdd, additive ? 1 : 0);
            LooseBlock.Set(s.MPrevVp, prevVp);
            LooseBlock.Set(s.MJitter, [PostChain.JitterX, PostChain.JitterY]);
            LooseBlock.Set(s.MSize, [(float)w, h]);
        }
        finally { LooseBlock.EndCapture(); }
        return VkArena(p.Block);
    }

    /// <summary>The particle stages with the motion define, translated for Vulkan.</summary>
    internal static VkProgramText VkPartMotionText()
        => WorldDepth.Translate(ShaderResource.Tokens(ShaderStages.WithDefine(ShaderSource.Get("cx_part_v.glsl"), GrassField.MotionOn)),
                                ShaderResource.Tokens(ShaderStages.WithDefine(ShaderSource.Get("cx_part_f.glsl"), GrassField.MotionOn)),
                                VkPrograms.Units);

    /// <summary>The velocity program and its pipeline, made once per device; refused, particles keep the field of what
    /// stands behind them this session, said once.</summary>
    private static bool VkPartMotionEnsure(VkBackend vk, VkPartState s, ExtenderLog log)
    {
        if (s.MotionTried) return s.Motion != null;
        s.MotionTried = true;
        s.MotionSide = new CommandList("vulkan particle velocity", PartArenaBytes, log);
        VkProg? p = VkBuild(vk, VkPartMotionText(), "particle velocity", false, log);
        if (p != null)
        {
            VkPartState t = s;
            LooseBlock.Resolve(p.Names, () =>
            {
                t.MView = LooseBlock.Loc("uView"); t.MProj = LooseBlock.Loc("uProj"); t.MEye = LooseBlock.Loc("uEye");
                t.MAdd = LooseBlock.Loc("uAdditive"); t.MPrevVp = LooseBlock.Loc("uPrevVP");
                t.MJitter = LooseBlock.Loc("uJitter"); t.MSize = LooseBlock.Loc("uSize");
            });
            var d = new PipelineDesc
            {
                Vs = p.Vs, Fs = p.Fs, Topo = Topology.Triangles, Raster = new RasterState(CullMode.None),
                Depth = new DepthState(true, false, WorldDepth.NearerOrEqual), Label = $"{PipeFeatures.Particles}velocity",
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
            log.Always("AlloyRenderer: the Vulkan particle velocity was REFUSED (logged above) -- particles keep the vectors of "
                     + "what stands behind them this session");
        return p != null;
    }
}
