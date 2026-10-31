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
/// the pass block's sun and ambient, stopped under cover by the sun array's cover layer; the roof drips are rain's
/// program again, kept only under a lip.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The rain, snow and splash programs on one device.</summary>
    private sealed class VkWxState
    {
        public VkBackend? Dev;
        public readonly VkProg?[] Prog = new VkProg?[3];
        public readonly PipelineId[] Pipe = new PipelineId[3];
        public readonly Weather.Loc[] L = new Weather.Loc[2];
        public Weather.SpLoc Sp;
        public readonly bool[] Tried = new bool[3];
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
        int count = Weather.StageVk(AreaWind(), out bool snow, out int drips);
        int k = snow ? 1 : 0;
        if (count < 0) return true;
        bool fall = count > 0 && VkWxEnsure(vk, s, k, log), drip = drips > 0 && VkWxEnsure(vk, s, 0, log);
        cl.CopyImage(new CopyImageCmd(c.Depth, new ImageRegion(0, 0, 0, 0), c.DepthRead, new ImageRegion(0, 0, 0, 0), c.W, c.H, 1));
        ReadOnlySpan<ImageId> read = [c.DepthRead];
        cl.ReadImages(read);
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, viewport[2], viewport[3]);
        bool lit = lightingOn && AlloyLights.Ready;
        if (fall && !VkWxDraw(side, s, c, k, snow ? 2 : 1, count, view, proj, viewport, near, far, lit)) return false;
        if (drip && !VkWxDraw(side, s, c, 0, 3, drips, view, proj, viewport, near, far, lit)) return false;
        if (VkWxEnsure(vk, s, VkSplashProg, log) && !VkSplashDraw(side, s, c, view, proj, viewport, near, far, lit)) return false;
        ReadOnlySpan<ColorAttachment> colors = [new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, WorldDepth.Clear)));
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan {(snow ? "snow" : "rain")} is LIVE -- {count} instance(s) and {drips} drip(s), "
                     + "stopped under the cover map");
        }
        return true;
    }

    /// <summary>One instanced draw of <paramref name="kind"/> (1 rain, 2 snow, 3 drips) through program
    /// <paramref name="k"/>, its own loose block; false only when the arena is full.</summary>
    private static bool VkWxDraw(CommandList side, VkWxState s, VkColourState c, int k, int kind, int count, in Matrix4x4 view,
                                 in Matrix4x4 proj, int* viewport, float near, float far, bool lit)
    {
        VkProg p = s.Prog[k]!;
        side.SetPipeline(s.Pipe[k]);
        Matrix4x4 v = view, pr = proj;
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            BindPass(p.L.CubeCount, viewport, near, far);
            Weather.WriteVk(in s.L[k], kind, &v, &pr, _eyePos, lit);
        }
        finally { LooseBlock.EndCapture(); }
        int loose = VkArena(p.Block), pass = VkArenaPass();
        if (loose < 0 || pass < 0) return false;
        VkBindSet(side, p, c, loose, pass, c.Slots, 0);
        side.Draw(4, count, 0, 0);
        return true;
    }

    /// <summary>The splash program's slot in <see cref="VkWxState"/>, past rain's and snow's.</summary>
    private const int VkSplashProg = 2;

    /// <summary>The rain and footfall crowns, through the splash program; false only when the arena is full.</summary>
    private static bool VkSplashDraw(CommandList side, VkWxState s, VkColourState c, in Matrix4x4 view, in Matrix4x4 proj,
                                     int* viewport, float near, float far, bool lit)
    {
        VkProg p = s.Prog[VkSplashProg]!;
        Matrix4x4 v = view, pr = proj;
        int n;
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            BindPass(p.L.CubeCount, viewport, near, far);
            n = Weather.WriteSplashVk(in s.Sp, &v, &pr, viewport, _eyePos, lit);
        }
        finally { LooseBlock.EndCapture(); }
        if (n == 0) return true;
        int loose = VkArena(p.Block), pass = VkArenaPass();
        if (loose < 0 || pass < 0) return false;
        side.SetPipeline(s.Pipe[VkSplashProg]);
        VkBindSet(side, p, c, loose, pass, c.Slots, 0);
        side.Draw(4, n, 0, 0);
        return true;
    }

    /// <summary>Program <paramref name="k"/> (0 rain, 1 snow, 2 splash) and its premultiplied pipeline, made once per device.</summary>
    private static bool VkWxEnsure(VkBackend vk, VkWxState s, int k, ExtenderLog log)
    {
        if (s.Tried[k]) return s.Prog[k] != null;
        s.Tried[k] = true;
        s.Side ??= new CommandList("vulkan weather", 1 << 14, log);
        (string vs, string fs) = k == VkSplashProg ? Weather.VkSplashTexts(log) : Weather.VkTexts(k == 1, log);
        string label = k == VkSplashProg ? "splash" : k == 1 ? "snow" : "rain";
        var units = new System.Collections.Generic.Dictionary<string, uint>(VkPrograms.Units) { ["uCover"] = TextureSlots.CoverEsm };
        VkProgramText t = WorldDepth.Translate(ShaderResource.Tokens(vs), ShaderResource.Tokens(fs), units);
        VkProg? p = VkBuild(vk, t, label, true, log);
        if (p != null)
        {
            VkWxState w = s;
            if (k == VkSplashProg) LooseBlock.Resolve(p.Names, () => w.Sp = Weather.VkSplashLocs());
            else LooseBlock.Resolve(p.Names, () => w.L[k] = Weather.VkLocs());
            var d = new PipelineDesc
            {
                Vs = p.Vs, Fs = p.Fs, Topo = Topology.TriangleStrip, Raster = new RasterState(CullMode.None),
                Depth = new DepthState(true, false, WorldDepth.NearerOrEqual), Label = $"vk {label}",
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
        if (p == null)
            log.Always(k == VkSplashProg ? "AlloyRenderer: the Vulkan splash program was REFUSED (logged above) -- no rain impacts on Vulkan this session"
                                         : "AlloyRenderer: the Vulkan weather program was REFUSED (logged above) -- nothing falls on Vulkan this session");
        return p != null;
    }
}
