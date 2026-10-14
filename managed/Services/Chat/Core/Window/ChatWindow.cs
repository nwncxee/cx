using System.Collections.Generic;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Chat.Core;

/// <summary>
/// A movable/resizable chat window: rectangle, tabs, and the pointer drag/resize state machine (all
/// pure -- pixels in, rect out, no native refs, so it self-checks off-device). v1 runs exactly one;
/// every draw/input path takes a <see cref="ChatWindow"/> so multi-window later is a list + a loop.
/// Coordinates are screen pixels, y-down (matching the render ortho and SDL mouse coords).
/// </summary>
public sealed partial class ChatWindow
{
    public float X, Y, W, H;

    /// <summary>Grid snap: pitch in px (0 = off), and the anchor center for each axis. Set by
    /// <see cref="Quickbar.QuickbarService.Tick"/> each frame from the live config + viewport.</summary>
    public static float SnapPx, SnapCX, SnapCY;
    private static float SnapX(float v) => SnapCX + (float)System.Math.Round((v - SnapCX) / SnapPx) * SnapPx;
    private static float SnapY(float v) => SnapCY + (float)System.Math.Round((v - SnapCY) / SnapPx) * SnapPx;

    /// <summary>Edge snap: magnet threshold in px (0 = off) and viewport bounds. Set by
    /// <see cref="Quickbar.QuickbarService.Tick"/> each frame.</summary>
    public static float EdgeThresh, EdgeVpW, EdgeVpH;
    internal const float EdgeMagnet = 12f;

    /// <summary>Applies the live grid + edge snap to an externally-owned window rect (top-left x/y, size w/h).
    /// Lets other extender windows (e.g. the dialog window) obey the same global snap settings.</summary>
    public static void SnapRect(ref float x, ref float y, float w, float h)
    {
        if (SnapPx > 0) { x = SnapX(x); y = SnapY(y); }
        if (EdgeThresh > 0)
        {
            if (System.Math.Abs(x) < EdgeThresh) x = 0;
            else if (System.Math.Abs(x + w - EdgeVpW) < EdgeThresh) x = EdgeVpW - w;
            if (System.Math.Abs(y) < EdgeThresh) y = 0;
            else if (System.Math.Abs(y + h - EdgeVpH) < EdgeThresh) y = EdgeVpH - h;
        }
    }

    /// <summary>Window-to-window edge snap: pulls the dragged rect (x/y mutated) flush against nearby window
    /// rects, on whichever axis has the closer edge. Runs AFTER grid/screen snap so a flush neighbour wins over
    /// the lattice. Gated by the caller on the edge-snap setting; uses the same <see cref="EdgeMagnet"/>.</summary>
    public static void SnapToRects(ref float x, ref float y, float w, float h, (float X, float Y, float W, float H)[] rects)
    {
        float m = EdgeMagnet;
        float bestDx = m, snapX = x;
        float bestDy = m, snapY = y;
        foreach (var o in rects)
        {
            // Only snap on an axis when the perpendicular ranges overlap (or nearly touch) -- i.e. we're actually
            // beside the other window, not diagonally offset. The +m slack lets flush-stacked edges also align.
            if (y < o.Y + o.H + m && o.Y < y + h + m)
            {
                Consider(x, o.X - w, ref bestDx, ref snapX);          // our right edge to their left (flush before)
                Consider(x, o.X + o.W, ref bestDx, ref snapX);        // our left edge to their right (flush after)
                Consider(x, o.X, ref bestDx, ref snapX);              // left edges aligned
                Consider(x, o.X + o.W - w, ref bestDx, ref snapX);    // right edges aligned
            }
            if (x < o.X + o.W + m && o.X < x + w + m)
            {
                Consider(y, o.Y - h, ref bestDy, ref snapY);          // our bottom edge to their top (flush above)
                Consider(y, o.Y + o.H, ref bestDy, ref snapY);        // our top edge to their bottom (flush below)
                Consider(y, o.Y, ref bestDy, ref snapY);              // top edges aligned
                Consider(y, o.Y + o.H - h, ref bestDy, ref snapY);    // bottom edges aligned
            }
        }
        x = snapX; y = snapY;
    }

    /// <summary>Keeps <paramref name="cand"/> if it is the nearest within-magnet candidate seen so far.</summary>
    private static void Consider(float p, float cand, ref float best, ref float snap)
    {
        float d = System.Math.Abs(p - cand);
        if (d < best) { best = d; snap = cand; }
    }

    /// <summary>This window's tabs, swapped atomically so the ingest thread (which reads <see cref="Active"/>
    /// and iterates <see cref="Tabs"/> during <c>Rebuild</c>) always sees a consistent snapshot even while the
    /// input thread adds/removes tabs. The &gt;=1-tab invariant is enforced by the mutation methods.</summary>
    public IReadOnlyList<Tab> Tabs => _tabs;
    private volatile Tab[] _tabs;
    public volatile int ActiveTab;

