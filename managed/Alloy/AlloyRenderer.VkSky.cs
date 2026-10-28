using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The sky dome on Vulkan: the same staged sky list <c>SkyOwnedPass</c> draws, in its own render pass over the scene
/// colour with the window-depth range collapsed to 1, so a dome parked at the camera survives only where nothing
/// wrote depth. A dome whose material names its own fragment stage draws that stage, translated -- its native version
/// where the module ships one, reading that stage's Vulkan images.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The sky's Vulkan programs on one device, replaced whole with the device.</summary>
    private sealed class VkSkyState
    {
        public VkBackend? Dev;
        /// <summary>One translated program per hosted content stage, by resref (the native one's under its own); null for
        /// one that did not build (logged once, then the dome sits out).</summary>
        public readonly Dictionary<string, VkProg?> Hosted = new(StringComparer.Ordinal);
        public bool Live;
        /// <summary>Domes that sat the frame out: a format the arena has not uploaded to Vulkan, a refused pipeline,
        /// a full arena, or a stage Vulkan does not host. Named in the LIVE line.</summary>
        public long SatOut;
    }

    private static VkSkyState _vkSky = new();


    /// <summary>The sky dome into <paramref name="cl"/>: its own pass over the scene colour and depth -- or layer
    /// <paramref name="layer"/> of <paramref name="into"/> over <paramref name="depth"/>, a probe face -- depth test on and
    /// write off as GL's is, at window depth 1. False (logged) only when the device refused the recording.</summary>
    private static bool VkRecordSky(VkBackend vk, CommandList cl, VkColourState s, in Matrix4x4 vp, int* viewport,
                                    ExtenderLog log, ImageId into = default, int layer = 0, ImageId depth = default)
    {
        if (_vkSky.Dev != vk) _vkSky = new VkSkyState { Dev = vk };
        VkSkyState k = _vkSky;
        if (_skyN == 0) return true;
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = s.ArenaUsed;
        side.SetViewport(0, 0, viewport[2], viewport[3]);
        if (!MeshArena.VkEbo.IsNull) side.BindIndexBuffer(MeshArena.VkEbo, IndexType.U16, 0);
        // the dome sits AT the camera: GL's glDepthRange(1,1) under LEQUAL is what makes "after the opaque pass"
        // mean "only where nothing was drawn", and the collapsed Vulkan range is the same thing
        side.SetDepthRange(1f, 1f);
        side.SetDepthState(new DepthState(true, false, CompareOp.LessEqual));
        side.SetRasterState(new RasterState(CullMode.Back));
        var push = new VkBatchPush { AlphaCut = -1f };
        PipelineId pipe = default;
        int fmtBound = -1;
        long drawn = 0;
        Span<int> count = stackalloc int[1], first = stackalloc int[1], baseVertex = stackalloc int[1];
        for (int i = 0; i < _skyN; i++)
        {
            VkProg? p = _skProg[i] != 0 ? VkSkyHosted(k, _skProg[i], log)
                                        : s.Progs[(int)VkPrograms.Id.Fullbright];
            int fmt = _skFmt[i];
            if (p == null || (uint)fmt >= MeshArena.Formats || MeshArena.VkVbo((MeshArena.Fmt)fmt).IsNull) { k.SatOut++; continue; }
            PipelineId id = VkSkyPipeline(p, fmt, 1 + Math.Min(_skSrc[i], 7) * 8 + Math.Min(_skDst[i], 7), _capturing);
            if (id.IsNull || !VkBindSky(side, p, i, vp, viewport, s.Slots)) { k.SatOut++; continue; }
            if (id != pipe) { side.SetPipeline(id); pipe = id; }
            if (fmt != fmtBound) { side.BindVertexBuffer(MeshArena.VkVbo((MeshArena.Fmt)fmt)); fmtBound = fmt; }
            int slot = _skyFirst + i;
            push.DrawBase = slot;
            side.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<VkBatchPush>(in push)));
            count[0] = _dCount[slot];
            first[0] = (int)(_dOff[slot] / sizeof(ushort));
            baseVertex[0] = _dBase[slot];
            side.DrawIndexedMulti(count, first, baseVertex);
            drawn++;
            Counters._batches++;
            Counters._draws++;
        }
        if (drawn == 0) return true;
        Span<ColorAttachment> colors = stackalloc ColorAttachment[1];
        colors[0] = new ColorAttachment(into.IsNull ? s.Hdr : into, 0, layer, LoadOp.Load, StoreOp.Store, default);
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(depth.IsNull ? s.Depth : depth, 0, 0,
                                                                                      LoadOp.Load, StoreOp.Store, 1f)));
        if (!k.Live)
        {
            k.Live = true;
            log.Always($"AlloyRenderer: the Vulkan sky is LIVE -- {drawn} of {_skyN} staged domes drawn"
                     + (k.SatOut > 0 ? $", {k.SatOut} sat out (logged above)" : ""));
        }
        return true;
    }

    /// <summary>Hosted sky program <paramref name="prog"/> (a GL name) translated for Vulkan, built the first time a
    /// dome asks; null when <see cref="SkyStage"/> does not hold it or Vulkan refused it -- said once per stage, and that
    /// dome then sits the frame out exactly as GL's refusal does. A refused native program stands its stage down.</summary>
    private static VkProg? VkSkyHosted(VkSkyState k, uint prog, ExtenderLog log)
    {
        if (!SkyStage.Hosted(prog, out string resref, out bool native, out int stage)) return null;
        string key = native ? StageResources.Prefix + resref : resref;
        if (k.Hosted.TryGetValue(key, out VkProg? hit)) return hit;
        VkProg? p = null;
        if (native && StageResources.Live(stage) && VkPrograms.SkyNativeText(resref, stage, log) is { } nt && _vkSky.Dev is { } nd)
        {
            p = VkBuild(nd, nt, $"sky {key}", lit: false, ContentShimBase + SkyShimBase + (uint)k.Hosted.Count, log);
            if (p != null) VkStageAttach(p, stage, -1);
            else StageResources.Fail(stage, "its Vulkan sky program did not build (logged above)", log);
            log.Always(p != null ? $"AlloyRenderer: the Vulkan sky hosts {key} -- the native stage, its images and passes on Vulkan"
                                 : $"AlloyRenderer: the Vulkan sky program for {key} did not build -- the dome sits out until the sky replants {resref}");
        }
        else if (!native && VkPrograms.SkyText(resref, log) is { } t && _vkSky.Dev is { } dev)
        {
            p = VkBuild(dev, t, $"sky {resref}", lit: false, ContentShimBase + SkyShimBase + (uint)k.Hosted.Count, log);
            log.Always(p != null ? $"AlloyRenderer: the Vulkan sky hosts {resref} -- the dome draws the content's own stage"
                                 : $"AlloyRenderer: the Vulkan sky program for {resref} did not build (logged above) -- the dome is NOT DRAWN");
        }
        k.Hosted[key] = p;
        return p;
    }

    /// <summary>The fake GL program names the sky's hosted stages resolve their locations under: past the content
    /// pairs' range, which is <see cref="ContentShimBase"/> plus a pair key.</summary>
    private const uint SkyShimBase = 1 << 20;   // not-an-offset: a reserved name range

    /// <summary>The sky's pipeline for <paramref name="p"/> drawing arena format <paramref name="fmt"/> under blend
    /// <paramref name="blend"/>, made on first ask; a <paramref name="probe"/> face's keeps the target's alpha, the
    /// capture's distance, which the dome -- infinitely far -- leaves at the clear. Depth clamp is the one state GL's sky sets that the seam cannot
    /// record, so it is baked here: without it a composed sky matrix clips the dome into a hole at the horizon.</summary>
    private static PipelineId VkSkyPipeline(VkProg p, int fmt, int blend, bool probe)
    {
        long key = fmt | (long)blend << 8 | SkyPipeKey | (probe ? SkyProbeKey : 0);
        if (p.Pipes.TryGetValue(key, out PipelineId id)) return id;
        VkColourState s = _vkc;
        var d = new PipelineDesc
        {
            Vs = p.Vs, Fs = p.Fs, Layout = s.Layouts[fmt], Topo = Topology.Triangles,
            Raster = new RasterState(CullMode.Back, false, false, true),
            Depth = new DepthState(true, false, CompareOp.LessEqual),
            Label = $"vk sky {p.Label} {(MeshArena.Fmt)fmt} blend {blend}",
        };
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = Format.Rgba16F;
        d.Targets.Depth = Format.D32F;
        d.Targets.Samples = 1;
        BlendFactor src = VkFactor((blend - 1) >> 3), dst = VkFactor((blend - 1) & 7);
        d.Blend[0] = probe ? new AttachmentBlend(true, src, dst, BlendFactor.Zero, BlendFactor.One)
                           : new AttachmentBlend(true, src, dst, src, dst);
        id = s.Dev!.CreatePipeline(in d);
        p.Pipes[key] = id;
        return id;
    }

    /// <summary>The bit that keeps a sky pipeline's key out of <see cref="VkPipeline"/>'s space, whose keys carry a
    /// format, a target set and a blend pair and never reach it.</summary>
    private const long SkyPipeKey = 1L << 32;   // not-an-offset: a cache-key tag
    /// <summary>The bit that keys a probe face's sky pipeline apart from the screen's.</summary>
    private const long SkyProbeKey = 1L << 33;   // not-an-offset: a cache-key tag

    /// <summary>One dome's bind: the per-pass terms the stage's own uniforms carry and the per-draw ones its
    /// <c>Draw</c> does, captured into the program's loose block, then the bind set with the material's engine
    /// textures on their units. False (logged) when the frame's uniform arena is full.</summary>
    private static bool VkBindSky(CommandList side, VkProg p, int i, in Matrix4x4 vp, int* viewport, BufferId slots)
    {
        VkColourState s = _vkc;
        Array.Clear(p.Block);
        VkUniformShim.Capture(p.Members, p.Block);
        try
        {
            VkUniformShim.Set(p.Vp, in vp);
            VkUniformShim.Set(p.AlphaMip, Renderer.CutMip.V);
            VkUniformShim.Set(p.Viz, _capturing ? 0 : _lightViz);
            Vector3 t = _skTint[i];
            VkUniformShim.Set(p.Tint, [t.X, t.Y, t.Z]);
            VkUniformShim.Set(p.SkyFog, SceneFog.SkyBand);
            VkUniformShim.Set(p.Global, [t.X, t.Y, t.Z, _skAlpha[i]]);
            // stock's sky reads the inverse view's translation column alone, so the eye position is the whole of it
            VkUniformShim.Set(p.ViewInv, Matrix4x4.CreateTranslation(_eyePos));
            UploadFeatureTerms(p.F, viewport);
            VkStageFrame(p);
        }
        finally { VkUniformShim.EndCapture(); }
        int loose = VkArena(p.Block);
        if (loose < 0 || (p.ReadsEngine && VkEngine() < 0)) return false;
        Span<int> tex = _skTex[i];
        VkBindSet(side, p, s, loose, -1, slots, tex[0], tex[1..]);
        return true;
    }
}
