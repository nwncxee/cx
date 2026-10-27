using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Object motion vectors for matrix movers. After <see cref="MotionVectors"/> has painted the camera
/// field, every opaque record whose model matrix changed THIS frame is drawn once more into the velocity target,
/// LEQUAL against the depth it already wrote, with the difference of its current and previous clip positions;
/// a posed skinmesh uses both frames' palettes, a dangly its pre-solve positions. Blended classes stay camera-only.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Draw indices of this pass's movers, serial-appended by the staging walk. Sized to every draw:
    /// a crowd moves thousands of parts a frame, and a cap would hand the ones past it camera-only vectors --
    /// exactly the ghosting this pass exists to remove.</summary>
    private static readonly int[] _mvDraw = new int[MaxOwned];
    private static int _mvN, _mvDrawn;
    /// <summary>DB-mode record rewrites since the last period line: with the model moving (past the record's
    /// 0,1 mm step) and without (alpha, lighting, reveal, prev catch-up). Bumped by the stage workers.</summary>
    private static long _dbRecModel, _dbRecOther;
    private static bool _mvSkinned, _mvDangly;

    /// <summary>Dangly positions from before this frame's solve, part-local xyz, appended per owned part by
    /// <see cref="NoteDanglyPrev"/> and reset by the first append of a new frame. Native and growing for
    /// <c>EnsureBoneSlots</c>' reason.</summary>
    private static float* _dpStage;
    private static int _dpUsed, _dpCap, _dpSeq = -1;
    /// <summary>Per ownership slot: the frame its snapshot is from, and where in <see cref="_dpStage"/> and
    /// how many vertices.</summary>
    private static readonly int[] _oDpAt = new int[MaxOwned], _oDpBase = new int[MaxOwned], _oDpN = new int[MaxOwned];
    /// <summary>Last frame's dangly vertices, read by the mover sweep alone.</summary>
    internal const uint BindDanglyPrev = SsboSlots.DanglyPrev;

    /// <summary>The dangly solve's hand-off, called before it overwrites <paramref name="dst"/>: keep the
    /// outgoing positions of an OWNED part for the mover sweep. Main thread, per dangly part per frame;
    /// allocates only when the stage grows, and a refused grow drops the part to its rigid vectors.</summary>
    internal static void NoteDanglyPrev(int part, float* dst, int n)
    {
        // Unarmed, _frameSeq stands still and the stage would never reset.
        int i = _armed ? SlotOfPart(part) : -1;
        if (i < 0) return;
        if (_dpSeq != _frameSeq) { _dpSeq = _frameSeq; _dpUsed = 0; }
        int need = _dpUsed + n * 3;
        if (need > _dpCap)
        {
            int cap = Math.Max(_dpCap * 2, Math.Max(need, 1 << 16));
            float* next;
            try { next = (float*)NativeMemory.Realloc(_dpStage, (nuint)cap * sizeof(float)); }
            catch (OutOfMemoryException) { return; }
            if (next == null) return;
            _dpStage = next; _dpCap = cap;
        }
        new ReadOnlySpan<float>(dst, n * 3).CopyTo(new Span<float>(_dpStage, _dpCap).Slice(_dpUsed, n * 3));
        _oDpAt[i] = _frameSeq; _oDpBase[i] = _dpUsed; _oDpN[i] = n;
        _dpUsed = need;
    }

    /// <summary>Was slot <paramref name="i"/>'s dangly solve snapshotted this frame?</summary>
    private static bool DanglyPrevNow(int i) => _oDpAt[i] == _frameSeq && _dpSeq == _frameSeq;

    /// <summary>A camera CUT -- the camera layer snapping after a stand-down gate lifts, or a mode switch.
    /// Drops the reprojection history so neither the motion vectors nor TAA carry one frame of the wrong
    /// camera. Flag writes only; safe from any point of the frame.</summary>
    public static void CameraCut() { MotionVectors.Revoke(); TaaStage.Reset(); }

    private static void NoteMoverDraw(int n, bool skinned, bool dangly)
    {
        _mvDraw[_mvN++] = n;
        _mvSkinned |= skinned;
        _mvDangly |= dangly;
    }

    /// <summary>The [alloy] period term for the sweep; resets the counters.</summary>
    internal static string MoverStats(int frames)
    {
        int d = _mvDrawn; _mvDrawn = 0;
        long rm = System.Threading.Interlocked.Exchange(ref _dbRecModel, 0), ro = System.Threading.Interlocked.Exchange(ref _dbRecOther, 0);
        float f = MathF.Max(frames, 1);
        string db = rm + ro == 0 ? "" : $", db records rewritten {rm / f:F1}/frame moved + {ro / f:F1}/frame other" + RecWhyStats(frames);
        return (d == 0 ? "" : $", {d / f:F1} mover draws/frame") + db;
    }

    /// <summary>Context loss: the frame's queued movers describe a world that is gone.</summary>
    private static void MotionContextLost() { _mvN = 0; _mvSkinned = false; _mvDangly = false; }

    /// <summary>The mover vertex stage. <c>gl_Position</c> is the prepass's exact <see cref="ClipExpr"/> text
    /// under <c>invariant</c>, so LEQUAL against the depth the mover already wrote passes on every fragment;
    /// the previous clip position rides through the record's <c>prev</c> and last frame's view-projection, and
    /// for a posed skinmesh through last frame's palette at <see cref="BindBonesPrev"/>.</summary>
    internal static string MotionVs => ShaderSource.Get("cx_motion_v.glsl");

    /// <summary>The same convention as <see cref="MotionVectors.CameraFs"/>: prev minus current, render pixels,
    /// the current position unjittered, bounded and never NaN.</summary>
    internal static string MotionFs => ShaderSource.Get("cx_motion_f.glsl");
}
