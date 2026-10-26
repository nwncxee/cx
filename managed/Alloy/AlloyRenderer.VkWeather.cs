using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Falling rain and snow (<see cref="PassId.WeatherOwned"/>) on Vulkan: <see cref="Weather"/>'s instanced streaks and
/// flakes through the same stages GL compiles, premultiplied over the scene colour, faded against a depth copy. Lit by
/// the pass block's sun and ambient; no cover map, drip, splash or wetness map is on Vulkan yet.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The rain and snow programs on one device.</summary>
    private sealed class VkWxState
    {
        public VkBackend? Dev;
        public readonly VkProg?[] Prog = new VkProg?[2];
        public readonly PipelineId[] Pipe = new PipelineId[2];
        public readonly Weather.Loc[] L = new Weather.Loc[2];
        public readonly bool[] Tried = new bool[2];
        public CommandList? Side;
        public bool Live;
    }

    private static VkWxState _vkWx = new();

    /// <summary>The precipitation into <paramref name="cl"/> as its own pass; false only when the arena is full.</summary>
    private static bool VkWeatherPass(VkBackend vk, CommandList cl, VkColourState c, in Matrix4x4 view, in Matrix4x4 proj,
                                      int* viewport, float near, float far, bool lightingOn, ExtenderLog log)
    {
        VkWxState s = _vkWx;
        if (s.Dev != vk) _vkWx = s = new VkWxState { Dev = vk };
        if (_capturing || c.DepthRead.IsNull) return true;
        int count = Weather.StageVk(AreaWind(), out bool snow);
        int k = snow ? 1 : 0;
        if (count == 0 || !VkWxEnsure(vk, s, k, log)) return true;
        cl.CopyImage(new CopyImageCmd(c.Depth, new ImageRegion(0, 0, 0, 0), c.DepthRead, new ImageRegion(0, 0, 0, 0), c.W, c.H, 1));
        ReadOnlySpan<ImageId> read = [c.DepthRead];
        cl.ReadImages(read);
        VkProg p = s.Prog[k]!;
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, viewport[2], viewport[3]);
        side.SetPipeline(s.Pipe[k]);
        Matrix4x4 v = view, pr = proj;
        Array.Clear(p.Block);
        VkUniformShim.Capture(p.Members, p.Block);
        try
        {
            BindPass(p.L.CubeCount, viewport, near, far);
            Weather.WriteVk(in s.L[k], snow, &v, &pr, _eyePos, lightingOn && AlloyLights.Ready);
        }
        finally { VkUniformShim.EndCapture(); }
        foreach (string name in VkParity.StoodDown) VkZero(p, name);
        int loose = VkArena(p.Block), pass = VkArenaPass();
        if (loose < 0 || pass < 0) return false;
        VkBindSet(side, p, c, loose, pass, c.Slots, 0);
        side.Draw(4, count, 0, 0);
        ReadOnlySpan<ColorAttachment> colors = [new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, 1f)));
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan {(snow ? "snow" : "rain")} is LIVE -- {count} instance(s); no cover map, drips, "
                     + "splashes or wetness map on Vulkan yet");
        }
        return true;
    }

    /// <summary>Program <paramref name="k"/> (0 rain, 1 snow) and its premultiplied pipeline, made once per device.</summary>
    private static bool VkWxEnsure(VkBackend vk, VkWxState s, int k, ExtenderLog log)
    {
        if (s.Tried[k]) return s.Prog[k] != null;
        s.Tried[k] = true;
        s.Side ??= new CommandList("vulkan weather", 1 << 14, log);
        uint name = VkUniformShim.ProgramBase + 51 + (uint)k;   // not-an-offset: the weather programs' shim names
        (string vs, string fs) = Weather.VkTexts(k == 1, log);
        var units = new System.Collections.Generic.Dictionary<string, uint>(VkPrograms.Units) { ["uCover"] = TextureSlots.CoverEsm };
        VkProgramText t = VkGlsl.Translate(ShaderResource.Tokens(vs), ShaderResource.Tokens(fs), units, glDepth: false);
        VkProg? p = VkBuild(vk, t, k == 1 ? "snow" : "rain", true, name, log);
        if (p != null)
        {
            VkWxState w = s;
            VkUniformShim.Resolve(p.Names, () => w.L[k] = Weather.VkLocs(name));
            var d = new PipelineDesc
            {
                Vs = p.Vs, Fs = p.Fs, Topo = Topology.TriangleStrip, Raster = new RasterState(CullMode.None),
                Depth = new DepthState(true, false, CompareOp.LessEqual), Label = $"vk {(k == 1 ? "snow" : "rain")}",
            };
            d.Targets.ColorCount = 1;
            d.Targets.Colors[0] = Format.Rgba16F;
            d.Targets.Depth = Format.D32F;
            d.Targets.Samples = 1;
            d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha);
            s.Pipe[k] = vk.CreatePipeline(in d);
            if (s.Pipe[k].IsNull) p = null;
        }
        s.Prog[k] = p;
        if (p == null) log.Always("AlloyRenderer: the Vulkan weather program was REFUSED (logged above) -- nothing falls on Vulkan this session");
        return p != null;
    }
}
