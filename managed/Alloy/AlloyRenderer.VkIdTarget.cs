using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>
/// <see cref="IdTarget"/> on Vulkan: the depth prepass's second attachment, one R32UI per pixel packing the record
/// slot and the primitive, and <c>/alloy viz 40</c>'s paint of it. The Vulkan depth stages already declare
/// <c>oVisId</c> -- they are GL's own text -- so until this the prepass had an output no attachment consumed.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The vis-ID target on one device, replaced whole with the device.</summary>
    private sealed class VkIdState
    {
        public VkBackend? Dev;
        public ImageId Tex;
        public int W, H;
        public bool Failed, Logged;
        /// <summary>This frame's prepass wrote the target, so the viz may paint it.</summary>
        public bool Armed;
        public PipelineId Viz;
        public ShaderId VizVs, VizFs;
        public SamplerId Nearest;
    }

    private static VkIdState _vkIds = new();

    /// <summary>Attachments the prepass names: slot 0 routed nowhere, the vis-ID on slot 1, as GL's
    /// <c>DrawBuffers(NONE, COLOR_ATTACHMENT1)</c> routes them.</summary>
    private const int PrepassTargets = 2;

    /// <summary>This frame's prepass attachment set: the vis-ID target when it exists, nothing otherwise.
    /// <paramref name="clear"/> on the first phase; the retest lands on the same image.</summary>
    private static ReadOnlySpan<ColorAttachment> VkIdAttachments(Span<ColorAttachment> into, bool clear)
    {
        VkIdState s = _vkIds;
        if (!s.Armed) return default;
        into[0] = default;
        into[1] = new ColorAttachment(s.Tex, 0, 0, clear ? LoadOp.Clear : LoadOp.Load, StoreOp.Store, default);
        return into[..PrepassTargets];
    }

    /// <summary>The target for this frame's prepass; false = no vis-ID this frame and the prepass draws depth
    /// alone, as it did before the target ported.</summary>
    private static bool VkIdBegin(VkBackend vk, int w, int h, ExtenderLog log)
    {
        VkIdState s = _vkIds;
        if (s.Dev != vk) _vkIds = s = new VkIdState { Dev = vk };
        s.Armed = false;
        if (!IdTarget.On || s.Failed || _capturing || w <= 0 || h <= 0) return false;
        if (s.Tex.IsNull || s.W != w || s.H != h)
        {
            vk.Destroy(s.Tex);
            s.Tex = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32Ui, w, h, 1, 1, 1, 1,
                                                 ImageUse.ColorTarget | ImageUse.Sampled, "vk vis-id target"));
            s.W = w; s.H = h;
            if (s.Tex.IsNull)
            {
                s.Failed = true;
                log.Always($"AlloyRenderer: the Vulkan vis-ID target at {w}x{h} was REFUSED (logged above) -- "
                         + "/alloy viz 40 is blank and the prepass draws depth alone for the session");
                return false;
            }
        }
        s.Armed = true;
        if (!s.Logged)
        {
            s.Logged = true;
            log.Always($"AlloyRenderer: the Vulkan vis-ID target is LIVE at {w}x{h} R32UI -- the prepass packs the "
                     + "record slot and the primitive into it, and /alloy viz 40 paints it");
        }
        return true;
    }

    /// <summary><c>/alloy viz 40</c>: every pixel's ID hashed to a colour over the scene colour, recorded after
    /// the colour bracket. Nothing when the target did not arm or the viz is off.</summary>
    private static void VkIdPaint(VkBackend vk, CommandList cl, VkColourState c, ExtenderLog log)
    {
        VkIdState s = _vkIds;
        if (!s.Armed || _lightViz != IdTarget.VizMode || _capturing) return;
        if (!VkIdVizMade(vk, s, log)) return;
        // the prepass wrote it as a target: hand it over before the paint's pass begins, where a barrier can go
        ReadOnlySpan<ImageId> target = [s.Tex];
        cl.ReadImages(target);
        Span<ColorAttachment> colors = stackalloc ColorAttachment[1];
        colors[0] = new ColorAttachment(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default);
        cl.BeginRenderPass(new RenderPassDesc(colors));
        cl.SetPipeline(s.Viz);
        cl.SetViewport(0, 0, c.W, c.H);
        ReadOnlySpan<ImageBinding> tex = [new(0, s.Tex, s.Nearest)];
        cl.BindSet(0, new BindSet(default, default, tex, default));
        cl.Draw(3);
        cl.EndRenderPass();
    }

    /// <summary>The viz's own pipeline, built the first time the mode is asked for.</summary>
    private static bool VkIdVizMade(VkBackend vk, VkIdState s, ExtenderLog log)
    {
        if (!s.Viz.IsNull) return true;
        if (!s.VizVs.IsNull) return false;                     // built once and refused: stay quiet
        var units = new Dictionary<string, uint>(StringComparer.Ordinal) { ["uId"] = 0 };
        VkProgramText t = VkGlsl.Translate(ShaderSource.Get("cx_fullscreen_v.glsl"), IdTarget.VizFs, units, glDepth: false);
        string ve = "", fe = "";
        byte[]? vs = t.Error == null ? CX.Graphics.VulkanToolchain.Compile(VkBackend.Translate(t.Vs), "vk id viz vs", ShaderKind.VertexShader, out ve) : null;
        byte[]? fs = t.Error == null ? CX.Graphics.VulkanToolchain.Compile(VkBackend.Translate(t.Fs), "vk id viz fs", ShaderKind.FragmentShader, out fe) : null;
        if (vs == null || fs == null)
        {
            s.VizVs = vk.CreateShader(ShaderStage.Vertex, [], "vk id viz refused");   // a non-null latch, nothing else
            log.Always($"AlloyRenderer: the Vulkan vis-ID viz did not build -- {t.Error ?? (vs == null ? ve : fe)}; /alloy viz 40 paints nothing");
            return false;
        }
        s.VizVs = vk.CreateShader(ShaderStage.Vertex, vs, "vk id viz vs");
        s.VizFs = vk.CreateShader(ShaderStage.Fragment, fs, "vk id viz fs");
        s.Nearest = vk.CreateSampler(new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.None, Wrap.Clamp));
        var d = new PipelineDesc { Vs = s.VizVs, Fs = s.VizFs, Label = "vk id viz" };
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = Format.Rgba16F;
        d.Targets.Samples = 1;
        s.Viz = vk.CreatePipeline(in d);
        if (s.Viz.IsNull) log.Always("AlloyRenderer: the Vulkan vis-ID viz pipeline was REFUSED -- /alloy viz 40 paints nothing");
        return !s.Viz.IsNull;
    }

    /// <summary>Off-device: the Vulkan prepass puts the vis-ID on the slot its fragment stages write, and the
    /// viz reads it unfiltered. Oracles: the embedded GLSL text of the shared output declaration and the viz
    /// stage, against the attachment index this file uses.</summary>
    internal static void VkIdSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.VkIdSelfCheck");
        check.Expect(IdTarget.OutDecl.Contains($"layout(location = {PrepassTargets - 1}) out uint oVisId;"),
            $"the depth stages write the vis-ID on location {PrepassTargets - 1}, the slot this pass attaches it to");
        check.Expect(DepthFs.Contains(IdTarget.OutDecl) && DepthKeyFs.Contains(IdTarget.OutDecl)
                     && DepthFeatFs.Contains(IdTarget.OutDecl),
            "every Vulkan prepass stage is GL's own text and so declares that output");
        check.Expect(IdTarget.VizFs.Contains("usampler2D uId;") && IdTarget.VizFs.Contains("texelFetch(uId"),
            "the viz reads the integer target unfiltered, so its sampler is NEAREST and its format R32UI");
        log.Info(check.Ok ? "AlloyRenderer.VkIdSelfCheck: passed" : "AlloyRenderer.VkIdSelfCheck: FAILED");
    }
}
