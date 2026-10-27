using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Ui;
using CX.Alloy.Db;

namespace CX.Alloy;

/// <summary>The loading-screen hold. The engine's <c>SendPlayerToServerArea_AreaLoaded</c> is what places the
/// player (the server's <c>MovePlayerToArea</c> runs on nothing else, and no server clock runs while it
/// waits); the hook swallows it, an opaque CX window covers the frames until the area's baseline bakes are in,
/// then the send goes out from here. Exits: the bake is in, the player skips, or <see cref="CapSeconds"/>.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>The hold's ceiling, whatever the knob says: a server's login readiness gate measured giving a character up 60 s after activation.</summary>
    private const float CapSeconds = 45f;
    /// <summary>Slots the enrol sweep visits per frame under the hold -- the whole table in ONE frame, so
    /// "nothing left to enrol" is a one-frame silence rather than a ten-frame one. A slot that is enrolled or
    /// empty costs the sweep two array reads, so the empty majority is a linear scan and the few that do
    /// enrol do the same work they did spread over ten frames.</summary>
    private const int HoldSweepPerFrame = AlloyRenderer.MaxOwned;
    /// <summary>Frames of silence (no close, no enrol, no chart landing) before the queue counts as drained.</summary>
    private static int QuietFrames => AlloyRenderer.MaxOwned / HoldSweepPerFrame + HoldSettleFrames + 2;
    /// <summary>The frame rate <see cref="QuietFrames"/> is sized against. Below it the window is spent in
    /// seconds instead (<see cref="QuietSeconds"/>): the count means "the sweep visited the whole table",
    /// which is frames, but a frame stretched by texture builds or a slow disk must cost the hold that wall
    /// clock rather than multiply it.</summary>
    private const float QuietFps = 30f;
    /// <summary>The quiet window's wall-clock ceiling: <see cref="QuietFrames"/> at <see cref="QuietFps"/>.
    /// Reached first, the hold releases with the sweep part-way -- a slot it had not visited enrols after
    /// placement and draws unbaked for a few frames, which is the trade this ceiling makes.</summary>
    private static float QuietSeconds => QuietFrames / QuietFps;
    private const uint KeyEscape = 27;   // SDLK_ESCAPE

    private static IntPtr _heldMsg;
    private static long _holdT0;
    private static bool _skipOpen;
    /// <summary>The last frame something arrived that the bake still has to turn into a landed rect.</summary>
    private static int _quietFrom = int.MinValue;
    /// <summary>...and its timestamp, for the window's wall-clock arm (<see cref="QuietSeconds"/>).</summary>
    private static long _quietT;
    private static long _firstLandT;
    private static Window? _surface;
    private static int _captionN = -1, _captionM = -1, _captionPhase = -1, _chartsSeen = -1;
    /// <summary>The caption <see cref="LoadScreen"/> shows for us; rebuilt only when one of its numbers moves.</summary>
    private static string _caption = "";

    /// <summary>Can a bake hold follow the engine's load? Then the load screen's bar is shared.</summary>
    internal static bool HoldHooked => _loadedSlot != null && Enabled;

    /// <summary>The hold is up: the send is ours to make.</summary>
    internal static bool Held => _heldMsg != IntPtr.Zero;

    /// <summary>The frames the hold covers: the engine's send arrives here and stays until <see cref="Release"/>.
    /// No hold (master off, the pass stood down, <c>lightmap.hold</c> 0) = the send goes straight through.</summary>
    [UnmanagedCallersOnly]
    private static void OnSendAreaLoaded(IntPtr msg)
    {
        OnAreaLoaded();
        StampsReset();
        TakeAreaKey();
        if (!Enabled || Lightmap.HoldSeconds.V <= 0 || _surface == null)
        {
            _log?.Info(Enabled ? "LightmapBake: AreaLoaded send passed through -- hold disabled (lightmap.hold 0)" : "LightmapBake: AreaLoaded send passed through -- no bake");
            LoadScreen.End();   // CX-100: no bake phase follows, so the engine's load owns the rest of the shared bar
            Forward(msg); return;
        }
        bool silent = AreaHitReady(out int planned);
        if (Held) _log?.Info("LightmapBake: the AreaLoaded send fired under a hold -- restarting it");
        _heldMsg = msg; _holdT0 = Stopwatch.GetTimestamp(); _holdFrame = _frame; _holdPresents = 0; _skipOpen = false; _firstLandT = 0;
        _quietFrom = int.MaxValue; _quietT = _holdT0; _sharedEnrols = 0; _captionPhase = -1;
        Hold = true; _surface.SuppressChords = true; _silentHold = silent;
        // CX-56: a full hit draws no backdrop -- up for a fifth of a second it is a flicker
        _log?.Info(silent ? $"LightmapBake: holding the AreaLoaded send silently -- area bake cache hit ({planned} rects), released once the plan's rects are claimed"
                          : "LightmapBake: holding the AreaLoaded send for the baseline bake");
    }

    private static void Forward(IntPtr msg)
    {
        if (_loadedSlot != null) (*_loadedSlot)(msg);
    }

    /// <summary>The set closed, a rect was minted, a chart landed: the drain clock restarts, on both arms.
    /// <paramref name="why"/> is carried for the release line's quiet term only.</summary>
    private static void Arrived(byte why) { if (Held) { _quietFrom = _frame; _quietT = Stopwatch.GetTimestamp(); _stWhy = why; } }

    /// <summary>An enrolment's verdict for the hold. Only a rect the bake has yet to produce is work the hold
    /// waits on; a slot that joined a rect already on file is not -- and that is what a streaming texture's
    /// re-enrol always produces, the bake key carrying no texture term -- so counting one would restart the
    /// quiet clock on every texture that landed and the hold could never go quiet while textures streamed.</summary>
    private static void Enrolled(bool minted) { if (minted) { _stMints++; Arrived(WhyMint); } else _sharedEnrols++; }
    /// <summary>Enrolments that found their rect already on file, since the hold began.</summary>
    private static long _sharedEnrols;

    /// <summary>Nothing left to enrol, pack or bake, for a quiet window past the last arrival -- or a set that
    /// closed with no static light (SetHash 0): nothing to bake, nothing to wait for. A <c>LoadArea</c> the
    /// engine refused closes no set at all, and that is the whole of the wait too. A lit set with no rect
    /// minted is a world the sweep has not seen yet, not a drained one.</summary>
    private static bool BaselineIn
        => _loadRefused
        || (_closed || _areaAdopted) && !_edgePending && (SetHash == 0 || _rectN > 0 && AreaUploadsIn && MeshChart.InFlight == 0 && _toPack.Count == 0 && _pending == 0 && _bakePage < 0
           && ClaimsIn && Drained(_frame, _quietFrom, QuietFrames, QuietSince, QuietSeconds));

    /// <summary>Wall clock since the last arrival; 0 until one happens under this hold.</summary>
    private static float QuietSince => (Stopwatch.GetTimestamp() - _quietT) / (float)Stopwatch.Frequency;

    /// <summary>The quiet verdict, frames OR wall clock, whichever lands first. The frame count is what the
    /// window MEANS -- <see cref="HoldSweepPerFrame"/> makes "nothing left to enrol" a whole-table sweep, and
    /// a sweep is frames -- and the seconds are its ceiling. A clock never touched
    /// (<see cref="int.MaxValue"/>) is not drained on either arm: nothing has arrived yet.</summary>
    internal static bool Drained(int frame, int quietFrom, int windowFrames, float since, float windowSeconds)
        => quietFrom != int.MaxValue && (frame - quietFrom >= windowFrames || since >= windowSeconds);

    /// <summary>The idle exit's wall-clock arm, and now its ceiling: the login send fires before the world scene
    /// exists (nothing renders until the player is placed -- 2026-09-21's log held 23 s over an empty frame), and
    /// a bake that never ticks has nothing to wait for either. Kept for a client presenting too slowly for
    /// <see cref="IdlePresents"/> to mean anything.</summary>
    private const float IdleSeconds = 2f;
    /// <summary>The idle exit's frame arm -- presents under the hold with no Alloy pass behind them.
    /// <see cref="DrawSurface"/> runs at every present while <see cref="_frame"/> moves only inside the pass,
    /// so this counts what the clock could not tell apart: a real area reaches the frame anchor within the
    /// first present, a scene-less one never reaches it at all. Eight is several times the live latency.</summary>
    private const int IdlePresents = 8;
    /// <summary>Presents drawn under this hold, reset with it.</summary>
    private static int _holdPresents;
    /// <summary>The bake frame at the hold's start: unchanged = the pass never ran under it.</summary>
    private static int _holdFrame;
    private static bool IdleUnderHold => _frame == _holdFrame || _edgePending;
    /// <summary>The idle exit's verdict: presents with no pass behind them OR the clock, the two arms
    /// <see cref="Drained"/> carries and for the same reason -- the count is what the exit MEANS, and the
    /// seconds are its ceiling so a stretched frame costs the hold wall clock rather than multiplying it.</summary>
    private static bool IdleOut(int presents, float since) => presents >= IdlePresents || since >= IdleSeconds;

    /// <summary>The hold is waiting for rects that cannot arrive: a rect is minted from a CHARTED draw, and a
    /// draw that comes out of the mesh arena carries no chart -- so the lights closed a static set, the set
    /// gave the bake a subject, and no receiver ever appeared. This is the stand-down, not the fix: counted
    /// and said once rather than a quiet early release, which is indistinguishable from a bake that worked.</summary>
    private static bool NoRectSource
        => _closed && SetHash != 0 && _rectN == 0 && MeshChart.Offered == 0;

    /// <summary>Said once per session, loudly: an unlit world is a visible difference, not a detail.</summary>
    private static void NoRectSourceNotice()
    {
        if (_saidNoRects) return;
        _saidNoRects = true;
        Notice.Show(_log, "CX: the lightmap bake has no receivers in scene-database mode -- the draw comes out "
                        + "of the mesh arena, which carries no lightmap chart yet, so static lighting is not "
                        + "baked here. The load is released rather than held; tiles are lit dynamically.");
    }
    private static bool _saidNoRects;

    /// <summary>Send the held message and log the hold: its length, the exit, the walk the set closed on and
    /// the census after settle.</summary>
    private static void Release(string why)
    {
        IntPtr msg = _heldMsg;
        float s = HeldSeconds;
        string stages = StampsClause();   // CX-56 step 0
        bool complete = BaselineIn;   // CX-56: the release's own verdict, and what decides whether the area plan is written
        End();
        float first = _firstLandT == 0 ? -1f : (_firstLandT - _holdT0) / (float)Stopwatch.Frequency;
        _log?.Info($"LightmapBake: hold released after {s:F1} s ({why}) -- first bake at {(first < 0 ? "never" : $"{first:F1} s")}, "
                 + $"{_landed} rects landed ({_landedHits} cached) / {_pending} pending, pages {Layers}, set {(_closed ? $"closed at walk {_walks}" : "OPEN")} "
                 + $"SetHash {SetHash:x16}, "
                 + $"charts {MeshChart.Done}/{MeshChart.Offered}, {_sharedEnrols} enrols shared a standing rect; census at the close static={_closeStatic} dynamic={_closeDynamic}, now static {CensusStatic} dynamic {CensusDynamic}"
                 + stages + AreaStats());
        // CX-54 step 0: the key sample is chosen over the whole area, so it is emitted here, not on the first rect.
        FlushKeySample();
        // the world the send places the player into is not the one any capture under the hold saw
        EnvProbe.Revoke();
        Forward(msg);
        AreaWrite(complete);
    }

    /// <summary>The world edge under a hold (a part-table wipe is one, and an area transition's debris can
    /// trip it): the send still goes out -- a swallowed send is a player the server never places. The one
    /// exception is the area edge, which IS the edge this hold was raised for.</summary>
    private static void DropHold() { if (Held && !_areaEdge) Release("world edge"); }

    /// <summary><c>AlloyRenderer.AreaEdge</c> runs <see cref="Revoke"/> as part of the one area-edge
    /// revoke list. Bracketed by these two, that revoke neither forwards the held send nor loses the edge
    /// marks the static set closes against -- <see cref="EndAreaEdge"/> re-arms them, so the bake restarts on
    /// the new area's inputs rather than falling back to the long walk-counted close.</summary>
    private static bool _areaEdge;
    internal static void BeginAreaEdge() => _areaEdge = true;
    internal static void EndAreaEdge()
    {
        _areaEdge = false; _edgePresent = GlContextService.Presents; OnAreaLoaded(); StampEdge();
        // the world scene dies BEFORE the next LoadArea: outside a hold the pending key is the area being left
        if (Held) TakeAreaKey();
    }

    /// <summary>The window is the input side only (Escape, the clicks it eats); the draw is <see cref="LoadScreen"/>'s,
    /// at the present, after the GL bracket hands the engine's context back -- the window roll-call sits under the
    /// engine's loadscreen (the NUI chain), and the first live run showed the stock screen, not this one.</summary>
    private static void End()
    {
        _silentHold = false; _heldMsg = IntPtr.Zero; Hold = false; _skipOpen = false; _surface!.SuppressChords = false;
        LoadScreen.End();   // CX-100: the hold was the last phase of the one screen, so the screen is over too
    }

    private static void InstallSurface(ExtenderLog log)
    {
        _surface = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            OnKey = (key, _) => { if (!Held) return false; if (key == KeyEscape && _skipOpen) Release("skipped"); return true; },
            OnMouseButton = (down, button, x, y) =>
            {
                if (!Held) return false;
                if (down && button == 1 && _skipOpen && LoadScreen.HitSkip(x, y)) Release("skipped");
                return true;
            },
        };
        FrameRegistry.Global.Register(FramePhase.PrePresent, 20, DrawSurface, "LightmapBake.hold", log);
    }

    private static float HeldSeconds => (Stopwatch.GetTimestamp() - _holdT0) / (float)Stopwatch.Frequency;

    /// <summary>Per presented frame. Ticks the exits, then feeds <see cref="LoadScreen"/> -- the ONE loading
    /// screen the engine's own load phase already drew -- and draws it over everything the engine drew.
    /// ponytail: the caption is a string built whenever its number moves; under an opaque backdrop a gen0
    /// pause shows nowhere, and a char buffer is the fix if it ever does.</summary>
    private static void DrawSurface()
    {
        if (!Held) return;
        _holdPresents++;
        if (MeshChart.Done != _chartsSeen) { _chartsSeen = MeshChart.Done; Arrived(WhyChart); }
        StampTick();
        float s = HeldSeconds;
        if (BaselineIn) { Release(_loadRefused ? "the engine refused the area load" : SetHash == 0 ? "no static light -- nothing to bake" : _silentHold ? $"baseline in, silent -- {_areaClaimed} of {_areaBase} baseline rects claimed" : "baseline in"); return; }
        if (IdleUnderHold && IdleOut(_holdPresents, s)) { Release($"Alloy idle under the hold -- no scene yet after {_holdPresents} presents"); return; }
        if (NoRectSource) { NoRectSourceNotice(); Release("no receiver geometry -- the bake has nothing to bake ONTO"); return; }
        if (s >= CapSeconds) { Release("cap"); return; }
        if (!Enabled) { Release("bake stood down"); return; }
        if (_silentHold) { if (s >= SilentCapSeconds) Release($"silent hold ceiling -- {_areaClaimed} of {_areaBase} baseline rects claimed"); return; }
        _skipOpen = s >= Lightmap.HoldSeconds.V;
        int phase = _closed && MeshChart.InFlight == 0 ? 1 : 0;
        int n = phase == 0 ? MeshChart.Done : PagesDone(), m = phase == 0 ? MeshChart.Offered : PagesUsed;
        if (n != _captionN || m != _captionM || phase != _captionPhase)
        {
            _captionN = n; _captionM = m; _captionPhase = phase;
            _caption = phase == 0 ? $"Building light charts {n}/{m}" : $"Baking the shadows... {n}/{m} pages";
        }
        LoadScreen.BakeStep(BakeFraction(phase, n, m), _caption, _skipOpen);
        LoadScreen.Draw();
    }

    /// <summary>The hold's two sub-phases as one 0..1 term: charts fill the first half, pages the second. Without
    /// the split the bar would stall at the charts' full mark while the pages counted up from zero.</summary>
    internal static float BakeFraction(int phase, int n, int m)
        => (Math.Clamp(phase, 0, 1) + (m > 0 ? Math.Clamp(n / (float)m, 0f, 1f) : 0f)) * 0.5f;

    /// <summary>The present "Compiling shaders" last went up in: once per present, the screen keeps it until the next.</summary>
    private static int _noticeAt = -1;
    /// <summary>The present the last area edge ran in: nothing of that area is on screen until it ends. A persistent world's login
    /// runs its edge ~125 ms after the AreaLoaded send, past the hold's idle exit (2026-09-29: the module's whole
    /// program set compiled on that frame, 9,6 s, with no hold up).</summary>
    private static int _edgePresent = -1;

    /// <summary>A shader compile is about to block the render thread on a loading screen -- under the hold, or
    /// on the frame an area edge ran in: put the backdrop up with "Compiling shaders…" NOW, as its own
    /// present, since every other draw comes after the compile. Mid-play first-use compiles get no notice: a
    /// backdrop flashing over the world is worse than the hitch.</summary>
    internal static void CompileNotice()
    {
        int now = GlContextService.Presents;
        CompileNoticeVk(now);
    }

    /// <summary><see cref="CompileNotice"/> on Vulkan: the backdrop recorded over the composite and presented by Vulkan at
    /// once, ahead of the compile. No GL bracket to step out of; the frame's own present follows as usual.</summary>
    private static void CompileNoticeVk(int now)
    {
        if (!Held && _edgePresent != now || _noticeAt == now || TextRig.Atlas == null || LoadScreen.PanelUp) return;
        if (TextRig.ViewportW <= 0 || TextRig.ViewportH <= 0) return;
        _noticeAt = now; _captionPhase = -1;
        LoadScreen.Notice("Compiling shaders…", "Only once: the result is saved for next time");
        if (ScreenOwner.PresentVulkanNow())
            _log?.Info($"LightmapBake: a shader compile {(Held ? "under the hold" : "on an area's first frame")} -- \"Compiling shaders\" is on screen (Vulkan) until it finishes");
    }

    /// <summary>Pages with no rect still queued, probing or packing.</summary>
    private static int PagesDone()
    {
        Span<bool> busy = stackalloc bool[MaxPages]; busy.Clear();
        for (int ri = 0; ri < _rectN; ri++)
        {
            RState st = _rects[ri].State;
            if ((st == RState.Queued || st == RState.Probing) && (uint)_rects[ri].Page < MaxPages) busy[_rects[ri].Page] = true;
        }
        int done = 0;
        for (int p = 0, used = PagesUsed; p < used; p++) if (!busy[p]) done++;
        return done;
    }

    /// <summary>Headless: the drain clock and the exits, no GL, no window.</summary>
    internal static bool HoldSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LightmapBake.HoldSelfCheck");
        check.Expect(CapSeconds > Lightmap.HoldSeconds.Max, "the cap must sit past the knob's range or the skip line never shows");
        check.Expect(QuietFrames < 60, "the drain clock is a second or more: a second visit would not hold ~0 s");
        check.Expect(AlloyRenderer.MaxOwned / HoldSweepPerFrame == 1, "the hold's sweep no longer covers the whole table in one frame, so QuietFrames' sweep term is wrong");
        check.Expect(BakeFraction(0, 0, 0) == 0f, "an unopened chart phase starts the bake term at zero");
        check.Expect(BakeFraction(0, 4, 4) == 0.5f, "a finished chart phase hands over at the halfway mark");
        check.Expect(BakeFraction(1, 0, 4) == 0.5f, "...and the page phase picks it up there, so the bar never stalls");
        check.Expect(BakeFraction(1, 4, 4) == 1f, "a finished bake fills the bake term");
        check.Expect(BakeFraction(0, 9, 0) == 0f, "a zero denominator is a zero term, not a NaN");
        check.Expect(IdleSeconds < CapSeconds && IdleSeconds <= Lightmap.HoldSeconds.Max, "the idle exit must come before the cap and the skip");
        // CX-56: the idle exit counts presents -- the draw slot runs at every present while the bake's frame
        // moves only inside the Alloy pass -- with the seconds kept as the ceiling, Drained's two-arm shape.
        check.Expect(!IdleOut(IdlePresents - 1, IdleSeconds - 0.01f), "the idle exit fires before either arm is reached");
        check.Expect(IdleOut(IdlePresents, 0f), "the present arm does not release a hold no Alloy pass ran under");
        check.Expect(IdleOut(0, IdleSeconds), "the clock ceiling no longer releases a hold that barely presented");
        check.Expect(IdlePresents / QuietFps < IdleSeconds, "the present arm cannot fire before the ceiling: the idle exit is still a two-second wait");
        // CX-54: the quiet verdict, both arms and the ceiling that stops a stretched frame multiplying
        const int w = 16; const float ws = 0.5f;
        check.Expect(!Drained(100, int.MaxValue, w, 99f, ws), "an untouched drain clock releases on the clock arm");
        check.Expect(!Drained(100, 90, w, 0.1f, ws), "a window neither arm has reached counts as drained");
        check.Expect(Drained(106, 90, w, 0.1f, ws), "the frame arm does not release after QuietFrames of silence");
        check.Expect(Drained(95, 90, w, 0.6f, ws), "the clock arm does not release when the frames stretched past the window's seconds");
        check.Expect(Drained(106, 90, w, 0.6f, ws) && !Drained(99, 90, w, 0.0f, ws), "the verdict is monotone in both frames and seconds");
        check.Expect(QuietSeconds > 0f && QuietSeconds < CapSeconds, "the quiet ceiling must sit inside the hold's own cap");
        // CX-54: only a MINTED rect is an arrival. A shared one is what a landing texture's re-enrol always
        // produces, and counting it is what kept the hold running while textures streamed.
        IntPtr msg = _heldMsg; int qf = _quietFrom; long qt = _quietT;
        _heldMsg = (IntPtr)1; _frame = 100; _quietFrom = 50; _quietT = 0;
        Enrolled(false);
        check.Expect(_quietFrom == 50, "a shared enrol restarted the quiet clock");
        Enrolled(true);
        check.Expect(_quietFrom == 100, "a minted rect did not restart the quiet clock");
        _heldMsg = msg; _quietFrom = qf; _quietT = qt; _sharedEnrols = 0;
        _quietFrom = int.MaxValue; _frame = 100;
        check.Expect(!BaselineIn, "an untouched drain clock counts as drained");
        // CX-56: the close is LoadArea's return and the blends, not a walk count. A lightless area sees no
        // light at all -- the set still closes on the first armed walk, the bake stays un-armed (SetHash 0)
        // and the hold has nothing to wait for.
        // CX-56: a silent hold waits for half the plan's baseline rects to be claimed; a loud one never does
        _silentHold = true; _areaBase = 10; _areaClaimed = 4;
        check.Expect(!ClaimsIn, "a silent hold released with fewer than half of the plan's rects claimed");
        _areaClaimed = 5;
        check.Expect(ClaimsIn, "a silent hold with half the plan claimed still waits");
        _silentHold = false; _areaClaimed = 0;
        check.Expect(ClaimsIn, "a loud hold waits on claims");
        _areaBase = 0;
        ExtenderLog? live = _log; _log = null;   // the close logs as evidence: a headless one must not open every cx.log
        _clsArea = 1; Reopen(); _edgePending = false; _stN = 0; CensusDynamic = CensusUnsettled = 0; SetHash = 0xBEEF;
        _loadArmed = false; _walks = 1; EndWalk();
        check.Expect(!_closed, "the set closed with LoadArea still unarmed -- a half-spawned area can be baked");
        _loadArmed = true; EndWalk();
        check.Expect(_closed, "a lightless area never closes its static set on the first walk past LoadArea");
        check.Expect(SetHash == 0 && _setN == 0, "a lightless close armed the bake");
        check.Expect(BaselineIn, "a closed lightless set does not release the hold");
        // ...and a light still blending its animation in holds the close open, up to the ceiling
        Reopen(); _stN = 0; CensusUnsettled = 1; _walks = 1; EndWalk();
        check.Expect(!_closed, "a static light still blending did not hold the close open");
        _walks = BlendCeiling; EndWalk();
        check.Expect(_closed, "the blend wait has no ceiling: one stuck blend would hold the loading screen to its cap");
        _log = live;
        _clsArea = IntPtr.Zero; Reopen(); _loadArmed = false; CensusUnsettled = 0; SetHash = 0; _quietFrom = int.MinValue;
        if (check.Ok) log.Info($"LightmapBake.HoldSelfCheck: passed (cap {CapSeconds:F0} s, drain {QuietFrames} frames or {QuietSeconds:F2} s, idle {IdlePresents} presents or {IdleSeconds:F0} s)");
        return check.Ok;
    }
}
