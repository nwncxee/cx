using CX.Framework;

namespace CX.Ui;

/// <summary>
/// A vertically scrolling box: a viewport rect, the height of the content laid out inside it, and the scroll
/// <see cref="Offset"/> the owner subtracts from its content y when drawing and adds to a pointer y when
/// hit-testing. Owns the scrollbar, the thumb drag, the track page-jump and the wheel step; the owner clips its
/// content to the viewport band with <see cref="VertexBatch.ClipY"/>, then calls <see cref="Draw"/> for the bar.
/// </summary>
public sealed class ScrollColumn
{
    /// <summary>Scrollbar gutter width (chat's <c>ScrollbarW</c>).</summary>
    public static float BarW => 8f * Metrics.Scale;
    /// <summary>Smallest thumb, so it stays grabbable when the content is very tall.</summary>
    public static float MinThumb => 24f * Metrics.Scale;
    /// <summary>Track alpha over whatever sits behind the column.</summary>
    private const float TrackA = 0.55f;

    /// <summary>This column wears the game's own scrollbar art: the gutter is NUI's width and the thumb sits
    /// between the two arrows. The owner sets it beside the skin it armed, so a refused skin stays flat.</summary>
    public bool Native;

    /// <summary>This column's gutter width: NUI's bar while <see cref="Native"/>, else <see cref="BarW"/>.</summary>
    public float Width => Native ? NativeChrome.BarWidth(Metrics.Scale) : BarW;

    /// <summary>The viewport rect (absolute px).</summary>
    public float X, Y, W, H;
    /// <summary>Total laid-out content height; the owner writes it after its layout pass.</summary>
    public float ContentH;
    /// <summary>Pixels scrolled from the content top, always within 0..<see cref="MaxOffset"/>.</summary>
    public float Offset;

    private bool _drag;
    private float _gy, _goff;   // thumb-grab anchor: pointer y + Offset at the grab

    public float MaxOffset => ContentH > H ? ContentH - H : 0f;
    public bool Overflows => H > 0f && ContentH > H;
    public bool Dragging => _drag;

