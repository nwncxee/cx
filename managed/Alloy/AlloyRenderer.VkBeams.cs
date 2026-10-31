using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The light shafts (<see cref="PassId.BeamsOwned"/>) on Vulkan: <see cref="Beams"/>' captured volumes, extruded along
/// the sun by the same stages GL compiles, added over the scene colour with depth tested and not written.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The pass's program and its pipeline per arena format, on one device.</summary>
    private sealed class VkBeamState
    {
        public VkBackend? Dev;
        public VkProg? Prog;
        public bool Tried, Live;
        public readonly PipelineId[] Pipes = new PipelineId[MeshArena.Formats];
        public CommandList? Side;
        public int Model = -1, View = -1, Proj = -1, Src = -1, ZClip = -1, Color = -1;
    }

    private static VkBeamState _vkBeam = new();

    /// <summary>The beams into <paramref name="cl"/> as their own pass; false only when the uniform arena is full.</summary>
    private static bool VkBeamPass(VkBackend vk, CommandList cl, VkColourState c, in Matrix4x4 view, in Matrix4x4 proj,
                                   int* viewport, ExtenderLog log)
    {
        VkBeamState s = _vkBeam;
        if (s.Dev != vk) _vkBeam = s = new VkBeamState { Dev = vk };
        if (_capturing) return true;
        int n = Beams.StageVk(out Vector3 dir, out float alpha, out float zClip);
        if (n == 0 || !VkBeamEnsure(vk, s, log)) return true;
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        VkPassStart(side, viewport[2], viewport[3]);
        side.SetDepthState(new DepthState(true, false, WorldDepth.NearerOrEqual));
        side.SetRasterState(new RasterState(CullMode.None));
        VkProg p = s.Prog!;
        int fmt = -1, drawn = 0;
        float tint = Beams.VkTint;
        for (int k = 0; k < n; k++)
        {
            (int part, int range) = Beams.VkBeam(k);
            if (!MeshArena.Runs(range, 0, out int baseVertex, out nint off, out int count, out _)) continue;
            int f = (int)MeshArena.PubFormat(range);
            if (s.Pipes[f].IsNull || MeshArena.VkVbo((MeshArena.Fmt)f).IsNull) continue;
            if (f != fmt) { fmt = f; side.SetPipeline(s.Pipes[f]); side.BindVertexBuffer(MeshArena.VkVbo((MeshArena.Fmt)f)); }
            Matrix4x4 m = SceneDb.PartWorld[part];
            Array.Clear(p.Block);
            LooseBlock.Capture(p.Members, p.Block);
            LooseBlock.Set(s.Model, m);
            LooseBlock.Set(s.View, view);
            LooseBlock.Set(s.Proj, proj);
            LooseBlock.Set(s.Src, [dir.X, dir.Y, dir.Z, -1f]);
            LooseBlock.Set(s.ZClip, zClip);
            LooseBlock.Set(s.Color, [tint, tint, tint, alpha]);
            LooseBlock.EndCapture();
            int at = VkArena(p.Block);
            if (at < 0) return false;
            ReadOnlySpan<BufferBinding> ubo = [new(UboSlots.Loose, c.Arena, at, p.Block.Length)];
            side.BindSet(0, new BindSet(ubo, default, default, default));
            side.DrawIndexed(count, 1, (int)(off / sizeof(ushort)), baseVertex);
            drawn++;
        }
        ReadOnlySpan<ColorAttachment> colors = [new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, WorldDepth.Clear)));
        if (!s.Live && drawn > 0)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan light shafts are LIVE -- {drawn} of {n} staged volume(s) drawn");
        }
        return true;
    }

    /// <summary>The program and its pipelines, made once per device; a refusal is said once and the shafts sit out.</summary>
    private static bool VkBeamEnsure(VkBackend vk, VkBeamState s, ExtenderLog log)
    {
        if (s.Tried) return s.Prog != null;
        s.Tried = true;
        s.Side = new CommandList("vulkan beams", 1 << 14, log);
        VkProgramText t = WorldDepth.Translate(Beams.VkVs, Beams.VkFs, VkPrograms.Units);
        VkProg? p = VkBuild(vk, t, "beams", false, log);
        if (p != null)
            LooseBlock.Resolve(p.Names, () =>
            {
                s.Model = LooseBlock.Loc("uModel"); s.View = LooseBlock.Loc("uView"); s.Proj = LooseBlock.Loc("uProj");
                s.Src = LooseBlock.Loc("uProjSrc"); s.ZClip = LooseBlock.Loc("uZClip"); s.Color = LooseBlock.Loc("uColor");
            });
        VkColourState c = _vkc;
        for (int f = 0; p != null && f < MeshArena.Formats; f++)
        {
            var d = new PipelineDesc
            {
                Vs = p.Vs, Fs = p.Fs, Layout = c.Layouts[f], Topo = Topology.Triangles, Raster = new RasterState(CullMode.None),
                Depth = new DepthState(true, false, WorldDepth.NearerOrEqual), Label = $"vk beams {(MeshArena.Fmt)f}",
            };
            d.Targets.ColorCount = 1;
            d.Targets.Colors[0] = Format.Rgba16F;
            d.Targets.Depth = Format.D32F;
            d.Targets.Samples = 1;
            d.Blend[0] = new AttachmentBlend(true, BlendFactor.SrcAlpha, BlendFactor.One, BlendFactor.SrcAlpha, BlendFactor.One);
            s.Pipes[f] = vk.CreatePipeline(in d);
        }
        s.Prog = p;
        if (p == null) log.Always("AlloyRenderer: the Vulkan light-shaft program was REFUSED (logged above) -- no shafts on Vulkan this session");
        return p != null;
    }
}
