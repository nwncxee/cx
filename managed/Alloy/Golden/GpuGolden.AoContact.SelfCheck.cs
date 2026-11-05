using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The AO stages over depth built by hand. Oracle: the clamped single-slice estimate integrated over the slice angles by hand,
/// so open floor at 60 degrees and power 2.2 averages 0.758, and the slice facing a 4 cm block darkens. A flat wall behind a straight slanted silhouette
/// repeats along the edge, so pixels on one step of it must agree, and occlusion must fade before the screen reach cap ends it.
/// </summary>
internal static partial class GpuGolden
{
    private const int AoSide = 128;
    private const float AoNear = 0.1f, AoDist = 10f, AoProjScale = 8f, AoTilt = MathF.PI / 3f, AoBlockHeight = 0.04f;
    /// <summary>The rows the block covers: the floor's far side, so the pixels below the edge look up at it.</summary>
    private const int AoBlockEdge = 61;
    /// <summary>The wall and the figure before it: far enough apart for the blur to keep them apart, near enough to occlude.</summary>
    private const float AoWallDist = 4f, AoFigureDist = 3.79f;
    /// <summary>The close camera: the cap holds the reach to about a third of the radius's own, as it does up close.</summary>
    private const float AoCloseMaxPx = 24f, AoCloseFigureDist = 3.85f;
    /// <summary>A cap the open floor's scene never reaches: its radius takes about 31 px.</summary>
    private const float AoOpenMaxPx = 96f;
    /// <summary>The screen reach cap of the fringe's scene, near the radius so the figure's occlusion stays strong.</summary>
    private const float AoFringeMaxPx = 64f;
    /// <summary>The hardest step allowed from one wall pixel to the next, past the blur's own reach from the silhouette.</summary>
    private const float AoReachStepLimit = 0.04f;
    /// <summary>The worst disagreement allowed among wall pixels touching the silhouette, in visibility.</summary>
    private const float AoFringeLimit = 0.06f;

    private static void AoContactSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        PipelineId raw = SsaoPassVk.Pipeline(vk, SsaoPass.ShdSsaoFs, "golden ao raw", log);
        PipelineId blur = SsaoPassVk.Pipeline(vk, SsaoPass.ShdSsaoBlurFs, "golden ao blur", log);
        check.Expect(!raw.IsNull && !blur.IsNull, "the AO golden's stages build for the device");
        if (raw.IsNull || blur.IsNull) return;
        byte[]? open = AoRun(vk, log, raw, false), block = AoRun(vk, log, raw, true);
        byte[]? fringe = AoDraw(vk, log, raw, blur, AoFigureScene(AoFigureDist), AoFringeMaxPx);
        byte[]? close = AoDraw(vk, log, raw, blur, AoFigureScene(AoCloseFigureDist), AoCloseMaxPx);
        vk.Destroy(blur);
        vk.Destroy(raw);
        check.Expect(open != null && block != null && fringe != null && close != null, "AO golden: a fixture, draw or readback was refused (logged above)");
        if (open == null || block == null || fringe == null || close == null) return;

        float least = 1f, most = 0f, sum = 0f;
        for (int y = 64; y < 68; y++)
            for (int x = 64; x < 68; x++)
            {
                float v = AoAt(open, x, y);
                least = MathF.Min(least, v);
                most = MathF.Max(most, v);
                sum += v;
            }
        Near(check, "AO golden: open floor at 60 degrees keeps its grey", sum / 16f, 0.758, 0.01);
        check.Expect(most - least < 0.02f, $"AO golden: open floor shows no slice pattern, {least:F3}..{most:F3} over one tile");

        // pattern value 8 sits at x&3 == 1, y&3 == 0: its slice runs straight up and down the screen
        const int X = 65, Y = 64;
        float drop = AoAt(open, X, Y) - AoAt(block, X, Y);
        check.Expect(drop > 0.02f, $"AO golden: the slice facing a low block 3 pixels away darkens, by {drop:F3}");

