using CX.Framework;

namespace CX.Alloy;

/// <summary>Temporal anti-aliasing as a post-stack stage. The projection is jittered by a sub-pixel
/// Halton(2,3) offset every frame (<see cref="PostChain.Jitter"/>, applied at the one site Alloy reads it)
/// and this stage averages the jittered frames; the stage draw copies the new history out with the
/// <c>post.taasharp</c> unsharp mask, so the history carries neither it nor dither.</summary>
internal static class TaaStage
{
    /// <summary>The stage's fragment text: the new history, with the post-resolve sharpen (display space in,
    /// display space out). A temporal average is softer at an edge than a spatial filter is, which is the one
    /// thing TAA gets held against SMAA for; <c>post.taasharp</c> is a cross-tap unsharp mask that puts the
    /// contrast back. The taps are texel-exact fetches: the history is NEAREST at this stage's own size.</summary>
    internal static string StageFs => ShaderSource.Get("cx_taastage_f.glsl");

    /// <summary>History invalid: the next resolve takes the current frame whole. World edge (area transition)
    /// and every /post taa 1.</summary>
    internal static void Reset() => PostStack.ResetVk();

    /// <summary>The accumulator. Velocity is <c>prev - cur</c> in RENDER pixels, so it is turned into uv
    /// through ITS OWN size, and it is DILATED -- the closest depth in the 3x3 names the velocity the whole
    /// neighbourhood reprojects with. The history is fetched Catmull-Rom and VARIANCE-CLIPPED in YCoCg, and
    /// the blend is inverse-luma weighted, so one firefly texel cannot outvote ten frames of history.</summary>
    internal static string TaaFs => ShaderSource.Get("cx_taa_f.glsl");

    /// <summary>Off-device: the stage composes under the stack's contract, the resolve dilates and reprojects
    /// through the velocity target in ITS units, variance-clips the history in YCoCg, and the gate is one key.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(StageFs.Contains("vec4 CxStage(vec4 c)") && ShaderContract.DisplaySafe(StageFs) && ShaderContract.SlotSafe(StageFs, "uHist"),
          "the stage is display space and reads the history");
        check.Expect(StageFs.Contains("uniform float uSharp;") && StageFs.Contains("h * 4.0 - n") && StageFs.Contains("if (uSharp > 0.0)"),
          "the copy-out carries the unsharp mask and is the raw history at taasharp 0");
        check.Expect(Post.TaaSharp.Default > 0f && Post.TaaSharp.Min == 0f, "post.taasharp ships sharpening, and 0 is the A/B back to the raw resolve");
        check.Expect(Post.Taa.RungValue(Quality.Shipped), "TAA is the DEFAULT anti-aliaser: a fresh cx.cfg resolves temporally and SMAA stands aside");
        check.Expect(TaaFs.Contains("vec2 velPx = CxDilatedVel(vUV, vsz);") && TaaFs.Contains("vec2 huv = vUV + velPx / vsz;")
          && TaaFs.Contains("vec2 vsz = vec2(textureSize(uVel, 0));"),
          "velocity becomes uv through the VELOCITY target's size, not the frame's (#391)");
        check.Expect(TaaFs.Contains("vec2 CxDilatedVel(vec2 uv, vec2 vsz)") && TaaFs.Contains("if (d > bestD) { bestD = d; best = o; }")
          && TaaFs.Contains("float bestD = textureLod(uDepth, uv, 0.0).r;"),
          "the velocity is dilated to the CLOSEST (reversed: greatest) depth in the 3x3, and an all-equal depth keeps the centre tap");
        check.Expect(TaaFs.Contains("vec3 CxHistory(vec2 uv, vec2 size)") && TaaFs.Contains("CxToYCoCg(CxHistory(huv, hsz))")
          && TaaFs.Contains("vec2 hsz = vec2(textureSize(uHist, 0));"),
          "history is fetched Catmull-Rom at ITS OWN size");
        check.Expect(TaaFs.Contains("vec3 sigma = sqrt(max(m2 / 9.0 - mean * mean, vec3(0.0)));")
          && TaaFs.Contains("vec3 mn = min(mean - CX_TAA_GAMMA * sigma, cy), mx = max(mean + CX_TAA_GAMMA * sigma, cy);")
          && TaaFs.Contains("vec3 CxClipToBox(vec3 mn, vec3 mx, vec3 q)"),
          "the clamp is a VARIANCE clip (mean +- gamma * stddev) along the line to the current sample, not a min/max box");
        check.Expect(TaaFs.Contains("const float CX_TAA_GAMMA = 1.25;"), "...at the width one standard TAA ships");
        check.Expect(TaaFs.Contains("vec3 CxToYCoCg(vec3 c)") && TaaFs.Contains("vec3 CxFromYCoCg(vec3 c)"), "the clip happens in a luma/chroma basis");
        // #391: the upsampling arm reads the render-sized frame at the jitter-shifted position, with the
        // same sign the velocity pass takes the jitter back out with (ndc - uJitter: the frame is at +j).
        check.Expect(TaaFs.Contains("vec2 sp = vUV * vec2(sz) + uJitterPx;") && TaaFs.Contains("c = textureLod(uCur, sp / vec2(sz), 0.0);"),
          "upsampling samples the frame at clean position + jitter");
        check.Expect(TaaFs.Contains("if (uUpsample != 0) a = mix(a * 0.25, a, conf);"), "a between-texel sample leans on the history");
        check.Expect(ShaderContract.SlotSafe(TaaFs, "uCur", "uHist", "uVel", "uDepth"), "the resolve declares the samplers Prepare binds");
        check.Expect(TaaFs.Contains("uBlend >= 1.0 ? 1.0 : min(0.5, uBlend + length(velPx) * 0.05)"), "a moving pixel converges faster than the resting share");
        check.Expect(TaaFs.Contains("float wc = a / (1.0 + max(cy.x, 0.0)), wh = (1.0 - a) / (1.0 + max(hy.x, 0.0));"),
          "the blend is inverse-luma weighted: a one-frame firefly cannot outvote the history");
        check.Expect(TaaFs.Contains("a = 1.0;") && TaaFs.Contains("(c.rgb * wc + h * wh) / max(wc + wh, 1e-5)"),
          "an off-screen history sample takes the current frame whole");
        log.Info(check.Ok ? "TaaStage.SelfCheck: passed" : "TaaStage.SelfCheck: FAILED");
        return check.Ok;
    }
}
