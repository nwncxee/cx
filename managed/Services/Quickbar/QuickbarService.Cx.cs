using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat;
using CX.Services.Radial;
using CX.Ui;
using static CX.Ui.PanelChrome;

namespace CX.Services.Quickbar;

/// <summary>The CX quickbar: a <see cref="Window"/> drawing the engine's 36 buttons in up to three rows, moved by its
/// grip, with its own key bindings (a chord consumer). What a button does stays the engine's (<see cref="QuickbarNative"/>).</summary>
public sealed unsafe partial class QuickbarService
{
    /// <summary>The CX bar's state, one lifetime: the session.</summary>
    private sealed class QuickbarBarState
    {
        public bool Armed, LayoutDirty = true, Moving, FacesMissing, Binding;
        public int Vw, Vh, AtlasEpoch = -1, Held;
        public float GrabDx, GrabDy, TextPx;
        public string Typeface = FontRegistry.DefaultName;
        public GlyphAtlas? Text;
        public Window? Window;
        /// <summary>The other CX windows' rects, taken when a move starts: what the bar snaps flush to.</summary>
        public (float X, float Y, float W, float H)[] SnapRects = Array.Empty<(float, float, float, float)>();
        public VertexBatch? Under, Over, TipBatch;
        /// <summary>CX's tooltip over the bar, the button it was fetched for (-1 none) and that button's content then.</summary>
        public readonly Tooltip Tip = new();
        public int TipButton = -1;
        /// <summary>The text fetched for <see cref="TipButton"/> ("" when that button has none).</summary>
        public string TipText = "";
        /// <summary>A left press on a filled slot (-1 none), where it landed, and whether it has moved far enough to be a drag.</summary>
        public int SlotFrom = -1;
        public float PressX, PressY;
        public bool SlotDrag, WasDown;
        public long TipSig;
        /// <summary>One stable key per engine button: <see cref="Tooltip"/> compares keys by reference.</summary>
        public readonly object[] TipKeys = NewKeys();
        public readonly QuickbarLayout Layout = new();
        public readonly QuickbarKeys Keys = new();
        public readonly Skin Skin = NewSkin();
    }

    private static QuickbarBarState _cx = new();

    private static object[] NewKeys()
    {
        var k = new object[QuickbarNative.Buttons];
        for (int i = 0; i < k.Length; i++) k[i] = new object();
        return k;
    }

    /// <summary>Design px of the corner labels and counts, × the bar's scale.</summary>
    private const float LabelPx = 11f;
    /// <summary>GUI px of one cell of an engine drag icon.</summary>
    private const float DragCellPx = 32f;
    /// <summary>The engine's own ghosting alpha (<c>CGuiQuickButton::SetGhosting</c>).</summary>
    private const float GhostAlpha = 0.4f;
    /// <summary>Screen px a press moves before it is a slot drag rather than a click.</summary>
    private const float DragSlopPx = 5f;
    /// <summary>SDL_BUTTON_LMASK.</summary>
    private const uint SdlLeftMask = 1;  // not-an-offset: SDL key/event code
    private const uint EquipLine = 0x5fd35f, ModeLine = 0xe8c060, GripDots = 0x8a93a6, HoverWash = 0xffffff26;  // not-an-offset: a colour
    /// <summary>SDL_Keymod bits: Ctrl and Shift either side, Alt the left one only (right Alt is AltGr).</summary>
    private const int KmodShift = 0x0003, KmodCtrl = 0x00C0, KmodLAlt = 0x0100;  // not-an-offset: SDL key/event code
    /// <summary>SDL keycodes the bind mode reads: Esc finishes, Backspace / Delete clear; the modifier keys alone bind nothing.</summary>
    private const int SdlkEscape = 27, SdlkBackspace = 8, SdlkDelete = 127, SdlkLCtrl = 0x400000E0, SdlkRGui = 0x400000E7;  // not-an-offset: SDL key/event code
    private const string BindHelp = "Hover a button and press a key to bind it  -  Backspace clears  -  Esc or a click elsewhere finishes";
    /// <summary>Skin part indices, in draw order.</summary>
    private const int KFrame = 0, KSlot = 1, KGrip = 2;