    public void SetRect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; Clamp(); }

    public bool Contains(float mx, float my) => mx >= X && mx < X + W && my >= Y && my < Y + H;

    /// <summary>True when the pointer is over the scrollbar gutter (only meaningful while overflowing).</summary>
    public bool OverBar(float mx, float my)
        => Overflows && mx >= X + W - Width && mx < X + W && my >= Y && my < Y + H;

    /// <summary>Content y under a pointer y (the owner hit-tests its rows against this).</summary>
    public float ToContent(float screenY) => screenY - Y + Offset;
    /// <summary>Screen y of a content y (the owner draws its rows at this).</summary>
    public float ToScreen(float contentY) => contentY - Offset + Y;

    /// <summary>Thumb rect, or false when nothing scrolls. Proportional to the visible fraction, floored at
    /// <see cref="MinThumb"/>; top of the track at Offset 0, bottom at <see cref="MaxOffset"/>. A
    /// <see cref="Native"/> column takes the engine's geometry instead (<see cref="NativeChrome.ThumbRect"/>), so
    /// the drag and the art agree.</summary>
    public bool ThumbRect(out float top, out float height)
    {
        top = 0f; height = 0f;
        if (!Overflows) return false;
        if (Native)
        {
            float bw = Width;
            if (!NativeChrome.ThumbRect(X + W - bw, Y, X + W, Y + H, Offset, H, ContentH, out _, out float ny0, out _, out float ny1))
                return false;
            top = ny0; height = ny1 - ny0;
            return true;
        }
        height = H * H / ContentH;
        if (height < MinThumb) height = MinThumb;
        if (height > H) height = H;
        float max = MaxOffset;
        top = Y + (max > 0f ? Offset / max : 0f) * (H - height);
        return true;
    }

    public bool OverThumb(float mx, float my)
        => ThumbRect(out float t, out float h) && mx >= X + W - Width && mx < X + W && my >= t && my < t + h;

    /// <summary>Thumb travel for a <paramref name="thumbH"/>-tall thumb: the track less both arrows and the thumb's
    /// own padding while <see cref="Native"/>, else the plain viewport.</summary>
    private float Travel(float thumbH)
    {
        float t = Native ? H - 2f * Width - 2f * NativeChrome.PadY - thumbH : H - thumbH;
        return t > 0f ? t : 0f;
    }

    /// <summary>Press: the thumb starts a drag; the rest of the gutter pages toward the pointer. True when the
    /// press landed on the bar (the owner then does not hit-test its content).</summary>
    public bool MouseDown(float mx, float my)
    {
        if (!OverBar(mx, my)) return false;
        if (OverThumb(mx, my)) { _drag = true; _gy = my; _goff = Offset; return true; }
        ThumbRect(out float top, out float _);
        ScrollBy(my < top ? -H : H);
        return true;
    }

    /// <summary>Motion while the thumb is grabbed: absolute-anchor mapping from thumb travel to Offset.</summary>
    public bool MouseMove(float mx, float my)
    {
        if (!_drag) return false;
        if (ThumbRect(out float _, out float th))
        {
            float travel = Travel(th);
            Offset = travel > 0f ? _goff + (my - _gy) * MaxOffset / travel : 0f;
            Clamp();
        }
        return true;
    }

    /// <summary>Release: ends a thumb drag. True iff one was in progress.</summary>
    public bool MouseUp() { bool d = _drag; _drag = false; return d; }

    /// <summary>Scroll by pixels (positive = content moves up = Offset grows). Returns true if it moved.</summary>
    public bool ScrollBy(float dpx)
    {
        float before = Offset;
        Offset += dpx;
        Clamp();
        return Offset != before;
    }

    /// <summary>Wheel notch in SDL sign (positive = away from the user = toward the top).</summary>
    public bool MouseWheel(int dy, float step) => ScrollBy(-dy * step);

    public void Clamp()
    {
        float max = MaxOffset;
        Offset = Offset < 0f ? 0f : Offset > max ? max : Offset;
    }

    public void Reset() { Offset = 0f; _drag = false; }

    /// <summary>Draw the track + thumb in the gutter at the right edge (nothing when the content fits). Pure and
    /// zero-alloc, like every input here: pixels in, offset out.</summary>
    public void Draw(VertexBatch b, (float r, float g, float b, float a) thumb) => Draw(b, thumb, null);

    /// <summary>The bar in the gutter at the right edge, nothing when the content fits: the game's own trough,
    /// arrows and tiled thumb when <paramref name="skin"/> is given and this column is <see cref="Native"/> (lit
    /// while <paramref name="hot"/> or dragging), else the themed track and thumb. Zero-alloc.</summary>
    public void Draw(VertexBatch b, (float r, float g, float b, float a) thumb, Skin? skin, bool hot = false)
    {
        if (!Overflows) return;
        float x0 = X + W - Width;
        if (Native && skin != null)
        {
            NativeChrome.Scrollbar(skin, x0, Y, X + W, Y + H, Offset, H, ContentH, hot || _drag);
            return;
        }
        if (!ThumbRect(out float top, out float h)) return;
        PanelChrome.Fill(b, x0, Y, X + W, Y + H, Theme.Rgb(Role.Track), TrackA);
        b.Rect(x0, top, X + W, top + h, thumb.r, thumb.g, thumb.b, thumb.a);
    }

    /// <summary>Off-device proof: offset clamp, thumb geometry, wheel + drag + page mapping, and a zero-alloc draw.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var sc = new ScrollColumn();
        sc.SetRect(100f, 50f, 200f, 240f);
        sc.ContentH = 100f;
        check.Expect(!sc.Overflows && !sc.ThumbRect(out _, out _) && sc.MaxOffset == 0f, "content that fits: no thumb, no travel");
        check.Expect(!sc.MouseWheel(-1, 24f) && sc.Offset == 0f, "wheel on fitting content moves nothing");

        sc.ContentH = 60f * 24f;   // 60 rows of 24 px in a 10-row box
        check.Expect(sc.Overflows && sc.MaxOffset == 1440f - 240f, "60 rows overflow a 10-row box by 50 rows");
        check.Expect(sc.ThumbRect(out float t0, out float h0) && System.Math.Abs(t0 - 50f) < 0.01f && h0 >= MinThumb, "thumb at the track top at Offset 0");
        check.Expect(sc.MouseWheel(-1, 24f) && sc.Offset == 24f, "one notch down scrolls one step");
        check.Expect(sc.MouseWheel(1, 24f) && sc.Offset == 0f, "one notch up scrolls back");
        check.Expect(!sc.MouseWheel(1, 24f) && sc.Offset == 0f, "wheel past the top clamps and reports no move");
        sc.ScrollBy(99999f);
        check.Expect(sc.Offset == sc.MaxOffset, "scroll past the end clamps to MaxOffset");
        check.Expect(sc.ThumbRect(out float t1, out float h1) && System.Math.Abs(t1 + h1 - (50f + 240f)) < 0.01f, "thumb at the track bottom at MaxOffset");
        check.Expect(System.Math.Abs(sc.ToContent(50f + 10f) - (sc.Offset + 10f)) < 0.01f, "ToContent adds the offset");
        check.Expect(System.Math.Abs(sc.ToScreen(sc.Offset + 10f) - 60f) < 0.01f, "ToScreen subtracts it");

        // Thumb drag: grab at the bottom, drag up the full travel -> Offset 0.
        float bx = 100f + 200f - 2f;
        check.Expect(!sc.MouseDown(150f, 100f), "press off the gutter is not the bar's");
        check.Expect(sc.MouseDown(bx, t1 + 2f) && sc.Dragging, "press on the thumb grabs it");
        check.Expect(sc.MouseMove(bx, t1 + 2f - (240f - h1)), "drag consumed");
        check.Expect(sc.Offset == 0f, "dragging the thumb to the track top scrolls to 0");
        sc.MouseMove(bx, t1 + 2f - (240f - h1) / 2f);
        check.Expect(System.Math.Abs(sc.Offset - sc.MaxOffset / 2f) < 1f, "half the travel is half the range");
        check.Expect(sc.MouseUp() && !sc.Dragging && !sc.MouseUp(), "release ends the drag once");

        // Track page-jump: below the thumb pages down by one viewport, above pages up.
        sc.Offset = 0f;
        check.Expect(sc.MouseDown(bx, 50f + 235f) && sc.Offset == 240f, "press below the thumb pages down one box");
        sc.MouseUp();
        check.Expect(sc.MouseDown(bx, 50f + 1f) && sc.Offset == 0f, "press above the thumb pages up");
        sc.MouseUp();

        // Draw allocates nothing (AC: the panel draws per frame).
        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        var b = new VertexBatch(atlas.SolidU, atlas.SolidV);
        sc.Draw(b, (1f, 1f, 1f, 1f));
        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { b.Clear(); sc.Draw(b, (1f, 1f, 1f, 1f)); }
        long delta = System.GC.GetAllocatedBytesForCurrentThread() - before;
        check.Expect(delta == 0, $"100 draws allocate nothing ({delta} bytes)");

        NativeGeometry(check);

        log.Info(check.Ok ? "ScrollColumn.SelfCheck: passed" : "ScrollColumn.SelfCheck: FAILED");
    }

    /// <summary>Oracle: the engine's bar worked out by hand at GUI scale 1 -- an 18 px gutter, square arrows at both
    /// ends, 4/2 padding -- over a 240 px box holding 1440 px of content: a 30 px thumb at (282..300, 70..100), 170 px
    /// of travel, and its bottom two px above the down arrow when scrolled out.</summary>
    private static void NativeGeometry(Checker check)
    {
        float saved = Metrics.Scale;
        Metrics.Scale = 1f;
        try
        {
            var sc = new ScrollColumn { Native = true };
            sc.SetRect(100f, 50f, 200f, 240f);
            sc.ContentH = 1440f;
            check.Expect(sc.Width == 18f && BarW == 8f, $"a native column's gutter is NUI's 18 px, not the flat 8 ({sc.Width})");
            check.Expect(sc.ThumbRect(out float t0, out float h0) && t0 == 70f && h0 == 30f,
                         $"the thumb clears the up arrow and is a sixth of the track ({t0}, {h0})");
            check.Expect(sc.OverBar(290f, 100f) && !sc.OverBar(281f, 100f), "the gutter starts 18 px in from the right edge");
            check.Expect(sc.OverThumb(290f, 71f) && !sc.OverThumb(290f, 101f), "the thumb hit-tests over its native rect");
            check.Expect(sc.MouseDown(290f, 71f) && sc.Dragging, "a press on the native thumb grabs it");
            sc.MouseMove(290f, 71f + 170f);
            check.Expect(sc.Offset == sc.MaxOffset, $"170 px of drag is the whole range ({sc.Offset} of {sc.MaxOffset})");
            check.Expect(sc.ThumbRect(out float t1, out float h1) && t1 + h1 == 270f, $"scrolled out: the thumb ends 2 px above the down arrow ({t1 + h1})");
            sc.MouseUp();
            sc.Offset = 0f;
            check.Expect(sc.MouseDown(290f, 285f) && sc.Offset == 240f, "a press in the down arrow pages one box");
            sc.MouseUp();
        }
        finally { Metrics.Scale = saved; }
    }
}
