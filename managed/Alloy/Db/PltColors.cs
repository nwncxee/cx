using System;
using System.Runtime.CompilerServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>A creature's PLT colours, as per-instance DATA rather than a composite: the PLT file is held once
/// (<see cref="TexKind.Plt"/>, RG8) and the palettes once (<see cref="PltPalettes"/>), with only the ten ROW
/// indices per object -- so a colour change is one byte into <see cref="SceneDb.ObjPltRow"/> plus a dirty bit.
/// The rows reach the shader as one <c>uvec4</c> per DRAW record in a block of its own (<see cref="Binding"/>).</summary>
internal static unsafe class PltColors
{
    /// <summary>Layers a PLT pixel may name: ten, one palette each.</summary>
    internal const int Layers = SceneDb.PltLayers;

    /// <summary>SSBO binding of the per-record PLT row block: persistent, bound once per frame.</summary>
    internal const uint Binding = SsboSlots.Plt;

    /// <summary>Records the block holds: one per ownership slot plus the sky's reserved block, i.e. exactly
    /// <c>AlloyRenderer.MaxRecords</c> -- the block is indexed by the same slot the draw record is.</summary>
    private static int Records => AlloyRenderer.MaxRecords;

    /// <summary>Six uints per record: five row words holding the ten rows two per word (16 bits each, because
    /// a master-palette row runs to 1791 and does not fit a byte), then a flag word whose low bit says this
    /// draw's diffuse IS a PLT. A zero flag word therefore means "not layered", which is what every
    /// non-creature record leaves it as.</summary>
    internal const int WordsPerRecord = 6, RowWords = 5;
    /// <summary>The flag word's bits: the draw is PLT-resolved, and its sampler clamps rather than repeats
    /// (<c>cx_plt.glsl</c> does its own addressing and cannot see a sampler's wrap mode). The third says the
    /// rows are staged for a reader that is NOT the diffuse resolve -- the palette tint, whose diffuse is an
    /// ordinary texture and whose hosted stage still reads the wearer's scheme.</summary>
    internal const uint FlagPlt = 1, FlagClamp = 2, FlagRows = 4;

    private static uint[] _words = Array.Empty<uint>();
    private static uint _ssbo;
    private static int _dirtyLo = int.MaxValue, _dirtyHi = -1;
    private static long _writes, _uploads, _colorWrites;

    internal static void Attach(ExtenderLog log)
    {
        if (_words.Length == 0) _words = new uint[Records * WordsPerRecord];
    }

    // ---- the colour-change path: one record write ----

    /// <summary>Set object <paramref name="obj"/>'s palette row for <paramref name="layer"/>. This is the
    /// whole of the game's colour path (<c>AurSetPalette</c> and the appearance setters route here): a byte
    /// and a dirty bit, never a re-composite.</summary>
    internal static void SetRow(DbId obj, int layer, int row)
    {
        if (!SceneDb.Objects.Valid(obj) || (uint)layer >= Layers) return;
        int at = obj.Index * Layers + layer;
        ushort v = (ushort)Clamp(row);
        if (SceneDb.ObjPltRow[at] == v) return;
        SceneDb.ObjPltRow[at] = v;
        _colorWrites++;
        SceneDb.Objects.Mark(obj.Index, Dirty.Material);
    }

