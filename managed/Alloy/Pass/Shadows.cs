using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>shadow</c> group (spec §4.3 Shadows), and the storage the shadow path reads:
/// <see cref="SceneInputs"/> (the focus), <see cref="CasterPass"/> (the bake and its cadence),
/// <see cref="AlloyLights"/> (the receiver's softness terms) and <see cref="AlloyRenderer"/> (the sun bias).
/// <see cref="Enabled"/> collapses the <c>shadows</c> key and the feed's own flag; sizing rows are Restart.</summary>
internal static class Shadows
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, SunDyn, Casters, Gather, Receiver, Rebake, Split, Layered, RefreshAll, DynSrc,
                                        DynEvents, FlipCollect, MaskH, Trace, Detail;
    internal static readonly Knob<float> Dist, Soft, Floor, DiscPx, Near, CsmBias, MaskHoles, MaskGraded, FocusDist, DynMin, CubeDist,
                                         SunAngle;
    internal static readonly Knob<int> Cascades, Size, Cubes, CubeSize, DynSize, DynCount, Cap, Fade, FadeSlots,
                                       Mask, MaskDither, StaticCap, DynHold, DynFull, SunFull, SunLatch, DynRanks,
                                       Stage, Taps, DeferredRes, PerPixel, Search;
    internal static readonly KnobButton Movers;

    static Shadows()
    {
        G = KnobGroup.Declare("shadow", "Graphics", "Lighting", master: "Enable Alloy shadows",
            masterHelp: "Real-time sun cascades and point-light shadow cubes, replacing the stock stencil shadows. "
                      + "Off, no surface takes a shadow from CX at all. Takes effect at the next launch.",
            masterEffect: Effect.Restart, masterTier: Tier.ChatOnly, order: 312, title: "Shadows");
        Enabled = G.Master!;

        G.Section("Sun");
        Dist = G.Slider("dist", "Sun shadow distance", 20f, 400f, 10f, 120f, "m", Tier.Advanced,
                        help: "How far from you the sun casts shadows. Cost does not depend on area size.",
                        changed: CasterPass.ShadowInvalidate)
                .Ladder(Quality.Shadows, 60f, 90f, 120f, 200f);
        Size = G.Dropdown("size", "Sun shadow resolution", new[] { 512, 1024, 2048, 4096 }, 2048, tier: Tier.Advanced,
                          effect: Effect.Restart, help: "Per-cascade map size. Larger is sharper and slower.")
                .Ladder(Quality.Shadows, 512, 1024, 2048, 4096);
        Cascades = G.Dropdown("cascades", "Sun shadow cascades", new[] { 1, 2, 3, 4 }, 4, tier: Tier.ChatOnly,
                              effect: Effect.Restart,
                              help: "More cascades = sharper shadows near you at the same distance.")
                    .Ladder(Quality.Shadows, 2, 3, 4, 4);
        // CX-49: the sun's source size, which is what makes its shadows harden toward the caster. An angle,
        // where a point light's own source is a world-unit radius (light.radius) -- the one shadow term the
        // sun cannot share with the cubes. The texel ceiling and the tap budget it DOES share (soft, taps).
        SunAngle = G.Slider("sunangle", "Sun angular size", 0f, 5f, 0.05f, 0.53f, "deg", Tier.Advanced,
                            help: "How wide the sun looks in the sky, which is what sets how fast its shadows soften away "
                                + "from whatever casts them: 0.53 is the real sun, larger is softer everywhere. 0 turns the "
                                + "blocker search off and every sun shadow takes the fixed, cheapest edge.")
                   // The sun lights every outdoor fragment, so this is the costliest shadow dial there is:
                   // off below the top two rungs, where the fixed cross is what the card can afford.
                   .Ladder(Quality.Shadows, 0f, 0f, 0.53f, 0.53f);
        G.Section("Point lights");
        Cubes = G.Slider("cubes", "Shadow-casting lights", 0, CasterPass.MaxCubes, 4, 32, tier: Tier.Advanced,
                         effect: Effect.Restart,
                         help: "How many nearby lights cast shadows at once. Lower on a small card.")
                 .Ladder(Quality.Shadows, 8, 16, 32, 48);
        CubeSize = G.Dropdown("cubesize", "Point-light shadow resolution", new[] { 128, 256, 512, 1024, 2048 }, 512,
                              tier: Tier.Advanced, effect: Effect.Restart,
                              help: "Per-face size of each light's shadow cube.")
                    .Ladder(Quality.Shadows, 128, 256, 512, 1024);
        CubeDist = G.Slider("cubedist", "Point-light shadow distance", 0f, 200f, 5f, 30f, "m", Tier.Advanced,
                            help: "A light farther than this from you casts no shadow, and no fragment farther than this from the camera "
                                + "reads one; shadows fade out over the last quarter. 0 = every light in the budget, at any distance.")
                    .Ladder(Quality.Shadows, 15f, 20f, 30f, 50f);
        Soft = G.Slider("soft", "Shadow softness", 0f, 16f, 0.25f, 4f, tier: Tier.Advanced,
                        help: "Widest blur of point-light and sun shadows, in shadow-map texels; the blur narrows toward the "
                            + "caster's contact. 0 is hard-edged and cheapest.")
                .Ladder(Quality.Shadows, 0f, 2f, 4f, 6f);
        Floor = G.Slider("floor", "Shadow floor", 0f, 1f, 0.05f, 0.35f, tier: Tier.ChatOnly,
                         help: "How much of a point light still reaches its own shadow, tinted by the light. 0 = black cutouts.");
        DynSize = G.Dropdown("dynsize", "Moving-caster resolution", new[] { 64, 128, 256, 512 }, 512, tier: Tier.ChatOnly, effect: Effect.Restart);
        DynCount = G.Slider("dyncount", "Moving-caster pool", 0, CasterPass.MaxCubes, 1, 32, tier: Tier.ChatOnly, effect: Effect.Restart,
                            help: "0 = no separate pool.");
        DynMin = G.Slider("dynmin", "Smallest moving caster", 0f, 3f, 0.1f, 0.5f, "m", Tier.ChatOnly,
                          help: "A moving part smaller than this (a candle flame, a swinging lantern) claims no moving-caster page; creatures always do.");
        SunDyn = G.Toggle("sundyn", "Separate moving-caster sun layer", true, Tier.ChatOnly, Effect.Restart);
        DiscPx = G.Slider("discpx", "Softness cutoff", 0f, 16f, 0.5f, 0f, "px", Tier.ChatOnly,
                          help: "Below this screen radius the blur stands down. 0 = never (consistent softness at every distance).");
        Taps = G.Slider("taps", "Shadow blur taps", 0, 16, 1, 8, tier: Tier.ChatOnly,
                        help: "Disc taps per shadowing light, and per sun cascade. 0 = no blur.");
        // CX-49: the ring the blocker search spends before the disc -- the cost that buys the contact
        // hardening, on both receivers (the sun caps it at CX_SUN_SEARCH, which it pays per outdoor fragment).
        Search = G.Slider("search", "Blocker search taps", 1, 16, 1, 16, tier: Tier.ChatOnly,
                          help: "How many taps look for what is casting the shadow, which is what lets the blur narrow "
                              + "toward the caster. Fewer is cheaper and guesses the width off less of the map.")
                  .Ladder(Quality.Shadows, 4, 8, 16, 16);
        Cap = G.Slider("cap", "Softened shadows per pixel", 0, 2, 1, 2, tier: Tier.ChatOnly,
                       help: "How many of a pixel's shadowed lights get the soft, contact-hardened filter; the rest take a "
                           + "single hardware tap. 0 = every light one hardware tap, and it stands the per-pixel budget "
                           + "and the deferred shadow record down with it.");
        // CX-188 budget: the per-pixel SHADOW BUDGET -- how many of a pixel's lights cast a shadow AT ALL. The
        // rest light it unshadowed, which is what the forward cap's evicted candidates already did; what this
        // ends is the deferred walk's hardware tap for a pixel's fifth-and-later shadowed light (measured ~1.1M
        // of them a frame on the test area). Default = the record's own size, so the shipped look is the
        // record's verdict for the four it holds and nothing beyond it.
        PerPixel = G.Slider("perpixel", "Shadowed lights per pixel", 0, DeferredShadows.RecordLights, 1,
                            DeferredShadows.RecordLights, tier: Tier.ChatOnly,
                            help: "How many lights may cast a shadow on one pixel. The strongest ones at that pixel win; the "
                                + "rest light it without a shadow, which is far cheaper where many lamps overlap. 0 = no limit.");
        // CX-188 step 3 (ADR 0029): the deferred path evaluates each pixel's shadows once into a visibility
        // record, so it -- and only it -- can take that record at half resolution and upsample it with a
        // depth- and normal-aware filter. Half by default: the record is the expensive half of the deferred
        // frame, and the upsample mixes the four disc rotations the pass interleaves across a 2x2 block of
        // record texels, so half resolution integrates sixteen tap positions a pixel where full takes four.
        DeferredRes = G.Dropdown("deferredres", "Deferred shadow resolution", new[] { 1, 2 }, 2,
                                 new[] { "full", "half" }, tier: Tier.ChatOnly,
                                 help: "Point-light shadows on solid surfaces are worked out once per pixel, or once per "
                                     + "2x2 block and blurred back up along surfaces. Half is much cheaper and slightly softer; "
                                     + "full keeps every shadow edge exactly where the pixel puts it.");
        Near = G.Slider("near", "Self-shadow skip", 0f, 4f, 0.05f, 1f, tier: Tier.ChatOnly,
                        help: "Carried lights don't shadow their bearer inside this radius.");
        CsmBias = G.Slider("csmbias", "Sun depth bias", 0f, 0.5f, 0.01f, 0.03f, tier: Tier.ChatOnly);
        Fade = G.Slider("fade", "Shadow fade-in", 0, 120, 1, 60, "frames", Tier.ChatOnly, help: "0 = pop.");
        FadeSlots = G.Slider("fadeslots", "Fade reserve", 0, 16, 1, 4, tier: Tier.ChatOnly);
        Mask = G.Dropdown("mask", "Cut-out shadows", new[] { -1, 0, 1, 2 }, 2, new[] { "skip", "solid", "alpha-tested", "auto" },
                          tier: Tier.ChatOnly, help: "Auto = alpha-tested where the texture is mostly holes, solid elsewhere.",
                          changed: CasterPass.ListChanged);
        MaskHoles = G.Slider("maskholes", "Auto threshold", 0f, 1f, 0.05f, 0.65f, tier: Tier.ChatOnly, changed: CasterPass.ListChanged);
        MaskGraded = G.Slider("maskgraded", "Glass threshold", 0f, 1f, 0.05f, 0.15f, tier: Tier.ChatOnly, changed: CasterPass.ListChanged);
        MaskDither = G.Slider("maskdither", "Translucent shadow dither", 0, CasterPass.MaskDitherMax, 1, 1, tier: Tier.ChatOnly,
                              changed: () => { CasterPass.ListChanged(); CasterPass.ShadowInvalidate(); });
        FocusDist = G.Slider("focusdist", "Cascade look-ahead", 0f, 100f, 1f, 24f, "m", Tier.ChatOnly);
        StaticCap = G.Slider("staticcap", "Static re-bakes per frame", 0, 16, 1, 3, tier: Tier.ChatOnly, help: "0 = unbounded (hitches).");
        DynHold = G.Slider("dynhold", "Mover hold", 0, 120, 1, 30, "frames", Tier.ChatOnly);
        DynFull = G.Slider("dynfull", "Full-rate cube ranks", 0, CasterPass.MaxCubes, 1, CasterPass.MaxCubes, tier: Tier.ChatOnly, help: "Cube ranks past this re-bake movers every other frame.");
        SunFull = G.Slider("sunfull", "Full-rate sun cascades", 0, CasterPass.MaxCascades, 1, CasterPass.MaxCascades, tier: Tier.ChatOnly, help: "Cascades past this re-bake movers every other frame.");
        SunLatch = G.Slider("sunlatch", "Cascade re-latches per frame", 0, CasterPass.MaxCascades, 1, 0, tier: Tier.ChatOnly, help: "0 = every drifted cascade re-fits the same frame.");
        DynRanks = G.Slider("dynranks", "Mover pages", 0, CasterPass.MaxCubes, 1, CasterPass.MaxCubes, tier: Tier.ChatOnly,
                            changed: CasterPass.ShadowInvalidate);

        G.Section("");
        Casters = G.Toggle("casters", "casters", true, Tier.ChatOnly, Effect.Restart);
        Stage = G.Slider("stage", "stage", 2, 4, 1, 4, tier: Tier.ChatOnly, effect: Effect.Restart);
        Gather = G.Toggle("gather", "gather", true, Tier.ChatOnly);
        Receiver = G.Toggle("receiver", "receiver", true, Tier.ChatOnly);
        Rebake = G.Toggle("rebake", "rebake", true, Tier.ChatOnly);
        Split = G.Toggle("split", "split", true, Tier.ChatOnly, changed: CasterPass.ShadowInvalidate);
        Layered = G.Toggle("layered", "layered", true, Tier.ChatOnly);
        RefreshAll = G.Toggle("refreshall", "refreshall", false, Tier.ChatOnly);
        DynSrc = G.Toggle("dynsrc", "dynsrc", true, Tier.ChatOnly);
        DynEvents = G.Toggle("dynevents", "dynevents", true, Tier.ChatOnly, changed: CasterPass.ShadowInvalidate);
        FlipCollect = G.Toggle("flipcollect", "flipcollect", false, Tier.ChatOnly);
        MaskH = G.Toggle("maskh", "maskh", true, Tier.ChatOnly, changed: CasterPass.ListChanged);
        Trace = G.Toggle("trace", "trace", false, Tier.ChatOnly);
        // the thresholds are not in any bake's key, so a change has to invalidate the held static maps --
        // otherwise the A/B measures the detail the maps were already baked at
        Detail = G.Toggle("detail", "detail", true, Tier.ChatOnly, changed: CasterPass.ShadowInvalidate);
        Movers = G.Button("movers", "movers", CasterPass.DumpMoversNext, Tier.ChatOnly);
    }
}
