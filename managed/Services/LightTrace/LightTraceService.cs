using System;
using CX.Alloy;
using CX.Framework;

namespace CX.Services.LightTrace;

/// <summary>
/// The lighting trace: one knob, and a chain on every leaf the engine changes an area's light through, so
/// "the nights are too dark" becomes a log showing what the server sent, what the engine applied and what
/// Alloy rendered. Off by default, and off is one bool test on the per-frame paths.
/// </summary>
public sealed unsafe class LightTraceService : IExtenderService
{
    private static readonly LightTraceState St = new();

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        St.Log = log;
        LightTraceKnobs.Declare();
        LightTraceLines.SelfCheck(log);
        LightTraceKnobs.G.Status = Status;
        FrameRegistry.Global.Register(FramePhase.PostPresent, 90, Sample, "LightTrace.sample", log);
    }

    private static string Status()
        => LightTraceKnobs.On.V
            ? $"lighttrace: on, {St.Lines} line(s) written; grep cx.log for {LightTraceLines.Tag}"
            : $"lighttrace: off ({St.Lines} line(s) written this session)";

    /// <summary>Write one trace line. Cold by construction: every caller is a packet, a state change or a
    /// once-a-second sample, so the format is allowed to allocate.</summary>
    private static void Say(string line)
    {
        St.Lines++;
        St.Log?.Always(line);
    }

    /// <summary>The two sampled triples, once a frame. When the trace is off this is one field read and a
    /// return: nothing is formatted, nothing allocates and no engine memory is touched.</summary>
    private static void Sample()
    {
        if (!LightTraceKnobs.On.V) return;
        long now = Environment.TickCount64;
        if (LightTraceLines.Due(now, St.AppliedAt, in St.AppliedSent, in St.Applied))
        {
            St.AppliedSent = St.Applied;
            St.AppliedAt = now;
            Say(LightTraceLines.Applied(in St.Applied));
        }
        var alloy = new LightTriple(FrameInputs.HasAreaAmbient ? FrameInputs.AreaAmbient : default,
                                    FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuse : default,
                                    FrameInputs.HasSun ? FrameInputs.SunDir : default);
        St.Alloy = alloy;
        if (LightTraceLines.Due(now, St.AlloyAt, in St.AlloySent, in alloy))
        {
            St.AlloySent = alloy;
            St.AlloyAt = now;
            Say(LightTraceLines.Alloy(in alloy));
        }
    }
}
