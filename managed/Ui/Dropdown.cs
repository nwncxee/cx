using System;
using CX.Framework;

namespace CX.Ui;

/// <summary>
/// The enumerated-choice control's open list: click the closed box, a list of choices drops beneath it (above
/// it when it would run past <c>clampBottom</c>), pick one, or click anywhere else to close. The list is a
/// <see cref="ScrollColumn"/> capped at <see cref="MaxVisible"/> rows, scrolled by wheel and thumb. One instance
/// serves every dropdown row of a panel; the closed box is the owner's to draw. Pure and zero-alloc in draw.
/// </summary>
public sealed class Dropdown
{
    /// <summary>Rows the open list shows at most before it scrolls.</summary>
    public const int MaxVisible = 8;
    /// <summary>The hairline around the open list and under the search row: the themed frame line.</summary>
    private static uint FrameRgb => Theme.Rgb(Role.Tx2);
    private const float FrameA = 1f;

    public bool Open { get; private set; }
    /// <summary>The choices shown; the caller's array, never copied.</summary>
    public string[] Choices = Array.Empty<string>();
    /// <summary>Index drawn as the current value. Every index given out, here and as <c>picked</c>, is into
    /// <see cref="Choices"/>, never into the filtered rows.</summary>
    public int Selected;
    /// <summary>Index under the pointer, or -1.</summary>
    public int Hover = -1;
    /// <summary>The owner's handle on which row this list belongs to (a <see cref="KnobPanel.Row"/> in the panel).</summary>
    public object? Tag;
    /// <summary>Item height, set at <see cref="OpenAt"/>.</summary>
    public float ItemH { get; private set; }
    /// <summary>The list viewport; its rect is the list rect (the search row, when any, sits above it).</summary>
    public readonly ScrollColumn List = new();
    /// <summary>Opened with a search row above the list: typed text filters the choices by substring.</summary>
    public bool Searching { get; private set; }
    /// <summary>The search text; "" shows every choice.</summary>
    public string Filter { get; private set; } = "";
    private const string SearchHint = "type to search";
    private int[] _vis = Array.Empty<int>();
    private int _n;

    public float X => List.X;
    public float Y => List.Y - SearchH;
    public float W => List.W;
    public float H => List.H + SearchH;
    private float SearchH => Searching ? ItemH : 0f;
    /// <summary>Visible choices after the filter.</summary>
    public int Shown => _n;
    /// <summary>The choice index of visible row <paramref name="i"/>.</summary>
    public int ChoiceAt(int i) => _vis[i];

    /// <summary>Open the list under the anchor box (its left x, bottom y and width), flipping above
    /// <paramref name="anchorTop"/> when the list would end past <paramref name="clampBottom"/>. The list
    /// scrolls so the selected item is visible.</summary>
    public void OpenAt(string[] choices, int selected, object? tag, float anchorX, float anchorTop, float anchorBottom,
                       float w, float itemH, float clampBottom = float.MaxValue, bool search = false)
    {
        Choices = choices; Selected = selected; Tag = tag; ItemH = itemH; Hover = -1;
        Searching = search; Filter = "";
        if (_vis.Length < choices.Length) _vis = new int[choices.Length];
        int n = choices.Length;
        int shown = n < MaxVisible ? n : MaxVisible;
        float sh = search ? itemH : 0f;
        float h = shown * itemH;
        float y = anchorBottom;
        if (y + h + sh > clampBottom && anchorTop - h - sh >= 0f) y = anchorTop - h - sh;
        List.Reset();
        List.SetRect(anchorX, y + sh, w, h);
        Refilter();
        if ((uint)selected < (uint)n) { List.Offset = selected * itemH - h * 0.5f + itemH * 0.5f; List.Clamp(); }
        Open = true;
    }

