using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Chat;

/// <summary>The pointer side of the chatbox: press / release / motion / wheel routed from the framework Window, window
/// hit-testing and hover, the gear and portrait clicks, and window create / snap geometry.</summary>
public sealed unsafe partial class ChatService
{
    private static volatile int _lastMx, _lastMy;   // latest pointer pos, for wheel hit-testing
    private static bool _mdiag;                      // one-shot: log first click coords + rect
    private static int _hoverIndex = -1;             // window under the pointer (-1 = none), for grip visibility
    private static bool _anyTabDrag;                 // true while any window has an active tab drag

    /// <summary>A press in the edit text is ours until its release (<see cref="_editDrag"/>: the caret follows the
    /// pointer until then); the click counter tells a double / triple press by time and place.</summary>
    private static bool _editPress, _editDrag;
    private static int _clicks, _clickX, _clickY;
    private static long _clickAt;

    /// <summary>A second press within this long, and this near, is a double click (SDL's own default window).</summary>
    private const int DoubleClickMs = 500, ClickSlopPx = 4;

    /// <summary>Collects the screen rects of the other live extender windows for window-to-window snapping.
    /// Called once at drag-start on the input thread; only one window drags at a time (one mouse), so a single
    /// snapshot is sufficient. <paramref name="excludeChat"/> drops the dragged chat window by identity; the dragged
    /// dialog or quickbar passes false for its own flag.</summary>
    public static (float X, float Y, float W, float H)[] GatherSnapRects(ChatWindow? excludeChat, bool includeDialog, bool includeQuickbar = true)
    {
        var list = new System.Collections.Generic.List<(float, float, float, float)>(6);
        ChatWindow[] wins = _layout?.Windows ?? System.Array.Empty<ChatWindow>();
        foreach (ChatWindow w in wins)
            if (!ReferenceEquals(w, excludeChat)) list.Add((w.X, w.Y, w.W, w.H));
        if (includeQuickbar && Quickbar.QuickbarService.SnapRectOut(out var q)) list.Add(q);
        if (Inventory.InventoryService.SnapRectOut(out var inv)) list.Add(inv);
        if (includeDialog && Dialog.DialogService.SnapRectOut(out var d)) list.Add(d);
        return list.ToArray();
    }

    /// <summary>The framework Window mouse-button delegate: pointer press/release, true when it lands on any chat
    /// window so the WM shell consumes it instead of the 3D world. The focused window is checked first (it owns
    /// the popped-out strip/dropdown outside its frame rect), then the rest in reverse draw order. A press that
    /// moves typing to another tab or window swaps the unsent lines (<c>chat.drafts</c>).</summary>
    public static bool OnMouseButton(bool down, int button, int x, int y)
    {
        bool consumed = MouseButton(down, button, x, y);
        if (down) SyncDraftTab();
        return consumed;
    }

