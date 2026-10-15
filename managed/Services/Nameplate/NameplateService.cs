using System.Numerics;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Services.Chat;
using CX.Services.Chat.Core;

using CX.Ui;

namespace CX.Services.Nameplate;

/// <summary>
/// The overhead nameplate and chat bubble: one <see cref="Window"/> that draws every world-anchored plate and
/// bubble, four cold hooks that feed it, and the <c>nameplate</c> group read on the frame. Vanilla is suppressed
/// by putting the engine in text-bubble mode 0 (its own supported "Off" setting), which stops it drawing either
/// element while still refreshing every bubble-owning object's projected anchor each tick. Live both ways.
/// </summary>
public sealed unsafe class NameplateService : IExtenderService
{
    private static ExtenderLog? _log;
    private static bool _armed, _wasInModule;
    /// <summary>The engine is at text-bubble mode 0 by our hand. Written on the frame at the master's edge, read by the cold
    /// hooks (forward 0 or the real mode; bank or not; clear the latch or not) and by <see cref="Suppressing"/>.</summary>
    private static volatile bool _suppressed;
    /// <summary>False when the chat handler did not hook: per-channel styling then degrades to one style
    /// whatever <c>nameplate.channelstyle</c> says.</summary>
    private static bool _chatHooked;

    /// <summary>Whether the vanilla text-bubble mode-0 suppression is in force. While it is -- and only while
    /// it is -- the engine refreshes every bubble-owning object's projected screen anchor every tick, which is the
    /// anchor <c>CombatTextService</c> reads. It checks this before swallowing anything, so a stood-down OR
    /// switched-off nameplate feature stands the combat text down with it.</summary>
    public static bool Suppressing => _suppressed;

    private static readonly PlateTable _table = new();
    private static Window? _plates;

    // ---- trampolines -------------------------------------------------------------------------
    private static SetChatBubbleModeFn _origSetMode;
    private static CreatureShowFeedbackBubbleTextFn _origCreaturePlate;
    private static PlaceableShowFeedbackBubbleTextFn _origPlaceablePlate;
    private static ItemShowFeedbackBubbleTextFn _origItemPlate;
    private static SetPopUpBubbleTextFn _origBubbleText;
    private static HandleServerToPlayerChatFn _origChat;

    /// <summary>Nesting depth of a <c>ShowFeedbackBubbleText</c> in flight on this thread. The bubble-text
    /// sink is shared: non-zero means the string arriving there is the plate's own, not chat.</summary>
    private static int _inPlate;

    /// <summary>Channel byte of the chat line currently being handled, or -1. A sentinel rather than 0
    /// because <c>HandleServerToPlayerQuickChat</c> reaches the same sink without passing through the chat
    /// handler, and a stale byte would mis-style it.</summary>
    private static int _channel = -1;

    /// <summary>The nameplate unit: every symbol the plates cannot run without. Optional and NOT listed:
    /// HandleServerToPlayerChat (per-channel styling degrades to one style), GetViewPort (falls back to the
    /// camera rect), GetObjectName (names fall back to the creature stats read).</summary>
    private static readonly string[] RequiredSymbols =
    {
        SymbolMap.SetChatBubbleMode, SymbolMap.SetPopUpBubbleText, SymbolMap.CreatureShowFeedbackBubbleText,
        SymbolMap.PlaceableShowFeedbackBubbleText, SymbolMap.ItemShowFeedbackBubbleText,
        SymbolMap.AppManager, SymbolMap.GetGameObject, SymbolMap.GetModule, SymbolMap.ExoStringDtor,
    };

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        NameplateNative.UseLog(log);
        // Every pure piece self-checks BEFORE any stand-down return -- run-demo's only gate.
        PlateLayout.SelfCheck(log);
        NameplateNative.SelfCheck(log);
        PlateTable.SelfCheck(log);
        UiCategory.Declare();   // the UI groups declare in panel order whether or not the hooks arm

        if (!SymbolMap.Require(log, "NameplateService", RequiredSymbols)) return;   // whole or nothing
        if (!EngineLayout.Require(log, "NameplateService", "CNWCObject", "CNWCCreature", "CNWCPlaceable",   // same, for offsets
                                  "CNWCCreatureStats", "CGuiInGamePopUpBubble", "CNWCModule", "CNWCArea",
                                  "CClientExoApp", "CClientExoAppInternal", "Camera")) return;

        // ORDER: the two critical symbols first, so nothing else installs if either refuses.
        _origSetMode = hooks.InstallSetChatBubbleMode(&OnSetChatBubbleMode);
        _origBubbleText = hooks.InstallSetPopUpBubbleText(&OnSetPopUpBubbleText);
        if (_origBubbleText == null || _origSetMode == null)
        {
            log.Info("NameplateService: standing down (mode="
                   + (_origSetMode != null ? "hooked" : "unresolved") + " text="
                   + (_origBubbleText != null ? "hooked" : "unresolved")
                   + ") -- suppression never arms, vanilla plates and bubbles draw as they always did");
            return;
        }

        _origCreaturePlate = hooks.InstallCreatureShowFeedbackBubbleText(&OnCreaturePlate);
        _origPlaceablePlate = hooks.InstallPlaceableShowFeedbackBubbleText(&OnPlaceablePlate);
        _origItemPlate = hooks.InstallItemShowFeedbackBubbleText(&OnItemPlate);
        _origChat = hooks.InstallHandleServerToPlayerChat(&OnServerChat);

        _chatHooked = _origChat != null;
        if (!_chatHooked) log.Info("NameplateService: channel styling disabled (chat handler unhooked)");