    /// <summary>The bar's skin: NUI's nine-sliced window behind it, the inventory's cell tile per button and NUI's scrollbar
    /// trough for the grip, as the CX inventory's game-art look; a server re-points a part with <c>quickbar.skin.&lt;part&gt;</c>.</summary>
    private static Skin NewSkin() => new("quickbar",
        new SkinPart("frame", "nui_windowh", 3, 3, 3, 3), new SkinPart("slot", "gui_inv_bkgnd2", 4, 4, 4, 4),
        new SkinPart("grip", "nui_windowv", 3, 3, 3, 3));

    /// <summary>The CX bar owns the quickbar: the master and <c>quickbar.replace</c> on, and its hooks armed. The stock bar
    /// moves off-screen and its keys stand down; the CX bar's keys work even while it is hidden.</summary>
    internal static bool CxActive => _cx.Armed && QuickbarKnobs.Cx;

    /// <summary>The CX bar is on screen: active, not hidden, in game.</summary>
    internal static bool CxShowing => CxActive && !QuickbarKnobs.Hidden.V && GameGate.InGame;

    internal static void Relayout() => _cx.LayoutDirty = true;

    /// <summary>One slot is being dragged onto another: the engine hit-test gives the bar nothing meanwhile.</summary>
    internal static bool SlotDragging => _cx.SlotDrag;

    /// <summary>SDL point (<paramref name="x"/>, <paramref name="y"/>) is on the bar.</summary>
    internal static bool OverBar(int x, int y) => _cx.Layout.Contains(x, y);

    /// <summary>Is <paramref name="w"/> the CX bar's window (a drop over it is the bar's, not "another CX window").</summary>
    internal static bool IsBar(Window w) => w == _cx.Window;

    /// <summary>Re-parse the three key rows through SDL's key-name table; a refused chord is said once per change.</summary>
    internal static void KeysChanged()
    {
        int bad = 0;
        for (int set = 0; set < QuickbarLayout.Sets; set++) bad += _cx.Keys.Parse(set, QuickbarKnobs.Keys[set].V, KeyOf);
        if (bad > 0) Notice.Show(_log, $"quickbar: {bad} key(s) in the key rows are not SDL key names and stay unbound");
    }

