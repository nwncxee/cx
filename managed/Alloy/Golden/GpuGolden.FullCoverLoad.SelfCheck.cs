using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>The fixture's edge in texels, the value a pass loads and the one the draw writes.</summary>
    private const int CoverSide = 4;
    private const float CoverSentinel = 1f, CoverDrawn = 0.5f;

    /// <summary>The premise the post stack's <see cref="LoadOp.DontCare"/> loads rest on: a full-viewport
    /// fullscreen triangle writes every texel of its target, so a load has nothing left to preserve. Measured on
    /// the device by LOADING a sentinel instead of clearing it, with the half-viewport control showing the
    /// sentinel where a draw does not reach.</summary>
    private static void FullCoverLoadSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        if (!BlendStages(vk, log, out ShaderId vs, out ShaderId fs)) { check.Skip("the golden's fullscreen stages were refused"); return; }
        PipelineId pipe = BlendPipeline(vk, vs, fs, "golden full cover", Format.Rgba16F, BlendFactor.One, BlendFactor.Zero);
        if (pipe.IsNull) { check.Skip("the golden's fullscreen pipeline was refused"); }
        else
        {
            float[]? full = CoverTexels(vk, log, pipe, CoverSide, "golden full cover");
            float[]? half = CoverTexels(vk, log, pipe, CoverSide / 2, "golden half cover");
            if (full == null || half == null) check.Skip("the full-coverage golden could not read its target back");
            else
            {
                int kept = 0, drawn = 0;
                foreach (float t in full) if (Math.Abs(t - CoverSentinel) < 1e-3f) kept++; else if (Math.Abs(t - CoverDrawn) < 1e-3f) drawn++;
                check.Expect(kept == 0 && drawn == full.Length,
                  $"a full-viewport post draw writes every texel of its target, so a load has nothing to preserve ({drawn} of {full.Length} drawn, {kept} loaded)");
                int keptHalf = 0;
                foreach (float t in half) if (Math.Abs(t - CoverSentinel) < 1e-3f) keptHalf++;
                check.Expect(keptHalf > 0,
                  $"...and the same read finds the loaded texels where a narrower viewport leaves them ({keptHalf} of {half.Length})");
            }
        }
        vk.Destroy(pipe); vk.Destroy(vs); vk.Destroy(fs);
    }

    /// <summary>A <see cref="CoverSide"/>-square RGBA16F target filled with <see cref="CoverSentinel"/>, then
    /// <paramref name="pipe"/> drawn over a <paramref name="vw"/>-wide viewport in a pass that LOADS it: every
    /// texel's red channel, or null when a step was refused.</summary>
    private static float[]? CoverTexels(VkBackend vk, ExtenderLog log, PipelineId pipe, int vw, string label)
    {
        const int Bpp = 8;
        ImageId col = PostImage(vk, Format.Rgba16F, CoverSide, CoverSide, ImageUse.ColorTarget | ImageUse.CopySrc, label);
        BufferId rb = vk.CreateBuffer(new BufferDesc(CoverSide * CoverSide * Bpp, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, label + " read"));
        float[]? got = null;
        if (!col.IsNull && !rb.IsNull)
        {
            var cl = new CommandList(label, 1 << 12, log);
            ReadOnlySpan<ColorAttachment> fill = [new ColorAttachment(col, 0, 0, LoadOp.Clear, StoreOp.Store, new Vector4(CoverSentinel))];
            cl.BeginRenderPass(new RenderPassDesc(fill));
            cl.EndRenderPass();
            ReadOnlySpan<ColorAttachment> over = [new ColorAttachment(col, 0, 0, LoadOp.Load, StoreOp.Store, default)];
            cl.BeginRenderPass(new RenderPassDesc(over));
            cl.SetPipeline(pipe);
            cl.SetViewport(0, 0, vw, CoverSide);
            cl.Draw(3);
            cl.EndRenderPass();
            if (vk.Submit(cl) && PostRead(vk, log, col, CoverSide, CoverSide, Bpp, rb) is { } bytes)
            {
                got = new float[CoverSide * CoverSide];
                for (int i = 0; i < got.Length; i++) got[i] = (float)BitConverter.ToHalf(bytes, i * Bpp);
            }
        }
        vk.Destroy(rb); vk.Destroy(col);
        return got;
    }
}