    /// <summary>Current send channel (the dropdown selection), the active tab's: every write also lands
    /// on <see cref="Tab.SendChannel"/>, so a tab keeps its channel while another is active and
    /// <see cref="SelectTab"/> gives it back. Dropdown and slash commands both write this; the shell resets every
    /// tab to <see cref="Channel.Talk"/> on relog.</summary>
    public Channel SendChannel
    {
        get => _send;
        set { _send = value; Active.SendChannel = value; }
    }
    private Channel _send = Channel.Talk;

    /// <summary>True while the channel dropdown list is expanded (over the body). Set by hit-testing.</summary>
    public bool ChannelListOpen;

    /// <summary>Render options the settings window edits, as (toggle bit, label). Data-driven: a new option is
    /// one entry -- the window grows to fit. <see cref="ChatToggle.MessageBody"/> is deliberately excluded -- a
    /// checkbox that blanks all chat is a trap.</summary>
    public static readonly (uint Bit, string Label)[] Options =
        { (ChatToggle.Timestamp, "Timestamps"), (ChatToggle.Portrait, "Portraits") };

    // ---- per-window settings: each window can look different (unobtrusive combat log vs prominent chat) ----

    /// <summary>Per-window animation master toggle. When false, lines never fade and slides snap.</summary>
    public bool AnimEnabled = true;
    /// <summary>Per-window hold/fade/ease durations.</summary>
    public float HoldSec = 60f, FadeSec = 6f, TauMs = 70f;
    /// <summary>Per-window fade-clock reset: scrolling this window makes its faded messages reappear.</summary>
    public System.DateTime LastScroll = System.DateTime.MinValue;
    /// <summary>Per-window text outline level (dark halo behind body glyphs for readability): a
    /// <c>TextStyle.OutlineLevels</c> value, 0 off, 1 thin, 2 thick.</summary>
    public int Outline;
    /// <summary>Per-window text size in design px; 0 = follow <c>ui.font</c>.</summary>
    public int FontPx;
    /// <summary>Per-window typeface, a <c>FontRegistry</c> name; "" = follow <c>ui.typeface</c>.</summary>
    public string Typeface = "";
    /// <summary>Per-window timestamp presentation.</summary>
    public TsFormat TimeFormat = TsFormat.HourMin;
    public TsSource TimeSource = TsSource.Local;
    /// <summary>Per-window message body text alignment.</summary>
    public TextAlign Align = TextAlign.Left;
    /// <summary>Per-window background fill shape.</summary>
    public BgMode BgMode = BgMode.Solid;
    /// <summary>Gradient direction in degrees: 0 runs left to right, 90 top to bottom (Radial ignores it).</summary>
    public float BgAngle = 90f;
    /// <summary>Gradient stops in [0,1]: Background holds until <see cref="BgStart"/>, Background 2 from
    /// <see cref="BgEnd"/>; End at or before Start is a hard edge.</summary>
    public float BgStart = 0f, BgEnd = 1f;
    /// <summary>Hide the tab bar when the pointer is not over the window.</summary>
    public bool AutoHideHeader = true;
    /// <summary>Hide the frame (outline + shadow) the same way the header hides.</summary>
    public bool AutoHideFrame;
    /// <summary>Tab under the pointer (-1 none) and when it became so; the tab it left and when. Set by the
    /// shell's motion path, read by Build for the hover plates the draw eases in/out.</summary>
    public int HoveredTab = -1, LeftTab = -1;
    public System.DateTime HoverSince, LeftSince;
    /// <summary>When true, the window cannot be moved, resized, or have its tabs dragged.</summary>
    public bool Locked;
    /// <summary>When true, incoming Tell auto-opens route here instead of the focused window.</summary>
    public bool TellsHere;

    /// <summary>Pass-1 wrap cache: block heights + content height, keyed on (width, content, settings).
    /// Volatile swap so concurrent builds see a consistent snapshot.</summary>
    internal volatile LayoutCache? WrapCache;

    /// <summary>Per-window dirty flag. When false, <see cref="ChatService.Rebuild"/>
    /// reuses the last-built frame for this window. Cleared before building, so a concurrent ingest-set survives.
    /// The focused window is always built regardless.</summary>
    public volatile bool Dirty = true;

    /// <summary>Clickable portrait rects, written by Build each rebuild. Input-thread hit-testing reads these
    /// to route portrait clicks to the tell flow. Empty when portraits are off or suppressed.</summary>
    // ponytail: stale line PlayerId goes dead after relog; rects die with store.Clear at session boundaries
    public volatile PortraitHit[] PortraitHits = System.Array.Empty<PortraitHit>();

    /// <summary>Index into <see cref="PortraitHits"/> the pointer is over, or -1. Set by the motion path;
    /// Build reads it to highlight the hovered frame.</summary>
    public int HoveredPortrait = -1;

