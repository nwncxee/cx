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

    /// <summary>One percent of full opacity: finer than any shadow change worth a line.</summary>
    private const float ShadowEps = 0.01f;

    /// <summary>Chain every leaf an area's light can change through, and arm the trace only if every one of
    /// them took. Each handler is a knob test, a format on the cold paths, and a call through its slot.</summary>
    private static void Hook(ExtenderLog log, HookService hooks)
    {
        if (!EngineLayout.Require(log, "LightTrace", "CNWCArea", "CNWCModule")) { Refuse(log, "engine offsets"); return; }
        if (!SymbolMap.Require(log, "LightTrace", SymbolMap.AppManager, SymbolMap.GetModule,
                               SymbolMap.AreaSetAreaLightColor, SymbolMap.AreaSetAreaLightDirection,
                               SymbolMap.AreaSetFogColors, SymbolMap.AreaSetFogAmount, SymbolMap.AreaSetFog,
                               SymbolMap.AreaSetGlobalLightAmbient, SymbolMap.AreaSetGlobalLightDiffuse,
                               SymbolMap.AreaSetGlobalLightDirection, SymbolMap.ModuleSetTimeOfDayState,
                               SymbolMap.AreaSetAsDawn, SymbolMap.AreaSetAsDusk, SymbolMap.AreaSetAsDay,
                               SymbolMap.AreaSetAsNight, SymbolMap.AreaUpdate, SymbolMap.AreaLoadArea,
                               SymbolMap.HandleServerToPlayerArea, SymbolMap.ShadowAlpha))
        { Refuse(log, "engine symbols"); return; }

        St.ShadowAlpha = (float*)Native.Sym(SymbolMap.ShadowAlpha);

        St.Colour = hooks.ChainAreaSetAreaLightColor(&OnAreaLightColor, TracePriority);
        St.Direction = hooks.ChainAreaSetAreaLightDirection(&OnAreaLightDirection, TracePriority);
        St.FogColours = hooks.ChainAreaSetFogColors(&OnFogColors, TracePriority);
        St.FogAmount = hooks.ChainAreaSetFogAmount(&OnFogAmount, TracePriority);
        St.FogPush = hooks.ChainAreaSetFog(&OnFogPush, TracePriority);
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
               && St.FogPush != null && St.GlobalAmbient != null && St.GlobalDiffuse != null
               && St.GlobalDirection != null && St.TimeOfDay != null && St.AsDawn != null && St.AsDusk != null
               && St.AsDay != null && St.AsNight != null && St.Update != null && St.LoadArea != null
               && St.AreaMessage != null && St.ShadowAlpha != null;
        if (!ok) { Refuse(log, "a chain on the day/night machine"); return; }
        St.Armed = true;
        St.Why = "";
        log.Info("LightTrace: chained the day/night machine's 16 leaves");
    }

    /// <summary>The trace cannot run: nothing the knob does would produce a line, so say which half refused
    /// and keep the reason for the status line.</summary>
    private static void Refuse(ExtenderLog log, string what)
    {
        St.Armed = false;
        St.Why = what + " did not resolve on this client build";
        log.Always($"LightTrace: standing down -- {St.Why}");
    }

    private static Vector3 Vec(VectorArg a)
    {
        Vec3 v = a.Value;
        return new Vector3(v.X, v.Y, v.Z);
    }

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

    /// <summary>The fog amount on its own, which re-pushes the area's stored colour. The packet that caused it
    /// is traced as its minor, so this records the APPLIED amount for the sampler and prints nothing.</summary>
    [UnmanagedCallersOnly]
    private static int OnFogAmount(nint area, int amount)
    {
        if (On) { St.Fog = amount; St.FogSeen = true; }
        return (*St.FogAmount)(area, amount);
    }

    /// <summary>The one push of fog to the scene, which the dawn/dusk crossfade and CX's own blend both use:
    /// amount AND colour, every frame across a transition, so it is recorded and never formatted here.</summary>
    [UnmanagedCallersOnly]
    private static int OnFogPush(nint area, int amount, VectorArg colour)
    {
        if (On) { St.Fog = amount; St.FogRgb = Vec(colour); St.FogSeen = true; }
        return (*St.FogPush)(area, amount, colour);
    }

    /// <summary>The ambient that reaches <c>gidy_sun</c>, pushed every frame across a transition: recorded for
    /// the sampler, never formatted here.</summary>
    [UnmanagedCallersOnly]
    private static int OnGlobalAmbient(nint area, int apply, VectorArg colour)
    {
        if (On) { St.AppliedAmbient = Vec(colour); St.AppliedSeen = true; }
        return (*St.GlobalAmbient)(area, apply, colour);
    }

    /// <inheritdoc cref="OnGlobalAmbient"/>
    [UnmanagedCallersOnly]
    private static int OnGlobalDiffuse(nint area, int apply, VectorArg colour)
    {
        // apply 0 hides the sun outright, so the colour it carries is not what the area is lit by.
        if (On) { St.AppliedDiffuse = apply == 0 ? Vector3.Zero : Vec(colour); St.AppliedSeen = true; }
        return (*St.GlobalDiffuse)(area, apply, colour);
    }

    /// <inheritdoc cref="OnGlobalAmbient"/>
    [UnmanagedCallersOnly]
    private static int OnGlobalDirection(nint area, VectorArg dir, byte coarse)
    {
        if (On) { St.AppliedDir = Vec(dir); St.AppliedSeen = true; }
        return (*St.GlobalDirection)(area, dir, coarse);
    }

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

    /// <summary>The area's own step, which runs the crossfade and the lightning flash. HOT: off, this is one
    /// field read and the call on; on, it reads the state the engine just wrote and formats only at an edge or
    /// once a second.</summary>
    [UnmanagedCallersOnly]
    private static void OnAreaUpdate(nint area, float dt)
    {
        (*St.Update)(area, dt);
        if (!On) return;
        Edges(area);
    }

    /// <summary>The two edges the engine leaves in its own state, read after it has stepped: the dawn/dusk
    /// flags with the module's clock, and the flash timer, which is non-zero exactly while a flash runs -- so
    /// neither edge depends on what anything did to the shadow tint.</summary>
    private static void Edges(nint area)
    {
        var a = new CNWCArea(area);
        DayNightPhase running = a.Transition;
        long now = Environment.TickCount64;
        CNWCModule m = CNWCModule.Current;
        if (running != St.Transition)
        {
            Say(running != DayNightPhase.None
                ? LightTraceLines.TransitionStart(running, m.IsNull ? 0f : m.TransitionLength)
                : LightTraceLines.TransitionEnd(St.Transition, (now - St.TransitionAt) / 1000f));
            St.Transition = running;
            St.TransitionAt = now;
        }

        bool flashing = a.LightningFlash > 0f;
        if (flashing != St.Flashing)
        {
            St.Flashing = flashing;
            Say(LightTraceLines.Lightning(flashing));
        }

        float alpha = *St.ShadowAlpha;
        if (LightTraceLines.DueScalar(now, St.ShadowAt, St.ShadowSent, alpha, ShadowEps))
        {
            St.ShadowSent = alpha;
            St.ShadowAt = now;
            float t = m.IsNull ? 0f : m.TransitionProgress;
            Say(LightTraceLines.Shadow(alpha, a.ShadowOpacity, Cause(running, t, flashing)));
        }
    }

    /// <summary>Why the shadow tint is where it is, for the line that reports it.</summary>
    private static string Cause(DayNightPhase running, float t, bool flashing)
        => flashing ? "lightning flash"
         : running != DayNightPhase.None
             ? FormattableString.Invariant($"transition {LightTraceLines.PhaseName(running)} t={t:F2}")
             : "steady";
}
