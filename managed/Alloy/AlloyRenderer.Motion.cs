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
    private static int _mvN, _mvDrawn, _mvSkinDrawn, _mvDanglyDrawn;
    /// <summary>DB-mode record rewrites since the last period line: with the model moving (past the record's
    /// 0,1 mm step) and without (alpha, lighting, reveal, prev catch-up). Bumped by the stage workers.</summary>
    private static long _dbRecModel, _dbRecOther;
    private static bool _mvSkinned, _mvDangly;
    private static int _mvDanglyLoc;

    /// <summary>Dangly positions from before this frame's solve, part-local xyz, appended per owned part by
    /// <see cref="NoteDanglyPrev"/> and reset by the first append of a new frame. Native and growing for
    /// <c>EnsureBoneSlots</c>' reason.</summary>
    private static float* _dpStage;
    private static int _dpUsed, _dpCap, _dpSeq = -1;
    private static uint _dpBuf;
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
        Buffer.MemoryCopy(dst, &_dpStage[_dpUsed], (long)n * 12, (long)n * 12);
        _oDpAt[i] = _frameSeq; _oDpBase[i] = _dpUsed; _oDpN[i] = n;
        _dpUsed = need;
    }

    /// <summary>Was slot <paramref name="i"/>'s dangly solve snapshotted this frame?</summary>
    private static bool DanglyPrevNow(int i) => _oDpAt[i] == _frameSeq && _dpSeq == _frameSeq;
    private static uint _progMotion;
    private static int _mvVp, _mvPrevVp, _mvJitter, _mvSize, _mvBase;
    private static bool _mvBuilt, _mvFailed;

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

    /// <summary>The mover sweep, inside the motion bracket. Reads the STAGED slot buffer, so it must run
    /// where <c>Issue.BindStagedSlots</c> has (the prepass hands it back). Leaves the framebuffer and viewport as the
    /// caller had them; program, VAO, cull and blend are the next pass's PassState to set.</summary>
    private static void MoverMotionPass(Matrix4x4* vp, float jx, float jy, int* viewport)
    {
        int n = _mvN; _mvN = 0;
        bool skinned = _mvSkinned, dangly = _mvDangly; _mvSkinned = false; _mvDangly = false;
        try
        {
            if (n == 0 || _mvFailed) return;
            if (!_mvBuilt) EnsureMotionProgram();
            if (_progMotion == 0) return;
            // only a frame a skinned mover draws; a failed stream sits the sweep out (StreamRing logs and paints it)
            if (skinned && !StreamRing.Stream(StreamRing.Site.PrevBones, BindBonesPrev, _bonePrevStage,
                                              (long)_boneSlots * BoneFloats * sizeof(float), out _)) return;
            if (!MotionVectors.BeginMovers(PostChain.DepthTex, viewport)) return;
            if (dangly) UploadDanglyPrev();
            Sweep(n, vp, jx, jy, viewport);
            MotionVectors.EndMovers(PostChain.Fbo, viewport);
        }
        // Every exit rolls the camera history -- with no movers on screen too, or the field would compare
        // every frame against the first one.
        finally { MotionVectors.RollHistory(); }
    }

    /// <summary>The effect classes' own vectors, one site for the whole frame: grass and particles are
    /// displaced by wind and by simulation rather than by a matrix, so the field over their pixels is the
    /// GROUND's, and each re-issues what it drew. Runs past both of the camera field's sites and before the
    /// resolve reads it, so the target is complete whichever site painted it.</summary>
    private static void EffectMotionPass(Matrix4x4* vp, Matrix4x4* view, Matrix4x4* proj,
                                        float jx, float jy, int* viewport)
    {
        if (!GrassField.HasMotion && !Particles.HasMotion) return;
        // The camera and the clock the field was built against: RollHistory has by now adopted this frame's.
        Matrix4x4 prevVp = MotionVectors.FieldPrevVp;
        int prevClock = MotionVectors.FieldPrevClock;
        // The engine texture walk the grass sweep needs, while the frame's own target is still bound: the
        // create path of a first-sighted atlas can touch GL.
        GrassField.PrepareMotion();
        if (!MotionVectors.BeginMovers(PostChain.DepthTex, viewport)) return;
        GrassField.DrawMotion(vp, &prevVp, prevClock, jx, jy, viewport);
        Particles.DrawMotion(view, proj, &prevVp, jx, jy, viewport);
        MotionVectors.EndMovers(PostChain.Fbo, viewport);
        if (!MotionVectors.EffectsSaid)
            MotionVectors.SayEffects($"grass {(GrassField.HasMotion ? "yes" : "no")}, "
                                   + $"particles {(Particles.HasMotion ? "yes" : "no")}");
    }

    private static void Sweep(int n, Matrix4x4* vp, float jx, float jy, int* viewport)
    {
        Matrix4x4 prevVp = MotionVectors.PrevVp;
        Gl.UseProgram(_progMotion);
        Gl.UniformMatrix4fv(_mvVp, 1, 0, (float*)vp);
        Gl.UniformMatrix4fv(_mvPrevVp, 1, 0, (float*)&prevVp);
        Gl.Uniform2f(_mvJitter, jx, jy);
        Gl.Uniform2f(_mvSize, viewport[2], viewport[3]);
        uint boundVao = 0; int boundCull = -1;
        for (int k = 0; k < n; k++)
        {
            int d = _mvDraw[k], i = _dSlot[d];
            // A DB slot's _oLodBit keeps only the arena bit, so the arena FORMAT comes from the slot's range,
            // exactly as the colour batch keys it -- else a skinned mover pulls through the static VAO.
            uint vao = VaoName(DbSlot(i) ? DbVaoKey(i) | DbArenaVaoBit : _oVao[i] | _oLodBit[i], _oCls[i]);
            if (vao == 0) continue;
            BindBatchState(_oNoCull[i], vao, ref boundCull, ref boundVao);
            Gl.Uniform1i(_mvBase, d);
            if (DanglyPrevNow(i)) { Gl.Uniform2i(_mvDanglyLoc, _oDpBase[i], _oDpN[i]); _mvDanglyDrawn++; }
            else Gl.Uniform2i(_mvDanglyLoc, 0, 0);
            // A DB slot carries no engine primitive enum, and index 0 is GL_POINTS in the engine's translation
            // table; an arena draw is TRIANGLES, as the colour batch row says for it unconditionally.
            MultiDrawRange(DbSlot(i) ? Gl.TRIANGLES : _primTable[_oPrim[i]], d, 1);
            _mvDrawn++;
            if (_oBone[i] >= 0) _mvSkinDrawn++;
        }
        Gl.BindVertexArray(0);
    }

    /// <summary>This frame's dangly snapshots onto <see cref="BindDanglyPrev"/>. Orphan-then-fill.</summary>
    private static void UploadDanglyPrev()
    {
        if (_dpBuf == 0) { uint b; Gl.CreateBuffers(1, &b); _dpBuf = b; }
        var size = (IntPtr)((long)Math.Max(_dpUsed, 1) * sizeof(float));
        Gl.NamedBufferData(_dpBuf, size, null, Gl.DYNAMIC_DRAW);
        if (_dpUsed > 0) Gl.NamedBufferSubData(_dpBuf, IntPtr.Zero, size, _dpStage);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindDanglyPrev, _dpBuf);
    }

    private static void EnsureMotionProgram()
    {
        _mvBuilt = true;
        _progMotion = GlProgram.Graphics(MotionVs, MotionFs, _log, "MoverMotion");
        if (_progMotion == 0) { _mvFailed = true; _log?.Info("MoverMotion: the program did not build -- movers keep camera-only vectors"); return; }
        _mvVp = GlProgram.Loc(_progMotion, "uVP"); _mvPrevVp = GlProgram.Loc(_progMotion, "uPrevVP");
        _mvJitter = GlProgram.Loc(_progMotion, "uJitter"); _mvSize = GlProgram.Loc(_progMotion, "uSize");
        _mvBase = GlProgram.Loc(_progMotion, "uDrawBase"); _mvDanglyLoc = GlProgram.Loc(_progMotion, "uDangly");
    }

    /// <summary>The [alloy] period term for the sweep; resets the counters.</summary>
    internal static string MoverStats(int frames)
    {
        int d = _mvDrawn, sk = _mvSkinDrawn, dg = _mvDanglyDrawn; _mvDrawn = 0; _mvSkinDrawn = 0; _mvDanglyDrawn = 0;
        long rm = System.Threading.Interlocked.Exchange(ref _dbRecModel, 0), ro = System.Threading.Interlocked.Exchange(ref _dbRecOther, 0);
        float f = MathF.Max(frames, 1);
        string db = rm + ro == 0 ? "" : $", db records rewritten {rm / f:F1}/frame moved + {ro / f:F1}/frame other" + RecWhyStats(frames);
        return _mvFailed ? "movers FAILED" : (d == 0 ? "" : $", {d / f:F1} mover draws/frame ({sk / f:F1} skinned, {dg / f:F1} dangly)") + db;
    }

    /// <summary>Context loss: the program name is gone.</summary>
    private static void MotionContextLost() { _progMotion = 0; _mvBuilt = false; _mvFailed = false; _mvN = 0; _mvSkinned = false; _mvDangly = false; _dpBuf = 0; }

    /// <summary>The mover vertex stage. <c>gl_Position</c> is the prepass's exact <see cref="ClipExpr"/> text
    /// under <c>invariant</c>, so LEQUAL against the depth the mover already wrote passes on every fragment;
    /// the previous clip position rides through the record's <c>prev</c> and last frame's view-projection, and
    /// for a posed skinmesh through last frame's palette at <see cref="BindBonesPrev"/>.</summary>
    internal static string MotionVs => ShaderSource.Get("cx_motion_v.glsl");

    /// <summary>The same convention as <see cref="MotionVectors.CameraFs"/>: prev minus current, render pixels,
    /// the current position unjittered, bounded and never NaN.</summary>
    internal static string MotionFs => ShaderSource.Get("cx_motion_f.glsl");
}
