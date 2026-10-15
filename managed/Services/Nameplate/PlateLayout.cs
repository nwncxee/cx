using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Nameplate;

/// <summary>One plate's laid-out boxes, in the same screen-px space the caller draws in. Sizes come out of
/// <see cref="PlateLayout.Measure"/>: the glyph metrics handed in already carry <see cref="Metrics.Scale"/>
/// (the atlas is baked at the effective px), so only the chrome constants are scaled -- do NOT scale these
/// again. All coordinates are relative to the plate's top-left.</summary>
public readonly struct PlateMetrics
{
    public readonly float W, H, PortraitX, PortraitY, PortraitSize, NameX, NameY, BandX, BandY, BarX, BarY, BarW, BarH;
    /// <summary>Width of the text column (the widest of name, band and any override line). The span a centred
    /// line is centred in -- never the whole box, which would slide the text under the portrait.</summary>
    public readonly float TextW;
    /// <summary>Top of the override lines that sit ABOVE the name (prepend mode), and of the ones BELOW the
    /// band and bar (append mode). Both are the text column's x (<see cref="NameX"/>).</summary>
    public readonly float PreY, PostY;

    public PlateMetrics(float w, float h, float portraitX, float portraitY, float portraitSize,
                        float nameX, float nameY, float bandX, float bandY,
                        float barX, float barY, float barW, float barH, float textW,
                        float preY = 0f, float postY = 0f)
    {
        W = w; H = h; PortraitX = portraitX; PortraitY = portraitY; PortraitSize = portraitSize;
        NameX = nameX; NameY = nameY; BandX = bandX; BandY = bandY;
        BarX = barX; BarY = barY; BarW = barW; BarH = barH; TextW = textW;
        PreY = preY; PostY = postY;
    }
}

/// <summary>One speech card's boxes (<see cref="PlateLayout.MeasureCard"/>), relative to its top-left: the portrait
/// at the pad, top-aligned, and the text column right of it -- the name header at the pad, the body from
/// <see cref="BodyY"/>.</summary>
public readonly struct CardMetrics
{
    public readonly float W, H, PortraitW, PortraitH, ColX, BodyY;

    public CardMetrics(float w, float h, float portraitW, float portraitH, float colX, float bodyY)
    { W = w; H = h; PortraitW = portraitW; PortraitH = portraitH; ColX = colX; BodyY = bodyY; }
}

/// <summary>Which existing plate line an <c>ObjectTextBubbleOverride</c> segment continues:
/// <c>First</c> means it runs in FRONT of that line (prepend), <c>Last</c> means it runs off its end
/// (append). <c>None</c> = the plate has no such line and every segment stands on its own.</summary>
public enum PlateSplice { None, NameFirst, NameLast, BandFirst, BandLast }

/// <summary>Pure layout + fade math for the overhead plate and bubble. No GL, no native reads, and no
/// allocation on the frame: every method here but one is called per entry per frame from the hot draw, and the
/// string/colour tables are static. The exception is <see cref="BreakName"/>, which runs COLD (once per name
/// resolve, on the hook thread) and is documented as such.</summary>
public static partial class PlateLayout
{
    /// <summary>Inner padding around the plate content, design px.</summary>
    private const float PadPx = 4f;
    /// <summary>Gap above the health bar. Design px.</summary>
    private const float GapPx = 4f;
    /// <summary>Gap between the portrait and the text column, design px. Wider than <see cref="GapPx"/>: a
    /// portrait is a picture butting against a centred text column, and 4px reads as the two touching.</summary>
    private const float PortraitGapPx = 8f;
    /// <summary>Health-bar thickness, design px.</summary>
    private const float BarHPx = 3f;
    /// <summary>NWN portrait aspect (w:h). Mirrors <see cref="TextRig.PortraitAspect"/>; duplicated as a local
    /// const so this file stays pure math and self-checks with no UI state.</summary>
    private const float PortraitAspect = 0.64f;

    /// <summary>Number of health bands (<c>CNWCCreatureStats + 0x8e</c> is 0..5).</summary>
    public const int BandCount = 6;

    /// <summary>The stock caption of each band: <c>damagelevels.2da</c>'s words in the game's own TLK. The plate
    /// shows these only where the client's table cannot be read.</summary>
    public static readonly string[] StockBandTexts =
        { "Uninjured", "Barely Injured", "Injured", "Heavily Wounded", "Near Death", "Dead" };