    /// <summary>The shell sets this each rebuild = own-input && typing. The input strip + dropdown only
    /// exist (render + hit-test) while it's true.</summary>
    public bool FooterVisible;

    /// <summary>Put the input strip above the window instead of below it (settings window). Flips
    /// <see cref="StripTop"/> and the channel-list stack direction; the renderer + hit-tests follow.</summary>
    public bool InputOnTop;

    /// <summary>Wrapped input-line count (>=1). The renderer measures the word-wrap and sets this so the
    /// strip (which grows downward one <see cref="FooterH"/> per row) hit-tests at its true height.</summary>
    public int FooterRows = 1;
    /// <summary>Full height of the pop-out input strip, grown by word-wrap.</summary>
    public float FooterHeight => FooterH * (FooterRows < 1 ? 1 : FooterRows);

    /// <summary>Render-component toggle bitset (see the render pipeline). Default: timestamp + body + portrait.</summary>
    public uint Toggles = ChatToggle.Timestamp | ChatToggle.MessageBody | ChatToggle.Portrait;

    /// <summary>The six sendable channels, in dropdown order.</summary>
    public static readonly Channel[] SendChannels =
        { Channel.Talk, Channel.Shout, Channel.Whisper, Channel.Tell, Channel.Party, Channel.DM };

    /// <summary>The GUIINGAME_MESSAGE_* flag for a sendable channel (the same bits <see cref="FilterChannel"/>
    /// uses, so the tab mask test is a direct AND).</summary>
    public static uint Bit(Channel ch) => ch switch
    {
        Channel.Talk    => 0x001,
        Channel.Shout   => 0x004,
        Channel.Whisper => 0x008,
        Channel.Tell    => 0x002,
        Channel.Party   => 0x040,
        Channel.DM      => 0x400,
        _ => 0,
    };

    /// <summary>Can the active tab display (and therefore send on) <paramref name="ch"/>?</summary>
    public bool CanSend(Channel ch) => (Active.ChannelMask & Bit(ch)) != 0;

    /// <summary>Can <paramref name="ch"/> appear in the channel dropdown? Tell is hidden when the tab has
    /// other sendable channels — tells are sent via portrait click, /t command, or dedicated tabs.</summary>
    public bool CanSelect(Channel ch)
    {
        if (!CanSend(ch)) return false;
        if (ch != Channel.Tell) return true;
        foreach (Channel o in SendChannels)
            if (o != Channel.Tell && CanSend(o)) return false;
        return true;
    }

    /// <summary>True when the active tab shows at least one sendable channel (else no input line).</summary>
    public bool HasSendable
    {
        get { foreach (Channel ch in SendChannels) if (CanSend(ch)) return true; return false; }
    }

    /// <summary>If <see cref="SendChannel"/> is not visible in the active tab, move it to the first visible
    /// selectable channel (or leave it — no footer will show anyway). Tell-only pinned tabs auto-set Tell.</summary>
    public void ClampSendChannel()
    {
        if (Active.PinnedName.Length > 0 && CanSelect(Channel.Tell)) { SendChannel = Channel.Tell; return; }
        if (CanSelect(SendChannel)) return;
        foreach (Channel ch in SendChannels) if (CanSelect(ch)) { SendChannel = ch; return; }
    }

    /// <summary>Make tab <paramref name="i"/> active and give it back the channel last used on it, clamped to
    /// what it can send. A tab not yet typed in this session starts on <see cref="Channel.Talk"/>, clamped, rather
    /// than inheriting whatever the tab before it was on -- a DM-only tab must not leave the next tab on DM.</summary>
    public void SelectTab(int i)
    {
        if ((uint)i >= (uint)_tabs.Length) return;
        ActiveTab = i;
        RestoreChannel();
    }

    /// <summary>The active tab's remembered channel back on the window (<see cref="Channel.Talk"/> when it has none),
    /// clamped.</summary>
    private void RestoreChannel()
    {
        SendChannel = Active.SendChannel ?? Channel.Talk;
        ClampSendChannel();
    }

