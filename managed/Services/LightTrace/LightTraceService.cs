using System;
using CX.Alloy;
using CX.Framework;

namespace CX.Services.LightTrace;

/// <summary>
/// The lighting trace, off by default: a chain on every leaf an area's light moves through, so "the nights
/// are too dark" becomes a log of what the server sent, what the engine applied and what Alloy rendered. The
/// APPLIED values exist only inside <c>SetGlobalLight*</c> and <c>SetFog</c>, and the transition and flash
/// edges only in <c>CNWCArea::Update</c>'s state, so those chains record and leave the format to the sampler.
/// </summary>
public sealed unsafe partial class LightTraceService : IExtenderService
{
    private static readonly LightTraceState St = new();

    /// <summary>The one gate every handler opens with: the knob, and a trace that actually installed.</summary>
    private static bool On => LightTraceKnobs.On.V && St.Armed;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        St.Log = log;
        LightTraceKnobs.Declare();
        LightTraceLines.SelfCheck(log);
        LightTraceKnobs.G.Status = Status;
        LightTraceKnobs.On.Changed = Armed;
        FrameRegistry.Global.Register(FramePhase.PostPresent, 90, Sample, "LightTrace.sample", log);
        Hook(log, hooks);
    }

    /// <summary>The knob moved: forget every sampled value, so the first line after a switch-on is one the
    /// engine really pushed rather than a default, and say in the log where the trace begins.</summary>
    private static void Armed()
    {
        St.AppliedSeen = false; St.FogSeen = false; St.Flashing = false;
        St.AppliedAt = -1; St.AlloyAt = -1; St.FogAt = -1; St.ShadowAt = -1;
        if (!St.Armed) { St.Log?.Always($"{LightTraceLines.Tag} unavailable -- {St.Why}"); return; }
        Say(LightTraceKnobs.On.V ? $"{LightTraceLines.Tag} on -- every area-light change from here"
                                 : $"{LightTraceLines.Tag} off");
    }

    private static string Status()
        => !St.Armed ? $"lighttrace: unavailable -- {St.Why}"
         : LightTraceKnobs.On.V ? $"lighttrace: on, {St.Lines} line(s) written; grep cx.log for {LightTraceLines.Tag}"
         : $"lighttrace: off ({St.Lines} line(s) written this session)";

    /// <summary>Write one trace line. Cold by construction: every caller is a packet, a state change or a
    /// once-a-second sample, so the format is allowed to allocate.</summary>
    private static void Say(string line)
    {
        St.Lines++;
        St.Log?.Always(line);
    }

    /// <summary>The sampled values, once a frame: what the engine applied to the area's sun, the fog it
    /// pushed, and what Alloy rendered. When the trace is off this is one field read and a return -- nothing
    /// is formatted, nothing allocates and no engine memory is touched.</summary>
    private static void Sample()
    {
        if (!On) return;
        long now = Environment.TickCount64;

        var applied = new LightTriple(St.AppliedAmbient, St.AppliedDiffuse, St.AppliedDir);
        if (St.AppliedSeen && LightTraceLines.Due(now, St.AppliedAt, in St.AppliedSent, in applied))
        {
            St.AppliedSent = applied;
            St.AppliedAt = now;
            Say(LightTraceLines.Applied(in applied));
        }

        if (St.FogSeen && LightTraceLines.DueFog(now, St.FogAt, St.FogSent, St.Fog, St.FogRgbSent, St.FogRgb))
        {
            St.FogSent = St.Fog;
            St.FogRgbSent = St.FogRgb;
            St.FogAt = now;
            Say(LightTraceLines.AppliedFog(St.Fog, St.FogRgb));
        }

        var alloy = new LightTriple(FrameInputs.HasAreaAmbient ? FrameInputs.AreaAmbient : default,
                                    FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuse : default,
                                    FrameInputs.HasSun ? FrameInputs.SunDir : default);
        if (LightTraceLines.Due(now, St.AlloyAt, in St.AlloySent, in alloy))
        {
            St.AlloySent = alloy;
            St.AlloyAt = now;
            Say(LightTraceLines.Alloy(in alloy));
        }
    }
}