    /// <summary>Band colours as flat 0..255 (r,g,b) triplets in band order; normalised by
    /// <see cref="BandColor"/>.</summary>
    private static readonly float[] BandRgb =
    {
        128f, 255f, 128f,
        200f, 255f,   1f,
        255f, 200f,   1f,
        255f, 100f,   1f,
        255f,  80f,  64f,
        128f, 128f, 128f,
    };

    /// <summary>How far <c>|guiScale - 1|</c> must stray before <c>Camera::MapToScreen</c> shrinks its viewport
    /// by the GUI scale: the double at <c>.rodata 0x12eeed0</c> (<c>67666676 3d0ab73f</c> = 0.09), compared
    /// <c>ucomisd; ja &lt;skip&gt;</c> at @0x4a3e14. Inside the band the engine leaves the rect alone, so
    /// un-shrinking there would introduce an offset that was never applied.</summary>
    private const float GuiScaleEpsilon = 0.09f;

    /// <summary>
    /// Turn the engine's stored bubble anchor (<see cref="NameplateNative.AnchorRaw"/>) into the pixels a
    /// <c>Window</c> draws in: top-left origin, y down. Three terms -- undo the GUI-scale shrink about the camera
    /// rect's centre (only outside the engine's own <see cref="GuiScaleEpsilon"/> dead band), rebase and rescale
    /// onto the draw viewport, then flip y. A non-positive camera extent is "unknown": that axis passes through.
    /// </summary>
    public static void ToDrawSpace(float ex, float ey, float vpX, float vpY, float vpW, float vpH,
                                   float guiScale, float drawW, float drawH, out float x, out float y)
    {
        if (guiScale > 0f && (guiScale > 1f + GuiScaleEpsilon || guiScale < 1f - GuiScaleEpsilon)
            && vpW > 0f && vpH > 0f)
        {
            float cx = vpX + vpW * 0.5f, cy = vpY + vpH * 0.5f;
            ex = cx + (ex - cx) * guiScale;
            ey = cy + (ey - cy) * guiScale;
        }

        if (vpW > 0f && drawW > 0f) x = (ex - vpX) * (drawW / vpW);
        else x = ex;

        if (vpH > 0f && drawH > 0f) y = (vpY + vpH - ey) * (drawH / vpH);
        else y = drawH > 0f ? drawH - ey : -ey;
    }

    /// <summary>Linear step for a fade that takes <paramref name="animMs"/> to cross 0..1, from a monotonic
    /// delta. A <paramref name="dtMs"/> of zero or less -- two draws inside one tick, or a clock that ran
    /// backwards -- is no step at all; an interval of a millisecond or less snaps.</summary>
    public static float FadeStep(long dtMs, float animMs)
    {
        if (dtMs <= 0) return 0f;
        if (!(animMs > 1f)) return 1f;      // NaN-safe: an unparseable knob snaps rather than freezing
        float s = dtMs / animMs;
        return s > 1f ? 1f : s;
    }

    /// <summary>Walk <paramref name="v"/> at most <paramref name="step"/> toward <paramref name="target"/>,
    /// never past it. The Dialog panel's shape (<c>DialogService.MoveToward</c>), reused verbatim.</summary>
    public static void MoveToward(ref float v, float target, float step)
    {
        if (v < target) { v += step; if (v > target) v = target; }
        else if (v > target) { v -= step; if (v < target) v = target; }
    }

    /// <summary>Ease a pixel offset toward zero: the exponential residual the dialog body uses,
    /// <c>tau = max(20, animMs * 0.4)</c>, with a half-pixel snap so it lands instead of asymptoting. A
    /// non-positive <paramref name="dtMs"/> leaves it exactly where it was.</summary>
    public static void SettleOffset(ref float v, long dtMs, float animMs)
    {
        if (v == 0f) return;
        if (dtMs <= 0) return;
        if (!(animMs > 0f)) { v = 0f; return; }   // "no animation" means no slide either, not a 20ms one
        float tau = animMs * 0.4f;
        if (!(tau > MinTauMs)) tau = MinTauMs;
        v -= v * (1f - System.MathF.Exp(-dtMs / tau));
        if (v < SnapPx && v > -SnapPx) v = 0f;
    }

    /// <summary>Floor on the ease time constant, ms: below this a slide is a jump at any sane frame rate.</summary>
    private const float MinTauMs = 20f;
    /// <summary>Offset residual under which a slide is finished, px.</summary>
    private const float SnapPx = 0.5f;