    // Layout metrics: design pixels at Metrics.Scale==1, multiplied by the live scale so the chrome tracks the
    // font size + game UI scale in lockstep (no clipping at larger sizes). ponytail: no per-metric tuning; one
    // uniform scale keeps every text-to-chrome ratio fixed, which is all the clipping fix needs.
    public static float TabBarH => 28f * Metrics.Scale;
    /// <summary>Maximum tab width (when few tabs). Tabs shrink to share the bar evenly.</summary>
    public static float MaxTabW => 120f * Metrics.Scale;
    /// <summary>Whether this window can be closed (non-Main). Set by the layout owner.</summary>
    public bool Closeable;
    /// <summary>Width reserved at the tab bar's right edge for buttons: just [⚙]; "Copy this window" and
    /// "Close this window" are rows in the settings panel.</summary>
    public float ButtonsW => GearW;
    /// <summary>Actual tab width for this window: bar minus button area, divided by tab count, capped at max.
    /// Takes the snapshotted tab count to avoid a torn read when a concurrent thread swaps _tabs.</summary>
    public float TabWFor(int tabCount) => System.Math.Min(MaxTabW, (W - ButtonsW) / (tabCount < 1 ? 1 : tabCount));
    /// <summary>Convenience: TabW from the live tab count (safe when only one thread is reading).</summary>
    public float TabW => TabWFor(_tabs.Length);
    public static float FooterH => 28f * Metrics.Scale;
    public static float DropdownW => 92f * Metrics.Scale;   // channel selector at the strip's left edge
    public static float Pad => 6f * Metrics.Scale;
    public static float ResizeGrip => 14f * Metrics.Scale;
    /// <summary>Gear icon hit box: a square at the tab bar's right edge (opens the settings window).</summary>
    public static float GearW => TabBarH;
    public static float MinW => 200f * Metrics.Scale;
    public static float MinH => 120f * Metrics.Scale;
    /// <summary>Scrollbar track width (px): the engine's own bar under the native look, else a thin gutter at
    /// the body's right edge. The text width reserves it either way, so the wider bar never overlaps a line.</summary>
    public static float ScrollbarW => Render.ChatSkin.Native ? NativeChrome.BarWidth(Metrics.Scale) : 8f * Metrics.Scale;
    /// <summary>Smallest thumb, so it stays grabbable when the history is very long.</summary>
    public static float SbMinThumb => 24f * Metrics.Scale;

    /// <summary>Total laid-out content height and the body (viewport) height, written by
    /// <see cref="WindowRenderer.Build"/> each rebuild so the scrollbar hit-test and thumb-drag map against
    /// the exact layout the draw used. 0 until the first build.</summary>
    public float ContentH, ViewH;
    /// <summary>How far the content can scroll up before the oldest line reaches the top (0 when it all fits).</summary>
    public float MaxScroll => ContentH > ViewH ? ContentH - ViewH : 0f;

    /// <summary>Scrollbar track height: the body minus the bottom <see cref="ResizeGrip"/>, so the thumb
    /// never enters the resize-grip corner (they used to overlap and steal each other's clicks).</summary>
    private float SbTrackH => System.Math.Max(0f, ViewH - ResizeGrip);

    /// <summary>Thumb height (px), proportional to the (grip-reserved) track and floored at
    /// <see cref="SbMinThumb"/>. Only meaningful when the content overflows the body.</summary>
    private float ThumbHeight()
    {
        float h = SbTrackH * ViewH / ContentH;
        return h < SbMinThumb ? SbMinThumb : h;
    }

    /// <summary>The scrollbar's rect: the body's right gutter, stopping one <see cref="ResizeGrip"/> short of
    /// the body bottom. The one geometry the draw, the thumb and the hit-test share.</summary>
    public void ScrollbarRect(out float x0, out float y0, out float x1, out float y1)
    {
        x0 = X + W - ScrollbarW; x1 = X + W;
        y0 = Y + TabBarH; y1 = y0 + SbTrackH;
    }

    /// <summary>Thumb rect (<paramref name="top"/>, <paramref name="height"/>) in screen px, or false when
    /// nothing scrolls (content fits). Sits at the track bottom (newest, just above the resize grip) at
    /// ScrollPx 0 and rises to the top as ScrollPx approaches <see cref="MaxScroll"/>; under the native look
    /// it is the engine's own thumb, between the bar's two arrows.</summary>
    public bool ThumbRect(out float top, out float height)
    {
        top = 0f; height = 0f;
        if (ContentH <= ViewH || ViewH <= 0f) return false;
        if (Render.ChatSkin.Native)
        {
            ScrollbarRect(out float x0, out float y0, out float x1, out float y1);
            if (!NativeChrome.ThumbRect(x0, y0, x1, y1, MaxScroll - Active.ScrollPx, ViewH, ContentH,
                                        out _, out float nty0, out _, out float nty1)) return false;
            top = nty0; height = nty1 - nty0;
            return true;
        }
        height = ThumbHeight();
        float frac = MaxScroll > 0f ? Active.ScrollPx / MaxScroll : 0f;   // 0 = newest/bottom, 1 = oldest/top
        top = Y + TabBarH + (1f - frac) * (SbTrackH - height);
        return true;
    }

    /// <summary>How far a thumb of <paramref name="height"/> travels between newest and oldest: the drag map's
    /// denominator. Under the native look the arrows and the thumb padding eat into the track.</summary>
    private float SbTravel(float height)
    {
        if (!Render.ChatSkin.Native) return SbTrackH - height;
        ScrollbarRect(out float x0, out float y0, out float x1, out float y1);
        return y1 - y0 - 2f * (x1 - x0) - 2f * NativeChrome.PadY - height;
    }

