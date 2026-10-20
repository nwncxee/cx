using System;
using System.Runtime.CompilerServices;

namespace CX.Alloy.Db;

/// <summary>Layout-relevant font metrics: a value copy of <c>CAurFontInfo</c>'s fields plus its two per-glyph
/// coordinate tables, so layout is a pure function and can be exercised off-device on synthetic metrics.
/// Field names are the engine's <c>.txi</c> keys; units are GUI units (one design pixel = 0.01) except the
/// glyph tables, which are normalised texture coordinates.</summary>
public sealed class TextFont
{
    /// <summary>The font name the game asked for -- for a TTF font the whole token string, e.g. <c>"ttf fnt_default 14"</c>.</summary>
    public string Name = "";
    /// <summary><c>CAurFontInfo+0x00</c> <c>numchars</c>: how many of the 256 glyph slots the tables actually hold.</summary>
    public int NumChars;
    /// <summary><c>+0x04</c> <c>fontheight</c>: the line box.</summary>
    public float FontHeight;
    /// <summary><c>+0x08</c> <c>baselineheight</c>: the ascent, baseline below the line box's top.</summary>
    public float BaselineHeight;
    /// <summary><c>+0x0c</c> <c>texturewidth</c>: the factor that turns a u-span into GUI units.</summary>
    public float TextureWidth;
    /// <summary><c>+0x10</c> <c>spacingR</c>: extra advance appended to every glyph.</summary>
    public float SpacingR;
    /// <summary><c>+0x14</c> <c>spacingB</c>: a vertical shift applied to the whole glyph box.</summary>
    public float SpacingB;
    /// <summary><c>+0x18</c> <c>caretindent</c>: the caret's x nudge.</summary>
    public float CaretIndent;
    /// <summary><c>+0x68</c>: a TTF font, whose advance is the glyph's own <c>xadvance</c> rather than its u-span.</summary>
    public bool Ttf;
    /// <summary><c>+0x40</c> <c>isdoublebyte</c>: the engine's <c>TextOutDoubleByte</c> is a stub, so such a font
    /// draws nothing. Kept so CX refuses the same way instead of inventing glyphs.</summary>
    public bool DoubleByte;

    /// <summary><c>upperleftcoords</c> (<c>+0x20</c>): u, v and -- TTF only -- the glyph's left bearing in u.</summary>
    public readonly float[] UlU = new float[256], UlV = new float[256], UlZ = new float[256];
    /// <summary><c>lowerrightcoords</c> (<c>+0x30</c>): u, v and -- TTF only -- the glyph's advance in u.</summary>
    public readonly float[] LrU = new float[256], LrV = new float[256], LrZ = new float[256];

    /// <summary>GUI units trimmed off the TOP and the BOTTOM of a glyph's quad because its v range runs past the
    /// glyph's own cell in the atlas: the engine extrapolates every glyph out to the full line box, and the
    /// extra row lands on the NEXT packed glyph. Only the extrapolated part, which holds no ink of this glyph,
    /// is trimmed, so every pixel that drew before still draws. Zero for a bitmap font.</summary>
    public readonly float[] TopTrim = new float[256], BotTrim = new float[256];

    /// <summary>The engine's atlas texture name, and the epoch it was built at: a GUI-scale change re-bakes a TTF
    /// atlas (<c>AurTextureRequestRefreshTTF</c>), which moves every UV, so a cached quad list keyed on a stale
    /// epoch is rebuilt rather than drawn.</summary>
    public uint Atlas;
    public int Epoch;
    /// <summary>The game UI scale a TTF atlas was baked at; <c>TextFontBake.FollowScale</c> re-bakes past it.</summary>
    public float BakedScale = 1f;

    /// <summary>Take every field but the name from <paramref name="src"/>: a re-bake lands in the font object
    /// every text document already holds.</summary>
    public void CopyFrom(TextFont src)
    {
        NumChars = src.NumChars; FontHeight = src.FontHeight; BaselineHeight = src.BaselineHeight;
        TextureWidth = src.TextureWidth; SpacingR = src.SpacingR; SpacingB = src.SpacingB;
        CaretIndent = src.CaretIndent; Ttf = src.Ttf; DoubleByte = src.DoubleByte;
        Array.Copy(src.UlU, UlU, 256); Array.Copy(src.UlV, UlV, 256); Array.Copy(src.UlZ, UlZ, 256);
        Array.Copy(src.LrU, LrU, 256); Array.Copy(src.LrV, LrV, 256); Array.Copy(src.LrZ, LrZ, 256);
        Array.Copy(src.TopTrim, TopTrim, 256); Array.Copy(src.BotTrim, BotTrim, 256);
        Atlas = src.Atlas; Epoch = src.Epoch; BakedScale = src.BakedScale;
    }

    /// <summary>One glyph's advance in GUI units, exactly the engine's expression
    /// (<c>CAurFont::TextOut</c> and <c>CalculateVisibleStringLengthAndWidth</c> agree on it):
    /// <c>(ttf ? lr.z : lr.u - ul.u) * texturewidth</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Advance(byte c) => (Ttf ? LrZ[c] : LrU[c] - UlU[c]) * TextureWidth;
}

