using System;

namespace CX.Framework;

/// <summary>
/// The General category: the <c>general</c> group (command gate, live log toggle, boot forensics) and the
/// <c>about</c> group (what this build is, the releases link, the row that copies the knob surface for a bug
/// report). Declared on first touch -- <see cref="HookService"/>'s constructor reads <see cref="HookDebug"/> right
/// after <see cref="Settings.Load"/> -- so it is the first group in the walk.
/// </summary>
public static class General
{
    public static readonly KnobGroup G;
    /// <summary>Off by default: every CX command but <c>/settings</c> then goes to the server as chat.</summary>
    public static readonly Knob<bool> Commands;
    /// <summary>Live twin of <see cref="ExtenderLog.Enabled"/>; <see cref="Settings.Load"/> reads the same key
    /// for the launch lines, before any knob exists.</summary>
    public static readonly Knob<bool> Logs;
    public static readonly Knob<bool> HookDebug;
    public static readonly Knob<int> PresentProbe, Pin;
    /// <summary>Debug: log the frame-time percentiles, and what model views cost in CPU and GPU, every few seconds.</summary>
    public static readonly Knob<bool> FrameTimes;
    /// <summary>Seconds after the first present at which this run halves the window's width; twice that is where it
    /// puts the width back, and 0 = off. A DEBUG trigger for the engine's own re-layout path
    /// (<c>SDL_SetWindowSize</c> -&gt; <c>SDL_WINDOWEVENT_SIZE_CHANGED</c> -&gt; <c>CGuiMan::SetGuiSize</c>), because
    /// the live harness runs in a headless compositor with no window edge to drag.</summary>
    public static readonly Knob<int> ResizeAt;

    public static readonly KnobGroup About;

    static General()
    {
        G = KnobGroup.Declare("general", "General", "General", order: 100,
            header: "No master switch: every feature has its own, and \"no CX\" is an uninstall.");
        Commands = G.Toggle("commands", "Enable chat commands", false,
            help: "Lets /grass, /shadow and the other CX commands work in chat. Off, they go to the server like any "
                + "other text — some servers use the same names. /settings always works.");
        Logs = G.Toggle("logs", "Write cx.log for bug reports", false, Tier.Advanced,
            help: "Writes what CX is doing to cx.log, beside cx.cfg, from now on; restart to capture the launch "
                + "lines too. Attach the file to a bug report.",
            changed: () => ExtenderLog.Enabled = Logs!.V);
        HookDebug = G.Toggle("hookdebug", "hookdebug", false, Tier.ChatOnly, Effect.Restart);
        PresentProbe = G.Slider("presentprobe", "presentprobe", 0, 200, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
        FrameTimes = G.Toggle("frametimes", "frametimes", false, Tier.ChatOnly,
            help: "Debug: every few seconds, log p50, p95, p99 and max of the frame time and of the CPU model views took, and the GPU time of the GUI replay.");
        Pin = G.Slider("pin", "pin", -1, 1023, 1, -1, tier: Tier.ChatOnly, effect: Effect.Restart);
        ResizeAt = G.Slider("resizeat", "resizeat", 0, 600, 1, 0, unit: "s",
            tier: Tier.ChatOnly, effect: Effect.Restart,
            help: "Debug: halve the window's width this many seconds in, and restore it at twice that, through "
                + "SDL and the engine's own panel re-layout. 0 = off.");
        CommandRegistry.Global.Gate = () => Commands.V;

        About = KnobGroup.Declare("about", "General", "About", order: 110,
            header: "What this build is and where to report a problem.");
        About.Status = Product.About;
        About.PanelStatus = Product.About;
        About.Link("releases", "Releases", Product.Url);
        About.Button("copy", "Copy my settings for a bug report", CopySurface,
            help: "Copies every CX setting, and what CX found when it started, to the clipboard -- paste it into a bug report.");

        CommandRegistry.Global.Register("/settings", "open the CX settings panel", OpenSettings, exempt: true);
    }

    /// <summary>The door: registered here, exempt, before any service, so it always dispatches.</summary>
    private static string? OpenSettings(string args) { CX.Ui.KnobPanelService.Open(); return null; }

    /// <summary>The About row that makes a bug report one paste: the <c>/knobs</c> surface on the clipboard. The
    /// refusal is loud -- a clipboard that said no must not look like a copy.</summary>
    private static void CopySurface()
    {
        if (!CX.Ui.SelectableText.Copy(KnobSurface.Report())) KnobGroup.Log?.Info("About: the clipboard refused the copy");
    }
}
