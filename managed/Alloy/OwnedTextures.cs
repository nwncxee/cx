using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The textures Alloy DRAWS FROM, uploaded by Alloy from the engine's DECODED PIXELS rather than
/// copied off its finished GL texture: the hook lets the engine's own upload (<c>glImageBasic</c> @0x48bcf0)
/// finish, then binds an Alloy-minted name and calls the trampoline again, in the ENGINE's context. Resources
/// are keyed on content, never a GL name; lifecycle in docs/owned-resource-lifetimes.md.</summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>One Alloy-owned texture: the resource behind a content key. <c>Gen</c> counts how many times
    /// the engine has re-uploaded this resource.</summary>
    private readonly struct Entry
    {
        internal readonly uint Owned;
        internal readonly long Bytes;
        internal readonly uint Gen;
        /// <summary>ADR 0023: the drip frame this resource lost its last index entry on; 0 = indexed (in use).</summary>
        internal readonly int Idle;
        internal Entry(uint owned, long bytes, uint gen, int idle = 0) { Owned = owned; Bytes = bytes; Gen = gen; Idle = idle; }
    }

    /// <summary>Content key -> Alloy's texture. The resource table; nothing in it is keyed on a GL name.</summary>
    private static readonly Dictionary<ulong, Entry> _res = new();
    /// <summary>Engine GL name -> content key. An INDEX, not a resource: it says which resource a tid means
    /// right now, and it is rewritten at the upload edge and erased at the delete edge.</summary>
    private static readonly Dictionary<int, ulong> _index = new();
    /// <summary>The reverse question the bindless path has to ask: is this name a copy of ours? A handle must
    /// never be minted on an engine texture, and a borrowed texture draws from the engine's id, so "armed" is
    /// not the same as "this name is owned".</summary>
    private static readonly HashSet<uint> _owned = new();
    /// <summary>Engine names Alloy draws BORROWED -- distinct tids, so the count is textures rather than
    /// frames. "All textures Alloy-created" is false the moment this is non-zero.</summary>
    private static readonly HashSet<int> _borrowed = new();
    /// <summary>Names retired and waiting for a bracket to delete them in. Pre-sized: entries are added from
    /// the engine's delete and upload funnels, which run on the render thread, and a List growth there would
    /// allocate on a hot-adjacent path.</summary>
    private static readonly List<uint> _pending = new(1024);
    private static ExtenderLog? _log;
    /// <summary>The CAurTexture layout probe's one-shot latch.</summary>
    private static bool _probedTex;
    private static bool _armed;
    /// <summary>Is the owned-texture path live? Read by the handle cache, which changes its invalidation
    /// discipline entirely on the answer (see <c>Issue.WipeHandles</c>).</summary>
    internal static bool Armed => _armed;

    /// <summary>Engine textures Alloy samples BORROWED, distinct by GL name. Not a refusal any more --
    /// nothing is attempted and declined; these are the names that never came through the upload funnel at
    /// all (see the class remarks for which, and why).</summary>
    internal static int Borrowed => _borrowed.Count;

    private static long _bytes, _peakBytes;

    /// <summary>The high-water figure the <c>/knobs</c> residency line reports, in MiB.</summary>
    internal static long PeakMib => _peakBytes / OneMiB;
    private static int _uploads, _reuploads, _peakCount;

    /// <summary>Wired once from <see cref="AlloyRenderer"/>'s init, before any frame runs.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _engBoundTex = (uint*)Native.Sym(SymbolMap.GlBoundTextures);
        if (_engBoundTex == null)
            log.Info("OwnedTextures: GLRender::m_boundTextures unresolved -- a name that dies outside the engine's DeleteTextures leaves its bind cache stale (#461)");
        // #284: resolved whatever `ownedtex` says. A texture with no GL id is a hole in the owned draw under
        // either key -- the retry is about the ENGINE's build having never run, not about who owns the copy.
        _buildAndStore = EngineFn.AurTextureBuildAndStore;
        _nullTex = (IntPtr*)Native.Sym(SymbolMap.AurTextureNullPtr);
        if (_nullTex == null)
            log.Info("OwnedTextures: CAurTextureBasic::m_pNullTexture unresolved -- a material naming the "
                   + "engine's NULL texture cannot be told from an unbuilt one, so it will be retried and "
                   + "written off as spent rather than named (#284)");
        if (_buildAndStore == null)
            log.Info("OwnedTextures: CAurTexture::BuildAndStore unresolved -- a texture whose build lost its "
                   + "race with the resource loader stays without a GL id, and its part is not drawn (#284)");
        // Restart knobs, every one: capability decisions about how the ENGINE loads content, read through Boot.
        // pltcpu costs a CPU composite per character texture at load -- which is what the engine did for years
        // -- and buys every PLT-skinned creature a place in the owned set. bcn spends load-time CPU to shrink
        // VRAM 3-4x on the uncompressed share the #265 census measured at ~2/3.
        bool wanted = Renderer.OwnedTex.Boot;
        _armed = wanted;
        _pltWanted = Renderer.PltCpu.Boot;
        _bcnWanted = Renderer.Bcn.Boot;
        _bcn = _bcnWanted && _armed;
        TranscodeBudget = Renderer.BcnDrip.Boot;
        _budgetCfgMb = Renderer.TexCache.Boot;
        PrefetchBudget = Renderer.TexPrefetch.Boot;
        _single = Renderer.TexSingle.Boot;
        DdsReader.Attach(log);
        TexLoad.Attach(log);
        // Zink bisect knobs: a handled texture's delete (auto = never on zink, whose bindless teardown faults on
        // a worker thread there) and the non-resident call ahead of it
        _texDelete = Renderer.TexDelete.Boot;
        _handleDrop = Renderer.HandleDrop.Boot;
        if (_armed)
            log.Info(_bcn
                ? "OwnedTextures: BCn transcode armed -- uncompressed uploads are re-encoded BC1/BC3 (alloy.bcn=0 in cx.cfg to keep them raw)"
                : "OwnedTextures: BCn transcode off (alloy.bcn=0) -- uncompressed uploads stay raw");
        // #360: RESOLVED whenever the table is armed, FORCED only when the knob says so. The flag is now
        // read as well as written -- it is what says whether a finished build went down the GPU branch and
        // therefore needs adopting by copy rather than by upload.
        if (_armed) _pltOnGpu = (int*)Native.Sym(SymbolMap.PltOnGpu);
        if (_armed && _pltWanted)
        {
            log.Info(_pltOnGpu != null
                ? "OwnedTextures: PLT compositing forced to the CPU -- player-character and tinted-creature "
                + "textures reach the upload funnel and become ownable (alloy.pltcpu=0 adopts the GPU composite by copy instead)"
                : "OwnedTextures: g_bGeneratePLTOnGPU unresolved -- PLT textures stay GPU-composited, "
                + "adopted by copy where that path is available and BORROWED where it is not (#360)");
            KeepPltOnCpu();
        }
        else if (_armed)
            log.Info("OwnedTextures: PLT compositing left on the GPU (alloy.pltcpu=0) -- the composite is adopted "
                   + "by a GPU-side copy at the bracket, keyed on the same content identity as an upload (#360)");
        log.Info(_armed
            ? "OwnedTextures: armed -- Alloy uploads from the engine's decoded pixels (alloy.ownedtex=0 to draw from engine ids)"
            : "OwnedTextures: stood down (alloy.ownedtex=0) -- owned draws sample engine texture ids");
    }

    /// <summary>Chain the two engine funnels this table lives on: the UPLOAD, where decoded pixels exist, and
    /// the DELETE -- the area-transition edge that fires no other edge Alloy watches.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!_armed) return;
        _origCreate = hooks.InstallRiCreateTexture(&OnCreateTexture);
        log.Info(_origCreate != null
            ? "OwnedTextures: watching RenderInterface::CreateTexture -- Alloy uploads every engine texture from its decoded pixels"
            : "OwnedTextures: CreateTexture not installed -- every texture is BORROWED (owned draws sample engine ids)");

        _origDelete = hooks.InstallRiDeleteTextures(&OnDeleteTextures);
        _origCreateCube = hooks.InstallRiCreateCubeTexture(&OnCreateCubeTexture);
        log.Info(_origCreateCube == null ? "OwnedTextures: CreateCubeTexture not installed -- cube textures stay the engine's"
            : _single ? "OwnedTextures: watching RenderInterface::CreateCubeTexture -- Alloy uploads every cube texture into a cube of its own"
            : "OwnedTextures: CreateCubeTexture hooked but texsingle=0 -- cube textures stay the engine's");
        // ADR 0023: the build itself, so a warm resource skips the read, the decode and the upload.
        _origBuildAndStoreTexture = hooks.InstallAurTextureBuildAndStoreTexture(&OnBuildAndStoreTexture);
        _znam = EngineFn.OperatorNewArray;
        log.Info(_origBuildAndStoreTexture != null && _znam != null
            ? $"OwnedTextures: watching CAurTextureBasic::BuildAndStoreTexture -- a resident texture is handed back without a read, decode or upload (alloy.texcache {(_budgetCfgMb < 0 ? "auto" : _budgetCfgMb.ToString())})"
            : "OwnedTextures: BuildAndStoreTexture not hooked or operator new[] unresolved -- textures are kept across the world edge but every build is cold");
        log.Info(_origDelete != null
            ? "OwnedTextures: watching RenderInterface::DeleteTextures -- a tid stops resolving the moment it stops meaning its resource"
            : "OwnedTextures: DeleteTextures not installed -- a recycled engine name will resolve the previous area's resource");

        // #360: glImageBasic is taken whenever the table is armed, because it is now TWO things -- half of
        // #358's hold, and the edge that says a PLT build has finished so its composite can be adopted.
        // ConstructImage is the hold alone, so it stays gated on the flag actually being there to hold.
        // Attach ran first (AlloyRenderer wires the pass table before it installs it), so a null _pltOnGpu
        // means the symbol did not resolve -- already logged there, with its reason.
        _origGlImageBasic = hooks.InstallAurTextureGlImageBasic(&OnGlImageBasic);
        if (_pltOnGpu != null)
            _origConstructImage = hooks.ChainAurTextureConstructImage(&OnConstructImage, ConstructPriority);
        if (_pltWanted)
            log.Info(_origConstructImage != null && _origGlImageBasic != null
                ? "OwnedTextures: GPU-PLT held at zero on both readers -- an options-apply that re-enables it "
                + "cannot rebuild a character outside the upload funnel any more (#358)"
                : "OwnedTextures: a GPU-PLT reader did not install -- the flag is only re-asserted per frame, so "
                + "an options-apply can still rebuild PLT textures GPU-side and leave them BORROWED (#358)");
        else
            log.Info(_origGlImageBasic != null
                ? "OwnedTextures: watching CAurTextureBasic::glImageBasic -- a GPU-composited PLT build is "
                + "queued for adoption by copy on the next bracket (#360)"
                : "OwnedTextures: glImageBasic not installed -- a GPU-composited PLT texture has no edge to "
                + "adopt it on and stays BORROWED (#360)");
        TexStream.Install(log, hooks);
        ModelLoad.Install(log, hooks);
    }

    private static AurTextureConstructImageFn* _origConstructImage;
    /// <summary>Outside <see cref="RasterReader"/>'s link on the same function: the hold covers its work too.</summary>
    private const int ConstructPriority = 50;
    private static AurTextureGlImageBasicFn _origGlImageBasic;

    /// <summary>The engine is about to decide whether to composite this texture's PLT layers on the CPU.
    /// Cold -- once per texture build -- and the whole body is one predicted compare.</summary>
    [UnmanagedCallersOnly]
    private static void OnConstructImage(IntPtr tex)
    {
        KeepPltOnCpu();
        (*_origConstructImage)(tex);
        // CX-117: on the way out the CPU composite is still in memory at +0x58 -- the one point in the frame
        // a PLT's finished pixels can be compared against Alloy's own resolve with no GL readback at all.
        if (Db.PltVerify.On) Db.PltVerify.Offer(tex);
    }

    /// <summary>...and the same hold on the reader that routes a built PLT texture around the upload funnel;
    /// both readers, because either one alone leaves half the decision to a stale flag. With the hold off it
    /// is also the edge that says a GPU composite has FINISHED: the flag is read before the original runs and
    /// the queue is filled after, when the cells carry names. Cold: once per texture build.</summary>
    [UnmanagedCallersOnly]
    private static int OnGlImageBasic(IntPtr tex, byte useSubImage)
    {
        if (_pltWanted) KeepPltOnCpu();
        bool gpu = _pltOnGpu != null && *_pltOnGpu != 0;
        // glImageBasic returns int (#433: the MSVC export encodes the return type Itanium drops), and
        // the caller propagates it -- so the trampoline's value must be carried, not dropped into work.
        int r = _origGlImageBasic != null ? _origGlImageBasic(tex, useSubImage) : 0;
        if (gpu) QueuePltCopy(tex);
        return r;
    }

    private static RiCreateTextureFn _origCreate;
    private static RiDeleteTexturesFn _origDelete;
    private static long _retired;

    /// <summary>The name byte the key hashes from: <see cref="CAurTextureBasic.ResolvedName"/>, a
    /// <c>char[0x20]</c> <c>LoadImage</c> @0x487980 fills and <c>CAurTexture::Restore</c> @0x48e300
    /// re-acquires by. Stable across a delete/rebuild, which is what makes it an identity. A pointer into the
    /// buffer, because the hash walks it.</summary>
    private static byte* NameOf(IntPtr tex) => (byte*)Unsafe.AsPointer(ref new CAurTextureBasic(tex).ResolvedName);

    /// <summary><c>GLRender::m_boundTextures</c>: 16 <c>{target, name}</c> entries the engine's cache-checked
    /// binds early-out on. <c>GLRender::DeleteTextures</c> zeroes the entry naming a deleted texture, so a
    /// name that dies without passing through it leaves the engine believing it is still bound: the driver
    /// reissues the name, its bind is skipped, and the next upload re-specifies whatever is REALLY bound.</summary>
    private static uint* _engBoundTex;
    private const int EngTexStages = 16;

    /// <summary>What the engine's own DeleteTextures would have done with this name. Cold; 16 compares.</summary>
    private static void ScrubEngineBoundCache(uint name)
    {
        if (_engBoundTex == null) return;
        for (int i = 0; i < EngTexStages; i++)
            if (_engBoundTex[i * 2 + 1] == name) _engBoundTex[i * 2 + 1] = 0;
    }

    /// <summary>The engine is about to upload cell <paramref name="cell"/> of <paramref name="tex"/> into the
    /// GL name it has bound. Let that happen, then do it again into a texture of ours.
    /// <para>Cold -- once per texture per (re)build, never per frame -- so the allocations a Dictionary
    /// insert makes are not on a hot path. Runs in the ENGINE's context; see the class remarks.</para></summary>
    [UnmanagedCallersOnly]
    private static void OnCreateTexture(IntPtr tex, byte useSubImage, int cell)
    {
        long t0 = FrameZones.Now;
        // #386: ONE upload. Adopt uploads into Alloy's name and hands that name to the engine's cell array;
        // only when it declines (or alloy.texsingle=0) does the engine's own copy get built.
        bool handled = _single && Adopt(tex, cell, single: true);
        if (!handled)
        {
            if (_origCreate != null) _origCreate(tex, useSubImage, cell);
            Adopt(tex, cell, single: false);
        }
        FrameZones.Add(FrameZones.Zone.Tex, t0);
        FrameZones.Count(FrameZones.Ev.TexUpload);
    }

    /// <summary>The upload handler's body, split out so <see cref="SelfCheck"/> can exercise it (an
    /// <c>UnmanagedCallersOnly</c> method cannot be called from managed code). True = Alloy's upload stands in
    /// for the engine's (<paramref name="single"/>), so the engine's cell entry names Alloy's texture and its
    /// own name is deleted unused; false = the caller must run the engine's upload.</summary>
    private static bool Adopt(IntPtr tex, int cell, bool single)
    {
        if (!_armed || _origCreate == null) return false;
        // The BASIC vtable specifically: every field below belongs to the subclass, and #138/#221 say a
        // texture pointer is only ever as good as its vtable.
        if (!AurTex.IsBasic(tex)) return false;

        var b = new CAurTextureBasic(tex);
        uint* cells = (uint*)b.CellNames;
        int n = b.NumX * b.NumY;
        if (cells == null || cell < 0 || cell >= n || n > MaxCells) return false;
        int etid = (int)cells[cell];
        if (etid <= 0) return false;
        FreshName((uint)etid);
        // A cell already naming an owned texture is the engine re-uploading INTO our name (a Restore after a
        // warm hand-back, or a refresh): fresh storage under a new name, exactly as a re-upload always was.

        // ponytail: before the first present there is no 4.6 context and no entry points, so a texture
        // uploaded that early stays borrowed for the session -- nothing re-drives this funnel for a texture
        // the engine already holds. The window is the main menu, which Alloy does not draw, and the count
        // says so out loud rather than hiding it. Upgrade path: re-drive AurTextureRefreshAll @0x48f050 from
        // the engine's context once the bracket exists.
        if (!GlContextService.Available)
        {
            if (!single) _borrowed.Add(etid);
            return false;
        }

        ulong key = Key(tex, cell);
        uint dst;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &dst);
        if (dst == 0) { if (!single) _borrowed.Add(etid); return false; }

        // The one bind-to-edit that stays: the ENGINE's upload below writes through the TEXTURE_2D binding,
        // so the save/restore keeps GLRender's bind cache true. Raw glBindTexture, NOT AurTextureBindTID:
        // routing our name through its wrapper would leave that cache naming a texture the engine does not own.
        int savedBind; Gl.GetIntegerv(Gl.TEXTURE_BINDING_2D, &savedBind);
        Gl.DrainErrors(8);   // the verdict below is ours to read, so nobody else's error may be sitting here
        Gl.BindTexture(Gl.TEXTURE_2D, dst);
        // ...and always the ALLOCATING form. `true` is the glTexSubImage2D path, which assumes storage a
        // freshly generated name does not have.
        _origCreate(tex, 0, cell);
        uint err = Gl.GetError();          // read BEFORE Measure, whose own queries would mask it
        if (err == 0 && DdsReader.Lookup((byte*)b.Image, out DdsReader.Info dds) && !DdsReader.EngineUploads(dds.Format))
        {
            uint real = UploadDdsFormat(dst, (byte*)b.Image, dds, etid);
            if (real != 0) { Gl.DeleteTextures(1, &dst); dst = real; }
        }
        long bytes = err == 0 ? Measure(dst) : 0;
        // An image unpacked from a DDS was BCn already: a second encode would only lose more.
        bool wantBcn = err == 0 && _bcn && b.CompressedSize == 0 && tex != RasterReader.Unpacked;
        TexScan.Result scan = default;
        bool scanned = err == 0 && TexScan.Scan(tex, cell, CoverageAlpha, wantBcn, out scan);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)savedBind);

        if (err != 0)
        {
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
            if (_diagLeft > 0) { _diagLeft--; _log?.Info($"OwnedTextures: tid {etid} {(single ? "left to the engine" : "borrowed")} -- the engine's upload into our name gave 0x{err:x}"); }
            if (!single) _borrowed.Add(etid);
            return false;   // single: the caller builds the engine's copy, and the double-path Adopt borrows it
        }

        if (single)
        {
            // The engine's cell now names OUR texture; its own name never received pixels and dies here.
            // The engine's bind cache still says the old name is bound on unit 0 -- a name that no longer
            // exists, so the next AurTextureBindTID of ours compares unequal and binds. Deleting a bound name
            // resets the binding to 0, which is the state that cache should read as.
            cells[cell] = dst;
            if (cell == 0) b.GlId = (int)dst;
            uint old = (uint)etid;
            if (!_owned.Contains(old) && Gl.DeleteTextures != null) { Gl.DeleteTextures(1, &old); ScrubEngineBoundCache(old); }
            else _engineHeld.Remove(old);   // a refresh INTO our name: the engine will never return the old one, so Discard deletes it
            _engineHeld.Add(dst);
            _borrowed.Remove(etid);
            _singleUploads++;
            etid = (int)dst;
        }
        Replace(key, etid, dst, bytes);
        Census(tex, bytes, 0, n, cell);
        HighWater();
        if (scanned) _stats[dst] = new Stat { Holes = scan.Holes, Graded = scan.Graded, Mean = scan.Mean, MeanOk = scan.MeanOk };
        else _stats.Remove(dst);
        if (AlloyCensus._dumpTex != null && AlloyCensus.TexName(tex).Contains(AlloyCensus._dumpTex, StringComparison.OrdinalIgnoreCase))
            _log?.Info($"[dumpframe] SCAN tex={AlloyCensus.TexName(tex)} cell={cell} scanned={scanned} compressed={b.CompressedSize != 0} refusedFmt={TexScan.LastRefusedFormat} holes={scan.Holes:F2} graded={scan.Graded:F2} {scan.W}x{scan.H} owned={dst}");
        if (wantBcn && scan.Rgba != null) EnqueueTranscode(key, etid, dst, bytes, scan.Rgba, scan.W, scan.H, scan.Mips);
        return true;
    }

    /// <summary><c>texsingle</c> in cx.cfg (boot 1) -- one upload per texture instead of the engine's copy
    /// plus Alloy's. 0 is the double path.</summary>
    private static bool _single = true;
    private static long _singleUploads;
    /// <summary>Owned names the ENGINE holds in a cell array (a single upload or a warm hand-back). Such a name
    /// must not be GL-deleted by Alloy while the engine may still bind it for its own 2D use -- icons,
    /// portraits and glyph sheets all come through this funnel -- so a swap or eviction drops its handle and
    /// leaves the delete to the engine's own DeleteTextures, which <see cref="FilterRetire"/> passes on.</summary>
    private static readonly HashSet<uint> _engineHeld = new();

    /// <summary>What the funnel-side scan (<see cref="TexScan"/>) learned about each owned name: the cutout
    /// hole fraction and the mean colour. Replaces every GPU readback the consumers used to make. Keyed by
    /// the owned name; removed with the name.</summary>
    private struct Stat { public float Holes; public float Graded; public Vector3 Mean; public bool MeanOk; }
    private static readonly Dictionary<uint, Stat> _stats = new();
    /// <summary>The hole and graded-alpha fractions of an owned name (or of the owned copy behind an engine
    /// tid). False = the name never came through the funnel; the caller tests rather than casts a possible slab.</summary>
    internal static bool TryHoles(int tid, out float holes, out float graded)
    {
        holes = 1f; graded = 0f;
        if (!TryStat(tid, out Stat st)) return false;
        holes = st.Holes; graded = st.Graded;
        return true;
    }
    /// <summary>The mean colour of an owned name, 0..1 in the texture's own encoding.</summary>
    internal static bool TryMean(int tid, out Vector3 mean)
    {
        mean = default;
        if (!TryStat(tid, out Stat st) || !st.MeanOk) return false;
        mean = st.Mean;
        return true;
    }
    private static bool TryStat(int tid, out Stat st)
    {
        if (tid > 0 && _stats.TryGetValue((uint)tid, out st)) return true;
        if (tid > 0 && _index.TryGetValue(tid, out ulong key) && _res.TryGetValue(key, out Entry e) && _stats.TryGetValue(e.Owned, out st)) return true;
        st = default;
        return false;
    }

    /// <summary>Stop drawing from a texture and queue it for the bracket to delete. Shared by the delete
    /// funnel and the re-upload path, which retire for different reasons and by the same mechanics.</summary>
    private static void Discard(Entry e)
    {
        if (e.Owned == 0) return;
        _owned.Remove(e.Owned); _stats.Remove(e.Owned);
        if (_engineHeld.Contains(e.Owned)) _pendingHandles.Add(e.Owned);   // the engine deletes it; we only drop the handle
        else { _pending.Add(e.Owned); _pendingMarked = false; }
        _bytes -= e.Bytes;
    }

    /// <summary>Adopt a freshly uploaded texture as the resource behind <paramref name="key"/>, retiring
    /// whatever held that key before. Split out from <see cref="Adopt"/>, whose other half is GL, so
    /// <see cref="SelfCheck"/> can assert the table mutation rather than a copy of it.</summary>
    private static void Replace(ulong key, int etid, uint owned, long bytes)
    {
        uint gen = 0; int idle = 0;
        if (_res.TryGetValue(key, out Entry old))
        {
            gen = old.Gen + 1; idle = old.Idle;
            if (idle != 0) _idleBytes += bytes - old.Bytes;
            Discard(old);
            _reuploads++;
            AlloyRenderer.NoteOwnedNameSwapped(old.Owned, owned);   // #386: records take the new name in place, no re-stage
        }
        _res[key] = new Entry(owned, bytes, gen, idle);
        _evictDue = true;
        _owned.Add(owned);
        _index[etid] = key;
        _borrowed.Remove(etid);
        _uploads++;
        _bytes += bytes;
        if (_res.Count > _peakCount) _peakCount = _res.Count;
    }

    /// <summary>A resource's identity: a 64-bit FNV-1a over the resolved name at <c>+0xa8</c> (ASCII
    /// lower-cased, as <c>AurTextureFind</c> compares it) and the PLT scheme array at <c>+0x48</c>/<c>+0xd4</c>
    /// (two skins sharing one resref are two resources), with the atlas cell in the low <see cref="CellMask"/>
    /// bits. NOT <c>AurTextureCalculateHashID</c>: a character product modulo a prime, blind to anagrams.</summary>
    private static ulong Key(IntPtr tex, int cell)
    {
        const ulong Prime = 0x100000001B3UL;   // not-an-offset: FNV-1a 64 prime
        ulong h = 0xCBF29CE484222325UL;   // not-an-offset: FNV-1a 64 basis
        for (byte* p = NameOf(tex); *p != 0; p++)
        {
            uint c = *p; if (c - 'A' < 26) c += 32;
            h = (h ^ c) * Prime;
        }
        var b = new CAurTextureBasic(tex);
        short n = b.SchemeCount;
        int* sch = (int*)b.Schemes;
        h = (h ^ (uint)n) * Prime;
        for (int i = 0; sch != null && i < n; i++) h = (h ^ (uint)sch[i]) * Prime;
        return (h & ~CellMask) | ((ulong)(uint)cell & CellMask);
    }
    /// <summary>The atlas cell's field in a key; <see cref="MaxCells"/> is what the funnels refuse past it.</summary>
    private const ulong CellMask = 0xFFFF;   // not-an-offset: key bit field
    private const int MaxCells = (int)CellMask + 1;

    /// <summary>How much VRAM <paramref name="tex"/> occupies, over every level
    /// that has storage. A block format answers directly; anything else is width*height*(sum of the channel
    /// widths). Accounting only -- a driver that answers nothing costs a zero, not a refusal.</summary>
    private static long Measure(uint tex)
    {
        long total = 0;
        for (int level = 0; level < MaxLevels; level++)
        {
            int w = 0, h = 0;
            Gl.GetTextureLevelParameteriv(tex, level, Gl.TEXTURE_WIDTH, &w);
            Gl.GetTextureLevelParameteriv(tex, level, Gl.TEXTURE_HEIGHT, &h);
            if (w <= 0 || h <= 0) break;
            int compressed = 0;
            Gl.GetTextureLevelParameteriv(tex, level, Gl.TEXTURE_COMPRESSED, &compressed);
            if (compressed != 0)
            {
                int size = 0;
                Gl.GetTextureLevelParameteriv(tex, level, Gl.TEXTURE_COMPRESSED_IMAGE_SIZE, &size);
                total += size > 0 ? size : 0;
                continue;
            }
            int r = 0, g = 0, b = 0, a = 0;
            Gl.GetTextureLevelParameteriv(tex, level, Gl.TEXTURE_RED_SIZE, &r);
            Gl.GetTextureLevelParameteriv(tex, level, Gl.TEXTURE_GREEN_SIZE, &g);
            Gl.GetTextureLevelParameteriv(tex, level, Gl.TEXTURE_BLUE_SIZE, &b);
            Gl.GetTextureLevelParameteriv(tex, level, Gl.TEXTURE_ALPHA_SIZE, &a);
            total += (long)w * h * (r + g + b + a) / 8;
        }
        return total;
    }

    /// <summary>Texture parameters that must survive the copy. Read off the source, written to the
    /// destination, in this order -- the four sampler dials, the mip range, and the four swizzle
    /// channels.</summary>
    private static readonly uint[] CarriedParams =
    {
        Gl.TEXTURE_MIN_FILTER, Gl.TEXTURE_MAG_FILTER, Gl.TEXTURE_WRAP_S, Gl.TEXTURE_WRAP_T,
        Gl.TEXTURE_BASE_LEVEL, Gl.TEXTURE_MAX_LEVEL,
        Gl.TEXTURE_SWIZZLE_R, Gl.TEXTURE_SWIZZLE_G, Gl.TEXTURE_SWIZZLE_B, Gl.TEXTURE_SWIZZLE_A,
    };

    /// <summary>One queued composite. Every refusal is counted and the source is left BORROWED, which is the
    /// state it was already in -- a refused copy costs the character nothing it had.</summary>
    private static void Copy(PltCopy q)
    {
        Gl.DrainErrors(8);   // the verdict below is ours to read

        int levels = 0, fmt = 0;
        int* lw = stackalloc int[MaxLevels], lh = stackalloc int[MaxLevels];
        for (; levels < MaxLevels; levels++)
        {
            int w = 0, h = 0;
            Gl.GetTextureLevelParameteriv(q.Src, levels, Gl.TEXTURE_WIDTH, &w);
            Gl.GetTextureLevelParameteriv(q.Src, levels, Gl.TEXTURE_HEIGHT, &h);
            if (w <= 0 || h <= 0) break;
            lw[levels] = w; lh[levels] = h;
        }
        Gl.GetTextureLevelParameteriv(q.Src, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        // An UNSIZED answer cannot be handed to glTexStorage2D, and guessing a promotion is what changed the
        // sampler swizzle under #211. Refused, named, and left borrowed instead.
        if (levels == 0 || fmt == 0 || fmt == (int)Gl.RGBA || fmt == (int)Gl.RGB)
        {
            _borrowed.Add((int)q.Src); _pltCopyRefused++;
            if (_pltCopyDiagLeft-- > 0)
                _log?.Info($"OwnedTextures: GPU-PLT tid {q.Src} not copied -- levels={levels} "
                         + $"internalformat=0x{fmt:x} (unsized or unreadable), left BORROWED (#360)");
            return;
        }

        int* saved = stackalloc int[CarriedParams.Length];
        for (int i = 0; i < CarriedParams.Length; i++)
            Gl.GetTextureParameteriv(q.Src, CarriedParams[i], saved + i);

        uint dst;
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &dst);
        if (dst == 0) { _borrowed.Add((int)q.Src); _pltCopyRefused++; return; }
        Gl.TextureStorage2D(dst, levels, (uint)fmt, lw[0], lh[0]);
        for (int i = 0; i < CarriedParams.Length; i++)
            Gl.TextureParameteri(dst, CarriedParams[i], saved[i]);
        for (int l = 0; l < levels; l++)
            Gl.CopyImageSubData(q.Src, Gl.TEXTURE_2D, l, 0, 0, 0, dst, Gl.TEXTURE_2D, l, 0, 0, 0,
                                lw[l], lh[l], 1);

        uint err = Gl.GetError();       // read BEFORE Measure, whose own queries would mask it
        if (err != 0)
        {
            if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
            _borrowed.Add((int)q.Src); _pltCopyRefused++;
            if (_pltCopyDiagLeft-- > 0)
                _log?.Info($"OwnedTextures: GPU-PLT tid {q.Src} not copied -- the copy gave 0x{err:x} "
                         + $"({lw[0]}x{lh[0]}, {levels} levels, 0x{fmt:x}), left BORROWED (#360)");
            return;
        }

        Replace(q.Key, (int)q.Src, dst, Measure(dst));
        _stats[dst] = new Stat { Holes = 0f };   // PLT composites are opaque; unscanned would read as masked
        _pltCopied++;
        HighWater();
    }

    /// <summary>The GL-NAME edge: the objects went with the context. Names are dropped and nothing is deleted
    /// -- deleting into a dead context is the terminal edge.</summary>
    internal static void ContextLost()
    {
        _res.Clear();
        _index.Clear();
        _owned.Clear();
        _borrowed.Clear();
        _pending.Clear(); _pendingForget.Clear(); foreach (var d in _deferred) d.Clear();   // the names died with the context
        _pltQ.Clear();   // #360: queued sources are names in a context that no longer exists
        _transCtx++;   // jobs in flight name owned textures of the dead context; each drops out at its upload
        _stats.Clear();
        _meta.Clear(); _inFlight.Clear(); _pendingHandles.Clear(); _idleBytes = 0; _evictDue = false;
        _prefetchN = 0; _prefetchHead = 0; _prefetchSeen.Clear(); _engineHeld.Clear();
        _bytes = 0;
        // #284: every engine texture is unbuilt again on the far side of a context loss, so the one-attempt
        // ledger has to go with the names it was a ledger about -- keeping it would refuse the whole world
        // its single retry. The queue holds pointers into a world that may not survive either.
        _rebuildTried.Clear(); Array.Clear(_rebuildQ); _rebuildN = 0; _dripFrame = 0;
        _standIn = new();   // their names went with the context too; the next draw that needs one re-mints it
        _censusRaw = _censusBcn = _censusTrans = 0;
        _censusRawBytes = _censusBcnBytes = _censusSaved = 0;
        _censusLastUpload = 0; _censusReported = false;
        // The refusal ledger was about the OLD context's driver state; the new context answers fresh.
        _bcnFails = 0; _bcn = _bcnWanted && _armed;
        Issue.ForgetHandles();   // the names went with the context; nothing to make non-resident
    }
}
