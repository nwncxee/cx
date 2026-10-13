using System;
using System.Text;
using CX.Services.Nui;
using CX.Ui;
using CX.Services.Panels;

namespace CX.Services.Cards;

/// <summary>Where cards go: beside the element, never over it, and on screen.</summary>
internal static class CardPlacer
{
    /// <summary>The default placement: right of the element, flipping left, tops level.</summary>
    public static void Place(PanelView[] views, float ex, float ey, float ew, float vw, float vh, float scale, Span<float> xs, Span<float> ys)
        => Place(views, ex, ey, ew, 0f, vw, vh, scale, new PanelStyle(), xs, ys);

    /// <summary>
    /// Puts the cards side by side with their tops level, against the element (<paramref name="ex"/>, <paramref name="ey"/>, <paramref name="ew"/>,
    /// <paramref name="eh"/>) on the side <paramref name="place"/> names, or the opposite side when it has no room and the style flips.
    /// Shifts by the style's <c>dx</c>, <c>dy</c>, then clamps inside <paramref name="vw"/> by <paramref name="vh"/>; a tab counts above the top.
    /// </summary>
    public static void Place(PanelView[] views, float ex, float ey, float ew, float eh, float vw, float vh, float scale, PanelStyle place, Span<float> xs, Span<float> ys)
    {
        float gap = place.CardGap * scale, off = place.Offset * scale, total = 0f, maxH = 0f, tab = 0f;
        for (int i = 0; i < views.Length; i++)
        {
            total += views[i].W + (i > 0 ? gap : 0f);
            maxH = Math.Max(maxH, views[i].H);
            tab = Math.Max(tab, views[i].TabH);
        }
        float x0, y0;
        if (place.Side is Side.Right or Side.Left)
        {
            x0 = place.Side == Side.Right ? ex + ew + off : ex - off - total;
            bool out_ = place.Side == Side.Right ? x0 + total > vw : x0 < 0f;
            if (out_ && place.Flip) x0 = place.Side == Side.Right ? ex - off - total : ex + ew + off;
            y0 = place.Align switch { Align.Center => ey + eh / 2f - maxH / 2f, Align.End => ey + eh - maxH, _ => ey };
        }
        else
        {
            y0 = place.Side == Side.Above ? ey - off - maxH : ey + eh + off;
            bool out_ = place.Side == Side.Above ? y0 - tab < 0f : y0 + maxH > vh;
            if (out_ && place.Flip) y0 = place.Side == Side.Above ? ey + eh + off : ey - off - maxH;
            x0 = place.Align switch { Align.Center => ex + ew / 2f - total / 2f, Align.End => ex + ew - total, _ => ex };
        }
        x0 = Window.ClampAxis(x0 + place.Dx * scale, total, vw);
        y0 = Window.ClampAxis(y0 - tab + place.Dy * scale, maxH + tab, vh) + tab;
        for (int i = 0; i < views.Length; i++)
        {
            xs[i] = x0; ys[i] = y0;
            x0 += views[i].W + gap;
        }
    }
}

/// <summary>
/// The hover card's state, with no drawing: which declared cards the pointer rests on, when they show and fade, and where
/// they sit. Looking a hit up costs nothing while the element, row and image stay the same, and a lookup allocates nothing.
/// The show and fade timing is <see cref="Tooltip"/>'s, so <c>ui.tipdelay</c> and the fades set it, and moving to a
/// neighbouring card skips the wait.
/// </summary>
internal sealed class CardPresenter
{
    /// <summary>Atlas, stretch and subject come through these so a self-check can supply its own.</summary>
    internal Func<float, (GlyphAtlas A, float K)> Face = PanelFonts.Resolve;
    internal Func<uint, PanelSubject> SubjectOf = static _ => default;

    private static readonly PanelStyle Fallback = new();
    private readonly Tooltip _tip = new();
    private CardSet? _set, _found;
    private IntPtr _el;
    private int _row = -2, _imgLen, _imgHash, _gen = -1;
    private float _ex, _ey, _ew, _eh;
    private uint _subject;
    private bool _rebuild;
    private readonly float[] _xs = new float[PanelLimits.MaxPanels], _ys = new float[PanelLimits.MaxPanels];

    public PanelView[]? Views => _set?.Views;
    public float X(int i) => _xs[i];
    public float Y(int i) => _ys[i];
    public float Alpha => _set == null ? 0f : _tip.Alpha;
    public bool Hovering => _tip.Hovering;
    public CardSet? Set => _set;

