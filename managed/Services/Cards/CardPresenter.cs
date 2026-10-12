using System;
using System.Text;
using CX.Services.Nui;
using CX.Ui;

namespace CX.Services.Cards;

/// <summary>Where cards go: beside the element, never over it, and on screen.</summary>
internal static class CardPlacer
{
    /// <summary>
    /// Puts the cards of <paramref name="views"/> side by side in declared order, with their top edges level. The group goes to the
    /// right of the element at (<paramref name="ex"/>, <paramref name="ey"/>, <paramref name="ew"/>), to its left when it does not fit,
    /// and is clamped inside the <paramref name="vw"/> by <paramref name="vh"/> screen either way. A card's tab and overflow image
    /// are not part of the clamp except for the tab's height above the top.
    /// </summary>
    public static void Place(CardView[] views, float ex, float ey, float ew, float vw, float vh, float scale, Span<float> xs, Span<float> ys)
    {
        float gap = 8f * scale, total = 0f, maxH = 0f, tab = 0f;
        for (int i = 0; i < views.Length; i++)
        {
            total += views[i].W + (i > 0 ? gap : 0f);
            maxH = Math.Max(maxH, views[i].H);
            tab = Math.Max(tab, views[i].TabH);
        }
        float x0 = ex + ew + gap;
        if (x0 + total > vw) x0 = ex - gap - total;
        x0 = Window.ClampAxis(x0, total, vw);
        float y0 = Window.ClampAxis(ey - tab, maxH + tab, vh) + tab;
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
    internal Func<float, (GlyphAtlas A, float K)> Face = CardFaces.Resolve;
    internal Func<uint, CardSubject> SubjectOf = static _ => default;

    private readonly Tooltip _tip = new();
    private CardSet? _set, _found;
    private IntPtr _el;
    private int _row = -2, _imgLen, _imgHash, _gen = -1;
    private float _ex, _ey, _ew;
    private uint _subject;
    private readonly float[] _xs = new float[CardLimits.MaxCards], _ys = new float[CardLimits.MaxCards];

    public CardView[]? Views => _set?.Views;
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
                _el = hit.Element; _row = hit.Row; _imgHash = h; _imgLen = hit.Image().Length; _gen = CardRegistry.Gen; _found = found;
                _subject = found == null ? 0u : SubjectId(in hit, found);
            }
        }
        else { _el = IntPtr.Zero; _row = -2; _found = null; _gen = -1; }
        if (found == null) { _tip.Leave(); return; }
        _set = found;
        _ex = hit.X; _ey = hit.Y; _ew = hit.W;
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
        if (_tip.Tick() || set.Views == null || set.ViewEpoch != epoch || set.ViewScale != scale)
        {
            CardSubject subject = _subject == 0 ? default : SubjectOf(_subject);
            var views = new CardView[set.Cards.Length];
            for (int i = 0; i < views.Length; i++) views[i] = CardLayout.Build(set.Cards[i], scale, Face, subject);
            set.Views = views;
            set.ViewEpoch = epoch;
            set.ViewScale = scale;
        }
        CardPlacer.Place(set.Views!, _ex, _ey, _ew, vw, vh, scale, _xs, _ys);
        return true;
    }

    private static int Hash(ReadOnlySpan<byte> s)
    {
        int h = 17;
        foreach (byte b in s) h = h * 31 + b;
        return h;
    }

    /// <summary>The item a card is about: the hit's own <c>@item:</c> image, else the card's declared <c>item</c>.</summary>
    private static uint SubjectId(in NuiHit hit, CardSet set)
    {
        if (ItemKey(hit.Image(), out uint oid)) return oid;
        string declared = set.Cards[0].Item;
        return declared.Length > 0 && uint.TryParse(declared, System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint d) ? d : 0u;
    }

    /// <summary>Matches in order: <c>token#row</c>, <c>token</c>, then the image the element drew (<c>@item:hex</c>, <c>@look:...</c>).</summary>
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
            if (ItemKey(img, out uint oid))
            {
                "@item:".AsSpan().CopyTo(buf);
                oid.TryFormat(buf[6..], out int w, "x");
                return CardRegistry.Find(buf[..(6 + w)]);
            }
            if (img.StartsWith("@look:"u8))
            {
                int n = Encoding.UTF8.GetChars(img, buf);
                return CardRegistry.Find(buf[..n]);
            }
        }
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
