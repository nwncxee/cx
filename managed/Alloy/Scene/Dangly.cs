using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>dangly</c> group (docs/spec/dangly-motions.md section 7): what the dangly dispatch reads
/// once per part per frame. <see cref="Motion"/> chooses between each node's generated rig and the stock
/// solve; a kind toggle stands one class down to stock. The master is ChatOnly (ADR 0030) and off means rigid
/// parts. Touched from <see cref="RenderCommands.Register"/> so it declares on the main thread.</summary>
internal static class Dangly
{
    /// <summary>The two <see cref="Motion"/> values: each node's own class, or the stock solve for every part.</summary>
    internal const string MotionAuto = "auto", MotionVanilla = "vanilla";

    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Sway, Swing, Jiggle, Collide;
    internal static readonly Knob<string> Motion;
    internal static readonly Knob<float> WindGain, Tilt, Breeze, Cage, Strand, Still;

    static Dangly()
    {
        G = KnobGroup.Declare("dangly", "Graphics", "Motion", master: "Swaying cloaks and hair",
            masterHelp: "Cloaks, hair, banners and other swaying parts move with wind and their own weight. Off, "
                      + "they go rigid: nothing else sways them, so this is a cfg-only switch.",
            masterTier: Tier.ChatOnly, order: 352, title: "Cloaks and hair");
        Enabled = G.Master!;

        Motion = G.Dropdown("motion", "Motion", new[] { MotionAuto, MotionVanilla }, MotionAuto,
                            new[] { "automatic", "stock" },
                            help: "Automatic moves each part the way its own model asks to be moved -- leaves and "
                                + "cloth sway and ring, cloaks swing, hair jiggles. Stock moves every part the "
                                + "way the unmodified game does.");

        G.Section("Forces");
        WindGain = G.Slider("windgain", "Wind response", 0f, 3f, 0.05f, 1f,
                            help: "How hard moving air pushes a part -- the wind, and the air a moving character "
                                + "runs through. 0 = none.");
        Tilt = G.Slider("tilt", "Gravity", 0f, 1f, 0.05f, 1f,
                        help: "How fully a part on a tipped-over object falls toward the ground. 0 = it keeps "
                            + "hanging the way its model authored it.");
        Breeze = G.Slider("breeze", "Base breeze", 0f, 2f, 0.05f, 0.5f,
                          help: "The least wind an outdoor area blows, so a still one is not frozen: 1 = an "
                              + "area set to weak wind, 2 = strong. 0 = only what the area sets. Automatic motion "
                              + "only.");

        G.Section("Movement");
        Cage = G.Slider("cage", "Freedom of movement", 0.5f, 4f, 0.1f, 1.5f, tier: Tier.Advanced,
                        help: "How far short hair, body parts, worn clothing and short plants may move from where "
                            + "the model puts them: x the authored limit. Hanging cloth and tall plants swing "
                            + "freely. Automatic motion only.");
        Strand = G.Slider("strand", "Hair swing", 0f, 2f, 0.05f, 1f,
                          help: "How freely long hair, braids and tails swing on a moving character, and they "
                              + "rest against the body instead of passing through it. Higher is looser. 0 = held "
                              + "like clothing. Automatic motion only.");

        G.Section("");
        Sway = G.Toggle("sway", "sway", true, tier: Tier.ChatOnly,
                        help: "Off, vegetation and other parts free at the top move the stock way.");
        Swing = G.Toggle("swing", "swing", true, tier: Tier.ChatOnly,
                         help: "Off, cloth and rigging move the stock way.");
        Jiggle = G.Toggle("jiggle", "jiggle", true, tier: Tier.ChatOnly,
                          help: "Off, body parts and creature appendages move the stock way.");
        Collide = G.Toggle("collide", "collide", true, tier: Tier.ChatOnly,
                           help: "Off, banners and other hanging cloth swing through the walls and floors they "
                               + "hang beside. Automatic motion only.");
        Still = G.Slider("still", "still", 0f, 1f, 0.001f, 0.01f, tier: Tier.ChatOnly);
    }
}
