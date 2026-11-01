using System;
using System.Numerics;
using System.Runtime.InteropServices;
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
        /// <summary>The hosted chain's own terms; unused by every other stage.</summary>
        public FbPostStage.VkLocs Fb;
        /// <summary>The toon stage's terms, and the aa stage's <c>uRt</c>; unused by every other stage.</summary>
        public ToonStage.VkLocs Toon;
        public int Rt = -1;
    }

    /// <summary>The stack on one device, replaced whole with the device: the stages, the ping-pong pair, the velocity
    /// target, the TAA history and FSR's output.</summary>
    private sealed class StackVkState
    {
        public VkBackend? Dev;
        public readonly StageVk[] Stages = NewStages();
        public ImageId A, B, Vel, Hist0, Hist1, FsrOut, FsrExposure;
        public int W, H, VelW, VelH, HistW, HistH, Cur, FsrW, FsrH;
        /// <summary>The exposure last written into <see cref="FsrExposure"/>; NaN before the first.</summary>
        public float FsrExposureSet = float.NaN;
        public bool HistValid, Up;
        public GuiVkProg? Motion, Taa;
        public bool MotionTried, TaaTried;
        public int MInvVp = -1, MPrevVp = -1, MSize = -1, MJitter = -1;
        public int TBlend = -1, TJitterPx = -1, TUpsample = -1;
        /// <summary>The live stages Vulkan cannot draw yet, as last said; null before the first frame.</summary>
        public string? Missing;
        /// <summary>The stages drawn, one bit per stage, as last said; -1 before the first frame.</summary>
        public int Said = -1;
    }

    private static StackVkState _vk = new();

    private static StageVk[] NewStages()
    {
        var a = new StageVk[_stages.Length];
        for (int i = 0; i < a.Length; i++) a[i] = new StageVk();
        return a;
    }

    /// <summary>The stages Vulkan draws: the plain ones, toon, the taa stage with its own resolve, the aa stage with
    /// its edge and weight passes, and the hosted chain -- its native version included.</summary>
    private static bool OnVulkan(Stage st) => st.Name is "gamma" or "toon" or "sharpen" or "vibrance" or "taa" or "aa"
                                              || st == _fbpost;

    /// <summary>The TAA history on Vulkan starts over: the next resolve takes the current frame whole.</summary>
    internal static void ResetVk() => _vk.HistValid = false;

    /// <summary>The module changed under the hosted chain: its program is spliced from the resource chain, so it
    /// is dropped and rebuilt from the new module's.</summary>
    private static void DropSplicedVk()
    {
        _vk.Stages[Array.IndexOf(_stages, _fbpost)] = new StageVk();
        FbPostStage.DropProgram();
    }

    /// <summary>Is the Vulkan taa stage refused on this device? The frame then draws unjittered.</summary>
    internal static bool TaaVkFailed => (_vk.TaaTried && _vk.Taa == null) || (_vk.MotionTried && _vk.Motion == null);

    /// <summary>The post chain and the stack on Vulkan: the resolve into the stack's first image (or the screen
    /// when no stage is live), each live stage after it, the last into the screen with the dither. The taa stage
    /// paints the camera velocity off <paramref name="depth"/> first. False (logged) when the frame has no way to the
    /// screen.</summary>
    internal static bool RecordVk(VkBackend vk, CommandList cl, ImageId hdr, ImageId depth, Matrix4x4* vp,
                                  Matrix4x4* vpClean, int* viewport, int* display, float near, float far, float fovY,
                                  ExtenderLog log)
    {
        if (_vk.Dev != vk) _vk = new StackVkState { Dev = vk };
        StackVkState s = _vk;
        PostChainVk.BeginFrame();
        if (FbPostStage.Stale(Mask(), Quality())) s.Stages[Array.IndexOf(_stages, _fbpost)] = new StageVk();
        if (!PostChainVk.Ensure(vk, log)) return false;
        int dw = display[2], dh = display[3];
        int* full = stackalloc int[4] { 0, 0, dw, dh };
        bool fsr = FsrVk(vk, s, cl, ref hdr, depth, vp, vpClean, ref viewport, full, near, far, fovY, log);
        int live = 0;
        string missing = "";
        bool taa = false;
        if (Post.Stack.V && _gspI != null)
            for (int i = 0; i < _stages.Length; i++)
            {
                Stage st = _stages[i];
                if (st.Failed || !st.Active()) continue;
                if (!OnVulkan(st)) { missing += (missing.Length == 0 ? "" : ", ") + st.Name; continue; }
                StageVk v = s.Stages[i];
                if (!v.Tried) Build(vk, st, v, log);
                if (v.P == null) continue;
                if (st.Name == "taa" && !TaaReady(vk, s, viewport, dw, dh, vp, vpClean, depth, cl, log)) continue;
                if (st.Name == "aa" && !SmaaStage.ReadyVk(vk, dw, dh, log)) continue;
                taa |= st.Name == "taa";
                live |= 1 << i;
            }
        if (missing != s.Missing)
        {
            s.Missing = missing;
            if (missing.Length > 0)
                log.Always($"PostStack: NOT DRAWN on Vulkan, and switched on -- the {missing} stage(s); the frame resolves without them");
        }
        if (live != s.Said)
        {
            s.Said = live;
            string names = "";
            for (int i = 0; i < _stages.Length; i++)
                if ((live & 1 << i) != 0) names += (names.Length == 0 ? "" : ", ") + _stages[i].Name;
            log.Info($"PostStack: drawn on Vulkan -- {(names.Length == 0 ? "no stage, the resolve alone" : names)}");
        }
        Rect rect = new(display[0], display[1], dw, dh);
        // upsampling, as GL's Begin decides it: the pair and every stage but the last at render size
        s.Up = taa && PostChain.TaaUpsample && (viewport[2] != dw || viewport[3] != dh);
        if (live != 0 && !Pair(vk, s, s.Up ? viewport[2] : dw, s.Up ? viewport[3] : dh, log)) { live = 0; taa = false; _targetBad = true; }
        TaaLive = taa;
        ImageId vel = taa || fsr ? s.Vel : default;
        if (live == 0)
        {
            if (fsr) MotionVectors.RollHistory();
            return PostChainVk.Record(vk, cl, hdr, depth, vk.Screen, true, viewport, display, vel, log);
        }
        int* pair = stackalloc int[4] { 0, 0, s.W, s.H };
        if (!PostChainVk.Record(vk, cl, hdr, depth, s.A, false, viewport, pair, vel, log)) return false;
        ImageId read = s.A, write = s.B;
        int left = System.Numerics.BitOperations.PopCount((uint)live);
        for (int i = 0; i < _stages.Length; i++)
        {
            if ((live & 1 << i) == 0) continue;
            Stage st = _stages[i];
            StageVk v = s.Stages[i];
            bool last = --left == 0;
            ImageId into = last ? vk.Screen : write;
            cl.BeginLabel(st.Label ??= System.Text.Encoding.UTF8.GetBytes("stage " + st.Name));
            ImageId hist = st.Name == "taa" ? TaaResolve(cl, s, read, depth, log) : default;
            bool aa = st.Name == "aa";
            if (aa) SmaaStage.PassesVk(cl, read, log);
            PostChainVk.Open(v.P!);
            LooseBlock.Set(v.Dither, last ? PostChain.DitherLsb : 0f);
            if (st.Value != null) LooseBlock.Set(v.Param, st.Value());
            if (st == _fbpost) FbPostStage.WriteVk(in v.Fb, last ? display : pair);
            if (v.P!.Stage >= 0)
            {
                StageResources.Use(v.P.Stage);
                StageResources.VkUploadFrame(v.P.StageFrame, v.P.Stage, -1, main: true);
            }
            if (st.Name == "toon") ToonStage.WriteVk(in v.Toon);
            if (aa) SmaaStage.WriteVk(v.Rt);
            int at = PostChainVk.Close(cl, v.P!);
            PostChainVk.Draw(cl, v.P!, PostChainVk.FormatOf(into), PostChainVk.Blend.None, into, LoadOp.Clear, last ? rect : new Rect(0, 0, s.W, s.H),
                             at, new PostChainVk.Inputs(read, default, depth, hist, Weights: aa ? SmaaStage.VkWeights : default), log);
            cl.EndLabel();
            (read, write) = (write, read);
        }
        if (taa || fsr) MotionVectors.RollHistory();
        _runs++;
        return true;
    }

    /// <summary>Stage <paramref name="st"/>'s program as GL builds it: the overridable pair, or the embedded text when
    /// the override breaks the stack's display-space contract. A refusal is said once and sits the stage out.</summary>
    private static void Build(VkBackend vk, Stage st, StageVk v, ExtenderLog log)
    {
        v.Tried = true;
        string raw = ShaderStages.Source(st.Resref);
        if (st == _fbpost)
        {
            // the chain spliced as GL's build splices it; an untouched text is no chain to host
            FbPostStage.Arm(Mask(), Quality());
            raw = FbPostStage.Hosting(raw, log);
            string spliced = VanillaShader.Splice(raw, log, "PostStack fbpost");
            if (spliced == raw) return;
            raw = spliced;
        }
        ShaderTexts.Pair(PostChain.ShdPostVs, ShaderStages.Source(PostChain.ShdPostVs), st.Resref, raw,
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
            if (st == _fbpost) v.Fb = FbPostStage.LocateVk();
            if (st.Name == "toon") v.Toon = ToonStage.LocateVk(p);
            v.Rt = p.Loc("uRt");
        }, log, st == _fbpost ? FbPostStage.NativeId : -1);
        if (v.P == null && st == _fbpost && FbPostStage.HostsNative)
        {
            FbPostStage.RefuseNative("its Vulkan post program did not build (logged above)", log);
            v.Tried = false;
            return;
        }
        if (v.P == null)
        {
            st.Failed = true;
            log.Always($"PostStack: the {st.Name} stage did not build on Vulkan (logged above) -- it sits the session out");
        }
    }

    /// <summary>The stack's two half-float images: the display size, or the render size when upsampling.</summary>
    private static bool Pair(VkBackend vk, StackVkState s, int w, int h, ExtenderLog log)
    {
        if (!s.A.IsNull && s.W == w && s.H == h) return true;
        vk.Destroy(s.A); vk.Destroy(s.B);
        s.A = PostChainVk.Target(vk, w, h, "vk post stack 0", alias: AliasLanes.Wide);
        s.B = PostChainVk.Target(vk, w, h, "vk post stack 1", alias: AliasLanes.Second);
        s.W = w; s.H = h;
        if (!s.A.IsNull && !s.B.IsNull) return true;
        log.Always($"PostStack: the {w}x{h} stack pair was REFUSED on Vulkan (logged above) -- the frame resolves without its stages");
        s.A = default;
        return false;
    }

    /// <summary>The taa stage's inputs this frame: its program, the history pair at the display size
    /// (<paramref name="dw"/> x <paramref name="dh"/>), and the camera velocity. False sits the stage out (the frame
    /// resolves without it).</summary>
    private static bool TaaReady(VkBackend vk, StackVkState s, int* viewport, int dw, int dh, Matrix4x4* vp,
                                 Matrix4x4* vpClean, ImageId depth, CommandList cl, ExtenderLog log)
    {
        if (!s.TaaTried)
        {
            s.TaaTried = true;
            s.Taa = PostChainVk.Program(vk, PostChain.FullscreenVs, TaaStage.TaaFs, "TAA resolve", p =>
            {
                s.TBlend = p.Loc("uBlend"); s.TJitterPx = p.Loc("uJitterPx"); s.TUpsample = p.Loc("uUpsample");
            }, log);
            if (s.Taa == null)
                log.Always("PostStack: the Vulkan TAA resolve did not build (logged above) -- the taa stage sits the session "
                         + "out and the frame draws unjittered");
        }
        if (s.Taa == null) return false;
        if (s.Hist0.IsNull || s.HistW != dw || s.HistH != dh)
        {
            vk.Destroy(s.Hist0); vk.Destroy(s.Hist1);
            s.Hist0 = PostChainVk.Target(vk, dw, dh, "vk taa history 0");
            s.Hist1 = PostChainVk.Target(vk, dw, dh, "vk taa history 1");
            s.HistW = dw; s.HistH = dh;
            s.HistValid = false;
            if (s.Hist0.IsNull || s.Hist1.IsNull) { s.Hist0 = default; return false; }
        }
        return VelocityReady(vk, s, viewport, vp, vpClean, depth, cl, log);
    }

    /// <summary>The camera velocity, painted off the finished depth into the render-size velocity target, with the
    /// movers and the grass over it: what TAA and FSR both reproject through. False when its pass did not build.</summary>
    private static bool VelocityReady(VkBackend vk, StackVkState s, int* viewport, Matrix4x4* vp, Matrix4x4* vpClean,
                                      ImageId depth, CommandList cl, ExtenderLog log)
    {
        if (!s.MotionTried)
        {
            s.MotionTried = true;
            s.Motion = PostChainVk.Program(vk, PostChain.FullscreenVs, MotionVectors.CameraFs, "camera velocity", p =>
            {
                s.MInvVp = p.Loc("uInvVP"); s.MPrevVp = p.Loc("uPrevVP"); s.MSize = p.Loc("uSize"); s.MJitter = p.Loc("uJitter");
            }, log);
            if (s.Motion == null)
                log.Always("PostStack: the Vulkan velocity pass did not build (logged above) -- TAA and FSR sit the session out "
                         + "and the frame draws unjittered");
        }
        if (s.Motion == null) return false;
        int w = viewport[2], h = viewport[3];
        if (s.Vel.IsNull || s.VelW != w || s.VelH != h)
        {
            vk.Destroy(s.Vel);
            s.Vel = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg16F, w, h, 1, 1, 1, 1,
                                                 ImageUse.Sampled | ImageUse.ColorTarget, "vk velocity", AliasLanes.Record));
            s.VelW = w; s.VelH = h;
            if (s.Vel.IsNull) return false;
        }
        if (!Matrix4x4.Invert(*vp, out Matrix4x4 inv)) return false;
        Matrix4x4 prev = MotionVectors.NoteVk(vpClean, AlloyRenderer.FrameSeq);
        cl.BeginLabel("velocity"u8);
        PostChainVk.Open(s.Motion);
        LooseBlock.Set(s.MInvVp, inv);
        LooseBlock.Set(s.MPrevVp, prev);
        LooseBlock.Set(s.MSize, [(float)w, h]);
        LooseBlock.Set(s.MJitter, [PostChain.JitterX, PostChain.JitterY]);
        int at = PostChainVk.Close(cl, s.Motion);
        PostChainVk.Draw(cl, s.Motion, Format.Rg16F, PostChainVk.Blend.None, s.Vel, LoadOp.DontCare, new Rect(0, 0, w, h), at,
                         new PostChainVk.Inputs(default, default, depth), log);
        AlloyRenderer.VkMoverMotion(vk, cl, s.Vel, w, h, vp, prev, log);
        AlloyRenderer.VkGrassMotion(vk, cl, s.Vel, w, h, vp, prev);
        cl.EndLabel();
        return true;
    }

    /// <summary>FSR 3.1 on this frame's full-window perspective view: the velocity, then the upscale into a display-size
    /// image that becomes <paramref name="hdr"/>, with <paramref name="viewport"/> the whole display, so the resolve and
    /// the stack run at the display size. A refusal stands FSR down for the session. True when FSR drew.</summary>
    private static bool FsrVk(VkBackend vk, StackVkState s, CommandList cl, ref ImageId hdr, ImageId depth, Matrix4x4* vp,
                              Matrix4x4* vpClean, ref int* viewport, int* full, float near, float far, float fovY, ExtenderLog log)
    {
        if (!Fsr.On) { Fsr.NoteScale(log); return false; }
        int rw = viewport[2], rh = viewport[3], dw = full[2], dh = full[3];
        // ponytail: one context and one history, so only the full-window world view; a second or orthographic view
        // draws unsmoothed. Per-view contexts if those views ever matter.
        if (fovY <= 0f || vk.FrameSize != (dw, dh)) return false;
        if (vk.FsrReady(rw, rh, dw, dh) is string why) { Fsr.Refuse(why, log); return false; }
        if (s.FsrOut.IsNull || s.FsrW != dw || s.FsrH != dh)
        {
            vk.Destroy(s.FsrOut);
            s.FsrOut = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, dw, dh, 1, 1, 1, 1,
                                                    ImageUse.Sampled | ImageUse.Storage, "vk fsr output"));
            s.FsrW = dw; s.FsrH = dh;
            if (s.FsrOut.IsNull) { Fsr.Refuse($"its {dw}x{dh} output image was refused (logged above)", log); return false; }
        }
        if (s.FsrExposure.IsNull)
        {
            s.FsrExposure = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, 1, 1, 1, 1, 1, 1,
                                                         ImageUse.Sampled | ImageUse.CopyDst, "vk fsr exposure"));
            if (s.FsrExposure.IsNull) { Fsr.Refuse("its exposure image was refused (logged above)", log); return false; }
        }
        float exposure = Post.Exposure.V;
        if (exposure != s.FsrExposureSet && vk.Upload(s.FsrExposure, 0, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(in exposure))))
            s.FsrExposureSet = exposure;
        if (!VelocityReady(vk, s, viewport, vp, vpClean, depth, cl, log)) { Fsr.Refuse("the velocity pass did not build", log); return false; }
        Fsr.Note(rw, rh, dw, dh, log);
        cl.BeginLabel("fsr"u8);
        cl.Upscale(Fsr.Command(hdr, depth, s.Vel, s.FsrExposure, s.FsrOut, rw, rh, PostChain.JitterX, PostChain.JitterY,
                               Fsr.FrameMs(), near, far, fovY, Fsr.TakeReset()));
        cl.EndLabel();
        hdr = s.FsrOut;
        viewport = full;
        return true;
    }

    /// <summary>The accumulator: this frame against the reprojected, clipped history into the other history image,
    /// which the stage draw then copies out with the sharpen. Its image.</summary>
    private static ImageId TaaResolve(CommandList cl, StackVkState s, ImageId cur, ImageId depth, ExtenderLog log)
    {
        ImageId read = s.Cur == 0 ? s.Hist0 : s.Hist1, write = s.Cur == 0 ? s.Hist1 : s.Hist0;
        cl.BeginLabel("taa accumulate"u8);
        PostChainVk.Open(s.Taa!);
        LooseBlock.Set(s.TBlend, s.HistValid ? PostChain.TaaBlend : 1f);
        LooseBlock.Set(s.TUpsample, s.Up ? 1 : 0);
        LooseBlock.Set(s.TJitterPx, [PostChain.JitterX * 0.5f * s.W, PostChain.JitterY * 0.5f * s.H]);
        int at = PostChainVk.Close(cl, s.Taa!);
        PostChainVk.Draw(cl, s.Taa!, Format.Rgba16F, PostChainVk.Blend.None, write, LoadOp.DontCare, new Rect(0, 0, s.HistW, s.HistH), at,
                         new PostChainVk.Inputs(cur, default, depth, read, s.Vel, HistLinear: true), log);
        cl.EndLabel();
        s.Cur ^= 1;
        s.HistValid = true;
        return write;
    }
}
