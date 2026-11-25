using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>The one object a <see cref="TargetFire.Fire"/> in flight answers every pick with.</summary>
internal sealed unsafe class TargetFireState
{
    /// <summary>The GUI hit-test chain slot below this one; null until <see cref="TargetFire.Init"/> chains it.</summary>
    public GuiObjectHitCheckFn* GuiHit;
    /// <summary>The CNWCObject* the picks answer; Zero when no fire is in flight.</summary>
    public IntPtr At;
    /// <summary>The target cursor's per-frame grading, null until <see cref="TargetFire.Init"/> hooks it.</summary>
    public ActionTargetUpdateFn Update;
    /// <summary>The engine is inside <see cref="Update"/>: its picks are the cursor's, which a covering window answers.</summary>
    public bool Grading;
    /// <summary>The window drawn over the world, asked during a grading; null when none is registered.</summary>
    public TargetFire.CoverPick? Cover;
}

/// <summary>
/// Resolve a live target mode at a chosen object, exactly as a click on it would: for one <c>HandleLMouseDown</c> the GUI
/// pick finds nothing and the world pick answers the object. Tab-target fires at its lock; the CX inventory fires at the
/// item clicked in its window, which the engine's own GUI pick cannot see.
/// </summary>
internal static unsafe partial class TargetFire
{
    private static readonly TargetFireState S = new();
    /// <summary>The GUI hit-test's chain slot, above the CX quickbar's proxy so a fire sees no GUI at all: between the
    /// mouse-look layer (100) and the framework input layer (0).</summary>
    private const int ChainPriority = 50;
    /// <summary>The world pick's "an object" verdict (PerformLButtonDownAction's <c>== 2</c> branch).</summary>
    private const int PickObject = 2;

    /// <summary>Chains the GUI hit-test; the world picks are <see cref="MouseLookService"/>'s, which asks
    /// <see cref="OverridePick"/> first.</summary>
    internal static void Init(ExtenderLog log, HookService hooks)
    {
        if (!AimAssist.Ready || !MouseLookService.PicksHooked) { log.Info("TargetFire: standing down (target-mode or pick hooks are not up)"); return; }
        S.GuiHit = hooks.ChainGuiObjectHitCheck(&OnGuiHitCheck, ChainPriority);
        if (S.GuiHit == null) { log.Info("TargetFire: standing down (GuiObjectHitCheck chain refused)"); return; }
        if (SymbolMap.Require(log, "TargetFire.cover", SymbolMap.ActionTargetUpdate)) S.Update = hooks.InstallActionTargetUpdate(&OnUpdate);
    }

    /// <summary>Whether a CX window covers the pointer, and the CNWCObject* under it there (Zero for none).</summary>
    internal delegate bool CoverPick(out IntPtr obj);

    /// <summary>Let <paramref name="cover"/> grade the target cursor where it covers the world; game thread.</summary>
    internal static void SetCover(CoverPick cover) => S.Cover = cover;

    /// <summary>Every hook a fire needs is up.</summary>
    internal static bool Ready => S.GuiHit != null;

    /// <summary>A fire is in flight: the default-action hook leaves the click to the engine.</summary>
    internal static bool InFlight => S.At != IntPtr.Zero;

    /// <summary>The engine's live <c>CActionTarget*</c>, Zero when no target mode is up.</summary>
    internal static IntPtr Live()
    {
        IntPtr app = CameraService.App();
        if (app == IntPtr.Zero) return IntPtr.Zero;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal;
        return intern.IsNull ? IntPtr.Zero : intern.ActionTarget.Ptr;
    }

    /// <summary>Resolve <paramref name="mode"/> at <paramref name="obj"/> (a CNWCObject*); false when not
    /// <see cref="Ready"/>. The mode's own allowed-type scan and validation still decide. Nothing may touch
    /// <paramref name="mode"/> after this returns: the fire callback may delete it.</summary>
    internal static bool Fire(IntPtr mode, IntPtr obj)
    {
        if (!Ready || mode == IntPtr.Zero || obj == IntPtr.Zero) return false;
        S.At = obj;
        try { AimAssist.Resolve(mode); }
        finally { S.At = IntPtr.Zero; }
        return true;
    }

    /// <summary>The world picks' first question: does a covering window grade the cursor, or is a fire in flight? Then the pick
    /// IS that object (a fire's with its position), verdict "object", or nothing. The <c>int*</c> flag is left alone:
    /// Update and HandleLMouseDown pass null, and PerformLButtonDownAction reads it on the ground verdict only.</summary>
    internal static bool OverridePick(IntPtr* obj, IntPtr point, out int verdict)
    {
        IntPtr at = S.At;
        verdict = 0;
        if (at == IntPtr.Zero && Covered(out at))
        {
            if (obj != null) *obj = at;
            verdict = at != IntPtr.Zero ? PickObject : 0;
            return true;
        }
        if (at == IntPtr.Zero) return false;
        if (obj != null) *obj = at;
        if (point != IntPtr.Zero) { var w = new CNWCObject(at).Position; float* p = (float*)point; p[0] = w.X; p[1] = w.Y; p[2] = w.Z; }
        verdict = PickObject;
        return true;
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnGuiHitCheck(IntPtr self, int x, int y, int fresh, int mask)
        => S.At != IntPtr.Zero || Covered(out _) ? IntPtr.Zero : (*S.GuiHit)(self, x, y, fresh, mask);

    /// <summary>Inside a grading, a window covers the pointer; <paramref name="obj"/> is what it holds there.</summary>
    private static bool Covered(out IntPtr obj)
    {
        obj = IntPtr.Zero;
        return S.Grading && S.Cover != null && S.Cover(out obj);
    }

    /// <summary>The cursor's per-frame grading, with its picks marked as the cursor's for its length.</summary>
    [UnmanagedCallersOnly]
    private static int OnUpdate(IntPtr self, float dt)
    {
        S.Grading = true;
        try { return S.Update(self, dt); }
        finally { S.Grading = false; }
    }
}
