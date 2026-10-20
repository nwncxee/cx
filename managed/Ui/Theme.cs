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
    /// <summary>The palette the player edits in <c>ui.custom_&lt;role&gt;</c>.</summary>
    public const string Custom = "custom";
    /// <summary>A content palette's resref prefix; the rest of the resref is its id.</summary>
    public const string FilePrefix = "cxpal_";

    private static readonly uint[] SlateRgb =
    {
        0x0d0f17, 0x2a3350, 0x1a1f29, 0x0a0c14, 0x0f121c, 0x1f2740, 0x192040, 0x335785, 0x4a78b4, 0x050508,  // not-an-offset: colours
        0x38669e, 0xd8dee9, 0x8a93a6, 0x5a6274, 0xb8c0cc, 0xeaf0f8, 0x2a2f3d, 0x3a4460, 0x2f3850, 0x1e2333,  // not-an-offset: colours
        0x05060a, 0x10141f, 0x1a160e, 0x3a3020, 0xd9a35a, 0x6c7385, 0x3a3f4d, 0x7fb87a,                      // not-an-offset: colours
    };

    /// <summary>Colours beside NWN's own GUI art, from the stock <c>nui_skin.tml</c>: black interiors, the
    /// <c>#AD8E60</c> border gold, <c>#F8F8F8</c> text, <c>#C8C8C8</c> header labels, <c>nui_header</c>'s blue.</summary>
    private static readonly uint[] NativeRgb =
    {
        0x000000, 0xad8e60, 0x1f4261, 0x000000, 0x0a0a0a, 0x522f13, 0x1a2333, 0xad8e60, 0xffcf64, 0x000000,  // not-an-offset: colours
        0x1f4261, 0xf8f8f8, 0xc8c8c8, 0x8a8070, 0xb38650, 0xffffff, 0x2e2618, 0xb38650, 0x815537, 0x000000,  // not-an-offset: colours
        0x000000, 0x0a0806, 0x1a160e, 0x3a3020, 0xd9a35a, 0x7a7060, 0x633e1e, 0x7fb87a,                      // not-an-offset: colours
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

    private sealed class ThemeState
    {
        public uint[] Rgb = SlateRgb;
        public bool Native;
        public int Epoch;
        /// <summary>Palettes the resource chain supplies (override, a hak, NWSync), by id.</summary>
        public Palette[] Found = Array.Empty<Palette>();
        /// <summary>The last non-custom palette chosen: what "start from the previous palette" copies.</summary>
        public string Previous = "slate";
    }
    private static ThemeState _s = new();

    /// <summary>The colour of every role in a set, built-in, custom or found; null for an unknown id.</summary>
    public static uint[]? RgbOf(string id)
    {
        if (id == Custom) return CustomRgb();
        foreach (Palette p in Palettes) if (p.Id == id) return p.Rgb;
        foreach (Palette p in _s.Found) if (p.Id == id) return p.Rgb;
        return null;
    }

    /// <summary>The player's custom palette as the swatch rows hold it.</summary>
    public static uint[] CustomRgb()
    {
        var k = UiKnobs.CustomRgb;
        var o = new uint[k.Length];
        for (int i = 0; i < o.Length; i++) o[i] = k[i].V >> 8;
        return o;
    }

    /// <summary>The last non-custom palette chosen.</summary>
    public static string Previous => _s.Previous;

    /// <summary>Publish a new scan: re-offer <c>ui.palette</c> (built-ins, custom, then found) and re-resolve. Main thread, cold.</summary>
    internal static void SetFound(Palette[] found)
    {
        _s = new ThemeState { Native = _s.Native, Rgb = _s.Rgb, Epoch = _s.Epoch, Found = found, Previous = _s.Previous };
        int n = Palettes.Length + 1 + found.Length;
        string[] ids = new string[n], labels = new string[n];
        for (int i = 0; i < Palettes.Length; i++) { ids[i] = Palettes[i].Id; labels[i] = Palettes[i].Label; }
        ids[Palettes.Length] = Custom; labels[Palettes.Length] = "Custom";
        for (int i = 0; i < found.Length; i++) { ids[Palettes.Length + 1 + i] = found[i].Id; labels[Palettes.Length + 1 + i] = found[i].Label; }
        UiKnobs.Palette.Offer(ids, labels);
        Apply();
    }

    /// <summary>A palette id as a file or a server names it: 1-10 of a-z, 0-9 and _ (the resref is <c>cxpal_</c> + id).</summary>
    public static bool ValidId(string id)
    {
        if (id.Length is 0 or > 10) return false;
        foreach (char c in id) if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return false;
        return true;
    }

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
        uint[] flat = RgbOf(UiKnobs.Palette.V) ?? SlateRgb;
        foreach (var k in UiKnobs.CustomRgb) k.Disabled = UiKnobs.Palette.V != Custom;
        string id = UiKnobs.Palette.V;
        _s = new ThemeState { Native = native, Rgb = native ? NativeRgb : flat, Epoch = _s.Epoch + 1, Found = _s.Found,
                              Previous = id != Custom && RgbOf(id) != null ? id : _s.Previous };
    }

    /// <summary>Resolve a role identifier (<c>@accent</c>, case-insensitive) to <c>rrggbbaa</c>, opaque.</summary>
    public static bool TryRole(ReadOnlySpan<char> s, out uint rgba)
    {
        bool ok = TryRoleName(s, out Role r);
        rgba = ok ? Rgba(r) : 0;
        return ok;
    }

    /// <summary>The role a role identifier names, for a caller that keeps the role itself and resolves it per frame.</summary>
    public static bool TryRoleName(ReadOnlySpan<char> s, out Role role)
    {
        role = default;
        return s.Length >= 2 && s[0] == '@' && char.IsAsciiLetter(s[1]) && Enum.TryParse(s[1..], true, out role) && Enum.IsDefined(role);
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
        uint ash = RgbOf("ash")![(int)Role.Accent];
        check.Expect(((ash >> 16) & 0xFF) == (ash & 0xFF) && ((ash >> 8) & 0xFF) == (ash & 0xFF), "ash is grey");
        foreach (Palette p in Palettes) check.Expect(p.Rgb[(int)Role.Warn] == 0xd9a35a, $"{p.Id} keeps the warning colour");
        uint ember = RgbOf("ember")![(int)Role.Accent];
        check.Expect(((ember >> 16) & 0xFF) > (ember & 0xFF), "ember's accent is warm");
        check.Expect(TryRole("@ACCENT", out _) && TryRole("@tx2", out _), "role identifiers resolve case-insensitively");
        check.Expect(ValidId("my_pal2") && !ValidId("") && !ValidId("Caps") && !ValidId("elevenchars") && !ValidId("a-b"), "palette ids are 1-10 of a-z0-9_");
        check.Expect(RgbOf("nope") == null && RgbOf(Custom)!.Length == SlateRgb.Length, "an unknown id has no colours; custom covers every role");
        check.Expect(!TryRole("@nope", out _) && !TryRole("accent", out _) && !TryRole("@7", out _), "a bad identifier is refused");
        log.Info(check.Ok ? "Theme.SelfCheck: passed" : "Theme.SelfCheck: FAILED");
    }
}
