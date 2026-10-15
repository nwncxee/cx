using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Geometry RESIDENCY: Alloy mints the GL buffer behind every engine vertex/index pool and uploads
/// it from the pool's host shadow, the engine's own upload suppressed -- one buffer, one upload, no copy, so
/// there is no ordering for a copy to get wrong. Keyed on the engine's POOL TOKEN, the GL name an index onto
/// the table (all a harvested VAO carries). No budget and no LRU: the engine's <c>DeletePool</c> frees.</summary>
internal static unsafe class OwnedBuffers
{
    /// <summary>One pool Alloy has uploaded. <see cref="Name"/> is the GL buffer minted for it (or adopted,
    /// when the engine got there before Alloy's entry points existed).</summary>
    private struct Entry
    {
        internal uint Name;
        internal long Bytes;
        /// <summary>The engine wrote into this pool since the last <see cref="ClearDirty"/> -- the re-bake
        /// predicate's vertex-motion signal.</summary>
        internal bool Dirty;
        /// <summary>Seen in an engine draw outside the Alloy window, i.e. the UI / a GUI 3D panel draws from
        /// it. The ticket's tagging, recorded rather than acted on.</summary>
        internal bool EngineDrawn;
        /// <summary>An Alloy VAO has been built over it.</summary>
        internal bool AlloyDrawn;
    }

    /// <summary>Capacity for the two tables and the dirty list. Pools are engine-wide (world VDOs, UI
    /// primitives, the global element stream) and <c>RequestPool</c> @0x4d1ec0 doubles its descriptor table on
    /// demand, so nothing bounds them. ponytail: a number, not a derivation -- past it a rehash allocates on
    /// the render thread once and the tables keep working; size from <c>_table.Count</c> if one is ever seen.</summary>
    private const int PoolCeiling = 1024;
    private static readonly Dictionary<uint, Entry> _byToken = new(PoolCeiling);
    /// <summary>GL name -> pool token. The index the harvested-VAO callers ask through.</summary>
    private static readonly Dictionary<int, uint> _byName = new(PoolCeiling);
    /// <summary>Tokens written to since the last <see cref="ClearDirty"/>.</summary>
    private static readonly List<uint> _dirty = new(PoolCeiling);

    private static ExtenderLog? _log;
    private static bool _armed;
    /// <summary>Is the geometry takeover live? False leaves every upload to the engine, which is vanilla
    /// residency -- the stand-down, not a second code path.</summary>
    internal static bool Armed => _armed;
    /// <summary>Buffers Alloy DRAWS from and did not upload -- asked at every VAO build, the geometry twin of
    /// <c>OwnedTextures.Borrowed</c> and what the exit bar reads. Distinct from <see cref="_refused"/>, which
    /// counts pools handed back whether or not Alloy ever draws them: the menu's pools are resident before
    /// Alloy's GL entry points exist and are never world geometry.</summary>
    internal static int Borrowed => _borrowed;
    private static long _bytes, _peakBytes;
    /// <summary>The high-water figure the <c>/knobs</c> residency line reports, in MiB.</summary>
    internal static long PeakMib => _peakBytes / OneMiB;
    private static int _minted, _adopted, _refused, _borrowed, _retired, _peakCount;
    private static long _uploads, _subUploads, _grows, _repacks, _wholePool;

