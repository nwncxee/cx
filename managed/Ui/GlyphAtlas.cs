namespace CX.Ui;

/// <summary>
/// One baked <b>face</b> -- a (typeface, pixel height) pair: its glyph table, line height, ascent and
/// advances. Several faces share one <see cref="AtlasSheet"/> bitmap, so every face's UVs address the
/// same GL texture. Face objects are epoch-scoped: a sheet re-bake produces new ones, so callers
/// re-fetch through <c>TextRig</c> when <c>TextRig.AtlasEpoch</c> changes rather than caching a reference.
/// </summary>
public sealed class GlyphAtlas
{
    public float LineHeight, Ascent;

    public GlyphAtlas() { AdvanceOfCached = AdvanceOf; }

    /// <summary>Centre texcoord of the sheet's solid-white block, so chrome rects can sample the same
    /// texture as text and get <c>coverage==1</c> -- one shader, one draw call.</summary>
    public float SolidU, SolidV;

    /// <summary>Dense codepoint ceiling for the flat array (0x0000-0x04FF covers Basic Latin, Latin-1,
    /// Latin Extended-A, and Cyrillic). Sparse extras (▪ ▾ ⚙) fall back to the dictionary.</summary>
    private const int DenseCeil = 0x0500;  // not-an-offset: a Unicode codepoint
    private readonly Glyph[] _dense = new Glyph[DenseCeil];
    private readonly bool[] _denseOk = new bool[DenseCeil];
    private readonly Dictionary<int, Glyph> _sparse = new();
    internal float SpaceAdvance;

    /// <summary>Cached delegate for <see cref="AdvanceOf"/>, reused across all WrapRunes calls to avoid
    /// per-call delegate allocation.</summary>
    public readonly System.Func<System.Text.Rune, float> AdvanceOfCached;

    public bool TryGet(int cp, out Glyph g)
    {
        if ((uint)cp < DenseCeil) { g = _dense[cp]; return _denseOk[cp]; }
        return _sparse.TryGetValue(cp, out g);
    }

    /// <summary>Scaled pixel advance for a rune; falls back to the space advance for uncovered codepoints.</summary>
    public float AdvanceOf(System.Text.Rune r)
    {
        int cp = r.Value;
        if ((uint)cp < DenseCeil) return _denseOk[cp] ? _dense[cp].Advance : SpaceAdvance;
        return _sparse.TryGetValue(cp, out Glyph g) ? g.Advance : SpaceAdvance;
    }

    internal void Store(int cp, Glyph g)
    {
        if ((uint)cp < DenseCeil) { _dense[cp] = g; _denseOk[cp] = true; }
        else _sparse[cp] = g;
    }

    internal int Count
    {
        get { int n = _sparse.Count; for (int i = 0; i < DenseCeil; i++) if (_denseOk[i]) n++; return n; }
    }

    /// <summary>Test-only: register a glyph advance for a codepoint so off-device self-checks can exercise
    /// measurement/alignment without a native font bake.</summary>
    internal void SetTestGlyph(int cp, float advance) => Store(cp, new Glyph { Advance = advance });

    /// <summary>Test-only: a glyph with a box, so a draw emits its quad: <paramref name="w"/> by <paramref name="h"/> px,
    /// standing on the baseline.</summary>
    internal void SetTestGlyph(int cp, float advance, int w, int h) =>
        Store(cp, new Glyph { Advance = advance, W = w, H = h, YOff = -h, U1 = 1f, V1 = 1f });
}
