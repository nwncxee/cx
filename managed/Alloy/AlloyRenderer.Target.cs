using System;
using CX.Alloy.Db;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Framebuffer plumbing. The engine's target is the default framebuffer by construction (the shim
/// binds FBO 0 at the anchor); this latches its viewport, sRGB state and clear values, and binds the 4.6-side
/// equivalent.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Where the engine wants this frame's result. Every field is a GL query answer, latched per
    /// generation rather than re-asked per frame.</summary>
    internal struct Target
    {
        internal int Vp0, Vp1, Vp2, Vp3;
        internal bool Srgb;
        internal float Clear0, Clear1, Clear2, Clear3;
    }

    private static Target _target;
    /// <summary>The generation the latch belongs to: the camera's own viewport (<c>+0x4dc</c>/<c>+0x4e0</c>) and
    /// the GUI scale -- what the GL viewport is BUILT from, <c>Camera::UpscaleViewport</c>'s output scaled by
    /// <c>CAurora::GetGUIScale()</c> -- plus the enrolled Scene, for the area transition that rebuilds the
    /// engine's render targets. Deliberately conservative: a spurious re-read costs seven GL calls once.</summary>
    private static float _tgtVpW = -1f, _tgtVpH = -1f, _tgtUiScale = -1f;
    private static IntPtr _tgtScene = IntPtr.Zero;
    private static bool _tgtValid;

    /// <summary>Drop the latch: the next frame re-reads it. Called wherever the GL objects behind it stop
    /// meaning anything (context loss / disarm), where a stale answer would be a draw into a dead target.</summary>
    internal static void ForgetTarget() => _tgtValid = false;

    /// <summary>Read the frame's target block in the ENGINE's context, before any 4.6 bracket opens -- every
    /// value in it is per-CONTEXT GL state, so inside the bracket GL answers for Alloy's. The colour pass
    /// reads the latch this leaves, which is what lets a whole view share one bracket.</summary>
    internal static void LatchFrameTarget() => ThisFrameTarget();

    /// <summary>This frame's target block. One int compare in the common case; a re-read on a generation
    /// change, and a re-read-and-compare once every <see cref="LogEvery"/> frames -- the self-check that
    /// keeps the latch honest, since every value in it is ultimately GL's to change.</summary>
    private static ref Target ThisFrameTarget()
    {
        bool gen = !_tgtValid || _tgtVpW != FrameInputs.CamViewW || _tgtVpH != FrameInputs.CamViewH
                   || _tgtUiScale != CX.Ui.Metrics.GameUiScale || _tgtScene != _enrollScene;
        if (gen)
        {
            _tgtVpW = FrameInputs.CamViewW; _tgtVpH = FrameInputs.CamViewH;
            _tgtUiScale = CX.Ui.Metrics.GameUiScale; _tgtScene = _enrollScene;
            ReadTarget(out _target);
            // The shim binds FBO 0 before the anchor, so there is no engine name here to go stale.
            _tgtValid = true;
        }
        else if (_frameSeq % LogEvery == 0)
        {
            ReadTarget(out Target now);
            if (!Same(in now, in _target))
            {
                _log?.Info($"AlloyRenderer: target self-check DRIFT -- latched vp " +
                           $"{_target.Vp2}x{_target.Vp3} srgb {_target.Srgb}, GL says vp " +
                           $"{now.Vp2}x{now.Vp3} srgb {now.Srgb}; adopting the live answer. The latch " +
                           "generation key is missing an edge.");
                _target = now;
            }
        }
        // The clear colour is live area state (the fog colour this frame), not frame topology, so it is read
        // per frame rather than latched with the rest of the block.
        ReadClear(ref _target);
        return ref _target;
    }

    internal static bool Same(in Target a, in Target b)
        => a.Vp0 == b.Vp0 && a.Vp1 == b.Vp1 && a.Vp2 == b.Vp2 && a.Vp3 == b.Vp3 && a.Srgb == b.Srgb;

    /// <summary>Where the engine wants the result: viewport and the sRGB encode state. Alloy draws in its own share-listed 4.6 context (GlContextService), so it cannot
    /// desync GLRender's state and has nothing to restore -- it still has to be TOLD this much.</summary>
    private static void ReadTarget(out Target t)
    {
        t = default;
        if (Renderer.OnVulkan)
        {
            // no GL: the camera's own view size, which is the world viewport the engine sets (origin 0), linear
            t.Vp2 = (int)FrameInputs.CamViewW; t.Vp3 = (int)FrameInputs.CamViewH;
            if ((t.Vp2 <= 0 || t.Vp3 <= 0) && GuiPass.Screen(out int sw, out int sh)) { t.Vp2 = sw; t.Vp3 = sh; }
            ReadClear(ref t);
            return;
        }
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        t.Vp0 = vp[0]; t.Vp1 = vp[1]; t.Vp2 = vp[2]; t.Vp3 = vp[3];
        t.Srgb = Gl.IsEnabled(Gl.FRAMEBUFFER_SRGB) != 0;
        ReadClear(ref t);
    }

    /// <summary>The target's clear COLOUR -- the area's fog colour, caught at the <c>SetFog</c> hook. The owned
    /// target starts from it. Read per frame, never latched: live area state, not frame topology.</summary>
    private static void ReadClear(ref Target t)
    {
        // GL's own clear colour is whatever last touched it (the GUI's), so the value the world wants is the
        // one the game layer hands CNWCArea::SetFog -- the area's fog COLOUR whatever the fog AMOUNT is, caught
        // at the hook as ScreenOwner.ClearRgba. Display-space, NOT linearised, and alpha 0: the post stack and
        // the GUI composite read the world target's alpha, so a 1 here is not cosmetic.
        float[] eng = ScreenOwner.ClearRgba;
        t.Clear0 = eng[0]; t.Clear1 = eng[1]; t.Clear2 = eng[2]; t.Clear3 = 0f;
    }

    /// <summary>The target block the colour pass was ENTERED with, copied at the top of the pass. A second
    /// <see cref="ThisFrameTarget"/> call mid-pass is not the same answer: on a self-check frame it re-queries
    /// GL, which mid-pass says whatever Alloy last bound -- the post stack's own colour-only framebuffer, which
    /// the latch would adopt as the engine's target for every later frame.</summary>
    private static Target _passTarget;

    /// <summary>Snapshot the pass's target block. Once, at the top of the colour pass, after the one
    /// <see cref="ThisFrameTarget"/> read.</summary>
    private static void HoldPassTarget(in Target t) => _passTarget = t;

    /// <summary>Put the pass's engine target back as the draw framebuffer, sRGB state included -- for the post
    /// stack's last stage, which writes where the resolve would have. From the snapshot, never a fresh read.</summary>
    internal static void RebindFrameTarget()
    {
        ref Target t = ref _passTarget;
        BindTarget();
        if (t.Srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
    }

    /// <summary>Bind the 4.6-side draw target: the <see cref="PresentTarget"/> image, which the engine's
    /// context blits to the real default framebuffer after the bracket. Zero -- no composite image -- writes
    /// framebuffer 0, which only the shared GLX drawable makes correct.</summary>
    private static void BindTarget() => Gl.BindFramebuffer(Gl.FRAMEBUFFER, PresentTarget.Fbo46);
}
