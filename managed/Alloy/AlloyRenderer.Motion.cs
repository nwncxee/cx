using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Object motion vectors for movers. After <see cref="MotionVectors"/> has painted the camera field, every
/// opaque or cutout record whose model or pose changed THIS frame is drawn once more into the velocity target against
/// the depth it already wrote, with its current and previous clip positions; a posed skinmesh uses both frames'
/// palettes, a dangly its pre-solve positions. Blended classes stay camera-only.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>This pass's movers, serial-appended by the staging walk. Sized to every draw: a crowd moves
    /// thousands of parts a frame, and a cap would hand the ones past it camera-only vectors -- exactly the
    /// ghosting this pass exists to remove.</summary>
    private static readonly MoverDraw[] _mvDraw = new MoverDraw[MaxOwned];
    private static int _mvN, _mvDrawn;
    /// <summary>DB-mode record rewrites since the last period line: with the model moving (past the record's
    /// 0,1 mm step) and without (alpha, lighting, reveal, prev catch-up). Bumped by the stage workers.</summary>
    private static long _dbRecModel, _dbRecOther;
    private static bool _mvSkinned, _mvDangly;

    /// <summary>One mover: the draw the sweep redraws, and the depth test it redraws under.</summary>
    private struct MoverDraw { internal int Draw; internal CompareOp Test; }

    /// <summary>Dangly positions from before this frame's solve, part-local xyz, appended per owned part by
    /// <see cref="NoteDanglyPrev"/> and reset by the first append of a new frame. Native and growing for
    /// <c>EnsureBoneSlots</c>' reason.</summary>
    private static NativeBuffer<float> _dpStage;
    private static int _dpUsed, _dpSeq = -1;
    /// <summary>Per ownership slot: the frame its snapshot is from, and where in <see cref="_dpStage"/> and
    /// how many vertices.</summary>
    private static readonly int[] _oDpAt = new int[MaxOwned], _oDpBase = new int[MaxOwned], _oDpN = new int[MaxOwned];
    /// <summary>Last frame's dangly vertices, read by the mover sweep alone.</summary>
    internal const uint BindDanglyPrev = SsboSlots.DanglyPrev;

    /// <summary>The dangly solve's hand-off, called before it overwrites <paramref name="outgoing"/>: keep the
    /// outgoing positions of an OWNED part for the mover sweep. Main thread, per dangly part per frame;
    /// allocates only when the stage grows, and a refused grow drops the part to its rigid vectors.</summary>
    internal static void NoteDanglyPrev(int part, ReadOnlySpan<Vector3> outgoing)
    {
        // Unarmed, _frameSeq stands still and the stage would never reset.
        int i = _armed ? SlotOfPart(part) : -1;
        if (i < 0) return;
        if (_dpSeq != _frameSeq) { _dpSeq = _frameSeq; _dpUsed = 0; }
        int need = _dpUsed + outgoing.Length * 3;
        if (need > _dpStage.Length
            && !_dpStage.Grow(Math.Max(_dpStage.Length * 2, Math.Max(need, 1 << 16)))) return;
        MemoryMarshal.Cast<Vector3, float>(outgoing).CopyTo(_dpStage.Span.Slice(_dpUsed, outgoing.Length * 3));
        _oDpAt[i] = _frameSeq; _oDpBase[i] = _dpUsed; _oDpN[i] = outgoing.Length;
        _dpUsed = need;
    }

    /// <summary>Was slot <paramref name="i"/>'s dangly solve snapshotted this frame?</summary>
    private static bool DanglyPrevNow(int i) => _oDpAt[i] == _frameSeq && _dpSeq == _frameSeq;

    /// <summary>A camera CUT -- the camera layer snapping after a stand-down gate lifts, or a mode switch.
    /// Drops every reprojection history, so none of the motion vectors, TAA, the reflections or the occlusion
    /// accumulator carries one frame of the wrong camera. Flag writes only; safe from any point of the frame.</summary>
    public static void CameraCut()
    {
        MotionVectors.Revoke(); TaaStage.Reset(); Fsr.Revoke(); VkSsrRevoke(); SsaoPassVk.Revoke();
    }

    /// <summary>Did a skin's pose change since last frame? Bit for bit: a held pose re-solves to the same matrices.</summary>
    private static bool PaletteMoved(in BonePalette now, in BonePalette prev)
    {
        ReadOnlySpan<Matrix4x4> a = now, b = prev;
        return !MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));
    }

    /// <summary>The two motion levels of one model change: the shadow re-bake's coarse step and the velocity
    /// sweep's fine one.</summary>
    private static (bool Caster, bool Mover) MoveLevels(in Matrix4x4 was, in Matrix4x4 now)
        => (Moved(in was, in now, CasterStep), Moved(in was, in now, MoveStep));

    /// <summary>Is slot <paramref name="i"/> a mover this frame: its model moved at the sweep's step, a posed skin's
    /// palette changed, or a dangly part was solved.</summary>
    private static bool SlotMoverNow(int i, bool posed, bool dangly)
    {
        bool mv = _mvMovedAt[i] == _frameSeq;
        return posed ? mv || PoseMovedNow(_oBone[i]) : dangly || mv && _oMx[i];
    }

    /// <summary>Did the palette at bone base <paramref name="bone"/> change between the two staged frames?</summary>
    private static bool PoseMovedNow(int bone)
    {
        int s = bone / BoneCount;
        return s < _boneSlots && PaletteMoved(in _boneStage.Span[s], in _bonePrevStage.Span[s]);
    }

    /// <summary>Does a moved draw join the sweep: it writes depth, and no content vertex half of its own
    /// (<paramref name="ownVertex"/>) places it where the sweep's stock skinning would not.</summary>
    private static bool MoverJoins(int cls, bool ownDepth, bool ownVertex)
        => (cls == (int)AlloyClass.Opaque || cls == (int)AlloyClass.Cutout) && !(ownDepth && ownVertex);

    /// <summary>The sweep's depth test for a mover of class <paramref name="cls"/> whose content pair may cut its own
    /// coverage (<paramref name="ownCoverage"/>): EQUAL for anything with holes, so they keep the camera field.</summary>
    private static CompareOp MoverDepthOf(int cls, bool ownCoverage)
        => cls == (int)AlloyClass.Cutout || ownCoverage ? CompareOp.Equal : WorldDepth.NearerOrEqual;

    /// <summary>Oracle: hand-built palettes and matrices with the steps written out as literals, and the
    /// class table's own members.</summary>
    private static bool MotionSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.MotionSelfCheck");
        BonePalette a = default, b = default;
        Span<Matrix4x4> pa = a, pb = b;
        for (int k = 0; k < pa.Length; k++) { pa[k] = Matrix4x4.Identity; pb[k] = Matrix4x4.Identity; }
        check.Expect(!PaletteMoved(in a, in b), "a held pose is not a mover");
        pb[17].M42 = 0.002f;
        check.Expect(PaletteMoved(in a, in b), "one bone breathing 2 mm makes the skin a mover");

        Matrix4x4 at = Matrix4x4.CreateTranslation(10f, 20f, 0f);
        var (c0, m0) = MoveLevels(in at, Matrix4x4.CreateTranslation(10.00005f, 20f, 0f));
        check.Expect(!c0 && !m0, "a 0,05 mm drift is neither a caster nor a mover");
        var (c1, m1) = MoveLevels(in at, Matrix4x4.CreateTranslation(10.005f, 20f, 0f));
        check.Expect(!c1 && m1, "a 5 mm step moves the velocity but does not re-bake the shadow");
        var (c2, m2) = MoveLevels(in at, Matrix4x4.CreateTranslation(10.02f, 20f, 0f));
        check.Expect(c2 && m2, "a 2 cm step is both");
        Matrix4x4 turned = Matrix4x4.CreateRotationZ(0.003f) * at;
        var (c3, m3) = MoveLevels(in at, in turned);
        check.Expect(!c3 && m3, "a 0,17 degree turn moves the velocity but does not re-bake the shadow");

        check.Expect(EnsureBoneSlots(1) && _boneStage.Length > 0 && _bonePrevStage.Length > 0, "the bone stage holds one slot");
        if (_boneStage.Length > 0 && _bonePrevStage.Length > 0)
        {
            int i = MaxOwned - 1, keepBone = _oBone[i], keepSlots = _boneSlots, keepMv = _mvMovedAt[i];
            BonePalette keepNow = _boneStage.Span[0], keepPrev = _bonePrevStage.Span[0];
            _oBone[i] = 0; _boneSlots = 1; _mvMovedAt[i] = _frameSeq - 1;
            _boneStage.Span[0] = a; _bonePrevStage.Span[0] = b;
            check.Expect(SlotMoverNow(i, posed: true, dangly: false), "a standing skin whose staged pose changed is a mover");
            _bonePrevStage.Span[0] = a;
            check.Expect(!SlotMoverNow(i, posed: true, dangly: false), "a standing skin holding its pose is not");
            _oBone[i] = keepBone; _boneSlots = keepSlots; _mvMovedAt[i] = keepMv;
            _boneStage.Span[0] = keepNow; _bonePrevStage.Span[0] = keepPrev;
        }

        check.Expect(MoverJoins((int)AlloyClass.Opaque, ownDepth: false, ownVertex: false), "an opaque part joins the sweep");
        check.Expect(MoverJoins((int)AlloyClass.Cutout, ownDepth: false, ownVertex: false), "a cutout part joins the sweep");
        check.Expect(!MoverJoins((int)AlloyClass.Blended, ownDepth: false, ownVertex: false), "a blended part writes no depth and stays out");
        check.Expect(MoverJoins((int)AlloyClass.Opaque, ownDepth: true, ownVertex: false),
                     "a content pair with the stock vertex half joins the sweep, as hair that cuts its own strands");
        check.Expect(!MoverJoins((int)AlloyClass.Opaque, ownDepth: true, ownVertex: true),
                     "a content pair that places its own vertices stays out");
        check.Expect(MoverDepthOf((int)AlloyClass.Cutout, ownCoverage: false) == CompareOp.Equal, "a cutout mover tests EQUAL, so its holes keep the camera field");
        check.Expect(MoverDepthOf((int)AlloyClass.Opaque, ownCoverage: true) == CompareOp.Equal,
                     "an opaque mover whose content pair cuts its own coverage tests EQUAL, so the gaps between strands keep the camera field");
        check.Expect(MoverDepthOf((int)AlloyClass.Opaque, ownCoverage: false) == CompareOp.GreaterEqual, "an opaque mover keeps the nearer-or-equal test");
        return check.Ok;
    }

    private static void NoteMoverDraw(int n, bool skinned, bool dangly, CompareOp test)
    {
        _mvDraw[_mvN++] = new MoverDraw { Draw = n, Test = test };
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
    /// under <c>invariant</c>, so the depth test against what the mover already wrote passes where it drew;
    /// the previous clip position rides through the record's <c>prev</c> and last frame's view-projection, and
    /// for a posed skinmesh through last frame's palette at <see cref="BindBonesPrev"/>.</summary>
    internal static string MotionVs => ShaderSource.Get("cx_motion_v.glsl");

    /// <summary>The same convention as <see cref="MotionVectors.CameraFs"/>: prev minus current, render pixels,
    /// the current position unjittered, bounded and never NaN.</summary>
    internal static string MotionFs => ShaderSource.Get("cx_motion_f.glsl");
}