/// <summary>One laid-out line, the port of the engine's <c>CStringItem</c>. <see cref="Bytes"/> holds codepage
/// bytes, never UTF-8 (memory note <c>engine-text-codepage</c>).</summary>
public sealed class TextLine
{
    public byte[] Bytes = [];
    /// <summary>Bytes in <see cref="Bytes"/> (there is no NUL terminator; the engine's <c>strlen</c> is this).</summary>
    public int Length;
    /// <summary><c>CStringItem+0x14</c>: how many bytes the draw emits, and the wrap break index.</summary>
    public int VisibleLength;
    /// <summary><c>+0x18</c> line state, <c>+0x1a</c> colour-table index.</summary>
    public short State, Colour;
    /// <summary><c>+0x1c</c> visible width, <c>+0x20</c> line height, <c>+0x24</c> ascent -- all GUI units.</summary>
    public float Width, Height, Ascent;
    /// <summary><c>+0x28</c>: the model riding this line (<c>StringGob::AttachModel</c> @0x5924a0) -- a list
    /// box's selection highlight, drawn at the line's pen under its glyphs.</summary>
    public DbId Model;

    /// <summary>Replace the line's text with <paramref name="src"/>[<paramref name="from"/>..+<paramref name="len"/>],
    /// reusing the buffer when it fits (the engine reallocates on every edit; this is the one deliberate difference
    /// in <c>CStringItem::SetString</c>).</summary>
    public void Set(ReadOnlySpan<byte> src, int from, int len)
    {
        if (Bytes.Length < len) Bytes = new byte[Math.Max(len, 32)];
        src.Slice(from, len).CopyTo(Bytes);
        Length = len;
    }

    public void Prepend(ReadOnlySpan<byte> src, int len)
    {
        byte[] old = Bytes; int n = Length;
        byte[] dst = old.Length >= len + n ? old : new byte[len + n];
        if (ReferenceEquals(dst, old)) Array.Copy(old, 0, old, len, n);
        else Array.Copy(old, 0, dst, len, n);
        src[..len].CopyTo(dst);
        Bytes = dst; Length = len + n;
    }

    public void Append(ReadOnlySpan<byte> src, int len)
    {
        if (Bytes.Length < Length + len) Array.Resize(ref Bytes, Math.Max(Length + len, 32));
        src[..len].CopyTo(Bytes.AsSpan(Length));
        Length += len;
    }

    public ReadOnlySpan<byte> Span => Bytes.AsSpan(0, Length);

    /// <summary><c>CStringItem::InsertString(str, n, at)</c> @0x493cc0, in place.</summary>
    public void Insert(ReadOnlySpan<byte> src, int at)
    {
        if (at < 0) at = 0;
        if (at > Length) at = Length;
        int n = src.Length;
        if (Bytes.Length < Length + n) Array.Resize(ref Bytes, Math.Max(Length + n, 32));
        Bytes.AsSpan(at, Length - at).CopyTo(Bytes.AsSpan(at + n));
        src.CopyTo(Bytes.AsSpan(at));
        Length += n;
    }

    /// <summary><c>CStringItem::DeleteChars(at, count)</c> @0x493d70: <paramref name="count"/> bytes removed
    /// AT <paramref name="at"/>, in place.</summary>
    public void Delete(int at, int count)
    {
        if (at < 0 || count <= 0 || at >= Length) return;
        if (count > Length - at) count = Length - at;
        Bytes.AsSpan(at + count, Length - at - count).CopyTo(Bytes.AsSpan(at));
        Length -= count;
    }
}

/// <summary>The caret, the port of the engine's <c>CCaret</c> at <c>StringGob+0x4d8</c>.</summary>
public struct TextCaret
{
    /// <summary>The caret's glyph, one byte (the engine keeps a 1-char string there).</summary>
    public byte Glyph;
    public int Line, Column;
    /// <summary>Pen offset of the caret inside its line, GUI units (the engine's <c>CAurFont::UpdateCaret</c> result).</summary>
    public float X;
}

/// <summary>One CX text object -- everything <c>StringGob</c> keeps, laid out the same way, plus the quad cache
/// the engine does not have. Mutated on the game thread by the <c>CAurString</c> slots
/// (<see cref="TextSlots"/>); read by the GUI pass.</summary>
public sealed class TextDoc
{
    /// <summary>The record this document belongs to, so the id-free layout core can mark it dirty without the
    /// caller remembering to.</summary>
    public DbId Owner;
    public TextFont? Font;
    /// <summary>Every line, in order. The engine's <c>CExoArrayList&lt;CStringItem*&gt;</c> at <c>+0x500</c>.</summary>
    public TextLine[] Lines = new TextLine[4];
    public int LineCount;
    /// <summary><c>+0x510</c>: the ctor's line ceiling; negative became 0x100000 in the engine.</summary>
    public int MaxLines = 1 << 20;
    /// <summary><c>+0x4c8</c>: wrapping on. The engine's ctor sets it from <c>maxLines != 1</c>.</summary>
    public bool Wrap = true;

    /// <summary><c>+0x518</c>: the colour table, 4 floats an entry. Entry 0 is the default
    /// (<c>SetColorDefault</c>), entry 1 the "selected" one (<c>SetColorSelected</c>).</summary>
    public float[] Colours = [1f, 1f, 1f, 1f];
    public int ColourCount = 1;

    /// <summary><c>+0x4f8</c>/<c>+0x4fc</c>: 0 left/top, 0.5 centre, 1 right/bottom.</summary>
    public float AlignH, AlignV;
    /// <summary><c>+0x534</c>/<c>+0x538</c>: 0 width = unbounded; height <c>&lt;= -5e-06</c> = unbounded.</summary>
    public float MaxWidth, MaxHeight = -1e-5f;
    /// <summary><c>+0x53c</c>: every line's height summed. <c>+0x540</c>: the visible lines' heights summed.</summary>
    public float TotalHeight, VisibleHeight;
    /// <summary><c>+0x528</c>/<c>+0x52c</c>/<c>+0x530</c>: the display block <c>GetDisplayInfo</c> hands out.</summary>
    public int TopLine, VisibleLines, ScrollBudget;
    /// <summary><c>+0x544</c>: scroll inside the top line; <c>+0x548</c>: pixel-scroll mode.</summary>
    public float SubLineScroll;
    public bool PixelScroll;

