using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Why a frame is not Alloy's. Three states rather than a bool, because a bug report has to
/// distinguish them: ADR 0010's fourth bullet is exactly "this machine cannot run it" vs "this player chose
/// not to".</summary>
internal enum AlloyStand
{
    /// <summary>Alloy draws this frame.</summary>
    Alloy,
    /// <summary>The config asked for the engine renderer. Nothing is wrong.</summary>
    Choice,
    /// <summary>The device, the driver or the engine's own settings cannot support the Alloy frame.</summary>
    Incapable,
    /// <summary>There is no world frame to draw: no module, or the renderer never armed.</summary>
    Idle,
}

/// <summary>The one place a frame is decided to be Alloy's or the engine's, and it decides ONCE per session:
/// the config half is <c>alloy.enabled=0</c> in <c>cx.cfg</c>, the capability half is asked at the first present
/// and frozen the moment it answers. Nothing re-asks, so the two resource worlds are never warm at once -- a
/// refusal taken after the frame is Alloy's is therefore a <see cref="Fail"/>, never a mode change.</summary>
internal static class AlloyGate
{
    private static AlloyStand _stand = AlloyStand.Idle;
    private static string _why = "the first frame has not been latched yet";
    /// <summary>The config half, read once at <see cref="Arm"/>. Separate state from the capability half by
    /// ADR 0010, so "I asked for vanilla" never reads as "this machine cannot".</summary>
    private static bool _optedOut;
    /// <summary>The frozen verdict, once the capability half has answered.</summary>
    private static AlloyStand _decided = AlloyStand.Idle;
    private static bool _frozen;
    private static string _decidedWhy = "";
    /// <summary>A failure taken after the decision froze on Alloy. Reported, never acted on.</summary>
    private static string _failed = "";
    /// <summary>Does every engine offset Alloy reads carry a resolution for the platform we booted on?
    /// Asked once at <see cref="Arm"/> -- the answer is a property of the build, not of the frame.</summary>
    private static bool _layoutOk = true;
    private static ExtenderLog? _log;

    /// <summary>This term says there is no WORLD frame, nothing more. The GUI scenes are Alloy's on
    /// their own verdict (<see cref="GuiPass.Capturing"/>), main menu included.</summary>
    private const string NoModule = "no module is loaded, so there is no world frame (the GUI is GuiPass's)";

    /// <summary>The latched verdict every render site reads. True only in <see cref="AlloyStand.Alloy"/>.</summary>
    internal static bool Alloy => _stand == AlloyStand.Alloy;

    internal static AlloyStand Stand => _stand;

    /// <summary>Why this frame is not Alloy's, or <c>""</c> when it is. A constant string on every path --
    /// this is read from the frame.</summary>
    internal static string Reason => _why;

    /// <summary>Did the config ask for the engine renderer? The override half of ADR 0010's split.</summary>
    internal static bool OptedOut => _optedOut;

    /// <summary>Has the device been ruled out? False alone means nothing about what is rendering; pair it
    /// with <see cref="Stand"/>.</summary>
    internal static bool Capable => _stand != AlloyStand.Incapable && _failed.Length == 0;

    /// <summary>...and why not, for the bug report. Also carries a post-decision <see cref="Fail"/>.</summary>
    internal static string CapabilityReason =>
        _failed.Length != 0 ? _failed : _stand == AlloyStand.Incapable ? _why : "";

    /// <summary>A failure taken after the frame was already Alloy's. There is nothing behind it and no way to
    /// warm the engine's resource world mid-session, so this is a HARD FAILURE: said once, on screen and in
    /// the log, naming the restart. First reason wins -- everything after it is downstream.</summary>
    internal static void Fail(string why)
    {
        if (_failed.Length != 0) return;
        _failed = why;
        _log?.Info($"[alloygate] HARD FAILURE -- {why}");
        Notice.Show(_log, $"Alloy renderer failed: {why}. Set {Renderer.Enabled.Key}=0 in cx.cfg and restart to "
                        + "play on the engine renderer.");
    }

    /// <summary>Has a post-decision failure been latched?</summary>
    internal static bool Failed => _failed.Length != 0;

