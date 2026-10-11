using System;

namespace CX.Services.Quickbar;

/// <summary>The CX quickbar's geometry, engine-free: a grip at the left, then <see cref="Strips"/> runs of
/// <see cref="Slots"/> buttons, one per set shown, stacked (the bottom the plain set, then Shift, then Ctrl) or side by
/// side (plain, Shift, Ctrl from the left). Screen px, y down; <see cref="X"/>/<see cref="Y"/> is the top-left corner.</summary>
internal sealed partial class QuickbarLayout
{
    /// <summary>Buttons per set and the engine's three sets (plain, Shift, Ctrl).</summary>
    public const int Slots = 12, Sets = 3;
    /// <summary>Design px: a button, the default gap between two, between two side-by-side sets, the frame, the grip's width.</summary>
    public const float CellPx = 36f, GapPx = 2f, StripGapPx = 8f, PadPx = 3f, GripPx = 10f;
    /// <summary>Scale at a Size of 1: the design px above read small beside the game's own HUD.</summary>
    public const float BaseScale = 1.25f;

    public float X, Y, W, H, S = BaseScale;
    public int Strips { get; private set; } = 1;
    public bool SideBySide { get; private set; }
    /// <summary>Design px of the grip as laid out: <see cref="GripPx"/>, or 0 while the position is locked.</summary>
    public float Grip { get; private set; } = GripPx;
    /// <summary>Design px between two buttons across and between two rows.</summary>
    public float GapX { get; private set; } = GapPx;
    public float GapY { get; private set; } = GapPx;

    public float Cell => CellPx * S;
    /// <summary>Slots drawn: every strip's twelve.</summary>
    public int Count => Strips * Slots;

    private float StepX => CellPx + GapX;
    private float StepY => CellPx + GapY;
    private float StripW => Slots * CellPx + (Slots - 1) * GapX;

    /// <summary>Size the bar for Size <paramref name="s"/>, <paramref name="strips"/> sets shown (clamped 1..3), their
    /// arrangement, whether it has a grip (none while locked) and the design px between buttons across and down.</summary>
    public void Layout(float s, int strips, bool sideBySide, bool grip = true, float gapX = GapPx, float gapY = GapPx)
    {
        S = s * BaseScale;
        GapX = Math.Max(0f, gapX); GapY = Math.Max(0f, gapY);
        Grip = grip ? GripPx : 0f;
        Strips = Math.Clamp(strips, 1, Sets);
        SideBySide = sideBySide;
        int across = sideBySide ? Strips : 1, down = sideBySide ? 1 : Strips;
        W = (PadPx * 2 + Grip + across * StripW + (across - 1) * StripGapPx) * S;
        H = (PadPx * 2 + down * CellPx + (down - 1) * GapY) * S;
    }

    /// <summary>The set strip <paramref name="k"/> shows while <paramref name="held"/> is the modifier set held: each strip
    /// its fixed set, except that the plain strip shows a held set no strip shows, as vanilla's one row does.</summary>
    public int SetOfStrip(int k, int held)
    {
        int set = SideBySide ? k : Strips - 1 - k;
        return set == 0 && held >= Strips ? held : set;
    }

    /// <summary>Engine button index of set <paramref name="set"/>, column <paramref name="col"/>.</summary>
    public static int Button(int set, int col) => set * Slots + col;

    /// <summary>The engine button drawn in slot <paramref name="i"/> (0..<see cref="Count"/>-1).</summary>
    public int ButtonOf(int i, int held) => Button(SetOfStrip(i / Slots, held), i % Slots);

    public void SlotRect(int i, out float x0, out float y0, out float x1, out float y1)
    {
        int k = i / Slots, col = i % Slots;
        float lx = PadPx + Grip + col * StepX + (SideBySide ? k * (StripW + StripGapPx) : 0f);
        float ly = PadPx + (SideBySide ? 0f : k * StepY);
        x0 = X + lx * S; y0 = Y + ly * S;
        x1 = x0 + Cell; y1 = y0 + Cell;
    }

    /// <summary>The engine button (0..35) nearest the point, or -1 off the bar: a drop on the frame, a gap (split at its
    /// middle) or the grip still lands on a button, as the stock bar's background takes it, rather than on the ground.</summary>
    public int SlotAt(float px, float py, int held)
    {
        if (!Contains(px, py)) return -1;
        float lx = (px - X) / S - PadPx - Grip, ly = (py - Y) / S - PadPx;
        int k = SideBySide ? Math.Clamp((int)((lx + StripGapPx / 2f) / (StripW + StripGapPx)), 0, Strips - 1)
                           : Math.Clamp((int)((ly + GapY / 2f) / StepY), 0, Strips - 1);
        if (SideBySide) lx -= k * (StripW + StripGapPx);
        int col = Math.Clamp((int)((lx + GapX / 2f) / StepX), 0, Slots - 1);
        return Button(SetOfStrip(k, held), col);
    }

    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;

    /// <summary>The move grip: the strip left of the first column.</summary>
    public bool GripAt(float px, float py) => Grip > 0f && Contains(px, py) && px < X + (PadPx + Grip) * S;
}

