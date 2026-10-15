using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.DayNight;

/// <summary>
/// Owned day/night. While the server holds <see cref="DayNightPolicy.Key"/>, the area's dawn and dusk
/// crossfade from what is on screen with no midpoint dip, a repeated or contradicting time-of-day state
/// neither re-cuts nor replays, and a sun-to-moon body change turns instead of popping. Unheld, the hook
/// passes straight through and vanilla's own shaping stands.
/// </summary>
public sealed unsafe class DayNightService : IExtenderService
{
    private static DayNightState _s = new();

    /// <summary>Chained below the default, so a trace layered above reports the values CX really applied.</summary>
    private const int ChainPriority = 40;
    private const int SaidCut = 1, SaidRestart = 2, SaidFlip = 4;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _s.Log = log;
        DayNightSelfCheck.Run(log);
        if (!EngineLayout.Require(log, "DayNight", "CNWCArea", "CNWCModule", "CClientExoApp")
            || !SymbolMap.Require(log, "DayNight", SymbolMap.AreaUpdateDayNightCycle, SymbolMap.AppManager,
                   SymbolMap.AreaSetGlobalLightAmbient, SymbolMap.AreaSetGlobalLightDiffuse,
                   SymbolMap.AreaSetGlobalLightDirection, SymbolMap.AreaSetFog)) return;
        _s.Shadow = (float*)Native.Sym(SymbolMap.ShadowAlpha);
        _s.Projection = (float*)Native.Sym(SymbolMap.ProjectionAlpha);
        _s.AppMgr = Native.Sym(SymbolMap.AppManager);
        if (_s.Shadow == null || _s.Projection == null)
        {
            log.Info("DayNight: shadowalpha/projectionalpha did not resolve -- owned day/night stands down, vanilla shaping only");
            return;
        }
        _s.Next = hooks.ChainAreaUpdateDayNightCycle(&OnUpdateDayNightCycle, ChainPriority);
        log.Info(_s.Next == null
            ? "DayNight: UpdateDayNightCycle chain refused -- owned day/night stands down, vanilla shaping only"
            : $"DayNight: chained CNWCArea::UpdateDayNightCycle; a server owns dawn/dusk with '{DayNightPolicy.Key}=1'");
    }

    /// <summary>The engine's own update runs first and keeps all of its bookkeeping -- skybox, sounds, tile
    /// animations, <c>isNight</c>, the transition flags. Owned mode then re-applies the light values alone,
    /// before anything renders.</summary>
    [UnmanagedCallersOnly]
    private static void OnUpdateDayNightCycle(IntPtr areaPtr, float dt)
    {
        (*_s.Next)(areaPtr, dt);
        DayNightState s = _s;
        if (!s.Owned) { s.Memory.Have = false; s.Said = 0; return; }
        if (areaPtr == IntPtr.Zero) return;

        var area = new CNWCArea(areaPtr);
        Phase phase = area.DawnRunning != 0 ? Phase.Dawn : area.DuskRunning != 0 ? Phase.Dusk : Phase.None;
        CNWCModule mod = phase == Phase.None ? default : Module(s);
        float t = 0f;
        if (phase != Phase.None)
        {
            if (mod.IsNull) return;
            float len = mod.TransitionLength;
            t = len > 0f ? mod.TransitionElapsed / len : 1f;
        }

        var r = new DayNightReading
        {
            Area = areaPtr,
            Night = Palette(area.NightAmbient, area.NightDiffuse, area.NightFog, area.NightFogAmount),
            Day = Palette(area.DayAmbient, area.DayDiffuse, area.DayFog, area.DayFogAmount),
            MoonDir = Read(area.MoonDirection),
            SunDir = Read(area.SunDirection),
            Running = phase,
            IsNight = area.IsNight != 0,
            T = t,
            Opacity = area.ShadowOpacity,
        };

        if (s.Memory.Area != areaPtr) s.Said = 0;
        DayNightStep step = DayNightMachine.Advance(in r, ref s.Memory, dt);
        if (step.HoldT >= 0f && !mod.IsNull) mod.TransitionElapsed = step.HoldT * mod.TransitionLength;
        if (step.Push) Apply(area, in step);
        Diagnose(s, in step, t);
    }

    /// <summary>The blended values, through the engine's own setters: the sun model's two colours, the fog
    /// pair, and the two render globals. The direction only moves while a transition or a cut fade runs.</summary>
    private static void Apply(CNWCArea area, in DayNightStep step)
    {
        Vec3 amb = Vec(step.Frame.Light.Ambient), dif = Vec(step.Frame.Light.Diffuse), fog = Vec(step.Frame.Light.Fog);
        area.AreaSetGlobalLightAmbient(1, VectorArg.Of(&amb));
        area.AreaSetGlobalLightDiffuse(1, VectorArg.Of(&dif));
        area.AreaSetFog((int)step.Frame.Light.FogAmount, VectorArg.Of(&fog));
        *_s.Shadow = step.Frame.ShadowAlpha;
        *_s.Projection = step.Frame.ProjectionAlpha;
        if (!step.Aim) return;
        Vec3 dir = Vec(step.Direction);
        // The coarse flag the engine's own mid-transition calls pass: a static-projection rebuild at 5 degrees, not 1.
        area.AreaSetGlobalLightDirection(VectorArg.Of(&dir), 1);
    }

    /// <summary>Each kind of vanilla move owned mode absorbed, said once per area: what a live run needs to
    /// tell "the server's clocks disagree" from "the client is doing it wrong".</summary>
    private static void Diagnose(DayNightState s, in DayNightStep step, float t)
    {
        if (step.Cut && (s.Said & SaidCut) == 0)
        {
            s.Said |= SaidCut;
            s.Log?.Info($"DayNight: palette cut absorbed -- crossfading over {DayNightBlend.CutFade:F2}s instead");
        }
        if (step.Restart && (s.Said & SaidRestart) == 0)
        {
            s.Said |= SaidRestart;
            s.Log?.Info($"DayNight: transition clock rewound to t={t:F3}, held at t={step.HoldT:F3} -- the server sends two day/night clocks");
        }
        if (step.Flip && (s.Said & SaidFlip) == 0)
        {
            s.Said |= SaidFlip;
            s.Log?.Info("DayNight: time-of-day state flipped the area back mid-transition -- the blend ignored it");
        }
    }

    private static CNWCModule Module(DayNightState s)
    {
        if (s.AppMgr == IntPtr.Zero) return default;
        IntPtr mgr = *(IntPtr*)s.AppMgr;
        IntPtr app = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
        return app == IntPtr.Zero ? default : new CClientExoApp(app).GetModule();
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