    /// <summary>The pointer is over <paramref name="hit"/> (<paramref name="has"/>), or over nothing declared. A hit that no key matches hides a shown card.</summary>
    public void Update(bool has, in NuiHit hit)
    {
        CardSet? found = null;
        if (has)
        {
            int h = Hash(hit.Image());
            if (hit.Element == _el && hit.Row == _row && h == _imgHash && hit.Image().Length == _imgLen && CardRegistry.Gen == _gen) found = _found;
            else
            {
                found = Lookup(in hit);
                bool declaredEmpty = found is { Cards.Length: 0 };
                if (declaredEmpty) found = null;
                bool sameTarget = hit.Element == _el && hit.Row == _row && h == _imgHash && hit.Image().Length == _imgLen;
                if (sameTarget && found != null && found != _set) _tip.Retarget(found);
                _el = hit.Element; _row = hit.Row; _imgHash = h; _imgLen = hit.Image().Length; _gen = CardRegistry.Gen; _found = found;
                _subject = ItemKey(hit.Image(), out uint drawn) ? drawn : 0u;
                _rebuild = found != null;
            }
        }
        else { _el = IntPtr.Zero; _row = -2; _found = null; _gen = -1; }
        if (found == null) { _tip.Leave(); return; }
        _set = found;
        _ex = hit.X; _ey = hit.Y; _ew = hit.W; _eh = hit.H;
        PanelStyle st = found.Cards.Length > 0 ? found.Cards[0].St : Fallback;
        _tip.DelayOverride = st.Delay; _tip.FadeInOverride = st.FadeIn; _tip.FadeOutOverride = st.FadeOut;
        _tip.Hover(found, "", hit.X, hit.Y, hit.Y + hit.H);
    }

    /// <summary>A press or a closed window: hide at once, no fade.</summary>
    public void Cut() { _tip.Cut(); _found = null; _gen = -1; }

    /// <summary>
    /// Builds the layout when the card has just become visible or the font or scale moved, and places it. False when nothing is visible.
    /// A frame that changes nothing allocates nothing.
    /// </summary>
    public bool Prepare(float vw, float vh)
    {
        CardSet? set = _set;
        if (set == null || _tip.Alpha <= 0f) return false;
        float scale = Metrics.Scale;
        int epoch = TextRig.AtlasEpoch;
        if (_tip.Tick() || _rebuild || set.Views == null || set.ViewEpoch != epoch || set.ViewScale != scale)
        {
            var views = new PanelView[set.Cards.Length];
            for (int i = 0; i < views.Length; i++)
            {
                string own = set.Cards[i].Item;
                uint oid = own.Length > 0 && uint.TryParse(own, System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint d) ? d : _subject;
                views[i] = PanelLayout.Build(set.Cards[i], scale, Face, oid == 0 ? default : SubjectOf(oid));
            }
            _rebuild = false;
            set.Views = views;
            set.ViewEpoch = epoch;
            set.ViewScale = scale;
        }
        CardPlacer.Place(set.Views!, _ex, _ey, _ew, _eh, vw, vh, scale, set.Cards.Length > 0 ? set.Cards[0].St : Fallback, _xs, _ys);
        return true;
    }

    private static int Hash(ReadOnlySpan<byte> s)
    {
        int h = 17;
        foreach (byte b in s) h = h * 31 + b;
        return h;
    }

    /// <summary>Matches in order: <c>token#row</c>, <c>token</c>, the image the element drew (<c>@item:hex</c>, <c>@look:...</c>), then the longest declared <c>prefix*</c> of the token. A key declared with no cards ends the search and is returned as it is.</summary>
    internal static CardSet? Lookup(in NuiHit hit)
    {
        Span<char> buf = stackalloc char[CardLimits.MaxKeyBytes + 16];
        ReadOnlySpan<byte> token = hit.Token();
        if (token.Length > 0)
        {
            int n = Encoding.UTF8.GetChars(token, buf);
            if (hit.Row >= 0 && n < buf.Length - 12)
            {
                buf[n] = '#';
                hit.Row.TryFormat(buf[(n + 1)..], out int w);
                if (CardRegistry.Find(buf[..(n + 1 + w)]) is { } byRow) return byRow;
            }
            if (CardRegistry.Find(buf[..n]) is { } byToken) return byToken;
        }
        ReadOnlySpan<byte> img = hit.Image();
        if (img.Length > 0)
        {
            CardSet? byImage = null;
            if (ItemKey(img, out uint oid))
            {
                "@item:".AsSpan().CopyTo(buf);
                oid.TryFormat(buf[6..], out int w, "x");
                byImage = CardRegistry.Find(buf[..(6 + w)]);
            }
            else if (img.StartsWith("@look:"u8))
            {
                int n = Encoding.UTF8.GetChars(img, buf);
                byImage = CardRegistry.Find(buf[..n]);
            }
            if (byImage != null) return byImage;
        }
        if (token.Length > 0) return CardRegistry.FindPrefix(buf[..Encoding.UTF8.GetChars(token, buf)]);
        return null;
    }

    /// <summary>True when <paramref name="img"/> is <c>@item:</c> and 1-8 hex digits (what follows is ignored, as the image loader ignores it).</summary>
    internal static bool ItemKey(ReadOnlySpan<byte> img, out uint oid)
    {
        oid = 0;
        if (!img.StartsWith("@item:"u8)) return false;
        int n = 0;
        for (int i = 6; i < img.Length && n < 8; i++, n++)
        {
            byte c = img[i];
            int d = c is >= (byte)'0' and <= (byte)'9' ? c - '0' : (c | 0x20) is >= (byte)'a' and <= (byte)'f' ? (c | 0x20) - 'a' + 10 : -1;
            if (d < 0) break;
            oid = oid << 4 | (uint)d;
        }
        return n > 0;
    }
}
