using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>post</c> group: the storage <see cref="PostChain"/>, <see cref="PostStack"/> and
/// <see cref="ToonStage"/> read. No master -- the resolve IS the Alloy frame; <c>hdr</c> is the Renderer group's.
/// There is no auto exposure: <c>exposure</c> is one fixed linear gain on the resolved frame BEFORE the tonemap, because adaptation re-grades an area away from its builder's lighting.</summary>
internal static class Post
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Scale, Exposure, Bloom, Threshold, Knee,
                                         AcesMix, TmKnee, Dither, TaaBlend, TaaSharp, ToonBands, ToonInk, ToonEdge;
    internal static readonly Knob<string> Aa;
    internal static readonly Knob<bool> Taau, HalfRes, Stack, FbPost, ClearViz, SkyRaw;
    internal static readonly Knob<int> Smaa, Tonemap, Viz;
    /// <summary>Anisotropic samples a linear mipmapped texture's sampler asks for; clamped to the device's
    /// ceiling at sampler creation. Samplers are minted with the texture, so this is read as <c>Boot</c>.</summary>
    internal static readonly Knob<int> Aniso;
    /// <summary><see cref="Aa"/>'s values: the anti-aliaser that draws. TAA is none of them: it draws only where FSR cannot.</summary>
    internal const string AaFsr = "fsr", AaSmaa = "smaa", AaOff = "off";

    static Post()
    {
        G = KnobGroup.Declare("post", "Graphics", "Image", order: 330);
        G.Status = PostChain.Describe;
        G.PanelStatus = CX.Alloy.Fsr.PanelLine;

        G.Section("Anti-aliasing");
        Scale = G.Slider("scale", "Render scale", 0.25f, 2f, 0.05f, 0.9f,
                         help: "Size of the 3D frame relative to your screen. Below 1 renders fewer pixels and FSR 3.1 rebuilds "
                             + "the full picture (faster); above 1 supersamples (sharper, slower). The UI is never scaled.");
        Scale.ShowIf = static () => Aa.V == AaFsr;
        Aa = G.Dropdown("aa", "Anti-aliasing", new[] { AaFsr, AaSmaa, AaOff }, AaFsr, new[] { "FSR 3.1", "SMAA", "Off" },
                        help: "How edges are smoothed. FSR 3.1 rebuilds the full-resolution picture from the render scale's smaller "
                            + "frame and the frames before it; at render scale 1 it is the anti-aliasing. SMAA smooths each frame "
                            + "on its own, and Off does not smooth; both render at full resolution.",
                        changed: OnAa)
             .Ladder(Quality.Preset, Quality.Classic(AaSmaa, AaFsr));
        Smaa = G.Dropdown("smaa", "SMAA quality", new[] { 1, 2, 3 }, 2, new[] { "medium", "high", "ultra" },
                          help: "How far SMAA searches along an edge. Higher finds longer edges and costs more.");
        Smaa.ShowIf = static () => Aa.V == AaSmaa;
        Aniso = G.Dropdown("aniso", "Anisotropic filtering", new[] { 1, 2, 4, 8, 16 }, 16,
                           new[] { "off", "2x", "4x", "8x", "16x" }, effect: Effect.Restart,
                           help: "How sharp a textured floor or wall stays where it slants away from the camera. Higher is "
                               + "sharper into the distance and costs GPU time in every world pass; off is the blurriest and "
                               + "the cheapest. Takes effect at the next launch.");
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
        Viz = G.Slider("viz", "viz", 0, PostChain.VizMotion, 1, 0, tier: Tier.ChatOnly);
        ClearViz = G.Toggle("clearviz", "clearviz", false, Tier.ChatOnly);
        SkyRaw = G.Toggle("skyraw", "skyraw", true, Tier.ChatOnly);
    }

    /// <summary>A new anti-aliaser starts its history over.</summary>
    private static void OnAa()
    {
        CX.Alloy.Fsr.Revoke();
        TaaStage.Reset();
    }
}
