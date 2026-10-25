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
    internal static readonly Knob<bool> Enabled;
    /// <summary>Is Alloy drawing this session? Then it draws the CX scene database (ADR 0028): the seam is installed
    /// and no engine <c>Scene</c>/<c>Gob</c>/<c>Part</c> exists. Off is vanilla. <c>alloy.enabled</c>'s Boot value,
    /// frozen for the session.</summary>
    internal static bool On => Enabled.Boot;
    internal static readonly Knob<float> Scale, Lod;
    /// <summary>How strongly the cutout test makes up for the alpha a mip averages away (<c>cx_alphamip.glsl</c>).</summary>
    internal static readonly Knob<float> CutMip;
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
    internal static readonly Knob<bool> Bcn, Hdr, Gl46, OwnedTex, TexSingle, DdsRead, HandleDrop,
                                        Spirv, VanillaBridge, HostFs, OwnSkin, DumpShaders, GlDebug, EngineZones,
                                        Gui, Movie;
    internal static readonly Knob<int> Border, Oit, OitLayers, TexCache, Bindless, BcnDrip, TexDelete, MaxElements, RasterRead,
                                       ClusterCap, GlSync, SkipBuckets, FrameDiff, ParityDump, StaleAudit;
    internal static readonly Knob<bool> Light, Prepass, Retest, HlDiag, Poll, PrepassKey, PrepassFeat, IdTarget,
                                        BlendDepth, GpuMeter, VisList, WashTest, DepthEqual;
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
    // ---- CX-120 seam-scene ----
    /// <summary>ADR 0028 section 5's render thread. Boot, and default OFF until the live harness clears it:
    /// what it moves is Alloy's whole GL context to a second thread, and the things that still reach the
    /// ENGINE's context on the game thread (<c>CX.Alloy.Db.RenderThread</c>'s remarks) are an ordering
    /// question the harness has to answer, not a correctness one the build can.</summary>
    internal static readonly Knob<bool> RenderThread;
    /// <summary>A pass on the render seam submits each command as it records it, instead of its whole list at the
    /// end: the same GL calls in the same order either way, so the A/B measures recording, not output.</summary>
    internal static readonly Knob<bool> RhiImmediate;
    /// <summary>Which device the render seam's passes record onto: GL 4.6, or Vulkan rendering into images GL
    /// imports. Boot, and not a render mode: Vulkan replaces GL Alloy pass by pass.</summary>
    internal static readonly Knob<string> Backend;
    internal const string BackendGl = "gl", BackendVk = "vk";
    /// <summary>The seam runs on Vulkan this session.</summary>
    internal static bool OnVulkan => Backend.Boot == BackendVk;
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
        return s + " — " + GuiPass.Stats() + "; " + NuiFlush.Stats();
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
    private static string AutoOnOff(int v) => v < 0 ? "auto" : v != 0 ? "on" : "off";
    private static (bool, int) ParseAutoOnOff(string s) => s switch
    {
        "auto" or "-1" => (true, -1),
        "on" or "1" => (true, 1),
        "off" or "0" => (true, 0),
        _ => (false, 0),
    };

    static Renderer()
    {
        Quality.Touch();
        G = KnobGroup.Declare("alloy", "Graphics", "Quality", master: "Enable the Alloy renderer",
            masterHelp: "Draw the 3D world with CX's own OpenGL 4.6 renderer: real-time shadows, HDR, grass, post-processing. "
                      + "Off, the game's own renderer draws the world and nothing under Graphics applies. "
                      + "Takes effect at the next launch.",
            masterEffect: Effect.Restart, masterTier: Tier.ChatOnly, order: 302, title: "Display");
        Enabled = G.Master!;
        G.Status = Status;
        G.PanelStatus = PanelStatus;

        Scale = G.Slider("scale", "Render scale", 0.25f, 2f, 0.05f, 1f,
                         help: "Size of the 3D frame relative to your screen. Below 1 renders fewer pixels and upscales (faster); "
                             + "above 1 supersamples (sharper, slower). The UI is never scaled.");
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
        CutMip = G.Slider("cutmip", "Distant cut-out fill", 0f, 1f, 0.05f, 0.25f, tier: Tier.Advanced,
                          help: "Leaves, fences and other cut-out textures thin out and shimmer with distance as their alpha "
                              + "averages away. This keeps them as full as they are up close. 0 is the stock test.");
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
        Oit = G.Dropdown("oit", "Transparency", new[] { 0, 1, 2 }, 2, new[] { "sorted", "weighted", "linked lists" }, tier: Tier.ChatOnly,
                         help: "How overlapping transparent surfaces are combined.", changed: AlloyRenderer.InvalidateRecords);
        OitLayers = G.Dropdown("oitlayers", "Transparency layers per pixel", new[] { 1, 2, 3, 4, 6, 8 }, 3, tier: Tier.ChatOnly,
                               effect: Effect.Restart, help: "Memory reserved for linked-list transparency; ~33 MB per layer at 1080p.");
        TexCache = G.Dropdown("texcache", "Texture memory budget", new[] { -1, 512, 1024, 2048, 4096, 8192 }, -1, tier: Tier.ChatOnly,
                              effect: Effect.Restart, format: AutoInt, parse: ParseAutoInt);
        Bindless = G.Dropdown("bindless", "Bindless textures", new[] { -1, 1, 0 }, -1, tier: Tier.ChatOnly, effect: Effect.Restart,
                              help: "Auto turns them off on Zink.", format: AutoOnOff, parse: ParseAutoOnOff);
        Native = G.Toggle("native", "Native shader stages", true, Tier.ChatOnly, changed: () => Programs._spliceStale = true,
                          help: "Run a content shader's native version (cx_<stage>) where the module ships one; off runs the "
                              + "stage the engine runs.");
        Bcn = G.Toggle("bcn", "Compress textures on load", true, Tier.ChatOnly, Effect.Restart,
                       help: "Re-encodes uncompressed textures as BC1/BC3: 3–4× less VRAM, a little load-time CPU.");

        Hdr = G.Toggle("hdr", "hdr", true, Tier.ChatOnly, Effect.Restart);
        Gl46 = G.Toggle("gl46", "gl46", true, Tier.ChatOnly, Effect.Restart);
        DrawHooks = G.Dropdown("drawhooks", "drawhooks", new[] { DrawFunnelService.DrawModeBatch, DrawFunnelService.DrawModeStats },
                               DrawFunnelService.DrawModeBatch, tier: Tier.ChatOnly, effect: Effect.Restart);
        OwnedTex = G.Toggle("ownedtex", "ownedtex", true, Tier.ChatOnly, Effect.Restart);
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
        GlDebug = G.Toggle("gldebug", "gldebug", false, Tier.ChatOnly, Effect.Restart);
        RenderThread = G.Toggle("renderthread", "renderthread", false, Tier.ChatOnly, Effect.Restart);
        Backend = G.Dropdown("backend", "Seam backend", new[] { BackendGl, BackendVk }, BackendGl, tier: Tier.ChatOnly,
                             effect: Effect.Restart,
                             help: "gl = the seam's passes run on GL 4.6; vk = they run on Vulkan inside the GL frame (the SSAO pilot).");
        RhiImmediate = G.Toggle("rhiimmediate", "Seam passes submit as they record", true, Tier.ChatOnly,
                                help: "1 = each command reaches GL as it is recorded; 0 = a pass's whole list at its end. Same GL calls either way.");
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
        GlSync = G.Slider("glsync", "glsync", 0, 1, 1, 0, tier: Tier.ChatOnly, effect: Effect.Restart);
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
        GpuMeter = G.Toggle("gpumeter", "gpumeter", true, Tier.ChatOnly, changed: () => GpuQuery.Enabled = GpuMeter!.V);
        GpuQuery.Enabled = GpuMeter.V;
        VisList = G.Toggle("vislist", "vislist", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        WashTest = G.Toggle("washtest", "washtest", true, Tier.ChatOnly, changed: AlloyRenderer.InvalidateRecords);
        OitViz = G.Slider("oitviz", "oitviz", 0, 5, 1, 0, tier: Tier.ChatOnly);

        // The flicker hunt's GPU half: every colour dispatch's kept total, read back fenced and never waited
        // on. Off by default -- a per-frame read of a GPU-written buffer is a real wait on Mesa.
        CullWatch = G.Toggle("cullwatch", "cullwatch", false, Tier.ChatOnly);
        LodHyst = G.Slider("lodhyst", "lodhyst", 0f, 1f, 0.01f, 0.1f, tier: Tier.ChatOnly);
        AnimDist = G.Slider("animdist", "animdist", 0f, 160f, 5f, 30f, "m", tier: Tier.ChatOnly,
                            help: "Characters farther away than this move their limbs every second frame, and "
                                + "past twice this every fourth, with their cloaks and hair held still. Where "
                                + "they stand, what they do and when is unchanged. 0 animates everything every frame.");
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
        HitCheck = G.Toggle("hitcheck", "hitcheck", false, Tier.ChatOnly);
        // CX-118: re-run the pick benchmark on demand. The boot-time run is measured against a CPU still
        // loading an area, so it reads pessimistically; this is the settled number. It REFUSES while the
        // scene database holds records, because its fixture writes to the real tables.
        G.Button("hitbench", "hitbench", CX.Alloy.Db.HitTestCheck.BenchArm, Tier.ChatOnly,
                 help: "Re-measure what a CX hit-test cast costs, now, and log it next to the engine's.");
    }
}
