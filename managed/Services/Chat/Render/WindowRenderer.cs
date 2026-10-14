using System.Collections.Generic;
using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;
using static CX.Ui.PanelChrome;

using CX.Services.Chat.Core.Model;

namespace CX.Services.Chat.Render;

/// <summary>
/// Builds one <see cref="VertexBatch"/> for a <see cref="ChatWindow"/> each time something changes (ingest /
/// input / resize -- all cold threads), which <see cref="ChatRenderer.Publish(RenderFrame[])"/> then draws. The
/// message body is assembled through an ordered array of stateless line components gated by the window's toggle
/// bitset: a new visual feature = a new component + a new bit. Only rows that fit the body are walked/wrapped.
/// </summary>
public static partial class WindowRenderer
{
    /// <summary>Per-window resolved palette, threaded through Build → EmitInputStrip.</summary>
    private struct Pal
    {
        public (float r, float g, float b, float a) Bg, Bg2, TabBar, TabActive, Footer, SelBg, Frame;
        public uint TabTextRgb, FrameRgb;
    }

    /// <summary>Per-thread reusable scratch for the ring walk.</summary>
    [System.ThreadStatic] private static ChatLine[]? _ringScratch;
    private static ChatLine[] RingScratch() => _ringScratch ??= new ChatLine[ChatStore.Capacity];

    /// <summary>Per-thread pooled VertexBatch + rune/colour lists. The internal
    /// <c>List&lt;float&gt;</c> keeps its capacity across rebuilds, avoiding per-build heap churn.
    /// Portraits/spans/blockH escape into published frames and must NOT be pooled.</summary>
    [System.ThreadStatic] private static VertexBatch? _batchScratch;
    [System.ThreadStatic] private static List<Rune>? _runesScratch;
    [System.ThreadStatic] private static List<uint>? _colsScratch;

    /// <summary>Per-thread markup class scratch, one byte per UTF-16 char of the edit line. Sized to
    /// <see cref="EditBuffer.MaxLength"/> on first use, so a keystroke never reallocates it.</summary>
    [System.ThreadStatic] private static byte[]? _markupScratch;

    /// <summary>The input line's text: the brightest role, as the engine's own edit boxes draw.</summary>
    private static uint EditRgb => Hi;
    /// <summary>Unread ephemeral tab: label + pulsing underline.</summary>
    private static uint UnreadRgb => Warn;
    /// <summary>Soft drop shadow outside the frame: rings of 1 px, outermost last, alpha scaled by the background's.</summary>
    private static readonly float[] ShadowAlpha = { 0.18f, 0.10f, 0.04f };
    /// <summary>Alpha of the hovered tab's Accent plate, relative to the colour's own (the active tab sits at 0.35).</summary>
    private const float HoverWash = 0.18f;
    /// <summary>Inset of the channel chip / thumb / caret inside their bands (px at scale 1).</summary>
    private const float Inset = 3f;

    /// <summary>How opaque each chat surface draws: the window and its outline let the world through, the
    /// header band and input bar less so, the accent and selection plates least.</summary>
    internal const byte BgA = 0xbd, HeaderA = 0xd9, AccentA = 0xf2, InputA = 0xe6, SelA = 0xb3;  // not-an-offset: alphas

    /// <summary>The chat chrome by meaning, each role at the alpha its surface draws: the window background and
    /// the second colour a gradient runs to, the header band, the accent, the input bar, the selection, the
    /// outline, the label text.</summary>
    internal static uint Bg => Theme.Rgba(Role.PanelBg, BgA);
    internal static uint Bg2 => Theme.Rgba(Role.NavOn, BgA);
    internal static uint HeaderCol => Theme.Rgba(Role.HeaderBg, HeaderA);
    internal static uint AccentCol => Theme.Rgba(Role.Accent, AccentA);
    internal static uint InputCol => Theme.Rgba(Role.Input, InputA);
    internal static uint SelCol => Theme.Rgba(Role.Selected, SelA);
    internal static uint FrameCol => Theme.Rgba(Role.Border, BgA);
    internal static uint TextCol => Theme.Rgba(Role.Tx);

    /// <summary>A native-art tint: white at <paramref name="a"/>, so the game's art keeps the window's translucency.</summary>
    private static uint Tint(byte a) => 0xffffff00u | a;  // not-an-offset: an rgba tint

    /// <summary>Art tints: the part's own colours (opaque white), and the idle scrollbar thumb's
    /// header-label grey, as NUI draws it (white under the pointer).</summary>
    private const uint ArtWhite = 0xffffffffu, ThumbIdle = 0xc8c8c8ffu;  // not-an-offset: rgba tints

