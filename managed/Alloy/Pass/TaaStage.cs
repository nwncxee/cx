using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Temporal anti-aliasing as a post-stack stage. The projection is jittered by a sub-pixel
/// Halton(2,3) offset every frame (<see cref="PostChain.Jitter"/>, applied at the one site Alloy reads it)
/// and this stage averages the jittered frames, resolving in <see cref="Prepare"/>; the stage draw copies the
/// new history out with the <c>post.taasharp</c> unsharp mask, so the history carries neither it nor dither.</summary>
internal static unsafe class TaaStage
{
    private static ExtenderLog? _log;
    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no name or
    /// uniform location from the dead context survives as a field nobody remembered to zero. The history's
    /// validity rides here because the world edge clears that one field by itself.</summary>
    private sealed class TaaState
    {
        public uint Fbo, Hist0, Hist1, Prog, Sampler, StageProg;
        public int LocCur, LocHist, LocVel, LocDepth, LocBlend, LocStageHist, LocJitterPx, LocUpsample;
        public int W, H, Cur;
        public bool Failed, Built, Valid;
    }

    private static TaaState _gl = new();

    /// <summary>Latched: a program or target failed on this context; the jitter stands down with the stage.</summary>
    internal static bool Failed => _gl.Failed;
    /// <summary>The stage's fragment text: the new history, with the post-resolve sharpen (display space in,
    /// display space out). A temporal average is softer at an edge than a spatial filter is, which is the one
    /// thing TAA gets held against SMAA for; <c>post.taasharp</c> is a cross-tap unsharp mask that puts the
    /// contrast back. The taps are texel-exact fetches: the history is NEAREST at this stage's own size.</summary>
    internal static string StageFs => ShaderSource.Get("cx_taastage_f.glsl");

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>History invalid: the next resolve takes the current frame whole. World edge (area transition)
    /// and every /post taa 1.</summary>
    internal static void Reset() { _gl.Valid = false; PostStack.ResetVk(); }

    /// <summary>Context loss: forget every GL name.</summary>
    internal static void Forget()
    => _gl = new TaaState();

    /// <summary>From <see cref="PostStack.Begin"/>: can the stage run at this size this frame? Needs the
    /// velocity target to have painted (the first frame after arming has none, and resolves plain).</summary>
    internal static bool Ready(int w, int h)
    {
        if (_gl.Failed) return false;
        if (Gl.GenSamplers == null) { _gl.Failed = true; _log?.Info("TaaStage: no sampler objects on this context -- the taa stage is DROPPED"); return false; }
        if (!MotionVectors.Painted) return false;
        if (!_gl.Built && !Build()) return false;
        return w == _gl.W && h == _gl.H || Size(w, h);
    }

    private static bool Build()
    {
        _gl.Built = true;
        _gl.Prog = GlProgram.Graphics(PostChain.FullscreenVs, TaaFs, _log, "TAA resolve");
        if (_gl.Prog == 0) { _gl.Failed = true; _log?.Info("TaaStage: the resolve did not build -- the taa stage is DROPPED; SMAA serves the aa option"); return false; }
        _gl.LocCur = GlProgram.Loc(_gl.Prog, "uCur"); _gl.LocHist = GlProgram.Loc(_gl.Prog, "uHist");
        _gl.LocVel = GlProgram.Loc(_gl.Prog, "uVel"); _gl.LocBlend = GlProgram.Loc(_gl.Prog, "uBlend");
        _gl.LocDepth = GlProgram.Loc(_gl.Prog, "uDepth");
        _gl.LocJitterPx = GlProgram.Loc(_gl.Prog, "uJitterPx"); _gl.LocUpsample = GlProgram.Loc(_gl.Prog, "uUpsample");
        return true;
    }