    /// <summary>Top-left the caller should draw a (<paramref name="w"/>×<paramref name="h"/>) plate at, given the
    /// engine's projected screen anchor: centred horizontally, sitting directly above the anchor. Clamped into
    /// the live viewport (<see cref="TextRig.ViewportW"/>/<see cref="TextRig.ViewportH"/>).</summary>
    public static void Place(float anchorX, float anchorY, float w, float h, out float x, out float y) =>
        Place(anchorX, anchorY, w, h, TextRig.ViewportW, TextRig.ViewportH, out x, out y);

    /// <summary>As <see cref="Place(float,float,float,float,out float,out float)"/>, with the viewport passed
    /// explicitly (the self-check's door, and the caller's when it already has the size). A zero or negative
    /// viewport extent means "unknown" and that axis is left unclamped.</summary>
    public static void Place(float anchorX, float anchorY, float w, float h, float vpW, float vpH,
                             out float x, out float y)
    {
        x = anchorX - w * 0.5f;
        y = anchorY - h;
        if (vpW > 0f)
        {
            float maxX = vpW - w;
            if (x > maxX) x = maxX;
            if (x < 0f) x = 0f;
        }
        if (vpH > 0f)
        {
            float maxY = vpH - h;
            if (y > maxY) y = maxY;
            if (y < 0f) y = 0f;
        }
    }

    /// <summary>Lay out portrait, name, health band and optional health bar into one plate box, with a
    /// single-line name. Kept as the one-line door so every caller that has no line count reads unchanged.</summary>
    public static PlateMetrics Measure(float nameW, float bandW, bool portrait, bool band, bool bar,
                                       float lineH, float scale) =>
        Measure(nameW, 1, bandW, portrait, band, bar, lineH, scale);

    /// <summary>As above for a name of <paramref name="nameLines"/> lines (<see cref="SplitLines"/>);
    /// <paramref name="nameW"/> is then the WIDEST line. <paramref name="bandW"/> and <paramref name="lineH"/> are
    /// measured glyph metrics used as given; <paramref name="scale"/> scales only the chrome. The portrait spans at
    /// most <see cref="PortraitMaxLines"/> name lines plus band and bar, vertically centred on the whole column.</summary>
    public static PlateMetrics Measure(float nameW, int nameLines, float bandW, bool portrait, bool band,
                                       bool bar, float lineH, float scale) =>
        Measure(nameW, nameLines, bandW, portrait, band, bar, lineH, scale, 0, 0, 0f);

    /// <summary>As above with the <c>ObjectTextBubbleOverride</c> bands folded in: <paramref name="preLines"/>
    /// override lines above the name, <paramref name="postLines"/> below the band and bar, and
    /// <paramref name="extraW"/> the widest override line PLUS the spliced line the override extends (the caller
    /// adds the band or name width to the splice). <paramref name="nameLines"/> is 0 only in replace mode.</summary>
    public static PlateMetrics Measure(float nameW, int nameLines, float bandW, bool portrait, bool band,
                                       bool bar, float lineH, float scale,
                                       int preLines, int postLines, float extraW)
    {
        float pad = PadPx * scale, gap = GapPx * scale, barH = BarHPx * scale, pgap = PortraitGapPx * scale;
        if (preLines < 0) preLines = 0;
        if (postLines < 0) postLines = 0;
        if (nameLines < 1 && preLines + postLines == 0) nameLines = 1;
        if (nameLines < 0) nameLines = 0;

        float textW = nameW;
        if (band && bandW > textW) textW = bandW;
        if (extraW > textW) textW = extraW;
        if (textW < 0f) textW = 0f;

        float preH = lineH * preLines, postH = lineH * postLines;
        float nameH = lineH * nameLines;
        float below = (band ? lineH : 0f) + (bar ? gap + barH : 0f);
        float contentH = preH + nameH + below + postH;

        // The portrait tracks the NAME lines; in replace mode there are none, so the override lines stand in.
        int spanLines = nameLines > 0 ? nameLines : preLines;
        if (spanLines < 1) spanLines = 1;
        int portraitLines = spanLines < PortraitMaxLines ? spanLines : PortraitMaxLines;
        float portraitSize = portrait ? portraitLines * lineH + below : 0f;
        float portraitW = portraitSize * PortraitAspect;
        float colX = pad + (portrait ? portraitW + pgap : 0f);

        float w = colX + textW + pad;
        float h = contentH + pad * 2f;

        float nameY = pad + preH;
        float bandY = nameY + nameH;
        float barY = bandY + (band ? lineH : 0f) + gap;
        float postY = nameY + nameH + below;

        return new PlateMetrics(w, h,
            pad, PortraitTop(pad, contentH, portraitSize), portraitSize,
            colX, nameY,
            colX, bandY,
            colX, barY, bar ? textW : 0f, bar ? barH : 0f, textW,
            pad, postY);
    }

