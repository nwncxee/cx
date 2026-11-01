using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// FSR 3.1 temporal upscaling on the Vulkan frame: the world renders at <c>alloy.scale</c>, FSR rebuilds it at the
/// display size before the resolve, and the resolve and the post stack then run at the display size. It replaces
/// TAA and SMAA while it draws.
/// </summary>
internal static partial class Fsr
{
    /// <summary>What FSR holds across frames: the session's refusal, a pending history reset and the last size said. Replaced whole on context loss.</summary>
    private sealed class FsrState
    {
        public string? Refused;
        public bool Reset = true, ScaleSaid;
        public (int, int, int, int) Said;
        public (int RenderW, int DisplayW, int Count) Phases;
    }

    private static FsrState _s = new();

    /// <summary>FSR draws the world this frame: <c>post.fsr</c> is on, the render scale is at most 1, and nothing
    /// refused it.</summary>
    internal static bool On => Post.Fsr.V && _s.Refused == null && Renderer.Scale.V <= 1f;

    /// <summary>FSR's jitter sequence length for a <paramref name="renderW"/> frame on a <paramref name="displayW"/>
    /// display, as its library counts it, asked once per size.</summary>
    internal static int PhaseCount(int renderW, int displayW)
    {
        if (_s.Phases.RenderW != renderW || _s.Phases.DisplayW != displayW)
            _s.Phases = (renderW, displayW, Math.Max(VkBackend.FsrPhaseCount(renderW, displayW), 1));
        return _s.Phases.Count;
    }

    /// <summary>The cx.log line naming an upscale from <paramref name="rw"/> x <paramref name="rh"/> to
    /// <paramref name="dw"/> x <paramref name="dh"/>.</summary>
    internal static string Line(int rw, int rh, int dw, int dh) => $"PostStack: FSR 3.1 upscales the world from {rw}x{rh} to {dw}x{dh}";

    /// <summary>Say the upscale once per size, and once that a render scale above 1 leaves FSR off.</summary>
    internal static void Note(int rw, int rh, int dw, int dh, ExtenderLog log)
    {
        if (_s.Said == (rw, rh, dw, dh)) return;
        _s.Said = (rw, rh, dw, dh);
        log.Always(Line(rw, rh, dw, dh));
    }

    /// <summary>The <c>post.fsr</c> knob is on and a render scale above 1 keeps FSR off: said once a session.</summary>
    internal static void NoteScale(ExtenderLog log)
    {
        if (!Post.Fsr.V || Renderer.Scale.V <= 1f || _s.ScaleSaid) return;
        _s.ScaleSaid = true;
        log.Always($"PostStack: FSR 3.1 is on but the render scale is {Renderer.Scale.V:F2}; FSR only upscales, so TAA draws while it is above 1");
    }

    /// <summary>Stand FSR down for the session: <paramref name="why"/> said once, on cx.log and on the panel, and
    /// the frame goes back to TAA.</summary>
    internal static void Refuse(string why, ExtenderLog log)
    {
        if (_s.Refused != null) return;
        _s.Refused = why;
        log.Always($"PostStack: FSR 3.1 is OFF for the session -- {why}. The frame goes back to TAA.");
        TaaStage.Reset();
    }

    /// <summary>The <c>post</c> panel's line: why FSR is off for the session, or empty.</summary>
    internal static string PanelLine() => _s.Refused == null ? "" : $"FSR 3.1 is off for this session: {_s.Refused}.";

    /// <summary>True once after the world edge: the next dispatch starts FSR's history over.</summary>
    internal static bool TakeReset()
    {
        bool r = _s.Reset;
        _s.Reset = false;
        return r;
    }

    /// <summary>World edge, or a camera cut: the next dispatch starts FSR's history over.</summary>
    internal static void Revoke() => _s.Reset = true;

    /// <summary>Context loss: the session's refusal and history start over.</summary>
    internal static void Forget() => _s = new FsrState();

    /// <summary>The game's frame delta in milliseconds, as FSR takes it: never past 100, and 16.7 while the engine's clock is unresolved.</summary>
    internal static float FrameMs() => FrameDelta.Ready ? MathF.Min(FrameDelta.Delta(FrameDelta.DtFloor) * 1000f, 100f) : 16.7f;

    /// <summary>One dispatch in FSR's terms from Alloy's. Alloy's targets hold rows bottom-up, the way NDC y runs, so
    /// a jitter of j NDC is j x size / 2 render pixels along the rows with no flip, and the velocity target (render
    /// pixels, <c>prev - cur</c>, the same rows) is FSR's motion as it stands.</summary>
    internal static UpscaleCmd Command(ImageId color, ImageId depth, ImageId motion, ImageId exposure, ImageId output,
                                       int rw, int rh, float jitterNdcX, float jitterNdcY, float frameMs,
                                       float near, float far, float fovY, bool reset)
        => new(color, depth, motion, exposure, output, jitterNdcX * rw * 0.5f, jitterNdcY * rh * 0.5f, 1f, 1f, rw, rh,
               frameMs, near, far, fovY, reset);

    /// <summary>Off-device: the approved examples, and FSR off leaving the frame as it was.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "Fsr.SelfCheck");
        bool fsr = Post.Fsr.V, taa = Post.Taa.V;
        float scale = Renderer.Scale.V;
        int skipped = Checker.Skipped;
        try
        {
            OffSelfCheck(check);
            // the examples log a test refusal and read cx.log back: run-demo's own log, never a player's
            if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) check.Skip("in a session: the FSR examples run in run-demo");
            else Examples(check, log);
        }
        finally { Post.Fsr.V = fsr; Post.Taa.V = taa; Renderer.Scale.V = scale; }
        log.Info(!check.Ok ? "Fsr.SelfCheck: FAILED" : Checker.Skipped > skipped ? "Fsr.SelfCheck: ran the rest (a skip above)" : "Fsr.SelfCheck: passed");
    }

    /// <summary>The approved examples.</summary>
    static partial void Examples(Checker check, ExtenderLog log);

    /// <summary>E3, a regression: FSR off draws the frame as before -- TAA, its 8-frame jitter, no FSR.</summary>
    private static void OffSelfCheck(Checker check)
    {
        Post.Fsr.V = false; Post.Taa.V = true; Renderer.Scale.V = 0.67f;
        check.Expect(!On, "E3: FSR off, FSR does not draw");
        check.Expect(PostChain.TaaOn, "E3: FSR off, TAA runs in the post stack");
        check.Expect(PostChain.JitterPhases(2560) == 8, $"E3: FSR off, the jitter repeats after 8 (got {PostChain.JitterPhases(2560)})");
    }
}
