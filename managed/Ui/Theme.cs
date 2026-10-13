using System;
using CX.Framework;

namespace CX.Ui;

/// <summary>A colour every CX window draws with, by meaning. Its lowercase name is the identifier a server may write in
/// place of a colour (<c>@accent</c>), so server content renders in whatever the player's look resolves to.</summary>
public enum Role
{
    PanelBg, Border, HeaderBg, CatBg, SubBg, ColBorder, NavOn, Accent, Accent2, Input, Selected, Tx, Tx2, Tx3, Grip, Hi,
    ToggleOff, Frame, Frame2, Track, ListBg, MasterBg, StripBg, StripBorder, Warn, Off, BadgeFrame, Ok,
}

/// <summary>One named colour set: an RGB per <see cref="Role"/>.</summary>
public sealed record Palette(string Id, string Label, uint[] Rgb);

/// <summary>The one look every CX window follows: <c>ui.look</c> picks the game's own GUI art or flat colour, and
/// <c>ui.palette</c> picks the flat colours. Native carries its own palette for what the art does not cover (text,
/// highlights), so one switch decides everything. A server may hold either knob.</summary>
public static class Theme
{
    public const string LookNative = "native", LookFlat = "flat";

    private static readonly uint[] SlateRgb =
    {
        0x0d0f17, 0x2a3350, 0x1a1f29, 0x0a0c14, 0x0f121c, 0x1f2740, 0x192040, 0x335785, 0x4a78b4, 0x050508,  // not-an-offset: colours
        0x38669e, 0xd8dee9, 0x8a93a6, 0x5a6274, 0xb8c0cc, 0xeaf0f8, 0x2a2f3d, 0x3a4460, 0x2f3850, 0x1e2333,  // not-an-offset: colours
        0x05060a, 0x10141f, 0x1a160e, 0x3a3020, 0xd9a35a, 0x6c7385, 0x3a3f4d, 0x7fb87a,                      // not-an-offset: colours
    };

    /// <summary>Colours beside NWN's own GUI art: its parchment-gold border and warm text over dark wood.</summary>
    // ponytail: hand-matched to nui_* art by eye; read them off the engine's nk_style if a server reports a mismatch.
    private static readonly uint[] NativeRgb =
    {
        0x14110c, 0x6b5636, 0x231d14, 0x0f0c08, 0x18140e, 0x3a2f1e, 0x2c2416, 0xad8e60, 0xd2b27a, 0x080604,  // not-an-offset: colours
        0x8a6d3e, 0xe6dcc6, 0xa89a80, 0x6e6452, 0xc8b48c, 0xfff4dc, 0x2e2618, 0x5a4a30, 0x463a26, 0x221c12,  // not-an-offset: colours
        0x0a0806, 0x1a150d, 0x1a160e, 0x3a3020, 0xd9a35a, 0x7a6e58, 0x4a3e2a, 0x7fb87a,                      // not-an-offset: colours
    };

    /// <summary>The flat palettes, first = default: slate as drawn today, the rest its hue turned (ash drained).</summary>
    public static readonly Palette[] Palettes =
    {
        new("slate", "Slate", SlateRgb),
        new("ember", "Ember", Shift(SlateRgb, 30f, 1f)),
        new("moss", "Moss", Shift(SlateRgb, 120f, 0.75f)),
        new("wine", "Wine", Shift(SlateRgb, 340f, 1f)),
        new("ash", "Ash", Shift(SlateRgb, 0f, 0f)),
    };

    /// <summary>The colours worn with the native look.</summary>
    public static readonly Palette Native = new("native", "Game art", NativeRgb);

    private sealed class ThemeState { public uint[] Rgb = SlateRgb; public bool Native; public int Epoch; }
    private static ThemeState _s = new();

    /// <summary>True while the native look is chosen; an owner whose art fails to arm still draws flat.</summary>
    public static bool IsNative => _s.Native;
    /// <summary>Bumps on every look or palette change; a window caching colours compares it per frame.</summary>
    public static int Epoch => _s.Epoch;

