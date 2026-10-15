using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;

namespace CX.Alloy.Seam;

public sealed unsafe partial class ObjectSlots
{
    /// <summary><c>Gob::Animate(float dt)</c> @0x49d8c0 on ONE object: the path
    /// <c>CAurObject::AnimateExternal</c> takes for a GUI panel's model, which runs outside the scene's own
    /// animate. The engine's version also walks the behaviour and the attachments; seam-anim owns that.</summary>
    [UnmanagedCallersOnly]
    private static void Animate(nint self, float dt)
    {
        if (!Live(self, 6, out DbId id, out _)) return;
        DbAnim.AnimateObject(id, dt);
    }

    /// <summary><c>Gob::PlayAnimation(char const* name, float speed, int loops, float fadeIn)</c> @0x49a750.
    /// A null or empty name STOPS the current animation -- that is what <c>Gob::~Gob</c> does on the way out,
    /// so the seam keeps it rather than treating it as a bad argument. The engine folds the visual
    /// transform's <c>AnimationSpeed</c> lerp into the speed, which is why the record sits in the shell.</summary>
    [UnmanagedCallersOnly]
    private static int PlayAnimation(nint self, nint name, float speed, int loops, float fadeIn)
    {
        if (!Live(self, 7, out DbId id, out nint shell)) return 0;
        int r = DbAnim.PlayAnimation(id, Str(name), speed * VisualXform.AnimSpeed(shell), loops, fadeIn);
        if (r != 0) SceneDb.Touch(id, Dirty.Anim);
        return r;
    }

    /// <summary><c>Gob::GetAnimation(char const*, float* time, float* length)</c> @0x499eb0: the RUN playing
    /// that animation, or null, with the run's current TIME through the first pointer and the animation's
    /// LENGTH through the second -- the engine's order, both -1 when nothing is playing, both pointers optional.
    /// The handle itself is what <c>CNWCAnimBase::GetOverlayAnimationPlaying</c> reads as "playing".</summary>
    [UnmanagedCallersOnly]
    private static nint GetAnimation(nint self, nint name, float* time, float* length)
    {
        if (!Live(self, 8, out DbId id, out _)) return 0;
        nint h = DbAnim.GetAnimation(id, Str(name), out float t, out float len);
        if (time != null) *time = t;
        if (length != null) *length = len;
        return h;
    }

    /// <summary><c>Gob::LoadAddInAnimations(char const*)</c> @0x43a960: borrow another model's animations.</summary>
    [UnmanagedCallersOnly]
    private static int LoadAddInAnimations(nint self, nint model)
    {
        if (!Live(self, 9, out DbId id, out _)) return 0;
        return DbAnim.LoadAddInAnimations(id, Str(model));
    }

    /// <summary><c>Gob::RemoveAddInAnimations()</c> @0x443f60.</summary>
    [UnmanagedCallersOnly]
    private static void RemoveAddInAnimations(nint self)
    {
        if (Live(self, 10, out DbId id, out _)) DbAnim.RemoveAddInAnimations(id);
    }

    /// <summary><c>Gob::EnableForceAnimations()</c> @0x436e80 is one byte at +0x1a, and
    /// <c>IsForceAnimated</c> @0x436ea0 reads the same byte -- "keep animating off screen", not a state.</summary>
    [UnmanagedCallersOnly]
    private static void EnableForceAnimations(nint self) => ForceAnim(self, 57, true);

    /// <summary><c>Gob::DisableForceAnimations()</c> @0x436e90.</summary>
    [UnmanagedCallersOnly]
    private static void DisableForceAnimations(nint self) => ForceAnim(self, 58, false);

    private static void ForceAnim(nint self, int logical, bool on)
    {
        if (!Live(self, logical, out DbId id, out nint shell)) return;
        new Gob(shell).SeamForceAnim = on ? (byte)1 : (byte)0;
        Flag(id, SeamFlag.ForceAnimate, on);
        DbAnim.SetAnimationsEnabled(id, on, force: true);
    }

    /// <summary><c>Gob::EnableAnimations()</c> @0x436eb0 is one byte at +0x1d -- and
    /// <c>Gob::IsAnimating</c> @0x436ed0 returns THAT byte, so "is animating" is the engine's name for
    /// "animations are enabled", not "an animation is playing". Kept as the engine has it.</summary>
    [UnmanagedCallersOnly]
    private static void EnableAnimations(nint self) => AnimGate(self, 59, true);

    /// <summary><c>Gob::DisableAnimations()</c> @0x436ec0.</summary>
    [UnmanagedCallersOnly]
    private static void DisableAnimations(nint self) => AnimGate(self, 60, false);

    private static void AnimGate(nint self, int logical, bool on)
    {
        if (!Live(self, logical, out DbId id, out nint shell)) return;
        new Gob(shell).SeamAnimEnabled = on ? (byte)1 : (byte)0;
        Flag(id, SeamFlag.Animate, on);
        DbAnim.SetAnimationsEnabled(id, on, force: false);
    }

    /// <summary><c>Gob::IsAnimating()</c> @0x436ed0: the +0x1d byte, as above.</summary>
    [UnmanagedCallersOnly]
    private static byte IsAnimating(nint self)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 77); return 0; }
        return new Gob(shell).SeamAnimEnabled;
    }

    /// <summary><c>Gob::IsForceAnimated()</c> @0x436ea0: the +0x1a byte.</summary>
    [UnmanagedCallersOnly]
    private static byte IsForceAnimated(nint self)
    {
        nint shell = Facade.GobBlock(self);
        if (shell == 0) { Facade.Object(self, 85); return 0; }
        return new Gob(shell).SeamForceAnim;
    }

    /// <summary><c>Gob::SetSynchronizeAnimations(bool)</c> @0x4447d0: one byte at +0x1b8 -- play this
    /// object's animations on the same clock as the objects attached to it.</summary>
    [UnmanagedCallersOnly]
    private static void SetSynchronizeAnimations(nint self, byte on)
    {
        if (!Live(self, 78, out DbId id, out nint shell)) return;
        new Gob(shell).SyncHier = on;
        Flag(id, SeamFlag.SyncAnimate, on != 0);
        DbAnim.SetSynchronizeAnimations(id, on != 0);
    }

    /// <summary><c>Gob::BeginLookAtAnimate(target, head, eye, a, b, c, d)</c> @0x49d5b0: the head-tracking
    /// overlay. Four floats, which Win64 assigns by argument INDEX while SysV walks its own SSE sequence --
    /// declaring them in order lets the runtime lay them out per platform, which is the only thing that is
    /// right on both.</summary>
    [UnmanagedCallersOnly]
    private static void BeginLookAtAnimate(nint self, nint target, nint head, nint eye,
                                           float a, float b, float c, float d)
    {
        if (!Live(self, 74, out DbId id, out _)) return;
        DbId at = target == 0 ? default : Facade.Object(target, 74);
        DbAnim.BeginLookAt(id, at, Str(head), Str(eye), a, b, c, d);
        SceneDb.Touch(id, Dirty.Anim);
    }

    /// <summary><c>Gob::EndLookAtAnimate()</c> @0x4979e0.</summary>
    [UnmanagedCallersOnly]
    private static void EndLookAtAnimate(nint self)
    {
        if (!Live(self, 75, out DbId id, out _)) return;
        DbAnim.EndLookAt(id);
        SceneDb.Touch(id, Dirty.Anim);
    }
}
