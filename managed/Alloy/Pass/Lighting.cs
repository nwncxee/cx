using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>light</c> group (spec §4.3 Lighting), and the storage <see cref="AlloyLights"/> and
/// <see cref="EnvProbe"/> read: the point-light curve's dials, the area-term scales, the wrap terms, the
/// receiver's departed-light fade, and the environment probe. No master: lighting is the lit stage, and off
/// is Renderer off.</summary>
internal static class Lighting
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Gain, Ambient, Sun, Overbright, Sss, Wrap, WrapNdl, Hot, HotSize, Radius, Falloff,
                                         ProbeGain, BinReach, AmbientDir,
                                         Daylight, DayLow, DayHigh, Flicker, FlickerSpeed, SsrRough, SsrPlanarCull, Relevance, Floor, FullModel;
    internal static readonly Knob<int> Fadeout, ProbeSize, BinCap, SsrDetail, SsrPlanarRes;
    internal static readonly Knob<bool> Probe, ReachFade, HiddenLights, Brdf, SpecAa, Ssr, SsrPlanar,
                                        Deferred, DeferredTiles, LightRec;

    static Lighting()
    {
        G = KnobGroup.Declare("light", "Graphics", "Lighting", order: 310, title: "Lights");
        G.Status = AlloyLights.DayStatus;

        G.Section("Lights");
        Gain = G.Slider("gain", "Point-light brightness", 0f, 4f, 0.05f, 1f,
                        help: "Scales every placed and carried light. 1 is the stock brightness.");
        Ambient = G.Slider("ambient", "Ambient light", 0f, 4f, 0.05f, 1f,
                           help: "Scales the area's ambient fill. 1 is the area author's value.");
        Sun = G.Slider("sun", "Sun and moon", 0f, 4f, 0.05f, 1f,
                       help: "Scales the area's directional light. 1 is the area author's value.");
        Overbright = G.Slider("overbright", "Carried-light boost", 0f, 1f, 0.05f, 0f, tier: Tier.Advanced,
                              help: "How much of a torch's extra intensity shows at its centre instead of only as reach. "
                                  + "0 is the stock game; 0.5 makes carried lights stand out.");
        Flicker = G.Slider("flicker", "Fire flicker", 0f, 2f, 0.05f, 1f,
                           help: "Animated lights (torches, braziers) keep a steady reach and flicker in brightness instead of pulsing in size. "
                               + "1 flickers as deep as the pulse was made; 0 is the stock pulse.");
        FlickerSpeed = G.Slider("flickerspeed", "Flicker speed", 0.1f, 4f, 0.05f, 1f, tier: Tier.Advanced,
                                help: "Rate of the flicker; 1 is a fireplace.");
        Daylight = G.Slider("daylight", "Daylight outshines lamps", 1f, 10f, 0.25f, 3f, tier: Tier.Advanced,
                            help: "How much brighter full daylight is than a lamp. Lamps and their glow fade as the day brightens "
                                + "and come back at dusk. 1 is the stock game: a torch looks the same at noon as at midnight.");
        DayLow = G.Slider("daylow", "Daylight starts at", 0f, 1f, 0.01f, 0.1f, tier: Tier.ChatOnly,
                          help: "Sun brightness (linear) below which lamps are at full strength.");
        DayHigh = G.Slider("dayhigh", "Full daylight at", 0f, 2f, 0.01f, 0.6f, tier: Tier.ChatOnly,
                           help: "Sun brightness (linear) at which lamps are dimmed the full Daylight amount.");
        Sss = G.Slider("sss", "Soft skin lighting", 0f, 1f, 0.05f, 0.25f, tier: Tier.Advanced,
                       help: "Light wrapping past the shadow line on characters. 0 is flat; the stock game uses 0.38 at shader quality above medium.");
        Wrap = G.Slider("wrap", "Sphere-light wrap", 0f, 4f, 0.05f, 1f, tier: Tier.ChatOnly,
                        help: "Near lights soften the shadow line on low-poly models; 0 = point lights.");
        WrapNdl = G.Slider("wrapndl", "Diffuse wrap", 0f, 2f, 0.05f, 1f, tier: Tier.ChatOnly,
                           help: "Distance-independent softening of every terminator.");
        Hot = G.Slider("hot", "Near-field core", 0f, 2f, 0.05f, 0f, tier: Tier.ChatOnly,
                       help: "Adds intensity at a light's centre only.");
        HotSize = G.Slider("hotsize", "Core size", 0f, 1f, 0.01f, 0.15f, tier: Tier.ChatOnly,
                           help: "Fraction of the range the core covers.");
        Radius = G.Slider("radius", "Source radius", 0f, 4f, 0.05f, 1f, tier: Tier.ChatOnly,
                          help: "Wrap radius and self-shadow skip base.");
        Falloff = G.Slider("falloff", "Rim fade exponent", 0.1f, 4f, 0.05f, 1f, tier: Tier.ChatOnly,
                           help: "1 = stock falloff exactly.");
        Fadeout = G.Slider("fadeout", "Departed-light fade", 0, 120, 1, SceneInputs.FadeFramesBoot, "frames", Tier.ChatOnly,
                           help: "Frames a light takes to fade after it leaves.");
        // CX-188 (ADR 0029): deferred point lights are THE path now -- on, the opaque and cutout passes write the
        // G-buffer and one compute pass lights them; off is the old forward loop inside the lit stage, kept as a
        // cfg key because it is the one lever that moves the whole point-light cost between two shapes.
        Deferred = G.Toggle("deferred", "Deferred point lights", true, Tier.ChatOnly,
                            help: "Point lights on solid surfaces are added in one screen pass after the world is drawn, instead of "
                                + "inside every surface's shader. Same image, faster with many lights. Off is the previous path.");
        // CX-188 step 2b: which light list the deferred pass walks. On, each 16x16 group culls the area's lights
        // against its OWN depth slab; off, each pixel walks its froxel's list, which is the forward loop's own
        // input and so the parity A/B. Chat-only: the answer is the same image either way, and the question is
        // "is the tile cull paying for itself here", which is a bench question and not a setting.
        // <para>OFF by default: measured on the CX-188 test area against a 14.8 ms GPU baseline,
        // <c>light.deferredtiles 0</c> was -0.8 ms -- the depth-bounded cull is SLOWER than the froxel lists
        // there. The froxel table is built once a frame for the whole grid; the cull re-walks every light of the
        // area per 16x16 group, and a top-down camera's tiles span enough depth that the slab buys little back.
        // The knob stays because an indoor or first-person camera is the case where it could win.</para>
        DeferredTiles = G.Toggle("deferredtiles", "deferredtiles", false, Tier.ChatOnly);
        // CX-188: the light record's A/B -- off, every blended lit fragment walks the lights again, even one lying on
        // a surface the deferred pass already lit (a floor decal). Chat-only, a bench question like the one above.
        LightRec = G.Toggle("lightrec", "lightrec", true, Tier.ChatOnly);
        G.Section("Reflections");
        Probe = G.Toggle("probe", "Reflections from the scene", true,
                         help: "Materials with an environment map reflect a capture of the surroundings instead of the stock sphere map.")
                 .Ladder(Quality.Reflections, false, true, true, true);
        ProbeGain = G.Slider("probegain", "Reflection strength", 0f, 4f, 0.05f, 1f, tier: Tier.ChatOnly,
                             help: "1 is the stock weighting. Fainter reflections are also cheaper: too faint a one traces no ray.");
        ProbeSize = G.Dropdown("probesize", "Reflection capture size", new[] { 64, 128, 256, 512 }, 64, tier: Tier.Advanced,
                               effect: Effect.Restart,
                               help: "Resolution the surroundings are captured at for those reflections. Larger is sharper and slower.")
                     .Ladder(Quality.Reflections, 64, 64, 64, 256);
        Ssr = G.Toggle("ssr", "Screen-space reflections", true,
                       help: "Glossy and rain-wet surfaces also reflect what is on screen -- grass, creatures, anything drawn -- "
                           + "and keep the capture where a reflection leaves the screen.")
               .Ladder(Quality.Reflections, false, true, true, true);
        SsrRough = G.Slider("ssrrough", "Screen-space reflection roughness", 0.05f, 1f, 0.05f, 0.5f, tier: Tier.ChatOnly,
                            help: "Surfaces rougher than this keep the capture alone. Lower is cheaper.");
        SsrDetail = G.Dropdown("ssrdetail", "Screen-space reflection detail", new[] { 0, 1, 2 }, 1, new[] { "full", "half", "quarter" },
                               tier: Tier.Advanced,
                               help: "The resolution a reflection finds what it hits at. Lower is cheaper and blockier.")
                     .Ladder(Quality.Reflections, 2, 2, 1, 1);
        SsrPlanar = G.Toggle("ssrplanar", "Mirror reflections on flat ground", false,
                             help: "Wet ground at the player's height reflects a second, mirrored draw of the scene: no holes behind "
                                 + "figures, and undersides the camera never sees. Costs a low-resolution redraw of the opaque set, "
                                 + "drawn without moving lights, so a street lit by them mirrors darker than it is.");
        SsrPlanarRes = G.Dropdown("ssrplanarres", "Mirror reflection detail", new[] { 2, 3, 4, 6, 8 }, 8,
                                  new[] { "half", "third", "quarter", "sixth", "eighth" },
                                  tier: Tier.ChatOnly,
                                  help: "The resolution the mirrored scene is drawn at. Lower is cheaper and softer.")
                        .Ladder(Quality.Reflections, 8, 8, 8, 4);
        SsrPlanarCull = G.Slider("ssrplanarcull", "Mirror reflection small-object cull", 0f, 16f, 0.5f, 2f, "px", Tier.ChatOnly,
                                 help: "Objects smaller than this many pixels of the mirrored draw are left out of it. Higher is cheaper.");
        HiddenLights = G.Toggle("hidden", "Hidden lights do not reflect", true, Tier.ChatOnly,
                                help: "A wet or glossy surface mirrors a light only where that light's shadow lets it -- a lamp on a terrace reflects in the street from where the ground sees over the parapet, not from the wall's foot.");
        G.Section("Materials");
        Brdf = G.Toggle("brdf", "Modern materials", true, Tier.ChatOnly,
                        help: "Energy-conserving highlights, reflections that brighten at grazing angles and fade in crevices, and the "
                            + "extra material layers (metal, clear coat, sheen, glow) a .mtr can declare for Alloy. Off is the stock shading exactly.")
               .Ladder(Quality.Preset, Quality.Classic(false, true));
        AmbientDir = G.Slider("ambientdir", "Directional ambient", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced,
                              help: "Ambient light takes its direction from the surroundings (sky above, ground below) at the area's own "
                                  + "brightness. 0 is the stock sun-facing shading.");
        SpecAa = G.Toggle("specaa", "Smooth highlights", true, Tier.ChatOnly,
                          help: "Widens a highlight that is narrower than a pixel, so glossy and normal-mapped surfaces do not sparkle "
                              + "or crawl in motion. Needs Modern materials.");
        G.Section("");
        BinReach = G.Slider("binreach", "Light reach", 0.1f, 4f, 0.05f, 0.8f, tier: Tier.ChatOnly,
                            help: "Fraction of each light's authored radius that lights the scene. 1 is the stock radius; lower is cheaper.")
                    .Ladder(Quality.Lighting, 0.5f, 0.65f, 0.8f, 1f);
        BinCap = G.Slider("bincap", "Lights per cluster", 0, AlloyLights.SortCap, 1, 0, tier: Tier.ChatOnly,
                          help: "0 keeps every light and lets the faint-light cutoff thin them per pixel. A cap keeps the N strongest per screen cluster: cheaper, but it draws the cluster grid where more lights overlap.")
                  .Ladder(Quality.Lighting, 16, 24, 0, 0);
        Relevance = G.Slider("relevance", "Faint-light cutoff", 0f, 0.1f, 0.005f, 0.01f, tier: Tier.ChatOnly,
                             help: "A light dimmer than this share of the brightest light at a pixel fades out and is skipped. Decided per pixel, so it never draws a grid. 0 = every light.")
                     .Ladder(Quality.Lighting, 0.03f, 0.02f, 0.01f, 0.005f);
        // CX-188 budget: the PERCEPTUAL reach. Where `binreach` scales every light's authored radius by one
        // number -- a look change, because the engine's curve still sits at ~14% of its peak at 0.8R -- this is
        // an ABSOLUTE brightness, so each light's walked and binned radius ends where that light stops being
        // visible. Chosen from first principles: at the foot of the sRGB ramp one 8-bit code step is 1/(12.92 x
        // 255) = 0.00030 display-linear, and a surface's own albedo and N.L take the light's contribution to
        // well under half of that again, so a light cut at 0.0003 cannot move a pixel by a code value.
        // <para>Measured honestly: on the engine's RANGE-NORMALISED curve that cut lands inside the last ~0.1%
        // of an ordinary lamp's radius, so it saves nothing there. What it does remove entirely is the dim end
        // -- a light mid-fade (light.fadeout), a faint glow, a lamp under full daylight -- whose cut radius
        // collapses to zero and which then costs no bin entry, no walk and no shadow at all.</para>
        Floor = G.Slider("floor", "Faint-light floor", 0f, 0.004f, 0.0001f, 0.0003f, tier: Tier.ChatOnly,
                         help: "A light stops reaching where its own brightness falls under this, instead of at its authored radius. "
                             + "The default is one display step at the darkest end, so the cut cannot be seen; raising it trades "
                             + "a visible dimming at the edge of every lamp's reach for a shorter walk. 0 = the authored radius.")
                 .Ladder(Quality.Lighting, 0.001f, 0.0005f, 0.0003f, 0.0003f);
        // CX-188 budget: the LIGHTING LOD. The three refinements Alloy adds to the engine's curve -- the
        // near-field core (#142), the sphere-sized specular lobe (#139) and the subsurface leak (#375) -- are
        // all shaped by a light you are standing in. On the fifth lamp down the street they are arithmetic
        // nobody can see, and the walk pays for them per light per pixel.
        // <para>Expressed as a SHARE of the pixel's brightest light, not as a count of the strongest N. A count
        // needs the Nth-largest contribution, which means loop-carried candidate slots in the hottest loop in
        // the renderer -- occupancy the feature would cost whether or not it is on, which is the lesson of the
        // reverted screen-space shadow mask. The share is light.relevance's own quantity, already computed:
        // continuous in position and in the light set, so a light drifting across the cut fades rather than
        // pops, and it costs three multiplies when it is off.</para>
        // <para>DEFAULT OFF. It is pop-free and it is cheap, but it is a LOOK change -- the measured saving is
        // roughly 10-15 of the ~100 scalar ops a light's body costs, and only on the faint tail -- and that is
        // not a trade to make to the shipped image without a live A/B. `/alloy bench` has the two steps.</para>
        FullModel = G.Slider("fullmodel", "Full lighting model above", 0f, 0.5f, 0.01f, 0f, tier: Tier.ChatOnly,
                             help: "Lights contributing less than this share of the brightest light at a pixel are drawn with the "
                                 + "plain falloff: no extra punch at the centre, no source-sized highlight, no light bleeding "
                                 + "through the surface. Cheaper where many lamps overlap. 0 = every light gets everything.");
        ReachFade = G.Toggle("reachfade", "reachfade", true, Tier.ChatOnly);
    }
}
