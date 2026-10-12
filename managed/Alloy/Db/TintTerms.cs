using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>The palette tint's SIDE TABLE (<c>cx_tintbuf.glsl</c>): the terms
/// <see cref="ShaderFeature.PaletteTint"/>'s hosted stage needs and nothing else in the world reads -- its
/// illumination colour, its coverage map's mip scale, its lobe, its sway and its four palette maps as
/// bindless handles. A row is per MATERIAL, so the record carries only its index, in <c>feat.z</c>.</summary>
internal static unsafe class TintTerms
{
    /// <summary>SSBO binding of the table. A SHARED point (<see cref="SsboSlots.Shared"/> and up), rebound at
    /// every lit program bind: nothing else in the lit bracket claims it, and its other tenants rebind it.</summary>
    internal const uint Binding = SsboSlots.Tint;

    /// <summary>Rows held at once -- distinct palette-tint MATERIALS, not parts. A body-part set is a handful
    /// per creature model; a session that visits many areas accumulates, and a retired recipe hands its row
    /// back. Spill is counted and named, never silent.</summary>
    internal const int MaxRows = 256;

    /// <summary>Words per row, matching <c>cx_tintbuf.glsl</c>'s <c>CxTint</c>: two <c>vec4</c> then two
    /// <c>uvec4</c> = 64 bytes, whose std430 alignment (16) already divides the size.</summary>
    internal const int WordsPerRow = 16;
    /// <summary>Where each member starts in the row: <c>glow</c> (illumination rgb, alpha mip scale),
    /// <c>terms</c> (anisotropy, specular intensity, sway speed, sway strength), then the four
    /// <c>texture7</c>..<c>texture10</c> handles, two words each.</summary>
    internal const int LaneGlow = 0, LaneTerms = 4, LanePal = 8;

    /// <summary>Palette map slots a row carries, which is <see cref="AlloyRenderer.PalSlots"/>.</summary>
    internal const int PalSlots = AlloyRenderer.PalSlots;

    /// <summary>Which palette slot the COVERAGE comes from: <c>texture9</c>, the third of the four.</summary>
    internal const int CoverPal = 2;

    /// <summary>Everything mutable, in one owner: context loss replaces the GL half and a fresh session the
    /// rest, so no name from a dead context survives as a field nobody remembered to zero.</summary>
    private sealed class TintState
    {
        public readonly uint[] Words = new uint[MaxRows * WordsPerRow];
        /// <summary>Which recipe owns each row, or -1. The row's handles are re-read off it at every
        /// <see cref="Upload"/>: a palette map lands asynchronously, and the registry re-makes a texture
        /// under us.</summary>
        public readonly int[] Owner = new int[MaxRows];
        public uint Ssbo;
        public int Rows, Sized, DirtyLo = int.MaxValue, DirtyHi = -1;
        public long Uploads, HandleWrites, Spilled;
        /// <summary>Uploads whose dirty span reached past the buffer, and so were HELD rather than dropped.</summary>
        public long Short;
        /// <summary>Has a row ever existed? Until one has, the GPU block is ONE zero row rather than the
        /// whole table, and the stage's length test turns every read into a defined false.</summary>
        public bool AnyRow, SaidNoBindless;
        public ExtenderLog? Log;

        public TintState() { for (int r = 0; r < MaxRows; r++) Owner[r] = -1; }
    }

    private static TintState _st = new();
    /// <summary>One lock over the row words. A row is written where its RECIPE is interned (the scene feed)
    /// and read where the block is uploaded (the Alloy bracket), which are not the same thread; the writes
    /// are per material and the read walks at most <see cref="MaxRows"/> rows a frame, so neither is hot.</summary>
    private static readonly object _lock = new();

    internal static void Attach(ExtenderLog log) => _st.Log = log;

    /// <summary>Rows in use, for the period line and the self-check.</summary>
    internal static int Rows => _st.Rows;

    /// <summary>A row for recipe <paramref name="recipe"/>, or -1 when the table is full. The index IS what
    /// the record carries, so it is stable for the recipe's life.</summary>
    internal static int Alloc(int recipe)
    {
        lock (_lock) return AllocLocked(recipe);
    }

