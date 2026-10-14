using System;
using CX.Framework;

namespace CX.Ui;

/// <summary>The native look's shared art: the stock <c>nui_skin.tml</c> parts with the insets the engine passes in
/// code, so every CX window wears what NUI wears. A window builds its own <see cref="Skin"/> from <see cref="NewSkin"/>
/// (its extra parts after these); a server re-points a part with <c>ui.skin.&lt;part&gt;</c>.</summary>
public static class NativeChrome
{
    public const int Frame = 0, Header = 1, Tab = 2, Button = 3, ButtonHot = 4, ButtonOn = 5, ButtonOnHot = 6, Close = 7, Edit = 8,
                     Trough = 9, Thumb = 10, Up = 11, Down = 12, Slider = 13, Count = 14;

    /// <summary>The policy prefix every native part answers to.</summary>
    public const string SkinName = "ui";

    /// <summary><c>window.scrollbar_size_x</c> at GUI scale 1: the only scrollbar metric the GUI scale grows.</summary>
    public const float BarPx = 18f;
    /// <summary><c>scrollv.padding</c>: the thumb's inset from the trough, never scaled.</summary>
    public const float PadX = 4f, PadY = 2f;
    /// <summary>The thumb's floor: one whole rib of <c>nui_scrollv</c>.</summary>
    public const float MinThumb = 16f;

    private static readonly SkinPart[] Common =
    {
        new("frame", "nui_windowh", 3, 3, 3, 3),
        new("header", "nui_windowh", 3, 3, 3, 3),
        new("tab", "nui_header", 5, 3, 3, 3),
        new("button", "nui_button_n", 3, 3, 3, 3),
        new("buttonhot", "nui_button_h", 7, 7, 7, 7),
        new("buttonon", "nui_button_a", 3, 3, 3, 3),
        new("buttononhot", "nui_button_ah", 7, 7, 7, 7),
        new("close", "nui_close"),
        new("edit", "nui_windowh", 3, 3, 3, 3),
        new("trough", "nui_windowv", 3, 3, 3, 3),
        new("thumb", "nui_scrollv", SrcX: 3, SrcY: 0, SrcW: 10, SrcH: 16, TileY: true),
        new("up", "nui_cnt_up"),
        new("down", "nui_cnt_down"),
        new("slider", "nui_slider"),
    };

    /// <summary>A skin of the common parts, indices <see cref="Frame"/>..<see cref="Down"/>, then
    /// <paramref name="extra"/> from <see cref="Count"/>.</summary>
    public static Skin NewSkin(params SkinPart[] extra)
    {
        var all = new SkinPart[Count + extra.Length];
        Common.CopyTo(all, 0);
        extra.CopyTo(all, Count);
        return new Skin(SkinName, all);
    }

    /// <summary>The bar's width at <paramref name="scale"/>, whole px.</summary>
    public static float BarWidth(float scale) => MathF.Round(BarPx * scale);

    /// <summary>The untinted tint at <paramref name="alpha"/>: what a part faded with its window draws with
    /// (<see cref="Skin.Flush"/>'s alpha is the whole batch's, so a fading element passes its own).</summary>
    public static uint White(float alpha)
        => 0xffffff00u | (uint)Math.Clamp((int)(alpha * 255f + 0.5f), 0, 255);  // not-an-offset: an rgba tint

    /// <summary><c>nui_slider</c>'s width over its height (12x16): the slider cursor is stretched whole into that shape.</summary>
    public const float SliderAspect = 0.75f;

    /// <summary>The thumb rect of a vertical bar at (<paramref name="x0"/>..<paramref name="x1"/>,
    /// <paramref name="y0"/>..<paramref name="y1"/>), arrows square at both ends; false when everything fits.</summary>
    public static bool ThumbRect(float x0, float y0, float x1, float y1, float top, float view, float total,
                                 out float tx0, out float ty0, out float tx1, out float ty1)
    {
        float w = x1 - x0, t0 = y0 + w, th = y1 - y0 - 2f * w;
        tx0 = x0 + PadX; tx1 = x1 - PadX; ty0 = ty1 = t0;
        if (total <= view || view <= 0f || th <= 0f) return false;
        float h = MathF.Max(MinThumb, th * view / total - 2f * PadY);
        float travel = MathF.Max(0f, th - 2f * PadY - h), max = total - view;
        ty0 = t0 + PadY + travel * Math.Clamp(top / max, 0f, 1f);
        ty1 = ty0 + h;
        return true;
    }

    /// <summary>nuklear's <c>scroll_step</c>: an arrow press moves a tenth of the view.</summary>
    public const float ArrowStep = 0.1f;

    /// <summary>Which arrow of a vertical bar <paramref name="py"/> presses: -1 up, 1 down, 0 the track. NUI steps the
    /// view on an arrow; it never drags from one.</summary>
    public static int Arrow(float y0, float y1, float w, float py) => py < y0 + w ? -1 : py >= y1 - w ? 1 : 0;

    /// <summary>The trough, both arrows and, when the content overflows, the tiled thumb (white
    /// <paramref name="hot"/>, the header-label grey idle).</summary>
    public static void Scrollbar(Skin skin, float x0, float y0, float x1, float y1, float top, float view, float total, bool hot)
    {
        float w = x1 - x0;
        skin.Draw(Trough, x0, y0, x1, y1);
        skin.Draw(Up, x0, y0, x1, y0 + w);
        skin.Draw(Down, x0, y1 - w, x1, y1);
        if (ThumbRect(x0, y0, x1, y1, top, view, total, out float tx0, out float ty0, out float tx1, out float ty1))
            skin.Draw(Thumb, tx0, ty0, tx1, ty1, hot ? 0xffffffffu : 0xc8c8c8ffu);  // not-an-offset: rgba tints
    }

    /// <summary>Off-device. Oracle: the engine's scrollbar geometry as hand-written numbers (18 px bar, 4/2 padding,
    /// square arrows) -- a 10 px thumb, a half view's thumb at the track's top, a scrolled-out one at its bottom.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(BarWidth(1f) == 18f && BarWidth(1.5f) == 27f, "the bar is 18 px at scale 1, grown by the GUI scale");
        check.Expect(!ThumbRect(0f, 0f, 18f, 236f, 0f, 100f, 100f, out _, out _, out _, out _), "no thumb when everything fits");
        check.Expect(ThumbRect(0f, 0f, 18f, 236f, 0f, 100f, 200f, out float a0, out float b0, out float a1, out float b1)
                     && a0 == 4f && a1 == 14f && b0 == 20f && b1 == 116f, "half a view: a 10 px wide, 96 px thumb under the up arrow");
        ThumbRect(0f, 0f, 18f, 236f, 100f, 100f, 200f, out _, out _, out _, out float e1);
        check.Expect(e1 == 216f, "scrolled out: the thumb ends padding above the down arrow");
        ThumbRect(0f, 0f, 18f, 236f, 0f, 1f, 10000f, out _, out float m0, out _, out float m1);
        check.Expect(m1 - m0 == 16f, "a long list's thumb keeps one whole rib");
        check.Expect(Arrow(0f, 236f, 18f, 17f) == -1 && Arrow(0f, 236f, 18f, 18f) == 0 && Arrow(0f, 236f, 18f, 218f) == 1, "the arrows are the square ends");
        log.Info(check.Ok ? "NativeChrome.SelfCheck: passed" : "NativeChrome.SelfCheck: FAILED");
    }
}
