using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static unsafe partial class PostStack
{
    /// <summary>One stack stage on Vulkan: its program and the two terms the stack sets on it.</summary>
    private sealed class StageVk
    {
        public GuiVkProg? P;
        public bool Tried;
        public int Dither = -1, Param = -1;
    }

    /// <summary>The stack on one device, replaced whole with the device: the stages, the ping-pong pair, the velocity
    /// target and the TAA history.</summary>
    private sealed class StackVkState
    {
        public VkBackend? Dev;
        public readonly StageVk[] Stages = NewStages();
        public ImageId A, B, Vel, Hist0, Hist1;
        public int W, H, VelW, VelH, HistW, HistH, Cur;
        public bool HistValid;
        public GuiVkProg? Motion, Taa;
        public bool MotionTried, TaaTried;
        public int MInvVp = -1, MPrevVp = -1, MSize = -1, MJitter = -1;
        public int TBlend = -1, TJitterPx = -1, TUpsample = -1;
        /// <summary>The live stages Vulkan cannot draw yet, as last said; null before the first frame.</summary>
        public string? Missing;
    }

    private static StackVkState _vk = new();

    private static StageVk[] NewStages()
    {
        var a = new StageVk[_stages.Length];
        for (int i = 0; i < a.Length; i++) a[i] = new StageVk();
        return a;
    }

    /// <summary>The stages Vulkan draws: the plain ones, and the taa stage with its own resolve. The toon, hosted-chain
    /// and SMAA stages draw passes of their own that are not on Vulkan yet.</summary>
    private static bool OnVulkan(Stage st) => st.Name is "gamma" or "sharpen" or "vibrance" or "taa";

    /// <summary>The TAA history on Vulkan starts over: the next resolve takes the current frame whole.</summary>
    internal static void ResetVk() => _vk.HistValid = false;

    /// <summary>Is the Vulkan taa stage refused on this device? The frame then draws unjittered.</summary>
    internal static bool TaaVkFailed => _vk.TaaTried && (_vk.Taa == null || _vk.Motion == null);

    /// <summary>The post chain and the stack on Vulkan: the resolve into the stack's first image (or the composite
    /// when no stage is live), each live stage after it, the last into the composite with the dither. The taa stage
    /// paints the camera velocity off <paramref name="depth"/> first. False (logged) when the frame has no way to the
    /// screen.</summary>
    internal static bool RecordVk(VkBackend vk, CommandList cl, ImageId hdr, ImageId depth, Matrix4x4* vp,
                                  Matrix4x4* vpClean, int* viewport, int* display, ExtenderLog log)
    {
        if (_vk.Dev != vk) _vk = new StackVkState { Dev = vk };
        StackVkState s = _vk;
        PostChainVk.BeginFrame();
        if (!PostChainVk.Ensure(vk, log)) return false;
        int dw = display[2], dh = display[3];
        int live = 0;
        string missing = "";
        bool taa = false;
        if (Post.Stack.V && _gspI != null && Pair(vk, s, dw, dh, log))
            for (int i = 0; i < _stages.Length; i++)
            {
                Stage st = _stages[i];
                if (st.Failed || !st.Active()) continue;
                if (!OnVulkan(st)) { missing += (missing.Length == 0 ? "" : ", ") + st.Name; continue; }
                StageVk v = s.Stages[i];
                if (!v.Tried) Build(vk, st, v, log);
                if (v.P == null) continue;
                if (st.Name == "taa" && !TaaReady(vk, s, viewport, vp, vpClean, depth, cl, log)) continue;
                taa |= st.Name == "taa";
                live |= 1 << i;
            }
        if (missing != s.Missing)
        {
            s.Missing = missing;
            if (missing.Length > 0)
                log.Always($"PostStack: NOT DRAWN on Vulkan, and switched on -- the {missing} stage(s); the frame resolves without them");
        }
        Rect rect = new(display[0], display[1], dw, dh);
        if (live == 0) return PostChainVk.Record(vk, cl, hdr, depth, vk.Composite, true, viewport, display, log);
        int* full = stackalloc int[4] { 0, 0, dw, dh };
        if (!PostChainVk.Record(vk, cl, hdr, depth, s.A, false, viewport, full, log)) return false;
        ImageId read = s.A, write = s.B;
        int left = System.Numerics.BitOperations.PopCount((uint)live);
        for (int i = 0; i < _stages.Length; i++)
        {
            if ((live & 1 << i) == 0) continue;
            Stage st = _stages[i];
            StageVk v = s.Stages[i];
            bool last = --left == 0;
            ImageId into = last ? vk.Composite : write;
            ImageId hist = st.Name == "taa" ? TaaResolve(cl, s, read, depth, log) : default;
            PostChainVk.Open(v.P!);
            VkUniformShim.Set(v.Dither, last ? PostChain.DitherLsb : 0f);
            if (st.Value != null) VkUniformShim.Set(v.Param, st.Value());
            int at = PostChainVk.Close(cl, v.P!);
            PostChainVk.Draw(cl, v.P!, PostChainVk.FormatOf(into), PostChainVk.Blend.None, into, LoadOp.Clear, last ? rect : new Rect(0, 0, dw, dh),
                             at, new PostChainVk.Inputs(read, default, depth, hist), log);
            (read, write) = (write, read);
        }
        if (taa) MotionVectors.RollHistory();
        _runs++;
        return true;
    }

    /// <summary>Stage <paramref name="st"/>'s program as GL builds it: the overridable pair, or the embedded text when
    /// the override breaks the stack's display-space contract. A refusal is said once and sits the stage out.</summary>
    private static void Build(VkBackend vk, Stage st, StageVk v, ExtenderLog log)
    {
        v.Tried = true;
        GlProgram.Texts(PostChain.ShdPostVs, ShaderStages.Source(PostChain.ShdPostVs), st.Resref, ShaderStages.Source(st.Resref),
                        log, $"PostStack {st.Name}", out string vs, out string fs);
        if (!(ShaderContract.DisplaySafe(fs) && ShaderContract.SlotSafe(fs, "uSrc")))
        {
            log.Info($"PostStack: the {st.Name} override is refused on Vulkan too -- it converts colour space or dropped uSrc");
            vs = PostChain.FullscreenVs;
            fs = PostStageHead + st.Fs + PostStageMain;
        }
        v.P = PostChainVk.Program(vk, vs, fs, $"stack {st.Name}", p =>
        {
            v.Dither = p.Loc("uDither");
            v.Param = st.Param == null ? -1 : p.Loc(st.Param);
        }, log);
        if (v.P == null) log.Always($"PostStack: the {st.Name} stage did not build on Vulkan (logged above) -- it sits the session out");
    }

    /// <summary>The stack's two half-float images at the display size.</summary>
    private static bool Pair(VkBackend vk, StackVkState s, int w, int h, ExtenderLog log)
    {
        if (!s.A.IsNull && s.W == w && s.H == h) return true;
        vk.Destroy(s.A); vk.Destroy(s.B);
        s.A = PostChainVk.Target(vk, w, h, "vk post stack 0");
        s.B = PostChainVk.Target(vk, w, h, "vk post stack 1");
        s.W = w; s.H = h;
        if (!s.A.IsNull && !s.B.IsNull) return true;
        log.Always($"PostStack: the {w}x{h} stack pair was REFUSED on Vulkan (logged above) -- the frame resolves without its stages");
        s.A = default;
        return false;
    }

    /// <summary>The taa stage's inputs this frame: its programs, the history pair at the display size, and the camera
    /// velocity painted off the finished depth. False sits the stage out (the frame resolves without it).</summary>
    private static bool TaaReady(VkBackend vk, StackVkState s, int* viewport, Matrix4x4* vp, Matrix4x4* vpClean,
                                 ImageId depth, CommandList cl, ExtenderLog log)
    {
        if (!s.TaaTried)
        {
            s.TaaTried = true;
            s.Motion = PostChainVk.Program(vk, PostChain.FullscreenVs, MotionVectors.CameraFs, "camera velocity", p =>
            {
                s.MInvVp = p.Loc("uInvVP"); s.MPrevVp = p.Loc("uPrevVP"); s.MSize = p.Loc("uSize"); s.MJitter = p.Loc("uJitter");
            }, log);
            s.Taa = PostChainVk.Program(vk, PostChain.FullscreenVs, TaaStage.TaaFs, "TAA resolve", p =>
            {
                s.TBlend = p.Loc("uBlend"); s.TJitterPx = p.Loc("uJitterPx"); s.TUpsample = p.Loc("uUpsample");
            }, log);
            if (s.Motion == null || s.Taa == null)
                log.Always("PostStack: the Vulkan TAA resolve or its velocity pass did not build (logged above) -- the taa "
                         + "stage sits the session out and the frame draws unjittered");
        }
        if (s.Motion == null || s.Taa == null) return false;
        int w = viewport[2], h = viewport[3];
        if (s.Vel.IsNull || s.VelW != w || s.VelH != h)
        {
            vk.Destroy(s.Vel);
            s.Vel = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg16F, w, h, 1, 1, 1, 1,
                                                 ImageUse.Sampled | ImageUse.ColorTarget, "vk velocity"));
            s.VelW = w; s.VelH = h;
            if (s.Vel.IsNull) return false;
        }
        if (s.Hist0.IsNull || s.HistW != s.W || s.HistH != s.H)
        {
            vk.Destroy(s.Hist0); vk.Destroy(s.Hist1);
            s.Hist0 = PostChainVk.Target(vk, s.W, s.H, "vk taa history 0");
            s.Hist1 = PostChainVk.Target(vk, s.W, s.H, "vk taa history 1");
            s.HistW = s.W; s.HistH = s.H;
            s.HistValid = false;
            if (s.Hist0.IsNull || s.Hist1.IsNull) { s.Hist0 = default; return false; }
        }
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 inv)) return false;
        Matrix4x4 prev = MotionVectors.NoteVk(vpClean, AlloyRenderer.FrameSeq);
        PostChainVk.Open(s.Motion);
        VkUniformShim.Set(s.MInvVp, inv);
        VkUniformShim.Set(s.MPrevVp, prev);
        VkUniformShim.Set(s.MSize, [(float)w, h]);
        VkUniformShim.Set(s.MJitter, [PostChain.JitterX, PostChain.JitterY]);
        int at = PostChainVk.Close(cl, s.Motion);
        PostChainVk.Draw(cl, s.Motion, Format.Rg16F, PostChainVk.Blend.None, s.Vel, LoadOp.DontCare, new Rect(0, 0, w, h), at,
                         new PostChainVk.Inputs(default, default, depth), log);
        return true;
    }

    /// <summary>The accumulator: this frame against the reprojected, clipped history into the other history image,
    /// which the stage draw then copies out with the sharpen. Its image.</summary>
    private static ImageId TaaResolve(CommandList cl, StackVkState s, ImageId cur, ImageId depth, ExtenderLog log)
    {
        ImageId read = s.Cur == 0 ? s.Hist0 : s.Hist1, write = s.Cur == 0 ? s.Hist1 : s.Hist0;
        PostChainVk.Open(s.Taa!);
        VkUniformShim.Set(s.TBlend, s.HistValid ? PostChain.TaaBlend : 1f);
        VkUniformShim.Set(s.TUpsample, 0);
        VkUniformShim.Set(s.TJitterPx, [PostChain.JitterX * 0.5f * s.W, PostChain.JitterY * 0.5f * s.H]);
        int at = PostChainVk.Close(cl, s.Taa!);
        PostChainVk.Draw(cl, s.Taa!, Format.Rgba16F, PostChainVk.Blend.None, write, LoadOp.DontCare, new Rect(0, 0, s.W, s.H), at,
                         new PostChainVk.Inputs(cur, default, depth, read, s.Vel, HistLinear: true), log);
        s.Cur ^= 1;
        s.HistValid = true;
        return write;
    }
}
