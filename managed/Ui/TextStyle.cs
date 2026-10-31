using CX.Framework;

namespace CX.Ui;

/// <summary>One UI element's own text rows -- size, typeface, outline -- declared into the element's group (the caller
/// sets the section first), so the panel, <c>cx.cfg</c> and chat get them like any knob; and the face they resolve to.
/// Size "as UI" (0) and typeface <c>default</c> follow <c>ui.font</c> / <c>ui.typeface</c>.</summary>
public sealed class TextStyle
{
    /// <summary>The outline ladder: off, thin, thick. A stored toggle's "1"/"0" parses to thin/off.</summary>
    public static readonly int[] OutlineLevels = { 0, 1, 2 };
    public static readonly string[] OutlineNames = { "Off", "Thin", "Thick" };
    /// <summary>The eight halo directions as flat (dx,dy) pairs, scaled by a ring's radius.</summary>
    private static readonly float[] Dirs = { -1, -1, 0, -1, 1, -1, -1, 0, 1, 0, -1, 1, 0, 1, 1, 1 };
    /// <summary>Floats <see cref="HaloOffsets"/> fills at most: two rings of eight (dx,dy) pairs.</summary>
    public const int MaxHaloFloats = 32;
    /// <summary>Source of every style's <see cref="Stamp"/>: one counter, so no two styles ever share a key.</summary>
    private static int s_stamps;

    /// <summary>Null for an element that sizes its text itself (the inventory, off its window size).</summary>
    public readonly Knob<int>? Size;
    public readonly Knob<string> Typeface;
    public readonly Knob<int> Outline;
    private readonly string _what;
    private GlyphAtlas? _face;
    /// <summary>The last (typeface, px) a miss asked a bake for: in flight or refused, so it is not asked again.</summary>
    private string? _askedName;
    private float _askedPx;

    /// <summary>Moves whenever <see cref="Face"/> resolves to another face object (a pick, a re-bake): the key a
    /// cached wrap or measure of this element's text is valid under. Unique across styles.</summary>
    public int Stamp { get; private set; }

    /// <summary>Declare <c>&lt;prefix&gt;font</c>, <c>&lt;prefix&gt;typeface</c>, <c>&lt;prefix&gt;outline</c> in
    /// <paramref name="g"/>. <paramref name="what"/> names the element in the help lines; <paramref name="size"/> is
    /// the size default (0 = as UI), <paramref name="sized"/> false leaves the size row out. <paramref name="tier"/>
    /// carries all three rows: Advanced for an element over the world, ChatOnly to follow the UI-wide rows.</summary>
    public TextStyle(KnobGroup g, string what, string prefix = "", int size = 0, int outline = 0, bool sized = true,
                     Action? changed = null, Tier tier = Tier.User)
    {
        _what = what;
        if (sized)
        {
            int[] ladder = new int[TextRig.MaxFontPx - TextRig.MinFontPx + 2];
            var names = new string[ladder.Length];
            names[0] = "as UI";
            for (int i = 1; i < ladder.Length; i++) { ladder[i] = TextRig.MinFontPx + i - 1; names[i] = ladder[i].ToString(); }
            Size = g.Dropdown(prefix + "font", "Text size", ladder, size, names, layout: true, tier: tier, changed: changed,
                              help: $"Text size for {what}; as UI follows the UI text size.");
        }
        Typeface = g.Dropdown(prefix + "typeface", "Typeface", FontRegistry.Names, FontRegistry.DefaultName, layout: true, tier: tier, changed: changed,
                              help: $"Font for {what}; default follows the UI typeface. Type in the list to search.");
        Outline = g.Dropdown(prefix + "outline", "Text outline", OutlineLevels, outline, OutlineNames, tier: tier, changed: changed,
                             help: $"A dark edge around the letters of {what}, so they read over a bright scene.");
        if (g.Registered) lock (s_all) s_all.Add(this);
    }

    /// <summary>Every style a registered group declared: each sheet bake carries their faces, so no element's first
    /// draw re-bakes the whole sheet under the frame.</summary>
    private static readonly List<TextStyle> s_all = new();

    /// <summary>The (typeface, px) of every declared style that draws with a face of its own, into <paramref name="into"/>.</summary>
    internal static void Wanted(List<(string Typeface, float Px)> into) { lock (s_all) Wanted(s_all, into); }

    private static void Wanted(List<TextStyle> styles, List<(string Typeface, float Px)> into)
    {
        foreach (TextStyle s in styles)
            if (!s.FollowsUi) into.Add((FontRegistry.Effective(s.Typeface.V), s.Px));
    }

    /// <summary>The element's face px at the GUI scale, clamped like the UI atlas.</summary>
    public float Px => TextRig.FacePx(Size?.V ?? 0);

    /// <summary>True when both rows follow the UI: the element draws with the UI face.</summary>
    public bool FollowsUi => (Size?.V ?? 0) <= 0 && Typeface.V == FontRegistry.DefaultName;

