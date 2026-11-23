using CX.Framework;

namespace CX.Services.Update;

/// <summary>The settings the release notice keeps, declared on one group: the About group in a session.</summary>
internal sealed class UpdateKnobs
{
    /// <summary>The consent answers: not yet answered, yes, no.</summary>
    public const string Ask = "ask", Yes = "yes", No = "no";

    public readonly KnobGroup Group;
    public readonly Knob<string> Consent, Found, Seen, Muted;
    public readonly KnobButton Copy, Mute;
    /// <summary>The notice the buttons act on; set by the notice that owns these knobs this launch.</summary>
    public UpdateNotice? Owner;

    private UpdateKnobs(KnobGroup g)
    {
        Group = g;
        Consent = g.Dropdown("updates", "Tell me about new CX releases", [Ask, Yes, No], Ask, ["Not answered yet", "Yes", "No"],
                             help: "Once a session CX asks the CX releases page whether a newer release is out, and shows a badge on the main-menu gear. It never downloads or installs anything.");
        Found = g.Text("updatefound", "updatefound", "", Tier.Hidden);
        Seen = g.Text("updateseen", "updateseen", "", Tier.Hidden);
        Muted = g.Text("updatemute", "updatemute", "", Tier.Hidden);
        Copy = g.Button("updatecopy", "Copy download link", () => Owner?.Copy(), help: "Copies the CX releases page's address.");
        Mute = g.Button("updatemute1", "Don't tell me about this release", () => Owner?.Mute());
    }

    /// <summary>Declare the notice's knobs on <paramref name="g"/>.</summary>
    public static UpdateKnobs Declare(KnobGroup g) => new(g);
}