    /// <summary>True when the pointer is over the scrollbar thumb (the grab zone).</summary>
    public bool OverScrollThumb(float mx, float my)
    {
        if (!ThumbRect(out float top, out float h)) return false;
        return mx >= X + W - ScrollbarW && mx <= X + W && my >= top && my <= top + h;
    }

    /// <summary>Top y of the input strip. Below the frame by default; above it (top grows up as the
    /// input wraps) when <see cref="InputOnTop"/>. Only live while <see cref="FooterVisible"/>.</summary>
    public float StripTop => InputOnTop ? Y - FooterHeight : Y + H;
    /// <summary>The dropdown button (closed state) on the strip's top row (where the renderer draws it).</summary>
    public bool OverDropdownButton(float mx, float my)
        => mx >= X && mx < X + DropdownW && my >= StripTop && my < StripTop + FooterH;
    /// <summary>Top y of channel-list item <paramref name="i"/>. The list stacks over the body (away from the
    /// world): up from the strip's top when the strip is below the frame, down from the frame top when above.
    /// ponytail: in above-mode with a multi-row (wrapped) input the list anchors to the frame, not the button
    /// row; the button sits at the strip top. Fine for the common single-row input; revisit if multi-row lands.</summary>
    public float ChannelItemTop(int i) => InputOnTop ? Y + FooterH * i : StripTop - FooterH * (i + 1);
    /// <summary>Channel-list item under the pointer while open: returns the <see cref="SendChannels"/>
    /// index of the hit channel, or -1. Only selectable rows are hit-testable (Tell hidden when other
    /// channels exist); the row counter runs over visible entries so the geometry matches the renderer.</summary>
    public int ChannelItemAt(float mx, float my)
    {
        if (mx < X || mx >= X + DropdownW) return -1;
        int row = 0;
        for (int i = 0; i < SendChannels.Length; i++)
        {
            if (!CanSelect(SendChannels[i])) continue;
            float top = ChannelItemTop(row);
            if (my >= top && my < top + FooterH) return i;
            row++;
        }
        return -1;
    }

    private enum Grab { None, Move, Resize, Scroll, TabHold, TabDrag }
    private Grab _grab;
    /// <summary>Other windows' rects captured at move-drag start, for window-to-window snapping.</summary>
    private (float X, float Y, float W, float H)[] _snapRects = System.Array.Empty<(float, float, float, float)>();
    private bool _pressConsumed;        // did we consume the matching mouse-down? (button-pair ownership)
    private float _gx, _gy, _gw, _gh;   // grab anchor: pointer/rect at mouse-down
    private float _gsp;                  // scroll-grab anchor: ScrollPx at the thumb grab
    private int _dragTabIdx;             // tab index being dragged
    /// <summary>Pixel distance the pointer must travel from the tab press before drag activates.</summary>
    private const float TabDragThreshold = 6f;

    /// <summary>True while a tab is being dragged (for cross-window drop detection).
    /// The hold phase (pre-threshold) is not a drag — no reorder, no cross-window drop.</summary>
    public bool TabDragActive => _grab == Grab.TabDrag;

    public ChatWindow(float x, float y, float w, float h, Tab[] tabs)
    {
        if (tabs.Length == 0) throw new System.ArgumentException("a window needs >=1 tab", nameof(tabs));
        X = x; Y = y; W = w; H = h; _tabs = (Tab[])tabs.Clone();
    }

    /// <summary>The active tab. Bounds-checks against the snapshot so a stale <see cref="ActiveTab"/>
    /// (visible between the two volatile writes on a mutation) falls back to tab 0 instead of throwing.</summary>
    public Tab Active { get { Tab[] t = _tabs; int i = ActiveTab; return t[(uint)i < (uint)t.Length ? i : 0]; } }

    /// <summary>Append a tab (atomic array swap).</summary>
    public void AddTab(Tab t)
    {
        Tab[] old = _tabs;
        var next = new Tab[old.Length + 1];
        old.CopyTo(next, 0);
        next[old.Length] = t;
        _tabs = next;
    }

    /// <summary>Replace the entire tab list (rejects empty, keeping the &gt;=1 invariant). Resets
    /// <see cref="ActiveTab"/> to 0, which takes over the window's current channel, clamped. Used by the
    /// persistence layer to restore saved tabs. ActiveTab assigned first so a concurrent reader on the old
    /// (possibly longer) array never sees the new (possibly shorter) array with a stale index.</summary>
    public void SetTabs(Tab[] tabs)
    {
        if (tabs.Length == 0) return;
        ActiveTab = 0;
        _tabs = (Tab[])tabs.Clone();
        SendChannel = _send;
        ClampSendChannel();
    }

