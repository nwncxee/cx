namespace CX.Alloy;

/// <summary>
/// What both halves of the alpha-to-coverage layer share -- the cutout parts whose material asks for coverage
/// instead of a hard cut (<c>parameter int NWN_ALPHA_MODE 2</c> on any <c>.mtr</c>): the gate, the discard, the
/// three full-screen stages and the period line's clause. The pass itself is <c>AlloyRenderer.VkCover</c>.
/// </summary>
internal static class CoverageLayer
{
    /// <summary>The alpha below which a fragment is discarded outright: a coverage of nothing at any sample count.</summary>
    internal const float Cut = 0.01f;

    /// <summary>Does a coverage part with this shader feature have a coverage source the colour draw and the
    /// shadow caster can BOTH sample? The diffuse alpha wherever Alloy's own stage shades or a content pair leaves
    /// its coverage alone. A pair that makes its own answers false and keeps the engine's cut.</summary>
    internal static bool HasCoverageSource(int feat) => !ContentPrograms.Cuts(feat);

    /// <summary>The run's sample count and the frame share it covered, for <see cref="Stats"/>.</summary>
    private sealed class CoverageCountState
    {
        public int Samples;
        public long Runs, Pixels, FrameArea;
    }

    private static readonly CoverageCountState _count = new();

    /// <summary>Will the layer try to open this frame? False hands the coverage tail back to the cutout pass.</summary>
    internal static bool Armed => Renderer.A2c.V >= 2;

    /// <summary>The colour layer's box resolve, the seed from the finished frame, and the depth write-back.</summary>
    internal static string ResolveFs => ShaderSource.Get("cx_a2cres_f.glsl");
    internal static string SeedFs => ShaderSource.Get("cx_a2cseed_f.glsl");
    internal static string DepthFs => ShaderSource.Get("cx_a2cdepth_f.glsl");

    /// <summary>One run, for the period line: its sample count, the pixels it covered and the frame it covered them in.</summary>
    internal static void CountVk(int samples, long pixels, long frameArea)
    {
        _count.Samples = samples;
        _count.Runs++; _count.Pixels += pixels; _count.FrameArea += frameArea;
    }

    internal static string Stats(int frames)
    {
        long n = _count.Runs, px = _count.Pixels, area = _count.FrameArea;
        _count.Runs = 0; _count.Pixels = 0; _count.FrameArea = 0;
        if (n == 0) return Renderer.A2c.V < 2 ? "a2c off" : "a2c idle";
        return $"a2c {(float)n / System.Math.Max(frames, 1):F2} runs/frame, {_count.Samples}x, " +
               $"scissor {100.0 * px / System.Math.Max(area, 1):F1}% of the frame";
    }
}