    /// <summary>Slot 50's handed-out item array: native storage, one entry per line, holding the line's
    /// 1-based index as a sentinel rather than a pointer. The engine's two sort routines permute this array
    /// in place and announce nothing, so <see cref="TextSlots.SyncItemOrder"/> reads the permutation back on
    /// the next slot call. Grow-only, owned by the document; <c>ItemCount</c> is 0 while none is outstanding.</summary>
    internal nint ItemBuf;
    internal int ItemCap, ItemCount;

    /// <summary>Slot 24's handed-out display block (<c>StringGob+0x528</c>): native, one per document, because the
    /// game WRITES through it -- <c>CGuiLabel::LoadGffData</c> @0x68a4c0 sets the box at <c>+0xc</c>/<c>+0x10</c>
    /// and calls nothing. <see cref="TextSlots.SyncDisplayInfo"/> adopts a write on the next slot call;
    /// <c>InfoW</c>/<c>InfoH</c> are what the block held last, so a later <c>SetMaxWidth</c> is not undone.</summary>
    internal nint InfoBuf;
    internal bool InfoOut;
    internal float InfoW, InfoH;

    public TextCaret Caret;
    public bool CaretVisible;
    /// <summary><c>+0x4ec..0x4f4</c>: the caret's colour. The engine's ctor sets (0.5, 1, 1) and nothing writes it
    /// again -- <c>SetColorSelected</c> goes to colour table entry 1, not here.</summary>
    public float CaretR = 0.5f, CaretG = 1f, CaretB = 1f;
    /// <summary>The other end of a text selection as a byte offset into the joined lines, -1 for none. CX's own
    /// (<see cref="EditSelect"/>): the engine has no selection, so nothing it calls reads or clears this.</summary>
    public int SelAnchor = -1;

    /// <summary>The built list the ISSUER walks, taken in ONE reference read. <c>TextQuads.Build</c> runs on the
    /// game thread, <c>TextQuads.Issue</c> in the per-view draw with unchecked pointer arithmetic, so reading
    /// the quads, the runs and the run count as three fields would let a rebuild land between them -- a NEW run
    /// index into the OLD array. Publishing is the one atomic reference assignment. Null until the first build.</summary>
    public QuadList? PublishedQuads;

    private readonly QuadList _a = new(), _b = new();

    /// <summary>The list the next build fills: the one that is NOT published, so the builder never writes an
    /// array an issuer may be walking. Two slots suffice only because of the frame boundary -- a slot is
    /// recycled one build after publication, and <c>RenderSnapshot.Publish</c> waits while the renderer still
    /// holds a frame. A SECOND build of the same document inside one frame takes a fresh list instead.</summary>
    public QuadList NextList()
    {
        QuadList next = ReferenceEquals(PublishedQuads, _a) ? _b : _a;
        return next.PubSeq == RenderSnapshot.FrameSeq ? new QuadList() : next;
    }

    /// <summary>Hand a built list to the issuer. Game thread, at the end of EVERY build -- including one that
    /// produced nothing, so a document that stops drawing says so rather than leaving its last list up.</summary>
    public void PublishQuads(QuadList q)
    {
        q.PubSeq = RenderSnapshot.FrameSeq;
        PublishedQuads = q;
    }

    /// <summary>Record recycling: the document draws nothing again until it is rebuilt. Unpublishing rather
    /// than zeroing the published list, because an issuer may be holding that list right now.</summary>
    public void ResetQuads() => PublishedQuads = null;

    private static readonly QuadList _empty = new();
    /// <summary>The current list: what was published, or an empty one before the first build. Every reader
    /// outside <c>TextQuads.Build</c> -- the cache-hit test, the self-checks -- goes through these, so "the
    /// document's quads" means the published list and nothing else.</summary>
    private QuadList Cur => PublishedQuads ?? _empty;

    /// <summary>The cached glyph quads: 5 floats a vertex (x, y, z, u, v), 6 vertices a glyph, exactly the engine's
    /// stream format 0x804. Valid for <see cref="QuadVerts"/> vertices when <see cref="QuadEpoch"/> matches the
    /// font's; <see cref="Runs"/>/<see cref="RunCount"/> cut it into one draw per markup colour.</summary>
    public float[] Quads => Cur.Quads;
    public int QuadVerts => Cur.QuadVerts;
    public int QuadEpoch = -1;
    /// <summary>The pixel grid the list was built on (<see cref="TextObjects.GridGen"/>): part of the cache key,
    /// because every snapped pen position moves with the grid.</summary>
    public int QuadGrid = -1;
    /// <summary>One colour run: first vertex, vertex count, rgba.</summary>
    public (int First, int Count, float R, float G, float B, float A)[] Runs => Cur.Runs;
    public int RunCount => Cur.RunCount;
    /// <summary>The run holding the caret's glyph, or -1. Its alpha is the only thing in a built list that changes
    /// between frames (the 1 s blink), so the issuer overrides it instead of the build re-running.</summary>
    public int CaretRun => Cur.CaretRun;
    /// <summary>The run holding the selection's highlight boxes, or -1: drawn first and untextured.</summary>
    public int SelRun => Cur.SelRun;
    public (DbId Model, float X, float Y)[] Attached => Cur.Attached;
    public int AttachedCount => Cur.AttachedCount;
    /// <summary>True when <see cref="Quads"/> no longer matches the text, the font, the width or the scale -- the one
    /// gate the ticket's "a string is laid out again only when ... changes" rests on.</summary>
    public bool QuadsStale = true;
    /// <summary>How many times this document has been laid out and had its quads rebuilt; the relayout
    /// self-check asserts on both. <c>TextObjects.Builds</c> is the session total of the second.</summary>
    public int Layouts, Builds;
}