    /// <summary>Remove tab <paramref name="index"/>, refusing the last one so a window always keeps &gt;=1 tab;
    /// returns false and changes nothing on the last tab or an out-of-range index. Keeps the same tab active --
    /// removing one before it decrements <see cref="ActiveTab"/>, and the tab that slides in gets its own channel
    /// back. ActiveTab is written first so a concurrent reader never sees an index past the new array's end.</summary>
    public bool RemoveTab(int index)
    {
        Tab[] old = _tabs;
        if (old.Length <= 1 || (uint)index >= (uint)old.Length) return false;
        int newActive = ActiveTab;
        if (index < newActive) newActive--;
        else if (newActive >= old.Length - 1) newActive = old.Length - 2;
        ActiveTab = newActive;
        var next = new Tab[old.Length - 1];
        System.Array.Copy(old, 0, next, 0, index);
        System.Array.Copy(old, index + 1, next, index, old.Length - index - 1);
        _tabs = next;
        RestoreChannel();
        return true;
    }

    /// <summary>Move a tab from one position to another within this window (atomic array swap).
    /// <see cref="ActiveTab"/> follows: if the active tab was moved, it tracks to <paramref name="to"/>;
    /// if another tab shifted past it, the index adjusts so the same tab stays active.</summary>
    public void MoveTab(int from, int to)
    {
        Tab[] old = _tabs;
        if ((uint)from >= (uint)old.Length || (uint)to >= (uint)old.Length || from == to) return;
        var next = (Tab[])old.Clone();
        Tab t = next[from];
        if (from < to) System.Array.Copy(old, from + 1, next, from, to - from);
        else System.Array.Copy(old, to, next, to + 1, from - to);
        next[to] = t;
        int a = ActiveTab;
        if (a == from) a = to;
        else if (from < to && a > from && a <= to) a--;
        else if (from > to && a >= to && a < from) a++;
        ActiveTab = a;
        _tabs = next;
    }

    /// <summary>Insert a tab at <paramref name="index"/> (atomic array swap). Clamps to [0, Count].</summary>
    public void InsertTab(Tab t, int index)
    {
        Tab[] old = _tabs;
        if (index < 0) index = 0;
        if (index > old.Length) index = old.Length;
        var next = new Tab[old.Length + 1];
        System.Array.Copy(old, 0, next, 0, index);
        next[index] = t;
        System.Array.Copy(old, index, next, index + 1, old.Length - index);
        _tabs = next;
    }

    /// <summary>Drop position in the tab bar for a cross-window tab drag. Returns the insert
    /// index (0..<see cref="Tabs"/>.Count) if the pointer is over the tab bar, or -1 if outside.</summary>
    public int TabBarDropIndex(float mx, float my)
    {
        if (my < Y || my > Y + TabBarH || mx < X || mx >= X + W - ButtonsW) return -1;
        int n = _tabs.Length;
        float tw = TabWFor(n);
        int i = (int)((mx - X) / tw);
        return i < 0 ? 0 : (i > n ? n : i);
    }

    /// <summary>True while dragging, resizing, or scrubbing the scrollbar -- the shell consumes
    /// pointer-motion only then.</summary>
    public bool Dragging => _grab != Grab.None;

    /// <summary>True while the scrollbar thumb is being dragged (the shell resets the fade clock as it moves).</summary>
    public bool ScrollDragging => _grab == Grab.Scroll;

    public bool Contains(float mx, float my) => mx >= X && mx <= X + W && my >= Y && my <= Y + H;

    /// <summary>Full UI hit-test: frame + input strip + open channel dropdown.</summary>
    public bool HitTestFull(float mx, float my)
    {
        if (Contains(mx, my)) return true;
        if (!FooterVisible) return false;
        if (mx >= X && mx < X + W && my >= StripTop && my < StripTop + FooterHeight) return true;
        if (ChannelListOpen)
        {
            int rows = 0;
            foreach (Channel ch in SendChannels) if (CanSelect(ch)) rows++;
            for (int i = 0; i < rows; i++)
            {
                float top = ChannelItemTop(i);
                if (mx >= X && mx < X + DropdownW && my >= top && my < top + FooterH) return true;
            }
        }
        return false;
    }

    /// <summary>True when the pointer is over the bottom-right resize grip (also the hover-handle zone).</summary>
    /// <summary>Grow the rect to the current scaled minimum. Called when Metrics.Scale changes so a rect saved
    /// at a smaller scale (or restored before the UI scale synced) can't leave the chrome overflowing its box.</summary>
    public void ClampToMin() { if (W < MinW) W = MinW; if (H < MinH) H = MinH; }

    public bool OverResizeGrip(float mx, float my)
        => mx >= X + W - ResizeGrip && mx <= X + W && my >= Y + H - ResizeGrip && my <= Y + H;

    /// <summary>True when the pointer is over the [⚙] button (rightmost slot of the tab bar).</summary>
    public bool OverGear(float mx, float my)
        => mx >= X + W - GearW && mx < X + W && my >= Y && my <= Y + TabBarH;

    /// <summary>Width of each ephemeral tab button (pin, close) at the right edge.</summary>
    public static float TabBtnW => 16f * Metrics.Scale;

