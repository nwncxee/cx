using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The camera history every velocity pass reprojects through, and the convention its stages share: once per frame
/// the previous frame's unjittered view-projection is handed out and this frame's queued, so a pixel whose model
/// matrix did not move is exact and the mover, skinmesh and dangly sweeps correct the rest. Convention:
/// <c>prev - cur</c>, in RENDER pixels, y-up, UNJITTERED, so a still camera under TAA paints exactly zero.
/// </summary>
internal static unsafe class MotionVectors
{

    private static ExtenderLog? _log;

    /// <summary>The camera history, which the WORLD owns: an area transition replaces it, so the next frame
    /// paints zero rather than a jump the length of the load screen. Context loss replaces it as well -- the
    /// target the history was reprojected against is gone.</summary>
    private sealed class MotionHistoryState
    {
        /// <summary>Last frame's UNJITTERED view-projection, and whether one has been taken at all.</summary>
        public Matrix4x4 PrevVp;
        public bool HasPrev;
        public int LastSeq = -1;
        /// <summary>This frame's clean camera, waiting for the frame's consumers: adopted once, by
        /// <see cref="RollHistory"/>, so a second armed pass in the same frame cannot roll it again.</summary>
        public Matrix4x4 PendingPrev;
        public int PendingSeq = -1;
        /// <summary>The previous camera THIS frame's field was reprojected against, held past
        /// <see cref="RollHistory"/> for the late sweep that corrects the field over grass and particles.</summary>
        public Matrix4x4 FieldPrevVp;
        /// <summary>This frame's world clock, kept so the next frame has one to be the previous of.</summary>
        public int Clock;
        /// <summary>The world clock of the frame <see cref="FieldPrevVp"/> came from: what a late sweep
        /// evaluates the analytic sway at to put last frame's blade back.</summary>
        public int FieldPrevClock;
        public bool HasClock;
        /// <summary>The frame both of the two above were taken on. A second armed pass in one frame must not
        /// re-take either: by then <see cref="RollHistory"/> has advanced the camera to this frame's.</summary>
        public int ClockSeq = -1;
    }

    /// <summary>The wrap <c>CxClock</c> applies to the world clock, mirrored here because the previous clock
    /// rides over as the same int: across the wrap last frame's number is the larger one.</summary>
    private const int ClockWrap = 3600000;

    private static MotionHistoryState _hist = new();
    private static int _runs;
    /// <summary>The clean camera of the last frame's first run, for the period's camera-steadiness term: how many
    /// frames the view-projection changed at all, and the largest element change.</summary>
    private static Matrix4x4 _camLast;
    private static int _camSeq = -1, _camMoved, _camStill;
    private static float _camMax;

    /// <inheritdoc cref="MotionHistoryState.FieldPrevVp"/>
    internal static Matrix4x4 FieldPrevVp => _hist.FieldPrevVp;
    /// <inheritdoc cref="MotionHistoryState.FieldPrevClock"/>
    internal static int FieldPrevClock => _hist.FieldPrevClock;
    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>World edge (area transition): the previous camera is another area's. The next frame paints
    /// zero rather than a jump the length of the load screen.</summary>
    internal static void Revoke() => _hist = new MotionHistoryState();

    /// <summary>The camera history for the Vulkan frame's own velocity pass: this frame
    /// queued for <see cref="RollHistory"/>, and last frame's camera returned.</summary>
    internal static Matrix4x4 NoteVk(Matrix4x4* vpClean, int frameSeq)
    {
        if (!_hist.HasPrev) { _hist.PrevVp = *vpClean; _hist.HasPrev = true; }
        NoteField(AlloyRenderer.TimeOfDay, frameSeq);
        if (frameSeq != _camSeq) NoteCamera(vpClean, frameSeq);
        _hist.PendingPrev = *vpClean; _hist.PendingSeq = frameSeq;
        _runs++;
        return _hist.PrevVp;
    }