    /// <summary>Halo radius in px for this element's outline (0 = none): <see cref="HaloOf"/> at <see cref="Px"/>.</summary>
    public float HaloPx => HaloOf(Outline.V, Px);

    /// <summary>Halo radius for outline <paramref name="level"/> on a face of <paramref name="facePx"/>: one px per level
    /// per <see cref="Metrics.DesignFontPx"/> of face, never under one px per level, so it keeps its weight as text grows.</summary>
    public static float HaloOf(int level, float facePx) => level <= 0 ? 0f : level * MathF.Max(1f, facePx / Metrics.DesignFontPx);

    /// <summary>The (dx,dy) offsets of a halo of radius <paramref name="px"/>, written into <paramref name="buf"/> (at least
    /// <see cref="MaxHaloFloats"/>) and returned as its filled part: eight directions on the outer ring, and eight more at
    /// half radius once it passes 1.5 px so a thick halo has no see-through gap. Empty for 0.</summary>
    public static Span<float> HaloOffsets(float px, Span<float> buf)
    {
        int n = 0;
        for (int k = 0; k < 2 && px > 0f && (k == 0 || px > 1.5f); k++)
        {
            float r = k == 0 ? px : px * 0.5f;
            for (int o = 0; o < Dirs.Length; o += 2) { buf[n++] = Dirs[o] * r; buf[n++] = Dirs[o + 1] * r; }
        }
        return buf[..n];
    }

    /// <summary>The element's face: <paramref name="ui"/> when it follows the UI, else its own. Hot-safe: a miss never
    /// bakes here, it queues the bake on the thread pool once per (typeface, px) and <paramref name="ui"/> stands in
    /// until it lands; <paramref name="cold"/> (a caller on the input / setter thread) bakes in place. A refused bake
    /// logs once. ponytail: every (typeface, px) picked stays in the sheet for the session; releasing one is the upgrade.</summary>
    public GlyphAtlas? Face(GlyphAtlas? ui, bool cold = false)
    {
        GlyphAtlas? f = Resolve(ui, cold);
        if (!ReferenceEquals(f, _face)) { _face = f; Stamp = Interlocked.Increment(ref s_stamps); }
        return f;
    }

    private GlyphAtlas? Resolve(GlyphAtlas? ui, bool cold)
    {
        if (FollowsUi || !TextRig.Ready) return ui;
        float px = Px;
        string name = Typeface.V;
        if (TextRig.BakedFace(name, px) is GlyphAtlas f) return f;
        if (name == _askedName && px == _askedPx) return ui;
        _askedName = name; _askedPx = px;
        if (!cold) { ThreadPool.QueueUserWorkItem(Bake, (this, name, px), preferLocal: false); return ui; }
        Bake((this, name, px));
        return TextRig.BakedFace(name, px) ?? ui;
    }

    /// <summary>Cold (the pool, or a cold caller): bake the asked face; the re-bake's <c>TextRig.Rebaked</c> tells the
    /// cached-geometry owners, and the next hot lookup finds it.</summary>
    private static void Bake((TextStyle Style, string Name, float Px) a)
    {
        if (TextRig.Face(a.Name, a.Px) == null)
            TextRig.Log?.Info($"[text] {a.Style._what}: face {a.Name} @ {a.Px}px refused; drawing with the UI face");
    }

    /// <summary>Off-device: the three rows declare under their keys with the ladders the panel shows, a stored toggle
    /// parses onto the outline ladder, a following style hands back the UI face and a headless one falls back to it,
    /// the halo radius grows with the face, and a batch lays a run's halos under all of its glyphs.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var g = new KnobGroup("cx-textcheck", "Test", "Text check", null, null, true, Effect.Live, null, null, "", register: false);
        g.Section("Bubble text");
        var s = new TextStyle(g, "bubbles", prefix: "bubble", outline: 1);
        check.Expect(s.Size is { Key: "cx-textcheck.bubblefont", Layout: true } && s.Size.Values[0] == 0 && s.Size.Choices[0] == "as UI"
                     && s.Size.Values[1] == TextRig.MinFontPx && s.Size.Values[^1] == TextRig.MaxFontPx, "size row: as UI then the 6-32 ladder");
        check.Expect(s.Typeface.Key == "cx-textcheck.bubbletypeface" && s.Typeface.Values[0] == FontRegistry.DefaultName, "typeface row over the registry, default first");
        check.Expect(s.Outline.Key == "cx-textcheck.bubbleoutline" && s.Outline.Section == "Bubble text" && s.Outline.V == 1, "outline row keyed, in the caller's section, at its default");
        check.Expect(s.Outline.TryParse("0", out int off) && off == 0 && s.Outline.TryParse("1", out int thin) && thin == 1
                     && s.Outline.TryParse("thick", out int thick) && thick == 2, "a stored toggle's 1/0 and a label parse onto the ladder");
        var bare = new TextStyle(new KnobGroup("cx-textcheck2", "Test", "Text check 2", null, null, true, Effect.Live, null, null, "", register: false),
                                 "the window", sized: false);
        check.Expect(bare.Size == null && bare.Typeface.Group.Knobs.Count == 2 && bare.Typeface.Key == "cx-textcheck2.typeface", "sized: false declares no size row");