    /// <summary>True when <paramref name="mx"/> is in the close-X zone of tab <paramref name="i"/>.</summary>
    public bool TabCloseHit(int i, float mx)
    {
        float tw = TabWFor(_tabs.Length);
        float right = X + (i + 1) * tw;
        return mx >= right - TabBtnW && mx < right;
    }

    /// <summary>True when <paramref name="mx"/> is in the pin zone of tab <paramref name="i"/> (left of close).</summary>
    public bool TabPinHit(int i, float mx)
    {
        float tw = TabWFor(_tabs.Length);
        float right = X + (i + 1) * tw;
        return mx >= right - 2f * TabBtnW && mx < right - TabBtnW;
    }

    /// <summary>Record the tab under the pointer for the hover plate; true when it changed (a rebuild is due).</summary>
    public bool SetHoverTab(float mx, float my, System.DateTime now)
    {
        int t = Contains(mx, my) && !OverGear(mx, my) ? TabAt(mx, my) : -1;
        if (t == HoveredTab) return false;
        if (HoveredTab >= 0) { LeftTab = HoveredTab; LeftSince = now; }
        HoveredTab = t; HoverSince = now;
        return true;
    }

    /// <summary>Tab index under the pointer in the tab bar, or -1 (bar background = the move handle).</summary>
    private int TabAt(float mx, float my)
    {
        if (my < Y || my > Y + TabBarH) return -1;
        int n = _tabs.Length;
        float tw = TabWFor(n);
        int i = (int)((mx - X) / tw);
        return (i >= 0 && i < n && mx >= X && mx < X + n * tw) ? i : -1;
    }

    /// <summary>
    /// Pointer press. Returns true if it lands on the window (the shell then consumes it, so the click
    /// doesn't fall through to the 3D world). Grabs move/resize, or switches tab.
    /// </summary>
    public bool MouseDown(float mx, float my)
    {
        // The gear icon (opens the settings window) is claimed by the shell before this runs -- see
        // ChatService.OnMouseButton. ChatWindow owns no options state now.

        // Pop-out strip + channel dropdown (below the frame, so outside Contains) -- only while typing.
        if (FooterVisible)
        {
            if (ChannelListOpen)
            {
                int item = ChannelItemAt(mx, my);
                if (item >= 0) SendChannel = SendChannels[item];   // pick a channel
                ChannelListOpen = false;                           // any click closes the list
                _pressConsumed = true;
                return true;
            }
            if (OverDropdownButton(mx, my)) { ChannelListOpen = true; _pressConsumed = true; return true; }
            if (mx >= X && mx < X + W && my >= StripTop && my < StripTop + FooterHeight)
            { _pressConsumed = true; return true; }                // click in the input strip: consume, no-op
        }

        if (!Contains(mx, my)) { _pressConsumed = false; return false; }
        _pressConsumed = true;
        // Scrollbar track stops one ResizeGrip above the bottom (see SbTrackH), so thumb and grip no longer
        // share the bottom-right corner -- both stay grabbable regardless of test order.
        if (OverScrollThumb(mx, my)) { _grab = Grab.Scroll; _gy = my; _gsp = Active.ScrollPx; return true; }
        if (OverResizeGrip(mx, my)) { if (!Locked) { _grab = Grab.Resize; _gx = mx; _gy = my; _gw = W; _gh = H; } return true; }
        int tab = TabAt(mx, my);
        if (tab >= 0)
        {
            if (_tabs[tab].Ephemeral)
            {
                // Close-X: right edge. Not on the last tab (window [×] is the dismiss).
                if (_tabs.Length > 1 && TabCloseHit(tab, mx))
                {
                    RemoveTab(tab);
                    return true;
                }
                // Pin: left of close. Keeps the tab (clears ephemeral).
                if (TabPinHit(tab, mx))
                {
                    _tabs[tab].Kept = true;
                    _pressConsumed = true;
                    return true;
                }
            }
            _tabs[tab].Unread = false; SelectTab(tab);
            if (!Locked) { _grab = Grab.TabHold; _dragTabIdx = tab; _gx = mx; _gy = my; }
            return true;
        }
        if (my <= Y + TabBarH)
        {
            if (!Locked)
            {
                _grab = Grab.Move; _gx = mx - X; _gy = my - Y;
                _snapRects = EdgeThresh > 0 ? Chat.ChatService.GatherSnapRects(this, true)
                                            : System.Array.Empty<(float, float, float, float)>();
            }
            return true;
        }
        return true;   // press inside the body/footer: consume so the world doesn't get it
    }