    /// <summary>Wired once from <see cref="AlloyRenderer"/>'s init, before any frame runs.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _armed = Renderer.OwnedGeom.Boot;
        log.Info(_armed
            ? "OwnedBuffers: armed -- Alloy mints and uploads every engine vertex/index pool from its host "
              + "shadow, and the engine's own upload is suppressed (alloy.ownedgeom=0 to leave uploads to the engine)"
            : "OwnedBuffers: stood down (alloy.ownedgeom=0) -- the engine uploads its own pools, Alloy draws from them");
    }

    /// <summary>The two engine edges: the upload funnel, which Alloy now PERFORMS rather than observes, and
    /// the delete funnel, where a pool's GL name stops meaning what it meant. Without the second, an area
    /// transition frees the pools, the tokens go back on the free list and the next area's pools inherit both
    /// the tokens and (from <c>glGenBuffers</c>) the names -- stale rows describing the wrong mesh.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        // Installed whether or not the upload takeover is armed: the VAO-cache death notice
        // (AlloyRenderer.OnEngineBufferDeleted) rides this funnel, and Alloy draws from engine pools either way.
        _origDelete = hooks.InstallGlDeleteBuffer(&OnDeleteBuffer);
        if (_origDelete == null) log.Info("OwnedBuffers: GLRender::DeleteBuffer did not resolve -- no buffer death notice for the VAO caches");
        // Whether or not the takeover is armed: the shared-VDO capacity is the engine's own pooling, and a
        // Mesa answer of 3000 fragments it 350x against NVIDIA's (see SymbolMap.GlMaxElementVertices).
        _maxElements = Renderer.MaxElements.Boot;
        if (_maxElements > 0)
        {
            bool a = hooks.InstallGlMaxElementVertices(&OnMaxElements) != null;
            bool b = hooks.InstallGlMaxElementIndices(&OnMaxElements) != null;
            log.Info(a && b
                ? $"OwnedBuffers: shared-VDO capacity pinned to {_maxElements} vertices/indices regardless of the driver's GL_MAX_ELEMENTS answer (alloy.maxelements=0 restores it)"
                : "OwnedBuffers: GetVertexBufferMaxElement* did not resolve -- the driver's GL_MAX_ELEMENTS answer sizes the shared VDOs");
        }
        if (!_armed) return;
        IntPtr anchor = Native.Sym(SymbolMap.GlPoolTableAnchor);
        // The anchor is a SYMBOL, and the {base, count} pair sits a fixed step from it -- the one place the
        // step is taken, because there is no view to reach it through yet.
        _table = GlPoolTable.AtAnchor(anchor);
        _orphan = (byte*)Native.Sym(SymbolMap.GlOrphanBuffers);
        _totalMemPool = (int*)Native.Sym(SymbolMap.GlTotalMemPool);
        _origUnlock = hooks.InstallGlUnlockPool(&OnUnlockPool);
        bool ok = !_table.IsNull && _origUnlock != null && _origDelete != null;
        if (!ok) _armed = false;
        log.Info(ok
            ? "OwnedBuffers: took GLRender::UnlockPool + GLRender::DeleteBuffer -- Alloy performs the pool "
              + "upload, the engine's is skipped, and a pool's row dies with its buffer"
            : "OwnedBuffers: stood down -- the pool table or the upload/delete funnels did not resolve, and a "
              + "takeover that cannot read the pool descriptor would upload nothing");
    }

    private static GlUnlockPoolFn _origUnlock;
    private static GlDeleteBufferFn _origDelete;

    /// <summary>NVIDIA's GL_MAX_ELEMENTS answer, which the engine has always pooled under there. The value is a
    /// glDrawRangeElements hint in GL, never a buffer limit, and the pools grow by doubling past it anyway.</summary>
    internal const int MaxElementsDefault = 1 << 20;
    private static int _maxElements;

    /// <summary>Replaces both <c>GLRender::GetVertexBufferMaxElement*</c> wholesale; the engine's cached copy
    /// is never consulted. Cold: read per VDO allocation.</summary>
    [UnmanagedCallersOnly]
    private static int OnMaxElements() => _maxElements;
    /// <summary>The {base, count} pair, NOT the table it points at: <c>RequestPool</c> @0x4d1ec0 grows the
    /// table with <c>calloc</c>+<c>free</c>, so the base moves and every lookup re-reads it.</summary>
    private static GlPoolTable _table;
    private static byte* _orphan;
    private static int* _totalMemPool;

    /// <summary>The engine is pushing a pool's host shadow into GL; Alloy does it instead, and the original
    /// never runs for a pool it took -- that is the suppression. Hot-adjacent (dynamic pools unlock every
    /// frame): in the steady state nothing here allocates or logs. Runs in the engine's own 3.3 context, which
    /// is safe because buffers are share-listed and the upload binds nothing (see <see cref="Upload"/>).</summary>
    [UnmanagedCallersOnly]
    private static void OnUnlockPool(uint token, uint size, uint offset)
    {
        if (!Take(token, size, offset) && _origUnlock != null) _origUnlock(token, size, offset);
    }

    /// <summary>The handler's body, split out so <see cref="SelfCheck"/> can exercise it -- an
    /// <c>UnmanagedCallersOnly</c> method cannot be called from managed code. False means "not ours", and the
    /// caller then runs the engine's own <c>UnlockPool</c> unchanged.</summary>
    private static bool Take(uint token, uint size, uint offset)
    {
        if (!_armed || size == 0) return false;
        GlPool p = Pool(token);
        if (p.IsNull) return false;
        // The engine's own two refusals, replicated: a pool with no bytes, and a pool whose lock flags say
        // the host block is not in a state to be pushed (GLRender::UnlockPool @0x4cfc80 tests +0x11 & 0x18
        // before it does anything). Handing those back costs nothing -- the original refuses them too.
        int bytes = p.Size;
        if (bytes <= 0 || (p.LockFlags & LockNoUpload) != 0) return false;
        byte* host = (byte*)p.Host;
        if (host == null) return false;
        if (!GlContextService.Available) { _refused++; return false; }

        uint name = p.Name;
        bool fresh = p.Named == 0;
        if (fresh)
        {
            Gl.CreateBuffers(1, &name);
            if (name == 0) { _refused++; return false; }
            p.Name = name;
            p.Named = 1;
            p.Uploaded = 0;
        }
        else if (name == 0) { _refused++; return false; }

        Upload(p, name, host, bytes, size, offset);
        Record(token, name, bytes, fresh);
        return true;
    }

    /// <summary>The engine's upload, byte for byte, aimed at the pool's buffer. Three branches, chosen by the
    /// <c>RequestPool</c> usage word at pool+0x14 exactly as <c>GLRender::UnlockPool</c> @0x4cfc80 chooses
    /// them: sub-data bit = allocate once then sub-data the named range; dynamic alone = allocate once, then
    /// sub-data the whole prefix from 0 (the offset is IGNORED there, as in the engine); neither = once.</summary>
    private static void Upload(GlPool p, uint name, byte* host, int bytes, uint size, uint offset)
    {
        // By NAME, binding nothing: no consumer relies on UnlockPool's residual bind, and an
        // ELEMENT_ARRAY_BUFFER bind here would be VAO state landing in whatever VAO the engine has current.
        uint usage = p.Usage;
        bool dynamic = (usage & UsageDynamic) != 0;
        uint hint = dynamic ? Gl.DYNAMIC_DRAW : Gl.STATIC_DRAW;
        long span = size == WholePool ? bytes : size;
        if ((usage & UsageSubData) != 0)
        {
            if (p.Uploaded == 0)
            { Gl.NamedBufferData(name, (IntPtr)bytes, null, hint); p.Uploaded = 1; }
            // The engine reads its own [offset, offset+size) with no bound check, and would over-read the
            // host block if a caller ever paired a whole-pool size with a non-zero offset. Nothing does --
            // CommitElementStreamData @0x45c9c0 unlocks with `xor %edx,%edx` -- but the clamp costs one
            // compare and the alternative is an out-of-bounds read of the engine's heap.
            if (offset > (uint)bytes) span = 0; else if (offset + span > bytes) span = bytes - offset;
            if (span > 0) new HostShadow(host).UploadBytes(name, offset, span);
            _subUploads++;
        }
        else if (dynamic)
        {
            if (p.Uploaded == 0)
            { Gl.NamedBufferData(name, (IntPtr)bytes, host, hint); p.Uploaded = 1; }
            else
            {
                if (_orphan != null && *_orphan != 0)
                    Gl.NamedBufferData(name, (IntPtr)bytes, null, hint);
                Gl.NamedBufferSubData(name, IntPtr.Zero, (IntPtr)span, host);
                _subUploads++;
            }
        }
        else if (p.Uploaded == 0)
        { Gl.NamedBufferData(name, (IntPtr)bytes, host, hint); p.Uploaded = 1; }
        _uploads++;
        if (size == WholePool) _wholePool++;
    }

    /// <summary>Book the pool: its name (which may have changed under it -- <c>ResizePool</c> keeps the name,
    /// but a pool token recycled through the free list does not), its size, and the dirty mark.</summary>
    private static void Record(uint token, uint name, int bytes, bool fresh)
    {
        if (!_byToken.TryGetValue(token, out Entry e))
        {
            if (fresh) _minted++; else _adopted++;
            _peakCount = Math.Max(_peakCount, _byToken.Count + 1);
        }
        if (e.Name != name)
        {
            if (e.Name != 0) _byName.Remove((int)e.Name);
            e.Name = name;
            _byName[(int)name] = token;
        }
        if (e.Bytes != bytes)
        {
            // A pool whose size moved under a live row was reallocated by ResizePool @0x4cfbc0 (AddItem
            // @0x45ca70 doubles capacity). Repacks are counted separately: one that compacts to the same byte
            // count is invisible here. Session totals, never reset.
            if (e.Bytes != 0) _grows++;
            _bytes += bytes - e.Bytes;
            e.Bytes = bytes;
        }
        if (!e.Dirty) { e.Dirty = true; _dirty.Add(token); }
        _byToken[token] = e;
        if (_bytes > _peakBytes) { _peakBytes = _bytes; _peakCount = _byToken.Count; _peakFresh = true; }
    }

    /// <summary>The pool descriptor for a token, or null when the table cannot answer. The base is re-read
    /// every call on purpose: <c>RequestPool</c> reallocates the whole table when it runs out of slots.
    /// <para>The bound check is the engine's own (<c>UnlockPool</c> tests the token against the descriptor
    /// count before it indexes), which is what makes this safe to run on an arbitrary token.</para></summary>
    private static GlPool Pool(uint token)
    {
        if (_table.IsNull || token == NoPool) return default;
        GlPool table = _table.Base;
        if (table.IsNull || token >= _table.Count) return default;
        return table[(int)token];
    }

    /// <summary>The host shadow of the pool this token names, or null. The normal weld reads and rewrites
    /// vertex bytes here, the same block <see cref="Upload"/> pushes, so nothing is read back.</summary>
    internal static byte* PoolHost(uint token)
    {
        GlPool p = Pool(token);
        return p.IsNull ? null : (byte*)p.Host;
    }

    /// <summary>The host shadow behind a GL buffer name, looked up among a VDO's two vertex pools -- a
    /// stream's <c>Buf</c> is a pool NAME, and which pool holds it is the descriptor's to say.</summary>
    internal static byte* PoolHostByName(uint name, uint tokA, uint tokB)
    {
        GlPool a = Pool(tokA), b = Pool(tokB);
        if (!a.IsNull && a.Name == name) return (byte*)a.Host;
        if (!b.IsNull && b.Name == name) return (byte*)b.Host;
        return null;
    }

    /// <summary>The engine is deleting this GL buffer name -- <c>DeletePool</c> @0x4d1d80 routes here for
    /// every named pool it releases. The name and the token both go back into circulation, so the row goes
    /// with them. Nothing is deleted by Alloy: the buffer is the pool's, the engine frees it here, and a
    /// second <c>glDeleteBuffers</c> on a recycled name would take out the pool that inherited it.</summary>
    [UnmanagedCallersOnly]
    private static void OnDeleteBuffer(uint name)
    {
        Retire(name);
        if (_origDelete != null) _origDelete(name);
    }

    private static void Retire(uint name)
    {
        if (!_byName.TryGetValue((int)name, out uint token)) return;
        _byName.Remove((int)name);
        if (_byToken.TryGetValue(token, out Entry e)) { _bytes -= e.Bytes; _byToken.Remove(token); }
        _retired++;
    }

    /// <summary>Does Alloy own the pool behind this engine buffer name? The VAO builders ask once per slot
    /// build, not per frame, and stamp the answer -- a name whose pool the engine uploaded (everything before
    /// the first present) still draws correctly, it is just not ours.</summary>
    internal static bool Owns(uint engineBuf)
    {
        if (!_armed || engineBuf == 0) return false;
        if (!_byName.TryGetValue((int)engineBuf, out uint token)) { _borrowed++; return false; }
        if (_byToken.TryGetValue(token, out Entry e) && !e.AlloyDrawn)
        { e.AlloyDrawn = true; _byToken[token] = e; }
        return true;
    }

    /// <summary>Has the engine written into this buffer since the last <see cref="ClearDirty"/>? The
    /// re-bake predicate's VERTEX-motion signal -- dangly and morph-animated nodes both commit through
    /// <c>CommitItemDynamicData</c>, so both land in the dirty set with no per-class test.</summary>
    internal static bool DirtySince(uint engineBuf)
        => engineBuf != 0 && _byName.TryGetValue((int)engineBuf, out uint token)
           && _byToken.TryGetValue(token, out Entry e) && e.Dirty;

    /// <summary>Close the dirty window, from inside the 4.6 bracket. There is nothing to copy -- the upload
    /// happened at the engine's own commit -- so what survives is the SIGNAL, and the readers of
    /// <see cref="DirtySince"/> run before this.</summary>
    internal static void ClearDirty()
    {
        if (_dirty.Count == 0) return;
        int n = _dirty.Count;
        for (int i = 0; i < n; i++)
            if (_byToken.TryGetValue(_dirty[i], out Entry e)) { e.Dirty = false; _byToken[_dirty[i]] = e; }
        _dirty.Clear();
    }

    /// <summary>An instrument, not a gate: an engine draw reaching a funnel while the Alloy window is CLOSED
    /// is the UI, the menu or a GUI 3D panel drawing for itself. The VDO it names owns up to three pool tokens
    /// (<c>VertexDataObject::Build</c> @0x45bac0 requests element at +0x18, static vertex at +0x24, dynamic at
    /// +0x30, and no fourth). Recorded, not acted on: three dictionary probes, allocating nothing.</summary>
    internal static void NoteEngineDraw(IntPtr vdo)
    {
        if (!_armed || vdo == IntPtr.Zero) return;
        var v = new VertexDataObject(vdo);
        Tag(v.ElemToken);
        Tag(v.VertToken);
        Tag(v.DynToken);
    }

    private static void Tag(uint token)
    {
        if (token == NoPool) return;
        if (_byToken.TryGetValue(token, out Entry e) && !e.EngineDrawn)
        { e.EngineDrawn = true; _byToken[token] = e; }
    }

    private const long OneMiB = 1024 * 1024;
    /// <summary>The engine's "this VDO has no such pool" token (<c>VertexDataObject::AddItem</c> tests it at
    /// +0x24 and +0x30), and equally <c>UnlockPool</c>'s first refusal.</summary>
    private const uint NoPool = EngineLayout.VdoNoPool;
    /// <summary>The size <c>GLRender::UnlockPool</c> reads as "the whole pool": both of its
    /// <c>glBufferSubData</c> sites substitute the pool's own byte count for it (@0x4cfda1/0x4cfda3 and
    /// @0x4cfe7d/0x4cfe7f).</summary>
    private const uint WholePool = 0xFFFFFFFF;   // not-an-offset: the "whole pool" size sentinel UnlockPool substitutes for
    /// <summary>Lock-flag bits that make <c>UnlockPool</c> do nothing at all.</summary>
    private const byte LockNoUpload = 0x18;   // not-an-offset: bits inside the lock-flags byte
    /// <summary>Usage bits from <c>RequestPool</c>'s third argument: 0x100 = the sub-data path (the three
    /// per-VDO pools are 0x100/0x100/0x102), 0x2 = dynamic.</summary>
    private const uint UsageSubData = 0x100, UsageDynamic = 0x2;   // not-an-offset: bits inside the usage word

    /// <summary>ADR 0016's measurement, and the only VRAM number this ticket ships. Called once a frame from
    /// inside the bracket, NOT from the upload funnel: the mark is RECORDED there (two compares) and the line
    /// -- which interpolates, and would otherwise do so a few hundred times per area load on the render
    /// thread -- is emitted here, and only once the mark has moved by a MiB.</summary>
    internal static void HighWater()
    {
        if (!_peakFresh) return;
        _peakFresh = false;
        if (_peakBytes < _lastReported + OneMiB) return;
        _lastReported = _peakBytes;
        _log?.Info($"OwnedBuffers: [vram] high-water {_peakBytes / OneMiB}MiB across {_peakCount} owned pools " +
                   $"(minted {_minted}, adopted {_adopted}, handed back {_refused}); engine host shadow " +
                   $"{EngineMib}MiB");
    }
    private static bool _peakFresh;
    private static long _lastReported;

    /// <summary>What the engine still holds in HOST memory for the same geometry -- <c>totalmempool</c>
    /// @0x19ad058, its own running total. The second of the two copies; there is no third.</summary>
    private static long EngineMib => _totalMemPool == null ? 0 : (long)*_totalMemPool / OneMiB;

    /// <summary>The WORLD edge. Nothing to do: a pool's life is the ENGINE's, and the area transition that
    /// revokes Alloy's scene is the same moment <c>DeletePool</c> retires each pool through the delete funnel
    /// this table already watches. Kept as the place that says so, and as the session's tagging report --
    /// an area transition is the natural point to have seen both a world and a UI draw.</summary>
    internal static void Revoke()
    {
        if (_byToken.Count == 0) return;
        int ui = 0, alloy = 0, both = 0;
        foreach (Entry e in _byToken.Values)
        {
            if (e.EngineDrawn) ui++;
            if (e.AlloyDrawn) alloy++;
            if (e.EngineDrawn && e.AlloyDrawn) both++;
        }
        _log?.Info($"OwnedBuffers: [uitag] {_byToken.Count} pools resident -- {alloy} bound by Alloy, {ui} " +
                   $"drawn by the engine outside the Alloy window (UI / GUI panels), {both} by both; " +
                   $"{_bytes / OneMiB}MiB in VRAM against {EngineMib}MiB of engine host shadow");
    }

    /// <summary>The GL-NAME edge: the objects went with the context. Rows are dropped and nothing is deleted
    /// -- deleting into a dead context is the terminal edge. The engine's pool descriptors still hold the dead
    /// names, as they do for its own buffers; context loss is terminal for both.</summary>
    internal static void ContextLost()
    {
        _byToken.Clear();
        _byName.Clear();
        _dirty.Clear();
        _bytes = 0;
    }

    internal static string Stats(int frames) =>
        _uploads == 0 ? "" : $"owned geom {_byToken.Count} pools {_bytes / OneMiB}MiB (peak {_peakBytes / OneMiB}MiB, " +
            $"engine host {EngineMib}MiB, uploads {_uploads} = {_subUploads} sub/{_uploads - _subUploads} whole, " +
            $"{_wholePool} whole-pool, grows {_grows}, repacks {_repacks}, minted {_minted}, adopted {_adopted}, handed back " +
            $"{_refused}, borrowed {_borrowed}, retired {_retired})";

    /// <summary>Off-device: the takeover contract, with no GL. A stood-down table takes nothing; a pool the
    /// engine refuses is refused here too; a taken pool is indexed by BOTH its token and its name; the dirty
    /// signal opens on the upload and closes on the frame; the delete funnel drops the row without deleting;
    /// the UI tag lands on the tokens a VDO names. The upload is the headless refusal path, run for real.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool wasArmed = _armed;
        GlPoolTable savedTable = _table;
        var savedTok = new Dictionary<uint, Entry>(_byToken);
        var savedName = new Dictionary<int, uint>(_byName);
        _byToken.Clear(); _byName.Clear(); _dirty.Clear();
        int refused = _refused, borrowed = _borrowed; _refused = 0; _borrowed = 0;

        // A fake pool table: two 0x20-byte descriptors behind a base pointer, laid out exactly as
        // RequestPool leaves them.
        byte* tableMem = stackalloc byte[0x20];
        byte* poolMem = stackalloc byte[2 * EngineLayout.PoolStride];   // a SIZE here, not a field read
        var table = new GlPoolTable((IntPtr)tableMem);
        var pools = new GlPool((IntPtr)poolMem);
        table.Base = pools;
        table.Count = 2;
        byte* payload = stackalloc byte[64];
        SetPool(pools[0], payload, 64, usage: UsageSubData, lockFlags: 0);
        SetPool(pools[1], payload, 64, usage: UsageSubData, lockFlags: LockNoUpload);

        _armed = false; _table = table;
        bool ok = !Take(0, 64, 0) && _byToken.Count == 0;            // stood down: the engine keeps its pool
        _armed = true;
        ok &= !Take(NoPool, 64, 0) && _byToken.Count == 0;           // not a token
        ok &= !Take(5, 64, 0);                                       // past the descriptor count
        ok &= !Take(0, 0, 0);                                        // a zero-length push is the engine's no-op
        ok &= !Take(1, 64, 0);                                       // ...and so is a locked pool
        // Headless: no GL entry points, so a real pool is REFUSED and handed back rather than half-taken.
        ok &= !Take(0, 64, 0) && _byToken.Count == 0 && _refused == 1 && pools.Named == 0;

        // From here the table is driven directly -- Record is the half that decides identity, and it is the
        // half that has no GL in it.
        Record(token: 3, name: 77, bytes: 128, fresh: true);
        ok &= _byToken.Count == 1 && _byName[77] == 3 && Owns(77) && !Owns(78);
        ok &= DirtySince(77) && _dirty.Count == 1;
        ClearDirty();
        ok &= !DirtySince(77) && _dirty.Count == 0;
        // A pool that grew keeps its row and its name, and the byte total follows it.
        Record(token: 3, name: 77, bytes: 256, fresh: false);
        ok &= _byToken.Count == 1 && _bytes == 256 && _grows > 0;
        // A recycled token that came back with a NEW name drops the old index rather than leaving it aliased.
        Record(token: 3, name: 78, bytes: 256, fresh: true);
        ok &= !_byName.ContainsKey(77) && _byName[78] == 3 && Owns(78);

        // The UI tag lands on the three tokens a VDO names, and on nothing else.
        byte* vdo = stackalloc byte[0x40];
        for (int i = 0; i < 0x40; i++) vdo[i] = 0xFF;                // every token = NoPool...
        new VertexDataObject((IntPtr)vdo).VertToken = 3;             // ...but the static one
        NoteEngineDraw((IntPtr)vdo);
        ok &= _byToken[3].EngineDrawn && _byToken[3].AlloyDrawn;

        // The delete funnel drops the row, and the name stops resolving. Nothing is queued: the buffer is
        // the engine's to free, and it is freeing it right now.
        Retire(78);
        ok &= _byToken.Count == 0 && !_byName.ContainsKey(78) && _bytes == 0 && !Owns(78);

        _byToken.Clear(); _byName.Clear(); _dirty.Clear();
        _bytes = 0; _grows = 0; _refused = refused; _borrowed = borrowed;
        _armed = wasArmed; _table = savedTable;
        foreach (var kv in savedTok) _byToken[kv.Key] = kv.Value;
        foreach (var kv in savedName) _byName[kv.Key] = kv.Value;
        log.Info(ok ? "OwnedBuffers.SelfCheck: passed" : "OwnedBuffers.SelfCheck: FAILED");
        return ok;
    }

    private static void SetPool(GlPool p, byte* host, int bytes, uint usage, byte lockFlags)
    {
        p.Host = (IntPtr)host;
        p.Size = bytes;
        p.Usage = usage;
        p.LockFlags = lockFlags;
        p.Uploaded = 0;
        p.Named = 0;
        p.Name = 0;
    }
}