        var ui = new GlyphAtlas();
        int stamp = s.Stamp;
        check.Expect(s.FollowsUi && ReferenceEquals(s.Face(ui), ui) && s.Stamp != stamp, "a following style hands back the UI face, stamped");
        stamp = s.Stamp;
        check.Expect(ReferenceEquals(s.Face(ui), ui) && s.Stamp == stamp, "the same face again keeps the stamp");
        s.Size!.V = 20;
        check.Expect(!s.FollowsUi && (TextRig.Ready || ReferenceEquals(s.Face(ui), ui)), "an own size headless falls back to the UI face");
        s.Size.V = 0;
        check.Expect(!ReferenceEquals(s.Face(new GlyphAtlas()), ui) && s.Stamp != stamp, "a different face object moves the stamp");
        check.Expect(bare.Face(ui) == ui && bare.Stamp != s.Stamp, "two styles never share a stamp");

        var want = new List<(string Typeface, float Px)>();
        s.Size.V = 26;
        check.Expect(Metrics.GameUiScale == 1f, "off-device the game UI scale is 1, so a face's px is its size");
        Wanted(new List<TextStyle> { s, bare }, want);
        check.Expect(want.Count == 1 && want[0] == ("default", 26f), "a bake wants the own-size style's face at 26 px, not the following one's");
        s.Size.V = 0;
        check.Expect(!s_all.Contains(s), "a style of an unregistered group is never baked ahead");

        check.Expect(HaloOf(0, 18f) == 0f && HaloOf(1, 18f) == 1f && HaloOf(2, 18f) == 2f && HaloOf(1, 36f) == 2f && HaloOf(1, 9f) == 1f,
                     "halo: off is 0, one px per level at 18 px, grows with the face, never under a px");
        Span<float> buf = stackalloc float[MaxHaloFloats];
        check.Expect(HaloOffsets(0f, buf).Length == 0 && HaloOffsets(1f, buf).Length == 16 && HaloOffsets(1f, buf)[0] == -1f,
                     "halo offsets: none when off, one ring of eight for a thin halo");
        Span<float> rings = HaloOffsets(4f, buf);
        check.Expect(rings.Length == MaxHaloFloats && rings[0] == -4f && rings[16] == -2f, "a thick halo adds a half-radius inner ring");

        var gl = new Glyph { W = 4, H = 4, U1 = 1f, V1 = 1f, Advance = 5f };
        var b = new VertexBatch(0.5f, 0.5f);
        b.Rect(0f, 0f, 10f, 10f, 0.2f, 0.2f, 0.2f, 1f);
        b.Outline = 1f;
        b.Glyph(gl, 0f, 0f, 1f, 1f, 1f, 1f);
        b.Glyph(gl, 5f, 0f, 1f, 1f, 1f, 1f);
        b.Rect(0f, 0f, 1f, 1f, 0.2f, 0.2f, 0.2f, 1f);
        ReadOnlySpan<float> v = b.AsSpan();
        const int Q = 6 * VertexBatch.Floats;
        check.Expect(b.VertexCount == 6 * (1 + 2 * 8 + 2 + 1), $"thin: 8 halo quads per glyph ({b.VertexCount} verts)");
        bool halosBlack = true;
        for (int q = 1; q <= 16; q++) halosBlack &= v[q * Q + 4] == 0f;
        check.Expect(v[4] == 0.2f && halosBlack && v[17 * Q + 4] == 1f && v[18 * Q + 4] == 1f && v[19 * Q + 4] == 0.2f,
                     "rect, then both glyphs' halos, then both bodies, then the next rect");
        b.Clear();
        b.Outline = 2f;
        b.Glyph(gl, 0f, 0f, 1f, 1f, 1f, 1f);
        check.Expect(b.VertexCount == 6 * (16 + 1), "thick: two rings of halo quads");
        b.Clear();
        b.Outline = 0f;
        b.Glyph(gl, 0f, 0f, 1f, 1f, 1f, 1f);
        check.Expect(b.VertexCount == 6, "no outline: the body alone");
        b.Outline = 1f;
        b.Reset(0.5f, 0.5f);
        b.Glyph(gl, 0f, 0f, 1f, 1f, 1f, 1f);
        check.Expect(b.Outline == 0f && b.VertexCount == 6, "a reset batch starts unoutlined");
        log.Info(check.Ok ? "TextStyle.SelfCheck: passed" : "TextStyle.SelfCheck: FAILED");
    }
}