    private static bool MouseButton(bool down, int button, int x, int y)
    {
        if (_layout == null || !GameGate.InGame || button != 1) return false;
        ChatWindow? fwin = _focused;
        if (fwin == null) return false;
        if (down && !_mdiag)
        {
            _mdiag = true;
            _log?.Info($"[chat] first click ({x},{y}) windows={_layout.Count}");
        }
        if (down)
        {
            // Settings window is modal and owned by the focused window.
            if (_settings.Open)
            {
                bool cs = _settings.MouseDown(x, y, fwin);
                if (_settings.CopyWindowRequested)
                {
                    _settings.CopyWindowRequested = false;
                    _settings.Reset();
                    CreateWindow(copyFrom: fwin);
                }
                if (_settings.CloseWindowRequested)
                {
                    int idx = _layout.FocusedIndex;
                    _settings.Reset();
                    if (IsTyping) CloseChat(false);
                    if (_layout.CloseWindow(idx)) { SaveConfig(); _log?.Info($"[chat] window {idx} closed (settings)"); }
                    Rebuild(); return true;
                }
                if (_settings.CopyRequested >= 0)
                {
                    int src = _settings.CopyRequested;
                    _settings.CopyRequested = -1;
                    ChatWindow[] wins = _layout.Windows;
                    if ((uint)src < (uint)wins.Length)
                    {
                        fwin.CopySettingsFrom(wins[src]);
                        _settings.Layout(fwin);
                    }
                }
                // The shared knob and a window font pick apply here, on the input thread (the face bakes cold).
                if (_settings.TellOpenPicked >= 0) { ChatKnobs.TellOpen.Set(_settings.TellOpenPicked); _settings.TellOpenPicked = -1; }
                if (_settings.FontChanged) { _settings.FontChanged = false; EnsureFace(fwin); }
                // TellsHere exclusivity — clear all other windows when one turns it on.
                if (_settings.TellsHereChanged)
                {
                    _settings.TellsHereChanged = false;
                    ChatWindow[] wins = _layout.Windows;
                    for (int i = 0; i < wins.Length; i++)
                        if (wins[i] != fwin) wins[i].TellsHere = false;
                }
                DirtyAll(); SyncFieldKeyboard();
                if (IsTyping && !fwin.HasSendable) CloseChat(false);
                Rebuild(); return cs;
            }
            if (EditPress(fwin, x, y)) return true;
            // Check focused first (owns the strip/dropdown that extends outside the frame).
            if (DownOnWindow(_layout.FocusedIndex, x, y)) return true;
            // Then check other windows in reverse draw order (last drawn = topmost).
            for (int i = _layout.Count - 1; i >= 0; i--)
            {
                if (i == _layout.FocusedIndex) continue;
                if (_layout.Windows[i].Contains(x, y))
                {
                    _layout.FocusedIndex = i;
                    if (DownOnWindow(i, x, y)) return true;
                    Rebuild();
                    return true;
                }
            }
            return false;
        }
        // Up: an edit-text press, then settings, then tab-drag cross-window drop, then generic grabs.
        if (_editPress) { _editPress = _editDrag = false; return true; }
        if (_settings.MouseUp()) { SaveConfig(); Rebuild(); return true; }
        // Cross-window tab drop: resolve before MouseUp clears the grab state.
        for (int i = 0; i < _layout.Count; i++)
        {
            ChatWindow src = _layout.Windows[i];
            if (!src.TabDragActive) continue;
            // Scan other windows in reverse draw order for a drop target.
            for (int j = _layout.Count - 1; j >= 0; j--)
            {
                if (j == i) continue;
                int dp = _layout.Windows[j].TabBarDropIndex(x, y);
                if (dp < 0) continue;
                if (_layout.TransferTab(i, src.ActiveTab, j, dp))
                {
                    _layout.FocusedIndex = j;
                    _layout.Windows[j].SelectTab(dp);
                    if (IsTyping && !_layout.Windows[j].HasSendable) CloseChat(false);
                }
                break;
            }
            src.MouseUp(x, y);
            src.Dirty = true;
            SaveConfig(); Rebuild();
            return true;
        }
        for (int i = 0; i < _layout.Count; i++)
        {
            bool cu = _layout.Windows[i].MouseUp(x, y);
            if (cu) { SaveConfig(); Rebuild(); return true; }
        }
        return false;
    }

    /// <summary>A press in the typing window's edit text (<c>chat.mouse</c>): place the caret (Shift extends the
    /// selection) and follow the pointer until release; a double press selects the word, a triple the line.</summary>
    private static bool EditPress(ChatWindow win, int x, int y)
    {
        if (!IsTyping || !ChatKnobs.Mouse.V || !win.FooterVisible || win.ChannelListOpen
            || !InputStripLayout.InEditArea(win, x, y) || TextRig.Atlas is not GlyphAtlas atlas) return false;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool again = Math.Abs(x - _clickX) <= ClickSlopPx && Math.Abs(y - _clickY) <= ClickSlopPx
                     && System.Diagnostics.Stopwatch.GetElapsedTime(_clickAt, now).TotalMilliseconds <= DoubleClickMs;
        _clicks = again ? _clicks % 3 + 1 : 1;
        _clickAt = now; _clickX = x; _clickY = y;
        string text = Edit.Text;
        if (_clicks == 1)
        {
            bool shift = EngineFn.SdlGetModState != null && (EngineFn.SdlGetModState() & KmodShift) != 0;
            Edit.SetCaret(InputStripLayout.HitTest(win, atlas, text, x, y), shift);
            _editDrag = true;
        }
        else if (_clicks == 2) Edit.SelectWordAt(InputStripLayout.HitTest(win, atlas, text, x, y, charUnder: true));
        else Edit.SelectAll();
        _editPress = true;
        _completer.Reset();
        Rebuild();
        return true;
    }