    private static bool Size(int w, int h)
    {
        if (w <= 0 || h <= 0) return false;
        if (_gl.Fbo == 0)
        {
            uint f; Gl.GenFramebuffers(1, &f); _gl.Fbo = f;
            _gl.Sampler = Gl.LinearClampSampler();
        }
        RenderTarget.AllocColor(ref _gl.Hist0, true, w, h, Gl.NEAREST);
        RenderTarget.AllocColor(ref _gl.Hist1, true, w, h, Gl.NEAREST);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _gl.Hist0, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            _gl.Failed = true;
            _log?.Info($"TaaStage: the history target is incomplete (0x{status:x}) -- the taa stage is DROPPED");
            return false;
        }
        _gl.W = w; _gl.H = h; _gl.Valid = false;
        _log?.Info($"TaaStage: history pair {w}x{h} RGBA16F");
        return true;
    }

    /// <summary>The resolve, from <see cref="PostStack.Run"/> ahead of the stage draw: the current frame
    /// (<paramref name="src"/>) against the reprojected, clamped history into the other history texture, which
    /// is then left on <see cref="TextureSlots.AaEdges"/> for the stage program. The caller rebinds its own
    /// target and viewport after. Hot: allocates nothing.</summary>
    internal static void Prepare(uint src, uint stageProg, int* viewport)
    {
        uint read = _gl.Cur == 0 ? _gl.Hist0 : _gl.Hist1, write = _gl.Cur == 0 ? _gl.Hist1 : _gl.Hist0;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, write, 0);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.PostHdr); Gl.BindTexture(Gl.TEXTURE_2D, src);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaEdges); Gl.BindTexture(Gl.TEXTURE_2D, read);
        Gl.BindSampler(TextureSlots.AaEdges, _gl.Sampler);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaArea); Gl.BindTexture(Gl.TEXTURE_2D, MotionVectors.Tex);
        Gl.BindSampler(TextureSlots.AaArea, _gl.Sampler);
        Gl.UseProgram(_gl.Prog);
        Gl.Uniform1i(_gl.LocCur, (int)TextureSlots.PostHdr);
        Gl.Uniform1i(_gl.LocHist, (int)TextureSlots.AaEdges);
        Gl.Uniform1i(_gl.LocVel, (int)TextureSlots.AaArea);
        // PostStack.Run bound the frame depth on this unit before the stage loop; it is the velocity
        // target's own size, which is what the dilation walks in.
        Gl.Uniform1i(_gl.LocDepth, (int)TextureSlots.PostBloom);
        Gl.Uniform1f(_gl.LocBlend, _gl.Valid ? PostChain.TaaBlend : 1f);
        // Upsampling: the current frame is render-sized and read bilinearly at the jitter-shifted position
        // of each display pixel, so it takes the history's LINEAR sampler for this draw (Run unbinds it).
        bool up = PostStack.Upsampling;
        if (up) Gl.BindSampler(TextureSlots.PostHdr, _gl.Sampler);
        Gl.Uniform1i(_gl.LocUpsample, up ? 1 : 0);
        // NDC jitter to render pixels: +j in NDC moved the image +j * size/2 pixels (Frame.cs, proj.M31)
        Gl.Uniform2f(_gl.LocJitterPx, PostChain.JitterX * 0.5f * PostStack.Width, PostChain.JitterY * 0.5f * PostStack.Height);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindSampler(TextureSlots.AaEdges, 0);
        Gl.BindSampler(TextureSlots.AaArea, 0);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaArea); Gl.BindTexture(Gl.TEXTURE_2D, 0);
        // the new history on the stage's unit -- same size as the target, so its NEAREST read is exact
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.AaEdges); Gl.BindTexture(Gl.TEXTURE_2D, write);
        if (stageProg != _gl.StageProg) { _gl.StageProg = stageProg; _gl.LocStageHist = GlProgram.Loc(stageProg, "uHist"); }
        Gl.UseProgram(stageProg);
        Gl.Uniform1i(_gl.LocStageHist, (int)TextureSlots.AaEdges);
        _gl.Cur ^= 1; _gl.Valid = true;
    }

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
        check.Expect(TaaFs.Contains("vec2 CxDilatedVel(vec2 uv, vec2 vsz)") && TaaFs.Contains("if (d < bestD) { bestD = d; best = o; }")
          && TaaFs.Contains("float bestD = textureLod(uDepth, uv, 0.0).r;"),
          "the velocity is dilated to the CLOSEST depth in the 3x3, and an all-equal depth keeps the centre tap");
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
        _gl.Valid = true; Reset(); check.Expect(!_gl.Valid, "Reset drops the history");
        log.Info(check.Ok ? "TaaStage.SelfCheck: passed" : "TaaStage.SelfCheck: FAILED");
        return check.Ok;
    }
}