    /// <summary>The current RGB for <paramref name="r"/>.</summary>
    public static uint Rgb(Role r) => _s.Rgb[(int)r];
    /// <summary>The current colour for <paramref name="r"/> as <c>rrggbbaa</c>.</summary>
    public static uint Rgba(Role r, byte a = 0xff) => (_s.Rgb[(int)r] << 8) | a;

    /// <summary>Re-resolve from the two knobs; their change hook.</summary>
    internal static void Apply()
    {
        bool native = UiKnobs.Look.V == LookNative;
        _s = new ThemeState { Native = native, Rgb = native ? NativeRgb : Find(UiKnobs.Palette.V).Rgb, Epoch = _s.Epoch + 1 };
    }

    private static Palette Find(string id)
    {
        foreach (Palette p in Palettes) if (p.Id == id) return p;
        return Palettes[0];
    }

    /// <summary>Resolve a role identifier (<c>@accent</c>, case-insensitive) to <c>rrggbbaa</c>, opaque.</summary>
    public static bool TryRole(ReadOnlySpan<char> s, out uint rgba)
    {
        rgba = 0;
        if (s.Length < 2 || s[0] != '@' || !char.IsAsciiLetter(s[1]) || !Enum.TryParse(s[1..], true, out Role r) || !Enum.IsDefined(r)) return false;
        rgba = Rgba(r);
        return true;
    }

    private static uint[] Shift(uint[] src, float hue, float satMul)
    {
        var o = new uint[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            var role = (Role)i;
            o[i] = role is Role.Warn or Role.Ok or Role.StripBg or Role.StripBorder ? src[i] : Rehue(src[i], hue, satMul);
        }
        return o;
    }

    /// <summary>Keep lightness, set hue to <paramref name="hue"/> degrees, scale saturation.</summary>
    private static uint Rehue(uint rgb, float hue, float satMul)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, b = (rgb & 0xFF) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b)), l = (max + min) * 0.5f, d = max - min;
        float s = d == 0f ? 0f : d / (1f - MathF.Abs(2f * l - 1f));
        s = MathF.Min(1f, s * satMul);
        float c = (1f - MathF.Abs(2f * l - 1f)) * s, h = hue / 60f, x = c * (1f - MathF.Abs(h % 2f - 1f)), m = l - c * 0.5f;
        (float, float, float) t = (int)h switch { 0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x) };
        static uint B(float v) => (uint)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
        return (B(t.Item1 + m) << 16) | (B(t.Item2 + m) << 8) | B(t.Item3 + m);
    }

    /// <summary>Off-device. Oracles: hand-written literals -- slate restates today's chrome, ash is grey, the warning
    /// colour survives every hue turn, and a role identifier resolves case-insensitively.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Palettes[0].Rgb[(int)Role.Accent] == 0x335785 && Palettes[0].Rgb[(int)Role.Tx] == 0xd8dee9, "slate is today's chrome");
        check.Expect(SlateRgb.Length == Enum.GetValues<Role>().Length && NativeRgb.Length == SlateRgb.Length, "every palette covers every role");
        uint ash = Find("ash").Rgb[(int)Role.Accent];
        check.Expect(((ash >> 16) & 0xFF) == (ash & 0xFF) && ((ash >> 8) & 0xFF) == (ash & 0xFF), "ash is grey");
        foreach (Palette p in Palettes) check.Expect(p.Rgb[(int)Role.Warn] == 0xd9a35a, $"{p.Id} keeps the warning colour");
        uint ember = Find("ember").Rgb[(int)Role.Accent];
        check.Expect(((ember >> 16) & 0xFF) > (ember & 0xFF), "ember's accent is warm");
        check.Expect(TryRole("@ACCENT", out _) && TryRole("@tx2", out _), "role identifiers resolve case-insensitively");
        check.Expect(!TryRole("@nope", out _) && !TryRole("accent", out _) && !TryRole("@7", out _), "a bad identifier is refused");
        log.Info(check.Ok ? "Theme.SelfCheck: passed" : "Theme.SelfCheck: FAILED");
    }
}