/// <summary>One built glyph list, complete in itself: the quads, their colour runs and the caret's run. The
/// unit of publication -- see <see cref="TextDoc.PublishedQuads"/>.</summary>
public sealed class QuadList
{
    public float[] Quads = [];
    public int QuadVerts;
    public (int First, int Count, float R, float G, float B, float A)[] Runs = [];
    public int RunCount;
    public int CaretRun = -1;
    public int SelRun = -1;
    /// <summary>Models riding visible lines, with the line's pen origin in the text's own space.</summary>
    public (DbId Model, float X, float Y)[] Attached = [];
    public int AttachedCount;
    /// <summary>The frame this list was published in. <see cref="TextDoc.NextList"/> refuses to recycle a slot
    /// published in the CURRENT frame, which is what makes two slots enough.</summary>
    public long PubSeq = -1;
}

/// <summary>CX's text objects -- <c>ObjKind.Text</c> records in <see cref="SceneDb"/> with their state in a
/// side table, an exact port of <c>StringGob</c>'s layout, quirks included: measure
/// <c>CAurFont::CalculateVisibleStringLengthAndWidth</c> @0x496700, break <c>WrapLines</c> @0x494d80, window
/// <c>ShowAsManyLinesAsYouCan_Temp</c> @0x4940a0, placement <c>Render</c> @0x4933a0; quads built per change.</summary>
public static class TextObjects
{
    /// <summary>One physical pixel in GUI units: the engine's <c>CAurora::cm_fGUIToPixel</c>, which
    /// <c>ComputeGUIScaleAndUpdate</c> @0x436ae0 sets to <c>0.01f / guiScale</c> (the scale floors at 1). Set
    /// through <see cref="SetGuiToPixel"/> so the generation moves with it; 0.01 until it is.</summary>
    public static float GuiToPixel { get; private set; } = 0.01f;

    /// <summary>Bumped on every change to <see cref="GuiToPixel"/>. Every snapped pen position depends on the
    /// grid, so a cached quad list keyed only on the font's atlas epoch would survive a scale change the atlas
    /// re-bake does not cover -- the engine re-bakes only past a 0.09 scale delta, while the grid moves on ANY.</summary>
    public static int GridGen { get; private set; } = 1;

    /// <summary>Publish a new pixel grid, moving <see cref="GridGen"/> when it actually changed.</summary>
    public static void SetGuiToPixel(float v)
    {
        if (!(v > 0f) || v == GuiToPixel) return;
        GuiToPixel = v;
        GridGen++;
    }

    /// <summary>GUI units per design pixel. The engine's "pixel" APIs (<c>GetLinePixelWidth</c>,
    /// <c>ScrollToPixel</c>) convert with this hard constant, NOT with <see cref="GuiToPixel"/>.</summary>
    public const float UnitPerPixel = 0.01f;

    /// <summary>The text state of every <c>ObjKind.Text</c> record, indexed by <see cref="DbId.Index"/>.
    /// ponytail: documents are POOLED by index and never released, so the table's high-water mark of line
    /// buffers stays resident for the session. A free list is the upgrade if GUI text ever churns indices.</summary>
    public static readonly TextDoc?[] Docs = new TextDoc?[SceneDb.MaxObjects];

    /// <summary>Totals for <c>/alloy</c>: quad lists built, and the frames a draw was issued from an
    /// already-cached list (the win this ticket exists for).</summary>
    public static long Builds, CacheHits;

    /// <summary>The record behind a text object, or null when the handle is dead or not a text object.</summary>
    public static TextDoc? Of(DbId id)
        => SceneDb.Objects.Valid(id) && SceneDb.ObjKindOf[id.Index] == ObjKind.Text ? Docs[id.Index] : null;

    /// <summary>Mint a text object in <paramref name="scene"/> with the engine's <c>NewCAurString(maxLines)</c>
    /// semantics. Null handle when the object table is full (<see cref="DbTable.Alloc"/> logs that).</summary>
    public static DbId Create(DbId scene, int maxLines)
    {
        DbId id = SceneDb.Objects.Alloc();
        if (id.IsNull) return id;
        int i = id.Index;
        DbFeed.InitObject(i);
        SceneDb.ObjKindOf[i] = ObjKind.Text;
        SceneDb.ObjVisible[i] = true;
        SceneDb.ObjScale[i] = 1f;
        SceneDb.ObjPartFirst[i] = 0; SceneDb.ObjPartCount[i] = 0;
        var d = Docs[i] ??= new TextDoc();
        d.Owner = id;
        TextSlots.ClearExternal(i);   // the engine's ctor zeroes it; a recycled index must not inherit a cookie
        SceneDb.TextPosOffset[i] = default;
        SceneDb.TextOffsetBehavior[i] = 0;
        Reset(d, maxLines);
        // JoinNew, not a bare ObjScene write: without the membership list a scene destroy frees none of its
        // labels, floaties or chat strings. Fresh record, so the O(n) duplicate test is not needed.
        DbScene.JoinNew(scene, id);
        // A StringGob is a Gob in its scene's list, so Scene::HitCheckGob @0x45f860 offers it to every cast --
        // and the engine's two alpha fields start OPAQUE, where an unseeded record's zero reads as "fully
        // transparent" and takes it out of every pick. Without this no label, list row or floaty was ever
        // reachable: the hit columns of a text record had no producer at all.
        HitFeed.NoteObject(id);
        // ...with its own classification, not a model's: a StringGob has no model, and a RECYCLED index whose
        // previous tenant's ModelDef is still in the column would hand the cast that model's bits -- an
        // `effect` among them takes the record out of every pick.
        SceneDb.HitObjClass[i] = 0;
        return id;
    }