    /// <summary>Adopt this frame's clean view-projection as the next frame's history, once per frame.</summary>
    internal static void RollHistory()
    {
        if (_hist.PendingSeq >= 0 && _hist.PendingSeq != _hist.LastSeq) { _hist.PrevVp = _hist.PendingPrev; _hist.LastSeq = _hist.PendingSeq; }
        _hist.PendingSeq = -1;
    }

    /// <summary>Adopt the camera and the world clock this frame's field is reprojected against, ONCE per
    /// frame, so a reconstructed previous position and the camera it goes through are one frame's -- a second
    /// armed pass must re-take neither, <see cref="RollHistory"/> having advanced the camera by then. A reset
    /// frame, and the frame the clock wraps on, take a zero step rather than a whole sway period.</summary>
    internal static void NoteField(int clock, int frameSeq)
    {
        if (frameSeq == _hist.ClockSeq) return;
        _hist.ClockSeq = frameSeq;
        _hist.FieldPrevVp = _hist.PrevVp;
        int prev = _hist.HasClock ? _hist.Clock : clock;
        if (clock % ClockWrap < prev % ClockWrap) prev = clock;
        _hist.FieldPrevClock = prev;
        _hist.Clock = clock;
        _hist.HasClock = true;
    }

    private static void NoteCamera(Matrix4x4* vpClean, int frameSeq)
    {
        if (_camSeq >= 0)
        {
            float m = 0f;
            float* a = (float*)vpClean;
            fixed (Matrix4x4* pb = &_camLast) { float* b = (float*)pb; for (int k = 0; k < 16; k++) m = MathF.Max(m, MathF.Abs(a[k] - b[k])); }
            if (m > 1e-6f) { _camMoved++; _camMax = MathF.Max(_camMax, m); } else _camStill++;
        }
        _camLast = *vpClean; _camSeq = frameSeq;
    }

    internal static string Stats(int frames)
    {
        int n = _runs; _runs = 0;
        string cam = $", camera moved {_camMoved}/{_camMoved + _camStill} frames (max element step {_camMax:G3})";
        _camMoved = 0; _camStill = 0; _camMax = 0f;
        return n == 0 ? "motion idle" : $"motion {(float)n / MathF.Max(frames, 1):F2} runs/frame" + cam + AlloyRenderer.MoverStats(frames);
    }

    /// <summary>Depth to NDC (default depth range, so z = 2d-1), through the inverse of this frame's
    /// view-projection and forward through the previous one. The world point stays homogeneous -- the
    /// previous clip w absorbs whatever w the inverse left -- and one divide gives the previous NDC.</summary>
    internal static string CameraFs => ShaderSource.Get("cx_motioncam_f.glsl");

    /// <summary>The convention as a LIBRARY, for the stages that correct the field while they draw their own
    /// class -- grass and particles. One text, so a late writer cannot disagree with the two stages above.</summary>
    internal static string MotionOut => ShaderSource.Get("cx_motionout.glsl");

