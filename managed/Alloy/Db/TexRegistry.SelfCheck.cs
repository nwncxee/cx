using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The off-device checks: the PLT decode on synthetic files, the palette row/column arithmetic
/// against a hand-computed composite, the registry's dedup / refcount / arrival-dirty behaviour, and the
/// missing-texture state. No engine, no GL -- run-demo runs all of it.</summary>
internal static unsafe partial class TexRegistry
{
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = PltCheck(log) & PaletteCheck(log) & RegistryCheck(log) & PltColors.SelfCheck(log)
                & TexCoverageOf.SelfCheck(log) & TxiCheck(log) & TxiSelfCheck(new Checker(log, "TexRegistry.TxiSelfCheck"));
        log.Info(ok ? "TexRegistry.SelfCheck: passed" : "TexRegistry.SelfCheck: FAILED");
        return ok;
    }

    /// <summary>The water keyword, on the shipped shape of the data: a TGA with its <c>.txi</c> appended
    /// after the image, the engine's own case-insensitive compare, and the two ways a scan can be wrong --
    /// a texture with no <c>.txi</c> at all and one whose block names a different map.</summary>
    private static bool TxiCheck(ExtenderLog log)
    {
        bool ok = true;
        void Expect(bool cond, string what)
        {
            if (cond) return;
            ok = false;
            log.Always($"TexRegistry.SelfCheck: {what}");
        }
        static byte[] Tga(string txi)
        {
            var head = new byte[64];
            byte[] tail = System.Text.Encoding.ASCII.GetBytes("TRUEVISION-XFILE.\0" + txi);
            var b = new byte[head.Length + tail.Length];
            head.CopyTo(b, 0); tail.CopyTo(b, head.Length);
            return b;
        }
        // The shipped block, verbatim from data/os_textures.bif's rural water texture.
        Expect(TxiNamesBumpyShiny(Tga("bumpyshinytexture ttr01__env\nbumpmaptexture shinywater\nproceduretype arturo\n")),
               "the shipped water .txi does not read as bumpy-shiny");
        Expect(TxiNamesBumpyShiny(Tga("BumpyShinyTexture TM_Refl_Glass4\n")),
               "the keyword is compared case-sensitively (the engine uses strcasecmp)");
        Expect(!TxiNamesBumpyShiny(Tga("envmaptexture tm_coinbase01\nalphamean 0.75\n")),
               "a .txi naming only an env map reads as bumpy-shiny");
        Expect(!TxiNamesBumpyShiny(new byte[128]), "a texture with no .txi at all reads as bumpy-shiny");
        log.Info(ok ? "TexRegistry.SelfCheck: the water .txi keyword reads on the shipped shape"
                    : "TexRegistry.SelfCheck: the water .txi keyword FAILED");
        return ok;
    }

    /// <summary>A synthetic PLT: header, flat pairs, the RLE form of the same pairs, the mip reduction, and
    /// every refusal (short file, wrong magic, impossible size, truncated pairs).</summary>
    private static bool PltCheck(ExtenderLog log)
    {
        bool ok = true;
        static byte[] Plt(int w, int h, int layers, bool rle, params byte[] body)
        {
            var b = new byte[PltImage.HeaderBytes + body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(b, PltImage.Magic);
            b[4] = (byte)'V'; b[5] = (byte)'1'; b[6] = b[7] = (byte)' ';
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PltImage.OffLayers), (uint)layers);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PltImage.OffCompressed), rle ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PltImage.OffWidth), (uint)w);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(PltImage.OffHeight), (uint)h);
            body.CopyTo(b, PltImage.HeaderBytes);
            return b;
        }
        void Expect(bool cond, string what)
        {
            if (cond) return;
            ok = false;
            log.Always($"TexRegistry.SelfCheck: {what}");
        }

        // 2x2, four pairs: (10,0) (20,1) (30,0) (40,0).
        byte[] flat = Plt(2, 2, 10, false, 10, 0, 20, 1, 30, 0, 40, 0);
        Expect(PltImage.Parse(flat, out int w, out int h, out int layers, out bool rle, out _)
            && w == 2 && h == 2 && layers == 10 && !rle, "the 2x2 header does not parse");
        byte* got = PltImage.Decode(flat, out w, out h, out int levels, out string? why);
        Expect(got != null, $"a flat 2x2 PLT was refused: {why}");
        if (got != null)
        {
            Expect(levels == 2, $"a 2x2 chain has {levels} levels, not 2");
            Expect(got[0] == 10 && got[1] == 0 && got[2] == 20 && got[3] == 1
                && got[4] == 30 && got[5] == 0 && got[6] == 40 && got[7] == 0,
                "the pairs did not survive the decode in (intensity, layer) order");
            // level 1: three children on layer 0 (10, 30, 40) win the majority; mean 26.67 -> 27 with the
            // engine-free rounding this reduction declares (sum + cnt/2) / cnt = (80 + 1) / 3 = 27.
            Expect(got[8] == 27 && got[9] == 0, $"the 2x2 reduction gave ({got[8]}, {got[9]}), not (27, 0)");
            NativeMemory.Free(got);
        }

        // The same pairs, RLE: a control byte's low seven bits are COUNT-1, its top bit means "repeat the
        // next byte". Four literals, a one-run, two literals, a one-run.
        byte[] packed = Plt(2, 2, 10, true, 0x03, 10, 0, 20, 1, 0x80, 30, 0x01, 0, 40, 0x80, 0);
        var dst = new byte[8];
        Expect(PltImage.Unrle(packed.AsSpan(PltImage.HeaderBytes), dst, out string? rw), $"the RLE stream was refused: {rw}");
        Expect(dst[0] == 10 && dst[1] == 0 && dst[2] == 20 && dst[3] == 1 && dst[4] == 30
            && dst[5] == 0 && dst[6] == 40 && dst[7] == 0, "the RLE stream decoded to the wrong pairs");
        // a repeat longer than one, and a run the output bound cuts short
        var five = new byte[5];
        Expect(PltImage.Unrle(new byte[] { 0x82, 9, 0x01, 1, 2 }, five, out _)
            && five[0] == 9 && five[1] == 9 && five[2] == 9 && five[3] == 1 && five[4] == 2,
            "a three-run followed by two literals did not decode -- the engine's runs straddle a pixel");
        var two = new byte[2];
        Expect(PltImage.Unrle(new byte[] { 0x8f, 7 }, two, out _) && two[0] == 7 && two[1] == 7,
            "a run past the output bound was not clipped");
        Expect(!PltImage.Unrle(new byte[] { 0x7f }, new byte[8], out _), "a truncated RLE literal run was accepted");
        Expect(!PltImage.Unrle(new byte[] { 0x80 }, new byte[8], out _), "an RLE stream ending on a run byte was accepted");
        Expect(!PltImage.Unrle(Array.Empty<byte>(), new byte[2], out _), "an empty RLE stream was accepted");

        Expect(!PltImage.Parse(new byte[8], out _, out _, out _, out _, out _), "an 8-byte file was accepted");
        byte[] bad = (byte[])flat.Clone(); bad[0] = (byte)'X';
        Expect(!PltImage.Parse(bad, out _, out _, out _, out _, out _), "a wrong magic was accepted");
        Expect(!PltImage.Parse(Plt(0, 2, 10, false, 0, 0), out _, out _, out _, out _, out _), "a zero width was accepted");
        Expect(!PltImage.Parse(Plt(PltImage.MaxSide + 1, 2, 10, false, 0, 0), out _, out _, out _, out _, out _),
            $"a width past {PltImage.MaxSide} was accepted");
        Expect(!PltImage.Parse(Plt(2, 2, PltImage.MaxFileLayers + 1, false, 0, 0), out _, out _, out _, out _, out _),
            $"a layer count past {PltImage.MaxFileLayers} was accepted");
        Expect(!PltImage.Parse(Plt(4, 4, 10, false, 1, 2, 3, 4), out _, out _, out _, out _, out _),
            "a 4x4 header with four bytes of pairs was accepted");
        Expect(PltImage.ChainBytes(4, 4, 3) == (16 + 4 + 1) * 2, "ChainBytes does not sum the chain");
        return ok;
    }

    /// <summary>The palette arithmetic, against a composite computed by hand. A slot owns 256 rows of 256 RGBA
    /// entries (<c>AurSetPalette</c>'s <c>slot &lt;&lt; 18</c>), a scheme integer is <c>slot*256 + colour</c>,
    /// and the composite of (intensity, layer) is <c>master[rows[layer]][intensity]</c> -- which is
    /// <c>ImageDecode</c>'s <c>palette[(rows[layer] &lt;&lt; 10) + intensity*4]</c>.</summary>
    private static bool PaletteCheck(ExtenderLog log)
    {
        bool ok = true;
        void Expect(bool cond, string what)
        {
            if (cond) return;
            ok = false;
            log.Always($"TexRegistry.SelfCheck: {what}");
        }
        Expect(PltPalettes.Height == 1792, $"the master palette is {PltPalettes.Height} rows, not the 1792 "
            + "Material::ReplaceTexturePLT divides a scheme integer by");
        Expect(PltPalettes.RowBytes == 1024, $"a palette row is {PltPalettes.RowBytes} bytes, not the 1024 "
            + "ImageDecode's `row << 10` assumes");

        // A 256x3 palette into slot 2 -- 256 wide because ImageCopy is a flat memcpy of w*h*bpp, so only a
        // full-width palette lands on the master's row boundaries (which every shipped one is). Stored
        // bottom-up, so after AurSetPalette's ImageFlipY master row 0 of the slot is the LAST stored row.
        const int Pw = PltPalettes.Width, Ph = 3;
        var src = new byte[Pw * Ph * 4];
        for (int y = 0; y < Ph; y++)
            for (int x = 0; x < Pw; x++)
                BinaryPrimitives.WriteUInt32LittleEndian(src.AsSpan((y * Pw + x) * 4), 0x01000000u | (uint)(y * 16 + x));
        var master = new byte[PltPalettes.Height * PltPalettes.RowBytes];
        PltPalettes.Compose(master, 2, src, Pw, Ph);
        int slotBase = 2 * PltPalettes.RowsPerSlot;
        Expect(BinaryPrimitives.ReadUInt32LittleEndian(master.AsSpan((slotBase + 0) * PltPalettes.RowBytes)) == 0x01000020u,
            "master row 0 of slot 2 is not the palette's LAST stored row (the ImageFlipY is missing or doubled)");
        Expect(BinaryPrimitives.ReadUInt32LittleEndian(master.AsSpan((slotBase + 2) * PltPalettes.RowBytes + 4)) == 0x01000001u,
            "master row 2, entry 1 of slot 2 is not the palette's first stored row, second column");
        // ...and a shorter re-SetPalette must not leave the taller one's rows behind.
        PltPalettes.Compose(master, 2, src, Pw, 1);
        Expect(BinaryPrimitives.ReadUInt32LittleEndian(master.AsSpan((slotBase + 1) * PltPalettes.RowBytes)) == 0,
            "a re-SetPalette with fewer rows left the previous palette's rows in the slot");
        PltPalettes.Compose(master, 2, src, Pw, Ph);
        Expect(BinaryPrimitives.ReadUInt32LittleEndian(master.AsSpan((slotBase + 1) * PltPalettes.RowBytes)) != 0,
            "the middle row did not land");
        Expect(BinaryPrimitives.ReadUInt32LittleEndian(master.AsSpan((slotBase + 3) * PltPalettes.RowBytes)) == 0,
            "a row past the palette's height is not zero (vanilla leaves it uninitialised; Alloy must not)");

        // The resolve: layer 1 names slot 2's row 2, intensity 1 -> the hand-computed entry above.
        var rows = new ushort[SceneDb.PltLayers];
        rows[1] = (ushort)(slotBase + 2);
        Expect(PltImage.Resolve(master, rows, 1, 1) == 0x01000001u,
            "the CPU resolve does not reproduce master[rows[layer]][intensity]");
        Expect(PltImage.Resolve(master, rows, 1, 9) == 0, "a layer with no row resolved non-zero");
        // RasterReader.DecodePlt is the engine's own arithmetic; the two must agree bit for bit.
        var pairs = new byte[] { 1, 1 };
        var outp = new uint[1];
        var intRows = new int[SceneDb.PltLayers];
        for (int l = 0; l < intRows.Length; l++) intRows[l] = rows[l];
        RasterReader.DecodePlt(pairs, master, intRows, outp);
        Expect(outp[0] == PltImage.Resolve(master, rows, 1, 1),
            "PltImage.Resolve and RasterReader.DecodePlt (ImageDecode's own arithmetic) disagree");
        return ok;
    }

    /// <summary>The registry: dedup on identity, the anagram families the engine's own hash cannot tell
    /// apart, refcounting, arrival marking the dependent part records, and the missing-texture state.</summary>
    private static bool RegistryCheck(ExtenderLog log)
    {
        bool ok = true;
        void Expect(bool cond, string what)
        {
            if (cond) return;
            ok = false;
            log.Always($"TexRegistry.SelfCheck: {what}");
        }
        // The check drives the real table, so its tallies must not reach the period line: a deliberate
        // Fail() reported as "2 FAILED (white stand-in)" in a live session is a counter that lies.
        (long sv_acq, long sv_hits, long sv_up, long sv_fail, long sv_evict, long sv_marks, long sv_full) =
            (_acquires, _hits, _uploads, _failed, _evicted, _marks, _full);
        long sv_cov = _covUnknown;
        long sv_res = _residentBytes, sv_idle = _idleBytes, sv_peak = _peakBytes;
        var sv_loud = new List<string>(_loudOnce);
        // ...and the whole allocator, not just the watermark. Restoring _high while leaving the free list
        // holding indices >= _high handed the first real Acquire a slot past the dense bound, where
        // EvictPastBudget and ContextLost never walk: a stale GL name and a stale RESIDENT bindless handle
        // outliving a context loss (review r1 finding 4).
        int sv_high = _high, sv_freeHead = _freeHead, sv_live = _live;
        int sv_idleHead = _idleHead, sv_idleTail = _idleTail, sv_mints = (int)_mints;
        var sv_hash = (int[])_hash.Clone();
        var sv_freeNext = (int[])_freeNext.Clone();
        var sv_grave = new List<uint>(_graveyard);
        var sv_dying = new List<ulong>(_dyingHandles);
        long sv_dropped = _handlesDropped;

        Expect(KeyOf("tex_core_0145_d", 0) != KeyOf("tex_core_0154_d", 0),
            "an anagram pair shares a key -- the engine's AurTextureCalculateHashID collision is back");
        Expect(KeyOf("TEX_CORE_0145_D", 0) == KeyOf("tex_core_0145_d", 0),
            "the key is case-sensitive, which AurTextureFind's strcasecmp is not");
        Expect(KeyOf("a", 1) != KeyOf("a", 0), "the atlas cell is not in the key");

        // The resref fold, which a signed `c - 'A' < 26` gets wrong for EVERY DIGIT: it made the live run's
        // resource lookups miss on "pmh0_head005" -> "pmhP_headPPU", and every body-part PLT is numbered.
        Span<byte> rr = stackalloc byte[ShaderResource.ResRefBytes + 1];
        ResRef("PMH0_Head005", rr);
        Expect(System.Text.Encoding.ASCII.GetString(rr[..12]) == "pmh0_head005",
            $"the resref fold gave \"{System.Text.Encoding.ASCII.GetString(rr[..12])}\", not \"pmh0_head005\"");
        Expect(rr[12] == 0 && rr[ShaderResource.ResRefBytes] == 0, "the resref is not null-padded");
        ResRef("a-very-long-resref-name-past-the-limit", rr);
        Expect(rr[ShaderResource.ResRefBytes] == 0, "an over-long resref overran the CResRef buffer");

        // Drive the table with no engine and no GL: Acquire would reach the resource manager, so the slots
        // are made by hand through the same paths the loader uses.
        int save = _high;
        TexId a = Insert("cx_selfcheck_a", TexKind.Plain);
        TexId b = Insert("cx_selfcheck_a", TexKind.Plain);
        Expect(a.Index == b.Index && _refs[a.Index] == 2, "two acquires of one resref are not one slot with two references");
        TexId c = Insert("cx_selfcheck_b", TexKind.Plt);
        Expect(c.Index != a.Index, "two different resrefs share a slot");
        // One resref is one slot whatever kind is ASKED: the kind is a property of the resource, resolved at
        // the locate step, and keying on the asking made Acquire(n) and Acquire(n, Plt) two uploads of one
        // file (review r1 finding 13).
        TexId aPlt = Insert("cx_selfcheck_a", TexKind.Plt);
        Expect(aPlt.Index == a.Index, "asking for the same resref as a PLT made a second slot");
        Release(aPlt);

        // Real part records: a dependent that is not a live part is refused and counted, so the check cannot
        // use made-up indices.
        int p1 = SceneDb.Parts.Alloc().Index, p2 = SceneDb.Parts.Alloc().Index;
        int p3 = SceneDb.Parts.Alloc().Index, p4 = SceneDb.Parts.Alloc().Index;
        SceneDb.Parts.TakeDirty();
        AddDependent(a, p1);
        AddDependent(a, p2);
        AddDependent(c, p3);
        long refusedBefore = _depRefused;
        // Pre-claim the cause so the DELIBERATE refusal below does not print a line that reads, in a live
        // cx.log, as a caller handing out bad part indices. The cause set is restored at the end either way.
        LoudOnce("deppart");
        AddDependent(a, SceneDb.MaxParts + 7);
        Expect(_depRefused == refusedBefore + 1, "a part index past the table was accepted as a dependent");
        Land(a, 0xBEEF, 4096, 0);
        Expect(SceneDb.Parts.DirtyCount == 2, $"arrival marked {SceneDb.Parts.DirtyCount} part records, not 2");
        Expect((SceneDb.Parts.DirtyAt(p1) & Dirty.Texture) != 0 && (SceneDb.Parts.DirtyAt(p2) & Dirty.Texture) != 0,
            "arrival did not mark both dependent parts Dirty.Texture");
        Expect((SceneDb.Parts.DirtyAt(p3) & Dirty.Texture) == 0, "arrival marked another texture's dependent");
        Expect(TryResident(a, out uint name, out _) && name == 0xBEEF, "a landed texture is not resident");
        Expect(Coverage(c) == TexCoverage.Unknown, "a slot still loading claims a coverage verdict");
        Land(b, 0xFEED, 1024, 0, TexCoverage.Cutout);   // b shares a's slot, which already landed: refused
        Expect(Coverage(a) == TexCoverage.Unknown,
            "a second Land overwrote the slot's coverage -- the first upload is the one that counts");
        AddDependent(a, p4);
        Expect(SceneDb.Parts.DirtyCount == 2, "a dependent was recorded on a RESIDENT slot (the list must be closed)");

        SceneDb.Parts.TakeDirty();
        LoudOnce($"tex:{ResrefOf(c)}");   // ...and the deliberate failure below
        Fail(c.Index, "the self-check says so");
        Expect(StateOf(c) == TexState.Failed, "a failed load is not in the Failed state");
        Expect(!TryResident(c, out _, out _), "a failed load answers resident");
        Expect(SceneDb.Parts.DirtyCount == 1 && (SceneDb.Parts.DirtyAt(p3) & Dirty.Texture) != 0,
            "a failure did not tell its dependent part -- the white stand-in would never be replaced");
        SceneDb.Parts.TakeDirty();
        SceneDb.Parts.Free(SceneDb.Parts.IdAt(p1)); SceneDb.Parts.Free(SceneDb.Parts.IdAt(p2));
        SceneDb.Parts.Free(SceneDb.Parts.IdAt(p3)); SceneDb.Parts.Free(SceneDb.Parts.IdAt(p4));
        SceneDb.Parts.TakeDirty();

        Release(a);
        Expect(StateOf(a) == TexState.Resident, "the first release of two dropped the slot out of Resident");
        Release(a);
        Expect(StateOf(a) == TexState.Idle, "the last release did not leave the slot Idle (ADR 0023)");
        TexId again = Insert("cx_selfcheck_a", TexKind.Plain);
        Expect(again.Index == a.Index && StateOf(again) == TexState.Resident,
            "an idle slot was not handed back warm -- ADR 0023's second sight costs nothing");
        Release(again);
        Release(b);

        // The Release-during-a-load race (review r1 finding 5): a slot released while its load is in flight
        // goes Idle with no name, and re-acquiring it has to re-OFFER it. Before the fix it was set back to
        // Loading and never offered again -- white for ever, with nothing logged.
        TexId d = Insert("cx_selfcheck_c", TexKind.Auto);
        Expect(_pend[d.Index] == PendNone, "the hand-built slot should not claim to be in flight");
        _pend[d.Index] = PendFlight;                       // pretend the worker has it
        Release(d);
        Expect(StateOf(d) == TexState.Idle && _name[d.Index] == 0,
            "a release during a load did not leave the slot idle-with-no-name");
        TexId d2 = Alloc("cx_selfcheck_c", TexKind.Auto, 0, out bool offer2);
        Expect(d2.Index == d.Index, "re-acquiring the released slot did not find it");
        Expect(!offer2, "a slot still IN FLIGHT was offered a second time");
        _pend[d.Index] = PendNone;                          // the job came back and settled nothing
        TexId d3 = Alloc("cx_selfcheck_c", TexKind.Auto, 0, out bool offer3);
        Expect(d3.Index == d.Index && offer3 && StateOf(d3) == TexState.Loading,
            "a nameless, idle, not-in-flight slot was not re-offered -- that is the forever-white bug");
        Release(d3); Release(d2);

        // the module edge: a held slot leaves the index, so the next module's acquire of the same name reads afresh,
        // and the held one retires at its last release instead of idling with the previous module's pixels
        TexId m1 = Insert("cx_selfcheck_m", TexKind.Auto);
        Detach(m1.Index);
        TexId m2 = Alloc("cx_selfcheck_m", TexKind.Auto, 0, out bool offerM);
        Expect(m2.Index != m1.Index && offerM, "a slot detached at the module edge still answered an acquire of its name");
        Release(m1);
        Expect(!Valid(m1), "a detached slot idled at its last release instead of retiring");
        Release(m2);

        // The dying-handle funnel. A DB record copies a slot's bindless handle at staging and holds it across
        // frames, so a drop the records never hear about is a non-resident handle under a live draw -- garbage
        // on NVIDIA, a GPU page fault on radeonsi. Every drop goes through DropHandle, so the queue is checked
        // on the two edges that reach it: a last release to idle, and a retire.
        const ulong fakeIdle = 0xC0FFEE01u, fakeRetire = 0xC0FFEE02u;   // not-an-offset: stand-in handle values, never dereferenced
        int dyingBefore = _dyingHandles.Count;
        TexId h1 = Insert("cx_selfcheck_h1", TexKind.Plain);
        _handle[h1.Index] = fakeIdle;
        Release(h1);
        Expect(_dyingHandles.Count == dyingBefore + 1 && _handle[h1.Index] == 0,
            "a last release did not queue its bindless handle -- every record still holding it samples a dead one");
        TexId h2 = Insert("cx_selfcheck_h2", TexKind.Plain);
        _handle[h2.Index] = fakeRetire;
        Retire(h2.Index);
        Expect(_dyingHandles.Count == dyingBefore + 2, "a retire did not queue its bindless handle");
        Expect(_dyingHandles.Contains(fakeIdle) && _dyingHandles.Contains(fakeRetire),
            "the queue holds something other than the two handles that died");
        long droppedBefore = _handlesDropped;
        ZeroDyingHandles();
        Expect(_dyingHandles.Count == 0 && _handlesDropped == droppedBefore + dyingBefore + 2,
            $"the bracket's drain left {_dyingHandles.Count} queued and counted "
          + $"{_handlesDropped - droppedBefore} drops -- the records keep the dead handles until it empties");

        // CX-188: every edge that can change what a record samples moves TableSerial. The renderer stamps it
        // per slot and skips re-resolving a part's textures while it holds still, so a missing bump IS a
        // record left sampling a dead handle -- which is why this asserts the bump on each edge rather than
        // trusting the writers to remember.
        int serial0 = TableSerial;
        TexId t1 = Insert("cx_selfcheck_t", TexKind.Plain);
        Expect(TableSerial != serial0, "an acquire did not move TableSerial");
        // CX-188: and the memo's FINALITY predicate, on the three states that reach it. A loading slot is
        // not final -- its arrival is what the memo waits for -- and getting that backwards freezes the white
        // stand-in on every part waiting for a texture; a null id and a failed load ARE final, and getting
        // THAT backwards re-resolves every texture-less part every frame for ever.
        Expect(Final(default), "a null texture id is not final, so a part with no diffuse re-resolves for ever");
        Expect(!Final(t1), "a LOADING slot is final, so its arrival would never repair the record");
        serial0 = TableSerial;
        Land(t1, 4242u, 16);
        Expect(TableSerial != serial0, "an arrival did not move TableSerial");
        Expect(!Final(t1), "a RESIDENT slot is final -- only a null id and a failed load are");
        serial0 = TableSerial;
        _handle[t1.Index] = 0xC0FFEE03u;   // not-an-offset: a stand-in handle value, never dereferenced
        DropHandle(t1.Index);
        Expect(TableSerial != serial0, "a dropped handle did not move TableSerial");
        serial0 = TableSerial;
        Fail(t1.Index, "the self-check's own failure");
        Expect(TableSerial != serial0, "a failed load did not move TableSerial");
        Expect(Final(t1), "a FAILED slot is not final, so every part on it re-resolves for ever");
        serial0 = TableSerial;
        Retire(t1.Index);
        Expect(TableSerial != serial0, "a retire did not move TableSerial");

        // Progressive streaming: a preview draws while the slot keeps LOADING, so the part waiting on it is told
        // twice -- once for the preview, once for the full chain that replaces it.
        long svPreviews = _previews;
        TexId pv = Insert("cx_selfcheck_pv", TexKind.Plain);
        int pp = SceneDb.Parts.Alloc().Index;
        SceneDb.Parts.TakeDirty();
        AddDependent(pv, pp);
        int graveBefore = _graveyard.Count;
        LandPreview(pv, 0xA1u, 0, TexCoverage.Cutout);
        Expect(TryDrawable(pv, out uint pn, out _) && pn == 0xA1u && StateOf(pv) == TexState.Loading,
            "a preview did not draw while its slot kept loading");
        Expect(!TryResident(pv, out _, out _) && !Resident(pv),
            "a preview answered RESIDENT -- a consumer that keeps the name (a font atlas) would hold one that dies");
        Expect((SceneDb.Parts.DirtyAt(pp) & Dirty.Texture) != 0, "a preview did not tell the part waiting on it");
        Expect(Coverage(pv) == TexCoverage.Cutout, "the preview's coverage verdict was not taken -- a leaf draws as a quad");
        Expect(!Final(pv), "a previewed slot reads final, so the full chain would never reach its records");
        SceneDb.Parts.TakeDirty();
        AddDependent(pv, pp);
        Land(pv, 0xA2u, 64, 0);
        Expect(TryResident(pv, out pn, out _) && pn == 0xA2u && StateOf(pv) == TexState.Resident && !_preview[pv.Index],
            "the full chain did not replace the preview");
        Expect(_graveyard.Count == graveBefore + 1, "the preview's GL name was not retired");
        Expect((SceneDb.Parts.DirtyAt(pp) & Dirty.Texture) != 0, "the full chain did not reach a part drawing the preview");
        SceneDb.Parts.TakeDirty();
        SceneDb.Parts.Free(SceneDb.Parts.IdAt(pp));
        SceneDb.Parts.TakeDirty();
        Retire(pv.Index);
        _previews = svPreviews;
        // ...and the tail starts at the first level within PreviewSide, past exactly the bytes above it.
        Expect(TailLevel(256, 128, 4, out int tl, out int tw, out int th, out long tskip) && tl == 2 && tw == 64 && th == 32
               && tskip == DdsReader.LevelSize(256, 128, 4) + DdsReader.LevelSize(128, 64, 4),
            $"a 256x128 chain's preview starts at level {tl} ({tw}x{th}, {tskip} bytes in)");
        Expect(!TailLevel(64, 64, 4, out _, out _, out _, out _), "a texture already within PreviewSide asked for a preview");
        // A single-mip DDS (an 8x4 DXT1, the DDS spec's header laid out by hand) uploads one level, not the padded chain.
        byte[] one = new byte[128 + 16];
        "DDS "u8.CopyTo(one);
        BinaryPrimitives.WriteUInt32LittleEndian(one.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(one.AsSpan(16), 8);
        "DXT1"u8.CopyTo(one.AsSpan(84));
        var dj = new Job { Kind = TexKind.Plain };
        Decode(dj, one);
        Expect(dj.Compressed && dj.Levels == 1, $"a one-level DDS was given {dj.Levels} level(s) -- the padding is sampled");
        DdsReader.FreePrepared(dj.Data);

        // a handle to an evicted slot resolves to nothing: the whole reason no holder needs a death notice
        Retire(a.Index);
        Expect(!Valid(a) && !Valid(b) && !TryResident(a, out _, out _),
            "a handle to an evicted slot still resolves -- the generation did not move on");
        for (int i = save; i < _high; i++) if (_state[i] != TexState.Free) Retire(i);

        // No byte-tally assertion here, either side of this restore: after it the saved values are simply
        // back, and before it the suite's own Alloc/Retire traffic legitimately drives them negative
        // (Retire subtracts bytes the direct allocator never added). It needs the GL upload path.
        _high = sv_high; _freeHead = sv_freeHead; _live = sv_live;
        _idleHead = sv_idleHead; _idleTail = sv_idleTail; _mints = sv_mints;
        sv_hash.CopyTo(_hash, 0);
        sv_freeNext.CopyTo(_freeNext, 0);
        _graveyard.Clear();
        foreach (uint n in sv_grave) _graveyard.Enqueue(n);
        // The check's own retires must not leave a dying handle for the next bracket to hunt through the
        // record table for. Headless they are all 0 and never queued; restore anyway, for the GL session.
        _dyingHandles.Clear();
        foreach (ulong v in sv_dying) _dyingHandles.Enqueue(v);
        _handlesDropped = sv_dropped;
        for (int i = sv_high; i < MaxSlots && _resref[i] != null; i++) { _resref[i] = null; _pend[i] = PendNone; }
        (_acquires, _hits, _uploads, _failed, _evicted, _marks, _full) =
            (sv_acq, sv_hits, sv_up, sv_fail, sv_evict, sv_marks, sv_full);
        _covUnknown = sv_cov;
        _residentBytes = sv_res; _idleBytes = sv_idle; _peakBytes = sv_peak;
        _loudOnce.Clear();
        foreach (string cause in sv_loud) _loudOnce.Add(cause);
        return ok;
    }

    /// <summary>A slot made without the resource manager: the REAL allocator, with the offer dropped. A hand
    /// copy of <see cref="Acquire"/> is a check of a second implementation (review r1 finding 19).</summary>
    private static TexId Insert(string resref, TexKind kind) => Alloc(resref, kind, 0, out _);
}