    /// <summary>Return a document to its just-constructed state, keeping its buffers.</summary>
    private static void Reset(TextDoc d, int maxLines)
    {
        d.Font = null;
        d.LineCount = 0;
        d.MaxLines = maxLines < 0 ? 1 << 20 : maxLines;   // the ctor rewrites only a NEGATIVE ceiling
        d.Wrap = maxLines != 1;
        d.ColourCount = 1;
        if (d.Colours.Length < 4) d.Colours = new float[4];
        d.Colours[0] = d.Colours[1] = d.Colours[2] = d.Colours[3] = 1f;
        d.AlignH = d.AlignV = 0f;
        d.MaxWidth = 0f; d.MaxHeight = -1e-5f;
        d.TotalHeight = d.VisibleHeight = 0f;
        d.TopLine = d.VisibleLines = d.ScrollBudget = 0;
        d.SubLineScroll = 0f; d.PixelScroll = false;
        d.Caret = default; d.Caret.Glyph = (byte)'_';
        d.CaretVisible = false;
        d.CaretR = 0.5f; d.CaretG = 1f; d.CaretB = 1f;
        d.SelAnchor = -1;
        d.ResetQuads(); d.QuadEpoch = -1; d.QuadsStale = true;
        d.Layouts = 0; d.Builds = 0;
        d.InfoOut = false;
        d.ItemCount = 0;   // a recycled index must not read the previous tenant's sort array back
    }

    public static void Destroy(DbId id)
    {
        if (!SceneDb.Objects.Valid(id)) return;
        TextSlots.ClearExternal(id.Index);
        SceneDb.TextOffsetBehavior[id.Index] = 0;
        DbScene.ObjectDied(id);   // leave the scene's member list before the handle goes
        SceneDb.ObjKindOf[id.Index] = ObjKind.Free;
        SceneDb.Objects.Free(id);
    }

    // ---- the change gate ----

    /// <summary>Something that moves glyphs changed: the quads are stale and the record is dirty, once. Every
    /// mutator here goes through it, so a caller that is not a vtable slot cannot leave <c>Dirty.Text</c> unset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Stale(TextDoc d)
    {
        d.QuadsStale = true;
        if (d.InfoOut) TextSlots.FillDisplayInfo(d);
        SceneDb.Touch(d.Owner, Dirty.Text);
    }

    /// <summary>The slot-layer spelling of <see cref="Stale"/>, for a record whose handle the caller already has.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void MarkText(DbId id, TextDoc d)
    {
        d.QuadsStale = true;
        if (d.InfoOut) TextSlots.FillDisplayInfo(d);
        SceneDb.Touch(id, Dirty.Text);
    }

    // ---- layout: the port ----

    /// <summary><c>CAurora::AlignGUIToPixel(float&amp;)</c> @0x436cf0 -- snap to the physical pixel grid. The
    /// engine's hand-inlined floor agrees with <see cref="MathF.Floor"/> on every input.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float AlignToPixel(float v) => MathF.Floor(v / GuiToPixel + 0.5f) * GuiToPixel;

    /// <summary><c>CAurFont::CalculateVisibleStringLengthAndWidth</c> @0x496700: measure <paramref name="l"/>
    /// under <paramref name="maxWidth"/>, writing its visible length, width, height and ascent; with
    /// <paramref name="doWrap"/> (the engine's third argument) an overflow that saw a space rewinds to it.
    /// Height and ascent GROW, never shrink, so a line measured by two fonts keeps the tallest.</summary>
    public static int Measure(TextFont f, TextLine l, float maxWidth, bool doWrap)
    {
        // The engine returns before touching the item when the font has no texture, and GetSymbolCoords hands
        // back zeroed vectors while a TTF atlas is dirty: an un-baked font measures NOTHING rather than wrongly.
        if (f.NumChars == 0) return 0;
        float asc = f.BaselineHeight + f.SpacingB;
        if (l.Ascent < asc) l.Ascent = asc;
        asc = l.Ascent;
        float descent = f.FontHeight - f.BaselineHeight + f.SpacingB;
        if (l.Height - asc < descent) l.Height = f.FontHeight + asc - f.BaselineHeight + f.SpacingB;

        ReadOnlySpan<byte> s = l.Span;
        int n = s.Length;
        float pen = 0f;
        l.Width = 0f;
        float limit = maxWidth != 0f ? maxWidth + 1.0000001e-05f : 1e6f;
        float lastSpaceW = 0f;
        int lastSpaceIdx = 0;

        for (int i = 0; i < n; i++)
        {
            byte c = s[i];
            if (c == (byte)'<')
            {
                int k = TextMarkup.Skip(s, i);
                if (k >= 2) { i += k - 1; continue; }
            }
            if (c == (byte)' ') { lastSpaceW = pen; lastSpaceIdx = i; }
            pen += f.Advance(c);
            if (pen > limit)
            {
                l.VisibleLength = i;
                if (lastSpaceIdx != 0 && doWrap)
                {
                    l.VisibleLength = lastSpaceIdx;
                    l.Width = lastSpaceW;
                    return 0;
                }
                return c == (byte)'\n' || lastSpaceIdx != 0 ? 0 : 1;
            }
            if (c == (byte)'\n') { l.VisibleLength = i; return 0; }
            l.Width = pen;
            pen += f.SpacingR;
            pen = AlignToPixel(pen);
        }
        l.VisibleLength = n;
        return 0;
    }

