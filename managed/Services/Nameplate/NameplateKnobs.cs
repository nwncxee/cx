using CX.Framework;
using CX.Ui;

namespace CX.Services.Nameplate;

/// <summary>The <c>nameplate</c> group and the storage <see cref="NameplateService"/> reads on the frame: the
/// master (off = the engine's own bubble mode back, stock names and bubbles draw; live both ways), then one
/// section per thing drawn. <see cref="Queue"/> and <see cref="Lines"/> stop at the table's
/// <see cref="PlateTable.MaxLines"/>/<see cref="PlateTable.MaxWrapped"/>, past which the drain clamps silently.</summary>
internal static class NameplateKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Portraits, Background, HealthBand, HealthBar, ChannelStyle, ClickToTell,
                                        WhileTalking;
    /// <summary>The plate names' text (<c>nameplate.font</c> / <c>typeface</c> / <c>outline</c>, the outline the old
    /// toggle's key) and the chat bubbles' (<c>nameplate.bubble*</c>).</summary>
    internal static readonly TextStyle Plate, Bubble;
    internal static readonly Knob<float> MaxDist, Width, FadeDist;
    internal static readonly Knob<int> Queue, Lines, Anim, PortraitLines;
    /// <summary>Percent of a plate's or bubble's alpha left while the scene hides its creature; 100 = no scene test.</summary>
    internal static readonly Knob<int> BehindWalls;
    /// <summary>A mask of <see cref="SpeakerName"/> and <see cref="SpeakerPortrait"/>.</summary>
    internal static readonly Knob<int> Speaker;
    internal static readonly KnobButton Probe;

    /// <summary>Design-px bounds of the text wrap width (bubbles AND plate names).</summary>
    internal const float WidthMin = 120f, WidthMax = 480f;
    /// <summary>Upper bound of the distance knobs, so a file cannot ask for an unbounded cull radius.</summary>
    internal const float DistMax = 100f;
    /// <summary><see cref="Speaker"/>'s bits: the name header over a bubble, the portrait beside it.</summary>
    internal const int SpeakerName = 1, SpeakerPortrait = 2;

    static NameplateKnobs()
    {
        G = KnobGroup.Declare("nameplate", "Interface", "Names & combat text", master: "Enable CX nameplates and chat bubbles",
            masterHelp: "Names, health and speech bubbles drawn by CX: sharper text, portraits, stacked lines. Off, the stock game's names and bubbles draw.",
            order: 220, title: "Nameplates");
        Enabled = G.Master!;

        G.Section("Nameplates and bubbles");
        Background = G.Toggle("background", "Background", true, help: "A box behind names and bubbles, as in the stock game.");
        MaxDist = G.Slider("maxdist", "Visible distance", 0f, DistMax, 5f, 45f, "m",
                           help: "Names and bubbles past this distance are not drawn. 0 draws them all.");
        FadeDist = G.Slider("fadedist", "Fade band", 0f, DistMax, 1f, 10f, "m", Tier.Advanced,
                            help: "They fade out over this distance before Visible distance.");
        Width = G.Slider("width", "Text width", WidthMin, WidthMax, 10f, 300f, "px", Tier.Advanced,
                         help: "The widest a name or a bubble gets before wrapping.");
        Anim = G.Slider("anim", "Animation", 0, 1000, 10, 180, "ms", Tier.ChatOnly, help: "0 = they pop in.");
        BehindWalls = G.Slider("behindwalls", "Behind walls", 0, 100, 5, 100, "%",
                               help: "How much of a name or bubble still shows while a wall or the ground hides its creature from the camera. 100 % shows it fully, as in the stock game. Alloy only.");

        G.Section("Nameplates");
        Portraits = G.Toggle("portraits", "Portrait", true, help: "A portrait beside the name.");
        HealthBand = G.Toggle("healthband", "Health colour", true, help: "The name is tinted by the creature's health, as in the stock game.");
        HealthBar = G.Toggle("healthbar", "Health bar", false,
                             help: "A thin bar under the name. Exact for you and your party; others by injury level. Not in the stock game.");
        WhileTalking = G.Toggle("whiletalking", "Show while talking", false,
                                help: "Hovering or Tab still shows the nameplate of someone talking, under their bubble. The stock game hides it until they finish.");
        ClickToTell = G.Toggle("clicktotell", "Click a name to tell", false, Tier.Advanced,
                               help: "Clicking the name starts a tell. Takes the click the world would have had.");
        Plate = new TextStyle(G, "names", outline: 1, tier: Tier.Advanced);

        G.Section("Chat bubbles");
        Speaker = G.Dropdown("speaker", "Speaker", new[] { SpeakerName | SpeakerPortrait, SpeakerName, SpeakerPortrait, 0 },
                             SpeakerName | SpeakerPortrait, new[] { "Name and portrait", "Name only", "Portrait only", "Neither (stock)" },
                             help: "Who is talking: their name above the bubble, their portrait beside it. "
                                 + "A nameplate shown under the bubble takes over what it already shows.");
        // ponytail: 6 lines tops, as the portrait is the engine's MEDIUM (64x100) image; a bigger one needs the LARGE fetched
        PortraitLines = G.Dropdown("portraitsize", "Portrait size", new[] { 2, 3, 4, 5, 6 }, 3,
                                   new[] { "2 lines", "3 lines", "4 lines", "5 lines", "6 lines" }, tier: Tier.ChatOnly,
                                   help: "How tall the portrait beside a bubble is, in lines of bubble text.");
        Queue = G.Dropdown("queue", "Messages kept", new[] { 1, 2, 3, 4 }, 2,
                           help: "How many recent things someone said stack in their bubble. The stock game keeps 1.");
        Lines = G.Slider("lines", "Lines per message", 1, PlateTable.MaxWrapped, 1, PlateTable.MaxWrapped, tier: Tier.ChatOnly,
                         help: "A longer message is cut to this many wrapped lines.");
        ChannelStyle = G.Toggle("channelstyle", "Colour by channel", true, Tier.Advanced,
                                help: "Shouts, whispers and emotes get their own colour and emphasis.");
        Bubble = new TextStyle(G, "chat bubbles", prefix: "bubble", outline: 1, tier: Tier.Advanced);
        Probe = G.Button("probe", "probe", NameplateService.ArmProbe, Tier.ChatOnly);
    }
}