/// <summary>The bar's key bindings, one chord per engine button: a set's row is twelve space-separated chords
/// (<c>F1</c>, <c>Shift+F1</c>, <c>Ctrl+Alt+q</c>, <c>Keypad_1</c>: <c>_</c> for a space in an SDL key name), <c>-</c> for
/// none. Engine-free: the key-name lookup is passed in.</summary>
internal sealed class QuickbarKeys
{
    /// <summary>Modifier bits of a chord.</summary>
    public const int Ctrl = 1, Shift = 2, Alt = 4;
    private const int Buttons = QuickbarLayout.Slots * QuickbarLayout.Sets;

    private readonly int[] _key = new int[Buttons], _mods = new int[Buttons];
    private readonly string[] _name = new string[Buttons], _label = new string[Buttons];

    public QuickbarKeys() { Array.Fill(_name, ""); Array.Fill(_label, ""); }

    /// <summary>Bind set <paramref name="set"/>'s twelve buttons from <paramref name="text"/>; returns the chords refused
    /// (an unknown key name, a modifier with no key). A refused or missing chord leaves its button unbound.</summary>
    public int Parse(int set, string text, Func<string, int> keyOf)
    {
        string[] tok = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int bad = 0;
        for (int col = 0; col < QuickbarLayout.Slots; col++)
        {
            int b = QuickbarLayout.Button(set, col);
            Clear(b);
            if (col >= tok.Length || tok[col] == "-") continue;
            if (!Chord(tok[col], keyOf, out int key, out int mods, out string name)) { bad++; continue; }
            Set(b, key, mods, name);
        }
        return bad;
    }

    /// <summary>Bind button <paramref name="b"/> to the chord, taking it off any other button that had it.</summary>
    public void Bind(int b, int key, int mods, string name)
    {
        for (int o = Match(key, mods); o >= 0; o = Match(key, mods)) Clear(o);
        Set(b, key, mods, name);
    }

    public void Clear(int b) { _key[b] = 0; _mods[b] = 0; _name[b] = ""; _label[b] = ""; }

    /// <summary>Set <paramref name="set"/>'s twelve chords as <see cref="Parse"/> reads them back.</summary>
    public string Row(int set)
    {
        var parts = new string[QuickbarLayout.Slots];
        for (int col = 0; col < parts.Length; col++)
        {
            int b = QuickbarLayout.Button(set, col);
            parts[col] = _key[b] == 0 ? "-"
                : ((_mods[b] & Ctrl) != 0 ? "Ctrl+" : "") + ((_mods[b] & Shift) != 0 ? "Shift+" : "") + ((_mods[b] & Alt) != 0 ? "Alt+" : "") + (_name[b] == "-" ? "Minus" : _name[b].Replace(' ', '_'));
        }
        return string.Join(' ', parts);
    }

    private void Set(int b, int key, int mods, string name)
    {
        _key[b] = key; _mods[b] = mods; _name[b] = name;
        _label[b] = (mods == 0 ? "" : ((mods & Ctrl) != 0 ? "C" : "") + ((mods & Shift) != 0 ? "S" : "") + ((mods & Alt) != 0 ? "A" : "") + "-")
                    + name.Replace("Keypad ", "KP").ToUpperInvariant();
    }

    /// <summary>A chord token: <c>Ctrl+</c>/<c>Shift+</c>/<c>Alt+</c> prefixes, then the key name (so <c>Keypad_+</c>
    /// keeps its plus), <c>_</c> for a space and <c>Minus</c> for the <c>-</c> key, which alone means "none".</summary>
    private static bool Chord(string tok, Func<string, int> keyOf, out int key, out int mods, out string name)
    {
        key = 0; mods = 0;
        for (bool more = true; more;)
        {
            more = false;
            foreach ((string p, int m) in Prefixes)
                if (tok.Length > p.Length && tok.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { mods |= m; tok = tok[p.Length..]; more = true; }
        }
        name = tok == "Minus" ? "-" : tok.Replace('_', ' ');
        if (name.Length == 0 || name.EndsWith('+') && name.Length > 1 && name[^2] != ' ') return false;
        key = keyOf(name);
        return key != 0;
    }

    private static readonly (string, int)[] Prefixes = { ("ctrl+", Ctrl), ("control+", Ctrl), ("shift+", Shift), ("alt+", Alt) };

    /// <summary>The button bound to exactly this chord, or -1.</summary>
    public int Match(int key, int mods)
    {
        for (int b = 0; b < Buttons; b++) if (_key[b] == key && _mods[b] == mods && key != 0) return b;
        return -1;
    }

    /// <summary>Is <paramref name="key"/> the key of any binding, whatever its modifiers.</summary>
    public bool Wants(int key)
    {
        for (int b = 0; b < Buttons; b++) if (_key[b] == key && key != 0) return true;
        return false;
    }

    /// <summary>The short corner label of button <paramref name="b"/>'s chord ("" unbound).</summary>
    public string Label(int b) => (uint)b < Buttons ? _label[b] : "";
}