    /// <summary><c>StringGob::WrapLines(int)</c> @0x494d80: measure line <paramref name="at"/> and push what does
    /// not fit onto the next line, repeating down the document. Returns -1 when not even one glyph fits.</summary>
    public static int WrapLines(TextDoc d, int at)
    {
        TextFont? f = d.Font;
        if (f == null) return 0;
        for (int i = at; ;)
        {
            TextLine l = d.Lines[i];
            int n = l.Length;
            Measure(f, l, d.MaxWidth, d.Wrap);
            int b = l.VisibleLength;
            // The break index points AT the space, which therefore stays on this line.
            if (b < n && l.Bytes[b] == (byte)' ') b++;
            if (b < n && l.Bytes[b] == (byte)'\n') b++;
            if (b == 0 && n != 0) return -1;
            if (b == n || d.LineCount == d.MaxLines || !d.Wrap) return 0;

            ReadOnlySpan<byte> s = l.Span;
            if (i + 1 < d.LineCount) d.Lines[i + 1].Prepend(s[b..], n - b);
            else
            {
                TextLine next = NewLine(d);
                // The engine copies the state short but NOT the colour short: a continuation line is colour 0.
                next.State = l.State;
                next.Colour = 0;
                next.Width = l.Width;
                next.VisibleLength = l.VisibleLength;
                next.Set(s, b, n - b);
            }
            l.Set(s, 0, b);
            i++;
        }
    }

    /// <summary><c>StringGob::UnWrapLines(int)</c> @0x495040: pull the next line back onto <paramref name="at"/>
    /// and re-wrap, unless this line ends in a newline.</summary>
    public static void UnWrapLines(TextDoc d, int at)
    {
        TextFont? f = d.Font;
        if (f == null) return;
        TextLine l = d.Lines[at];
        l.Height = 0f; l.Ascent = 0f;
        Measure(f, l, d.MaxWidth, false);
        if (d.LineCount == at + 1) return;
        int end = l.VisibleLength;
        if (end < l.Length && l.Bytes[end] == (byte)'\n') return;
        TextLine next = d.Lines[at + 1];
        l.Append(next.Span, next.Length);
        next.Length = 0;
        next.Height = 0f; next.Ascent = 0f;
        Measure(f, next, d.MaxWidth, false);
        WrapLines(d, at);
        if (d.Lines[at + 1].Length == 0) DeleteLine(d, at + 1);
    }

    /// <summary><c>StringGob::RecalcDisplayData</c> @0x4943a0: re-measure every line, sum the heights, then
    /// recompute the visible window. Note that it does NOT zero the per-line height first, so a line once
    /// measured by a tall font stays tall -- an engine behaviour, reproduced.</summary>
    public static void Relayout(TextDoc d)
    {
        d.TotalHeight = 0f;
        TextFont? f = d.Font;
        if (f != null)
            for (int i = 0; i < d.LineCount; i++)
            {
                Measure(f, d.Lines[i], d.MaxWidth, false);
                d.TotalHeight += d.Lines[i].Height;
            }
        ShowAsManyLines(d);
        d.Layouts++;
        Stale(d);
    }

    /// <summary><c>StringGob::ShowAsManyLinesAsYouCan_Temp</c> @0x4940a0: how many lines fit below the top line,
    /// and how many more would fit scrolling back.</summary>
    public static void ShowAsManyLines(TextDoc d)
    {
        d.VisibleHeight = 0f;
        float limit = d.MaxHeight <= -5e-06f ? 1e7f : d.MaxHeight + 0.001f;
        d.VisibleLines = 0;
        int top = d.TopLine, n = d.LineCount;
        float run = 0f;
        int fit = 0;
        if (d.PixelScroll)
        {
            // The pixel-scroll body is NOT the same walk: it measures against limit + the sub-line scroll and it
            // KEEPS the line that overshoots -- a chat log's partially visible top line is that line.
            float ceiling = limit + d.SubLineScroll;
            if (top >= 0 && top < n)
            {
                run = d.Lines[top].Height;
                fit = 1;
                if (run <= ceiling)
                    while (true)
                    {
                        d.VisibleHeight = run;
                        d.VisibleLines = fit;
                        if (fit == n - top) { d.ScrollBudget = fit; Backward(d, top, fit, run, ceiling, true); return; }
                        run += d.Lines[top + fit].Height;
                        fit++;
                        if (run > ceiling) break;
                    }
                d.VisibleHeight = run;
                d.VisibleLines = fit;
            }
            d.ScrollBudget = fit;
            Backward(d, top, fit, run, ceiling, true);
            return;
        }
        if (top >= 0 && top < n)
        {
            run = d.Lines[top].Height;
            if (run <= limit)
                for (int i = top + 1; ; i++)
                {
                    d.VisibleHeight = run;
                    fit = i - top;
                    d.VisibleLines = fit;
                    if (i < 0 || i >= n) break;
                    run += d.Lines[i].Height;
                    if (run > limit) break;
                }
        }
        d.ScrollBudget = fit;
        Backward(d, top, fit, run, limit, false);
    }

    /// <summary>The scrollback budget: how many more lines fit counting backwards from the top line. In
    /// pixel-scroll mode the engine ends one HIGHER than it counted -- the partially visible line above --
    /// which the line-scroll body does not do.</summary>
    private static void Backward(TextDoc d, int top, int fit, float run, float ceiling, bool pixel)
    {
        int stop = top + fit;
        for (int i = top - 1; i >= 0; i--)
        {
            run += d.Lines[i].Height;
            if (run > ceiling) { if (pixel) d.ScrollBudget = fit + 1; return; }
            fit++;
            d.ScrollBudget = fit;
            if (fit == stop) return;
        }
        if (pixel && top > 0) d.ScrollBudget = fit + 1;
    }

