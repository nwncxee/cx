namespace CX.Alloy;

/// <summary>The bake's two programs. The bake stage draws a charted mesh with its chart UV as the position
/// (UV2 -> rect offset/scale -> page NDC) and its world position and vertex normal interpolated, then runs
/// the colour pass's own per-light body against the scratch cube in slot 0, so a baked texel is the lit
/// stage's own arithmetic. The encoder folds a page's scratch planes into the resident, compressed ones.</summary>
internal static partial class LightmapBake
{
    internal static string BakeVs => ShaderSource.Get("cx_bake_v.glsl");

    /// <summary>Conservative coverage: each triangle's edges are pushed out by <c>CX_LM_PUSH</c> page texels
    /// (mitre-clamped at sharp corners), so a face thinner than a texel still owns the texels it crosses --
    /// a window recess a hand deep had no texel centre inside it and baked black. The pushed vertices carry
    /// their barycentrics against the ORIGINAL triangle, which the fragment stage clamps back inside it.</summary>
    internal static string BakeGs => ShaderSource.Get("cx_bake_g.glsl");

    internal static string BakeFs => ShaderSource.Get("cx_bake_f.glsl");

    /// <summary>Per 4x4 block: a covered texel (alpha > 0) converts as is, an uncovered one within two of a
    /// covered one takes its nearest covered neighbour (the dilation that keeps bilinear reads off the gutter),
    /// and the block goes out as BC6H (A) and BC7 (B); C is written raw where the finish has a texel.</summary>
    internal static string EncCs => ShaderSource.Get("cx_bakeenc_c.glsl");
}