        // No HitTestOverride: a plate must never answer UiHost.HitTest -- see OnPlateClick.
        _plates = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            DrawContent = static () => { long t = FrameZones.Now; Draw(); FrameZones.Add(FrameZones.Zone.Plates, t); },
            OnMouseButton = OnPlateClick,
        };
        UiHost.SendToBack(_plates);   // plates paint UNDER the chat box and the dialog panel

        NameplateKnobs.G.Status = Status;
        _armed = true;
        log.Info("NameplateService: armed (vanilla bubbles suppressed via text-bubble mode 0 while nameplate.enabled)");
    }

    // ---- hooks -------------------------------------------------------------------------------

    /// <summary>Hook 1. <c>CGuiInGamePopUpBubble::SetChatBubbleMode(int)</c> is static and is the suppression:
    /// whatever the options screen or the ini bind asks for, the engine is left at 0 while
    /// <see cref="_suppressed"/>, and the requested value is kept in <see cref="EngineMode"/> for the off edge.
    /// Unarmed or off it passes through untouched; this handler is live from <c>Commit</c> either way. Cold.</summary>
    [UnmanagedCallersOnly]
    private static void OnSetChatBubbleMode(int mode)
    {
        if (!_armed) { if (_origSetMode != null) _origSetMode(mode); return; }
        _log?.Info($"[nameplate] text-bubble-mode {mode} requested");
        EngineMode = mode;
        if (_origSetMode != null) _origSetMode(_suppressed ? 0 : mode);
    }

    /// <summary>The engine's own text-bubble mode as last requested (the ini bind, then the options screen),
    /// never applied while suppressed.</summary>
    internal static volatile int EngineMode = -1;

    /// <summary>The user's mode when none was ever requested through the hook: 2 (Full), the config bind's
    /// default. Not expected on the LD_PRELOAD / proxy-DLL load path, where the hook is live before the ini
    /// bind runs.</summary>
    private const int DefaultEngineMode = 2;

    /// <summary>Hook 2 (x3). Flag the plate's own text so the shared sink below can tell it from chat, then
    /// let the original run -- it sets the per-class "plate is up" flag we poll every frame and drives the
    /// vanilla state machine. Nothing is read off <c>this</c> here on purpose: <c>CNWCItem</c> is multiply
    /// inherited, so only the sink's <c>CNWCObject*</c> is layout-safe for every class.</summary>
    [UnmanagedCallersOnly]
    private static void OnCreaturePlate(IntPtr self)
    { _inPlate++; if (_origCreaturePlate != null) _origCreaturePlate(self); _inPlate--; }

    [UnmanagedCallersOnly]
    private static void OnPlaceablePlate(IntPtr self)
    { _inPlate++; if (_origPlaceablePlate != null) _origPlaceablePlate(self); _inPlate--; }

    [UnmanagedCallersOnly]
    private static void OnItemPlate(IntPtr self)
    { _inPlate++; if (_origItemPlate != null) _origItemPlate(self); _inPlate--; }

    /// <summary>Hook 3. The one sink both elements pass through, and the only place the object is a clean
    /// <c>CNWCObject*</c>. After the original runs, a plate's own text is recorded as a plate candidate and
    /// anything else is queued as a chat line with the engine's decay. The <c>+0x14c</c> reset at the tail is
    /// load-bearing: under text-bubble mode 0 nothing else clears it and <c>MouseOver</c> then never builds a plate.</summary>
    [UnmanagedCallersOnly]
    private static void OnSetPopUpBubbleText(IntPtr self, IntPtr exo, int forceShow, float perChar, float fixedSec)
    {
        bool live = _suppressed && self != IntPtr.Zero;
        bool bank = live && NameplateKnobs.Enabled.V;
        // BEFORE the forward: the Win64 callee destroys its by-value CExoString and the caller sees it zeroed
        // on return (docs/windows-symbols.md), so a read afterwards yields "" and no line is ever banked --
        // which is bubbles working on Linux, where the CALLER owns the copy, and silently dead on Windows.
        string text = bank && _inPlate == 0 ? Native.ReadExo(exo) : "";

        if (_origBubbleText != null) _origBubbleText(self, exo, forceShow, perChar, fixedSec);
        if (!live) return;
        NameplateNative.UnfreezeAnchor(self);
        if (!bank) { NameplateNative.ClearLiveTextLatch(self); return; }

        byte type = NameplateNative.TypeOf(self);
        if (_inPlate > 0)
            _table.Note(self, type);
        else if (text.Length != 0)
            _table.NoteBubble(self, type, text, NameplateNative.Decay(text.Length, perChar, fixedSec),
                              _channel, Environment.TickCount64);
        // One line, first chat bubble only: the decision tree for "no bubbles" is exactly len and decay, and
        // its ABSENCE says the hook never ran for chat at all. Cold (one call per spoken line) and silent
        // without logs=1.
        if (_inPlate == 0 && !_bubbleSeen)
        {
            _bubbleSeen = true;
            _log?.Info($"[nameplate] first bubble: type={type} len={text.Length} " +
                       $"decay={NameplateNative.Decay(text.Length, perChar, fixedSec)} ch={_channel} " +
                       $"perChar={perChar} fixed={fixedSec}");
        }
        NameplateNative.ClearLiveTextLatch(self);
    }

    private static bool _bubbleSeen;

    /// <summary>Hook 4 (optional). Latch the channel byte for the <c>SetPopUpBubbleText</c> that runs on the
    /// same stack, and drop it again on return so a quick-chat line cannot inherit it.</summary>
    [UnmanagedCallersOnly]
    private static void OnServerChat(IntPtr self, byte channel)
    {
        int prev = _channel;
        _channel = channel;
        if (_origChat != null) _origChat(self, channel);
        _channel = prev;
    }

    // ---- the per-frame draw ------------------------------------------------------------------

    /// <summary>Plate/bubble geometry, reused. Doubling is a guard, not a path.</summary>
    private static float[] _verts = new float[8192];

    private static readonly float[] _boxX = new float[PlateTable.MaxEntries];
    private static readonly float[] _boxY = new float[PlateTable.MaxEntries];
    private static readonly float[] _boxW = new float[PlateTable.MaxEntries];
    private static readonly float[] _boxH = new float[PlateTable.MaxEntries];
    private static readonly float[] _boxA = new float[PlateTable.MaxEntries];
    /// <summary>Where <c>Place</c> put each box, before separation and before the slide residual. Kept so the
    /// push can be measured on its own and absorbed into the residual rather than teleporting the box.</summary>
    private static readonly float[] _boxBaseY = new float[PlateTable.MaxEntries];
    private static readonly long[] _boxKey = new long[PlateTable.MaxEntries];
    private static readonly uint[] _boxId = new uint[PlateTable.MaxEntries];
    private static readonly bool[] _boxIn = new bool[PlateTable.MaxEntries];
    /// <summary>Height and width of the plate stacked under a bubble (<c>nameplate.whiletalking</c>); 0 = none. The
    /// bubble leaves to that plate what it already shows (<see cref="UseCard"/>).</summary>
    private static readonly float[] _boxStackH = new float[PlateTable.MaxEntries];
    private static readonly float[] _boxPlateW = new float[PlateTable.MaxEntries];
    private static readonly int[] _boxOrder = new int[PlateTable.MaxEntries];

    /// <summary>One clickable plate, published for the input thread.</summary>
    private struct Hit { public float X, Y, W, H; public string Name; }

    /// <summary>Double-buffered so the render thread can publish without allocating and the input thread
    /// never reads a half-written array. A torn read across the flip costs one stale rect, nothing worse.</summary>
    private static readonly Hit[][] _hits = { new Hit[PlateTable.MaxEntries], new Hit[PlateTable.MaxEntries] };
    private static readonly int[] _hitCount = new int[2];
    private static volatile int _hitCur;

    private const int MaxPortraitsPerFrame = 32;
    private static readonly string[] _portraitRef = new string[MaxPortraitsPerFrame];
    private static readonly float[] _portraitRect = new float[MaxPortraitsPerFrame * 5];   // x,y,w,h,alpha

    /// <summary>The entry being measured or emitted: its style's halo offsets (<see cref="TextStyle.HaloOffsets"/>) and
    /// the key its wraps are cached under (<see cref="TextStyle.Stamp"/>), set by <see cref="Use"/>. Render thread only.</summary>
    private static readonly float[] _haloOff = new float[TextStyle.MaxHaloFloats];
    private static int _haloN, _wrapKey;

    /// <summary>Point the measure / emit helpers at a plate's or a bubble's text style.</summary>
    private static void Use(TextStyle s) { _haloN = TextStyle.HaloOffsets(s.HaloPx, _haloOff).Length; _wrapKey = s.Stamp; }

    /// <summary>The emitted entry's glyph bodies, held back while its halos go straight into <see cref="_verts"/> and
    /// appended by <see cref="FlushBodies"/> once it is done: every halo of a plate or bubble lies under every glyph of it.</summary>
    private static float[] _bodies = new float[4096];
    private static int _bn;

    private static readonly (float r, float g, float b, float a) PlateBg = (0.03f, 0.04f, 0.06f, 0.78f);
    private static readonly (float r, float g, float b, float a) BubbleBg = (0.04f, 0.05f, 0.08f, 0.80f);
    private static readonly (float r, float g, float b, float a) BarTrack = (0.10f, 0.11f, 0.14f, 0.9f);

    /// <summary>Chrome padding inside a bubble box, design px.</summary>
    private const float BubblePadPx = 5f;
    /// <summary>Vertical gap between a bubble box and the one queued above it, design px.</summary>
    private const float BubbleGapPx = 2f;
    /// <summary>Gap between two queued lines inside one speech card, design px -- wider than
    /// <see cref="BubbleGapPx"/>, since no box edge separates them there.</summary>
    private const float CardParaGapPx = 4f;
    /// <summary>Opacity of a queued line older than the newest, so the eye lands on what was said last.</summary>
    private const float OlderLineAlpha = 0.65f;
    /// <summary>Vertical clearance the separation pass leaves between two overlapping boxes, design px.</summary>
    private const float SeparationGapPx = 2f;
    /// <summary>Speech tail under a bubble box, design px. Sized off the design grid, not the atlas, so it
    /// scales with <c>Metrics.Scale</c> exactly as the padding does.</summary>
    private const float TailHeightPx = 9f, TailWidthPx = 12f;
    /// <summary>How far below its resting place a new plate/bubble starts, design px. It eases up from there
    /// while the entry fades in; the same residual absorbs a box that grows under a newer line.</summary>
    private const float SlideUpPx = 14f;

    /// <summary>Monotonic clock the animation deltas come off -- <c>Environment.TickCount64</c>, the only
    /// dt source the framework has. Primed on the first drawn frame so a long load is not a 30s step.</summary>
    private static long _animClockMs;
    private static bool _animPrimed;

    /// <summary>Set by the <c>nameplate.probe</c> button; the next drawn frame dumps one line per tracked entry
    /// and clears it. Cold by construction -- one press, one dump.</summary>
    private static volatile bool _probe;

    /// <summary>The <c>probe</c> button's press.</summary>
    internal static void ArmProbe() => _probe = true;

    private static void Draw()
    {
        bool inModule = GameGate.InModule;
        if (_wasInModule && !inModule) _table.Clear();
        _wasInModule = inModule;

        if (!_armed) return;
        bool want = NameplateKnobs.Enabled.V;
        if (want != _suppressed) ApplyMaster(want);

        int queue = NameplateKnobs.Queue.V; float animMs = NameplateKnobs.Anim.V;
        _table.Drain(queue, animMs);   // BEFORE every early return below: the hook ring is fixed-size

        _hitCount[_hitCur ^ 1] = 0;
        if (!GameGate.InGame) { Publish(0); return; }             // acceptance §11: GUI hidden -> nothing
        GlyphAtlas? atlas = TextRig.Atlas;
        if (atlas == null || !want) { Publish(0); return; }

        float vpW = TextRig.ViewportW, vpH = TextRig.ViewportH;
        long now = Environment.TickCount64;
        long dt = _animPrimed ? now - _animClockMs : 0L;
        _animClockMs = now; _animPrimed = true;
        _table.Refresh(now, dt, animMs, NameplateKnobs.MaxDist.V, NameplateKnobs.FadeDist.V, queue, vpW, vpH);
        if (_probe) { _probe = false; Probe(vpW, vpH); }

        int count = _table.VisibleCount;
        if (count == 0) { Publish(0); return; }

        float scale = Metrics.Scale;
        GlyphAtlas plate = NameplateKnobs.Plate.Face(atlas)!, bubble = NameplateKnobs.Bubble.Face(atlas)!;
        bool portraits = NameplateKnobs.Portraits.V;
        bool background = NameplateKnobs.Background.V;
        bool stackPlate = NameplateKnobs.WhileTalking.V;

        // Pass 1: measure and place every box at its anchor. The slide residual is NOT folded in yet -- it is
        // applied after separation, so a box easing into place cannot perturb the overlap tests.
        for (int i = 0; i < count; i++)
        {
            PlateTable.Entry e = _table.Visible[i];
            float w, h, stackH = 0f;
            if (e.ShowBubble)
            {
                bool stacked = stackPlate && e.Plated && PlateHasContent(e);
                Use(NameplateKnobs.Bubble);
                MeasureBubble(e, bubble, scale, bubble.LineHeight, stacked, out w, out h);
                if (stacked)
                {
                    Use(NameplateKnobs.Plate);
                    MeasurePlate(e, plate, scale, plate.LineHeight, portraits, out float pw, out stackH);
                    _boxPlateW[i] = pw;
                    if (pw > w) w = pw;
                    h += stackH;
                }
            }
            else { Use(NameplateKnobs.Plate); MeasurePlate(e, plate, scale, plate.LineHeight, portraits, out w, out h); }
            _boxStackH[i] = stackH;
            PlateLayout.Place(e.AnchorX, e.AnchorY, w, h, vpW, vpH, out _boxX[i], out _boxBaseY[i]);
            // A newcomer starts a slide below its place; afterwards, GROWTH of the box (a newer line queued
            // beneath the older ones) pushes the top edge up, and feeding that delta back into the residual
            // makes the older lines EASE to the new offset instead of jumping to it. Shrink is deliberately
            // NOT absorbed: the box's bottom is pinned to the anchor, so when the oldest line expires every
            // surviving line stays exactly where it was and there is no displacement to animate.
            bool landed = stackH > 0f && !e.LastBubble;
            if (e.AnimNew) { e.AnimNew = false; e.YOff = SlideUpPx * scale; }
            else if (!landed && e.LastH > 0f && h > e.LastH) e.YOff += h - e.LastH;
            else if (e.ShowBubble && stackH < e.LastStackH) e.YOff -= e.LastStackH - stackH;
            e.LastH = h; e.LastStackH = stackH; e.LastBubble = e.ShowBubble;
            _boxY[i] = _boxBaseY[i];
            _boxW[i] = w; _boxH[i] = h;
            _boxKey[i] = e.SortKey;
            _boxId[i] = e.Id;
            _boxIn[i] = !e.FadingOut;
            // The bubble's own fade is now PER LINE (PlateTable.ExpireLines), applied inside EmitBubble; all the
            // box alpha still owes it is the "no line left to draw" cut.
            _boxA[i] = e.DistanceFade * e.Alpha * (e.ShowBubble && e.TopLine < 0 ? 0f : 1f);
        }

        int sepCount = PlateLayout.OrderByRecency(_boxOrder, _boxKey, _boxId, _boxIn, count);
        PlateLayout.Separate(_boxX, _boxY, _boxW, _boxH, _boxOrder, sepCount, SeparationGapPx * scale, vpH);

        // Absorb whatever the push changed by into the slide residual, then draw at placed + push + residual.
        // A box that took no part keeps the push it last had, so it fades where it stands rather than sliding
        // back down to its bare anchor as the stack it belonged to dissolves around it.
        for (int i = 0; i < count; i++)
        {
            PlateTable.Entry e = _table.Visible[i];
            if (_boxIn[i]) PlateLayout.AbsorbPush(ref e.YOff, ref e.LastPush, _boxY[i] - _boxBaseY[i]);
            _boxY[i] = _boxBaseY[i] + e.LastPush + e.YOff;
            if (vpH <= 0f) continue;
            float maxY = vpH - _boxH[i];
            if (_boxY[i] > maxY) _boxY[i] = maxY;
            if (_boxY[i] < 0f) _boxY[i] = 0f;
        }

        // Pass 2: emit. Order of emission IS draw order (no depth), so chrome before text.
        int n = 0, pn = 0;
        float su = atlas.SolidU, sv = atlas.SolidV;
        int hitN = 0;
        Hit[] hits = _hits[_hitCur ^ 1];
        for (int i = 0; i < count; i++)
        {
            PlateTable.Entry e = _table.Visible[i];
            float a = _boxA[i];
            if (a <= 0f) continue;
            if (!e.ShowBubble && !PlateHasContent(e)) continue;
            float stackH = _boxStackH[i];
            if (e.ShowBubble)
            {
                Use(NameplateKnobs.Bubble);
                EmitBubble(ref n, ref pn, su, sv, bubble, e, _boxX[i], _boxY[i], _boxW[i], a, scale, bubble.LineHeight,
                           background, stackH > 0f);
                if (stackH > 0f)
                {
                    FlushBodies(ref n);
                    Use(NameplateKnobs.Plate);
                    EmitPlate(ref n, ref pn, su, sv, plate, e, _boxX[i] + (_boxW[i] - _boxPlateW[i]) * 0.5f,
                              _boxY[i] + _boxH[i] - stackH, a, scale, plate.LineHeight, portraits, background,
                              hits, ref hitN);
                }
            }
            else
            {
                Use(NameplateKnobs.Plate);
                EmitPlate(ref n, ref pn, su, sv, plate, e, _boxX[i], _boxY[i], a, scale, plate.LineHeight, portraits,
                          background, hits, ref hitN);
            }
            FlushBodies(ref n);
        }

        if (n > 0) TextRig.DrawBatch(_verts, n / VertexBatch.Floats);
        if (pn > 0) DrawPortraits(pn);
        Publish(hitN);
    }

    /// <summary>The master's edge, on the frame (the one thread the bubble mode is written from): on = text-bubble mode 0 and
    /// the table starts empty, so every entry is re-noted through the hooks and its anchor unfrozen; off = the
    /// engine's own mode back (one cold <c>ReloadModel</c> walk) and the table dropped, since nothing would keep
    /// its entries' anchors fresh.</summary>
    private static void ApplyMaster(bool on)
    {
        _suppressed = on;
        _table.Clear();
        int mode = on ? 0 : (EngineMode >= 0 ? EngineMode : DefaultEngineMode);
        if (_origSetMode != null) _origSetMode(mode);
        _log?.Info($"[nameplate] master {(on ? "on: engine at text-bubble mode 0" : $"off: engine bubble mode {mode} restored")}");
    }

    /// <summary>COLD, one shot per probe press: every term of the anchor conversion, so a residual
    /// offset can be settled from a single log rather than another disassembly session. Logs the raw
    /// bubble-anchor floats, what they converted to, both viewports, the GUI scale, and the
    /// behind-camera dot product for every tracked slot -- including the ones the frame gates rejected.</summary>
    private static void Probe(float vpW, float vpH)
    {
        ExtenderLog? log = _log;
        if (log == null) return;

        bool hasRect = NameplateNative.ViewportRect(out float rx, out float ry, out float rw, out float rh);
        bool hasCamRect = NameplateNative.CameraViewportRect(out float mx, out float my, out float mw, out float mh);
        bool hasGl = NameplateNative.GlViewportRect(out float gx, out float gy, out float gw, out float gh);
        bool hasCam = NameplateNative.CameraPosition(out float cx, out float cy, out float cz);
        bool hasFwd = NameplateNative.CameraForward(out float fx, out float fy, out float fz);
        float gui = Metrics.GameUiScale;

        log.Info($"[nameplate probe] draw={vpW}x{vpH} used={(hasRect ? "" : "MISSING ")}({rx},{ry},{rw},{rh}) "
               + $"camvp={(hasCamRect ? "" : "MISSING ")}({mx},{my},{mw},{mh}) "
               + $"glvp={(hasGl ? "" : "MISSING ")}({gx},{gy},{gw},{gh}) "
               + $"guiscale={gui.ToString("F3", CultureInfo.InvariantCulture)} "
               + $"campos={(hasCam ? $"({cx},{cy},{cz})" : "MISSING")} "
               + $"camfwd={(hasFwd ? $"({fx},{fy},{fz})" : "MISSING")} visible={_table.VisibleCount}");

        PlateTable.Entry[] all = _table.Entries;
        for (int i = 0; i < all.Length; i++)
        {
            PlateTable.Entry e = all[i];
            if (!e.Used) continue;
            IntPtr obj = NameplateNative.GameObject(e.Id);
            if (obj == IntPtr.Zero) { log.Info($"[nameplate probe] id=0x{e.Id:x} <no live object>"); continue; }
            if (!NameplateNative.AnchorRaw(obj, out float ax, out float ay))
            { log.Info($"[nameplate probe] id=0x{e.Id:x} <no bubble>"); continue; }

            PlateLayout.ToDrawSpace(ax, ay, rx, ry, rw, rh, gui, vpW, vpH, out float dx2, out float dy2);
            NameplateNative.Position(obj, out float ox, out float oy, out float oz);
            float dot = hasCam && hasFwd ? (ox - cx) * fx + (oy - cy) * fy + (oz - cz) * fz : float.NaN;
            log.Info($"[nameplate probe] id=0x{e.Id:x} type={e.Type} name='{e.Name}' "
                   + $"raw=({ax},{ay}) draw=({dx2},{dy2}) world=({ox},{oy},{oz}) "
                   + $"fwddot={dot.ToString("F2", CultureInfo.InvariantCulture)} "
                   + $"dist={e.Distance.ToString("F1", CultureInfo.InvariantCulture)} "
                   + $"alpha={e.Alpha.ToString("F2", CultureInfo.InvariantCulture)} "
                   + $"yoff={e.YOff.ToString("F1", CultureInfo.InvariantCulture)} "
                   + $"plated={e.Plated} bubble={e.ShowBubble} alive={e.Alive} band={e.Damage} "
                   + $"health={e.Health.ToString("F3", CultureInfo.InvariantCulture)} "
                   + $"ovmode={e.OverrideMode} ovparas={e.ParaCount} ov='{e.OverrideText.Replace("\n", "\\n")}'");
        }
    }

    private static void Publish(int count)
    {
        int back = _hitCur ^ 1;
        _hitCount[back] = count;
        _hitCur = back;
    }

    // ---- measurement -------------------------------------------------------------------------

    private static float MeasureRunes(GlyphAtlas atlas, ReadOnlySpan<char> s)
    {
        float w = 0f;
        foreach (Rune r in s.EnumerateRunes()) w += atlas.AdvanceOf(r);
        return w;
    }

    /// <summary>Width of one wrapped name line, the ellipsis included on the last line of a name the wrap had
    /// to cut. Hot, but the ellipsis measure only happens on a cut name.</summary>
    private static float NameLineW(PlateTable.Entry e, GlyphAtlas atlas, int i, int lines)
    {
        float w = MeasureRunes(atlas, e.Name.AsSpan(e.NameStart[i], e.NameLen[i]));
        if (e.NameTruncated && i == lines - 1) w += MeasureRunes(atlas, PlateTable.Ellipsis);
        return w;
    }

    /// <summary>Widest of the entry's name lines, and how many there are. The single funnel through which the
    /// name acquires its wrap: <c>EnsureNameWrap</c> is cold-on-change (keyed on width + atlas epoch + scale),
    /// so a steady frame is three compares and the measure walks preallocated spans, allocating nothing.</summary>
    private static float MeasureName(PlateTable.Entry e, GlyphAtlas atlas, float maxW, out int lines)
    {
        PlateTable.EnsureNameWrap(e, atlas.AdvanceOfCached, maxW, _wrapKey);
        lines = e.NameLineCount;
        float w = 0f;
        for (int i = 0; i < lines; i++)
        {
            float lw = NameLineW(e, atlas, i, lines);
            if (lw > w) w = lw;
        }
        return w;
    }

    /// <summary>Whether a plate for this entry has anything to show: a name, or a health band to colour.</summary>
    private static bool PlateHasContent(PlateTable.Entry e)
        => e.Name.Length != 0 || (NameplateKnobs.HealthBand.V && e.Damage >= 0);

    /// <summary>The max width a plate name column and a bubble box share -- one knob, <c>nameplate.width</c>.</summary>
    private static float TextMaxW(float scale) => NameplateKnobs.Width.V * scale;

    private static void MeasurePlate(PlateTable.Entry e, GlyphAtlas atlas, float scale, float lineH,
                                     bool portraits, out float w, out float h)
    {
        bool band = NameplateKnobs.HealthBand.V && e.Damage >= 0;
        float nameW = MeasureName(e, atlas, TextMaxW(scale), out int nameLines);
        float bandW = band ? MeasureRunes(atlas, PlateLayout.BandText(e.Damage)) : 0f;
        bool portrait = portraits && e.PortraitResref.Length != 0;
        OverrideLayout ov = LayoutOverride(e, atlas, scale, nameLines, band, bandW);
        PlateMetrics m = PlateLayout.Measure(ov.ShowName ? nameW : 0f, ov.ShowName ? nameLines : 0,
                                             bandW, portrait, ov.ShowBand,
                                             NameplateKnobs.HealthBar.V && e.Damage >= 0, lineH, scale,
                                             ov.PreLines, ov.PostLines, ov.ExtraW);
        w = m.W; h = m.H;
    }

    /// <param name="stacked">a nameplate is stacked under this bubble (<see cref="UseCard"/>).</param>
    private static void MeasureBubble(PlateTable.Entry e, GlyphAtlas atlas, float scale, float lineH, bool stacked,
                                      out float w, out float h)
    {
        if (UseCard(e, stacked, out bool name, out bool portrait))
        {
            CardMetrics m = MeasureCardOf(e, atlas, scale, lineH, name, portrait);
            w = m.W; h = m.H + TailH(scale);
            return;
        }
        float pad = BubblePadPx * scale, gap = BubbleGapPx * scale;
        float maxW = TextMaxW(scale);
        w = 0f; h = 0f;
        for (int i = 0; i < e.LineCount; i++)
        {
            PlateTable.BubbleLine ln = e.Lines[i];
            PlateTable.EnsureWrap(ln, atlas, maxW, _wrapKey, NameplateKnobs.Lines.V);
            float bw = ln.WrapPixelW + pad * 2f;
            if (bw > w) w = bw;
            h += ln.SpanCount * lineH + pad * 2f + (i > 0 ? gap : 0f);
        }
        if (w <= 0f) w = pad * 2f;
        if (h <= 0f) h = lineH + pad * 2f;
        h += TailH(scale);   // the band the tail hangs into; the box's own bottom edge stops above it
    }

    /// <summary>Whether this bubble draws as a speech card, and with which parts (<see cref="PlateLayout.CardParts"/>).</summary>
    private static bool UseCard(PlateTable.Entry e, bool stacked, out bool name, out bool portrait)
        => PlateLayout.CardParts(NameplateKnobs.Speaker.V, stacked, NameplateKnobs.Portraits.V, e.Name.Length != 0,
                                 e.PortraitResref.Length != 0, out name, out portrait);

    /// <summary>Wrap every queued line -- past the engine's <c>"[speaker]: "</c> prefix when the header already names
    /// the speaker -- and lay the card out around the widest of them and the header. The header is the plate's own
    /// name wrap (same lines, same cache), so a "name + title" name keeps both lines.</summary>
    private static CardMetrics MeasureCardOf(PlateTable.Entry e, GlyphAtlas atlas, float scale, float lineH,
                                             bool name, bool portrait)
    {
        float maxW = TextMaxW(scale), gap = CardParaGapPx * scale, bodyH = 0f;
        int nameLines = 0;
        float textW = name ? MeasureName(e, atlas, maxW, out nameLines) : 0f;
        for (int i = 0; i < e.LineCount; i++)
        {
            PlateTable.BubbleLine ln = e.Lines[i];
            PlateTable.EnsureWrap(ln, atlas, maxW, _wrapKey, NameplateKnobs.Lines.V, name ? ln.Prefix : 0);
            if (ln.WrapPixelW > textW) textW = ln.WrapPixelW;
            bodyH += ln.SpanCount * lineH + (i > 0 ? gap : 0f);
        }
        return PlateLayout.MeasureCard(textW, bodyH, nameLines, portrait ? NameplateKnobs.PortraitLines.V : 0, lineH,
                                       BubblePadPx * scale, scale);
    }

    /// <summary>The chrome the speech tail needs, or 0 when there is no box for it to hang off (the background
    /// turned off). A plate never gets one -- it is a label, not speech.</summary>
    private static float TailH(float scale) => NameplateKnobs.Background.V ? TailHeightPx * scale : 0f;

    // ---- emit --------------------------------------------------------------------------------

    private static void Grow(int need)
    {
        while (need > _verts.Length) Array.Resize(ref _verts, _verts.Length * 2);
    }

    private static void Vtx(ref int n, float x, float y, float u, float v,
                            float r, float g, float b, float a)
    { float[] q = _verts; q[n++] = x; q[n++] = y; q[n++] = u; q[n++] = v; q[n++] = r; q[n++] = g; q[n++] = b; q[n++] = a; }

    private static void EmitRect(ref int n, float su, float sv, float x0, float y0, float x1, float y1,
                                 float r, float g, float b, float a)
    {
        Grow(n + 6 * VertexBatch.Floats);
        Vtx(ref n, x0, y0, su, sv, r, g, b, a); Vtx(ref n, x1, y0, su, sv, r, g, b, a); Vtx(ref n, x1, y1, su, sv, r, g, b, a);
        Vtx(ref n, x0, y0, su, sv, r, g, b, a); Vtx(ref n, x1, y1, su, sv, r, g, b, a); Vtx(ref n, x0, y1, su, sv, r, g, b, a);
    }

    /// <summary>Solid-colour triangle, for the speech tail. The batch is submitted as <c>GL_TRIANGLES</c> with
    /// culling off, so three vertices in any winding is a whole primitive.</summary>
    private static void EmitTri(ref int n, float su, float sv, float x0, float y0, float x1, float y1,
                                float x2, float y2, float r, float g, float b, float a)
    {
        Grow(n + 3 * VertexBatch.Floats);
        Vtx(ref n, x0, y0, su, sv, r, g, b, a); Vtx(ref n, x1, y1, su, sv, r, g, b, a); Vtx(ref n, x2, y2, su, sv, r, g, b, a);
    }

    /// <summary>One sheared glyph quad into <paramref name="q"/> at <paramref name="n"/>, growing it on demand.</summary>
    private static void GlyphQuad(ref float[] q, ref int n, in Glyph gl, float penX, float baseline,
                                  float r, float g, float b, float a, float shear)
    {
        if (gl.W <= 0) return;
        while (n + 6 * VertexBatch.Floats > q.Length) Array.Resize(ref q, q.Length * 2);
        float x0 = penX + gl.XOff, y0 = baseline + gl.YOff, x1 = x0 + gl.W, y1 = y0 + gl.H;
        float dx = shear * gl.H;
        Span<float> v = q.AsSpan(n, 6 * VertexBatch.Floats);
        v[0] = x0 + dx; v[1] = y0; v[2] = gl.U0; v[3] = gl.V0;
        v[8] = x1 + dx; v[9] = y0; v[10] = gl.U1; v[11] = gl.V0;
        v[16] = x1; v[17] = y1; v[18] = gl.U1; v[19] = gl.V1;
        v[24] = x0 + dx; v[25] = y0; v[26] = gl.U0; v[27] = gl.V0;
        v[32] = x1; v[33] = y1; v[34] = gl.U1; v[35] = gl.V1;
        v[40] = x0; v[41] = y1; v[42] = gl.U0; v[43] = gl.V1;
        for (int o = 4; o < v.Length; o += VertexBatch.Floats) { v[o] = r; v[o + 1] = g; v[o + 2] = b; v[o + 3] = a; }
        n += 6 * VertexBatch.Floats;
    }

    /// <summary>One glyph: its halo (the style's black rings, the whole readability answer over a bright world) into
    /// <see cref="_verts"/> now, its body into <see cref="_bodies"/> for <see cref="FlushBodies"/>.</summary>
    private static void EmitGlyph(ref int n, in Glyph gl, float penX, float baseline,
                                  float r, float g, float b, float a, float shear)
    {
        for (int o = 0; o < _haloN; o += 2)
            GlyphQuad(ref _verts, ref n, gl, penX + _haloOff[o], baseline + _haloOff[o + 1], 0f, 0f, 0f, a, shear);
        GlyphQuad(ref _bodies, ref _bn, gl, penX, baseline, r, g, b, a, shear);
    }

    /// <summary>Append the entry's held-back glyph bodies after its halos.</summary>
    private static void FlushBodies(ref int n)
    {
        if (_bn == 0) return;
        Grow(n + _bn);
        Array.Copy(_bodies, 0, _verts, n, _bn);
        n += _bn; _bn = 0;
    }

    /// <summary>One string at one colour, outlined when the style asks.</summary>
    private static void EmitString(ref int n, GlyphAtlas atlas, ReadOnlySpan<char> s, float penX, float baseline,
                                   float r, float g, float b, float a, float shear)
    {
        foreach (Rune ru in s.EnumerateRunes())
        {
            if (!atlas.TryGet(ru.Value, out Glyph gl)) { penX += atlas.AdvanceOf(ru); continue; }
            EmitGlyph(ref n, gl, penX, baseline, r, g, b, a, shear);
            penX += gl.Advance;
        }
    }

    /// <summary>One line of a plate NAME. Same glyphs as <see cref="EmitString"/>, except that a char the name's
    /// own <c>&lt;cRGB&gt;</c> tags coloured wins over the reaction colour. <paramref name="colours"/> is indexed
    /// by char of <c>Entry.Name</c>, so the line's own char offset is passed in rather than assumed to be
    /// zero.</summary>
    private static void EmitNameLine(ref int n, GlyphAtlas atlas, ReadOnlySpan<char> s, uint[]? colours,
                                     int offset, float penX, float baseline,
                                     float r, float g, float b, float a)
    {
        if (colours == null) { EmitString(ref n, atlas, s, penX, baseline, r, g, b, a, 0f); return; }
        int ci = 0;
        while (ci < s.Length)
        {
            Rune.DecodeFromUtf16(s.Slice(ci), out Rune ru, out int used);
            int at = offset + ci;
            uint c = at < colours.Length ? colours[at] : PlateLayout.Plain;
            float cr = r, cg = g, cb = b;
            if (c != PlateLayout.Plain)
            { cr = ((c >> 16) & 0xFF) / 255f; cg = ((c >> 8) & 0xFF) / 255f; cb = (c & 0xFF) / 255f; }
            ci += used;
            if (!atlas.TryGet(ru.Value, out Glyph gl)) { penX += atlas.AdvanceOf(ru); continue; }
            EmitGlyph(ref n, gl, penX, baseline, cr, cg, cb, a, 0f);
            penX += gl.Advance;
        }
    }

    /// <summary>Emit one run of already-parsed, per-rune-coloured text and return the pen. The one place colour
    /// runs become glyphs: the bubble's wrapped spans and the plate's override lines both come through here.
    /// <paramref name="fallbackRgb"/> re-colours only runes the parser left at plain white, which is how a
    /// channel style tints untagged text without overriding the author's own colours.</summary>
    private static float EmitRuns(ref int n, GlyphAtlas atlas, List<Rune> runes, uint[] colours,
                                  int from, int to, float penX, float baseline, float a,
                                  uint fallbackRgb, float shear, out uint tailRgb)
    {
        tailRgb = 0xffffffu;
        for (int k = from; k < to && k < runes.Count; k++)
        {
            Rune ru = runes[k];
            uint c = k < colours.Length ? colours[k] : 0xffffffu;
            tailRgb = c;
            if (!atlas.TryGet(ru.Value, out Glyph gl)) { penX += atlas.AdvanceOf(ru); continue; }
            if (c == 0xffffffu && fallbackRgb != 0u) c = fallbackRgb;
            float r = ((c >> 16) & 0xFF) / 255f, g = ((c >> 8) & 0xFF) / 255f, b = (c & 0xFF) / 255f;
            EmitGlyph(ref n, gl, penX, baseline, r, g, b, a, shear);
            penX += gl.Advance;
        }
        return penX;
    }

    /// <summary>Width of one wrapped span of a colour-run line. Hot but tiny -- override lines are status
    /// labels, and a plate carries at most <see cref="PlateTable.MaxParas"/> of them.</summary>
    private static float SpanW(GlyphAtlas atlas, PlateTable.BubbleLine ln, int s)
    {
        TextWrapper.LineSpan sp = ln.Spans[s];
        float w = 0f;
        for (int k = sp.Start; k < sp.End && k < ln.Runes.Count; k++) w += atlas.AdvanceOf(ln.Runes[k]);
        return w;
    }

    /// <summary>Where an entry's <c>ObjectTextBubbleOverride</c> lands on the plate, resolved once and used by
    /// both the measure and the emit. The engine composes ONE string, <c>name\nband</c>, and glues the override
    /// onto its head or tail -- so the segment at the joint continues an existing line and the rest become
    /// lines of their own.</summary>
    private struct OverrideLayout
    {
        public bool ShowName, ShowBand;
        public PlateSplice Target;
        /// <summary>Para spliced onto <see cref="Target"/>'s line, or -1.</summary>
        public int SpliceIdx;
        public int PreFrom, PreTo, PostFrom, PostTo;
        public int PreLines, PostLines;
        public float SpliceW, ExtraW;
    }

    /// <summary>Wrap the override's lines around the placement <see cref="PlateLayout.PlaceOverride"/> chose.
    /// The splice keeps to a SINGLE line, wrapped into whatever its anchor leaves of the column: a status
    /// suffix is short, and letting it wrap would put half of it under the band and half beside it.
    /// <para>ponytail: raise that cap here if a server ever glues a paragraph onto the injury line.</para></summary>
    private static OverrideLayout LayoutOverride(PlateTable.Entry e, GlyphAtlas atlas, float scale,
                                                 int nameLines, bool band, float bandW)
    {
        OverrideLayout o = default;
        o.Target = PlateLayout.PlaceOverride(e.OverrideMode, e.ParaCount, nameLines, band,
                                             out o.ShowName, out o.ShowBand, out o.SpliceIdx,
                                             out o.PreFrom, out o.PreTo, out o.PostFrom, out o.PostTo);
        if (e.OverrideMode == 0 || e.ParaCount == 0) return o;

        float maxW = TextMaxW(scale);
        if (o.SpliceIdx >= 0)
        {
            float anchorW = o.Target switch
            {
                PlateSplice.BandFirst or PlateSplice.BandLast => bandW,
                PlateSplice.NameFirst => NameLineW(e, atlas, 0, nameLines),
                PlateSplice.NameLast => NameLineW(e, atlas, nameLines - 1, nameLines),
                _ => 0f,
            };
            PlateTable.BubbleLine sp = e.Paras[o.SpliceIdx];
            if (sp.Runes.Count != 0)
            {
                float room = maxW - anchorW;
                float floorW = maxW * SpliceMinFraction;
                if (room < floorW) room = floorW;
                PlateTable.EnsureWrap(sp, atlas, room, _wrapKey, 1);
                o.SpliceW = sp.SpanCount > 0 ? SpanW(atlas, sp, 0) : 0f;
                if (sp.Truncated) o.SpliceW += MeasureRunes(atlas, PlateTable.Ellipsis);
            }
            o.ExtraW = anchorW + o.SpliceW;
        }

        o.PreLines = WrapParas(e, atlas, maxW, o.PreFrom, o.PreTo, ref o.ExtraW);
        o.PostLines = WrapParas(e, atlas, maxW, o.PostFrom, o.PostTo, ref o.ExtraW);
        return o;
    }

    /// <summary>Least of the text column a spliced segment is allowed, however wide the line it joins.</summary>
    private const float SpliceMinFraction = 0.25f;

    private static int WrapParas(PlateTable.Entry e, GlyphAtlas atlas, float maxW, int from, int to,
                                 ref float extraW)
    {
        int lines = 0;
        for (int i = from; i < to; i++)
        {
            PlateTable.BubbleLine ln = e.Paras[i];
            if (ln.Runes.Count == 0) continue;   // the empty segment that says "nothing glues here"
            PlateTable.EnsureWrap(ln, atlas, maxW, _wrapKey, NameplateKnobs.Lines.V);
            lines += ln.SpanCount;
            if (ln.WrapPixelW > extraW) extraW = ln.WrapPixelW;
        }
        return lines;
    }

    /// <summary>Draw the spliced override segment -- its single wrapped line -- at the pen the plate line it
    /// continues left off at.</summary>
    private static void EmitSplice(ref int n, GlyphAtlas atlas, PlateTable.Entry e, in OverrideLayout ov,
                                   float penX, float baseline, float a)
    {
        PlateTable.BubbleLine sp = e.Paras[ov.SpliceIdx];
        // An EMPTY splice segment ("\nSneaking") keeps whatever spans the pooled line last held; it is never
        // re-wrapped, so the run count is the only honest liveness test.
        if (sp.Runes.Count == 0 || sp.SpanCount == 0) return;
        float end = EmitRuns(ref n, atlas, sp.Runes, sp.Colours, sp.Spans[0].Start, sp.Spans[0].End,
                             penX, baseline, a, 0u, 0f, out uint tail);
        if (!sp.Truncated) return;
        EmitString(ref n, atlas, PlateTable.Ellipsis, end, baseline,
                   ((tail >> 16) & 0xFF) / 255f, ((tail >> 8) & 0xFF) / 255f, (tail & 0xFF) / 255f, a, 0f);
    }

    /// <summary>Draw a range of standalone override lines down from <paramref name="top"/>, and return the y
    /// past the last one.</summary>
    private static float EmitParas(ref int n, GlyphAtlas atlas, PlateTable.Entry e, int from, int to,
                                   float colX, float textW, float top, float a, float lineH)
    {
        for (int i = from; i < to; i++)
        {
            PlateTable.BubbleLine ln = e.Paras[i];
            if (ln.Runes.Count == 0) continue;
            for (int s = 0; s < ln.SpanCount; s++)
            {
                float lw = SpanW(atlas, ln, s);
                float lx = PlateLayout.LineX(colX, textW, lw);
                EmitRuns(ref n, atlas, ln.Runes, ln.Colours, ln.Spans[s].Start, ln.Spans[s].End,
                         lx, top + atlas.Ascent, a, 0u, 0f, out _);
                top += lineH;
            }
        }
        return top;
    }

    private static void EmitPlate(ref int n, ref int pn, float su, float sv, GlyphAtlas atlas,
                                  PlateTable.Entry e, float x, float y, float a, float scale, float lineH,
                                  bool portraits, bool background, Hit[] hits, ref int hitN)
    {
        bool band = NameplateKnobs.HealthBand.V && e.Damage >= 0;
        bool bar = NameplateKnobs.HealthBar.V && e.Damage >= 0;
        float nameW = MeasureName(e, atlas, TextMaxW(scale), out int nameLines);
        string bandText = band ? PlateLayout.BandText(e.Damage) : "";
        float bandW = band ? MeasureRunes(atlas, bandText) : 0f;
        bool portrait = portraits && e.PortraitResref.Length != 0;
        OverrideLayout ov = LayoutOverride(e, atlas, scale, nameLines, band, bandW);
        band = ov.ShowBand;
        if (!ov.ShowName) nameLines = 0;
        PlateMetrics m = PlateLayout.Measure(ov.ShowName ? nameW : 0f, nameLines, bandW, portrait, band, bar,
                                             lineH, scale, ov.PreLines, ov.PostLines, ov.ExtraW);

        if (background) EmitRect(ref n, su, sv, x, y, x + m.W, y + m.H, PlateBg.r, PlateBg.g, PlateBg.b, PlateBg.a * a);

        (float r, float g, float b) nc = PlateLayout.ReactionColor(e.Reaction);
        float colX = x + m.NameX;

        // Override lines that sit above the name (prepend mode) or replace the plate outright (mode 1).
        EmitParas(ref n, atlas, e, ov.PreFrom, ov.PreTo, colX, m.TextW, y + m.PreY, a, lineH);

        for (int i = 0; i < nameLines; i++)
        {
            ReadOnlySpan<char> line = e.Name.AsSpan(e.NameStart[i], e.NameLen[i]);
            float lw = NameLineW(e, atlas, i, nameLines);
            bool prefixed = ov.Target == PlateSplice.NameFirst && i == 0;
            bool suffixed = ov.Target == PlateSplice.NameLast && i == nameLines - 1;
            float lx = PlateLayout.LineX(colX, m.TextW, lw + (prefixed || suffixed ? ov.SpliceW : 0f));
            float by = y + m.NameY + i * lineH + atlas.Ascent;
            if (prefixed) { EmitSplice(ref n, atlas, e, ov, lx, by, a); lx += ov.SpliceW; }
            EmitNameLine(ref n, atlas, line, e.NameColours, e.NameStart[i], lx, by, nc.r, nc.g, nc.b, a);
            float penX = lx + lw;
            if (e.NameTruncated && i == nameLines - 1)
                EmitString(ref n, atlas, PlateTable.Ellipsis,
                           lx + lw - MeasureRunes(atlas, PlateTable.Ellipsis), by, nc.r, nc.g, nc.b, a, 0f);
            if (suffixed) EmitSplice(ref n, atlas, e, ov, penX, by, a);
        }

        // The name glyphs only, never the plate box or the override -- see OnPlateClick. Every line centres in
        // the text column, so the column IS the rect: a narrower one would miss the name it just moved.
        if (NameplateKnobs.ClickToTell.V && ov.ShowName && e.Name.Length != 0 && nameW > 0f && hitN < hits.Length)
            hits[hitN++] = new Hit { X = colX, Y = y + m.NameY, W = m.TextW,
                                     H = lineH * nameLines, Name = e.Name };

        if (band)
        {
            (float r, float g, float b) bc = PlateLayout.BandColor(e.Damage);
            float by = y + m.BandY + atlas.Ascent;
            bool spliced = ov.Target is PlateSplice.BandFirst or PlateSplice.BandLast;
            float bx = PlateLayout.LineX(x + m.BandX, m.TextW, bandW + (spliced ? ov.SpliceW : 0f));
            // A prepend that has no name line to run into runs into the band instead -- in FRONT of it.
            if (ov.Target == PlateSplice.BandFirst) { EmitSplice(ref n, atlas, e, ov, bx, by, a); bx += ov.SpliceW; }
            EmitString(ref n, atlas, bandText, bx, by, bc.r, bc.g, bc.b, a, 0f);
            if (ov.Target == PlateSplice.BandLast) EmitSplice(ref n, atlas, e, ov, bx + bandW, by, a);
        }

        // ... and the ones below it (append mode).
        EmitParas(ref n, atlas, e, ov.PostFrom, ov.PostTo, colX, m.TextW, y + m.PostY, a, lineH);

        if (bar && m.BarW > 0f)
        {
            (float r, float g, float b) bc = PlateLayout.BandColor(e.Damage);
            float frac = e.Health < 0f ? 0f : e.Health;
            EmitRect(ref n, su, sv, x + m.BarX, y + m.BarY, x + m.BarX + m.BarW, y + m.BarY + m.BarH,
                     BarTrack.r, BarTrack.g, BarTrack.b, BarTrack.a * a);
            EmitRect(ref n, su, sv, x + m.BarX, y + m.BarY, x + m.BarX + m.BarW * frac, y + m.BarY + m.BarH,
                     bc.r, bc.g, bc.b, a);
        }

        if (portrait)
            AddPortrait(ref pn, e.PortraitResref, x + m.PortraitX, y + m.PortraitY,
                        m.PortraitSize * TextRig.PortraitAspect, m.PortraitSize, a);
    }

    /// <summary>Queue one portrait for <see cref="DrawPortraits"/>. Past <see cref="MaxPortraitsPerFrame"/> the
    /// rest go without.</summary>
    private static void AddPortrait(ref int pn, string resref, float x, float y, float w, float h, float a)
    {
        if (pn >= MaxPortraitsPerFrame) return;
        _portraitRef[pn] = resref;
        int o = pn * 5;
        _portraitRect[o] = x; _portraitRect[o + 1] = y; _portraitRect[o + 2] = w; _portraitRect[o + 3] = h;
        _portraitRect[o + 4] = a;
        pn++;
    }

    private static void EmitBubble(ref int n, ref int pn, float su, float sv, GlyphAtlas atlas, PlateTable.Entry e,
                                   float x, float y, float w, float a, float scale, float lineH, bool background,
                                   bool stacked)
    {
        if (UseCard(e, stacked, out bool name, out bool portrait))
        {
            EmitCard(ref n, ref pn, su, sv, atlas, e, x, y, w, a, scale, lineH, background, name, portrait);
            return;
        }
        float pad = BubblePadPx * scale, gap = BubbleGapPx * scale;
        float cy = y;
        // The tail hangs off the NEWEST line's box -- the bottom-most one -- so it is the last one's geometry
        // and alpha the tail is drawn from.
        float tailX = x, tailW = w, tailY = y, tailA = 0f;
        for (int i = 0; i < e.LineCount; i++)
        {
            PlateTable.BubbleLine ln = e.Lines[i];
            float bh = ln.SpanCount * lineH + pad * 2f;
            float bw = ln.WrapPixelW + pad * 2f;
            float bx = x + (w - bw) * 0.5f;
            // Oldest at the top, so it is also the first to fade out from under the newer lines. Each line
            // carries its OWN fade (PlateTable.ExpireLines), which is what stops a dying line from popping.
            float la = a * ln.Alpha * (i == e.TopLine ? 1f : OlderLineAlpha);
            tailX = bx; tailW = bw; tailY = cy + bh; tailA = la;
            ChannelStyle st = StyleOf(ln.Channel);
            if (background)
                EmitRect(ref n, su, sv, bx, cy, bx + bw, cy + bh,
                         BubbleBg.r, BubbleBg.g, BubbleBg.b, BubbleBg.a * la);

            EmitLineSpans(ref n, atlas, ln, bx + pad, cy + pad, la, lineH, st);
            cy += bh + gap;
        }

        if (background) EmitTail(ref n, su, sv, e, tailX, tailW, tailY, tailA, scale);
    }

    /// <summary>A speech card: one box with the speaker's portrait top-left, their name as a header in the reaction
    /// colour, then every queued line oldest first, left-aligned under it. The box, header and portrait take the
    /// NEWEST line's alpha -- the card lives as long as that line -- while each line keeps its own fade and the older
    /// ones are dimmed, as in the plain boxes.</summary>
    private static void EmitCard(ref int n, ref int pn, float su, float sv, GlyphAtlas atlas, PlateTable.Entry e,
                                 float x, float y, float w, float a, float scale, float lineH, bool background,
                                 bool name, bool portrait)
    {
        if (e.TopLine < 0) return;
        CardMetrics m = MeasureCardOf(e, atlas, scale, lineH, name, portrait);
        float pad = BubblePadPx * scale, gap = CardParaGapPx * scale;
        float cx = x + (w - m.W) * 0.5f;
        float boxA = a * e.Lines[e.TopLine].Alpha;
        if (background)
            EmitRect(ref n, su, sv, cx, y, cx + m.W, y + m.H, BubbleBg.r, BubbleBg.g, BubbleBg.b, BubbleBg.a * boxA);

        float colX = cx + m.ColX;
        int nameLines = name ? e.NameLineCount : 0;
        (float r, float g, float b) nc = PlateLayout.ReactionColor(e.Reaction);
        for (int i = 0; i < nameLines; i++)
        {
            float by = y + pad + i * lineH + atlas.Ascent;
            EmitNameLine(ref n, atlas, e.Name.AsSpan(e.NameStart[i], e.NameLen[i]), e.NameColours, e.NameStart[i],
                         colX, by, nc.r, nc.g, nc.b, boxA);
            if (e.NameTruncated && i == nameLines - 1)
                EmitString(ref n, atlas, PlateTable.Ellipsis,
                           colX + NameLineW(e, atlas, i, nameLines) - MeasureRunes(atlas, PlateTable.Ellipsis), by,
                           nc.r, nc.g, nc.b, boxA, 0f);
        }

        float top = y + m.BodyY;
        for (int i = 0; i < e.LineCount; i++)
        {
            PlateTable.BubbleLine ln = e.Lines[i];
            float la = a * ln.Alpha * (i == e.TopLine ? 1f : OlderLineAlpha);
            EmitLineSpans(ref n, atlas, ln, colX, top, la, lineH, StyleOf(ln.Channel));
            top += ln.SpanCount * lineH + gap;
        }

        if (portrait) AddPortrait(ref pn, e.PortraitResref, cx + pad, y + pad, m.PortraitW, m.PortraitH, boxA);
        if (background) EmitTail(ref n, su, sv, e, cx, m.W, y + m.H, boxA, scale);
    }

    /// <summary>One queued line's wrapped spans, left-aligned at <paramref name="x"/> down from <paramref name="top"/>
    /// in its channel style. A line the wrap cut says so with an ellipsis on its last kept span rather than stopping
    /// mid-word.</summary>
    private static void EmitLineSpans(ref int n, GlyphAtlas atlas, PlateTable.BubbleLine ln, float x, float top,
                                      float la, float lineH, ChannelStyle st)
    {
        for (int s = 0; s < ln.SpanCount; s++)
        {
            float baseline = top + s * lineH + atlas.Ascent;
            TextWrapper.LineSpan sp = ln.Spans[s];
            float penX = EmitRuns(ref n, atlas, ln.Runes, ln.Colours, sp.Start, sp.End, x, baseline,
                                  la * st.Alpha, st.Rgb, st.Shear, out uint tail);
            if (!ln.Truncated || s != ln.SpanCount - 1) continue;
            if (tail == 0xffffffu && st.Rgb != 0u) tail = st.Rgb;
            EmitString(ref n, atlas, PlateTable.Ellipsis, penX, baseline,
                       ((tail >> 16) & 0xFF) / 255f, ((tail >> 8) & 0xFF) / 255f, (tail & 0xFF) / 255f,
                       la * st.Alpha, st.Shear);
        }
    }

    /// <summary>The speech tail under a box whose bottom edge is at <paramref name="bottom"/>, its tip leaning toward
    /// the speaker's anchor (<see cref="PlateLayout.TailPoints"/>).</summary>
    private static void EmitTail(ref int n, float su, float sv, PlateTable.Entry e, float boxX, float boxW,
                                 float bottom, float ta, float scale)
    {
        float th = TailH(scale);
        if (th <= 0f || ta <= 0f) return;
        PlateLayout.TailPoints(e.AnchorX, boxX, boxW, TailWidthPx * scale, BubblePadPx * scale,
                               out float bl, out float br, out float tipX);
        if (br > bl)
            EmitTri(ref n, su, sv, bl, bottom, br, bottom, tipX, bottom + th,
                    BubbleBg.r, BubbleBg.g, BubbleBg.b, BubbleBg.a * ta);
    }

    // ---- channel styling ---------------------------------------------------------------------

    /// <summary>Per-channel emphasis: a tint applied only to text the server did not colour itself, an
    /// alpha multiplier and a synthetic-italic shear.</summary>
    private readonly struct ChannelStyle
    {
        public readonly uint Rgb; public readonly float Alpha, Shear;
        public ChannelStyle(uint rgb, float alpha, float shear) { Rgb = rgb; Alpha = alpha; Shear = shear; }
    }

    private static readonly ChannelStyle Neutral = new(0u, 1f, 0f);

    /// <summary>Channel values are the standard NWN message channels read out of
    /// <c>HandleServerToPlayerChat</c>'s switch: talk 1, shout 2, whisper 3, tell 4, party 6, DM 7, and the
    /// DM variants 8..0x0e / 0x11..0x1e of the same. Off (or unlatched) is one neutral style.</summary>
    private static ChannelStyle StyleOf(int channel)
    {
        if (!_chatHooked || !NameplateKnobs.ChannelStyle.V || channel < 0) return Neutral;
        return channel switch
        {
            0x02 or 0x09 or 0x12 => new ChannelStyle(0xffd28cu, 1f, 0f),      // shout
            0x03 or 0x0a or 0x13 => new ChannelStyle(0xa8b0bcu, 0.75f, 0.2f),// whisper: dim + italic
            0x04 or 0x14 => new ChannelStyle(0xe8a8ffu, 1f, 0f),             // tell
            0x06 or 0x16 => new ChannelStyle(0x9ce0a0u, 1f, 0f),             // party
            0x07 or 0x0e or 0x1e => new ChannelStyle(0xffb060u, 1f, 0f),     // DM
            _ => Neutral,
        };
    }

    // ---- portraits ---------------------------------------------------------------------------

    /// <summary>One line per NEW portrait texture, the first eight: level-0 size, internal format, level-1 size,
    /// mip range, filter, and a hash of levels 0 and 1 as the driver holds them. Read off the ENGINE's texture,
    /// because that is what the plate samples. Cold: once per portrait name; the readback is a sync, spent eight
    /// times a session.</summary>
    private static void PortraitDiag(int tid, string resref)
    {
        if (_diagN >= _diagTids.Length || !GlContextService.Available) return;
        for (int i = 0; i < _diagN; i++) if (_diagTids[i] == tid) return;
        _diagTids[_diagN++] = tid;
        uint t = (uint)tid;
        int w = 0, h = 0, fmt = 0, w1 = 0, h1 = 0, maxLevel = -1, minFilter = 0;
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_WIDTH, &w);
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_HEIGHT, &h);
        Gl.GetTextureLevelParameteriv(t, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        Gl.GetTextureLevelParameteriv(t, 1, Gl.TEXTURE_WIDTH, &w1);
        Gl.GetTextureLevelParameteriv(t, 1, Gl.TEXTURE_HEIGHT, &h1);
        Gl.GetTextureParameteriv(t, Gl.TEXTURE_MAX_LEVEL, &maxLevel);
        Gl.GetTextureParameteriv(t, Gl.TEXTURE_MIN_FILTER, &minFilter);
        ulong hash0 = 0, hash1 = 0;
        if (w > 0 && h > 0 && w <= 1024 && h <= 1024)
        {
            byte[] px = new byte[w * h * 4];   // cold: once per portrait name
            int align; Gl.GetIntegerv(Gl.PACK_ALIGNMENT, &align);
            Gl.PixelStorei(Gl.PACK_ALIGNMENT, 1);
            fixed (byte* p = px)
            {
                Gl.GetTextureImage(t, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, px.Length, p);
                hash0 = Fnv(p, w * h * 4);
                if (w1 > 0 && h1 > 0)
                {
                    Gl.GetTextureImage(t, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, px.Length, p);
                    hash1 = Fnv(p, w1 * h1 * 4);
                }
            }
            Gl.PixelStorei(Gl.PACK_ALIGNMENT, align);
        }
        _log?.Info($"[nameplate] portrait {resref} tid {tid}: {w}x{h} fmt 0x{fmt:x} level1 {w1}x{h1} " +
                   $"maxlevel {maxLevel} minfilter 0x{minFilter:x} hash0 {hash0:x16} hash1 {hash1:x16} glerr 0x{Gl.GetError():x}");
    }

    private static readonly int[] _diagTids = new int[8];
    private static int _diagN;

    private static ulong Fnv(byte* p, int n)
    {
        ulong x = 14695981039346656037UL;
        for (int i = 0; i < n; i++) { x ^= p[i]; x *= 1099511628211UL; }
        return x;
    }

    /// <summary>Second pass, the chat renderer's shape: resolve EVERY texture id and build ALL the geometry first,
    /// because <c>AurTextureGet</c>'s create path can touch GL and rebind <c>ARRAY_BUFFER</c> -- it must never run
    /// between our BindBuffer and BufferData. Then one upload and one bind+draw per plate. A texture id of 0 means
    /// the engine has not uploaded it yet: skip that plate this frame.</summary>
    private static void DrawPortraits(int count)
    {
        if (count <= 0 || !AurTex.Ready || !TextRig.EnsurePortraitProgram()) return;
        if (!TextRig._pvaoUp) TextRig.SetupPortraitVao();

        int* tids = stackalloc int[MaxPortraitsPerFrame];
        QuadVerts* verts = stackalloc QuadVerts[MaxPortraitsPerFrame];
        int drawn = 0;
        for (int i = 0; i < count; i++)
        {
            int o = i * 5;
            int tid = AurTex.TidByName(_portraitRef[i]);
            if (tid == 0) continue;
            PortraitDiag(tid, _portraitRef[i]);
            tids[drawn] = tid;
            TextRig.FillQuad(ref verts[drawn],
                             new CX.Services.Chat.Core.Model.PortraitQuad(
                                 _portraitRect[o], _portraitRect[o + 1], _portraitRect[o + 2], _portraitRect[o + 3],
                                 _portraitRef[i], default),
                             0f, _portraitRect[o + 4]);
            drawn++;
        }
        if (drawn == 0) return;

        Matrix4x4 m = TextRig.Ortho(TextRig.ViewportW, TextRig.ViewportH);

        Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
        Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        Gl.UseProgram(TextRig._progRgb);
        Gl.UniformMatrix4fv(TextRig._uProjRgb, 1, (byte)Gl.FALSE, (float*)&m);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(TextRig._uTexRgb, 0);
        Gl.BindVertexArray(TextRig._pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, TextRig._pvbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (IntPtr)(drawn * sizeof(QuadVerts)), verts, Gl.DYNAMIC_DRAW);
        for (int i = 0; i < drawn; i++)
        {
            Gl.BindTexture(Gl.TEXTURE_2D, (uint)tids[i]);
            Gl.DrawArrays(Gl.TRIANGLES, i * 6, 6);
        }
    }

    // ---- input: click a plate -> /tell -------------------------------------------------------

    /// <summary>Set while a press landed on a plate name, so the matching release is consumed too and the engine
    /// never sees a button-up with no button-down.</summary>
    private static bool _plateGrab;

    /// <summary>A plate name as one line again, for the chat entry: the name carries the LFs
    /// <c>PlateLayout.BreakName</c> put there, and an embedded newline in a <c>/tell</c> prefill is a bug.
    /// Cold (a click), so the copy costs nothing that matters; a break-free name is returned as-is.</summary>
    private static string Flatten(string name) => name.IndexOf('\n') < 0 ? name : name.Replace('\n', ' ');

    /// <summary>Click a plate's NAME to start a tell to it, through the chat's own Tell entry path
    /// (<see cref="ChatService.StartTell"/>). This window publishes NO hit-test override, so the pointer over a
    /// plate is not framework UI: the cursor stays the engine's and every click outside the narrow name box falls
    /// through to the world. Opt-in, because even the name box is world the player would otherwise have clicked.</summary>
    private static bool OnPlateClick(bool down, int button, int x, int y)
    {
        if (button != 1) return false;
        if (!down) { bool had = _plateGrab; _plateGrab = false; return had; }
        if (!NameplateKnobs.ClickToTell.V) return false;
        int cur = _hitCur;
        Hit[] h = _hits[cur];
        int n = _hitCount[cur];
        for (int i = 0; i < n && i < h.Length; i++)
            if (x >= h[i].X && x < h[i].X + h[i].W && y >= h[i].Y && y < h[i].Y + h[i].H)
            { ChatService.StartTell(Flatten(h[i].Name)); _plateGrab = true; return true; }
        return false;
    }

    /// <summary>The bare <c>/nameplate</c> line: the rows, then the table's live counters.</summary>
    private static string Status()
        => $"{NameplateKnobs.G.Report()} | tracked={_table.VisibleCount} overflow={_table.Overflow} feedbackword={NameplateNative.FeedbackWord()}";
}