        AoFringeChecks(check, fringe, close);
    }

    /// <summary>The figure's first column on row <paramref name="y"/> past its slanted edge: 2 rows per column.</summary>
    private static int AoEdgeColumn(int y) => 40 + y / 2;

    /// <summary>Depth of the wall with the figure at <paramref name="figureDist"/> before it.</summary>
    private static float[] AoFigureScene(float figureDist)
    {
        var d = new float[AoSide * AoSide];
        for (int y = 0; y < AoSide; y++)
        {
            Span<float> row = d.AsSpan(y * AoSide, AoSide);
            row[..AoEdgeColumn(y)].Fill(AoNear / AoWallDist);
            row[AoEdgeColumn(y)..].Fill(AoNear / figureDist);
        }
        return d;
    }

    private static void AoFringeChecks(Checker check, byte[] px, byte[] close)
    {
        // the same step of the stair repeats every 2 rows, so only the pattern can part rows of one parity
        for (int parity = 0; parity < 2; parity++)
        {
            float least = 1f, most = 0f;
            for (int y = 52 + parity; y < 76; y += 2)
            {
                float v = AoAt(px, AoEdgeColumn(y) - 1, y);
                least = MathF.Min(least, v);
                most = MathF.Max(most, v);
            }
            check.Expect(most - least < AoFringeLimit, $"AO golden: the wall beside a silhouette shows no dots, {least:F3}..{most:F3} on rows of parity {parity}");
        }

        const int Row = 64;
        float step = 0f;
        for (int x = AoEdgeColumn(Row) - 6; x > 1; x--)
            step = MathF.Max(step, MathF.Abs(AoAt(close, x, Row) - AoAt(close, x - 1, Row)));
        check.Expect(step < AoReachStepLimit, $"AO golden: occlusion fades out before the screen reach ends, hardest step {step:F3}");
    }

    private static float AoAt(byte[] px, int x, int y) => px.AsSpan(y * AoSide, AoSide)[x] / 255f;

    /// <summary>View distance on the floor at row <paramref name="y"/>: row 0 is its far side, and
    /// <paramref name="raise"/> lifts it along its normal.</summary>
    private static float AoFloorDist(int y, float raise)
    {
        float ny = (y + 0.5f) / AoSide * 2f - 1f;
        float c = MathF.Cos(AoTilt), s = MathF.Sin(AoTilt);
        return (AoDist * c - raise) / (s * ny / AoProjScale + c);
    }

    /// <summary>One draw of <paramref name="pipe"/> over the floor, with or without the block; the R8 target's bytes,
    /// null when a step was refused.</summary>
    private static byte[]? AoRun(VkBackend vk, ExtenderLog log, PipelineId pipe, bool withBlock)
    {
        var d = new float[AoSide * AoSide];
        for (int y = 0; y < AoSide; y++)
        {
            float z = AoNear / AoFloorDist(y, withBlock && y < AoBlockEdge ? AoBlockHeight : 0f);
            d.AsSpan(y * AoSide, AoSide).Fill(z);
        }
        return AoDraw(vk, log, pipe, default, d, AoOpenMaxPx);
    }

    /// <summary>The raw stage over <paramref name="d"/>, then <paramref name="blur"/> when it has one, as the pass records
    /// them; the last R8 target's bytes, null when a step was refused.</summary>
    private static byte[]? AoDraw(VkBackend vk, ExtenderLog log, PipelineId pipe, PipelineId blur, float[] d, float maxPx)
    {
        ImageId depth = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, AoSide, AoSide, 1, 1, 1, 1,
                                                     ImageUse.Sampled | ImageUse.CopyDst, "golden ao depth"));
        ImageId rawOut = blur.IsNull ? default : vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8, AoSide, AoSide, 1, 1, 1, 1,
                                                                              ImageUse.ColorTarget | ImageUse.Sampled, "golden ao raw"));
        ImageId target = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8, AoSide, AoSide, 1, 1, 1, 1,
                                                      ImageUse.ColorTarget | ImageUse.CopySrc, "golden ao target"));
        BufferId ubo = vk.CreateBuffer(new BufferDesc(Std140.BlockBytes<SsaoBlock>(), BufferUse.Uniform, MemoryKind.Upload, "golden ao block"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(AoSide * AoSide, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden ao read"));
        try
        {
            if (depth.IsNull || (rawOut.IsNull && !blur.IsNull) || target.IsNull || ubo.IsNull || rb.IsNull) return null;
            if (!vk.Upload(depth, 0, 0, MemoryMarshal.AsBytes(d.AsSpan()))) return null;

            var b = new SsaoBlock
            {
                Proj = new Matrix4x4(AoProjScale, 0f, 0f, 0f, 0f, AoProjScale, 0f, 0f, 0f, 0f, 0f, -1f, 0f, 0f, AoNear, 0f),
                Rt = new Vector4(1f / AoSide, 1f / AoSide, AoSide, AoSide),
                Radius = 0.6f, Power = 2.2f, MaxPx = maxPx, Dirs = 1, Steps = 3,
            };
            Span<byte> blk = stackalloc byte[Std140.BlockBytes<SsaoBlock>()];
            blk.Clear();
            MemoryMarshal.Write(blk, in b);
            var cl = new CommandList("golden ao", 1 << 12, log);
            cl.UpdateBuffer(ubo, 0, blk);
            cl.ReadImages([depth]);
            SsaoPassVk.RecordStages(cl, pipe, blur, rawOut, target, depth, new Rect(0, 0, AoSide, AoSide), [new(UboSlots.Ssao, ubo)]);
            return vk.Submit(cl) ? PostRead(vk, log, target, AoSide, AoSide, 1, rb) : null;
        }
        finally
        {
            vk.Destroy(rb); vk.Destroy(ubo); vk.Destroy(target); vk.Destroy(rawOut); vk.Destroy(depth);
        }
    }
}