    /// <summary>Lay out a speech card: portrait left, <paramref name="nameLines"/> of header, then the body. The portrait
    /// is <paramref name="portraitLines"/> text lines tall (0 = none) whatever the body, so a one-liner still shows a
    /// readable face; <paramref name="pad"/> arrives scaled, <paramref name="scale"/> scales the rest of the chrome.</summary>
    public static CardMetrics MeasureCard(float textW, float bodyH, int nameLines, int portraitLines, float lineH,
                                          float pad, float scale)
    {
        float ph = portraitLines > 0 ? portraitLines * lineH : 0f;
        float pw = ph * PortraitAspect;
        float colX = pad + (ph > 0f ? pw + PortraitGapPx * scale : 0f);
        float headH = nameLines > 0 ? nameLines * lineH + NameGapPx * scale : 0f;
        float contentH = headH + (bodyH > 0f ? bodyH : 0f);
        if (ph > contentH) contentH = ph;
        return new CardMetrics(colX + (textW > 0f ? textW : 0f) + pad, contentH + pad * 2f, pw, ph, colX, pad + headH);
    }

    /// <summary>Gap between a card's name header and what was said, design px.</summary>
    private const float NameGapPx = 2f;

    /// <summary>The speech card's parts from <c>nameplate.speaker</c>'s mask and what the
    /// speaker has. A plate <paramref name="stacked"/> under the bubble takes over the name, and the portrait when
    /// <paramref name="platePortrait"/>: each shows once. False = no card, the plain per-line boxes.</summary>
    public static bool CardParts(int speaker, bool stacked, bool platePortrait, bool hasName, bool hasPortrait,
                                 out bool name, out bool portrait)
    {
        name = !stacked && (speaker & NameplateKnobs.SpeakerName) != 0 && hasName;
        portrait = !(stacked && platePortrait) && (speaker & NameplateKnobs.SpeakerPortrait) != 0 && hasPortrait;
        return name || portrait;
    }

    /// <summary>Runes of the <c>"[speaker]: "</c> prefix the engine glues onto a placeable's or door's chat line
    /// (object types 7/9/10, <c>HandleServerToPlayerChat</c>'s tail), or 0. A card with a name
    /// header skips it rather than naming the speaker twice. A prefix with nothing after it is not skipped -- that
    /// would draw an empty line. Pure, cold (once per line, on the drain).</summary>
    public static int SpeakerPrefixLen(System.Collections.Generic.IReadOnlyList<System.Text.Rune> r)
    {
        if (r.Count == 0 || r[0].Value != '[') return 0;
        for (int i = 1; i + 3 < r.Count; i++)
            if (r[i].Value == ']' && r[i + 1].Value == ':' && r[i + 2].Value == ' ') return i + 3;
        return 0;
    }

    /// <summary>NAME lines a portrait may grow with (the band and bar are always added on top). Past this a
    /// longer name grows the box downward and the portrait, capped, centres against the taller column.</summary>
    public const int PortraitMaxLines = 2;

    /// <summary>Top edge of the portrait: vertically centred against the content column, never above the pad. The
    /// clamp is defensive -- the size rule (<see cref="PortraitMaxLines"/> name lines plus the same band/bar term
    /// the content carries) cannot exceed <paramref name="contentH"/> today, and where the two are equal this is
    /// exactly <paramref name="pad"/>.</summary>
    public static float PortraitTop(float pad, float contentH, float portraitSize)
    {
        float slack = contentH - portraitSize;
        return slack > 0f ? pad + slack * 0.5f : pad;
    }

    /// <summary>Left edge of one text line inside a column of <paramref name="textW"/> starting at
    /// <paramref name="colX"/>: centred in the column, as vanilla centres every plate line. The column, never
    /// the whole box -- centring in the box would slide the text under the portrait. A line wider than the
    /// column pins to the start rather than hanging off the left.</summary>
    public static float LineX(float colX, float textW, float lineW)
    {
        float slack = textW - lineW;
        return slack > 0f ? colX + slack * 0.5f : colX;
    }

    /// <summary>
    /// Normalise every line break a builder can express in a name down to one LF: a real LF/CR/CRLF, the literal
    /// two-character <c>\n</c> (what a toolset name field can carry), and a run of <see cref="SpacesPerBreak"/>+
    /// spaces. Spaces beside a break are dropped, consecutive breaks collapse and the ends are trimmed. COLD, and
    /// an unchanged name allocates nothing. ponytail: plate names only -- bubbles wrap already and would double-break.
    /// </summary>
    public static string BreakName(string s) => BreakName(s, null, out _);