    private static int AllocLocked(int recipe)
    {
        TintState s = _st;
        for (int r = 0; r < MaxRows; r++)
        {
            if (s.Owner[r] >= 0) continue;
            s.Owner[r] = recipe;
            s.Rows++;
            if (!s.AnyRow)
                s.Log?.Always("TintTerms: a palette-tint material is in sight -- its hosted fragment stage "
                            + "runs (palette rows, glow, lobe and coverage), and its VERTEX half does NOT: "
                            + "Alloy hosts fragment stages only, so a palette-tint part does not sway");
            s.AnyRow = true;
            Array.Clear(s.Words, r * WordsPerRow, WordsPerRow);
            Touch(r);
            return r;
        }
        if (s.Spilled++ == 0)
            s.Log?.Always($"TintTerms: {MaxRows} palette-tint materials is the table's whole capacity -- "
                        + "further ones draw the plain lit path, with no tint, no glow and no coverage map");
        return -1;
    }

    /// <summary>Recipe <paramref name="recipe"/> is going: its row goes back. A row another recipe already
    /// holds is left alone, because the retire paths may ask twice.</summary>
    internal static void Free(int row, int recipe)
    {
        lock (_lock) FreeLocked(row, recipe);
    }

    private static void FreeLocked(int row, int recipe)
    {
        TintState s = _st;
        if ((uint)row >= MaxRows || s.Owner[row] != recipe) return;
        s.Owner[row] = -1;
        s.Rows--;
        Array.Clear(s.Words, row * WordsPerRow, WordsPerRow);
        Touch(row);
    }

    /// <summary>Write row <paramref name="row"/>'s material terms, in <see cref="MtrParams.FoldTint"/>'s
    /// order. Called once when the recipe is defined -- a <c>.mtr</c> does not animate.</summary>
    internal static void SetTerms(int row, ReadOnlySpan<float> f)
    {
        if ((uint)row >= MaxRows || f.Length != MtrParams.TintTermCount) return;
        int b = row * WordsPerRow + LaneGlow;
        bool moved = false;
        lock (_lock)
        {
            for (int k = 0; k < f.Length; k++)
            {
                uint w = BitConverter.SingleToUInt32Bits(f[k]);
                if (_st.Words[b + k] == w) continue;
                _st.Words[b + k] = w; moved = true;
            }
            if (moved) Touch(row);
        }
    }

    /// <summary>One palette map's handle into row <paramref name="row"/>, slot <paramref name="k"/> of
    /// <see cref="PalSlots"/> (<c>texture7</c> is 0). A zero handle is "nothing bound", which the stage reads
    /// as a coverage of nothing rather than as some other material's texture.</summary>
    private static bool SetHandle(int row, int k, ulong handle)
    {
        int b = row * WordsPerRow + LanePal + k * 2;
        uint lo = (uint)handle, hi = (uint)(handle >> 32);
        if (_st.Words[b] == lo && _st.Words[b + 1] == hi) return false;
        _st.Words[b] = lo; _st.Words[b + 1] = hi;
        _st.HandleWrites++;
        return true;
    }

    private static void Touch(int row)
    {
        if (row < _st.DirtyLo) _st.DirtyLo = row;
        if (row > _st.DirtyHi) _st.DirtyHi = row;
    }

    /// <summary>Re-read every live row's palette handles off its recipe, push what moved and bind the block.
    /// In Alloy's bracket, beside the PLT rows': a map lands asynchronously and the registry re-makes a
    /// texture under us, so the handles are a per-frame read of at most <see cref="MaxRows"/> rows rather
    /// than a subscription. Bound on EVERY frame -- an unbound binding is an undefined read.</summary>
    internal static void Upload()
    {
        if (Gl.CreateBuffers == null || Gl.NamedBufferData == null) return;
        lock (_lock) UploadLocked();
    }