    /// <summary>Pointer motion. Consumed (true) only mid-drag, where it moves/resizes the window.</summary>
    public bool MouseMove(float mx, float my)
    {
        switch (_grab)
        {
            case Grab.Move:
                X = mx - _gx; Y = my - _gy;
                if (SnapPx > 0) { X = SnapX(X); Y = SnapY(Y); }
                if (EdgeThresh > 0)
                {
                    if (System.Math.Abs(X) < EdgeThresh) X = 0;
                    else if (System.Math.Abs(X + W - EdgeVpW) < EdgeThresh) X = EdgeVpW - W;
                    if (System.Math.Abs(Y) < EdgeThresh) Y = 0;
                    else if (System.Math.Abs(Y + H - EdgeVpH) < EdgeThresh) Y = EdgeVpH - H;
                    SnapToRects(ref X, ref Y, W, H, _snapRects);   // flush-neighbour snap wins over grid/screen
                }
                return true;
            case Grab.Resize:
                if (SnapPx > 0)
                {
                    W = System.Math.Max(MinW, SnapX(X + _gw + (mx - _gx)) - X);
                    H = System.Math.Max(MinH, SnapY(Y + _gh + (my - _gy)) - Y);
                }
                else
                {
                    W = System.Math.Max(MinW, _gw + (mx - _gx));
                    H = System.Math.Max(MinH, _gh + (my - _gy));
                }
                if (EdgeThresh > 0)
                {
                    if (System.Math.Abs(X + W - EdgeVpW) < EdgeThresh) W = EdgeVpW - X;
                    if (System.Math.Abs(Y + H - EdgeVpH) < EdgeThresh) H = EdgeVpH - Y;
                }
                return true;
            case Grab.TabHold:
            {
                float dx = mx - _gx, dy = my - _gy;
                if (dx * dx + dy * dy < TabDragThreshold * TabDragThreshold) return true;
                _grab = Grab.TabDrag;
                goto case Grab.TabDrag;
            }
            case Grab.TabDrag:
            {
                int n = _tabs.Length;
                float tw = TabWFor(n);
                int target = (int)((mx - X) / tw);
                if (target < 0) target = 0;
                if (target >= n) target = n - 1;
                if (target != _dragTabIdx) { MoveTab(_dragTabIdx, target); _dragTabIdx = target; }
                return true;
            }
            case Grab.Scroll:
            {
                // Absolute anchor: map the pointer's travel from the grab straight to ScrollPx (drag DOWN ->
                // toward newest -> ScrollPx down). Incremental deltas would accumulate error past the clamp.
                if (ThumbRect(out _, out float th))
                {
                    float travel = SbTravel(th);
                    if (travel > 0f)
                    {
                        float sp = _gsp - (my - _gy) * MaxScroll / travel;
                        Active.ScrollPx = sp < 0f ? 0f : (sp > MaxScroll ? MaxScroll : sp);
                    }
                }
                return true;
            }
            default: return false;
        }
    }

    /// <summary>Pointer release. Consumed iff we consumed the matching press -- otherwise the engine
    /// owns this button pair (e.g. a world hold-drag that happens to release over the box), and eating
    /// the up would leave it thinking the button is still held.</summary>
    public bool MouseUp(float mx, float my)
    {
        bool c = _grab != Grab.None || _pressConsumed;
        _grab = Grab.None;
        _pressConsumed = false;
        return c;
    }

    /// <summary>Scroll by a pixel delta: positive = up, into history. Clamps the low end (bottom) here; the
    /// renderer clamps the high end once it has measured the content height.</summary>
    public void ScrollByPx(float dpx)
    {
        float o = Active.ScrollPx + dpx;
        Active.ScrollPx = o < 0f ? 0f : o;
    }

    /// <summary>Body rectangle: everything between the tab bar and the frame's bottom. The input strip pops out
    /// <em>below</em> the frame, so the body keeps its full height whether or not it shows.
    /// When <paramref name="headerVisible"/> is false the body expands upward into the header band.</summary>
    public void BodyRect(out float bx, out float by, out float bw, out float bh, bool headerVisible = true)
    {
        float top = headerVisible ? TabBarH : 0f;
        bx = X; by = Y + top; bw = W; bh = H - top;
    }

    /// <summary>Copy all per-window settings (look, not identity) from <paramref name="src"/>. Tabs and rect
    /// are excluded: tabs are content (the user arranges them per window), rect is position.</summary>
    public void CopySettingsFrom(ChatWindow src)
    {
        Toggles = src.Toggles;
        InputOnTop = src.InputOnTop;
        AnimEnabled = src.AnimEnabled;
        HoldSec = src.HoldSec; FadeSec = src.FadeSec; TauMs = src.TauMs;
        Outline = src.Outline;
        FontPx = src.FontPx; Typeface = src.Typeface;
        TimeFormat = src.TimeFormat; TimeSource = src.TimeSource;
        Align = src.Align;
        BgMode = src.BgMode; BgAngle = src.BgAngle; BgStart = src.BgStart; BgEnd = src.BgEnd;
        AutoHideHeader = src.AutoHideHeader; AutoHideFrame = src.AutoHideFrame;
    }
}