    private static int KeyOf(string name)
    {
        if (EngineFn.SdlGetKeyFromName == null) return 0;
        // A key event's sym is the LOWER-case letter, while SDL names a one-character key in upper case.
        if (name.Length == 1) name = name.ToLowerInvariant();
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = utf8) return EngineFn.SdlGetKeyFromName(p);
    }

    private static void InitCx(ExtenderLog log, HookService hooks)
    {
        if (!QuickbarNative.Attach(log, hooks)) return;
        RadialIcons.Init(log);
        KeysChanged();
        UiHost.AddChordConsumer(new Chords());
        _cx.Window = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            CapturesPointer = true,
            DrawContent = DrawBar,
            DrawOver = DrawOverBar,
            OnMouseButton = BarButton,
            OnMouseMotion = BarMotion,
            OnKey = BindKey,
            HitTestOverride = (px, py) => CxShowing && _cx.Layout.Contains(px, py),
        };
        UiHost.SendToBack(_cx.Window);   // a HUD element: every other CX window, and what it drags, paints over it
        FrameRegistry.Global.Register(FramePhase.Start, 51, BuildModels, "Quickbar.models", log);
        _cx.Armed = true;
        log.Info($"QuickbarService: CX quickbar armed ({(QuickbarKnobs.Cx ? "drawing the quickbar" : "off -- quickbar.replace")})");
    }

    // ---- the engine hit-test's view ----

    /// <summary>The engine button under SDL point (<paramref name="x"/>, <paramref name="y"/>) when the CX bar is the topmost
    /// thing there, else -1: a NUI window, another CX window over the bar or an engine modal keeps the point.</summary>
    internal static int SlotAt(int x, int y)
    {
        QuickbarLayout l = _cx.Layout;
        if (_cx.Binding || !l.Contains(x, y) || UiInputService.NuiCovers(x, y) || QuickbarNative.EngineModal()) return -1;
        if (l.GripAt(x, y) && !QuickbarNative.EngineDragging()) return -1;   // the grip's press is the bar's own, a drop on it a button's
        if (UiHost.HitTest(x, y) != _cx.Window) return -1;
        return l.SlotAt(x, y, _cx.Held);
    }

    private static bool CxRect(out (float X, float Y, float W, float H) r)
    {
        QuickbarLayout l = _cx.Layout;
        r = (l.X, l.Y, l.W, l.H);
        return CxShowing && l.W > 0f;
    }

    // ---- input ----

    /// <summary>The grip's left press starts a move: the hit-test leaves the grip to the engine's own answer (nothing), so
    /// the press reaches here. In bind mode every press on the bar is eaten and one anywhere else finishes it.</summary>
    private static bool BarButton(bool down, int button, int x, int y)
    {
        QuickbarLayout l = _cx.Layout;
        if (_cx.Binding)
        {
            if (!down) return l.Contains(x, y);
            if (l.Contains(x, y) && UiHost.HitTest(x, y) == _cx.Window) return true;
            StopBinding();
            return false;
        }
        if (!down)
        {
            if (!_cx.Moving) return false;
            _cx.Moving = false;
            QuickbarKnobs.X.Set(l.X); QuickbarKnobs.Y.Set(l.Y);
            if (!QuickbarKnobs.Placed.V) QuickbarKnobs.Placed.Set(true);
            return true;
        }
        if (!CxShowing || button != 1 || !l.GripAt(x, y) || UiHost.HitTest(x, y) != _cx.Window) return false;
        _cx.Moving = true; _cx.GrabDx = x - l.X; _cx.GrabDy = y - l.Y;
        _cx.SnapRects = Chat.Core.ChatWindow.EdgeThresh > 0 ? ChatService.GatherSnapRects(null, true, includeQuickbar: false)
                                                           : Array.Empty<(float, float, float, float)>();
        return true;
    }

    private static bool BarMotion(int x, int y)
    {
        if (!_cx.Moving) return false;
        QuickbarLayout l = _cx.Layout;
        float nx = x - _cx.GrabDx, ny = y - _cx.GrabDy;
        Chat.Core.ChatWindow.SnapRect(ref nx, ref ny, l.W, l.H);
        if (Chat.Core.ChatWindow.EdgeThresh > 0) Chat.Core.ChatWindow.SnapToRects(ref nx, ref ny, l.W, l.H, _cx.SnapRects);
        l.X = Window.ClampAxis(nx, l.W, _cx.Vw);
        l.Y = Window.ClampAxis(ny, l.H, _cx.Vh);
        return true;
    }

    // ---- bind mode ----

    /// <summary>The settings row's press: close the panel and bind on the bar, every set shown. Cold.</summary>
    internal static void StartBinding()
    {
        if (!CxShowing || _cx.Window == null) { Notice.Show(_log, "quickbar: turn on \"Use the CX quickbar\" and be in game to bind its keys"); return; }
        KnobPanelService.Close();
        _cx.Binding = true; _cx.LayoutDirty = true;
        UiHost.RequestKeyboard(_cx.Window, true);
    }

    private static void StopBinding()
    {
        if (!_cx.Binding) return;
        _cx.Binding = false; _cx.LayoutDirty = true;
        if (_cx.Window != null) UiHost.RequestKeyboard(_cx.Window, false);
    }

    /// <summary>The bind mode's keys, as the keyboard owner: bind the hovered button to the chord, write its set's row.</summary>
    private static bool BindKey(uint key, int sdlMods)
    {
        if (!_cx.Binding) return false;
        if (key == SdlkEscape) { StopBinding(); return true; }
        if (key >= SdlkLCtrl && key <= SdlkRGui) return true;
        int mx = -1, my = -1;
        if (EngineFn.SdlGetMouseState != null) EngineFn.SdlGetMouseState(&mx, &my);
        int b = _cx.Layout.SlotAt(mx, my, 0);
        if (b < 0) return true;
        if (key == SdlkBackspace || key == SdlkDelete) _cx.Keys.Clear(b);
        else
        {
            string name = KeyName((int)key);
            if (name.Length == 0) return true;
            _cx.Keys.Bind(b, (int)key, ModsOf(sdlMods), name);
        }
        // Every row first: a row's write re-parses all three from their knobs, so the later rows must already hold the edit.
        string[] rows = new string[QuickbarLayout.Sets];
        for (int set = 0; set < rows.Length; set++) rows[set] = _cx.Keys.Row(set);
        for (int set = 0; set < rows.Length; set++) QuickbarKnobs.Keys[set].Set(rows[set]);
        return true;
    }

    private static int ModsOf(int sdlMods) => ((sdlMods & KmodCtrl) != 0 ? QuickbarKeys.Ctrl : 0) | ((sdlMods & KmodShift) != 0 ? QuickbarKeys.Shift : 0)
                                            | ((sdlMods & KmodLAlt) != 0 ? QuickbarKeys.Alt : 0);

    private static string KeyName(int key)
    {
        if (EngineFn.SdlGetKeyName == null) return "";
        byte* p = EngineFn.SdlGetKeyName(key);
        return p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";
    }

    /// <summary>The settings row's press: every set back to the game's F1..F12. Cold.</summary>
    internal static void ResetKeys()
    {
        for (int set = 0; set < QuickbarLayout.Sets; set++) QuickbarKnobs.Keys[set].Set(QuickbarKnobs.DefaultKeys[set]);
    }

    /// <summary>The bar's keys as a tier-2 chord consumer: the framework has already withheld a typing, modal or unfocused
    /// key and an auto-repeat; this matches the exact chord and presses the engine button.</summary>
    private sealed class Chords : IChordConsumer
    {
        public int Priority => 1;
        public bool WantsKey(int key) => CxActive && GameGate.InGame && _cx.Keys.Wants(key);

        public void Fire(uint key, int sdlMods)
        {
            if (!CxActive || !GameGate.InGame) return;
            int b = _cx.Keys.Match((int)key, ModsOf(sdlMods));
            if (b >= 0) QuickbarNative.Press(b);
        }

        public void FireMouse(int button, int sdlMods) { }
    }

    // ---- the frame ----

    /// <summary>Before the window roll-call: every item button's PLT icon gets its engine model, as the inventory's do.</summary>
    private static void BuildModels()
    {
        if (!CxShowing) return;
        IntPtr panel = QuickbarNative.Panel();
        for (int b = 0; b < QuickbarNative.Buttons; b++)
        {
            QuickSlot q = QuickbarNative.Read(panel, b);
            if (q.Type == QuickbarNative.TypeItem) RadialIcons.BuildModel(q.Item);
        }
    }

    /// <summary>The held modifier's set as the stock bar picks it (<c>UpdateQuickBarAppearance</c>): Shift, else Ctrl, else plain.</summary>
    private static int HeldSet()
    {
        int m = EngineFn.SdlGetModState != null ? EngineFn.SdlGetModState() : 0;
        return (m & KmodShift) != 0 ? 1 : (m & KmodCtrl) != 0 ? 2 : 0;
    }

    private static void DrawBar()
    {
        if (!CxShowing) { _cx.Moving = _cx.SlotDrag = false; _cx.SlotFrom = -1; _cx.Tip.Cut(); _cx.TipButton = -1; if (_cx.Binding) StopBinding(); return; }
        int vw = TextRig.ViewportW, vh = TextRig.ViewportH;
        if (vw != _cx.Vw || vh != _cx.Vh || _cx.LayoutDirty) LayoutBar(vw, vh);
        if (!Faces()) return;
        _cx.Held = HeldSet();

        QuickbarLayout l = _cx.Layout;
        GlyphAtlas text = _cx.Text!;
        Skin? skin = null;
        if (Theme.IsNative && _cx.Skin.Arm(_log, "")) { _cx.Skin.Begin(); skin = _cx.Skin; }
        VertexBatch under = _cx.Under ??= new VertexBatch(text.SolidU, text.SolidV), over = _cx.Over ??= new VertexBatch(text.SolidU, text.SolidV);
        under.Reset(text.SolidU, text.SolidV); over.Reset(text.SolidU, text.SolidV);
        under.Outline = over.Outline = TextStyle.HaloOf(QuickbarKnobs.Text.Outline.V, _cx.TextPx);

        float s = l.S;
        if (skin != null) skin.Draw(KFrame, l.X, l.Y, l.X + l.W, l.Y + l.H);
        else { FillRgba(under, l.X, l.Y, l.X + l.W, l.Y + l.H, Theme.Rgba(Role.PanelBg, 0xe6)); OutlineRgba(under, l.X, l.Y, l.X + l.W, l.Y + l.H, Theme.Rgba(Role.Border)); }
        if (l.Grip > 0f)
        {
            float gx1 = l.X + (QuickbarLayout.PadPx + l.Grip) * s, gcx = (l.X + gx1) / 2f;
            skin?.Draw(KGrip, l.X + 2f * s, l.Y + 2f * s, gx1 - 1f * s, l.Y + l.H - 2f * s);
            for (float gy = l.Y + l.H / 2f - 6f * s; gy <= l.Y + l.H / 2f + 6f * s; gy += 4f * s) Fill(over, gcx - s, gy, gcx + s, gy + 2f * s, GripDots);
        }

        int mx = -1, my = -1;
        uint mouse = EngineFn.SdlGetMouseState != null ? EngineFn.SdlGetMouseState(&mx, &my) : 0;
        int hover = _cx.Binding ? l.SlotAt(mx, my, _cx.Held) : SlotAt(mx, my);
        TrackSlotDrag(mx, my, (mouse & SdlLeftMask) != 0, hover);
        uint labelCol = Tx2, countCol = Hi;
        IntPtr panel = QuickbarNative.Panel();
        float hx0 = 0f, hy0 = 0f, hy1 = 0f;
        for (int i = 0; i < l.Count; i++)
        {
            int b = l.ButtonOf(i, _cx.Held);
            l.SlotRect(i, out float x0, out float y0, out float x1, out float y1);
            if (skin != null) skin.Draw(KSlot, x0, y0, x1, y1);
            else { FillRgba(under, x0, y0, x1, y1, Theme.Rgba(Role.ListBg)); OutlineRgba(under, x0, y0, x1, y1, Theme.Rgba(Role.Frame)); }
            QuickSlot q = QuickbarNative.Read(panel, b);
            if (q.Type != 0)
            {
                if (q.Outline != QuickbarNative.OutlineNone)
                {
                    uint c = q.Outline == QuickbarNative.OutlineEquip ? EquipLine : ModeLine;
                    Outline(over, x0, y0, x1, y1, c); Outline(over, x0 + 1f, y0 + 1f, x1 - 1f, y1 - 1f, c);
                }
                if (q.Count.Length > 0)
                {
                    float w = UiText.Measure(text, q.Count);
                    UiText.Draw(over, text, q.Count, Math.Max(x0 + 2f * s, x1 - 3f * s - w), y1 - 3f * s - text.LineHeight + text.Ascent, countCol, x1);
                }
            }
            if (QuickbarKnobs.Labels.V || _cx.Binding)
            {
                string label = _cx.Keys.Label(b);
                if (label.Length > 0) UiText.Draw(over, text, label, x0 + 2f * s, y0 + 1f * s + text.Ascent, _cx.Binding ? Hi : labelCol, x1 - 1f * s);
            }
            if (b == hover) { FillRgba(over, x0, y0, x1, y1, HoverWash); hx0 = x0; hy0 = y0; hy1 = y1; }
            if (_cx.Binding && b == hover) { Outline(over, x0, y0, x1, y1, Accent2); Outline(over, x0 + 1f, y0 + 1f, x1 - 1f, y1 - 1f, Accent2); }
        }
        if (_cx.Binding) DrawBindBanner(over, text, l);
        HoverTip(hover, panel, hx0, hy0, hy1);

        skin?.Flush();
        TextRig.DrawBatch(under.AsSpan(), under.VertexCount);
        for (int i = 0; i < l.Count; i++)
        {
            QuickSlot q = QuickbarNative.Read(panel, l.ButtonOf(i, _cx.Held));
            if (q.Type == 0) continue;
            l.SlotRect(i, out float x0, out float y0, out float x1, out float y1);
            float alpha = q.Disabled || (_cx.SlotDrag && l.ButtonOf(i, _cx.Held) == _cx.SlotFrom) ? GhostAlpha : 1f, inset = 2f * s;
            if (q.Type == QuickbarNative.TypeItem) DrawItem(q.Item, x0 + inset, y0 + inset, x1 - inset, y1 - inset, alpha);
            else
            {
                TextRig.DrawIcon(q.Icon, x0 + inset, y0 + inset, x1 - x0 - 2f * inset, y1 - y0 - 2f * inset, alpha);
                TextRig.DrawIcon(q.Overlay, x0 + inset, y0 + inset, x1 - x0 - 2f * inset, y1 - y0 - 2f * inset, alpha);
            }
        }
        TextRig.DrawBatch(over.AsSpan(), over.VertexCount);
        DrawEngineDrag(mx, my);
    }

    /// <summary>Point the tooltip at the hovered button; its text is fetched from the engine only when the button or its
    /// content changes. Nothing shows while binding, moving the bar or dragging.</summary>
    private static void HoverTip(int hover, IntPtr panel, float x0, float y0, float y1)
    {
        QuickSlot q = hover < 0 ? default : QuickbarNative.Read(panel, hover);
        if (q.Type == 0 || _cx.Binding || _cx.Moving || _cx.SlotDrag || QuickbarNative.EngineDragging()) { _cx.TipButton = -1; _cx.Tip.Leave(); return; }
        long sig = ((long)q.Type << 32) | q.Item;
        if (hover != _cx.TipButton || sig != _cx.TipSig)
        {
            _cx.TipButton = hover; _cx.TipSig = sig;
            _cx.TipText = QuickbarNative.TipText(hover);
        }
        if (_cx.TipText.Length == 0) _cx.Tip.Leave();
        else _cx.Tip.Hover(_cx.TipKeys[hover], _cx.TipText, x0, y0, y1);
    }

    /// <summary>A left press on a filled slot that moves <see cref="DragSlopPx"/> becomes a slot drag; its release on another
    /// slot swaps the two (the engine's setters, then one update each to the server). Polled per frame from SDL's own state.</summary>
    private static void TrackSlotDrag(int mx, int my, bool down, int hover)
    {
        if (!down)
        {
            if (_cx.SlotDrag)
            {
                int to = _cx.Layout.Contains(mx, my) && UiHost.HitTest(mx, my) == _cx.Window ? _cx.Layout.SlotAt(mx, my, _cx.Held) : -1;
                if (to >= 0 && to != _cx.SlotFrom && !QuickbarNative.Swap(_cx.SlotFrom, to, _log))
                    Notice.Show(_log, "quickbar: command-line and DM buttons cannot be moved by dragging");
                _cx.TipButton = -1;
            }
            _cx.SlotDrag = _cx.WasDown = false; _cx.SlotFrom = -1;
            return;
        }
        if (!_cx.WasDown)
        {
            _cx.WasDown = true;
            if (hover >= 0 && !_cx.Binding && !QuickbarKnobs.LockSlots.V && QuickbarNative.SwapReady && QuickbarNative.TypeOf(hover) != 0)
            { _cx.SlotFrom = hover; _cx.PressX = mx; _cx.PressY = my; }
            return;
        }
        if (_cx.SlotFrom >= 0 && !_cx.SlotDrag && Math.Abs(mx - _cx.PressX) + Math.Abs(my - _cx.PressY) > DragSlopPx) { _cx.SlotDrag = true; _cx.Tip.Cut(); }
    }

    /// <summary>Over every CX window: the dragged slot's icon at the pointer, and the tooltip. The bar sits under them all; these must not.</summary>
    private static void DrawOverBar()
    {
        if (!CxShowing) return;
        if (_cx.SlotDrag) DrawSlotDrag();
        GlyphAtlas? text = _cx.Text;
        if (text == null || _cx.Tip.Alpha <= 0f) return;
        VertexBatch b = _cx.TipBatch ??= new VertexBatch(text.SolidU, text.SolidV);
        b.Reset(text.SolidU, text.SolidV);
        uint bg = Theme.Rgba(Role.PanelBg, 0xe6);
        _cx.Tip.Draw(b, text, text.Ascent, (((bg >> 24) & 0xFF) / 255f, ((bg >> 16) & 0xFF) / 255f, ((bg >> 8) & 0xFF) / 255f, 1f),
                     Hi, _cx.Vw, _cx.Vh);
        TextRig.DrawBatch(b.AsSpan(), b.VertexCount);
    }

    private static void DrawSlotDrag()
    {
        int mx = -1, my = -1;
        if (EngineFn.SdlGetMouseState != null) EngineFn.SdlGetMouseState(&mx, &my);
        QuickSlot q = QuickbarNative.Read(QuickbarNative.Panel(), _cx.SlotFrom);
        float half = QuickbarLayout.CellPx * _cx.Layout.S / 2f;
        if (q.Type == QuickbarNative.TypeItem) DrawItem(q.Item, mx - half, my - half, mx + half, my + half, 1f);
        else
        {
            TextRig.DrawIcon(q.Icon, mx - half, my - half, 2f * half, 2f * half, 1f);
            TextRig.DrawIcon(q.Overlay, mx - half, my - half, 2f * half, 2f * half, 1f);
        }
    }

    /// <summary>The engine's dragged icon again, over the bar: the engine draws it in its GUI scene, under every CX window.
    /// The same rect: 32 GUI px a cell, centred on the pointer (the <c>gui_icon_NxNl</c> quads about the mouse model).</summary>
    private static void DrawEngineDrag(int mx, int my)
    {
        if (!QuickbarNative.EngineDrag(out IntPtr icon) || icon == IntPtr.Zero) return;
        var gi = new CGuiIcon(icon);
        int cw = Math.Max(1, gi.Cols), ch = Math.Max(1, gi.Rows);
        float w = DragCellPx * cw * Metrics.GameUiScale, h = DragCellPx * ch * Metrics.GameUiScale;
        QuickbarLayout l = _cx.Layout;
        if (mx + w / 2f < l.X || mx - w / 2f > l.X + l.W || my + h / 2f < l.Y || my - h / 2f > l.Y + l.H) return;
        RadialIcons.DrawGuiIcon(icon, mx - w / 2f, my - h / 2f, w, h, 1f, cw, ch);
    }


    private static void FillRgba(VertexBatch b, float x0, float y0, float x1, float y1, uint rgba) => Fill(b, x0, y0, x1, y1, rgba >> 8, (rgba & 0xFF) / 255f);
    private static void OutlineRgba(VertexBatch b, float x0, float y0, float x1, float y1, uint rgba) => Outline(b, x0, y0, x1, y1, rgba >> 8, (rgba & 0xFF) / 255f);

    /// <summary>The bind mode's one-line help, above the bar (below it when the bar sits at the top).</summary>
    private static void DrawBindBanner(VertexBatch b, GlyphAtlas text, QuickbarLayout l)
    {
        float w = UiText.Measure(text, BindHelp), pad = 4f * l.S, h = text.LineHeight + 2f * pad;
        float x0 = Math.Clamp(l.X + (l.W - w) / 2f - pad, 0f, Math.Max(0f, _cx.Vw - w - 2f * pad));
        float y0 = l.Y - h - 2f * l.S >= 0f ? l.Y - h - 2f * l.S : l.Y + l.H + 2f * l.S;
        Fill(b, x0, y0, x0 + w + 2f * pad, y0 + h, PanelBg, 0.95f);
        Outline(b, x0, y0, x0 + w + 2f * pad, y0 + h, Accent2);
        UiText.Draw(b, text, BindHelp, x0 + pad, y0 + pad + text.Ascent, Hi, x0 + w + pad + 1f);
    }

    /// <summary>An item's icon fitted into the cell, cropped to its inventory cells as the inventory draws it.</summary>
    private static void DrawItem(uint oid, float x0, float y0, float x1, float y1, float alpha)
    {
        QuickbarNative.ItemCells(oid, out int cw, out int ch);
        float side = Math.Min(x1 - x0, y1 - y0), w = side * cw / Math.Max(cw, ch), h = side * ch / Math.Max(cw, ch);
        RadialIcons.Draw((IntPtr)oid, (x0 + x1 - w) / 2f, (y0 + y1 - h) / 2f, w, h, alpha, cw, ch);
    }

    /// <summary>The bar's face at its scale, re-fetched when the atlas re-baked; false (and one notice) when not baked.</summary>
    private static bool Faces()
    {
        if (_cx.Text != null && _cx.AtlasEpoch == TextRig.AtlasEpoch) return true;
        _cx.AtlasEpoch = TextRig.AtlasEpoch;
        _cx.Text = TextRig.BakedFace(_cx.Typeface, _cx.TextPx);
        if (_cx.Text != null) return true;
        if (!_cx.FacesMissing) { _cx.FacesMissing = true; Notice.Show(_log, $"quickbar: the {_cx.TextPx} px face is not baked -- the CX quickbar cannot draw"); }
        return false;
    }

    /// <summary>Size the bar, place it (bottom centre until first moved, clamped on screen) and bake its face. Cold.</summary>
    private static void LayoutBar(int vw, int vh)
    {
        _cx.Vw = vw; _cx.Vh = vh; _cx.LayoutDirty = false;
        QuickbarLayout l = _cx.Layout;
        l.Layout(Metrics.Scale * QuickbarKnobs.Size.V, _cx.Binding ? QuickbarLayout.Sets : QuickbarKnobs.Rows.V,
                 QuickbarKnobs.Arrange.V == QuickbarKnobs.SideBySide, grip: !QuickbarKnobs.Locked.V, QuickbarKnobs.GapX.V, QuickbarKnobs.GapY.V);
        if (!QuickbarKnobs.Placed.V) { l.X = (vw - l.W) / 2f; l.Y = vh - l.H; }
        else { l.X = QuickbarKnobs.X.V; l.Y = QuickbarKnobs.Y.V; }
        l.X = Window.ClampAxis(l.X, l.W, vw); l.Y = Window.ClampAxis(l.Y, l.H, vh);
        float px = MathF.Round(Math.Clamp(LabelPx * l.S, TextRig.MinFontPx, TextRig.AtlasMaxPx));
        string face = QuickbarKnobs.Text.Typeface.V;
        if (px != _cx.TextPx || face != _cx.Typeface)
        {
            _cx.TextPx = px; _cx.Typeface = face;
            TextRig.Face(face, px);
            _cx.AtlasEpoch = -1;
        }
        ChatService.RequestRebuild();
    }
}