    private static void UploadLocked()
    {
        TintState s = _st;
        if (s.Rows > 0)
        {
            bool handles = AlloyRenderer.Bindless;
            if (!handles && !s.SaidNoBindless)
            {
                s.SaidNoBindless = true;
                s.Log?.Always("TintTerms: a palette-tint material is in sight and the bindless handle table is "
                            + "OFF -- the lit stage spends texture units 0..30, so its four palette maps have "
                            + "nowhere to sit. Those parts draw NOTHING, their coverage reading zero; "
                            + "`alloy.bindless 1` plus a restart is what turns the feature on");
            }
            // a null handle is a GPU fault on AMD's driver, so every empty slot takes a stand-in's instead
            ulong whiteH = handles ? AlloyRenderer.HandleFor(OwnedTextures.WhiteTid()) : 0ul;
            ulong nullH = handles ? AlloyRenderer.HandleFor(OwnedTextures.NullTid()) : 0ul;
            for (int r = 0; r < MaxRows; r++)
            {
                if (s.Owner[r] < 0) continue;
                bool moved = false;
                for (int k = 0; k < PalSlots; k++)
                {
                    ulong h = handles ? DrawRecipe.PalHandle(s.Owner[r], k) : 0ul;
                    if (h == 0) h = k == CoverPal ? whiteH : nullH;
                    moved |= SetHandle(r, k, h);
                }
                if (moved) Touch(r);
            }
        }
        int want = s.AnyRow ? s.Words.Length : WordsPerRow;
        if (s.Ssbo == 0 || want != s.Sized)
        {
            if (s.Ssbo == 0) { uint b; Gl.CreateBuffers(1, &b); if (b == 0) return; s.Ssbo = b; }
            fixed (uint* p = s.Words) Gl.NamedBufferData(s.Ssbo, (IntPtr)(want * sizeof(uint)), p, Gl.DYNAMIC_DRAW);
            s.Sized = want;
            s.DirtyLo = int.MaxValue; s.DirtyHi = -1;
            s.Uploads++;
        }
        else if (s.DirtyHi >= s.DirtyLo && Gl.NamedBufferSubData != null)
        {
            int off = s.DirtyLo * WordsPerRow, n = (s.DirtyHi - s.DirtyLo + 1) * WordsPerRow;
            if (off + n > s.Sized)
            {
                // the dirty span is KEPT: a row dropped here is one the GPU never gets, and the terms behind
                // a record's index would stay whatever the last tenant wrote -- another material's glow
                if (s.Short++ == 0)
                    s.Log?.Always($"TintTerms: rows {s.DirtyLo}..{s.DirtyHi} reach past the {s.Sized}-word "
                                + "buffer -- held dirty for the next upload rather than dropped, which would "
                                + "leave a palette material wearing another one's terms");
                Bind();
                return;
            }
            fixed (uint* p = s.Words)
                Gl.NamedBufferSubData(s.Ssbo, (IntPtr)(off * sizeof(uint)), (IntPtr)(n * sizeof(uint)), p + off);
            s.Uploads++;
            s.DirtyLo = int.MaxValue; s.DirtyHi = -1;
        }
        Bind();
    }