    /// <summary>Rebuild the visible rows from <see cref="Filter"/> (a substring match, case-folded); an empty
    /// filter shows every choice. Cold: only typing gets here.</summary>
    private void Refilter()
    {
        _n = 0;
        for (int i = 0; i < Choices.Length; i++)
            if (Filter.Length == 0 || Choices[i].Contains(Filter, StringComparison.OrdinalIgnoreCase)) _vis[_n++] = i;
        List.ContentH = _n * ItemH;
        List.Offset = 0f; List.Clamp();
        Hover = -1;
    }

    /// <summary>Typed text while searching: appended to the filter (printable only, capped at 32).</summary>
    public void Type(string s)
    {
        if (!Open || !Searching) return;
        foreach (char c in s)
            if (Filter.Length < 32 && c >= ' ' && c != '\n' && c != '\r') Filter += c;
        Refilter();
    }

    public void Backspace()
    {
        if (!Open || !Searching || Filter.Length == 0) return;
        Filter = Filter.Substring(0, Filter.Length - 1);
        Refilter();
    }

    /// <summary>Enter while searching: the one visible choice, or the choice the filter names exactly ("Arial" among
    /// Arial / Arial Black), is picked (its index); else -1 and the list stays.</summary>
    public int Accept()
    {
        if (!Open || !Searching) return -1;
        int i = _n == 1 ? _vis[0] : -1;
        for (int k = 0; i < 0 && k < _n; k++) if (Choices[_vis[k]].Equals(Filter, StringComparison.OrdinalIgnoreCase)) i = _vis[k];
        if (i < 0) return -1;
        Selected = i; Close();
        return i;
    }

    public void Close() { Open = false; Hover = -1; Tag = null; Searching = false; Filter = ""; List.MouseUp(); }

    public bool Contains(float mx, float my)
        => Open && (List.Contains(mx, my) || (Searching && mx >= X && mx < X + W && my >= Y && my < List.Y));
    public bool Dragging => List.Dragging;

    /// <summary>Screen top of item <paramref name="i"/> (may lie outside the list while scrolled).</summary>
    public float ItemTop(int i) => List.ToScreen(i * ItemH);

    /// <summary>Visible-row index under the pointer (the gutter and the search row excluded), or -1.</summary>
    public int ItemAt(float mx, float my)
    {
        if (!Open || !List.Contains(mx, my) || List.OverBar(mx, my)) return -1;
        int i = (int)(List.ToContent(my) / ItemH);
        return (uint)i < (uint)_n ? i : -1;
    }

    /// <summary>Press while open: always consumed. An item press picks it (<paramref name="picked"/> = its choice
    /// index) and closes; the gutter scrolls; the search row is inert; anything else closes with nothing picked.
    /// False when closed.</summary>
    public bool MouseDown(float mx, float my, out int picked)
    {
        picked = -1;
        if (!Open) return false;
        if (List.MouseDown(mx, my)) return true;
        if (Searching && Contains(mx, my) && my < List.Y) return true;
        int i = ItemAt(mx, my);
        if (i >= 0) { picked = _vis[i]; Selected = picked; }
        Close();
        return true;
    }

    /// <summary>Motion while open: the thumb drag, else the hover highlight. True when something to redraw changed.</summary>
    public bool MouseMove(float mx, float my)
    {
        if (!Open) return false;
        if (List.MouseMove(mx, my)) return true;
        int h = ItemAt(mx, my);
        if (h == Hover) return false;
        Hover = h;
        return true;
    }

    public bool MouseUp() => List.MouseUp();

    /// <summary>Wheel while open scrolls the list one item per notch, wherever the pointer is.</summary>
    public bool MouseWheel(int dy)
    {
        if (!Open) return false;
        bool moved = List.MouseWheel(dy, ItemH);
        Hover = -1;   // the rows moved under a still pointer; the next motion re-hovers
        return moved;
    }