    /// <summary><c>StringGob::ScrollToLine(int)</c> @0x494330.</summary>
    public static int ScrollToLine(TextDoc d, int line)
    {
        int n = d.LineCount;
        int to = line > n ? n : line < 0 ? 0 : line;
        bool atEnd = d.VisibleLines + d.TopLine == n && to < d.TopLine;
        d.TopLine = to;
        if (atEnd) ShowAsManyLines(d);
        return to;
    }

    /// <summary><c>StringGob::ScrollToPixel(int)</c> @0x494580: the argument is DESIGN pixels, converted with the
    /// hard 0.01, and the remainder inside the landing line becomes <see cref="TextDoc.SubLineScroll"/>.</summary>
    public static int ScrollToPixel(TextDoc d, int px)
    {
        d.PixelScroll = true;
        int n = d.LineCount;
        d.SubLineScroll = 0f;
        float want = px * UnitPerPixel;
        if (d.TotalHeight <= want) { ScrollToLine(d, n - 1); return -1; }
        int landed = 0;
        if (n >= 1)
        {
            float run = d.Lines[0].Height;
            if (want >= run)
            {
                float prev = run;
                int i = 1;
                for (; i < n; i++)
                {
                    prev = run;
                    run += d.Lines[i].Height;
                    if (run > want) break;
                }
                landed = i < n ? i : n - 1;
                want -= prev;
            }
            d.SubLineScroll = want;
            ShowAsManyLines(d);
        }
        ScrollToLine(d, landed);
        return -1;
    }

    // ---- line editing ----

    /// <summary>Ceiling on the blank lines <c>SetLine</c> grows to reach an index, so a wild index from the
    /// game layer cannot allocate without bound. ponytail: raise it if a panel ever needs more.</summary>
    private const int MaxGrow = 1 << 16;

    internal static TextLine NewLine(TextDoc d)
    {
        if (d.LineCount == d.Lines.Length) Array.Resize(ref d.Lines, d.Lines.Length * 2);
        TextLine l = d.Lines[d.LineCount] ??= new TextLine();
        l.Length = 0; l.VisibleLength = 0; l.State = 0; l.Colour = 0;
        l.Width = 0f; l.Height = 0f; l.Ascent = 0f; l.Model = default;
        d.LineCount++;
        return l;
    }

    /// <summary><c>StringGob::AddLine</c> @0x495400.</summary>
    public static bool AddLine(TextDoc d, ReadOnlySpan<byte> text)
    {
        TextLine l = NewLine(d);
        l.Set(text, 0, text.Length);
        int r = 0;
        if (d.Font != null)
        {
            l.Height = 0f; l.Ascent = 0f;
            r = Measure(d.Font, l, d.MaxWidth, false);
            WrapLines(d, d.LineCount - 1);
        }
        ShowAsManyLines(d);
        Stale(d);
        return r != 0;
    }

    /// <summary><c>StringGob::AddText</c> @0x494f60: appends to the LAST line.</summary>
    public static bool AddText(TextDoc d, ReadOnlySpan<byte> text)
    {
        int i = d.LineCount - 1;
        if (i < 0) return AddLine(d, text);
        TextLine l = d.Lines[i];
        l.Append(text, text.Length);
        int r = d.Font != null ? Measure(d.Font, l, d.MaxWidth, false) : 0;
        Stale(d);
        if (!d.Wrap) return r != 0;
        WrapLines(d, i);
        ShowAsManyLines(d);
        return r != 0;
    }

    /// <summary><c>StringGob::InsertLine</c> @0x4954f0: measures but does NOT wrap.</summary>
    public static void InsertLine(TextDoc d, ReadOnlySpan<byte> text, int at)
    {
        // A vtable slot must never throw: the frame above it is the engine's, and an IndexOutOfRange unwinding
        // through it takes the process down.
        if (at < 0 || at > d.LineCount) return;
        TextLine l = NewLine(d);
        for (int i = d.LineCount - 1; i > at; i--) d.Lines[i] = d.Lines[i - 1];
        d.Lines[at] = l;     // the pooled line rotates into place; nothing is dropped
        l.Set(text, 0, text.Length);
        if (d.Font != null) { l.Height = 0f; l.Ascent = 0f; Measure(d.Font, l, d.MaxWidth, false); }
        ShowAsManyLines(d);
        Stale(d);
    }

    /// <summary><c>StringGob::SetLine</c> @0x495600: grows the document with blank lines up to
    /// <paramref name="at"/>, then measures that one line.</summary>
    public static void SetLine(TextDoc d, int at, ReadOnlySpan<byte> text)
    {
        if (at < 0 || at > MaxGrow) return;
        bool grew = at >= d.LineCount;
        while (at >= d.LineCount) NewLine(d);
        TextLine l = d.Lines[at];
        l.Set(text, 0, text.Length);
        if (d.Font != null) { l.Height = 0f; l.Ascent = 0f; Measure(d.Font, l, d.MaxWidth, false); }
        if (grew) ShowAsManyLines(d);
        Stale(d);
    }

    /// <summary><c>StringGob::DeleteLine</c> @0x494cb0, scroll fix-up included.</summary>
    public static void DeleteLine(TextDoc d, int at)
    {
        if ((uint)at >= (uint)d.LineCount) return;
        TextLine gone = d.Lines[at];
        for (int i = at; i < d.LineCount - 1; i++) d.Lines[i] = d.Lines[i + 1];
        d.Lines[d.LineCount - 1] = gone;
        d.LineCount--;
        int top = d.TopLine;
        if (at < top) { top--; d.TopLine = top; }
        if (d.LineCount < d.VisibleLines + top) d.VisibleLines = d.LineCount - top;
        Stale(d);
    }