    /// <summary>The whole refusal set, in report order: configuration and choice first (they are not
    /// failures), then capability. Asked until it answers <see cref="AlloyStand.Alloy"/> or
    /// <see cref="AlloyStand.Choice"/>, then never again; allocation-free, every reason a literal. An
    /// <see cref="AlloyStand.Incapable"/> answer does not freeze: cold it means "not yet" as often as "never".</summary>
    private static AlloyStand Evaluate(out string why)
    {
        if (_optedOut)
        { why = "alloy.enabled=0 in cx.cfg, or Vulkan cannot start -- the engine renders"; return AlloyStand.Choice; }
        if (!DrawFunnelService.Armed)
        { why = "alloy.drawhooks is not batch in cx.cfg, so nothing is enrolled to draw"; return AlloyStand.Choice; }
        if (!PostChain.Configured)
        { why = "alloy.hdr=0 in cx.cfg, and the Alloy stages have no display-space variant"; return AlloyStand.Choice; }
        // ahead of the capability block: an opt-out reported as "this device cannot" misdirects the report
        if (GlContextService.OptedOut)
        { why = "alloy.gl46=0 in cx.cfg"; return AlloyStand.Choice; }
        // #456: the first capability term, and the only one answerable cold -- it is a fact about the binary,
        // so it is asked ahead of everything that needs a warm client. Incapable rather than Choice: the
        // player asked for nothing here.
        if (!_layoutOk)
        { why = "an engine offset Alloy reads has no resolution for this platform (docs/engine-layout.md)";
          return AlloyStand.Incapable; }
        // ...and only now. EVERY opt-out above returns from Initialize before _armed is set, so asking
        // "never armed" ahead of them reports the mechanism instead of the request -- which is the ADR 0010
        // misdirection this chain exists to prevent.
        if (!AlloyRenderer.ArmedForFrame)
        { why = "the Alloy renderer never armed (its hooks did not install)"; return AlloyStand.Idle; }
        // #458: same shape, one layer down. Alloy drawing inside the engine's RenderSinglePass was the
        // ownframe=0 mode, deleted as unusable -- so a shim that could not arm stands the whole path down.
        if (!RspShim.Armed)
        { why = "the RenderSinglePass shim never armed (an engine symbol or draw funnel did not resolve)"; return AlloyStand.Idle; }
        // Alloy draws the scene database and nothing else: no seam, no world to draw (SeamService says why).
        if (!CX.Alloy.Seam.SeamService.Db)
        { why = "the scene seam did not install (its facade self-check or a factory failed)"; return AlloyStand.Incapable; }
        // ...and ahead of it for the OTHER reason: the capability terms below are only answerable on a warm
        // client. The 4.6 context is created from a present inside a world frame, so asking them at the main
        // menu answers "no 4.6 context on this device" every single boot -- which is what the pre-#263 chain
        // avoided by putting this term here, and what putting it after them reintroduced.
        if (!GameGate.InModule) { why = NoModule; return AlloyStand.Idle; }
        if (!GlContextService.Available)
        { why = "no GL 4.6 share-listed context on this device"; return AlloyStand.Incapable; }
        if (!AlloyRenderer.DrawCapable)
        { why = "an Alloy program did not build, or glMultiDrawElementsBaseVertex/glBindBufferBase is missing";
          return AlloyStand.Incapable; }
        if (PostChain.Failed)
        { why = "the HDR target cannot arm on this device"; return AlloyStand.Incapable; }
        why = "";
        return AlloyStand.Alloy;
    }

    /// <summary>Only a POSITIVE answer decides. <see cref="AlloyStand.Idle"/> and
    /// <see cref="AlloyStand.Incapable"/> both mean "not yet" as often as "never" -- the main menu is Idle,
    /// and the 4.6 context is created from a present inside a world frame -- so freezing either would be a
    /// renderer that never arms.</summary>
    private static bool Freezes(AlloyStand v) => v == AlloyStand.Alloy || v == AlloyStand.Choice;

    /// <summary>The latch, from the present's Start phase -- which sits between scene frames, so a frame is
    /// wholly one renderer's or wholly the other's. Once <see cref="_decided"/> holds a verdict the only term
    /// that still moves is whether there is a world frame at all.</summary>
    internal static void OnFrameStart()
    {
        AlloyStand was = _stand;
        string wasWhy = _why;

        if (!_frozen)
        {
            AlloyStand v = Evaluate(out string vWhy);
            if (Freezes(v)) { _frozen = true; _decided = v; _decidedWhy = vWhy; }
            else { _stand = v; _why = vWhy; Report(was, wasWhy); return; }
        }
        bool idle = _decided == AlloyStand.Alloy && !GameGate.InModule;
        _stand = idle ? AlloyStand.Idle : _decided;
        _why = idle ? NoModule : _decidedWhy;
        Report(was, wasWhy);
    }

