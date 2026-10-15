using CX.Framework;

namespace CX.Services.LightTrace;

/// <summary>The lighting trace's one declaration: <c>lighttrace.enabled</c>, off by default, a chat and
/// <c>cx.cfg</c> switch rather than a settings row -- it is a builder's diagnostic, not a player setting.</summary>
internal static class LightTraceKnobs
{
    internal static readonly KnobGroup G;

    /// <summary>Write every area-light change, its source and its values to <c>cx.log</c>.</summary>
    internal static readonly Knob<bool> On;

    static LightTraceKnobs()
    {
        G = KnobGroup.Declare("lighttrace", "Graphics", "Lighting", order: 320, title: "Lighting trace",
            master: "Trace every area-light change to cx.log", masterDefault: false, masterTier: Tier.ChatOnly,
            masterHelp: "Diagnostic for area builders: every light, direction and fog packet the server sends, "
                      + "every day/night state change and transition, the lightning flash, and once a second the "
                      + "colours the engine applied to the area's sun next to the ones Alloy rendered -- all to "
                      + "cx.log under the [lighttrace] prefix. Off by default; off costs nothing per frame.");
        On = G.Master!;
    }

    /// <summary>Touching the group runs its static constructor; the host calls this before any reader.</summary>
    internal static void Declare() { }
}
