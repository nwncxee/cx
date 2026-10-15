using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.LightTrace;

public sealed unsafe partial class LightTraceService
{
    /// <summary>Outermost on every shared leaf: the trace reports the arguments as they ARRIVE, so a layer
    /// that rewrites them sits inside it and the log shows the server's own values.</summary>
    private const int TracePriority = 1000;

    private static readonly IntPtr AppMgr = Native.Sym(SymbolMap.AppManager);
    private static readonly GetModuleFn GetModule = EngineFn.GetModule;

    /// <summary>Chain every leaf an area's light can change through. Each handler is a knob test, a format on
    /// the cold paths, and a call through its slot.</summary>
    private static bool Hook(ExtenderLog log, HookService hooks)
    {
        if (!EngineLayout.Require(log, "LightTrace", "CNWCArea", "CNWCModule")) return false;
        if (!SymbolMap.Require(log, "LightTrace", SymbolMap.AppManager, SymbolMap.GetModule,
                               SymbolMap.AreaSetAreaLightColor, SymbolMap.AreaSetAreaLightDirection,
                               SymbolMap.AreaSetFogColors, SymbolMap.AreaSetFogAmount,
                               SymbolMap.AreaSetGlobalLightAmbient, SymbolMap.AreaSetGlobalLightDiffuse,
                               SymbolMap.AreaSetGlobalLightDirection, SymbolMap.ModuleSetTimeOfDayState,
                               SymbolMap.AreaSetAsDawn, SymbolMap.AreaSetAsDusk, SymbolMap.AreaSetAsDay,
                               SymbolMap.AreaSetAsNight, SymbolMap.AreaUpdate, SymbolMap.AreaLoadArea,
                               SymbolMap.HandleServerToPlayerArea, SymbolMap.ShadowAlpha)) return false;

        St.ShadowAlpha = (float*)Native.Sym(SymbolMap.ShadowAlpha);

        St.Colour = hooks.ChainAreaSetAreaLightColor(&OnAreaLightColor, TracePriority);
        St.Direction = hooks.ChainAreaSetAreaLightDirection(&OnAreaLightDirection, TracePriority);
        St.FogColours = hooks.ChainAreaSetFogColors(&OnFogColors, TracePriority);
        St.FogAmount = hooks.ChainAreaSetFogAmount(&OnFogAmount, TracePriority);
        St.GlobalAmbient = hooks.ChainAreaSetGlobalLightAmbient(&OnGlobalAmbient, TracePriority);
        St.GlobalDiffuse = hooks.ChainAreaSetGlobalLightDiffuse(&OnGlobalDiffuse, TracePriority);
        St.GlobalDirection = hooks.ChainAreaSetGlobalLightDirection(&OnGlobalDirection, TracePriority);
        St.TimeOfDay = hooks.ChainModuleSetTimeOfDayState(&OnTimeOfDayState, TracePriority);
        St.AsDawn = hooks.ChainAreaSetAsDawn(&OnAsDawn, TracePriority);
        St.AsDusk = hooks.ChainAreaSetAsDusk(&OnAsDusk, TracePriority);
        St.AsDay = hooks.ChainAreaSetAsDay(&OnAsDay, TracePriority);
        St.AsNight = hooks.ChainAreaSetAsNight(&OnAsNight, TracePriority);
        St.Update = hooks.ChainAreaUpdate(&OnAreaUpdate, TracePriority);
        St.LoadArea = hooks.ChainAreaLoadArea(&OnLoadArea, TracePriority);
        St.AreaMessage = hooks.ChainHandleServerToPlayerArea(&OnAreaMessage, TracePriority);

        bool ok = St.Colour != null && St.Direction != null && St.FogColours != null && St.FogAmount != null
               && St.GlobalAmbient != null && St.GlobalDiffuse != null && St.GlobalDirection != null
               && St.TimeOfDay != null && St.AsDawn != null && St.AsDusk != null && St.AsDay != null
               && St.AsNight != null && St.Update != null && St.LoadArea != null && St.AreaMessage != null
               && St.ShadowAlpha != null;
        log.Info(ok ? "LightTrace: chained the day/night machine's 15 leaves"
                    : "LightTrace: standing down -- a chain on the day/night machine was refused");
        return ok;
    }