    /// <summary>All ten rows at once -- the shape <c>AurTextureSetLayered(tex, name, int* rows, int count)</c>
    /// and <c>Material::ReplaceTexturePLT</c> hand over, so the seam passes the engine's own array straight
    /// through.</summary>
    internal static void SetRows(DbId obj, ReadOnlySpan<int> rows)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int n = Math.Min(rows.Length, Layers);
        bool changed = false;
        for (int l = 0; l < n; l++)
        {
            int at = obj.Index * Layers + l;
            ushort v = (ushort)Clamp(rows[l]);
            if (SceneDb.ObjPltRow[at] == v) continue;
            SceneDb.ObjPltRow[at] = v;
            changed = true;
        }
        if (!changed) return;
        _colorWrites++;
        SceneDb.Objects.Mark(obj.Index, Dirty.Material);
    }

    /// <summary>Pin <paramref name="rows"/> on part <paramref name="part"/> alone: the rows of the
    /// <c>ReplaceTexture</c> that swapped its texture, which win over its object's from then on.</summary>
    internal static void SetPartRows(int part, ReadOnlySpan<int> rows)
    {
        if ((uint)part >= (uint)SceneDb.MaxParts) return;
        int n = Math.Min(rows.Length, Layers), at = part * Layers;
        bool changed = !SceneDb.PartPltOwn[part];
        for (int l = 0; l < Layers; l++)
        {
            ushort v = l < n ? (ushort)Clamp(rows[l]) : (ushort)0;
            if (SceneDb.PartPltRow[at + l] == v) continue;
            SceneDb.PartPltRow[at + l] = v;
            changed = true;
        }
        SceneDb.PartPltOwn[part] = true;
        if (!changed) return;
        _colorWrites++;
        DbId obj = SceneDb.PartObj[part];
        if (SceneDb.Objects.Valid(obj)) SceneDb.Objects.Mark(obj.Index, Dirty.Material);
    }

    /// <summary>Part <paramref name="part"/> goes back to its object's rows (<c>Gob::RestoreTexture</c>).</summary>
    internal static void ClearPart(int part)
    {
        if ((uint)part < (uint)SceneDb.MaxParts) SceneDb.PartPltOwn[part] = false;
    }

    /// <summary>A piece grafted onto <paramref name="parent"/> takes the parent's rows when it has none. The
    /// engine's colour is on the layered texture, so a body part that arrives after the colour call still
    /// draws coloured; CX's piece is a fresh object whose rows would otherwise be row 0 on every layer --
    /// <c>pal_skin01</c>'s first colour over metal, cloth and leather alike.</summary>
    internal static void Inherit(DbId child, DbId parent)
    {
        if (!SceneDb.Objects.Valid(child) || !SceneDb.Objects.Valid(parent)) return;
        int c = child.Index * Layers, p = parent.Index * Layers;
        for (int l = 0; l < Layers; l++) if (SceneDb.ObjPltRow[c + l] != 0) return;
        Array.Copy(SceneDb.ObjPltRow, p, SceneDb.ObjPltRow, c, Layers);
        SceneDb.Objects.Mark(child.Index, Dirty.Material);
    }

    /// <summary>A row outside the master palette would sample off the texture. Clamped and counted rather
    /// than trusted: a scheme integer comes from content.</summary>
    private static int Clamp(int row)
    {
        if ((uint)row < PltPalettes.Height) return row;
        _clamped++;
        return row < 0 ? 0 : PltPalettes.Height - 1;
    }
    private static long _clamped;

    /// <summary>The object record is going: zero its rows. A <see cref="DbId"/> index is recycled and these
    /// are not derived from anything the next tenant sets, so without this a new creature wears the dead
    /// one's skin, hair and armour. No dirty mark -- the record is being freed, and the free marks it.</summary>
    internal static void Free(DbId obj)
    {
        if ((uint)obj.Index >= SceneDb.MaxObjects) return;
        Array.Clear(SceneDb.ObjPltRow, obj.Index * Layers, Layers);
    }

    internal static ushort Row(DbId obj, int layer)
        => SceneDb.Objects.Valid(obj) && (uint)layer < Layers ? SceneDb.ObjPltRow[obj.Index * Layers + layer] : (ushort)0;

    // ---- the draw record's side ----

    /// <summary>Stage record <paramref name="slot"/>'s row word from part <paramref name="part"/>'s own rows,
    /// else object <paramref name="obj"/>'s -- for a part whose diffuse is a PLT, and with
    /// <paramref name="rowsOnly"/> for one whose SHADER reads the wearer's scheme off an ordinary diffuse.
    /// Every other record is cleared by <see cref="Clear"/>; allocation-free, as the staging walk is hot.</summary>
    internal static void Stage(int slot, DbId obj, int part = -1, bool clamp = false, bool rowsOnly = false)
    {
        if ((uint)slot >= (uint)Records || _words.Length == 0) return;
        if (!SceneDb.Objects.Valid(obj))
        {
            // A record staged against a dead object would read ObjPltRow at a raw index: the rows of whoever
            // holds that slot now, or past the column. Clear the flag and count it instead.
            _badObj++;
            Clear(slot);
            return;
        }
        bool own = (uint)part < (uint)SceneDb.MaxParts && SceneDb.PartPltOwn[part];
        int at = own ? part * Layers : obj.Index * Layers, b = slot * WordsPerRecord;
        ushort[] r = own ? SceneDb.PartPltRow : SceneDb.ObjPltRow;
        bool changed = false;
        for (int k = 0; k < RowWords; k++)
        {
            uint w = r[at + k * 2] | ((uint)r[at + k * 2 + 1] << 16);
            if (_words[b + k] == w) continue;
            _words[b + k] = w; changed = true;
        }
        uint flags = rowsOnly ? FlagRows : FlagPlt | (clamp ? FlagClamp : 0u);
        if (_words[b + RowWords] != flags) { _words[b + RowWords] = flags; changed = true; }
        if (changed) { _everPlt = true; Touch(slot); }
    }

    /// <summary>Records staged against an object that was not live, and therefore cleared.</summary>
    private static long _badObj;
    /// <summary>Has any record ever carried the PLT flag? Until one has, the GPU block stays a
    /// <see cref="WordsPerRecord"/>-word zero buffer rather than the full table, and <c>cx_plt.glsl</c>'s
    /// <c>length()</c> test turns that into a defined false.</summary>
    private static bool _everPlt;

    /// <summary>Does record <paramref name="slot"/> currently carry the PLT flag? A plain read; the stage uses
    /// it to keep a record that must be CLEARED on the main thread.</summary>
    internal static bool Flagged(int slot)
        => (uint)slot < (uint)Records && _words.Length != 0 && _words[slot * WordsPerRecord + RowWords] != 0;

    /// <summary>Record <paramref name="slot"/> is not PLT-resolved (the ordinary case). Only the flag word is
    /// cleared: the rows are stale but never read, and zeroing six words per non-PLT record every restage is
    /// work the whole scene would pay for a handful of creatures.</summary>
    internal static void Clear(int slot)
    {
        if ((uint)slot >= (uint)Records || _words.Length == 0) return;
        int b = slot * WordsPerRecord + RowWords;
        if (_words[b] == 0) return;
        _words[b] = 0;
        Touch(slot);
    }

    private static void Touch(int slot)
    {
        if (slot < _dirtyLo) _dirtyLo = slot;
        if (slot > _dirtyHi) _dirtyHi = slot;
        _writes++;
    }

    // ---- the staleness audit's mirror (CX-120) ----

    /// <summary>What the last <see cref="Upload"/> pushed, word for word. Allocated only while
    /// <c>alloy.staleaudit</c> is armed: a record whose words differ from this is one the GPU never got.</summary>
    private static uint[] _mirror = Array.Empty<uint>();

    private static void Mirror(int from, int n)
    {
        if (!AlloyRenderer.StaleAuditOn) return;
        if (_mirror.Length != _words.Length) _mirror = new uint[_words.Length];
        if (from + n <= _words.Length) Array.Copy(_words, from, _mirror, from, n);
    }

    /// <summary>Which of record <paramref name="slot"/>'s words the GPU does not have, or null.</summary>
    internal static string? MirrorDiff(int slot)
    {
        if (_mirror.Length != _words.Length || (uint)slot >= (uint)Records) return null;
        int b = slot * WordsPerRecord;
        for (int k = 0; k < WordsPerRecord && b + k < _words.Length; k++)
            if (_words[b + k] != _mirror[b + k])
                return $"PLT word {k} is 0x{_words[b + k]:x8} on the CPU and 0x{_mirror[b + k]:x8} on the GPU "
                     + "(the rows changed without reaching the upload)";
        return null;
    }

    /// <summary>Which of record <paramref name="slot"/>'s staged rows disagree with the database's rows for
    /// its part or object, or null. The audit's second question: the staged word against the live column.</summary>
    internal static string? ExpectedDiff(int slot, DbId obj, int part)
    {
        if ((uint)slot >= (uint)Records || _words.Length == 0 || !SceneDb.Objects.Valid(obj)) return null;
        bool own = (uint)part < (uint)SceneDb.MaxParts && SceneDb.PartPltOwn[part];
        int at = own ? part * Layers : obj.Index * Layers, b = slot * WordsPerRecord;
        ushort[] r = own ? SceneDb.PartPltRow : SceneDb.ObjPltRow;
        for (int k = 0; k < RowWords; k++)
        {
            uint w = r[at + k * 2] | ((uint)r[at + k * 2 + 1] << 16);
            if (_words[b + k] != w)
                return $"staged rows word {k} is 0x{_words[b + k]:x8}, the database says 0x{w:x8} "
                     + $"({(own ? "the part's own rows" : "its object's rows")})";
        }
        return null;
    }

    /// <summary>The rows word a record carries, for the self-check and the live verifier.</summary>
    internal static uint WordAt(int slot, int word)
        => (uint)slot < (uint)Records && _words.Length != 0 ? _words[slot * WordsPerRecord + word] : 0;

    /// <summary>Push the records written since the last call and bind the block. In Alloy's bracket; uploads
    /// only the touched span, so a still scene costs one bind. On Vulkan it only counts a send for that frame.</summary>
    internal static void Upload()
    {
        if (_words.Length == 0) return;
        if (Renderer.OnVulkan)
        {
            // the Vulkan frame re-sends Sent whole when Sends moves
            int w = _everPlt ? _words.Length : WordsPerRecord;
            if (w != _sized || _dirtyHi >= _dirtyLo) { _sized = w; Mirror(0, w); _uploads++; }
            _dirtyLo = int.MaxValue; _dirtyHi = -1;
            return;
        }
        if (Gl.CreateBuffers == null || Gl.NamedBufferData == null) return;
        // The block is bound on EVERY frame, in both modes. cx_plt.glsl is part of the lit stage either way,
        // and an unbound shader-storage binding is an undefined read: one stray bit in the flag word would
        // resolve a wall through the palette. Until a PLT record exists the buffer is one record of zeros, so
        // the shader's `i < uPltRow.length()` makes that read defined and false (review r1, BLOCKER 3).
        int want = _everPlt ? _words.Length : WordsPerRecord;
        if (_ssbo == 0 || want != _sized)
        {
            if (_ssbo == 0) { uint b; Gl.CreateBuffers(1, &b); if (b == 0) return; _ssbo = b; }
            fixed (uint* p = _words) Gl.NamedBufferData(_ssbo, (IntPtr)(want * sizeof(uint)), p, Gl.DYNAMIC_DRAW);
            _sized = want;
            Mirror(0, want);
            _dirtyLo = int.MaxValue; _dirtyHi = -1;
            _uploads++;
        }
        else if (_dirtyHi >= _dirtyLo && Gl.NamedBufferSubData != null)
        {
            int off = _dirtyLo * WordsPerRecord, n = (_dirtyHi - _dirtyLo + 1) * WordsPerRecord;
            if (off + n <= _sized)
            {
                fixed (uint* p = _words.AsSpan(off))
                    Gl.NamedBufferSubData(_ssbo, (IntPtr)(off * sizeof(uint)), (IntPtr)(n * sizeof(uint)), p);
                _uploads++;
                Mirror(off, n);
            }
            _dirtyLo = int.MaxValue; _dirtyHi = -1;
        }
        if (Gl.BindBufferBase != null) Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, Binding, _ssbo);
    }

    /// <summary>The words the GPU buffer holds, and how many sends there have been: a second backend re-sends them
    /// whole when the count moves.</summary>
    internal static ReadOnlySpan<uint> Sent => _words.AsSpan(0, Math.Min(_sized, _words.Length));

    /// <inheritdoc cref="Sent"/>
    internal static long Sends => _uploads;

    /// <summary>Words the GPU buffer currently holds: <see cref="WordsPerRecord"/> until a PLT record exists,
    /// the whole table after.</summary>
    private static int _sized;

    /// <summary>Terminal per session: the buffer belonged to the lost context. The rows themselves live in
    /// <see cref="SceneDb.ObjPltRow"/> and are re-uploaded whole on the next <see cref="Upload"/>.</summary>
    internal static void ContextLost()
    {
        _ssbo = 0; _sized = 0;
        _dirtyLo = int.MaxValue; _dirtyHi = -1;
    }

    internal static string Note()
        => $"pltrows: {_colorWrites} colour writes, {_writes} record writes, {_uploads} uploads"
         + (_clamped == 0 ? "" : $", {_clamped} rows CLAMPED to the master palette")
         + (_badObj == 0 ? "" : $", {_badObj} records staged against a DEAD object");

    /// <summary>Off-device: the colour path is a record write (not a composite), the packing round-trips, and
    /// the flag word is what tells a PLT record from every other one.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        void Expect(bool cond, string what)
        {
            if (cond) return;
            ok = false;
            log.Always($"PltColors.SelfCheck: {what}");
        }
        if (_words.Length == 0) _words = new uint[Records * WordsPerRecord];
        // The check writes the real columns, so its tallies go back afterwards: a period line is a report of
        // what the session did, not of what its self-check did.
        (long svColour, long svWrites, long svClamped) = (_colorWrites, _writes, _clamped);
        Expect(WordsPerRecord == RowWords + 1 && RowWords * 2 >= Layers,
            $"{RowWords} row words cannot hold {Layers} rows");

        DbId obj = SceneDb.Objects.Alloc();
        Expect(!obj.IsNull, "the object table would not allocate");
        if (obj.IsNull) return ok;
        SceneDb.Objects.TakeDirty();

        // CreateCloak's own shape: slot*256 + colour, the integers the engine's scheme array carries.
        var rows = new int[Layers];
        for (int l = 0; l < Layers; l++) rows[l] = l * 100 + 7;
        SetRows(obj, rows);
        Expect(SceneDb.Objects.DirtyCount == 1 && (SceneDb.Objects.DirtyAt(obj.Index) & Dirty.Material) != 0,
            "setting the rows did not mark the object Dirty.Material");
        for (int l = 0; l < Layers; l++) Expect(Row(obj, l) == rows[l], $"row {l} did not round-trip");

        SceneDb.Objects.TakeDirty();
        long before = _colorWrites;
        SetRows(obj, rows);
        Expect(_colorWrites == before && SceneDb.Objects.DirtyCount == 0,
            "re-setting the same rows counted a write -- an unchanged colour must cost nothing");
        SetRow(obj, 3, 1234);
        Expect(Row(obj, 3) == 1234 && _colorWrites == before + 1, "a single-layer colour change did not take");
        Expect(SceneDb.Objects.DirtyCount == 1, "a colour change did not mark the object");
        SceneDb.Objects.TakeDirty();

        long clampedBefore = _clamped;
        SetRow(obj, 4, PltPalettes.Height + 10);
        Expect(Row(obj, 4) == PltPalettes.Height - 1 && _clamped == clampedBefore + 1,
            "a row past the master palette was not clamped and counted");

        const int Slot = 3;
        Clear(Slot);
        Expect(WordAt(Slot, RowWords) == 0, "a cleared record still claims to be PLT");
        Stage(Slot, obj);
        Expect(WordAt(Slot, RowWords) == FlagPlt, "a staged PLT record does not carry the flag");
        for (int l = 0; l < Layers; l++)
        {
            uint w = WordAt(Slot, l >> 1);
            uint got = (l & 1) == 0 ? w & 0xffff : w >> 16;
            Expect(got == Row(obj, l), $"packed row {l} is {got}, not {Row(obj, l)} -- cx_plt.glsl reads it this way");
        }
        Clear(Slot);
        SceneDb.Objects.Free(obj);
        SceneDb.Objects.TakeDirty();
        (_colorWrites, _writes, _clamped) = (svColour, svWrites, svClamped);
        return ok;
    }
}
