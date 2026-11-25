using System;
using System.Globalization;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The <c>alloy</c> group (spec §4.3 Renderer), and the storage every Alloy boot gate and world-pass
/// dial reads: the Restart rows are read through <c>Boot</c> by the gate or pass that latches them, the Live
/// rows per frame by the pass that owns them. Touched from <see cref="RenderCommands.Register"/> so the group
/// declares at Initialize, ahead of every stand-down: bare <c>/alloy</c> must answer in a dead session.</summary>
internal static class Renderer
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, UpscaleAi, Smooth, RayTrace;
    internal static readonly Knob<float> TileBlend;
    /// <summary>Is Alloy drawing this session? Then it draws the CX scene database (ADR 0028) and no engine
    /// <c>Scene</c>/<c>Gob</c>/<c>Part</c> exists. Off is vanilla, whole: a refused Vulkan probe or a seam that stood
    /// down. The seam initializes before every other service, so they all read its verdict.</summary>
    internal static bool On => Compose(Requested, OnDevice, seamRefused: CX.Alloy.Seam.SeamService.StoodDown);

    /// <summary>Did the player ask for Alloy, and can Vulkan start? What the seam asks before it installs.</summary>
    internal static bool Requested => Enabled.Boot && VulkanUp();

    /// <summary>A real client, not the self-check host: off-device the seam never installs, and the suites run.</summary>
    private static bool OnDevice
    {
        get
        {
            if (_boot.Device < 0) _boot.Device = CX.Framework.Native.Sym(SymbolMap.AppManager) != IntPtr.Zero ? 1 : 0;
            return _boot.Device == 1;
        }
    }

    /// <summary>Alloy is on when it was requested and, in a client, the seam did not stand down.</summary>
    internal static bool Compose(bool requested, bool onDevice, bool seamRefused)
        => requested && !(seamRefused && onDevice);

    /// <summary>The session's Vulkan verdict, taken once at the first <see cref="Requested"/>: -1 untaken, 0 refused, 1 up;
    /// and whether this is a real client, the same way.</summary>
    private sealed class BootState { public int VkUp = -1, Device = -1; }

    private static readonly BootState _boot = new();

    /// <summary>Can Vulkan run here? Its refusal is said once, with the vanilla fallback.</summary>
    private static bool VulkanUp()
    {
        if (_boot.VkUp >= 0) return _boot.VkUp == 1;
        if (KnobGroup.Log is not ExtenderLog log) return true;
        // off-device (the self-check host): no client renders, so there is no Vulkan verdict to take and the suites run
        if (!OnDevice) { _boot.VkUp = 1; return true; }
        string? why = CX.Graphics.Rhi.VkBackend.Refusal(log);
        _boot.VkUp = why == null ? 1 : 0;
        if (why != null)
            log.Always($"Renderer: Vulkan cannot start ({why}) -- Alloy stands down and the game runs vanilla this session");
        return why == null;
    }
    internal static readonly Knob<float> Lod, UpscaleProtect;
    /// <summary>How many layers of itself a see-through creature draws as, under the list resolve that composites it once.</summary>
    internal static readonly Knob<float> Ghost;
    internal static readonly Knob<string> Highlight, DrawHooks, DumpTex;
    /// <summary>The alpha-to-coverage layer's sample count (<see cref="CoverageLayer"/>), and whether it
    /// shapes the mask itself (per-primitive rotation, so stacked equal-alpha cards both show).</summary>
    internal static readonly Knob<int> A2c;
    internal static readonly Knob<bool> A2cMask;
    internal static readonly Knob<bool> PltVerify;
    /// <summary>Content stages' native versions (<c>cx_&lt;stage&gt;</c>) and the images and passes they declare.</summary>
    internal static readonly Knob<bool> Native;
    internal static readonly Knob<bool> TexPreview;
    internal static readonly Knob<bool> Bcn, Hdr, OwnedTex, TexSingle, DdsRead, HandleDrop,
                                        Spirv, VanillaBridge, HostFs, OwnSkin, DumpShaders, GpuDebug, EngineZones,
                                        Gui, Movie, OneQueue;
    internal static readonly Knob<int> Upscale, UpscaleFactor, UpscaleModel, Border, Oit, TexCache, BcnDrip, TexDelete, MaxElements, RasterRead,
                                       ClusterCap, SkipBuckets, FrameDiff, ParityDump, StaleAudit;
    /// <summary>MiB the device-local heaps are capped at, as a smaller card would be; 0 caps nothing.</summary>
    internal static readonly Knob<int> VramCap;
    internal static readonly Knob<bool> Light, Prepass, Retest, HlDiag, Poll, PrepassKey, PrepassFeat, IdTarget,
                                        BlendDepth, VisList, WashTest, DepthEqual;
    // ---- CX-103/104/105: the scene pipeline's dials ----
    internal static readonly Knob<bool> CullWatch;
    internal static readonly Knob<float> LodHyst;
    /// <summary>Metres from the camera past which a character's pose is advanced every second pass, and past
    /// twice that every fourth, with its swaying parts held at rest. 0 poses everything every frame.</summary>
    internal static readonly Knob<float> AnimDist;
    /// <summary>Authored <c>.lod</c> alternates, loaded and switched for every object. On whatever the game's own
    /// LOD option says: a cfg-only off switch for debugging, not a player choice.</summary>
    internal static readonly Knob<bool> AuthoredLod;
    internal static readonly Knob<int> Viz, Threads, Cull, HlChan, OitViz;
    internal static readonly KnobButton Consts, FrameDumpBtn, ShaderDump, Snap;
    /// <summary>Alloy draws this session, on Vulkan: the one backend.</summary>
    internal static bool OnVulkan => On;
    /// <summary>Arm <see cref="CX.Alloy.Db.HitProbe"/>, the live hit-test instrument.</summary>
    internal static readonly Knob<bool> HitCheck;

    /// <summary>The highlight mode the pass reads per frame: the stock glow, or the owned outline + fill.</summary>
    internal static bool OutlineHighlight => Highlight.V == "outline";

    /// <summary>The Renderer status line: which renderer this session is running, and why when it is not
    /// Alloy. The renderer is chosen once at boot (ADR 0018's amendment to ADR 0010), so the line names the
    /// failure clause too -- without it "I set alloy.enabled=0 and nothing changed" reads the same as "this
    /// machine cannot run the Alloy renderer".</summary>
    internal static string Status()
    {
        string s = AlloyGate.Alloy ? "Running: Alloy"
                 : AlloyGate.Stand == AlloyStand.Idle ? $"World: idle — {AlloyGate.Reason}"
                 : $"Running: Vanilla — {AlloyGate.Reason}";
        if (AlloyGate.Failed) s += $" — the Alloy renderer FAILED: {AlloyGate.CapabilityReason}";
        return s + " — " + GuiPass.Stats() + "; " + NuiFlush.Stats() + "; " + NullGl.Status();
    }

    /// <summary>The panel's line (ADR 0030): nothing while CX draws the world, and why it does not when it
    /// does not. Without it "I set <c>alloy.enabled=0</c>" and "this machine cannot run the renderer" read the
    /// same -- every Graphics setting silently inert either way. Idle is the main menu, which is neither.</summary>
    internal static string PanelStatus()
        => AlloyGate.Alloy || AlloyGate.Stand == AlloyStand.Idle ? ""
         : "CX is not drawing the 3D world, so nothing in Graphics applies: "
         + (AlloyGate.Failed ? AlloyGate.CapabilityReason : AlloyGate.Reason) + ".";

    private static string AutoInt(int v) => v < 0 ? "auto" : v.ToString(CultureInfo.InvariantCulture);
    private static (bool, int) ParseAutoInt(string s)
        => s == "auto" ? (true, -1)
         : int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? (true, i) : (false, 0);

    static Renderer()
    {
        KnobNeeds.WatchAlloy(static () => On);
        Quality.Touch();
        G = KnobGroup.Declare("alloy", "Graphics", "Quality", master: "Enable the Alloy renderer",
            masterHelp: "Draw the 3D world with CX's own Vulkan renderer: real-time shadows, HDR, grass, post-processing. "
                      + "Off, or on a machine Vulkan cannot run, the game's own renderer draws the world and nothing under Graphics applies. "
                      + "Takes effect at the next launch.",
            masterEffect: Effect.Restart, masterTier: Tier.ChatOnly, order: 302, title: "Display");
        Enabled = G.Master!;
        G.Status = Status;
        G.PanelStatus = PanelStatus;

        Highlight = G.Dropdown("highlight", "Selection highlight", new[] { "glow", "outline" }, "outline",
                               help: "How a selected or hovered object is marked: an outline with a light fill, or a soft glow around the object.");
        Lod = G.Slider("lod", "Skip models smaller than", 0f, 8f, 0.5f, 1f, "px", Tier.Advanced,
                       help: "Distant models whose projected size is under this many pixels are not drawn at all.",
                       changed: AlloyRenderer.InvalidateRecords)
               .Ladder(Quality.Detail, 2f, 1.5f, 1f, 0.5f);
        A2c = G.Dropdown("a2c", "Alpha-to-coverage samples", new[] { 0, 2, 4, 8 }, 4, new[] { "off", "2x", "4x", "8x" }, tier: Tier.Advanced,
                         help: "Materials that ask for alpha-to-coverage (often hair) get soft, layered edges from this many "
                             + "samples per pixel. Off draws them with the hard cutout edge every other leaf has.");
        A2cMask = G.Toggle("a2cmask", "a2cmask", false, Tier.ChatOnly);
        Ghost = G.Slider("ghost", "See-through creature thickness", 1f, 3f, 0.1f, 1.2f, "×", Tier.Advanced,
                         help: "A creature an effect such as Invisibility makes see-through draws as this many layers of "
                             + "itself, so its shape still shows. 1 is the effect's own transparency. A fully invisible "
                             + "creature stays invisible.");
        TexPreview =G.Toggle("texpreview", "Low-resolution texture previews", true, Tier.Advanced, Effect.Live,
                              help: "While an area streams in, every texture first shows a small blurred version of itself, then sharpens when the full one lands. Off: a texture shows white until it has fully loaded.");

        G.Section("");
        Gui = G.Toggle("gui", "Draw the interface with Alloy", true, Tier.ChatOnly,
                       help: "Menus, panels, text, the minimap and the cursor are drawn by CX's renderer from what the game "
                           + "lays out. Off, the game's own renderer draws them again.");
        Border = G.Slider("border", "Neighbouring-area ring", 5, 40, 5, 15, "tiles", Tier.ChatOnly, Effect.Restart,
                          help: "Tiles drawn past the area edge so the horizon is not empty; the stock game draws 5.");
        Oit = G.Dropdown("oit", "Transparency", new[] { 0, 1 }, 1, new[] { "sorted", "weighted" }, tier: Tier.ChatOnly,
                         help: "How overlapping transparent surfaces are combined.", changed: AlloyRenderer.InvalidateRecords);
        Upscale = G.Dropdown("upscale", "Upscale small textures", new[] { 0, 256, 512, 1024 }, 512,
                             new[] { "off", "up to 256 px", "up to 512 px", "up to 1024 px" }, tier: Tier.Advanced,
                             changed: CX.Alloy.Db.TexRegistry.UpscaleChanged,
                             help: "Model textures this size or smaller are rebuilt at twice the resolution as they load, edges kept crisp "
                                 + "and lightly sharpened, so stock textures hold up close. Each one takes four times the video memory, "
                                 + "and loading takes a little longer. Changes apply at once, so it doubles as a before-and-after switch.");
        UpscaleAi = G.Toggle("upscaleai", "AI texture upscaling", true, Tier.Advanced, changed: CX.Alloy.Db.TexRegistry.UpscaleChanged,
                             help: "Upscale small textures with an AI model on the graphics card (Real-ESRGAN, through DirectML), which "
                                 + "rebuilds edges and fine detail far better than the classic way. Each texture is done once, in the "
                                 + "background, and kept on disk. Needs a Direct3D 12 GPU; elsewhere the classic way is used.");
        UpscaleFactor = G.Dropdown("upscalefactor", "AI upscale size", new[] { 2, 4 }, 4, new[] { "2x", "4x" }, tier: Tier.Advanced,
                                   changed: CX.Alloy.Db.TexRegistry.UpscaleChanged,
                                   help: "How far the AI enlarges a texture of 512 px or less. 4x is the model's own scale and the sharpest, "
                                       + "for sixteen times that texture's memory; larger textures and the classic way always take 2x.");
        UpscaleModel = G.Dropdown("upscalemodel", "AI upscale model", new[] { 0, 1 }, 1, new[] { "compact", "full" }, tier: Tier.Advanced,
                                  changed: CX.Alloy.Db.TexRegistry.UpscaleChanged,
                                  help: "Which AI model rebuilds the textures. Full rebuilds fine surface grain -- stone, weave, wood -- and takes "
                                      + "about ten times the graphics card's time per texture, once; compact is quick and smooths fine texture "
                                      + "into clean shapes.");
        UpscaleProtect = G.Slider("upscaleprotect", "AI upscale: keep smooth areas smooth", 0f, 1f, 0.05f, 1f, tier: Tier.Advanced,
                                  changed: CX.Alloy.Db.TexRegistry.UpscaleChanged,
                                  help: "How far flat gradients keep the classic upscale, where an AI model can invent ripples. 0 takes the "
                                      + "AI everywhere, sharpest on soft textures; 1 protects every smooth area.");
        RayTrace = G.Toggle("raytrace", "Ray-traced reflections", true, Tier.Advanced, Effect.Restart,
                            help: "On a graphics card with ray tracing (NVIDIA RTX, recent AMD or Intel), reflections in water, wet ground "
                                + "and polished surfaces show what is really around them -- off screen and behind the camera included -- "
                                + "where screen-space reflection runs out. Elsewhere the reflections stay as they are.");
        TileBlend = G.Slider("tileblend", "Blend ground across tile edges", 0f, 1f, 0.05f, 1f, tier: Tier.Advanced,
                             help: "Where two tiles' ground textures meet on the area's 10 m grid, they blend into each other over a "
                                 + "ragged band instead of meeting in a ruled line. Ground only: walls, props and figures keep their edges. "
                                 + "Needs Screen-space reflections on.");
        Smooth = G.Toggle("smooth", "Smooth rounded models", true, Tier.Advanced, Effect.Restart,
                          help: "Low-polygon rounded shapes on tiles and placeables -- columns, barrels, rocks, arches -- are curved "
                              + "out between their corners as they load, so their outlines stop looking faceted. Hard edges stay sharp. "
                              + "Armour and clothing parts round off too; heads and creatures are left as they are.");
        TexCache = G.Dropdown("texcache", "Texture memory budget", new[] { -1, 512, 1024, 2048, 4096, 8192 }, -1, tier: Tier.ChatOnly,
                              effect: Effect.Restart, format: AutoInt, parse: ParseAutoInt);
        Native = G.Toggle("native", "Native shader stages", true, Tier.ChatOnly, changed: () => Programs.State.SpliceStale = true,
                          help: "Run a content shader's native version (cx_<stage>) where the module ships one; off runs the "
                              + "stage the engine runs.");
        Bcn = G.Toggle("bcn", "Compress textures on load", true, Tier.ChatOnly, Effect.Restart,
                       help: "Re-encodes uncompressed textures as BC1/BC3: 3–4× less VRAM, a little load-time CPU.");

        Hdr = G.Toggle("hdr", "hdr", true, Tier.ChatOnly, Effect.Restart);
        DrawHooks = G.Dropdown("drawhooks", "drawhooks", new[] { DrawFunnelService.DrawModeBatch, DrawFunnelService.DrawModeStats },
                               DrawFunnelService.DrawModeBatch, tier: Tier.ChatOnly, effect: Effect.Restart);
        OwnedTex = G.Toggle("ownedtex", "ownedtex", true, Tier.ChatOnly, Effect.Restart);
        OneQueue = G.Toggle("onequeue", "onequeue", false, Tier.ChatOnly, Effect.Restart);
        VramCap = G.Slider("vramcap", "VRAM cap", 0, 65536, 256, 0, "MiB", Tier.ChatOnly, Effect.Restart,
                           help: "Caps the video memory the renderer may use at this many MiB, as a smaller card would; 0 caps nothing.");
        // CX-117: compare Alloy's whole PLT chain (file decode + master palette + scheme rows + resolve)
        // against the engine's own CPU composite, once per PLT texture at its first build. Cold, and
        // off by default: it reads every PLT file a second time.
        PltVerify = G.Toggle("pltverify", "pltverify", false, Tier.ChatOnly, Effect.Restart);
        BcnDrip = G.Slider("bcndrip", "bcndrip", 1, 64, 1, 1, tier: Tier.ChatOnly, effect: Effect.Restart);
        TexSingle = G.Toggle("texsingle", "texsingle", true, Tier.ChatOnly, Effect.Restart);
        // CX-52 step 1: off until a live session reads zero MISMATCHED on the dds: note (the ticket's acceptance).
        DdsRead = G.Toggle("ddsread", "ddsread", true, Tier.ChatOnly, Effect.Restart);
        // CX-52 step 2: 0 = the engine decodes TGA/PLT, 1 = the engine's decode stands and Alloy's is checked
        // beside it, 2 = Alloy's decode is what the engine gets (live-clean 2026-09-26: 684 TGA + 830 PLT, 0 errors).
        RasterRead = G.Slider("rasterread", "rasterread", 0, 2, 1, 2, tier: Tier.ChatOnly, effect: Effect.Restart);
        TexDelete = G.Slider("texdelete", "texdelete", -1, 1, 1, -1, tier: Tier.ChatOnly, effect: Effect.Restart);
        HandleDrop = G.Toggle("handledrop", "handledrop", true, Tier.ChatOnly, Effect.Restart);
        MaxElements = G.Slider("maxelements", "maxelements", 0, 1 << 24, 1, EnginePools.MaxElementsDefault, tier: Tier.ChatOnly, effect: Effect.Restart);
        Spirv = G.Toggle("spirv", "spirv", false, Tier.ChatOnly, Effect.Restart);
        VanillaBridge = G.Toggle("vanillabridge", "vanillabridge", true, Tier.ChatOnly, Effect.Restart);
        HostFs = G.Toggle("hostfs", "hostfs", true, Tier.ChatOnly, Effect.Restart);
        OwnSkin = G.Toggle("ownskin", "ownskin", true, Tier.ChatOnly, Effect.Restart);
        ClusterCap = G.Slider("clustercap", "clustercap", 8, 128, 1, 128, tier: Tier.ChatOnly, effect: Effect.Restart);
        DumpTex = G.Text("dumptex", "dumptex", "", Tier.ChatOnly, Effect.Restart);
        DumpShaders = G.Toggle("dumpshaders", "dumpshaders", false, Tier.ChatOnly, Effect.Restart);
        GpuDebug = G.Toggle("gpudebug", "gpudebug", false, Tier.ChatOnly, Effect.Restart);
        // CX-120: the pass number to dump the frame's whole input set at, 0 = off. A pass NUMBER rather than a
        // toggle because the first pass of a session is before the camera is placed, and the settled frame is
        // the one worth comparing between the two scene layers.
        FrameDiff = G.Slider("framediff", "framediff", 0, 100000, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
        // CX-120: the world pass to dump every nearby creature's composition at, 0 = off. Same arming as
        // framediff: the first pass of an area is before the body parts are grafted.
        ParityDump = G.Slider("paritydump", "paritydump", 0, 100000, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
        // CX-120: frames between staleness audits of the DB-mode draw records, 0 = off. A PERIOD rather than
        // a toggle because the audit walks every live slot and diffs three GPU mirrors: once a second is a
        // diagnostic, every frame is a different frame.
        StaleAudit = G.Slider("staleaudit", "staleaudit", 0, 100000, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
        SkipBuckets = G.Slider("skipbuckets", "skipbuckets", 0, 3, 1, 3, tier: Tier.ChatOnly, effect: Effect.Restart);
        EngineZones = G.Toggle("enginezones", "enginezones", true, Tier.ChatOnly, Effect.Restart);
        // CX-99: 0 hands every movie frame back to the engine's own quad. Live, because a movie is the one
        // surface a player can be stuck staring at with no way to restart into a different config.
        Movie = G.Toggle("movie", "movie", true, Tier.ChatOnly);

        // The world pass's live dials (the former /draw): a dial can change what a persistent record MEANS
        // (#242), and one dirty-all on a chat command is free, so every one of them invalidates.
        Light = G.Toggle("light", "light", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Prepass = G.Toggle("prepass", "prepass", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Viz = G.Slider("viz", "viz", 0, AlloyRenderer.LightVizMax, 1, 0, tier: Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Threads = G.Slider("threads", "threads", 0, StagePool.MaxChunks - 1, 1, AlloyRenderer.DefThreads, tier: Tier.ChatOnly,
                           changed: () => { StagePool.Rearm(); AlloyRenderer.InvalidateRecords(); });
        Cull = G.Slider("cull", "cull", CullPass.ModeOff, CullPass.ModeHiZ, 1, CullPass.ModeHiZ, tier: Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Retest = G.Toggle("retest", "retest", CullPass.DefRetest, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        HlDiag = G.Toggle("hldiag", "hldiag", false, Tier.ChatOnly);
        HlChan = G.Slider("hlchan", "hlchan", 0, 31, 1, CX.Alloy.Highlight.DefHlChannels, tier: Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        Poll = G.Toggle("poll", "poll", false, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        PrepassKey = G.Toggle("prepasskey", "prepasskey", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        PrepassFeat = G.Toggle("prepassfeat", "prepassfeat", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        DepthEqual = G.Toggle("depthequal", "depthequal", true, Tier.ChatOnly);
        IdTarget = G.Toggle("idtarget", "idtarget", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        BlendDepth = G.Toggle("blenddepth", "blenddepth", true, Tier.ChatOnly);
        // the GL meters time GL work, of which a Vulkan session has none
        VisList = G.Toggle("vislist", "vislist", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        WashTest = G.Toggle("washtest", "washtest", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        OitViz = G.Slider("oitviz", "oitviz", 0, 3, 1, 0, tier: Tier.ChatOnly);

        // The flicker hunt's GPU half: every colour dispatch's kept total, read back fenced and never waited
        // on. Off by default -- a per-frame read of a GPU-written buffer is a real wait on Mesa.
        CullWatch = G.Toggle("cullwatch", "cullwatch", false, Tier.ChatOnly);
        LodHyst = G.Slider("lodhyst", "lodhyst", 0f, 1f, 0.01f, 0.1f, tier: Tier.ChatOnly);
        AnimDist = G.Slider("animdist", "animdist", 0f, 160f, 5f, 30f, "m", tier: Tier.ChatOnly,
                            help: "Characters farther away than this move their limbs every second frame, and "
                                + "past twice this every fourth, with their cloaks and hair held still. Where "
                                + "they stand, what they do and when is unchanged. Anything out of view pauses its animation "
                                + "until you look at it. 0 animates everything every frame.");
        AuthoredLod = G.Toggle("authoredlod", "authoredlod", true, Tier.Hidden);

        Consts = G.Button("consts", "consts", AlloyRenderer.LogScriptables, Tier.ChatOnly);
        FrameDumpBtn = G.Button("framedump", "framedump", FrameDump.Arm, Tier.ChatOnly);
        G.Button("bench", "bench", AlloyRenderer.BenchArm, Tier.ChatOnly,
                 help: "Hold the camera still: steps the GPU-cost settings, measures each, restores them, logs a [bench] table.");
        ShaderDump = G.Button("shaderdump", "shaderdump", ShaderStages.DumpToLog, Tier.ChatOnly);
        Snap = G.Button("snap", "snap", FrameTap.Arm, Tier.ChatOnly);
        // CX-89: every visible door's fade alpha against mindooralpha -- the instrument for "doors still
        // take a click", which no screenshot shows.
        // CX-118: chain the engine's own Scene::HitCheck to measure what a vanilla cast costs and to print
        // the records it fills -- the live cross-check on the CHitInfo layout rows and the only number the
        // CX cast's own benchmark can honestly be compared against.
        HitCheck = G.Toggle("hitcheck", "hitcheck", false, Tier.ChatOnly, Effect.Restart);
        // CX-118: re-run the pick benchmark on demand. The boot-time run is measured against a CPU still
        // loading an area, so it reads pessimistically; this is the settled number. It REFUSES while the
        // scene database holds records, because its fixture writes to the real tables.
        G.Button("hitbench", "hitbench", CX.Alloy.Db.HitTestCheck.BenchArm, Tier.ChatOnly,
                 help: "Re-measure what a CX hit-test cast costs, now, and log it next to the engine's.");
    }
}