    /// <summary>Press dispatch on a single window: the [⚙] then ChatWindow.MouseDown. Focuses and returns true if consumed.</summary>
    private static bool DownOnWindow(int index, int x, int y)
    {
        ChatWindow win = _layout!.Windows[index];
        if (win.OverGear(x, y))
        {
            _layout.FocusedIndex = index;
            win.ChannelListOpen = false;
            float scale = Metrics.GameUiScale < 1f ? 1f : Metrics.GameUiScale;
            _panelPx = MathF.Round(KnobPanel.FacePx * scale); _panelSmallPx = MathF.Round(KnobPanel.SmallPx * scale);
            GlyphAtlas? body = TextRig.Face(_panelPx) ?? TextRig.Atlas;   // cold: a miss bakes, on the input thread
            _settings.Panel.Title = _layout.Count > 1 ? $"Chat window {index + 1}" : "Chat window";
            _settings.OpenAt(win, TextRig.ViewportW, TextRig.ViewportH, _panelPx, body, TextRig.Face(_panelSmallPx) ?? body,
                             winIndex: index, winCount: _layout.Count);
            Rebuild(); return true;
        }
        // Portrait click: intercept before ChatWindow.MouseDown (which would consume it as a body no-op).
        if (TryPortraitClick(win, index, x, y)) return true;
        bool cd = win.MouseDown(x, y);
        if (cd)
        {
            _layout.FocusedIndex = index;
            if (IsTyping && !win.HasSendable) CloseChat(false);
            Rebuild();
        }
        return cd;
    }

    /// <summary>Portrait click → tell flow: resolve the portrait's PlayerId to a player name, then
    /// prefill <c>/t "Name" </c> into the chat input (same as vanilla's PrepChatPromptForTellToPlayer).
    /// Returns true if the click landed on a portrait and was consumed.</summary>
    private static bool TryPortraitClick(ChatWindow win, int winIndex, int x, int y)
    {
        PortraitHit[] ph = win.PortraitHits;
        for (int i = 0; i < ph.Length; i++)
        {
            var h = ph[i];
            if (x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H)
            {
                string? name = _playerResolver?.NameOf(h.PlayerId);
                if (name != null)
                {
                    _layout!.FocusedIndex = winIndex;
                    StartTellTo(name, h.PlayerId);
                }
                return true;
            }
        }
        return false;
    }

    /// <summary>Open a new window: <paramref name="copyFrom"/>'s look (the panel's "Copy this window") or the
    /// defaults (<c>/newwin</c>), one All tab, offset from Main, and focus it.</summary>
    private static void CreateWindow(ChatWindow? copyFrom = null)
    {
        if (_layout == null) return;
        ChatWindow main = _layout.Main;
        float offset = _layout.Count * 30f;
        var win = new ChatWindow(main.X + offset, main.Y + offset, main.W, main.H,
            new[] { new Tab("All", 0xffffffff) });
        if (copyFrom != null) { win.CopySettingsFrom(copyFrom); EnsureFace(win); }
        if (_layout.AddWindow(win) == null)
        {
            _log?.Info("[chat] new window: cap reached");
            return;
        }
        _layout.FocusedIndex = _layout.Count - 1;
        SaveConfig();
        _log?.Info($"[chat] window {_layout.Count - 1} created");
        Rebuild();
    }