    private static (float r, float g, float b, float a) Unpack(uint rgba)
        => (((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);

    private static Pal ResolvePalette() => new()
    {
        Bg = Unpack(Bg),
        Bg2 = Unpack(Bg2),
        TabBar = Unpack(HeaderCol),
        TabActive = Unpack(AccentCol),
        Footer = Unpack(InputCol),
        SelBg = Unpack(SelCol),
        TabTextRgb = TextCol >> 8,
        Frame = Unpack(FrameCol), FrameRgb = FrameCol >> 8,
    };


    /// <summary>The render pipeline: components run in array order; the window's toggle bitset selects which.</summary>
    private static readonly ILineComponent[] Pipeline = { new TimestampComponent(), new MessageBodyComponent() };

    private static uint GripRgb => Grip;

    /// <summary>Portrait width:height. The engine's own portrait pipeline (ConvertPortrait) crops every
    /// portrait to this ratio (0.64 -&gt; taller than wide), so a gutter icon drawn at it is undistorted.</summary>
    private const float PortraitAspect = 0.64f;

    /// <summary>Portrait border, echoing vanilla's framed reply-portrait. A filled rect drawn behind the
    /// portrait (in the chrome batch) so its outer <see cref="PortraitFrameW"/> px show as a ring.</summary>
    private const float PortraitFrameW = 1f;
    private static (float r, float g, float b, float a) PortraitFrame => C(Grip);
    private static (float r, float g, float b, float a) PortraitFrameHover => C(Hi);

    /// <summary>Vertical gap between messages -- breathing room, and it keeps adjacent portrait frames from
    /// touching (which read as one continuous outline). ponytail: fixed; a density setting is the ceiling.</summary>
    public const float MsgGap = 8f;

    /// <summary>Breathing room below the newest message so its block (notably a portrait's 1px bottom frame)
    /// clears the body's bottom scissor edge instead of being clipped flush against it.</summary>
    public const float BottomPad = 4f;

    /// <summary>Assemble a chatbox frame. <paramref name="atlas"/> is the face the body text draws in (the
    /// window's own); <paramref name="chrome"/> the UI face the tab bar, buttons, input strip and channel list
    /// draw in (sized by <see cref="Metrics.Scale"/>, which follows the UI face) -- null = the body face.
    /// <paramref name="showFooter"/> draws the edit line; <paramref name="showGrip"/> the corner resize grip.</summary>
    public static RenderFrame Build(ChatWindow win, SettingsWindow settings, ChatStore store, GlyphAtlas atlas,
                                    bool showFooter, string editText, int caret, bool showGrip,
                                    (int start, int end)? selection = null, GlyphAtlas? chrome = null)
    {
        var p = ResolvePalette();
        List<SkinCmd>? sk = ChatSkin.Native ? new List<SkinCmd>() : null;
        void Art(int part, float x0, float y0, float x1, float y1, uint rgba = ArtWhite)
            => sk!.Add(new SkinCmd(part, x0, y0, x1, y1, rgba));
        float halo = TextStyle.HaloOf(win.Outline, TextRig.FacePx(win.FontPx));
        chrome ??= atlas;
        float chromeBase = (ChatWindow.TabBarH - chrome.LineHeight) * 0.5f + chrome.Ascent;   // baseline drop that centres a line in a band (TabBarH == FooterH)

        VertexBatch b = _batchScratch ??= new VertexBatch(0, 0);
        b.Reset(atlas.SolidU, atlas.SolidV);
        var portraits = new List<PortraitQuad>();
        var hits = new List<PortraitHit>();
        var spans = new List<LineSpanVerts>();
        float lh = atlas.LineHeight;
        float glyphBaseline = atlas.Ascent;
        float descentGap = lh - atlas.Ascent;

        var tabs = win.Tabs;
        int activeTab = win.ActiveTab;
        if ((uint)activeTab >= (uint)tabs.Count) activeTab = 0;
        Tab active = tabs[activeTab];

        // Panel + tab bar. The tab bar hides when auto-hide is on and the pointer is outside the window
        // (and the settings panel is closed), leaving just the background in the header band.
        // Unread ephemeral tabs force the header visible so the blink is seen.
        // Frame: a soft shadow outside (alpha scaled by the frame colour's), the 1 px outline on the edge.
        // Native: the game's nine-sliced window art is the background, the outline and the shadow at once, and
        // each tab is a NUI button -- so a hovered tab snaps to its hot art instead of easing a wash in.
        bool hasUnread = false;
        for (int i = 0; i < tabs.Count; i++) if (tabs[i].Unread && tabs[i].Ephemeral) { hasUnread = true; break; }
        bool inside = showGrip || settings.Open || hasUnread;
        bool showHeader = !win.AutoHideHeader || inside, showFrame = !win.AutoHideFrame || inside;
        float fa = p.Frame.a;
        if (sk != null)
        {
            if (showFrame) Art(NativeChrome.Frame, win.X, win.Y, win.X + win.W, win.Y + win.H, Tint(BgA));
        }
        else
        {
            if (showFrame)
                for (int k = ShadowAlpha.Length; k >= 1; k--)
                    Outline(b, win.X - k, win.Y - k, win.X + win.W + k, win.Y + win.H + k, 0x000000, ShadowAlpha[k - 1] * fa);
            EmitBackground(b, win, p.Bg, p.Bg2);
        }
        int blinkStart = 0, blinkEnd = 0, hoverStart = 0, hoverEnd = 0, leftStart = 0, leftEnd = 0;
        if (showHeader)
        {
            float barBot = win.Y + ChatWindow.TabBarH;
            if (sk != null) Art(NativeChrome.Header, win.X, win.Y, win.X + win.W, barBot, Tint(HeaderA));
            else
            {
                b.Rect(win.X, win.Y, win.X + win.W, barBot, p.TabBar.r, p.TabBar.g, p.TabBar.b, p.TabBar.a);
                Fill(b, win.X, barBot - 1f, win.X + win.W, barBot, p.FrameRgb, p.TabBar.a);
            }
            float tabW = win.TabWFor(tabs.Count);
            float ul = 2f * Metrics.Scale;   // the active / unread underline
            for (int i = 0; i < tabs.Count; i++)
            {
                float tx = win.X + i * tabW;
                bool on = i == activeTab, eph = tabs[i].Ephemeral;
                bool hot = i == win.HoveredTab;
                if (sk != null)
                {
                    Art(on ? hot ? NativeChrome.ButtonOnHot : NativeChrome.ButtonOn
                           : hot ? NativeChrome.ButtonHot : NativeChrome.Button,
                        tx, win.Y, tx + tabW, barBot, Tint(HeaderA));
                }
                else if (on)
                {
                    b.Rect(tx, win.Y, tx + tabW, barBot - 1f, p.TabActive.r, p.TabActive.g, p.TabActive.b, p.TabActive.a * 0.35f);
                    b.Rect(tx + 1f, barBot - ul, tx + tabW - 1f, barBot, p.TabActive.r, p.TabActive.g, p.TabActive.b, p.TabActive.a);
                }
                else if (hot || i == win.LeftTab)
                {
                    // Hover plate: a lighter Accent wash the draw eases in (entered) or out (left).
                    if (hot) hoverStart = b.VertexCount; else leftStart = b.VertexCount;
                    b.Rect(tx, win.Y, tx + tabW, barBot - 1f, p.TabActive.r, p.TabActive.g, p.TabActive.b, p.TabActive.a * HoverWash);
                    if (hot) hoverEnd = b.VertexCount; else leftEnd = b.VertexCount;
                }
                // Unread ephemeral tab: gold label + an underline whose alpha the draw pulses.
                bool unread = eph && tabs[i].Unread;
                if (unread)
                {
                    if (blinkStart == 0) blinkStart = b.VertexCount;
                    Fill(b, tx + 1f, barBot - ul, tx + tabW - 1f, barBot, UnreadRgb);
                    blinkEnd = b.VertexCount;
                }
                bool closeX = eph && tabs.Count > 1;
                float btns = (closeX ? ChatWindow.TabBtnW : 0f) + (eph ? ChatWindow.TabBtnW : 0f);
                float avail = tabW - 2f * ChatWindow.Pad - btns;
                float lw = UiText.Measure(chrome, tabs[i].Label);
                float lx = lw < avail ? tx + ChatWindow.Pad + (avail - lw) * 0.5f : tx + ChatWindow.Pad;   // centred when it fits
                UiText.Draw(b, chrome, tabs[i].Label, lx, win.Y + chromeBase, unread ? UnreadRgb : p.TabTextRgb,
                            tx + ChatWindow.Pad + avail, on || unread ? 1f : 0.62f);
                if (eph)
                {
                    float pinX = tx + tabW - btns;
                    UiText.Draw(b, chrome, "▪", pinX, win.Y + chromeBase, Tx2, pinX + ChatWindow.TabBtnW);
                    if (closeX)
                        UiText.Draw(b, chrome, "×", tx + tabW - ChatWindow.TabBtnW, win.Y + chromeBase, Tx2, tx + tabW);
                }
            }
        }

        if (showFrame && sk == null) Outline(b, win.X, win.Y, win.X + win.W, win.Y + win.H, p.FrameRgb, fa);   // over the bar, so the frame is unbroken

        // Body: one pixel-scrolled column. ScrollPx (px up from the content bottom) drives the layout, so
        // scrolling is smooth rather than message-quantised; partial messages at the top/bottom edges are
        // emitted and clipped by the body scissor. Two passes: pass 1 measures EVERY match to get the content
        // height -- needed to clamp the scroll and size the scrollbar BEFORE placing, so an over-scroll can't
        // leave a stale gap-at-top frame -- then pass 2 re-wraps only the blocks that intersect the viewport.
        win.BodyRect(out float bx, out float by, out float bw, out float bh, showHeader);
        // Portraits keep the engine's portrait aspect (taller than wide) in a left gutter, only on lines with a
        // resref. A portrait line is one BLOCK, its height padded up to the portrait so it never overlaps its
        // neighbours. The gutter is reserved on resref, before the texture resolves, so the icon appears a
        // frame later WITHOUT the text reflowing. Portraits draw in a second pass over the body (before the
        // tail chrome); the settings panel is tail-drawn so it naturally covers them.
        bool portraitsOn = (win.Toggles & ChatToggle.Portrait) != 0;
        float portraitW = lh;                       // gutter-width icon...
        float portraitH = lh / PortraitAspect;      // ...grown tall to the real portrait shape (~1.56 rows)
        float gutter = portraitW + ChatWindow.Pad;
        // Reserve the scrollbar column unconditionally (even when the content fits and no track draws): the
        // text width stays stable, so lines don't reflow the moment history grows past one screen.
        float maxTextW = bw - 2 * ChatWindow.Pad - ChatWindow.ScrollbarW;

        // Snapshot the store version BEFORE the ring walk: a concurrent Append between View and this read
        // would pair the new version with stale blockH, giving a persistent false hit. Reading first means
        // a mid-build Append stores the old version → next build misses → self-heals in one frame.
        int storeVer = store.Version;
        ChatLine[] all = RingScratch();
        string? pin = active.PinnedName.Length > 0 ? active.PinnedName : null;
        int nAll = store.View(active.ChannelMask, active.PlayerId, all, pin);
        List<Rune> runes = _runesScratch ??= new List<Rune>();
        List<uint> cols = _colsScratch ??= new List<uint>();

        // Pass 1: wrap every match to a block height; sum -> contentH (a gap between messages).
        // Cached per window: the wrap is stable as long as width, content, and text-affecting settings
        // haven't changed, so a move drag (only X/Y change) skips the entire 512-line pass.
        LayoutCache? lc = win.WrapCache;
        float[] blockH;
        float contentH;
        if (lc != null && lc.Hit(maxTextW, active.ChannelMask, active.PlayerId, active.PinnedName,
                win.Toggles, storeVer, (int)win.TimeFormat, (int)win.TimeSource, atlas, nAll))
        {
            blockH = lc.BlockH;
            contentH = lc.ContentH;
        }
        else
        {
            blockH = new float[nAll];
            contentH = 0f;
            for (int li = 0; li < nAll; li++)
            {
                bool hasP = BuildRunes(win, all[li], portraitsOn, runes, cols);
                float indent = hasP ? gutter : 0f;
                List<TextWrapper.LineSpan> wr = TextWrapper.WrapRunes(runes, maxTextW - indent, atlas.AdvanceOfCached);
                blockH[li] = hasP ? System.Math.Max(wr.Count * lh, descentGap + portraitH) : wr.Count * lh;
                contentH += blockH[li];
            }
            if (nAll > 1) contentH += (nAll - 1) * MsgGap;
            contentH += BottomPad;
            win.WrapCache = new LayoutCache(blockH, contentH, nAll, maxTextW, active.ChannelMask,
                active.PlayerId, active.PinnedName, win.Toggles, storeVer, (int)win.TimeFormat, (int)win.TimeSource, atlas);
        }

        // Clamp the scroll to the measured content, then hand the metrics to the window (scrollbar + drag map).
        float maxScroll = contentH - bh > 0f ? contentH - bh : 0f;
        if (active.ScrollPx > maxScroll) active.ScrollPx = maxScroll;
        if (active.ScrollPx < 0f) active.ScrollPx = 0f;
        win.ContentH = contentH; win.ViewH = bh;

        // Pass 2: place from the bottom (newest block's bottom = body bottom, pushed down by ScrollPx) and emit
        // blocks intersecting the viewport PLUS a margin above and below, so content sliding out under the
        // scroll/slide residual has geometry to slide on instead of popping. The margin equals the residual
        // cap, so a bounded residual can never expose an un-emitted block. The body scissor clips it all.
        float easeMargin = ChatAnim.MaxResidualPx;
        int bodyStart = b.VertexCount;   // the animated (fade/slide/scroll) slice starts here
        b.Outline = halo;
        float rowBottom = by + bh - BottomPad + active.ScrollPx;
        for (int li = 0; li < nAll; li++)
        {
            float top = rowBottom - blockH[li];
            if (top < by + bh + easeMargin && rowBottom > by - easeMargin)   // viewport + ease margin
            {
                int spanStart = b.VertexCount;   // this line's verts (rows + portrait frame) -> one fade unit
                bool hasPortrait = BuildRunes(win, all[li], portraitsOn, runes, cols);
                float indent = hasPortrait ? gutter : 0f;
                float availW = maxTextW - indent;
                bool gutterRight = hasPortrait && win.Align == TextAlign.Right;
                float rowX = bx + ChatWindow.Pad + (gutterRight ? 0f : indent);
                List<TextWrapper.LineSpan> wraps = TextWrapper.WrapRunes(runes, availW, atlas.AdvanceOfCached);
                for (int w = 0; w < wraps.Count; w++)
                {
                    var span = wraps[w];
                    RowAlign.Compute(win.Align, atlas, runes, span, availW, w == wraps.Count - 1,
                             out float xOff, out float spaceExtra);
                    EmitRow(b, atlas, runes, cols, span, rowX + xOff, top + w * lh + glyphBaseline, spaceExtra);
                }
                if (hasPortrait)
                {
                    float px = gutterRight
                        ? bx + ChatWindow.Pad + maxTextW - portraitW
                        : bx + ChatWindow.Pad;
                    float py = top + descentGap;
                    bool hovered = win.HoveredPortrait == hits.Count;
                    var fc = hovered ? PortraitFrameHover : PortraitFrame;
                    b.Rect(px - PortraitFrameW, py - PortraitFrameW, px + portraitW + PortraitFrameW, py + portraitH + PortraitFrameW,
                           fc.r, fc.g, fc.b, fc.a);
                    portraits.Add(new PortraitQuad(px, py, portraitW, portraitH, all[li].PortraitResref, all[li].Timestamp));
                    hits.Add(new PortraitHit(px - PortraitFrameW, py - PortraitFrameW,
                        portraitW + 2 * PortraitFrameW, portraitH + 2 * PortraitFrameW, all[li].PlayerId));
                }
                if (b.VertexCount > spanStart) spans.Add(new LineSpanVerts(spanStart, b.VertexCount, all[li].Timestamp));
            }
            rowBottom = top - MsgGap;              // gap before the next (older) message
            if (rowBottom <= by - easeMargin) break;   // past the top margin -> older blocks are all off it
        }
        b.Outline = 0f;
        int bodyEnd = b.VertexCount;   // ...and ends here; chrome (scrollbar/footer/grip/dropdown) follows, unanimated

        // Scrollbar (chrome: after the body slice, so unfaded and unscissored): a track + an Accent-coloured
        // thumb whose size shows how much history there is and whose position shows where in it the view sits.
        // Only when the content overflows AND the pointer is over the window (or mid-scrub) -- it's a hover
        // affordance, kept up during a drag so the thumb doesn't vanish under the grabbing pointer.
        if (contentH > bh && (showGrip || win.ScrollDragging))
        {
            if (sk != null)
            {
                // The native bar is the engine's: a trough, square arrows at both ends and the tiled thumb,
                // over ChatWindow.ScrollbarW -- wider than the flat gutter, which is why the text width
                // reserves it and the hit-test shares NativeChrome's geometry.
                win.ScrollbarRect(out float sx0, out float sy0, out float sx1, out float sy1);
                float sbw = sx1 - sx0;
                Art(NativeChrome.Trough, sx0, sy0, sx1, sy1);
                Art(NativeChrome.Up, sx0, sy0, sx1, sy0 + sbw);
                Art(NativeChrome.Down, sx0, sy1 - sbw, sx1, sy1);
                if (NativeChrome.ThumbRect(sx0, sy0, sx1, sy1, maxScroll - active.ScrollPx, bh, contentH,
                                           out float ntx0, out float nty0, out float ntx1, out float nty1))
                    Art(NativeChrome.Thumb, ntx0, nty0, ntx1, nty1, win.ScrollDragging ? ArtWhite : ThumbIdle);
            }
            else
            {
                float sbx = bx + bw - ChatWindow.ScrollbarW, ins = Inset * Metrics.Scale;
                // Track stops one grip above the body bottom to match the hit zone (ChatWindow.SbTrackH): keeps
                // the thumb clear of the resize-grip corner.
                Fill(b, sbx + ins, by + ins, bx + bw - ins, by + bh - ChatWindow.ResizeGrip, Track, 0.6f);
                if (win.ThumbRect(out float tt, out float th))
                    b.Rect(sbx + ins, tt, bx + bw - ins, tt + th, p.TabActive.r, p.TabActive.g, p.TabActive.b, 1f);
            }
        }

        // Pop-out input strip: below the frame, a channel dropdown at its left + a word-wrapped edit
        // line that grows the strip DOWNWARD one FooterH per wrapped row (so long input never clips).
        if (showFooter)
            EmitInputStrip(b, chrome, win, editText, caret, selection, p, sk);

        // Resize grip: three nested parallel diagonal lines in the bottom-right corner (the classic
        // "gripper"), shown while the pointer is over the window.
        if (showGrip && !win.Locked)
        {
            float gr = ((GripRgb >> 16) & 0xFF) / 255f, gg = ((GripRgb >> 8) & 0xFF) / 255f, gb = (GripRgb & 0xFF) / 255f;
            float cx = win.X + win.W - 3f, cy = win.Y + win.H - 3f;   // inset so the outer line clears the border
            GripLine(b, cx, cy, 6f, gr, gg, gb);
            GripLine(b, cx, cy, 11f, gr, gg, gb);
            GripLine(b, cx, cy, 16f, gr, gg, gb);
        }

        // Tab-bar [⚙] at the right edge, shown on hover (or settings open).
        if (showGrip || settings.Open)
        {
            float gearLeft = win.X + win.W - ChatWindow.GearW;
            float gx = gearLeft + (ChatWindow.GearW - UiText.Measure(chrome, "⚙")) * 0.5f;
            UiText.Draw(b, chrome, "⚙", gx, win.Y + chromeBase, settings.Open ? Hi : Tx2, win.X + win.W);
        }

        // Channel dropdown list (open): stacked over the body (away from the strip), so it draws LAST = on top.
        // The chosen channel wears the panel's nav look: a lit row with an accent bar at its left.
        int skinOver = sk?.Count ?? 0;   // the art from here on sits over the body, where the list covers it
        if (showFooter && win.ChannelListOpen)
        {
            float fh = ChatWindow.FooterH, lx0 = win.X, lx1 = win.X + ChatWindow.DropdownW;
            float minTop = float.MaxValue, maxBot = float.MinValue;
            int row = 0;
            for (int i = 0; i < ChatWindow.SendChannels.Length; i++)
            {
                Channel ch = ChatWindow.SendChannels[i];
                if (!win.CanSelect(ch)) continue;
                float top = win.ChannelItemTop(row);
                bool sel = ch == win.SendChannel;
                if (sk != null) Art(sel ? NativeChrome.ButtonOn : NativeChrome.Button, lx0, top, lx1, top + fh);
                else
                {
                    Fill(b, lx0, top, lx1, top + fh, sel ? NavOn : HeaderBg, 0.98f);
                    if (sel) b.Rect(lx0 + 1f, top, lx0 + 1f + 3f * Metrics.Scale, top + fh, p.TabActive.r, p.TabActive.g, p.TabActive.b, 1f);
                }
                UiText.Draw(b, chrome, ch.ToString(), lx0 + ChatWindow.Pad + 3f * Metrics.Scale, top + chromeBase,
                            sel ? Hi : Tx2, lx1 - 2f);
                if (top < minTop) minTop = top;
                if (top + fh > maxBot) maxBot = top + fh;
                row++;
            }
            // The list's frame art flushes ahead of the row buttons (parts draw in declaration order).
            if (row > 0)
            {
                if (sk != null) Art(NativeChrome.Frame, lx0, minTop, lx1, maxBot);
                else Outline(b, lx0, minTop, lx1, maxBot, Border);
            }
        }


        win.PortraitHits = hits.Count > 0 ? hits.ToArray() : System.Array.Empty<PortraitHit>();

        return new RenderFrame
        {
            Verts = b.ToArray(), Count = b.VertexCount, Portraits = portraits, Spans = spans,
            Skin = sk != null ? sk.ToArray() : System.Array.Empty<SkinCmd>(), SkinOver = skinOver,
            BodyStart = bodyStart, BodyEnd = bodyEnd, Bx = bx, By = by, Bw = bw, Bh = bh,
            Scrolled = active.ScrollPx != 0f,
            LastScroll = win.LastScroll,
            BlinkStart = blinkStart, BlinkEnd = blinkEnd,
            HoverStart = hoverStart, HoverEnd = hoverEnd, HoverSince = win.HoverSince,
            LeftStart = leftStart, LeftEnd = leftEnd, LeftSince = win.LeftSince,
        };
    }

    /// <summary>A chrome-only frame for the ghost tab floating at the pointer during a tab drag.
    /// Draws on top of everything (appended as the last frame in the publish array).</summary>
    public static RenderFrame BuildGhostTab(GlyphAtlas atlas, float x, float y, float w, string label)
    {
        var p = ResolvePalette();
        var b = new VertexBatch(atlas.SolidU, atlas.SolidV);
        float h = ChatWindow.TabBarH;
        bool art = ChatSkin.Native;
        if (!art)
        {
            Fill(b, x, y, x + w, y + h, NavOn, 0.9f);
            Outline(b, x, y, x + w, y + h, Border);
            b.Rect(x + 1f, y + h - 2f * Metrics.Scale, x + w - 1f, y + h, p.TabActive.r, p.TabActive.g, p.TabActive.b, 1f);
        }
        float lw = UiText.Measure(atlas, label), avail = w - 2f * ChatWindow.Pad;
        UiText.Draw(b, atlas, label, x + ChatWindow.Pad + (lw < avail ? (avail - lw) * 0.5f : 0f),
                    y + (h - atlas.LineHeight) * 0.5f + atlas.Ascent, Hi, x + w - ChatWindow.Pad);
        int vc = b.VertexCount;
        return new RenderFrame
        {
            Verts = b.ToArray(), Count = vc, BodyStart = vc, BodyEnd = vc,
            Skin = art ? new[] { new SkinCmd(NativeChrome.ButtonOn, x, y, x + w, y + h, ArtWhite) }
                       : System.Array.Empty<SkinCmd>(),
        };
    }

    /// <summary>One diagonal grip line: a 45° segment (bottom-left to top-right) at corner-distance
    /// <paramref name="len"/>, drawn as a run of small squares. Three of these nested make the gripper.</summary>
    private static void GripLine(VertexBatch b, float cx, float cy, float len, float r, float g, float bl)
    {
        for (float t = 0f; t <= len; t += 2f)
        {
            float x = cx - len + t, y = cy - t;   // (cx-len,cy) -> (cx,cy-len)
            b.Rect(x, y - 2f, x + 2f, y, r, g, bl, 0.5f);
        }
    }

    /// <summary>Emit the window background: a solid rect, or a <see cref="BgGrid"/>² quad grid whose vertex
    /// colours come from <see cref="BgT"/> (GPU-interpolated between them).</summary>
    private static void EmitBackground(VertexBatch b, ChatWindow win,
        (float r, float g, float b, float a) c1, (float r, float g, float b, float a) c2)
    {
        float x0 = win.X, y0 = win.Y, x1 = win.X + win.W, y1 = win.Y + win.H;
        if (win.BgMode == BgMode.Solid) { b.Rect(x0, y0, x1, y1, c1.r, c1.g, c1.b, c1.a); return; }
        const int N = BgGrid;
        float w = x1 - x0, h = y1 - y0;
        (float, float, float, float) At(int col, int row)
            => LerpColor(c1, c2, BgT(win, 2f * col / N - 1f, 2f * row / N - 1f, w, h));
        for (int row = 0; row < N; row++)
            for (int col = 0; col < N; col++)
                b.GradientRect(
                    x0 + col * w / N, y0 + row * h / N,
                    x0 + (col + 1) * w / N, y0 + (row + 1) * h / N,
                    At(col, row), At(col + 1, row), At(col + 1, row + 1), At(col, row + 1));
    }

    // ponytail: 20×20 grid; a hard stop blurs over one cell (5% of the window) -- a shader is the upgrade
    private const int BgGrid = 20;

    /// <summary>Gradient position of a point, 0 = Background .. 1 = Background 2, after the window's stops.
    /// <paramref name="sx"/>/<paramref name="sy"/> are the point in [-1,1] window space; <paramref name="w"/>/
    /// <paramref name="h"/> the window size (Linear measures along the angle in pixels, like CSS, so the
    /// corners land exactly on 0 and 1).</summary>
    internal static float BgT(ChatWindow win, float sx, float sy, float w, float h)
    {
        double th = win.BgAngle * System.Math.PI / 180.0;
        float dx = (float)System.Math.Cos(th), dy = (float)System.Math.Sin(th);
        float t;
        switch (win.BgMode)
        {
            case BgMode.Radial:
                t = (float)System.Math.Sqrt(sx * sx + sy * sy);
                break;
            case BgMode.Conic:
            {
                double a = System.Math.Atan2(sy, sx) - th;
                a = System.Math.Abs(System.Math.IEEERemainder(a, 2.0 * System.Math.PI));
                t = (float)(a / System.Math.PI);
                break;
            }
            default:
            {
                float p = sx * w * dx + sy * h * dy;
                float max = w * System.Math.Abs(dx) + h * System.Math.Abs(dy);
                t = max > 0f ? (p / max + 1f) * 0.5f : 0f;
                break;
            }
        }
        float s0 = win.BgStart, s1 = win.BgEnd;
        if (s1 <= s0) return t < s0 ? 0f : 1f;
        t = (t - s0) / (s1 - s0);
        return t < 0f ? 0f : t > 1f ? 1f : t;
    }

    private static (float r, float g, float b, float a) LerpColor(
        (float r, float g, float b, float a) a, (float r, float g, float b, float a) b, float t = 0.5f)
        => (a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);

    /// <summary>Laid-out block height (px) of one message -- the same wrap + portrait padding a body block
    /// gets in <see cref="Build"/>. The ingest path uses it to slide/compensate by the exact new-line height
    /// (block + one <see cref="MsgGap"/>) without re-measuring the whole backlog, so it's correct even under
    /// ring eviction (the read position holds to the bottom-anchored growth, not the net content change).</summary>
    public static float MeasureBlock(ChatWindow win, ChatLine line, GlyphAtlas atlas)
    {
        bool portraitsOn = (win.Toggles & ChatToggle.Portrait) != 0;
        win.BodyRect(out _, out _, out float bw, out _);
        float maxTextW = bw - 2 * ChatWindow.Pad - ChatWindow.ScrollbarW;
        List<Rune> runes = _runesScratch ??= new List<Rune>();
        List<uint> cols = _colsScratch ??= new List<uint>();
        bool hasP = BuildRunes(win, line, portraitsOn, runes, cols);
        float indent = hasP ? atlas.LineHeight + ChatWindow.Pad : 0f;
        float textH = TextWrapper.WrapRunes(runes, maxTextW - indent, atlas.AdvanceOfCached).Count * atlas.LineHeight;
        return hasP ? System.Math.Max(textH, atlas.LineHeight / PortraitAspect) : textH;
    }

    /// <summary>Run the toggle-gated line pipeline into <paramref name="runes"/>/<paramref name="cols"/>
    /// (cleared first). Returns whether the line should reserve a portrait gutter (portraits on + has resref).</summary>
    private static bool BuildRunes(ChatWindow win, ChatLine line, bool portraitsOn, List<Rune> runes, List<uint> cols)
    {
        runes.Clear(); cols.Clear();
        for (int c = 0; c < Pipeline.Length; c++)
            if ((win.Toggles & Pipeline[c].Bit) != 0) Pipeline[c].Contribute(win, line, runes, cols);
        return portraitsOn && !string.IsNullOrEmpty(line.PortraitResref);
    }

    /// <summary>Lay one wrapped row (a span into <paramref name="runes"/>) with per-rune colour.</summary>
    private static void EmitRow(VertexBatch b, GlyphAtlas atlas, List<Rune> runes, List<uint> cols,
                                TextWrapper.LineSpan span, float startX, float baseline,
                                float spaceExtra = 0f)
    {
        float penX = startX;
        for (int k = span.Start; k < span.End; k++)
        {
            Rune r = runes[k];
            if (!atlas.TryGet(r.Value, out Glyph g)) { penX += atlas.AdvanceOf(r); continue; }
            uint c = cols[k];
            b.Glyph(g, penX, baseline, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
            penX += g.Advance;
            if (r.Value == ' ') penX += spaceExtra;
        }
    }

    /// <summary>The word-wrapped input strip below the frame: a channel chip (top row) + the edit text
    /// wrapped to the strip width, the strip growing downward one <see cref="ChatWindow.FooterH"/> per row.
    /// Geometry comes from <see cref="InputStripLayout"/>, so the caret drawn here and the one a click computes
    /// are the same. Caret and selection are mapped from UTF-16 indices into wrapped rune rows.</summary>
    private static void EmitInputStrip(VertexBatch b, GlyphAtlas atlas, ChatWindow win, string editText,
                                       int caret, (int start, int end)? selection, Pal p, List<SkinCmd>? sk)
    {
        float fh = ChatWindow.FooterH, ins = Inset * Metrics.Scale;
        float baseDrop = (fh - atlas.LineHeight) * 0.5f + atlas.Ascent;

        List<Rune> runes = _runesScratch ??= new List<Rune>();
        InputStripLayout.Layout(win, atlas, editText, runes,
                                out List<TextWrapper.LineSpan> wraps, out float startX, out _);
        int rows = wraps.Count;
        win.FooterRows = rows;   // set BEFORE reading StripTop: in above-mode StripTop depends on FooterHeight
        float fy = win.StripTop;

        // Strip background over all rows, framed like the window; the channel chip on the top row only.
        // Native: the engine's edit-box art for the strip, a NUI button for the chip.
        float cx0 = win.X + ins, cx1 = win.X + ChatWindow.DropdownW - ins;
        if (sk != null)
        {
            sk.Add(new SkinCmd(NativeChrome.Edit, win.X, fy, win.X + win.W, fy + fh * rows, Tint(InputA)));
            sk.Add(new SkinCmd(NativeChrome.Button, cx0, fy + ins, cx1, fy + fh - ins, ArtWhite));
        }
        else
        {
            b.Rect(win.X, fy, win.X + win.W, fy + fh * rows, p.Footer.r, p.Footer.g, p.Footer.b, p.Footer.a);
            Outline(b, win.X, fy, win.X + win.W, fy + fh * rows, p.FrameRgb, p.Footer.a);
            Fill(b, cx0, fy + ins, cx1, fy + fh - ins, NavOn);
            Outline(b, cx0, fy + ins, cx1, fy + fh - ins, Frame2);
        }
        UiText.Draw(b, atlas, win.SendChannel.ToString(), cx0 + ChatWindow.Pad, fy + baseDrop, Hi, cx1 - ChatWindow.Pad - UiText.Measure(atlas, "▾"));
        UiText.Draw(b, atlas, "▾", cx1 - ChatWindow.Pad * 0.5f - UiText.Measure(atlas, "▾"), fy + baseDrop, Tx2, cx1);

        int caretR = InputStripLayout.RuneIndex(runes, caret);
        (int selA, int selB) = selection is (int a, int c)
            ? (InputStripLayout.RuneIndex(runes, a), InputStripLayout.RuneIndex(runes, c)) : (-1, -1);
        List<uint>? cols = ChatKnobs.Markup.V && runes.Count > 0 ? MarkupColors(editText, runes) : null;

        for (int w = 0; w < rows; w++)
        {
            TextWrapper.LineSpan span = wraps[w];
            float ry = fy + fh * w;
            // Selection highlight for this row's slice of [selA,selB) -- drawn first, behind the glyphs.
            if (selA >= 0 && selB > span.Start && selA < span.End)
            {
                int rs = System.Math.Max(selA, span.Start), re = System.Math.Min(selB, span.End);
                float x0 = RowAlign.Measure(atlas, runes, span.Start, rs, startX);
                float x1 = RowAlign.Measure(atlas, runes, span.Start, re, startX);
                b.Rect(x0, ry + ins, x1, ry + fh - ins, p.SelBg.r, p.SelBg.g, p.SelBg.b, p.SelBg.a);
            }
            if (cols != null) EmitRow(b, atlas, runes, cols, span, startX, ry + baseDrop);
            else EmitRunes(b, atlas, runes, span, startX, ry + baseDrop, EditRgb);

            if (InputStripLayout.CaretOnRow(wraps, w, caretR))
            {
                float cx = RowAlign.Measure(atlas, runes, span.Start, caretR, startX);
                b.Rect(cx, ry + ins + 1f, cx + 2f, ry + fh - ins - 1f, p.TabActive.r, p.TabActive.g, p.TabActive.b, 1f);
            }
        }

        if (ChatKnobs.Counter.V)
        {
            bool near = editText.Length >= InputStripLayout.CounterWarn;
            string txt = near ? $"{editText.Length}/{EditBuffer.MaxLength}" : editText.Length.ToString();
            float right = win.X + win.W - ChatWindow.Pad;
            UiText.Draw(b, atlas, txt, right - UiText.Measure(atlas, txt),
                        fy + fh * (rows - 1) + baseDrop, near ? Warn : Tx2, right);
        }
    }

    /// <summary>Per-rune colours for the edit text: each <see cref="InputMarkup"/> class mapped to its swatch,
    /// filled into the shared colour scratch so <see cref="EmitRow"/>'s glyph loop stays allocation-free.</summary>
    private static List<uint> MarkupColors(string editText, List<Rune> runes)
    {
        byte[]? s = _markupScratch;
        if (s == null || s.Length < editText.Length)
            _markupScratch = s = new byte[System.Math.Max(editText.Length, EditBuffer.MaxLength)];
        InputMarkup.Classify(editText, s.AsSpan(0, editText.Length));
        List<uint> cols = _colsScratch ??= new List<uint>();
        cols.Clear();
        int u = 0;
        for (int k = 0; k < runes.Count; k++) { cols.Add(ClassRgb(s[u])); u += runes[k].Utf16SequenceLength; }
        return cols;
    }

    /// <summary>A markup class's RGB: the swatch value's top 24 bits. The swatch alpha is ignored -- input text
    /// draws opaque, like the plain <see cref="EditRgb"/> it replaces.</summary>
    private static uint ClassRgb(byte cls) => cls switch
    {
        InputMarkup.Emote  => ChatKnobs.EmoteCol.V >> 8,
        InputMarkup.Speech => ChatKnobs.SpeechCol.V >> 8,
        InputMarkup.Ooc    => ChatKnobs.OocCol.V >> 8,
        _                  => EditRgb,
    };

    /// <summary>Draw one wrapped rune span in a single colour.</summary>
    private static void EmitRunes(VertexBatch b, GlyphAtlas atlas, List<Rune> runes, TextWrapper.LineSpan span,
                                  float startX, float baseline, uint rgb)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float penX = startX;
        for (int k = span.Start; k < span.End; k++)
        {
            if (atlas.TryGet(runes[k].Value, out Glyph gl)) { b.Glyph(gl, penX, baseline, r, g, bl, 1f); penX += gl.Advance; }
            else penX += atlas.AdvanceOf(runes[k]);
        }
    }
}
