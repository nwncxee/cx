using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.DayNight;

/// <summary>
/// Owned day/night. While the server holds <see cref="DayNightPolicy.Key"/>, the area's dawn and dusk
/// crossfade from what is on screen with no midpoint dip, a repeated or contradicting time-of-day state
/// neither re-cuts nor replays, and a sun-to-moon body change turns instead of popping. Unheld, every hook
/// passes straight through and vanilla's own shaping stands.
/// </summary>
public sealed unsafe class DayNightService : IExtenderService
{
    private static DayNightState _s = new();

    /// <summary>Chained below the trace, so the trace reports the values CX really applied.</summary>
    private const int ChainPriority = 40;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _s.Log = log;
        DayNightSelfCheck.Run(log);
        if (!EngineLayout.Require(log, "DayNight", "CNWCArea", "CNWCModule", "CClientExoApp")
            || !SymbolMap.Require(log, "DayNight", SymbolMap.AreaUpdateDayNightCycle, SymbolMap.AreaLoadArea,
                   SymbolMap.AreaSetAsDawn, SymbolMap.AreaSetAsDusk, SymbolMap.AreaSetAsDay, SymbolMap.AreaSetAsNight,
                   SymbolMap.AreaSetGlobalLightAmbient, SymbolMap.AreaSetGlobalLightDiffuse,
                   SymbolMap.AreaSetGlobalLightDirection, SymbolMap.AreaSetFog)) return;
        _s.Shadow = (float*)Native.Sym(SymbolMap.ShadowAlpha);
        _s.Projection = (float*)Native.Sym(SymbolMap.ProjectionAlpha);
        if (_s.Shadow == null || _s.Projection == null)
        {
            log.Info("DayNight: shadowalpha/projectionalpha did not resolve -- owned day/night stands down, vanilla shaping only");
            return;
        }
        _s.Next = hooks.ChainAreaUpdateDayNightCycle(&OnUpdateDayNightCycle, ChainPriority);
        _s.Load = hooks.ChainAreaLoadArea(&OnLoadArea, ChainPriority);
        _s.AsDawn = hooks.ChainAreaSetAsDawn(&OnSetAsDawn, ChainPriority);
        _s.AsDusk = hooks.ChainAreaSetAsDusk(&OnSetAsDusk, ChainPriority);
        _s.AsDay = hooks.ChainAreaSetAsDay(&OnSetAsDay, ChainPriority);
        _s.AsNight = hooks.ChainAreaSetAsNight(&OnSetAsNight, ChainPriority);
        bool ok = _s.Next != null && _s.Load != null && _s.AsDawn != null && _s.AsDusk != null
               && _s.AsDay != null && _s.AsNight != null;
        log.Info(ok
            ? $"DayNight: chained UpdateDayNightCycle, LoadArea and the four SetAs*; a server owns dawn/dusk with '{DayNightPolicy.Key}=1'"
            : "DayNight: a chain on the day/night machine was refused -- owned day/night stands down, vanilla shaping only");
    }

    /// <summary>The engine's own update runs first and keeps all of its bookkeeping -- skybox, sounds, tile
    /// animations, <c>isNight</c>, the transition flags. Owned mode then re-applies the light values alone,
    /// before anything renders.</summary>
    [UnmanagedCallersOnly]
    private static void OnUpdateDayNightCycle(IntPtr areaPtr, float dt)
    {
        (*_s.Next)(areaPtr, dt);
        DayNightState s = _s;
        if (!s.Owned) { s.Forget(); return; }
        if (areaPtr == IntPtr.Zero || s.InLoad) return;

        var area = new CNWCArea(areaPtr);
        DayNightPhase phase = area.Transition;
        CNWCModule mod = phase == DayNightPhase.None ? default : CNWCModule.Current;
        if (phase != DayNightPhase.None && mod.IsNull) return;

        var r = new DayNightReading
        {
            Area = areaPtr,
            Edge = s.Edge,
            Night = Palette(area.NightAmbient, area.NightDiffuse, area.NightFog, area.NightFogAmount),
            Day = Palette(area.DayAmbient, area.DayDiffuse, area.DayFog, area.DayFogAmount),
            MoonDir = Read(area.MoonDirection),
            SunDir = Read(area.SunDirection),
            Running = phase,
            IsNight = area.IsNight != 0,
            T = phase == DayNightPhase.None ? 0f : mod.TransitionProgress,
            Opacity = area.ShadowOpacity,
        };
        if (s.Edge || s.Memory.Area != areaPtr) { s.Edge = false; s.Said = 0; }

        DayNightStep step = DayNightMachine.Advance(in r, ref s.Memory, dt);
        if (step.HoldT >= 0f) mod.TransitionElapsed = step.HoldT * mod.TransitionLength;
        if (step.Push) Apply(area, in step.Frame, step.Direction, step.Aim, step.Shade);
        Diagnose(s, in step, r.T);
    }

    /// <summary>A new area in this object: nothing the machine remembers is still true, and the four
    /// <c>SetAs*</c> chains must not push the previous area's light into it.</summary>
    [UnmanagedCallersOnly]
    private static int OnLoadArea(IntPtr areaPtr, IntPtr camera, IntPtr loadscreen)
    {
        DayNightState s = _s;
        s.Edge = true;
        s.InLoad = true;
        try { return (*s.Load)(areaPtr, camera, loadscreen); }
        finally { s.InLoad = false; }
    }

    // The four palette cuts, cold. Each pushes a whole palette itself, so re-applying the held frame here
    // keeps the cut off every frame whatever order the loop pumps messages in; the crossfade to the new
    // palette then starts on the next update.

    [UnmanagedCallersOnly]
    private static void OnSetAsDawn(IntPtr area) { (*_s.AsDawn)(area); Hold(area); }

    [UnmanagedCallersOnly]
    private static void OnSetAsDusk(IntPtr area) { (*_s.AsDusk)(area); Hold(area); }

    [UnmanagedCallersOnly]
    private static void OnSetAsDay(IntPtr area) { (*_s.AsDay)(area); Hold(area); }

    [UnmanagedCallersOnly]
    private static void OnSetAsNight(IntPtr area) { (*_s.AsNight)(area); Hold(area); }

    /// <summary>Put back what was on screen. Silent during a load, on an area the machine has not picked up,
    /// and inside itself: <c>SetAsDawn</c> reaches <c>SetAsDay</c> on a non-cycle area, so it holds once.</summary>
    private static void Hold(IntPtr areaPtr)
    {
        DayNightState s = _s;
        if (s.InLoad || s.Holding || s.Edge || areaPtr == IntPtr.Zero) return;
        if (!s.Memory.Have || s.Memory.Area != areaPtr || !s.Owned) return;
        var area = new CNWCArea(areaPtr);
        s.Holding = true;
        try { Apply(area, in s.Memory.Applied, s.Memory.Direction, true, area.Transition != DayNightPhase.None); }
        finally { s.Holding = false; }
    }

    /// <summary>The held values, through the engine's own setters: the sun model's two colours, the fog pair,
    /// and the beam tint. <paramref name="shade"/> is false outside a transition, where the engine writes the
    /// shadow global once and the lightning flash owns its dimming.</summary>
    private static void Apply(CNWCArea area, in LightFrame frame, Vector3 direction, bool aim, bool shade)
    {
        Vec3 amb = Vec(frame.Light.Ambient), dif = Vec(frame.Light.Diffuse), fog = Vec(frame.Light.Fog);
        area.AreaSetGlobalLightAmbient(1, VectorArg.Of(&amb));
        area.AreaSetGlobalLightDiffuse(1, VectorArg.Of(&dif));
        area.AreaSetFog((int)frame.Light.FogAmount, VectorArg.Of(&fog));
        *_s.Projection = frame.ProjectionAlpha;
        if (shade) *_s.Shadow = frame.ShadowAlpha;
        if (!aim) return;
        Vec3 dir = Vec(direction);
        // The coarse flag the engine's own mid-transition calls pass: a static-projection rebuild at 5 degrees, not 1.
        area.AreaSetGlobalLightDirection(VectorArg.Of(&dir), 1);
    }

    /// <summary>Each kind of vanilla move owned mode absorbed, said once per area: what a live run needs to
    /// tell "the server's clocks disagree" from "the client is doing it wrong".</summary>
    private static void Diagnose(DayNightState s, in DayNightStep step, float t)
    {
        if (step.Cut && (s.Said & DayNightState.SaidCut) == 0)
        {
            s.Said |= DayNightState.SaidCut;
            s.Log?.Info($"DayNight: palette cut absorbed -- crossfading over {DayNightBlend.CutFade:F2}s instead");
        }
        if (step.Restart && (s.Said & DayNightState.SaidRestart) == 0)
        {
            s.Said |= DayNightState.SaidRestart;
            s.Log?.Info($"DayNight: transition clock rewound to t={t:F3}, held at t={step.HoldT:F3} -- the server sends two day/night clocks");
        }
        if (step.Flip && (s.Said & DayNightState.SaidFlip) == 0)
        {
            s.Said |= DayNightState.SaidFlip;
            s.Log?.Info("DayNight: time-of-day state flipped the area back mid-transition -- the blend ignored it");
        }
    }

    private static LightSet Palette(Span<float> ambient, Span<float> diffuse, Span<float> fog, byte amount) => new()
    {
        Ambient = Read(ambient),
        Diffuse = Read(diffuse),
        Fog = Read(fog),
        FogAmount = amount,
    };

    private static Vector3 Read(Span<float> v) => new(v[0], v[1], v[2]);

    private static Vec3 Vec(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
}
