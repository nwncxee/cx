using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>post</c> group: the storage <see cref="PostChain"/>, <see cref="PostStack"/> and
/// <see cref="ToonStage"/> read. No master -- the resolve IS the Alloy frame; <c>hdr</c> and the render scale
/// are the Renderer group's. There is no auto exposure: <c>exposure</c> is one fixed linear gain on the
/// resolved frame BEFORE the tonemap, because adaptation re-grades an area away from its builder's lighting.</summary>
internal static class Post
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Exposure, Bloom, Threshold, Knee,
                                         AcesMix, TmKnee, Dither, TaaBlend, TaaSharp, ToonBands, ToonInk, ToonEdge;
    internal static readonly Knob<bool> Taa, Taau, HalfRes, Stack, FbPost, Motion, ClearViz, SkyRaw;
    internal static readonly Knob<int> Tonemap, Viz;

    static Post()
    {
        G = KnobGroup.Declare("post", "Graphics", "Image", order: 330);
        G.Status = PostChain.Describe;

        G.Section("Exposure");
        Exposure = G.Slider("exposure", "Exposure", 0.25f, 4f, 0.05f, 1f,
                            help: "Overall brightness before tone mapping. 1 is neutral.");
        G.Section("Bloom");
        Bloom = G.Slider("bloom", "Bloom", 0f, 0.5f, 0.01f, 0.06f, help: "Glow around bright lights. 0 is off.")
                .Ladder(Quality.Preset, Quality.Classic(0f, 0.06f));
        G.Section("Tone");
        Tonemap = G.Dropdown("tonemap", "Tone mapping", new[] { PostChain.TonemapShoulder, PostChain.TonemapAces, PostChain.TonemapNeutral },
                             PostChain.TonemapAces,
                             help: "How bright values above white are compressed.",
                             format: v => v switch { PostChain.TonemapShoulder => "shoulder", PostChain.TonemapNeutral => "neutral", _ => "aces" })
                  .Ladder(Quality.Preset, Quality.Classic(PostChain.TonemapShoulder, PostChain.TonemapAces));
        G.Section("Anti-aliasing");
        Taa = G.Toggle("taa", "Temporal anti-aliasing", true, help: "Smooths edges over several frames.")
              .Ladder(Quality.Preset, Quality.Classic(false, true));
        Taa.Changed = () => { if (Taa.V) TaaStage.Reset(); };
        G.Section("Bloom");
        Threshold = G.Slider("threshold", "Bloom threshold", 0f, 4f, 0.05f, 1f, tier: Tier.Advanced);
        Knee = G.Slider("knee", "Bloom knee", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced);
        G.Section("Tone");
        AcesMix = G.Slider("acesmix", "ACES mix", 0f, 1f, 0.05f, 0.5f, tier: Tier.ChatOnly, help: "Only affects ACES tone mapping.");
        TmKnee = G.Slider("tmknee", "Shoulder knee", 0f, 1f, 0.05f, 0.8f, tier: Tier.ChatOnly, help: "Only affects shoulder and ACES tone mapping.")
                 .Ladder(Quality.Preset, Quality.Classic(1f, 0.8f));
        Dither = G.Slider("dither", "Output dither", 0f, 2f, 0.1f, 1f, tier: Tier.ChatOnly);
        G.Section("Anti-aliasing");
        TaaSharp = G.Slider("taasharp", "TAA sharpness", 0f, 1f, 0.05f, 0.3f, tier: Tier.Advanced,
                            help: "Undoes the softness the history blend leaves. 0 = the raw resolve.");
        G.Section("");
        // Never 0 (a frozen history would never converge) and never above 0.5 (the raw jitter, "an earthquake").
        TaaBlend = G.Slider("taablend", "TAA history weight", 0.02f, 0.5f, 0.01f, 0.1f, tier: Tier.ChatOnly);
        Taau = G.Toggle("taau", "Temporal upsampling", true, Tier.ChatOnly, help: "With render scale below 1.",
                        changed: TaaStage.Reset);
        // Never a ladder rung: it only looks clean under TAA, and a feature must look right with TAA off.
        HalfRes = G.Toggle("halfres", "Half-resolution ambient occlusion", false, Tier.ChatOnly,
                           help: "Ambient occlusion is computed at a quarter of the pixels and upsampled. Needs temporal anti-aliasing to look clean.");
        Stack = G.Toggle("stack", "Game post-effects", true, Tier.ChatOnly,
                         help: "The stock framebuffer effects (gamma, sharpen, vibrance, SMAA, toon…) on the Alloy frame.");
        FbPost = G.Toggle("fbpost", "Hosted content post", true, Tier.ChatOnly);
        ToonBands = G.Slider("toonbands", "Toon bands", 1f, 32f, 1f, 1f, tier: Tier.ChatOnly,
                             help: "With the game's Toon option; 1 = no posterise.");
        ToonInk = G.Slider("toonink", "Toon ink", 0f, 1f, 0.05f, 1f, tier: Tier.ChatOnly);
        ToonEdge = G.Slider("toonedge", "Toon edge", 1f, 8f, 0.5f, 1f, tier: Tier.ChatOnly);
        Motion = G.Toggle("motion", "motion", false, Tier.ChatOnly);
        Viz = G.Slider("viz", "viz", 0, PostChain.VizMotion, 1, 0, tier: Tier.ChatOnly);
        ClearViz = G.Toggle("clearviz", "clearviz", false, Tier.ChatOnly);
        SkyRaw = G.Toggle("skyraw", "skyraw", true, Tier.ChatOnly);
    }
}