    /// <summary>As <see cref="BreakName(string)"/>, carrying a per-char colour array through the same rewrite so
    /// the two cannot drift out of alignment. <paramref name="colours"/> is indexed by char of
    /// <paramref name="s"/>, the returned array by char of the returned string. A whitespace char the rewrite
    /// INSERTS takes the colour of the char that follows it, keeping the index arithmetic honest.</summary>
    public static string BreakName(string s, uint[]? colours, out uint[]? outColours)
    {
        outColours = colours;
        if (!NeedsBreak(s)) return s;

        var sb = new System.Text.StringBuilder(s.Length);
        var cols = colours == null ? null : new System.Collections.Generic.List<uint>(s.Length);
        int spaces = 0;
        bool brk = false, any = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                brk = true; spaces = 0; continue;
            }
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == 'n')
            {
                i++; brk = true; spaces = 0; continue;
            }
            if (c == ' ') { spaces++; continue; }

            uint col = cols == null ? 0u : i < colours!.Length ? colours[i] : Plain;
            if (any)
            {
                if (brk || spaces >= SpacesPerBreak) { sb.Append('\n'); cols?.Add(col); }
                else for (int k = 0; k < spaces; k++) { sb.Append(' '); cols?.Add(col); }
            }
            spaces = 0; brk = false;
            sb.Append(c); cols?.Add(col); any = true;
        }
        outColours = cols?.ToArray();
        return sb.ToString();
    }

    /// <summary>The packed RGB that means "no colour tag covered this char" -- white, i.e. take whatever colour
    /// the caller was going to use anyway. An explicit <c>&lt;cÿÿÿ&gt;</c> therefore reads as untagged, and no
    /// sentinel fixes that: <see cref="ColorRunParser"/> hands back the default colour and an explicit white as the
    /// same run, so the distinction is gone before this sees it. The fix is teaching the parser tag DEPTH.</summary>
    public const uint Plain = 0xffffffu;  // not-an-offset: a colour

    /// <summary>
    /// Strip the Aurora inline colour grammar out of a name and hand back the colour each surviving char was
    /// under, packed 0xRRGGBB. Null out-array means "every char was plain" -- the common name, which costs one
    /// <c>IndexOf</c> and allocates nothing. Without this the tags reach the glyph emit and draw as literal
    /// <c>&lt;c???&gt;</c> text. The grammar is not re-implemented: <see cref="ColorRunParser"/> is the one parser.
    /// </summary>
    public static string StripColours(string raw, out uint[]? colours)
    {
        colours = null;
        if (raw.Length == 0 || raw.IndexOf('<') < 0) return Native.EngineText(raw);

        ColorRun[] runs = ColorRunParser.Parse(raw);
        int len = 0;
        bool any = false;
        for (int i = 0; i < runs.Length; i++)
        {
            len += runs[i].Text.Length;
            if (Pack(runs[i]) != Plain) any = true;
        }
        if (len == 0) return "";

        var sb = new System.Text.StringBuilder(len);
        uint[]? cols = any ? new uint[len] : null;
        int n = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            ColorRun run = runs[i];
            uint c = Pack(run);
            sb.Append(run.Text);
            if (cols != null) for (int k = 0; k < run.Text.Length; k++) cols[n + k] = c;
            n += run.Text.Length;
        }
        colours = cols;
        return sb.ToString();
    }

    private static uint Pack(in ColorRun r) =>
        ((uint)(r.R * 255f + 0.5f) << 16) | ((uint)(r.G * 255f + 0.5f) << 8) | (uint)(r.B * 255f + 0.5f);

    /// <summary>Consecutive spaces that stand in for a line break. Three, per the report: two is a normal
    /// wide gap, three is someone pushing text onto the next line.</summary>
    public const int SpacesPerBreak = 3;

    /// <summary>Whether <see cref="BreakName"/> would change anything -- the pre-scan that keeps the common
    /// name (no breaks, no padding, no stray edge space) allocation-free.</summary>
    private static bool NeedsBreak(string s)
    {
        if (s.Length == 0) return false;
        if (s[0] == ' ' || s[s.Length - 1] == ' ') return true;
        int spaces = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n' || c == '\r') return true;
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == 'n') return true;
            if (c == ' ') { if (++spaces >= SpacesPerBreak) return true; }
            else spaces = 0;
        }
        return false;
    }

    /// <summary>Index the LF-separated lines of a <see cref="BreakName"/>ed string into caller-owned span arrays.
    /// Pure and allocation-free, so the frame can measure and emit each line without a substring. Past
    /// <paramref name="max"/> lines the remainder is dropped, the last kept line ending at its own break.</summary>
    /// <returns>the number of lines written; 0 for an empty string.</returns>
    public static int SplitLines(string s, int[] start, int[] len, int max)
    {
        if (s.Length == 0 || max < 1) return 0;
        int n = 0, from = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i != s.Length && s[i] != '\n') continue;
            start[n] = from; len[n] = i - from; n++;
            from = i + 1;
            if (n >= max) break;
        }
        return n;
    }

    /// <summary>Split an <c>ObjectTextBubbleOverride</c> payload into segments on a real LF / CR / CRLF and
    /// nothing else -- NOT <see cref="BreakName"/>, whose trim and collapse would corrupt a server-composed wire
    /// format. Empty segments are preserved (one states that nothing joins the plate's text), bar trailing ones.</summary>
    /// <param name="max">segment ceiling; a longer override keeps the first <paramref name="max"/>.</param>
    public static int SplitOverride(string s, int[] start, int[] len, int max)
    {
        if (s.Length == 0 || max < 1) return 0;
        int n = 0, from = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i != s.Length && s[i] != '\n' && s[i] != '\r') continue;
            start[n] = from; len[n] = i - from; n++;
            if (i + 1 < s.Length && s[i] == '\r' && s[i + 1] == '\n') i++;   // CRLF is one break, not two
            from = i + 1;
            if (n >= max) break;
        }
        while (n > 1 && len[n - 1] == 0) n--;
        return n;
    }

    /// <summary>The mode-to-placement half of the <c>ObjectTextBubbleOverride</c> rule: which segment continues
    /// an existing line and which stand alone. The engine builds ONE string, <c>name\nband</c>, and joins the
    /// override to its head or tail -- so append's FIRST segment continues the last line, prepend's LAST runs
    /// into the first, and replace makes the segments the whole plate.</summary>
    public static PlateSplice PlaceOverride(int mode, int paraCount, int nameLines, bool band,
                                            out bool showName, out bool showBand, out int spliceIdx,
                                            out int preFrom, out int preTo, out int postFrom, out int postTo)
    {
        showName = true; showBand = band;
        spliceIdx = -1;
        preFrom = 0; preTo = 0; postFrom = 0; postTo = 0;
        if (mode is < 1 or > 3 || paraCount < 1) return PlateSplice.None;

        if (mode == 1)   // replace: name and band are the server's to discard
        {
            showName = false; showBand = false;
            preTo = paraCount;
            return PlateSplice.None;
        }

        if (mode == 2)   // prepend: override + text
        {
            PlateSplice t = nameLines > 0 ? PlateSplice.NameFirst
                          : band ? PlateSplice.BandFirst : PlateSplice.None;
            if (t == PlateSplice.None) { preTo = paraCount; return t; }
            spliceIdx = paraCount - 1; preTo = paraCount - 1;
            return t;
        }

        // 3, append: text + override
        PlateSplice a = band ? PlateSplice.BandLast
                      : nameLines > 0 ? PlateSplice.NameLast : PlateSplice.None;
        if (a == PlateSplice.None) { postTo = paraCount; return a; }
        spliceIdx = 0; postFrom = 1; postTo = paraCount;
        return a;
    }

    /// <summary>1.0 nearer than <c>maxDist - fadeDist</c>, linear to 0.0 at <paramref name="maxDist"/>.
    /// <paramref name="maxDist"/> &lt;= 0 means unlimited. 0 =&gt; cull.</summary>
    public static float DistanceFade(float dist, float maxDist, float fadeDist)
    {
        if (maxDist <= 0f) return 1f;
        if (dist >= maxDist) return 0f;
        if (fadeDist <= 0f) return 1f;
        if (fadeDist > maxDist) fadeDist = maxDist;   // else the band starts behind the camera and nothing is ever opaque
        float start = maxDist - fadeDist;
        if (dist <= start) return 1f;
        return (maxDist - dist) / fadeDist;
    }

    /// <summary>Hold at 1 for <paramref name="holdSec"/> then linear to 0 over <paramref name="fadeSec"/>
    /// (the <c>ChatAnim.FadeAlpha</c> shape, on a monotonic ms clock).</summary>
    public static float BubbleAlpha(long nowMs, long postedMs, float holdSec, float fadeSec)
    {
        float age = (nowMs - postedMs) / 1000f;
        if (age <= holdSec) return 1f;
        if (fadeSec <= 0f) return 0f;
        if (age >= holdSec + fadeSec) return 0f;
        return 1f - (age - holdSec) / fadeSec;
    }

    /// <summary>Where a speech-bubble tail hangs off the bottom of its box: the tip tracks the ANCHOR, not the box
    /// centre (<see cref="Place"/> and <see cref="Separate"/> both move the box), the base stays inside the box
    /// inset by <paramref name="inset"/>, and a box narrower than the tail drops the inset then the width.</summary>
    /// <param name="baseL">left end of the tail's base along the box's bottom edge; <paramref name="baseR"/> the right.</param>
    public static void TailPoints(float anchorX, float boxX, float boxW, float tailW, float inset,
                                  out float baseL, out float baseR, out float tipX)
    {
        float avail = boxW - inset * 2f;
        if (avail < tailW) { inset = 0f; avail = boxW; }
        if (avail < tailW) tailW = avail > 0f ? avail : 0f;

        float half = tailW * 0.5f;
        float lo = boxX + inset + half, hi = boxX + boxW - inset - half;
        if (hi < lo) lo = hi = boxX + boxW * 0.5f;

        float cx = anchorX < lo ? lo : (anchorX > hi ? hi : anchorX);
        baseL = cx - half; baseR = cx + half;
        tipX = anchorX < boxX ? boxX : (anchorX > boxX + boxW ? boxX + boxW : anchorX);
    }

    /// <summary>Our replacement for the engine's <c>BumpAllBubbles</c>, which text-bubble mode 0 disables: one pass
    /// pushing every box that still overlaps an already-placed one above it, then re-clamping into the viewport
    /// (<paramref name="vpH"/> &lt;= 0 means unknown, so unclamped). Hot: in place, no allocation. ponytail: one
    /// pass, vertical only, and the clamp can re-introduce an overlap in a crowd; a fixed point is the upgrade.</summary>
    public static void Separate(float[] x, float[] y, float[] w, float[] h, int count, float gap, float vpH)
        => Separate(x, y, w, h, null, count, gap, vpH);

    /// <summary>The ordered form: <paramref name="order"/> names which boxes take part and in what sequence, and
    /// <paramref name="count"/> is its length, not the box count. The FIRST index visited keeps its placed position
    /// and every later one is pushed clear ABOVE it; an absent index takes no part, which is how a fading box stops
    /// shoving live ones. <c>null</c> means every box in array order; <paramref name="vpH"/> as above.</summary>
    public static void Separate(float[] x, float[] y, float[] w, float[] h, int[]? order, int count,
                                float gap, float vpH)
    {
        for (int oi = 1; oi < count; oi++)
        {
            int i = order == null ? oi : order[oi];
            for (int oj = 0; oj < oi; oj++)
            {
                int j = order == null ? oj : order[oj];
                if (x[i] + w[i] <= x[j] || x[j] + w[j] <= x[i]) continue;
                if (y[i] + h[i] <= y[j] || y[j] + h[j] <= y[i]) continue;
                y[i] = y[j] - h[i] - gap;
            }
            if (vpH <= 0f) continue;
            float maxY = vpH - h[i];
            if (y[i] > maxY) y[i] = maxY;
            if (y[i] < 0f) y[i] = 0f;
        }
    }

    /// <summary>Fill <paramref name="order"/> with the included box indices, newest <paramref name="postedMs"/>
    /// first, so <see cref="Separate"/> stacks a conversation chronologically. Ties break on the ascending unique
    /// object <paramref name="ids"/>, so the sequence is a total order and cannot flicker. Hot, no allocation.</summary>
    /// <returns>how many indices were written -- the <c>count</c> to hand <see cref="Separate"/>.</returns>
    public static int OrderByRecency(int[] order, long[] postedMs, uint[] ids, bool[] include, int count)
    {
        int n = 0;
        for (int i = 0; i < count; i++) if (include[i]) order[n++] = i;
        for (int i = 1; i < n; i++)
        {
            int v = order[i];
            long kp = postedMs[v]; uint kid = ids[v];
            int j = i - 1;
            while (j >= 0 && SortsAfter(postedMs[order[j]], ids[order[j]], kp, kid))
            { order[j + 1] = order[j]; j--; }
            order[j + 1] = v;
        }
        return n;
    }

    /// <summary>True when (<paramref name="ap"/>, <paramref name="aid"/>) belongs BEHIND (<paramref name="bp"/>,
    /// <paramref name="bid"/>): older, or the same instant with a larger id.</summary>
    private static bool SortsAfter(long ap, uint aid, long bp, uint bid)
        => ap < bp || (ap == bp && aid > bid);

    /// <summary>Absorb a change in a box's separation push into its slide residual, so a box that changes stack
    /// slot eases to the new one instead of teleporting. Draw at <c>placed + push + yOff</c>: the residual is set
    /// so that expression is unchanged on the frame the push moves, and <see cref="SettleOffset"/> then closes
    /// it. Pure book-keeping -- it never touches the placement itself.</summary>
    public static void AbsorbPush(ref float yOff, ref float lastPush, float push)
    {
        yOff += lastPush - push;
        lastPush = push;
    }

    /// <summary>Health-band colour, 0..1. Out-of-range indices clamp to the nearest band.</summary>
    public static (float r, float g, float b) BandColor(int band)
    {
        int i = band < 0 ? 0 : band >= BandCount ? BandCount - 1 : band;
        int o = i * 3;
        return (BandRgb[o] / 255f, BandRgb[o + 1] / 255f, BandRgb[o + 2] / 255f);
    }

    /// <summary>The damage band <paramref name="hp"/> of <paramref name="max"/> falls in: <c>CNWSObject::GetDamageLevel</c>
    /// (Linux nm @0xb6dc70) replayed test for test -- float divide, <c>f &lt; t</c> against the same float thresholds,
    /// so a zero <paramref name="max"/> lands where the engine's NaN/infinity does.</summary>
    public static int BandOf(int hp, int max)
    {
        float f = (float)hp / max;
        if (!(f < 0.95f)) return 0;
        if (!(f < 0.75f)) return 1;
        if (!(f < 0.5f)) return 2;
        if (!(f < 0.25f)) return 3;
        return f <= 0f ? 5 : 4;
    }

    /// <summary>The health bar's fill for a band alone: its midpoint, full for uninjured, empty for dead.</summary>
    private static readonly float[] BandFill = { 1f, 0.85f, 0.625f, 0.375f, 0.125f, 0f };

    /// <summary>The health bar's fill, 0..1: <c>(cur + temp) / max</c> while <paramref name="max"/> is set and the
    /// pool lands in <paramref name="band"/> (only self and party are ever sent HP, and a leaver's goes stale),
    /// else the band's midpoint. ponytail: stale HP that still agrees with the band draws a stale fill within it;
    /// checking party membership (<c>CPanelPartyBar::FindMemberPanel</c>) is the upgrade if that reads wrong.</summary>
    public static float HealthFraction(int cur, int temp, int max, int band)
    {
        int b = band < 0 ? 0 : band >= BandCount ? BandCount - 1 : band;
        if (max > 0)
        {
            int hp = (short)(cur + temp);   // the engine sums the two shorts as a short
            if (BandOf(hp, max) == b)
            {
                float f = (float)hp / max;
                return f < 0f ? 0f : f > 1f ? 1f : f;
            }
        }
        return BandFill[b];
    }

    /// <summary>One caption per band from what the client's table resolved to: a null entry (the lookup failed)
    /// takes the stock word, a blank one stays <c>""</c> (the server chose no caption), anything else is trimmed.
    /// Cold: allocates the array once per resolve. Missing trailing entries count as failed lookups.</summary>
    public static string[] BandCaptions(ReadOnlySpan<string?> resolved)
    {
        var captions = new string[BandCount];
        for (int i = 0; i < BandCount; i++)
            captions[i] = i >= resolved.Length || resolved[i] is not { } r ? StockBandTexts[i] : r.Trim();
        return captions;
    }

    /// <summary>Health-band caption from <paramref name="captions"/> (<see cref="BandCaptions"/> or
    /// <see cref="StockBandTexts"/>). Out-of-range indices clamp to the nearest band; the returned string is a
    /// cached one, never built (hot path), and may be empty.</summary>
    public static string BandText(string[] captions, int band) =>
        captions[band < 0 ? 0 : band >= BandCount ? BandCount - 1 : band];

    /// <summary>Name colour by reaction byte (<c>CNWCCreature + 0x3a8</c>): 1 friend green, 2 enemy red,
    /// anything else neutral blue. 0..1.</summary>
    public static (float r, float g, float b) ReactionColor(byte reaction) => reaction switch
    {
        1 => (128f / 255f, 255f / 255f, 128f / 255f),
        2 => (255f / 255f, 80f / 255f, 64f / 255f),
        _ => (128f / 255f, 128f / 255f, 255f / 255f),
    };
}
