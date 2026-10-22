using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>
/// Footsteps on contact for a walk that has none of its own, a <see cref="PoseService"/> module. The engine keys its
/// footsteps to the model's <c>snd_Footstep</c> animation events (bound in <c>CNWCCreature::LoadModel</c>); a walk that
/// carries them keeps them. One that does not steps here, through the engine's own <c>PlayFootstep</c>, on the frames
/// <see cref="StrideWarp"/> plants a foot. Each step is offered to <see cref="CX.Alloy.Weather.Footstep"/> first.
/// </summary>
internal static unsafe class Footfall
{
    /// <summary>Seconds under which a second step is the same footfall: a sprint steps every ~0.25 s.</summary>
    private const float MinGap = 0.12f;
    /// <summary>Seconds a stepping creature's ownership outlives its last frame: the engine's event fires inside
    /// <c>Gob::Animate</c>, before this frame's step is known.</summary>
    private const float Hold = 0.25f;

    private static PlayFootstepFn _orig;
    private static bool _ours;
    internal static int Steps, Skipped;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!SymbolMap.Require(log, "Footfall", SymbolMap.PlayFootstep)) return;
        _orig = hooks.InstallPlayFootstep(&OnPlayFootstep);
        if (_orig == null) log.Info("Footfall: PlayFootstep did not hook -- footsteps stay on the animation's events");
    }

    /// <summary>After the stride warp: steps a body whose walk has no footstep events of its own
    /// (<see cref="PoseService.Slot.Unvoiced"/>) on a frame a foot planted (<see cref="PoseService.Slot.Touchdown"/>).</summary>
    internal static void Step(ref PoseService.Slot s, IntPtr creature, bool snap, float dt)
    {
        s.Stepping = _orig != null && AnimKnobs.Footsteps.V && s.Loco && s.Unvoiced && !snap && creature != IntPtr.Zero;
        if (!s.Stepping) { s.SinceStep = MinGap; return; }
        s.SteppedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!Due(ref s.SinceStep, s.Touchdown, dt)) return;
        _ours = true;
        if (!CX.Alloy.Weather.Footstep(creature)) _orig(creature);
        _ours = false;
        Steps++;
    }

    /// <summary>One frame: true when a foot <paramref name="touched"/> down and the last step is <see cref="MinGap"/> behind.</summary>
    internal static bool Due(ref float since, bool touched, float dt)
    {
        since += dt;
        if (!touched || since < MinGap) return false;
        since = 0f;
        return true;
    }

    [UnmanagedCallersOnly]
    private static void OnPlayFootstep(IntPtr creature)
    {
        if (!_ours && PoseService.Stepping(creature, (long)(Hold * System.Diagnostics.Stopwatch.Frequency))) { Skipped++; return; }
        if (_ours || !CX.Alloy.Weather.Footstep(creature)) _orig(creature);
    }

    /// <summary>Touchdowns every 0.5 s and a doubled one 2 frames after each: one step per footfall, the double merged.
    /// Oracle: the hand-written touchdown frames.</summary>
    internal static bool SelfCheck()
    {
        float since = MinGap, dt = 1f / 60f;
        int n = 0;
        for (int i = 0; i < 600; i++)
            if (Due(ref since, i % 30 == 0 || i % 30 == 2, dt)) n++;
        return n == 20;
    }
}
