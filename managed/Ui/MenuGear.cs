using System;
using CX.Framework;

namespace CX.Ui;

/// <summary>
/// The settings gear: a glyph-atlas sprite -- DejaVu's ⚙ from an icon-only face -- in the lower-left corner,
/// scaling with the game's UI scale; it brightens and turns while hovered, and a press opens the settings panel.
/// Drawn the same way at the main menu and under the in-game Options panel (<c>GameGate.OptionsOpen</c>). Pure:
/// layout, hover and the vertex emit, no GL or engine reads; <see cref="UiHostService"/> owns its window and draw.
/// </summary>
public sealed class MenuGear
{
    /// <summary>Corner inset in design px (GUI scale 1), per spec §3 Doors 2.</summary>
    public const float InsetPx = 24f;
    /// <summary>The face that makes the sprite 64 design px tall: DejaVu's ⚙ box is 1523 of the font's 2384
    /// stbtt height units, 0.639 em, so 64 px of gear is a 100 px face.</summary>
    public const float FacePx = 100f;
    public const int Codepoint = 0x2699;  // not-an-offset: a Unicode codepoint
    /// <summary>Spin while hovered, radians per second, and how fast the spin and the brightness settle.</summary>
    public const float SpinRate = 1.2f, Settle = 8f;
    /// <summary>Idle tint (warm, a little dim) and the hovered tint (white).</summary>
    public const float IdleR = 0.86f, IdleG = 0.76f, IdleB = 0.56f, IdleA = 0.85f;

    /// <summary>The sprite's rect in viewport px (y-down), from the last <see cref="Layout"/>.</summary>
    public float X, Y, W, H;
    /// <summary>Set by the input thread from pointer motion; read by the animation on the render thread.</summary>
    public volatile bool Hovered;
    public float Angle, Spin, Bright;

    /// <summary>Pin the glyph's box <paramref name="scale"/>-scaled inset into the lower-left of a
    /// <paramref name="vpW"/>×<paramref name="vpH"/> viewport. The glyph is already baked at the scaled face,
    /// so its W/H are the on-screen size; only the inset scales here.</summary>
    public void Layout(float vpW, float vpH, float scale, in Glyph g)
    {
        float inset = InsetPx * scale;
        W = g.W; H = g.H;
        X = inset;
        Y = vpH - inset - H;
    }

    /// <summary>The face px for a GUI scale: <see cref="FacePx"/> × scale, whole pixels.</summary>
    public static float FaceFor(float scale) => MathF.Round(FacePx * (scale < 1f ? 1f : scale));

    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;

    /// <summary>The badge's side as a fraction of the gear's, and its centre on the gear's box, from the top-left.</summary>
    public const float BadgeFrac = 0.36f, BadgeAtX = 0.84f, BadgeAtY = 0.16f;
    /// <summary>The badge's disc: a warm red under a white mark.</summary>
    public const float BadgeR = 0.82f, BadgeG = 0.22f, BadgeB = 0.16f;
    /// <summary>The mark's height as a fraction of the badge's.</summary>
    public const float MarkFrac = 0.62f;
    /// <summary>The disc codepoint, an icon row every face carries.</summary>
    public const int DiscCodepoint = 0x25CF;  // not-an-offset: a Unicode codepoint

    /// <summary>The badge's box: a square on the gear's upper-right shoulder.</summary>
    public (float X0, float Y0, float X1, float Y1) Badge
    {
        get
        {
            float h = W * BadgeFrac * 0.5f, cx = X + W * BadgeAtX, cy = Y + H * BadgeAtY;
            return (cx - h, cy - h, cx + h, cy + h);
        }
    }

    /// <summary>Emit the badge: <paramref name="disc"/> stretched over its box, <paramref name="mark"/> centred on it.</summary>
    public void EmitBadge(VertexBatch b, in Glyph disc, in Glyph mark)
    {
        (float x0, float y0, float x1, float y1) = Badge;
        b.Tex(x0, y0, x1, y1, disc.U0, disc.V0, disc.U1, disc.V1, BadgeR, BadgeG, BadgeB, 1f);
        if (mark.H <= 0) return;
        float h = (y1 - y0) * MarkFrac, w = h * mark.W / mark.H, cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
        b.Tex(cx - w * 0.5f, cy - h * 0.5f, cx + w * 0.5f, cy + h * 0.5f, mark.U0, mark.V0, mark.U1, mark.V1, 1f, 1f, 1f, 1f);
    }

    /// <summary>Advance the hover animation by <paramref name="dt"/> seconds: the spin rate and the brightness ease
    /// toward their hovered/idle targets, and the angle integrates the spin (it keeps its resting angle).</summary>
    public void Tick(float dt)
    {
        if (dt > 0.1f) dt = 0.1f;
        float k = 1f - MathF.Exp(-Settle * dt);
        float target = Hovered ? 1f : 0f;
        Bright += (target - Bright) * k;
        Spin += (target * SpinRate - Spin) * k;
        Angle += Spin * dt;
        if (Angle > MathF.PI * 2f) Angle -= MathF.PI * 2f;
    }

    /// <summary>Emit the sprite: one rotated glyph quad, tinted between the idle and hovered colours.</summary>
    public void Emit(VertexBatch b, in Glyph g)
    {
        float t = Bright;
        b.GlyphSprite(g, X + W * 0.5f, Y + H * 0.5f, Angle,
            IdleR + (1f - IdleR) * t, IdleG + (1f - IdleG) * t, IdleB + (1f - IdleB) * t, IdleA + (1f - IdleA) * t);
    }