    /// <summary>Off-device: the stage text carries its convention and its bounds, and the history edge
    /// resets.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(CameraFs.Contains("prev.xy / prev.w - (ndc.xy - uJitter)"), "motion is previous minus current (FSR2's convention), the current unjittered");
        check.Expect(AlloyRenderer.MotionFs.Contains("vPrev.xy / vPrev.w - (vCur.xy / vCur.w - uJitter)") && AlloyRenderer.MotionVs.Contains(AlloyRenderer.ClipExpr)
          && AlloyRenderer.MotionVs.Contains("invariant gl_Position;") && AlloyRenderer.MotionVs.Contains("uPrevVP * d.prev * vec4(p, 1.0)"),
          "the mover sweep (#392) shares the convention, and its clip position is the prepass's exact text");
        check.Expect(AlloyRenderer.MotionVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * pos;")
          && AlloyRenderer.MotionVs.Contains($"binding = {AlloyRenderer.BindBonesPrev})")
          && AlloyRenderer.MotionVs.Contains("uPrevVP * d.prev * (CxPrevSkinMat(bb) * vec4(aPos, 1.0))"),
          "a skinned mover (CX-26) takes the prepass's skinned clip text and last frame's palette");
        check.Expect(AlloyRenderer.MotionVs.Contains($"binding = {AlloyRenderer.BindDanglyPrev})")
          && AlloyRenderer.MotionVs.Contains("int k = gl_VertexID - gl_BaseVertex;") && AlloyRenderer.MotionVs.Contains("k < uDangly.y"),
          "a dangly mover (CX-26) reads its own vertex's pre-solve position, bounded by the snapshot");
        check.Expect(ShaderContract.RecordSafe(AlloyRenderer.MotionVs),
          "the mover stage's CxDraw declaration does not add up to the record stride C# stages");
        _hist.PendingPrev = Matrix4x4.CreateTranslation(1f, 0f, 0f); _hist.PendingSeq = 3; _hist.LastSeq = 2;
        RollHistory();
        check.Expect(_hist.PrevVp.M41 == 1f && _hist.LastSeq == 3, "the history rolls once the frame's consumers are done");
        _hist.PendingPrev = Matrix4x4.CreateTranslation(2f, 0f, 0f); _hist.PendingSeq = 3;
        RollHistory();
        check.Expect(_hist.PrevVp.M41 == 1f, "...and a second pass in the same frame does not roll it again");
        check.Expect(CameraFs.Contains("abs(prev.w) < 1e-6") && CameraFs.Contains("isnan(v)") && CameraFs.Contains("clamp(v, -uSize, uSize)"),
          "no NaN and no unbounded value leaves the stage");
        check.Expect(CameraFs.Contains("d * 2.0 - 1.0"), "depth decodes for the default depth range");
        _hist.HasPrev = true; _hist.LastSeq = 7;
        Revoke();
        check.Expect(!_hist.HasPrev && _hist.LastSeq == -1, "Revoke drops the previous camera");
        // CX-48: the field's previous frame, off a hand-written sequence of world clocks and cameras. Every
        // expected value here is a literal from that sequence, not a second computation of the roll.
        _hist.PrevVp = Matrix4x4.CreateTranslation(1f, 0f, 0f);
        NoteField(1000, 1);
        check.Expect(FieldPrevClock == 1000, "the first frame's previous clock is its own: a zero sway step, not a jump from zero");
        check.Expect(FieldPrevVp.M41 == 1f, "...and the camera the field is reprojected against is the history's");
        _hist.PrevVp = Matrix4x4.CreateTranslation(2f, 0f, 0f);
        NoteField(1040, 2);
        check.Expect(FieldPrevClock == 1000 && FieldPrevVp.M41 == 2f, "...then the previous FRAME's clock, and the camera beside it");
        // The roll has by now advanced the camera, which is exactly what a second pass must not pick up.
        _hist.PrevVp = Matrix4x4.CreateTranslation(9f, 0f, 0f);
        NoteField(1080, 2);
        check.Expect(FieldPrevClock == 1000 && FieldPrevVp.M41 == 2f,
          "...and a second armed pass inside one frame re-takes neither the clock nor the camera");
        NoteField(1080, 3);
        check.Expect(FieldPrevClock == 1040 && FieldPrevVp.M41 == 9f, "...the next frame takes both again");
        NoteField(3600040, 4);
        check.Expect(FieldPrevClock == 3600040, "the hourly wrap takes a zero step rather than a whole sway period");
        Revoke();
        NoteField(5000, 5);
        check.Expect(FieldPrevClock == 5000, "an area transition takes a zero step: the grass of another area is not this blade's history");
        check.Expect(MotionOut.Contains("(prev.xy / prev.w - (cur.xy / cur.w - uJitter)) * 0.5 * uSize")
          && MotionOut.Contains("isnan(v)") && MotionOut.Contains("clamp(v, -uSize, uSize)"),
          "the library the late writers share carries the two fullscreen stages' convention and their bounds");
        check.Expect((int)PassId.GrassOwned < (int)PassId.PostProcess && (int)PassId.ParticlesOwned < (int)PassId.PostProcess,
          "grass and particles draw before the one site that corrects the field, so the sweep there sees this frame's own");
        log.Info(check.Ok ? "MotionVectors.SelfCheck: passed" : "MotionVectors.SelfCheck: FAILED");
        return check.Ok;
    }
}
