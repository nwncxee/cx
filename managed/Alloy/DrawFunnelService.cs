using CX.Framework;
namespace CX.Alloy;

/// <summary>Who owns the engine's draw hooks, decided once at boot by <c>alloy.drawhooks</c>: <c>batch</c> (the
/// default) is Alloy, <c>stats</c> hands them to <c>DrawStatsService</c>'s histogram, anything else is neither.
/// One mode, because both hook the same symbols. Also hosts two boot-time pieces that must run ahead of every
/// stand-down: the frame tap and the text-layout self-check.</summary>
public sealed class DrawFunnelService : IExtenderService
{
    /// <summary>The <c>cx.cfg</c> values of <c>alloy.drawhooks</c>. <c>batch</c> keeps its name though the
    /// batcher is gone: the key is a contract with existing installs (ADR 0013).</summary>
    internal const string DrawModeBatch = "batch", DrawModeStats = "stats";

    /// <summary>Alloy owns the draw hooks this session (Alloy requested and <c>drawhooks=batch</c>). False is an
    /// <see cref="AlloyGate"/> refusal.</summary>
    internal static bool Armed { get; private set; }

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        FrameTap.Install(log);   // diagnostic; ahead of the stand-down so a vanilla session can snap the same way
        if (!Renderer.On)
        { log.Info("DrawFunnels: standing down (alloy.enabled=0) -- the engine renders this session"); return; }
        string mode = Renderer.DrawHooks.Boot;
        if (mode != DrawModeBatch)
        { log.Info($"DrawFunnels: standing down (cx.cfg alloy.drawhooks={mode})"); return; }
        // The StringGob/CAurFont layout port is pure arithmetic over synthetic and real font metrics, so a
        // headless run is the only place it is ever checked.
        Db.TextObjectsSelfCheck.SelfCheck(log);
        // The engine reads the frame still makes: the keyhole dissolve, the bumpy-water gate, material accessors.
        if (!PartTable.Resolve())
        { log.Info("DrawFunnels: engine symbols unresolved -- Alloy unavailable (headless/Windows?)"); return; }
        Armed = true;
    }
}