    /// <summary>Log and, for a capability verdict, tell the player. Reference compare, not value: every
    /// reason is a literal, and this runs on every frame.</summary>
    private static void Report(AlloyStand was, string wasWhy)
    {
        if (_stand == was && ReferenceEquals(_why, wasWhy)) return;
        if (_stand == AlloyStand.Alloy) { _log?.Info("[alloygate] ALLOY -- the frame is Alloy's"); return; }
        _log?.Info($"[alloygate] {_stand} -- {_why}");
        // Only a capability verdict is news: the config choice is the player's own, and Idle is the main menu.
        if (_stand == AlloyStand.Incapable)
            Notice.Show(_log, $"Alloy renderer off -- {_why}. The engine is rendering (vanilla mode).");
    }

    /// <summary>Read the config half and join the frame. Called from <c>AlloyRenderer.Initialize</c>, ahead
    /// of every stand-down -- bare <c>/alloy</c> reports this state even in a session that armed nothing.</summary>
    internal static void Arm(ExtenderLog log)
    {
        _log = log;
        // #456: the EMPTY class set -- every row in the table, not the classes Alloy names. An explicit list
        // would be a second copy of the table that drifts. The consequence, said out loud because the next
        // person to hit it will not be an Alloy dev: a future Unresolved row ANYWHERE, a 2D class included,
        // stands Alloy down on Windows. Sound only because CLAUDE.md makes Unresolved a transient state.
        _layoutOk = EngineLayout.Require(log, "Alloy");
        // Restart knob: Boot, not V -- the verdict is frozen for the session whatever the panel says later.
        _optedOut = !Renderer.On;
        log.Info(_optedOut
            ? "[alloygate] Alloy is off (alloy.enabled=0, or Vulkan cannot start) -- the engine renders everything "
              + "this session"
            : "[alloygate] Alloy requested (alloy.enabled=0 in cx.cfg and restart for the engine renderer)");
        SelfCheck(log);
        FrameRegistry.Global.Register(FramePhase.Start, 1, OnFrameStart, "AlloyGate.verdict", log);
    }

    /// <summary>Off-device: the decision freezes once and a post-decision failure does not move it. That last
    /// property is the whole point of the ticket, and nothing on a live client can demonstrate it cheaply.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        (bool optedOut, bool frozen, AlloyStand decided, string decidedWhy, string failed, AlloyStand stand,
         string why, bool layoutOk) saved =
            (_optedOut, _frozen, _decided, _decidedWhy, _failed, _stand, _why, _layoutOk);
        _layoutOk = true;   // #456: the check drives Evaluate, and a real stand-down here is not its subject
        // Fail() speaks to the chatbox as well as the log, and the sink is independent of _log: without
        // this the check would put a fabricated failure notice on screen the moment service init order
        // happens to put a sink in place first.
        ExtenderLog? quiet = _log; _log = null;
        Action<string>? sink = Notice.Sink; Notice.Sink = null;
        try
        {
            _optedOut = true; _frozen = false; _decided = AlloyStand.Idle; _decidedWhy = ""; _failed = "";
            OnFrameStart();
            check.Expect(!Alloy && Stand == AlloyStand.Choice, "the config opt-out is a Choice, not a device verdict");
            check.Expect(Capable, "...and it says nothing about the device");
            _optedOut = false;
            OnFrameStart();
            check.Expect(Stand == AlloyStand.Choice, "the decision is frozen: clearing the config does not un-decide it");

            // ...but a NEGATIVE answer must not freeze, or a client that boots to the main menu (Idle) or
            // whose 4.6 context is not up yet (Incapable) would never arm at all. This is the one the live
            // client caught: the capability block asked at the menu and put a dropout notice on screen.
            // Asserted on the rule rather than through Evaluate, whose answer here is whatever the host
            // running the check happens to be -- which is exactly the coupling that made the first version
            // of this assertion pass on one host and fail on another.
            check.Expect(Freezes(AlloyStand.Alloy) && Freezes(AlloyStand.Choice),
              "a positive verdict decides the session");
            check.Expect(!Freezes(AlloyStand.Idle) && !Freezes(AlloyStand.Incapable),
              "an Idle or Incapable verdict does not freeze the decision");

            _frozen = true; _decided = AlloyStand.Alloy; _decidedWhy = ""; _stand = AlloyStand.Alloy;
            Fail("first"); Fail("second");
            check.Expect(CapabilityReason == "first", "the first failure reason wins and later ones are ignored");
            check.Expect(Alloy, "a failure after the decision leaves the frame Alloy's -- there is nothing behind it");
            OnFrameStart();
            check.Expect(Stand != AlloyStand.Incapable, "...and does not move the next verdict either: it is restart-only");
        }
        finally
        {
            (_optedOut, _frozen, _decided, _decidedWhy, _failed, _stand, _why, _layoutOk) = saved;
            _log = quiet; Notice.Sink = sink;
        }

        log.Info(check.Ok ? "AlloyGate.SelfCheck: passed" : "AlloyGate.SelfCheck: FAILED");
    }
}