    /// <summary>The client module, which holds the transition clock. Two pointer loads and the engine's own
    /// getter; read once per frame while the trace is on, never while it is off.</summary>
    private static CNWCModule Module()
    {
        IntPtr mgr = AppMgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)AppMgr;
        IntPtr app = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
        return new CNWCModule(app == IntPtr.Zero ? IntPtr.Zero : GetModule(app));
    }

    private static Vector3 Vec(VectorArg a)
    {
        Vec3 v = a.Value;
        return new Vector3(v.X, v.Y, v.Z);
    }

    // ---- the four packets (cold: one server message each) --------------------------------------------

    [UnmanagedCallersOnly]
    private static void OnAreaLightColor(nint area, uint slot, VectorArg colour, float fade)
    {
        if (On) Say(LightTraceLines.Colour(slot, Vec(colour), fade, new CNWCArea(area).Id));
        (*St.Colour)(area, slot, colour, fade);
    }

    [UnmanagedCallersOnly]
    private static void OnAreaLightDirection(nint area, uint which, VectorArg dir, float fade)
    {
        if (On) Say(LightTraceLines.Direction(which, Vec(dir), fade, new CNWCArea(area).Id));
        (*St.Direction)(area, which, dir, fade);
    }

    [UnmanagedCallersOnly]
    private static void OnFogColors(nint area, VectorArg sun, VectorArg moon, float fade)
    {
        if (On) Say(LightTraceLines.FogColour(Vec(sun), Vec(moon), fade, new CNWCArea(area).Id));
        (*St.FogColours)(area, sun, moon, fade);
    }

    /// <summary>The fog amount, which the dawn/dusk crossfade also pushes every frame: the value is recorded
    /// and the sampler decides whether a line is due.</summary>
    [UnmanagedCallersOnly]
    private static int OnFogAmount(nint area, int amount)
    {
        if (On) { St.Fog = amount; St.FogSeen = true; }
        return (*St.FogAmount)(area, amount);
    }

    // ---- what reaches gidy_sun (per frame across a transition: recorded, never formatted here) --------

    [UnmanagedCallersOnly]
    private static int OnGlobalAmbient(nint area, int apply, VectorArg colour)
    {
        if (On) { St.AppliedAmbient = Vec(colour); St.AppliedSeen = true; }
        return (*St.GlobalAmbient)(area, apply, colour);
    }

    [UnmanagedCallersOnly]
    private static int OnGlobalDiffuse(nint area, int apply, VectorArg colour)
    {
        // apply 0 hides the sun outright, so the colour it carries is not what the area is lit by.
        if (On) { St.AppliedDiffuse = apply == 0 ? Vector3.Zero : Vec(colour); St.AppliedSeen = true; }
        return (*St.GlobalDiffuse)(area, apply, colour);
    }

    [UnmanagedCallersOnly]
    private static int OnGlobalDirection(nint area, VectorArg dir, byte coarse)
    {
        if (On) { St.AppliedDir = Vec(dir); St.AppliedSeen = true; }
        return (*St.GlobalDirection)(area, dir, coarse);
    }

    // ---- the state machine (cold) --------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static void OnTimeOfDayState(nint module, byte state, uint elapsedMs, int force)
    {
        if (On) Say(LightTraceLines.TimeOfDay(state, elapsedMs, force));
        (*St.TimeOfDay)(module, state, elapsedMs, force);
    }

    [UnmanagedCallersOnly]
    private static void OnAsDawn(nint area) { SetAs("dawn", area); (*St.AsDawn)(area); }

    [UnmanagedCallersOnly]
    private static void OnAsDusk(nint area) { SetAs("dusk", area); (*St.AsDusk)(area); }

    [UnmanagedCallersOnly]
    private static void OnAsDay(nint area) { SetAs("day", area); (*St.AsDay)(area); }

    [UnmanagedCallersOnly]
    private static void OnAsNight(nint area) { SetAs("night", area); (*St.AsNight)(area); }

    private static void SetAs(string which, nint area)
    {
        if (!On) return;
        var a = new CNWCArea(area);
        Say(LightTraceLines.SetAs(which, a.Id, a.UsesDayNightCycle, a.IsNight));
    }

    [UnmanagedCallersOnly]
    private static int OnLoadArea(nint area, nint camera, nint loadscreen)
    {
        int r = (*St.LoadArea)(area, camera, loadscreen);
        if (On)
        {
            var a = new CNWCArea(area);
            Say(LightTraceLines.Loaded(a.Id, a.UsesDayNightCycle, a.IsNight));
        }
        return r;
    }

    /// <summary>The area message's minor, named before the switch runs: the colour, direction and fog leaves
    /// all drop a packet whose area id is not the current one, so the pair of lines tells them apart.</summary>
    [UnmanagedCallersOnly]
    private static int OnAreaMessage(nint msg, byte minor)
    {
        if (On && LightTraceLines.MinorName(minor) is string name) Say(LightTraceLines.Minor(minor, name));
        return (*St.AreaMessage)(msg, minor);
    }

    // ---- the per-frame edges -------------------------------------------------------------------------

    /// <summary>The area's own step, which runs the crossfade and the lightning flash. HOT: off, this is one
    /// field read and the call on; on, it reads the transition flags and the shadow tint and formats only at
    /// an edge.</summary>
    [UnmanagedCallersOnly]
    private static void OnAreaUpdate(nint area, float dt)
    {
        (*St.Update)(area, dt);
        if (!On) return;
        Edges(area);
    }

    private static void Edges(nint area)
    {
        var a = new CNWCArea(area);
        string? running = a.DawnRunning != 0 ? "dawn" : a.DuskRunning != 0 ? "dusk" : null;
        long now = Environment.TickCount64;
        CNWCModule m = Module();
        float t = !m.IsNull && m.TransitionLength > 0f ? m.TransitionElapsed / m.TransitionLength : 0f;
        if (running != St.Transition)
        {
            if (running != null)
                Say(LightTraceLines.TransitionStart(running, m.IsNull ? 0f : m.TransitionLength));
            else if (St.Transition != null)
                Say(LightTraceLines.TransitionEnd(St.Transition, (now - St.TransitionAt) / 1000f));
            St.Transition = running;
            St.TransitionAt = now;
        }

        float alpha = *St.ShadowAlpha;
        float expected = LightTraceLines.ShadowExpected(a.ShadowOpacity, t, running != null);
        bool flashing = LightTraceLines.Flashing(alpha, expected);
        if (flashing != St.Flashing)
        {
            St.Flashing = flashing;
            Say(LightTraceLines.Lightning(flashing));
        }
        if (LightTraceLines.DueScalar(now, St.ShadowAt, St.ShadowSent, alpha, ShadowEps))
        {
            St.ShadowSent = alpha;
            St.ShadowAt = now;
            Say(LightTraceLines.Shadow(alpha, a.ShadowOpacity, Cause(running, t, flashing)));
        }
    }

    /// <summary>One percent of full opacity: finer than any shadow change worth a line.</summary>
    private const float ShadowEps = 0.01f;

    /// <summary>Why the shadow tint is where it is, for the line that reports it.</summary>
    private static string Cause(string? running, float t, bool flashing)
        => flashing ? "lightning flash"
         : running != null ? FormattableString.Invariant($"transition {running} t={t:F2}")
         : "steady";
}