    /// <summary><c>StringGob::DeleteText</c> @0x494a50: every line, and the display block back to zero.</summary>
    public static void DeleteText(TextDoc d)
    {
        d.LineCount = 0;
        d.TopLine = 0; d.VisibleLines = 0;
        Stale(d);
    }

    /// <summary><c>StringGob::SetText</c> @0x494b10 = <c>DeleteText</c> + <c>AddText</c> + the caret fix-up.</summary>
    public static bool SetText(TextDoc d, ReadOnlySpan<byte> text)
    {
        DeleteText(d);
        bool r = AddText(d, text);
        int n = d.LineCount;
        int line = d.Caret.Line;
        if (line >= n)
        {
            d.Caret.Column = 1000000;
            line = (n > 0 ? n : 1) - 1;
            d.Caret.Line = line;
        }
        if ((uint)line < (uint)n && d.Lines[line].Length < d.Caret.Column) UpdateCaret(d, line, 1e6f, false);
        return r;
    }

    /// <summary><c>CStringItem::InsertString</c> on a line, with no allocation when the buffer already fits.</summary>
    public static void InsertAt(TextLine l, ReadOnlySpan<byte> text, int at) => l.Insert(text, at);

    /// <summary><c>CStringItem::DeleteChars</c> on a line.</summary>
    public static void DeleteChars(TextLine l, int at, int count) => l.Delete(at, count);

    /// <summary><c>StringGob::SetFont</c> @0x494440's layout half: adopt <paramref name="f"/> and re-lay out.</summary>
    public static void SetFont(TextDoc d, TextFont? f)
    {
        d.Font = f;
        // The engine answers -1 and returns BEFORE RecalcDisplayData when the font did not load; only a font
        // that resolved re-lays out.
        if (f != null) Relayout(d);
    }

    // ---- the caret ----

    /// <summary><c>CAurFont::UpdateCaret</c> @0x496ad0: walk the line's bytes, stopping at the FIRST of the
    /// pen passing <paramref name="limit"/> by more than half the next glyph (a click lands on the nearer gap),
    /// the caret's current column, or a newline when <paramref name="stopAtNewline"/>. It ALWAYS writes both
    /// column and x, so a target x with column -1 turns a coordinate into a column, and the reverse.</summary>
    public static void UpdateCaret(TextDoc d, int line, float limit, bool stopAtNewline)
    {
        TextFont? f = d.Font;
        d.Caret.Line = line;
        if (f == null) { d.Caret.X = 0f; d.Caret.Column = 0; return; }
        ReadOnlySpan<byte> s = (uint)line < (uint)d.LineCount ? d.Lines[line].Span : default;
        float pen = 0f;
        for (int i = 0; i < s.Length; i++)
        {
            float adv = f.Advance(s[i]);
            if (limit < pen + adv * 0.5f || d.Caret.Column == i || (stopAtNewline && s[i] == (byte)'\n'))
            { d.Caret.X = pen; d.Caret.Column = i; return; }
            pen = AlignToPixel(pen + adv + f.SpacingR);
        }
        d.Caret.X = pen;
        d.Caret.Column = s.Length;
    }

    /// <summary>The pen offset of column <paramref name="col"/> in <paramref name="line"/>: <see cref="UpdateCaret"/>'s
    /// walk, so a selection box edge lands where the caret would.</summary>
    public static float PenAt(TextDoc d, int line, int col)
    {
        TextFont? f = d.Font;
        if (f == null || (uint)line >= (uint)d.LineCount) return 0f;
        ReadOnlySpan<byte> s = d.Lines[line].Span;
        float pen = 0f;
        for (int i = 0; i < col && i < s.Length; i++) pen = AlignToPixel(pen + f.Advance(s[i]) + f.SpacingR);
        return pen;
    }

    /// <summary>The caret's blink alpha: <c>0.25*cos(2*pi*t) + 0.75</c>, a 1 s period over [0.5, 1], from
    /// <c>StringGob::Render</c>'s <c>CExoTimers::GetMillisecondTimer</c> term.</summary>
    public static float CaretAlpha(uint milliseconds)
        => (float)(Math.Cos(2.0 * Math.PI * milliseconds / 1000.0) * 0.25 + 0.75);

    // ---- colours ----

    /// <summary><c>StringGob::SetIndexedColor</c> @0x493270: grows the table on demand.</summary>
    public static void SetIndexedColour(TextDoc d, int index, float r, float g, float b, float a)
    {
        if (index < 0) return;
        if (index >= d.ColourCount)
        {
            if (d.Colours.Length < (index + 1) * 4) Array.Resize(ref d.Colours, (index + 1) * 4);
            d.ColourCount = index + 1;
        }
        int o = index * 4;
        d.Colours[o] = r; d.Colours[o + 1] = g; d.Colours[o + 2] = b; d.Colours[o + 3] = a;
        Stale(d);
    }

    /// <summary>The rgba of a line's colour-table entry, clamped into the table like the engine's raw index read
    /// cannot be (the engine reads past the table for an out-of-range index; CX refuses and uses entry 0,
    /// which is the one deliberate divergence in the colour path).</summary>
    public static (float R, float G, float B, float A) ColourOf(TextDoc d, int index)
    {
        int i = (uint)index < (uint)d.ColourCount ? index : 0;
        int o = i * 4;
        return (d.Colours[o], d.Colours[o + 1], d.Colours[o + 2], d.Colours[o + 3]);
    }
}
