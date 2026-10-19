using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Particles' own motion vectors: a particle is moved by the engine's simulation, not by a matrix,
/// so the field over its pixels is whatever stood BEHIND it. After the colour pass has drawn, the same quads
/// are re-issued into the velocity target from last frame's centre, which the record now carries.</summary>
internal static unsafe partial class Particles
{
    /// <summary>What the velocity sweep re-issues: the groups this pass drew, and the GL texture name each
    /// one actually bound. The name is captured rather than re-resolved because the engine's own bind
    /// wrapper ticks each atlas's animation controller, and asking it twice a frame would step every
    /// animated effect at double speed. Rewritten in place by the colour pass -- nothing allocates here.</summary>
    private sealed class PartMotionState
    {
        public int Gen = -1, Groups;
        public readonly uint[] TexName = new uint[MaxGroups];
    }

    private static PartMotionState _motion = new();

    /// <summary>A step no particle takes in one frame, in world units: past it the slot is a POOLED one the
    /// engine recycled under us, not a particle that moved, and its stored centre is another particle's.</summary>
    private const float PrevJumpMax = 8f;

    /// <summary>Last frame's centre for a record slot: the centre the slot held, or this frame's own where
    /// there is no previous frame to take one from -- the mirror is not last frame's (<paramref name="fresh"/>),
    /// or the step is past <see cref="PrevJumpMax"/>. Either way the particle gets a zero step, which is the
    /// camera field's own answer rather than a reprojection across the area.</summary>
    private static Vector3 PrevCentre(bool fresh, in Vector3 held, in Vector3 centre)
    {
        if (fresh) return centre;
        return (centre - held).LengthSquared() > PrevJumpMax * PrevJumpMax ? centre : held;
    }

    /// <summary>Note this pass's groups for the sweep.</summary>
    private static void NoteMotionGroups(int groups)
    { _motion.Groups = groups; _motion.Gen = AlloyRenderer.PassGen; }

    /// <summary>Has this pass drawn particles the sweep can re-issue?</summary>
    internal static bool HasMotion
        => _motion.Gen == AlloyRenderer.PassGen && _motion.Groups > 0 && !_off && Enabled.V
        && !_gl.Failed && _gl.MotionProg != 0 && _gl.Vao != 0;

    /// <summary>Re-issue this pass's quads into the velocity target the caller has opened, from last frame's
    /// centre: each fragment the sprite is most of paints the particle's own vector over the field that
    /// belongs to what is behind it. The caller owns the framebuffer, the depth test and the viewport.</summary>
    internal static void DrawMotion(Matrix4x4* view, Matrix4x4* proj, Matrix4x4* prevVp, float jx, float jy, int* viewport)
    {
        if (!HasMotion) return;
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindRecs, _gl.RecBuf);
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindIdx, _gl.IdxBuf);
        Gl.UseProgram(_gl.MotionProg);
        Gl.UniformMatrix4fv(_gl.MLocView, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(_gl.MLocProj, 1, 0, (float*)proj);
        Gl.UniformMatrix4fv(_gl.MLocPrevVp, 1, 0, (float*)prevVp);
        Gl.Uniform2f(_gl.MLocJitter, jx, jy);
        Gl.Uniform2f(_gl.MLocSize, viewport[2], viewport[3]);
        Gl.Uniform1i(_gl.MLocTex, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        if (_gl.Samp != 0) Gl.BindSampler(0, _gl.Samp);
        Gl.BindVertexArray(_gl.Vao);
        new PassState(depth: true, depthWrite: false, cull: false).Apply();
        Gl.ColorMask(1, 1, 1, 1);
        int cull = -1, add = -1;
        uint bound = 0;
        for (int gi = 0; gi < _motion.Groups; gi++)
        {
            if (_gParts[gi] == 0 || _motion.TexName[gi] == 0) continue;
            int a = _gCls[gi] == ClsAdd ? 1 : 0;
            if (a != add) { add = a; Gl.Uniform1i(_gl.MLocAdditive, a); }
            if (_gCull[gi] != cull)
            { cull = _gCull[gi]; if (cull != 0) Gl.Enable(Gl.CULL_FACE); else Gl.Disable(Gl.CULL_FACE); }
            if (_motion.TexName[gi] != bound)
            { bound = _motion.TexName[gi]; Gl.BindTexture(Gl.TEXTURE_2D, bound); }
            Gl.DrawArrays(Gl.TRIANGLES, _gStart[gi] * 6, _gParts[gi] * 6);
        }
        if (_gl.Samp != 0) Gl.BindSampler(0, 0);
        Gl.BindVertexArray(0);
        Gl.Disable(Gl.CULL_FACE);
    }

    /// <summary>The velocity arm of the particle stages -- the same two texts with <c>CX_MOTION</c> compiled
    /// in, so the quad it expands is the quad that was drawn. A failure leaves the class on the camera's own
    /// vectors; nothing else about the pass depends on it.</summary>
    private static void BuildMotionProgram()
    {
        _gl.MotionProg = GlProgram.Graphics(
            ShaderStages.WithDefine(ShaderSource.Get("cx_part_v.glsl"), GrassField.MotionDefine),
            ShaderStages.WithDefine(ShaderSource.Get("cx_part_f.glsl"), GrassField.MotionDefine),
            _log, "Particles motion vectors");
        if (_gl.MotionProg == 0)
        { _log?.Info("Particles: the velocity program is unavailable -- particles keep the camera's vectors"); return; }
        _gl.MLocView = GlProgram.Loc(_gl.MotionProg, "uView");
        _gl.MLocProj = GlProgram.Loc(_gl.MotionProg, "uProj");
        _gl.MLocPrevVp = GlProgram.Loc(_gl.MotionProg, "uPrevVP");
        _gl.MLocJitter = GlProgram.Loc(_gl.MotionProg, "uJitter");
        _gl.MLocSize = GlProgram.Loc(_gl.MotionProg, "uSize");
        _gl.MLocTex = GlProgram.Loc(_gl.MotionProg, "uTex");
        _gl.MLocAdditive = GlProgram.Loc(_gl.MotionProg, "uAdditive");
        // The shadow samplers again: this program compiles the same light libraries, and three sampler types
        // left on image unit 0 is the INVALID_OPERATION that discards the whole draw.
        Gl.UseProgram(_gl.MotionProg);
        Gl.Uniform1i(GlProgram.Loc(_gl.MotionProg, "uSunShadow"), (int)AlloyRenderer.SunUnit);
        Gl.Uniform1i(GlProgram.Loc(_gl.MotionProg, "uSunDynShadow"), (int)AlloyRenderer.SunDynUnit);
        Gl.Uniform1i(GlProgram.Loc(_gl.MotionProg, "uLocalShadow"), (int)AlloyRenderer.CubeUnit);
        Gl.Uniform1i(GlProgram.Loc(_gl.MotionProg, "uLocalDynShadow"), (int)AlloyRenderer.CubeDynUnit);
    }

    /// <summary>The two stage mains, for the suite that asserts the velocity arm expands the drawn quad.</summary>
    internal static string MotionVmain => ShaderSource.Get("cx_part_vmain.glsl");
    /// <inheritdoc cref="MotionVmain"/>
    internal static string MotionFmain => ShaderSource.Get("cx_part_fmain.glsl");
}