    /// <summary>The closed box the owner draws: a value box with the current choice and a ▾ at its right edge.</summary>
    public static void DrawBox(VertexBatch b, GlyphAtlas atlas, float x0, float y0, float x1, float y1, float baseline,
                               string value, (float r, float g, float b, float a) bg, uint textRgb, float alpha = 1f)
    {
        b.Rect(x0, y0, x1, y1, bg.r, bg.g, bg.b, bg.a * alpha);
        float arrowW = atlas.TryGet(0x25BE, out Glyph ag) ? ag.Advance : 0f;
        UiText.Draw(b, atlas, value, x0 + 4f, baseline, textRgb, x1 - arrowW - 6f, alpha);
        if (arrowW > 0f)
            b.Glyph(ag, x1 - arrowW - 3f, baseline, ((textRgb >> 16) & 0xFF) / 255f, ((textRgb >> 8) & 0xFF) / 255f, (textRgb & 0xFF) / 255f, 0.8f * alpha);
    }

    /// <summary>Draw the open list on top of everything: frame, body, the selected + hovered fills, the
    /// visible items (clipped to the box) and the scrollbar. With <paramref name="skin"/> given the list wears the
    /// game's own window art (the search row its edit box) in place of the frame and body fills.</summary>
    public void Draw(VertexBatch b, GlyphAtlas atlas, float baseDrop,
                     (float r, float g, float b, float a) bg, (float r, float g, float b, float a) active, uint textRgb,
                     Skin? skin = null)
    {
        if (!Open) return;
        List.Native = skin != null;
        float x0 = List.X, y0 = List.Y, x1 = List.X + List.W, y1 = List.Y + List.H;
        uint frameRgb = FrameRgb;
        float fr = ((frameRgb >> 16) & 0xFF) / 255f, fg = ((frameRgb >> 8) & 0xFF) / 255f, fb = (frameRgb & 0xFF) / 255f;
        if (skin != null) skin.Draw(NativeChrome.Frame, x0 - 1f, Y - 1f, x1 + 1f, y1 + 1f);
        else
        {
            b.Rect(x0 - 1f, Y - 1f, x1 + 1f, y1 + 1f, fr, fg, fb, FrameA);
            b.Rect(x0, Y, x1, y1, bg.r, bg.g, bg.b, 0.98f);
        }
        float textClip = x1 - (List.Overflows ? List.Width : 0f) - 2f;
        float vpad = (ItemH - atlas.LineHeight) / 2f;
        if (Searching)
        {
            if (skin != null) skin.Draw(NativeChrome.Edit, x0, Y, x1, y0);
            else b.Rect(x0, Y, x1, y0, active.r, active.g, active.b, active.a * 0.25f);
            b.Rect(x0, y0 - 1f, x1, y0, fr, fg, fb, FrameA);
            bool hint = Filter.Length == 0;
            float pen = UiText.Draw(b, atlas, hint ? SearchHint : Filter, x0 + 4f, Y + baseDrop + vpad, textRgb, x1 - 4f, hint ? 0.45f : 1f);
            if (!hint) UiText.Draw(b, atlas, "_", pen, Y + baseDrop + vpad, textRgb, x1 - 4f);
        }
        b.ClipY(y0, y1);
        for (int i = 0; i < _n; i++)
        {
            float top = ItemTop(i);
            if (top + ItemH <= y0 || top >= y1) continue;
            int c = _vis[i];
            if (c == Selected) b.Rect(x0, top, textClip + 2f, top + ItemH, active.r, active.g, active.b, active.a);
            else if (i == Hover) b.Rect(x0, top, textClip + 2f, top + ItemH, active.r, active.g, active.b, active.a * 0.55f);
            UiText.Draw(b, atlas, Choices[c], x0 + 4f, top + baseDrop + vpad, textRgb, textClip);
        }
        b.ClearClip();
        List.Draw(b, active, skin);
    }