    /// <summary>Off-device: the anchor at 1280×720, 1920×1080 and GUI scale 1.5, the hit-test, the hover animation
    /// (spins while hovered, settles when not, never snaps), and a zero-alloc emit.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var gear = new MenuGear();
        var g1 = new Glyph { W = 64, H = 64, U0 = 0.1f, V0 = 0.1f, U1 = 0.2f, V1 = 0.2f, Advance = 77f };
        gear.Layout(1280f, 720f, 1f, g1);
        check.Expect(gear.X == 24f && gear.Y == 632f && gear.W == 64f && gear.H == 64f, $"720p: 64 px at (24,632) ({gear.X},{gear.Y} {gear.W}x{gear.H})");
        gear.Layout(1920f, 1080f, 1f, g1);
        check.Expect(gear.X == 24f && gear.Y == 992f, $"1080p: (24,992) ({gear.X},{gear.Y})");
        var g15 = new Glyph { W = 96, H = 96 };
        check.Expect(FaceFor(1.5f) == 150f && FaceFor(0.5f) == 100f, "face = 100 px x scale, floored at 1");
        gear.Layout(1920f, 1080f, 1.5f, g15);
        check.Expect(gear.X == 36f && gear.Y == 948f && gear.H == 96f, $"scale 1.5: 96 px inset 36 ({gear.X},{gear.Y} {gear.H})");
        check.Expect(gear.Contains(40f, 1000f) && !gear.Contains(20f, 1000f) && !gear.Contains(40f, 940f), "hit-test is the rect");

        check.Expect(gear.Angle == 0f && gear.Spin == 0f && gear.Bright == 0f, "starts still and idle");
        gear.Hovered = true;
        for (int i = 0; i < 60; i++) gear.Tick(1f / 60f);
        check.Expect(gear.Bright > 0.99f && gear.Spin > SpinRate * 0.99f && gear.Angle > 0.5f, $"a second of hover: bright {gear.Bright:F2}, spin {gear.Spin:F2}, angle {gear.Angle:F2}");
        float rest = gear.Angle;
        gear.Hovered = false;
        gear.Tick(1f / 60f);
        check.Expect(gear.Angle > rest && gear.Spin < SpinRate, "un-hover eases the spin down rather than stopping dead");
        for (int i = 0; i < 120; i++) gear.Tick(1f / 60f);
        float settled = gear.Angle;
        gear.Tick(1f / 60f);
        check.Expect(gear.Bright < 0.01f && MathF.Abs(gear.Angle - settled) < 1e-4f, "two seconds later it rests at its angle, idle tint");
        gear.Tick(5f);
        check.Expect(gear.Angle >= 0f && gear.Angle < MathF.PI * 2f + 1e-3f, "a stalled frame is clamped, the angle stays wrapped");

        var b = new VertexBatch(0.5f, 0.5f);
        gear.Layout(1280f, 720f, 1f, g1);
        gear.Angle = MathF.PI / 2f;
        gear.Emit(b, g1);
        check.Expect(b.VertexCount == 6, "one quad");
        ReadOnlySpan<float> v = b.AsSpan();
        float cx = gear.X + 32f, cy = gear.Y + 32f;
        bool onCircle = true;
        for (int i = 0; i < 6; i++)
        {
            float dx = v[i * VertexBatch.Floats] - cx, dy = v[i * VertexBatch.Floats + 1] - cy;
            if (MathF.Abs(MathF.Sqrt(dx * dx + dy * dy) - 32f * MathF.Sqrt(2f)) > 0.01f) onCircle = false;
        }
        check.Expect(onCircle, "rotation keeps every corner on the box's circumcircle");
        check.Expect(MathF.Abs(v[0] - (cx + 32f)) < 0.01f && MathF.Abs(v[1] - (cy - 32f)) < 0.01f, "a quarter turn puts the top-left corner top-right");
        check.Expect(v[2] == 0.1f && v[3] == 0.1f && v[4] == IdleR && v[7] == IdleA, "top-left keeps its texcoord; idle tint at Bright 0");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { b.Reset(0.5f, 0.5f); gear.Tick(1f / 60f); gear.Emit(b, g1); }
        check.Expect(GC.GetAllocatedBytesForCurrentThread() == before, "100 tick+emit rounds allocate nothing");

        b.Reset(0.5f, 0.5f);
        (float bx0, float by0, float bx1, float by1) = gear.Badge;
        check.Expect(MathF.Abs(bx1 - bx0 - gear.W * BadgeFrac) < 0.01f && bx0 + bx1 > 2f * gear.X + gear.W && by0 + by1 < 2f * gear.Y + gear.H,
                     $"the badge sits on the gear's upper-right ({bx0},{by0})-({bx1},{by1}) on ({gear.X},{gear.Y}) {gear.W}x{gear.H}");
        gear.EmitBadge(b, g1, g1);
        ReadOnlySpan<float> bv = b.AsSpan();
        check.Expect(b.VertexCount == 12 && bv.Length > 1 && bv[0] == bx0 && bv[1] == by0, $"a disc filling the badge's box, then its mark ({b.VertexCount} vertices)");

        log.Info(check.Ok ? "MenuGear.SelfCheck OK" : "MenuGear.SelfCheck FAILED");
    }
}