    /// <summary>Bind the block on <see cref="Binding"/>. Called at every lit program bind: the point is a
    /// shared one, and a reader that trusted an earlier bind would read the particle index block.</summary>
    internal static void Bind()
    {
        if (_st.Ssbo != 0 && Gl.BindBufferBase != null)
            Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, Binding, _st.Ssbo);
    }

    /// <summary>Terminal per session: the buffer belonged to the lost context. The rows themselves are CPU
    /// state, and go up whole on the next <see cref="Upload"/>.</summary>
    internal static void ContextLost()
    {
        _st.Ssbo = 0; _st.Sized = 0;
        _st.DirtyLo = 0; _st.DirtyHi = MaxRows - 1;
    }

    /// <summary>The row word at <paramref name="word"/>, for the self-check and the live verifier.</summary>
    internal static uint WordAt(int row, int word)
        => (uint)row < MaxRows ? _st.Words[row * WordsPerRow + word] : 0;

    internal static string Note()
        => _st.Rows == 0 && _st.Uploads == 0 ? ""
         : $"tintterms: {_st.Rows} palette materials, {_st.Uploads} uploads, {_st.HandleWrites} handle writes"
         + (_st.Spilled == 0 ? "" : $", {_st.Spilled} SPILLED past {MaxRows}")
         + (_st.Short == 0 ? "" : $", {_st.Short} upload(s) HELD past the buffer's end");

    /// <summary>Off-device: the row layout against the GLSL declaration text, and the allocation contract.
    /// Oracle: <c>cx_tintbuf.glsl</c>'s own <c>CxTint</c> declaration, its binding literal and its coverage
    /// slot -- a second artifact rather than a restatement -- plus hand-written values through a row.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TintTerms.SelfCheck");
        string src = ShaderSource.Get("cx_tintbuf.glsl");
        check.Expect(src.Contains("struct CxTint { vec4 glow; vec4 terms; uvec4 pal01; uvec4 pal23; };"),
          $"cx_tintbuf.glsl does not declare the {WordsPerRow}-word row this stages (2 vec4 + 2 uvec4)");
        check.Expect(src.Contains($"layout(std430, binding = {Binding}) readonly buffer CxTintBuf"),
          $"cx_tintbuf.glsl does not declare CxTintBuf at binding {Binding}");
        check.Expect(src.Contains($"#define CX_TINT_COVER_PAL {CoverPal}"),
          $"the coverage map is palette slot {CoverPal} here and something else in the GLSL");
        // ...and the lanes themselves, computed from the GLSL's own member list (four words per vec4/uvec4
        // under std430) rather than restated -- a member inserted there and not here is a row read crooked.
        var members = System.Text.RegularExpressions.Regex.Matches(src, @"\b(u?vec4)\s+(\w+);");
        int at = 0;
        foreach (System.Text.RegularExpressions.Match m in members)
        {
            int want = m.Groups[2].Value switch { "glow" => LaneGlow, "terms" => LaneTerms, "pal01" => LanePal, _ => -1 };
            if (want >= 0) check.Expect(at == want, $"the GLSL puts {m.Groups[2].Value} at word {at}, this stages it at {want}");
            at += 4;
        }
        check.Expect(at == WordsPerRow, $"the GLSL row is {at} words and this stages {WordsPerRow}");

        int row = Alloc(recipe: int.MaxValue);
        check.Expect(row >= 0, "the table would not allocate a row");
        if (row < 0) { log.Info("TintTerms.SelfCheck: FAILED"); return false; }
        Span<float> terms = stackalloc float[MtrParams.TintTermCount] { 0.25f, 0.5f, 0.75f, 1.5f, 0.6f, 2f, 3f, 4f };
        SetTerms(row, terms);
        check.Expect(BitConverter.UInt32BitsToSingle(WordAt(row, LaneGlow + 2)) == 0.75f
          && BitConverter.UInt32BitsToSingle(WordAt(row, LaneGlow + 3)) == 1.5f,
          "the illumination colour's blue or the alpha mip scale did not land in the glow lane");
        check.Expect(BitConverter.UInt32BitsToSingle(WordAt(row, LaneTerms)) == 0.6f
          && BitConverter.UInt32BitsToSingle(WordAt(row, LaneTerms + 1)) == 2f
          && BitConverter.UInt32BitsToSingle(WordAt(row, LaneTerms + 3)) == 4f,
          "the anisotropy, the specular intensity or the sway strength did not land in the terms lane");
        SetHandle(row, CoverPal, 0x1122334455667788ul);
        check.Expect(WordAt(row, LanePal + CoverPal * 2) == 0x55667788u
          && WordAt(row, LanePal + CoverPal * 2 + 1) == 0x11223344u,
          "a 64-bit palette handle is not stored low word first, which is how a uvec2 reads it");
        Free(row, int.MaxValue);
        check.Expect(WordAt(row, LaneTerms) == 0 && Alloc(recipe: int.MaxValue) == row,
          "a freed row was not cleared, or did not come back to the next ask");
        Free(row, int.MaxValue);
        log.Info(check.Ok ? $"TintTerms.SelfCheck: passed ({MaxRows} rows of {WordsPerRow} words at binding {Binding})"
                          : "TintTerms.SelfCheck: FAILED");
        return check.Ok;
    }
}