    /// <summary>Off-device proof: a 27-entry list opens capped at <see cref="MaxVisible"/> rows, scrolls by wheel
    /// and thumb, picks the item under the pointer offset-correctly, closes on pick and on an outside press,
    /// flips above a short viewport, and draws without allocating.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var sizes = new string[27];
        for (int i = 0; i < 27; i++) sizes[i] = (6 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var d = new Dropdown();
        const float ih = 24f;
        check.Expect(!d.MouseDown(0f, 0f, out _) && !d.MouseMove(0f, 0f) && !d.MouseWheel(1), "closed: nothing consumed");

        d.OpenAt(sizes, 0, "font", 100f, 200f, 220f, 120f, ih, 1000f);
        check.Expect(d.Open && d.Y == 220f && d.H == MaxVisible * ih, "27 entries open below the anchor at 8 visible rows");
        check.Expect(d.List.Overflows && d.List.Offset == 0f, "the list overflows and starts at the top for item 0");
        check.Expect(d.ItemAt(110f, 220f + ih * 2 + 1f) == 2, "item hit-test at offset 0");
        check.Expect(d.MouseWheel(-1) && d.List.Offset == ih, "one notch scrolls one item");
        check.Expect(d.ItemAt(110f, 220f + ih * 2 + 1f) == 3, "item hit-test follows the scroll offset");
        check.Expect(d.ItemAt(100f + 120f - 2f, 230f) == -1, "the gutter is not an item");
        // Thumb drag to the bottom -> the last item is in view.
        check.Expect(d.List.ThumbRect(out float tt, out float th), "thumb shown");
        check.Expect(d.MouseDown(100f + 120f - 2f, tt + 1f, out int p0) && p0 < 0 && d.Open && d.Dragging, "thumb press grabs, picks nothing, stays open");
        d.MouseMove(100f + 120f - 2f, tt + 1f + d.H);
        check.Expect(d.MouseUp() && d.List.Offset == d.List.MaxOffset, "thumb dragged to the end shows the tail");
        check.Expect(d.ItemAt(110f, d.Y + d.H - 1f) == 26, "the last item sits at the list bottom");
        check.Expect(d.MouseMove(110f, d.Y + d.H - 1f) && d.Hover == 26, "hover tracks the item under the pointer");
        check.Expect(!d.MouseMove(111f, d.Y + d.H - 1f), "motion within the same item reports no change");
        check.Expect(d.MouseDown(110f, d.Y + d.H - 1f, out int picked) && picked == 26 && d.Selected == 26 && !d.Open, "picking the last item closes with its index");

        // Outside press closes, consumed, nothing picked.
        d.OpenAt(sizes, 26, "font", 100f, 200f, 220f, 120f, ih, 1000f);
        check.Expect(d.List.Offset == d.List.MaxOffset, "opening on the last item scrolls it into view");
        check.Expect(d.MouseDown(5f, 5f, out int none) && none < 0 && !d.Open, "outside press closes, consumed, picks nothing");

        // Short viewport: the list flips above the anchor.
        d.OpenAt(sizes, 0, "font", 100f, 200f, 220f, 120f, ih, 300f);
        check.Expect(d.Open && System.Math.Abs(d.Y - (200f - MaxVisible * ih)) < 0.01f, "the list flips above the anchor when it would run past the clamp");
        d.Close();

        // Short list: no scroll, height = n rows.
        var three = new[] { "a", "b", "c" };
        d.OpenAt(three, 1, null, 0f, 0f, 20f, 80f, ih);
        check.Expect(d.H == 3 * ih && !d.List.Overflows, "3 entries: 3 rows, no scrollbar");
        check.Expect(d.MouseDown(10f, 20f + ih * 2 + 1f, out int c) && c == 2, "pick the third");

        // Draw allocates nothing.
        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        for (char ch = '0'; ch <= '9'; ch++) atlas.SetTestGlyph(ch, 6f);
        var b = new VertexBatch(atlas.SolidU, atlas.SolidV);
        d.OpenAt(sizes, 5, "font", 100f, 200f, 220f, 120f, ih, 1000f);
        d.MouseMove(110f, 230f);
        d.Draw(b, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), (0.2f, 0.3f, 0.5f, 1f), 0xFFFFFF);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { b.Clear(); d.Draw(b, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), (0.2f, 0.3f, 0.5f, 1f), 0xFFFFFF); }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        check.Expect(delta == 0, $"100 open-list draws allocate nothing ({delta} bytes)");
        check.Expect(b.VertexCount > 0, "the draw emitted geometry");
        d.Close();

        // Search: the row sits above the list, typing filters by substring, picks map back to choice indices.
        d.OpenAt(sizes, 20, "font", 100f, 200f, 220f, 120f, ih, 1000f, search: true);
        check.Expect(d.Searching && d.Y == 220f && d.List.Y == 220f + ih && d.H == (MaxVisible + 1) * ih && d.Shown == 27, "a searchable list opens with the search row above 8 rows, every choice shown");
        check.Expect(d.Contains(110f, 221f) && d.ItemAt(110f, 221f) == -1, "the search row is inside the list but is no item");
        check.Expect(d.MouseDown(110f, 221f, out int sp) && sp < 0 && d.Open, "a press on the search row keeps the list open, picks nothing");
        d.Type("2");
        check.Expect(d.Shown == 12 && d.ChoiceAt(0) == 6 && d.ChoiceAt(11) == 26 && d.List.Offset == 0f, $"'2' leaves the twelve sizes containing a 2, from the top ({d.Shown})");
        check.Expect(d.Accept() < 0 && d.Open, "Enter with several matches picks nothing");
        d.Type("0");
        check.Expect(d.Shown == 1 && d.ChoiceAt(0) == 14, "'20' is one size");
        d.Backspace(); d.Backspace(); d.Type("1"); d.Type("2");
        check.Expect(d.Shown == 1 && d.Accept() == 6 && !d.Open, "'12' names one size exactly: Enter picks it");
        var arial = new[] { "Arial", "Arial Black", "Arial Narrow", "Courier", "Georgia", "Impact", "Tahoma", "Times", "Verdana" };
        d.OpenAt(arial, 0, "face", 100f, 200f, 220f, 120f, ih, 1000f, search: true);
        d.Type("arial");
        check.Expect(d.Shown == 3 && d.Accept() == 0 && !d.Open, "an exact name among wider matches is what Enter picks");
        d.OpenAt(sizes, 20, "font", 100f, 200f, 220f, 120f, ih, 1000f, search: true);
        d.Type("2");
        check.Expect(d.MouseDown(110f, d.List.Y + ih + 1f, out int p2) && p2 == 14 && d.Selected == 14 && !d.Open, "the second visible row is choice 14 (20)");
        d.OpenAt(sizes, 0, "font", 100f, 200f, 220f, 120f, ih, 1000f, search: true);
        d.Type("3"); d.Type("3");
        check.Expect(d.Shown == 0, "no size contains '33'");
        d.Backspace();
        check.Expect(d.Filter == "3" && d.Shown == 5, "backspace re-widens the filter");
        d.Type("2");
        check.Expect(d.Shown == 1 && d.Accept() == 26 && !d.Open && d.Selected == 26, "Enter on the one match picks it");
        d.OpenAt(sizes, 0, "font", 100f, 200f, 220f, 120f, ih, 1000f, search: true);
        d.Type("9");
        b.Clear(); d.Draw(b, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), (0.2f, 0.3f, 0.5f, 1f), 0xFFFFFF);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { b.Clear(); d.Draw(b, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), (0.2f, 0.3f, 0.5f, 1f), 0xFFFFFF); }
        delta = GC.GetAllocatedBytesForCurrentThread() - before;
        check.Expect(delta == 0, $"100 filtered draws allocate nothing ({delta} bytes)");
        d.Close();
        check.Expect(!d.Searching && d.Filter.Length == 0, "close drops the search state");

        log.Info(check.Ok ? "Dropdown.SelfCheck: passed" : "Dropdown.SelfCheck: FAILED");
    }
}