    /// <summary>Pointer motion. Consumed (true) only while dragging/resizing; also tracks hover grips.
    /// Only rebuilds when a drag is active or the hover target changes (avoids a full re-wrap on every
    /// mouse-poll event).</summary>
    public static bool OnMouseMotion(int x, int y)
    {
        _lastMx = x; _lastMy = y;
        if (_layout == null || !GameGate.InGame) return false;
        if (_editDrag && IsTyping && TextRig.Atlas is GlyphAtlas ea)
        {
            Edit.SetCaret(InputStripLayout.HitTest(_layout.Focused, ea, Edit.Text, x, y), true);
            Rebuild();
            return true;
        }
        if (_settings.Open && _focused != null)
        {
            // The panel draws itself from live state every frame (no rebuild for a move or a hover); only a
            // slider drag changes the window's look, which the frames carry.
            if (_settings.MouseMove(x, y, _focused) && _settings.Panel.SliderDragging) { DirtyAll(); Rebuild(); }
            return true;
        }
        ChatWindow[] wins = _layout.Windows;
        bool consumed = false;
        for (int i = 0; i < wins.Length; i++)
        {
            if (wins[i].MouseMove(x, y)) consumed = true;
            if (wins[i].ScrollDragging) wins[i].LastScroll = System.DateTime.UtcNow;
        }
        // Topmost window under the pointer (reverse draw order = focused last = checked first).
        // During a tab drag, don't let the source's Dragging pin the hover -- the pointer may be over
        // a target window whose header must stay visible for the drop.
        int newHover = -1;
        int fi = _layout.FocusedIndex;
        bool tabDrag = false;
        for (int i = 0; i < wins.Length; i++) if (wins[i].TabDragActive) { tabDrag = true; break; }
        _anyTabDrag = tabDrag;
        bool fiDrag = (uint)fi < (uint)wins.Length && wins[fi].Dragging && !wins[fi].TabDragActive;
        if ((uint)fi < (uint)wins.Length && (wins[fi].Contains(x, y) || fiDrag)) newHover = fi;
        else for (int i = wins.Length - 1; i >= 0; i--)
            if (wins[i].Contains(x, y) || (wins[i].Dragging && !wins[i].TabDragActive)) { newHover = i; break; }
        // Portrait hover: hit-test the topmost window's portrait rects for a highlight.
        int oldPortraitHover = -1;
        int newPortraitHover = -1;
        if (newHover >= 0 && (uint)newHover < (uint)wins.Length)
        {
            oldPortraitHover = wins[newHover].HoveredPortrait;
            PortraitHit[] ph = wins[newHover].PortraitHits;
            for (int i = 0; i < ph.Length; i++)
            {
                var h = ph[i];
                if (x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H) { newPortraitHover = i; break; }
            }
            wins[newHover].HoveredPortrait = newPortraitHover;
        }
        // Clear hover on the window we left (if different).
        if (_hoverIndex >= 0 && _hoverIndex != newHover && (uint)_hoverIndex < (uint)wins.Length)
            wins[_hoverIndex].HoveredPortrait = -1;
        bool hoverChanged = newHover != _hoverIndex || newPortraitHover != oldPortraitHover;
        System.DateTime hnow = System.DateTime.UtcNow;
        for (int i = 0; i < wins.Length; i++)
            if (wins[i].SetHoverTab(i == newHover ? x : float.NaN, y, hnow)) { wins[i].Dirty = true; hoverChanged = true; }
        if (consumed || hoverChanged)
        {
            // Mark the windows that actually changed dirty.
            if (consumed) wins[fi].Dirty = true;   // the dragging window IS focused (press focuses before grab)
            if (hoverChanged)
            {
                if (newHover >= 0 && (uint)newHover < (uint)wins.Length) wins[newHover].Dirty = true;
                if (_hoverIndex >= 0 && (uint)_hoverIndex < (uint)wins.Length) wins[_hoverIndex].Dirty = true;
            }
            if (tabDrag) DirtyAll();   // tab drag needs all headers for drop targets
            _hoverIndex = newHover;
            Rebuild();
        }
        return consumed;
    }

    /// <summary>Lines a wheel notch scrolls.</summary>
    private const float WheelLines = 3f;

    /// <summary>Wheel notch. Consumed (and scrolls) only when the pointer is over a chat window. Routes
    /// to the hovered window (not necessarily focused). Scrolling on the focused window eases the residual;
    /// on others it snaps.</summary>
    public static bool OnMouseWheel(int dy)
    {
        if (_layout == null || !GameGate.InGame) return false;
        if (_settings.Open) { _settings.MouseWheel(dy); return true; }   // the panel / its open list scrolls; it draws itself
        // Find the topmost window under the pointer (focused draws last = checked first).
        ChatWindow[] wins = _layout.Windows;
        ChatWindow? hit = null; int hitIdx = -1;
        int fi = _layout.FocusedIndex;
        if ((uint)fi < (uint)wins.Length && wins[fi].Contains(_lastMx, _lastMy)) { hit = wins[fi]; hitIdx = fi; }
        else for (int i = wins.Length - 1; i >= 0; i--)
            if (wins[i].Contains(_lastMx, _lastMy)) { hit = wins[i]; hitIdx = i; break; }
        if (hit == null) return false;
        GlyphAtlas? atlas = TextRig.Atlas;
        if (atlas == null) return true;
        float before = hit.Active.ScrollPx;
        hit.ScrollByPx(dy * WheelLines * WindowAtlas(hit, atlas).LineHeight);
        hit.Dirty = true;
        Rebuild();
        float applied = hit.Active.ScrollPx - before;
        hit.LastScroll = System.DateTime.UtcNow;
        if (applied != 0f && hitIdx == _layout.FocusedIndex) ChatAnim.AddResidual(-applied);
        return true;
    }

    /// <summary>True when (<paramref name="x"/>, <paramref name="y"/>) is over any chat UI element:
    /// window frames, input strips, open channel dropdowns, or the settings panel.</summary>
    public static bool MouseOverUi(int x, int y)
    {
        if (_layout == null || !GameGate.InGame) return false;
        if (_settings.Open && _settings.Contains(x, y)) return true;
        ChatWindow[] wins = _layout.Windows;
        for (int i = 0; i < wins.Length; i++)
            if (wins[i].HitTestFull(x, y)) return true;
        return false;
    }
}
