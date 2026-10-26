using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Framework;
using CX.Graphics;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy.Db;

/// <summary>The registry's load pipeline; which thread owns each hop is the contract. <b>Game thread</b>
/// (<see cref="Enqueue"/>/<see cref="Offer"/>) resolves the resref to a <see cref="TexLocation"/> VALUE record,
/// since <c>CExoResMan</c> is not thread-safe, so every engine-memory read happens there; the <c>cx-tex</c>
/// worker (<see cref="Work"/>) decodes, pure; the bracket (<see cref="Drain"/>) uploads; <see cref="Tick"/> files.</summary>
internal static unsafe partial class TexRegistry
{
    /// <summary>Resource types: TGA, DDS and PLT (<c>CResHelper&lt;CResPLT,6&gt;</c>). The registry asks for
    /// the PLT alone for a <see cref="TexKind.Plt"/> slot, and otherwise the DDS/TGA pair in the order the
    /// engine's format race (<see cref="TexLoad.Winner"/>) ranks them.</summary>
    private const ushort TgaResType = 3, PltResType = 6, DdsResType = 0x7f1;   // not-an-offset: restype ids

    /// <summary>Loads in flight at once. The queue IS the budget: past it an acquire stays
    /// <see cref="TexState.Loading"/> and is offered on a later <see cref="Drain"/>.</summary>
    private const int MaxPending = 64;
    /// <summary>Uploads per bracket. An upload is a driver allocation plus a copy, so a whole area's arrival
    /// is spread over frames rather than spent in one.</summary>
    private const int UploadsPerFrame = 8;
    /// <summary>The preview's largest side: the first mip at or under it, and the levels below. A 64 px BC3 level
    /// is 4 KB, so a whole area's previews upload in a few frames.</summary>
    internal const int PreviewSide = 64;
    /// <summary>Fast-queue uploads per bracket, and the bytes they may spend: previews are tiny, but a small texture
    /// decoded whole on the fast queue (a TGA, a PLT) is not.</summary>
    private const int FastUploadsPerFrame = 64;
    private const long FastBytesPerFrame = 8L << 20;

    private sealed class Job
    {
        public int Slot;
        public uint Gen;
        public string Resref = "";
        public TexKind Kind;
        public TexLocation Loc;
        /// <summary>The decoded bytes, native and owned by the job until the upload or the recycle.</summary>
        public byte* Data;
        public int W, H, Levels, Channels;
        /// <summary>The GL internal format and the (format, type) pair of an uncompressed upload.</summary>
        public uint GlFmt, PixFmt, PixType;
        public bool Compressed, GenMips, Clamp, Nearest, Mip;
        public TexCoverage Cov;
        /// <summary>Which decode answered <see cref="Cov"/>, in the words the <c>texcov:</c> line prints -- the
        /// route by which a texture that draws solid can be named out of <c>cx.log</c>. Null until a decode sets it.</summary>
        public string? CovWhy;
        /// <summary>The image's mean colour (its own encoding), for the sky-matched fog; valid when MeanOk.</summary>
        public System.Numerics.Vector3 Mean;
        public bool MeanOk;
        /// <summary>The slot's <c>.txi</c> names a <c>bumpyshinytexture</c>, which is the engine's own route
        /// into the water class. Read at the offer, on the game thread. <see cref="TxiNamesBumpyShiny"/>.</summary>
        public bool BumpyShiny;
        /// <summary>The slot's <c>.txi</c> names an <c>envmaptexture</c>, which is what makes the engine's
        /// material env-MAPPED (<c>Material::ParseField</c> @0x5c5850 stores it at <c>+0xd</c>,
        /// <c>Material::HasEnvMap</c> @0x5c6180 answers for it) -- the "this is shiny 2002 content" flag the
        /// lit stage spends the diffuse alpha as reflectivity on. <see cref="TxiNamesEnvMap"/>.</summary>
        public bool EnvMap;
        public long Bytes;
        public string? Why;
        /// <summary>Came off the fast queue: a preview pass, which a texture needing no preview finishes whole.</summary>
        public bool Fast;
        /// <summary><see cref="Data"/> is a preview tail (native, from <see cref="TryTail"/>) rather than the full chain.</summary>
        public bool Tail;
        /// <summary>The <c>.txi</c> grid and flipbook (<see cref="TxiMaterial"/>); <see cref="Pixels"/> is the whole
        /// image once <see cref="CutGrid"/> made the upload one cell of it.</summary>
        public int GridX = 1, GridY = 1, FullW;
        public bool Cycle;
        public float Fps = 1f;
        public byte[]? Pixels;
        /// <summary>The <c>.txi</c> ripple (<see cref="ParseArturo"/>), and the running one the worker built
        /// from it (<see cref="KeepArturo"/>) for the bracket to file.</summary>
        public ArturoTxi Arturo;
        /// <inheritdoc cref="Arturo"/>
        public Arturo? Ripple;
    }

    /// <summary>Does this texture's <c>.txi</c> name a <c>bumpyshinytexture</c>? That is the water class:
    /// <c>Material::ParseField</c> @0x5c5850 stores the name, <c>Material::HasBumpyShinyMap</c> @0x5c5ca0
    /// answers for it, and <c>Scene::AddPartsTreeToDrawBuckets</c> @0x460880 files the part into bucket 6 on
    /// it. Case-insensitive, as the engine's <c>strcasecmp</c> is.</summary>
    private static bool TxiNamesBumpyShiny(string? txi)
        => txi != null && txi.Contains("bumpyshinytexture", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc cref="TxiNamesBumpyShiny(string)"/>
    private static bool TxiNamesEnvMap(string? txi)
        => txi != null && txi.Contains("envmaptexture", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the bracket publishes back to the game thread: the slot it uploaded for, the generation
    /// it uploaded against, and either a name and handle or the reason there is none.</summary>
    private readonly record struct Landed(int Slot, uint Gen, uint Name, ulong Handle, long Bytes,
                                         TexCoverage Cov, bool BumpyShiny, bool EnvMap, string? Why,
                                         bool Preview = false, string? CovWhy = null);

    private static readonly Queue<Job> _in = new(), _out = new();
    /// <summary>The fast queue: every new load's first pass, ahead of every full one (<see cref="Job.Fast"/>).
    /// Not bounded by <see cref="MaxPending"/> -- what it holds is a preview's few KB.</summary>
    private static readonly Queue<Job> _inFast = new(), _outFast = new();
    private static int _fastPending;
    private static long _previews;
    private static readonly Stack<Job> _pool = new();
    private static readonly Queue<int> _waiting = new();
    private static readonly Queue<Landed> _landed = new();
    /// <summary>Slots the bracket re-minted a handle for (a sampler change, or an idle slot acquired again).
    /// <see cref="Tick"/> marks their dependents: a record staged while the handle was 0 drew black.</summary>
    private static readonly Queue<int> _reminted = new();
    private static readonly object _gate = new();
    private static int _pending;

    /// <summary>Textures on their way in: being read and decoded, or queued behind <see cref="MaxPending"/>.
    /// The streaming bar's texture term; 0 once an area has settled.</summary>
    internal static int InFlight { get { lock (_gate) return _pending + _waiting.Count + _fastPending; } }
    private static bool _worker;
    /// <summary>Per worker: the file handles each one keeps open.</summary>
    [ThreadStatic] private static Handles? _handles;
    /// <summary>The worker's one decode buffer for the coverage scan, grown to the largest level 0 it has
    /// met. Worker-thread only, like <see cref="_handles"/>: a managed array per texture would put
    /// large-object allocations on it, and NativeAOT's GC stops the main thread for those.</summary>
    [ThreadStatic] private static byte[]? _covScratch;

    /// <summary>Offer slot <paramref name="i"/>'s load, or queue it for a later <see cref="Tick"/>. Game
    /// thread; takes no table lock, because <see cref="_pend"/> is the only column it writes and only the game
    /// thread writes it.</summary>
    private static void Enqueue(int i)
    {
        if (_pend[i] != PendNone) return;   // already queued or in flight: one offer per load, ever
        if (!ResLocate.Ready && !ResLocate.Attach(out string? why))
        { Fail(i, $"the resource manager is not attached ({why ?? "no reason given"})"); return; }
        bool fast = !_preview[i] && Renderer.TexPreview.V;
        if (!fast)
        {
            bool room;
            lock (_gate) room = _pending < MaxPending;
            if (!room) { _pend[i] = PendWaiting; lock (_gate) _waiting.Enqueue(i); return; }
        }
        Offer(i, fast);
    }

    private static void Offer(int i, bool fast)
    {
        string resref = _resref[i] ?? "";
        Job j = Rent();
        j.Slot = i; j.Gen = _gen[i] & 0xFFF; j.Resref = resref; j.Kind = _kind[i];
        j.Data = null; j.Why = null; j.W = j.H = j.Levels = 0; j.Bytes = 0; j.Channels = 0; j.Fast = fast; j.Tail = false;
        // The .txi is its own resource, and the only one the engine reads: CAurTextureBasic::Init @0x588940
        // and Material::Init @0x5c5b90 both take it from AurResGet(name, ".txi"), which reaches the resman's
        // CResHelper<CResTXI,2022> and nothing else. So it is looked up here, on the thread that may read the
        // resource tables, and a hak's .txi beats the base game's by the resman's own order.
        string? txi = ReadResourceText(resref, TextFontLoad.ResTxi);
        TxiSampler(i, txi);
        j.Pixels = null; j.FullW = 0; j.Ripple = null;
        TxiMaterial(i, txi, j);
        j.Compressed = false; j.GenMips = false; j.Clamp = _clamp[i]; j.Cov = TexCoverage.Unknown; j.MeanOk = false;
        j.Nearest = _nearest[i]; j.Mip = _mipmap[i];
        j.BumpyShiny = TxiNamesBumpyShiny(txi);
        j.EnvMap = TxiNamesEnvMap(txi);
        uint* resId = stackalloc uint[ResLocate.ResIdWords];
        if (!LocateFor(resref, j.Kind, TxiIsProcedural(j.Arturo, j.GridX, j.GridY), resId, out j.Loc, out TexKind found, out string? why))
        { Recycle(j); _pend[i] = PendNone; Fail(i, why ?? "no resource"); return; }
        // Auto resolved to whatever the resource tables hold, and that is the slot's kind from here on: a PLT
        // takes the pair decode and the shader resolve, anything else the ordinary one. The slot KEY does not
        // carry the kind, so this cannot split one file across two slots.
        j.Kind = found;
        lock (_lock) { _kind[i] = found; TableSerial++; }
        _pend[i] = PendFlight;
        StartWorker();
        lock (_gate)
        {
            if (fast) { _fastPending++; _inFast.Enqueue(j); }
            else { _pending++; _in.Enqueue(j); }
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>The sampler words <c>CAurTextureBasic::ParseField</c> @0x5881e0 reads from the texture's own
    /// <c>.txi</c> -- <c>clamp</c> (+200), <c>mipmap</c> (+0xe4), <c>filter</c> (+0xe5) -- which
    /// <c>GLRender::SetTextureParameters</c> @0x5ca180 applies. No <c>.txi</c> keeps the ctor's 0/1/1.</summary>
    private static void TxiSampler(int i, string? txi)
    {
        if (txi == null) return;
        lock (_lock)
        {
            bool clamp = _clamp[i], nearest = _nearest[i], mip = _mipmap[i];
            TxiSamplerWords(txi, ref clamp, ref nearest, ref mip);
            _clamp[i] = clamp; _nearest[i] = nearest; _mipmap[i] = mip;
        }
    }

    /// <summary>The sampler words of one <c>.txi</c> text, applied over the state the slot already has: a word
    /// the text does not name leaves its flag where it was, as the engine's ctor defaults do.</summary>
    private static void TxiSamplerWords(string? txi, ref bool clamp, ref bool nearest, ref bool mip)
    {
        if (txi == null) return;
        foreach (string raw in txi.Split('\n'))
        {
            string line = raw.Trim();
            int sp = line.IndexOfAny([' ', '\t']);
            if (sp < 0 || !int.TryParse(line.AsSpan(sp + 1).Trim(), out int v)) continue;
            ReadOnlySpan<char> key = line.AsSpan(0, sp);
            // ponytail: one bool for both axes, so a one-axis clamp (1 or 2) clamps both; per-axis = a bits column.
            if (key.Equals("clamp", StringComparison.OrdinalIgnoreCase)) clamp = (v & 3) != 0;
            else if (key.Equals("filter", StringComparison.OrdinalIgnoreCase)) nearest = v == 0;
            else if (key.Equals("mipmap", StringComparison.OrdinalIgnoreCase)) mip = v != 0;
        }
    }

    /// <summary>Is there a PLT named <paramref name="resref"/>? <c>Material::ReplaceTexturePLT</c> @0x5c7a10's
    /// <c>CExoResMan::Exists(resref, 6)</c> gate: a coloured replace with no PLT changes nothing. Game thread.</summary>
    internal static bool PltExists(string resref)
    {
        Span<byte> name = stackalloc byte[ShaderResource.ResRefBytes + 1];
        TexRegistry.ResRef(resref, name);
        fixed (byte* p = name) return ResLocate.Find(p, PltResType, out _, out _);
    }

    /// <summary>Where this resref's bytes live, for the kind asked. A PLT slot takes the PLT restype alone --
    /// a PLT and a DDS of the same name are different images and resolving one as the other is how a head
    /// ends up wearing a wall. A DDS/TGA pair goes in the engine's race order, except that
    /// <paramref name="raw"/> puts the TGA first -- <see cref="TxiIsProcedural"/>.</summary>
    private static bool LocateFor(string resref, TexKind kind, bool raw, uint* resId, out TexLocation loc, out TexKind found, out string? why)
    {
        loc = default; why = null; found = TexKind.Plain;
        Span<byte> name = stackalloc byte[ShaderResource.ResRefBytes + 1];
        TexRegistry.ResRef(resref, name);
        Span<ushort> types = stackalloc ushort[3];
        int nTypes;
        fixed (byte* p = name)
        {
            if (kind == TexKind.Plt) { types[0] = PltResType; nTypes = 1; }
            else
            {
                bool tga = raw || TexLoad.Winner(p, out _, out _) == TgaResType;
                types[0] = tga ? TgaResType : DdsResType; types[1] = tga ? DdsResType : TgaResType; types[2] = PltResType;
                nTypes = kind == TexKind.Plain ? 2 : 3;
            }
            for (int t = 0; t < nTypes; t++)
            {
                ushort rt = types[t];
                if (!ResLocate.Find(p, rt, out IntPtr table, out IntPtr entry)) continue;
                if (ResLocate.Locate(table, entry, resId, out loc, out string? w, out ResLocate.Source src))
                { found = rt == PltResType ? TexKind.Plt : TexKind.Plain; return true; }
                why ??= w ?? $"restype {rt} is {src}";
            }
        }
        why ??= kind switch
        {
            TexKind.Plt => "no PLT resource of that name",
            TexKind.Plain => "no DDS or TGA resource of that name",
            _ => "no DDS, TGA or PLT resource of that name",
        };
        return false;
    }

    private static Job Rent()
    {
        lock (_gate) if (_pool.Count > 0) return _pool.Pop();
        return new Job();
    }

    private static void Recycle(Job j)
    {
        if (j.Data != null)
        {
            if (j.Tail) NativeMemory.Free(j.Data);
            else if (j.Kind == TexKind.Plain && j.Compressed) DdsReader.FreePrepared(j.Data);
            else NativeMemory.Free(j.Data);
            j.Data = null;
        }
        lock (_gate) _pool.Push(j);
    }

    /// <summary>Decode workers: a quarter of the cores, 1..4. Each keeps its own file handles, scratch and NWSync
    /// connection, and nothing else a worker touches is shared.</summary>
    private static int Workers => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    private static void StartWorker()
    {
        if (_worker) return;
        _worker = true;
        for (int w = 0; w < Workers; w++)
            new Thread(static () =>
            {
                while (true)
                {
                    Job j;
                    lock (_gate)
                    {
                        while (_inFast.Count == 0 && _in.Count == 0) Monitor.Wait(_gate);
                        j = _inFast.Count > 0 ? _inFast.Dequeue() : _in.Dequeue();
                    }
                    try { Work(j); }
                    catch (Exception e) { j.Why = $"{e.GetType().Name}: {e.Message}"; }
                    lock (_gate) (j.Fast ? _outFast : _out).Enqueue(j);
                }
            }) { IsBackground = true, Name = w == 0 ? "cx-tex" : $"cx-tex-{w}" }.Start();
    }

    /// <summary>The worker: read and decode. No GL, no engine memory -- everything it reads was copied for it
    /// at the offer, so nothing it holds can outlive a table the engine rebuilt. NWSync (kind 5) carries no
    /// container path -- its <see cref="TexLocation.Path"/> is the bare resref -- so it is read through
    /// <see cref="NwSyncReader"/> and not through a file handle.</summary>
    private static void Work(Job j)
    {
        if (j.Loc.Kind == KindNwSync) { NwSyncWork(j); return; }
        if (!(_handles ??= new Handles()).Read(j.Loc, out ReadOnlySpan<byte> raw, out string? why)) { j.Why = why ?? "the read failed"; return; }
        Decode(j, raw);
    }

    /// <summary>The NWSync read: <see cref="NwSyncReader"/> hands over a decompressed blob this thread owns,
    /// so the decode runs inside the lifetime and the blob is freed on every path out.</summary>
    private static void NwSyncWork(Job j)
    {
        if (!NwSyncReader.Armed) { j.Why = "NWSync is not armed, and the location has no container path"; return; }
        Span<byte> name = stackalloc byte[j.Loc.Path.Length];
        System.Text.Encoding.ASCII.GetBytes(j.Loc.Path, name);
        if (!NwSyncReader.TryRead(name, j.Loc.ResType, out byte* data, out int size))
        { j.Why = "NWSync has no row for it (counted on the nwsync note)"; return; }
        try { Decode(j, new ReadOnlySpan<byte>(data, size)); }
        finally { NativeMemory.Free(data); }
    }

    /// <summary>Decode the bytes the read produced, whichever container they came from.</summary>
    private static void Decode(Job j, ReadOnlySpan<byte> raw)
    {
        if (j.Fast && j.Kind != TexKind.Plt && j.Loc.ResType != TgaResType && j.GridX * j.GridY <= 1 && TryTail(j, raw)) return;
        if (j.Kind == TexKind.Plt)
        {
            byte* rg = PltImage.Decode(raw, out int w, out int h, out int levels, out string? pw);
            if (rg == null) { j.Why = pw ?? "not a PLT"; return; }
            j.Data = rg; j.W = w; j.H = h; j.Levels = levels;
            j.GlFmt = Gl.RG8; j.PixFmt = Gl.RG; j.PixType = Gl.UNSIGNED_BYTE;
            j.Compressed = false; j.GenMips = false;
            j.Bytes = PltImage.ChainBytes(w, h, levels);
            // A PLT carries no alpha of its own: the composite's alpha is the PALETTE texel's (ImageDecode
            // @0x4b6580 and fs_pltgen.shd copy the palette RGBA whole), and that channel is NOT coverage --
            // it is the engine's REFLECTIVITY mask (#121: fEnvMapLevel = 1 - alpha). The shipped palettes say
            // so outright: along pal_armor01's row 44 the alpha ramps DOWN from 219 to 40 as the intensity
            // rises, and measured on the stock male body 33% of pmh0_chest004's texels and 62% of
            // pmh0_legr015's resolve at or under the 0.2 discard -- Cutout punched a third of the armour away.
            // Vanilla does not test it: every PLT-wearing appearance (MODELTYPE P, all 23 rows) names an
            // ENVMAP, which gives its gob an env map, which makes Material::GetIsOpaque @0x4c6190 answer
            // opaque without asking the texture -- so the part draws in the dynamic OPAQUE bucket, where
            // Scene::RenderDynamicGeometry @0x46df80 leaves state 1 off and fAlphaDiscardValue is -1
            // (GLRender::SetUniforms @0x4ccbf0). Turn creature env mapping off and the engine bakes the same
            // reading in: ImageFillAlpha @0x4b8c30 writes rgb = mix(envGrey, rgb, a) and then a = 255.
            // Opaque is that verdict. (The sheen itself still waits on an env map -- DrawRecipe's ponytail marker.)
            // ...on an env-mapped gob. A gob the game gives NO env map (a cloakmodel.2da row whose ENVMAP is
            // ****, CNWCCreatureAppearance::CreateCloak @0x736c90) is asked HasAlpha, which a PLT always answers
            // yes, so there the palette alpha is coverage; that is per instance, so AlloyRenderer.DbClsOf says it.
            j.Cov = TexCoverage.Opaque;
            j.CovWhy = "PLT -> Opaque where its gob is env-mapped (palette alpha = reflectivity); a cutout on the "
                     + "palette alpha where it is not";
            return;
        }
        if (j.Loc.ResType == TgaResType)
        {
            byte* px = RasterReader.PrepareTga(raw, out RasterReader.Raster r, out string? tw);
            if (px == null) { j.Why = tw ?? "the TGA was refused"; return; }
            j.Data = px; j.W = r.W; j.H = r.H; j.Levels = Levels(r.W, r.H); j.Channels = r.Px;
            (j.GlFmt, j.PixFmt) = r.Px switch
            {
                1 => (Gl.R8, Gl.RED),
                3 => (Gl.RGB8, Gl.RGB_),
                _ => (Gl.RGBA8, Gl.RGBA_),
            };
            j.PixType = Gl.UNSIGNED_BYTE;
            j.Compressed = false; j.GenMips = true;
            j.Bytes = r.Bytes;
            j.Cov = TexCoverageOf.OfRaster(new ReadOnlySpan<byte>(px, r.Bytes), r.W, r.H, r.Px);
            j.CovWhy = TexCoverageOf.Why($"TGA, {r.Px} channel(s)", j.Cov);
            j.MeanOk = TexScan.MeanOfRaster(px, r.W, r.H, r.Px, out j.Mean);
            CutGrid(j);
            KeepArturo(j);
            return;
        }
        byte* chain = DdsReader.Prepare(raw, out DdsReader.Info info, out string? dw);
        if (chain == null) { j.Why = dw ?? "the DDS was refused"; return; }
        if (info.Faces != 1) { DdsReader.FreePrepared(chain); j.Why = $"{info.Faces} faces -- a cube map is not a registry texture"; return; }
        uint fmt = DdsReader.GlFormat(info.Format);
        if (fmt == 0) { DdsReader.FreePrepared(chain); j.Why = $"{info.Format} has no GL internal format"; return; }
        // Present, not Levels: CopyChain pads missing mips with one corner block, which a minified quad samples.
        j.Data = chain; j.W = info.W; j.H = info.H; j.Levels = info.Present;
        j.Channels = info.Channels; j.GlFmt = fmt; j.Compressed = true; j.GenMips = false;
        j.Bytes = info.ChainBytes;
        byte[] sc = _covScratch ?? Array.Empty<byte>();
        j.Cov = TexCoverageOf.OfBcn(new ReadOnlySpan<byte>(chain, DdsReader.LevelSize(info.W, info.H, info.Channels)),
                                    info.W, info.H, info.Format, ref sc);
        j.CovWhy = TexCoverageOf.Why($"DDS {info.Format}, {info.Channels} channel(s)", j.Cov);
        _covScratch = sc;
        if (info.Format is DdsReader.Fmt.Bc1 or DdsReader.Fmt.Bc3)
            j.MeanOk = TexScan.MeanOfDxt(chain, info.W, info.H, info.Format == DdsReader.Fmt.Bc3, out j.Mean);
    }

    /// <summary>The fast pass's preview: the DDS's own mips from the first one at or under <see cref="PreviewSide"/>
    /// down, copied straight out of the file -- no full-chain copy, no full-size coverage scan, which are most of a
    /// decode. False (the fast job then decodes the whole texture) for one that is already that small, one whose
    /// tail the file does not carry, or anything the full decode would refuse and name.</summary>
    private static bool TryTail(Job j, ReadOnlySpan<byte> raw)
    {
        if (!DdsReader.Parse(raw, out DdsReader.Info info, out _) || info.Faces != 1) return false;
        uint fmt = DdsReader.GlFormat(info.Format);
        if (fmt == 0 || !TailLevel(info.W, info.H, info.Channels, out int first, out int w0, out int h0, out long skip)) return false;
        if (first >= info.Present) return false;
        int levels = info.Present - first;
        long bytes = 0;
        for (int l = 0, w = w0, h = h0; l < levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            bytes += DdsReader.LevelSize(w, h, info.Channels);
        byte* data = (byte*)NativeMemory.Alloc((nuint)bytes);
        raw.Slice(info.DataOffset + (int)skip, (int)bytes).CopyTo(new Span<byte>(data, (int)bytes));
        j.Data = data; j.Tail = true; j.W = w0; j.H = h0; j.Levels = levels;
        j.Channels = info.Channels; j.GlFmt = fmt; j.Compressed = true; j.GenMips = false; j.Bytes = bytes;
        byte[] sc = _covScratch ?? Array.Empty<byte>();
        j.Cov = TexCoverageOf.OfBcn(new ReadOnlySpan<byte>(data, DdsReader.LevelSize(w0, h0, info.Channels)), w0, h0, info.Format, ref sc);
        j.CovWhy = TexCoverageOf.Why($"DDS {info.Format} preview mip {w0}x{h0}", j.Cov);
        _covScratch = sc;
        return true;
    }

    /// <summary>Where a <paramref name="w"/>x<paramref name="h"/> chain's preview starts: the level index, its size,
    /// and the bytes of the levels above it. False when level 0 is already within <see cref="PreviewSide"/>.</summary>
    internal static bool TailLevel(int w, int h, int channels, out int level, out int lw, out int lh, out long skip)
    {
        level = 0; lw = w; lh = h; skip = 0;
        if (Math.Max(w, h) <= PreviewSide) return false;
        while (Math.Max(lw, lh) > PreviewSide)
        {
            skip += DdsReader.LevelSize(lw, lh, channels);
            lw = Math.Max(1, lw >> 1); lh = Math.Max(1, lh >> 1); level++;
        }
        return true;
    }

    /// <summary>One small text resource (a <c>.txi</c>), read through the same location record and file
    /// handles a texture is. Game thread -- it reads the engine's resource tables -- and cold: the caller
    /// caches by name. Null when there is no such resource, which the caller reports.</summary>
    internal static string? ReadResourceText(string resref, ushort resType)
        => ReadResourceBytes(resref, resType, out _) is byte[] b ? System.Text.Encoding.ASCII.GetString(b) : null;

    /// <summary>The bytes behind <see cref="ReadResourceText"/>, from a BIF, a hak, a loose file or NWSync; null
    /// with <paramref name="why"/> set when there is none or it cannot be read (null <paramref name="why"/>: no
    /// such resource). Game thread, cold.</summary>
    internal static byte[]? ReadResourceBytes(string resref, ushort resType, out string? why)
    {
        why = null;
        if (!ResLocate.Ready && !ResLocate.Attach(out why)) return null;
        Span<byte> name = stackalloc byte[ShaderResource.ResRefBytes + 1];
        ResRef(resref, name);
        uint* resId = stackalloc uint[ResLocate.ResIdWords];
        TexLocation loc;
        fixed (byte* p = name)
        {
            if (!ResLocate.Find(p, resType, out IntPtr table, out IntPtr entry)) return null;
            if (!ResLocate.Locate(table, entry, resId, out loc, out why, out ResLocate.Source src))
            {
                why ??= src == ResLocate.Source.Resident ? "the engine holds it resident" : "cannot locate";
                return null;
            }
        }
        if (loc.Kind == KindNwSync)
        {
            if (!NwSyncReader.Armed) { why = "the NWSync reader is not armed"; return null; }
            Span<byte> res = stackalloc byte[loc.Path.Length];
            System.Text.Encoding.ASCII.GetBytes(loc.Path, res);
            if (!NwSyncReader.TryRead(res, loc.ResType, out byte* blob, out int size)) { why = "NWSync read failed"; return null; }
            try { return new ReadOnlySpan<byte>(blob, size).ToArray(); }
            finally { NativeMemory.Free(blob); }
        }
        // The worker's handles belong to the worker; a game-thread read takes its own, which is what the
        // engine's own CExoFile-per-resource did.
        using var h = new Handles();
        if (!h.Read(loc, out ReadOnlySpan<byte> raw, out why)) return null;
        return raw.ToArray();
    }

    /// <summary>Levels of the geometric chain down to 1x1.</summary>
    internal static int Levels(int w, int h)
    {
        int n = 1;
        while (w > 1 || h > 1) { w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); n++; }
        return n;
    }

    // ---- the bracket (GL only) ----

    /// <summary>Alloy's 4.6 bracket: delete what retired, upload what the worker finished and mint its handle.
    /// Nothing else may mint a bindless handle -- residency is per context -- and nothing else here touches
    /// the table or the engine: the result goes to <see cref="_landed"/> for <see cref="Tick"/> to file.</summary>
    internal static void Drain()
    {
        // FIRST, ahead of the deletes: a record still holding a dead handle must stop before anything this
        // bracket does can be followed by a draw. The colour frame enters this bracket before it binds a
        // target, so this is the frame's earliest point at which the renderer's tables may be touched.
        // Only names queued BEFORE the handle drain: a name's handle is queued ahead of it, so each of these
        // has gone non-resident before its delete.
        int graves;
        lock (_lock) graves = _graveyard.Count;
        ZeroDyingHandles();
        for (; graves > 0; graves--)
        {
            uint dead;
            lock (_lock) { if (_graveyard.Count == 0) break; dead = _graveyard.Dequeue(); }
            // The handle went non-resident above, but the frames in flight were submitted holding it.
            if (!Renderer.OnVulkan) GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, dead);   // a Vulkan name has no GL object
            VkTextures.Forget(dead);
        }
        long fastBytes = 0;
        for (int n = 0; n < FastUploadsPerFrame && fastBytes < FastBytesPerFrame; n++)
        {
            Job j;
            lock (_gate) { if (_outFast.Count == 0) break; j = _outFast.Dequeue(); _fastPending--; }
            fastBytes += j.Bytes;
            try { Finish(j); } finally { Recycle(j); }
        }
        for (int n = 0; n < UploadsPerFrame; n++)
        {
            Job j;
            lock (_gate) { if (_out.Count == 0) break; j = _out.Dequeue(); _pending--; }
            try { Finish(j); } finally { Recycle(j); }
        }
        ApplySamplerWishes();
        StepCycles();
        StepArturo();
        // ...and AGAIN: ApplySamplerWishes drops and re-mints handles of its own, and leaving those to the
        // next frame's drain is a whole frame of records pointing at a handle this one just retired.
        ZeroDyingHandles();
    }

    /// <summary>The sampler changes <see cref="RequestSampler"/> recorded. A handled texture is frozen, so the
    /// slot moves to a copy with the new state and a new handle; the old name retires through the graveyard.
    /// A slot that is not resident yet keeps its wish: the upload reads the same columns.</summary>
    private static void ApplySamplerWishes()
    {
        while (true)
        {
            int i;
            lock (_lock)
            {
                if (_samplerQ.Count == 0) break;
                i = _samplerQ.Dequeue();
                if (!_samplerWish[i] || _state[i] == TexState.Free) continue;
                if (_name[i] == 0) continue;       // not uploaded yet; Upload reads _clamp/_nearest/_mipmap itself
                _samplerWish[i] = false;
            }
            uint name;
            bool clamp, nearest, mip;
            lock (_lock) { name = _name[i]; clamp = _clamp[i]; nearest = _nearest[i]; mip = _mipmap[i]; }
            if (name == 0) continue;
            uint target = Renderer.OnVulkan ? VkTextures.NewName() : Gl.GetTextureHandleARB != null ? CloneLevels(name) : name;
            if (target == 0)
            {
                if (LoudOnce("clone")) _log?.Always($"TexRegistry: the sampler change on \"{_resref[i]}\" could not copy the texture -- it keeps the state it was minted with");
                continue;
            }
            if (!Renderer.OnVulkan)
            {
                int wrap = clamp ? Gl.CLAMP_TO_EDGE : Gl.REPEAT;
                Gl.TextureParameteri(target, Gl.TEXTURE_WRAP_S, wrap);
                Gl.TextureParameteri(target, Gl.TEXTURE_WRAP_T, wrap);
                Gl.TextureParameteri(target, Gl.TEXTURE_MAG_FILTER, nearest ? Gl.NEAREST : Gl.LINEAR);
                Gl.TextureParameteri(target, Gl.TEXTURE_MIN_FILTER,
                    !mip ? (nearest ? Gl.NEAREST : Gl.LINEAR)
                         : nearest ? Gl.NEAREST_MIPMAP_NEAREST : Gl.LINEAR_MIPMAP_LINEAR);
                Gl.TextureParameteri(target, Gl.TEXTURE_MAX_ANISOTROPY, mip && !nearest ? Aniso() : 1);
            }
            ulong fresh;
            lock (_lock)
            {
                DropHandle(i);
                if (target != name) { _graveyard.Enqueue(name); _name[i] = target; }
            }
            if (target != name) VkTextures.Clone(name, target, VkSampler(clamp, nearest, mip));
            fresh = MintHandle(target, _resref[i] ?? "");
            lock (_lock) { _handle[i] = fresh; _samplerDone++; TableSerial++; if (fresh != 0) _mints++; }
            lock (_gate) _reminted.Enqueue(i);
        }
    }

    private static float _anisoMax;

    /// <summary>The player's Game Options anisotropy, clamped to the driver's ceiling: the level
    /// <c>GLRender::SetTextureAnisotropicFiltering</c> @0x5ca100 gives every linear mipmapped texture.
    /// ponytail: taken at upload/mint; a level changed mid-area reaches textures loaded after it (the engine
    /// refreshes all on change), re-mint every slot if that matters.</summary>
    internal static int Aniso()
    {
        if (_anisoMax == 0f && Gl.GetFloatv != null) { float v; Gl.GetFloatv(Gl.MAX_TEXTURE_MAX_ANISOTROPY, &v); _anisoMax = MathF.Max(v, 1f); }
        return (int)Math.Clamp(StockConfig.Aniso, 1, (long)MathF.Max(_anisoMax, 1f));
    }

    /// <summary>One finished job, uploaded and published. In the bracket.</summary>
    private static void Finish(Job j)
    {
        int i = j.Slot;
        if ((uint)i >= MaxSlots) return;
        // Stale: the slot was evicted (or re-used) while the worker held the job, so this upload has no
        // subject. The generation is the whole test -- no address, no identity probe.
        bool stale;
        lock (_lock) stale = (_gen[i] & 0xFFF) != j.Gen || _state[i] == TexState.Free;
        if (stale) return;
        if (j.Why != null) { Publish(new Landed(i, j.Gen, 0, 0, 0, TexCoverage.Unknown, false, false, j.Why)); return; }
        uint name = Upload(j, out string? why);
        if (name == 0) { Publish(new Landed(i, j.Gen, 0, 0, 0, TexCoverage.Unknown, false, false, why ?? "the driver refused the upload")); return; }
        if (!j.Tail) lock (_lock) if ((_gen[i] & 0xFFF) == j.Gen) { _mean[i] = j.Mean; _meanOk[i] = j.MeanOk; }
        FileCycle(j);
        FileArturo(j);
        ulong handle = MintHandle(name, j.Resref);
        if (GlContextService.Debug)
            _log?.Always($"TexRegistry: [gldebug] minted \"{j.Resref}\" tex {name} fmt 0x{j.GlFmt:x} {j.W}x{j.H} levels {j.Levels} handle 0x{handle:x}");
        Publish(new Landed(i, j.Gen, name, handle, j.Bytes, j.Cov, j.BumpyShiny, j.EnvMap, null, j.Tail, j.CovWhy));
    }

    private static void Publish(in Landed l) { lock (_gate) _landed.Enqueue(l); }

    // ---- the game thread's half ----

    /// <summary>The game thread's frame hop: file what the bracket uploaded, offer what was waiting, and evict
    /// past the budget. Everything here either writes <see cref="SceneDb"/> or reads the engine's resource
    /// manager, so it belongs on this thread and nowhere else.</summary>
    internal static void Tick()
    {
        // The seven palettes, read where a resource read belongs: needed the first time a PLT record draws.
        // PltVerify loads them itself when Alloy is off, which is why this does not force it.
        if (Renderer.On && !PltPalettes.Loaded) PltPalettes.LoadDefaults();
        while (true)
        {
            int r;
            lock (_gate) { if (_reminted.Count == 0) break; r = _reminted.Dequeue(); }
            lock (_lock) MarkDependents(r);
        }
        while (true)
        {
            Landed l;
            lock (_gate) { if (_landed.Count == 0) break; l = _landed.Dequeue(); }
            if ((uint)l.Slot >= MaxSlots) continue;
            bool stale;
            lock (_lock) stale = (_gen[l.Slot] & 0xFFF) != l.Gen || _state[l.Slot] == TexState.Free;
            if (stale)
            {
                // The slot went while the upload was in flight: the name is ours and nothing references it.
                if (l.Name != 0) lock (_lock) _graveyard.Enqueue(l.Name);
                continue;
            }
            _pend[l.Slot] = PendNone;
            if (l.Why != null) Fail(l.Slot, l.Why);
            else if (l.Preview) { LandPreview(TexId.Make(l.Slot, l.Gen), l.Name, l.Handle, l.Cov); Enqueue(l.Slot); }
            else Land(TexId.Make(l.Slot, l.Gen), l.Name, l.Bytes, l.Handle, l.Cov, l.BumpyShiny, l.EnvMap, l.CovWhy);
        }
        while (true)
        {
            int i;
            lock (_gate) { if (_pending >= MaxPending || _waiting.Count == 0) break; i = _waiting.Dequeue(); }
            if (_pend[i] != PendWaiting) continue;   // released and re-acquired, or retired, while it waited
            _pend[i] = PendNone;
            Enqueue(i);
        }
        EvictPastBudget();
    }

    /// <summary>The upload: immutable storage for the whole chain, then one sub-image per level. Never
    /// respecify -- a mip read after respecification returned undefined pixels on Mesa/radeonsi, which is the
    /// bug behind every-mip-but-the-base-wrong.</summary>
    private static uint Upload(Job j, out string? why)
    {
        why = null;
        if (Renderer.OnVulkan)
        {
            uint n = VkTextures.NewName();
            MirrorJob(n, j);
            if (VkTextures.Has(n)) return n;
            why = $"Vulkan refused a {j.W}x{j.H} {j.Levels}-level 0x{j.GlFmt:x} image";
            return 0;
        }
        if (Gl.CreateTextures == null || Gl.TextureStorage2D == null) { why = "no GL 4.5 direct state access"; return 0; }
        uint dst; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &dst);
        if (dst == 0) { why = "glCreateTextures returned no name"; return 0; }
        int svUnpack; Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &svUnpack);
        if (svUnpack != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
        // GL's default row alignment is 4 bytes. An RG8 mip of odd width (a 2-byte row), an R8 or RGB8 level
        // whose width is not a multiple of 4, and every tail of a PLT chain all have rows that are not
        // 4-aligned: at the default the driver skips padding that is not there, which reads the next row's
        // pixels and runs off the end of the last one.
        int svAlign; Gl.GetIntegerv(Gl.UNPACK_ALIGNMENT, &svAlign);
        if (svAlign != 1) Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, 1);
        Gl.DrainErrors(8);
        Gl.TextureStorage2D(dst, j.Levels, j.GlFmt, j.W, j.H);
        Gl.TextureParameteri(dst, Gl.TEXTURE_MAX_LEVEL, j.Levels - 1);
        // The texture's own .txi clamp (TxiSampler) or a script's SetTextureAttributes.
        int wrap = j.Clamp ? Gl.CLAMP_TO_EDGE : Gl.REPEAT;
        Gl.TextureParameteri(dst, Gl.TEXTURE_WRAP_S, wrap);
        Gl.TextureParameteri(dst, Gl.TEXTURE_WRAP_T, wrap);
        // A PLT is sampled by texelFetch and resolved per layer; a filtered RG8 read would interpolate LAYER
        // INDICES, which is a different colour, not a blurrier one. Its own bilinear is in cx_plt.glsl, and a
        // .txi filter attribute cannot talk it out of nearest.
        bool nearest = j.Kind == TexKind.Plt || j.Nearest;
        bool mip = j.Kind == TexKind.Plt || j.Mip;
        Gl.TextureParameteri(dst, Gl.TEXTURE_MAG_FILTER, nearest ? Gl.NEAREST : Gl.LINEAR);
        Gl.TextureParameteri(dst, Gl.TEXTURE_MIN_FILTER,
            !mip ? (nearest ? Gl.NEAREST : Gl.LINEAR)
                 : nearest ? Gl.NEAREST_MIPMAP_NEAREST : Gl.LINEAR_MIPMAP_LINEAR);
        if (mip && !nearest) Gl.TextureParameteri(dst, Gl.TEXTURE_MAX_ANISOTROPY, Aniso());
        if (j.Channels == 1 && !j.Compressed)
        {
            Gl.TextureParameteri(dst, Gl.TEXTURE_SWIZZLE_G, (int)Gl.RED);
            Gl.TextureParameteri(dst, Gl.TEXTURE_SWIZZLE_B, (int)Gl.RED);
        }
        if (j.Compressed)
        {
            long off = 0;
            for (int l = 0, w = j.W, h = j.H; l < j.Levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            {
                int sz = DdsReader.LevelSize(w, h, j.Channels);
                fixed (byte* lvl = new Span<byte>(j.Data, checked((int)(off + sz))).Slice((int)off))
                    Gl.CompressedTextureSubImage2D(dst, l, 0, 0, w, h, j.GlFmt, sz, lvl);
                off += sz;
            }
        }
        else if (j.Kind == TexKind.Plt)
        {
            long off = 0;
            for (int l = 0, w = j.W, h = j.H; l < j.Levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            {
                fixed (byte* lvl = new Span<byte>(j.Data, checked((int)(off + (long)w * h * PltImage.BytesPerPixel))).Slice((int)off))
                    Gl.TextureSubImage2D(dst, l, 0, 0, w, h, j.PixFmt, j.PixType, lvl);
                off += (long)w * h * PltImage.BytesPerPixel;
            }
        }
        else
        {
            int svRow = 0;
            if (j.Pixels != null) { Gl.GetIntegerv(Gl.UNPACK_ROW_LENGTH, &svRow); Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, j.FullW); }
            Gl.TextureSubImage2D(dst, 0, 0, 0, j.W, j.H, j.PixFmt, j.PixType, j.Data);
            if (j.Pixels != null) Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, svRow);
            if (j.GenMips && Gl.GenerateTextureMipmap != null) Gl.GenerateTextureMipmap(dst);
        }
        uint err = Gl.GetError();
        if (svAlign != 1) Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, svAlign);
        if (svUnpack != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)svUnpack);
        if (err == 0) { MirrorJob(dst, j); return dst; }
        if (Gl.DeleteTextures != null) Gl.DeleteTextures(1, &dst);
        why = $"the driver refused a {j.W}x{j.H} {j.Levels}-level 0x{j.GlFmt:x} upload (0x{err:x})";
        return 0;
    }

    /// <summary>The <c>texcov:</c> note, once per load, for every texture the cutout test will NOT reach: an
    /// <see cref="TexCoverage.Opaque"/> or <see cref="TexCoverage.Unknown"/> verdict, or an env-mapped one
    /// whose alpha is reflectivity rather than coverage (as in vanilla, whose only <c>AlphaDiscard</c> under
    /// <c>ENVIRONMENT_MAP</c> runs before the diffuse is sampled). Grep it to name a texture drawing solid.</summary>
    private static void TexNote(TexId id, TexCoverage cov, bool envMap, string? covWhy)
    {
        if (cov is not (TexCoverage.Opaque or TexCoverage.Unknown) && !envMap) return;
        int i = id.Index;
        if ((uint)i >= MaxSlots) return;
        string env = envMap ? " -- env-mapped (.txi envmaptexture): its alpha is REFLECTIVITY, never coverage" : "";
        _log?.Info($"texcov: \"{_resref[i] ?? ""}\" {covWhy ?? $"no route recorded -> {cov}"}{env}");
    }

    /// <summary>A texture arrived: the slot takes its name and the handle the bracket minted for it, and every
    /// dependent part record is marked <see cref="Dirty.Texture"/>. Game thread, GL-free -- so the off-device
    /// check drives it with a synthetic name.</summary>
    internal static void Land(TexId id, uint name, long bytes, ulong handle = 0,
                              TexCoverage cov = TexCoverage.Unknown, bool bumpyShiny = false,
                              bool envMap = false, string? covWhy = null)
    {
        TexNote(id, cov, envMap, covWhy);
        lock (_lock)
        {
            if (!Valid(id)) return;
            int i = id.Index;
            if (_name[i] != 0)
            {
                if (!_preview[i]) { _graveyard.Enqueue(name); return; }   // a second upload for one slot: keep the first
                DropHandle(i);
                _graveyard.Enqueue(_name[i]);
                _name[i] = 0;
                _preview[i] = false;
            }
            // Released while it loaded: Release already filed it on the idle list, and filing it twice links it to itself.
            if (_state[i] == TexState.Idle) { IdleUnlink(i); _idleBytes -= _bytes[i]; }
            _cov[i] = cov;
            _bumpyShiny[i] = bumpyShiny;
            CoverageSerial++; TableSerial++;
            if (bumpyShiny) _bumpyShinySeen++;
            _envMap[i] = envMap;
            if (envMap) _envMapSeen++;
            if (cov == TexCoverage.Unknown) _covUnknown++;
            _name[i] = name;
            _handle[i] = handle;
            _bytes[i] = bytes;
            if (_refs[i] > 0) { _state[i] = TexState.Resident; _residentBytes += bytes; }
            else { _state[i] = TexState.Idle; _idleBytes += bytes; IdlePush(i); }
            if (_residentBytes + _idleBytes > _peakBytes) _peakBytes = _residentBytes + _idleBytes;
            if (handle != 0) _mints++;
            _uploads++;
            MarkDependents(i);
        }
    }

    /// <summary>A preview arrived: the slot draws it -- name, handle and the preview's coverage verdict, so a cutout
    /// leaf is cut out from the first frame -- while it stays <see cref="TexState.Loading"/>, and every part that
    /// was waiting is marked. They register again while it loads, so the full chain's <see cref="Land"/> reaches
    /// them too. Game thread, GL-free.</summary>
    internal static void LandPreview(TexId id, uint name, ulong handle, TexCoverage cov)
    {
        lock (_lock)
        {
            if (!Valid(id)) { _graveyard.Enqueue(name); DropValue(handle); return; }
            int i = id.Index;
            if (_name[i] != 0) { _graveyard.Enqueue(name); DropValue(handle); return; }   // the full chain beat it
            if (_refs[i] <= 0) { DropValue(handle); handle = 0; }   // idle: no handle, as Release leaves one
            _name[i] = name;
            _handle[i] = handle;
            _preview[i] = true;
            if (_cov[i] == TexCoverage.Unknown && cov != TexCoverage.Unknown) { _cov[i] = cov; CoverageSerial++; }
            TableSerial++;
            if (handle != 0) _mints++;
            _previews++;
            MarkDependents(i);
        }
    }

    /// <summary>A minted handle no slot keeps: queued for the bracket, which makes it non-resident and zeroes it
    /// out of the records. Caller holds <see cref="_lock"/>.</summary>
    private static void DropValue(ulong handle)
    {
        if (handle == 0) return;
        _dyingHandles.Enqueue(handle);
    }

    /// <summary>The load failed. Loud once per resref, counted after that, and the slot keeps the white
    /// stand-in as an explicit state -- never a quiet hole. Game thread.</summary>
    internal static void Fail(int i, string why)
    {
        string name; TexKind kind;
        lock (_lock)
        {
            _state[i] = TexState.Failed;
            _pend[i] = PendNone;
            TableSerial++;
            _failed++;
            name = _resref[i] ?? "?"; kind = _kind[i];
            MarkDependents(i);
        }
        if (LoudOnce($"tex:{name}"))
            _log?.Always($"TexRegistry: \"{name}\" ({kind}) did not load -- {why}. "
                + "For the rest of the session a diffuse slot naming it draws the WHITE STAND-IN, a map slot draws unbound (stock)");
    }

    /// <summary>A new immutable texture holding every level of <paramref name="src"/>, its swizzle and MAX_LEVEL
    /// carried, sampler state not: a texture a bindless handle was minted for refuses every later TexParameter,
    /// so a sampler change is a fresh name. 0 when the driver refused the copy (the error is drained).</summary>
    private static uint CloneLevels(uint src)
    {
        const int MaxLevels = 16;
        if (Gl.CopyImageSubData == null || Gl.GetTextureLevelParameteriv == null) return 0;
        int* lw = stackalloc int[MaxLevels], lh = stackalloc int[MaxLevels];
        int levels = 0, fmt = 0;
        for (; levels < MaxLevels; levels++)
        {
            int w = 0, h = 0;
            Gl.GetTextureLevelParameteriv(src, levels, Gl.TEXTURE_WIDTH, &w);
            Gl.GetTextureLevelParameteriv(src, levels, Gl.TEXTURE_HEIGHT, &h);
            if (w <= 0 || h <= 0) break;
            lw[levels] = w; lh[levels] = h;
        }
        Gl.GetTextureLevelParameteriv(src, 0, Gl.TEXTURE_INTERNAL_FORMAT, &fmt);
        if (levels == 0 || fmt == 0) return 0;
        Gl.DrainErrors(8);
        uint dst; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &dst);
        if (dst == 0) return 0;
        Gl.TextureStorage2D(dst, levels, (uint)fmt, lw[0], lh[0]);
        foreach (uint p in (ReadOnlySpan<uint>)[Gl.TEXTURE_SWIZZLE_R, Gl.TEXTURE_SWIZZLE_G, Gl.TEXTURE_SWIZZLE_B, Gl.TEXTURE_SWIZZLE_A, Gl.TEXTURE_MAX_LEVEL])
        {
            int v; Gl.GetTextureParameteriv(src, p, &v);
            Gl.TextureParameteri(dst, p, v);
        }
        for (int l = 0; l < levels; l++)
            Gl.CopyImageSubData(src, Gl.TEXTURE_2D, l, 0, 0, 0, dst, Gl.TEXTURE_2D, l, 0, 0, 0, lw[l], lh[l], 1);
        if (Gl.GetError() == 0) return dst;
        Gl.DeleteTextures(1, &dst);
        return 0;
    }

    // ---- handles ----

    /// <summary>A resident bindless handle for a freshly uploaded name. Minted in the BRACKET and nowhere
    /// else: a handle belongs to the share group and only Alloy's context is in it. A handled texture is
    /// immutable from that moment -- a change is a new slot, never a sub-image. 0 when the device mints
    /// nothing, which drops the slot's parts to the bound sampler path.</summary>
    private static ulong MintHandle(uint name, string resref)
    {
        if (name == 0) return 0;
        if (Renderer.OnVulkan) return VkTextures.HandleOf(name);
        if (Gl.GetTextureHandleARB == null || Gl.MakeTextureHandleResidentARB == null) return 0;
        ulong v = Gl.GetTextureHandleARB(name);
        if (v == 0)
        {
            if (LoudOnce("mint"))
                _log?.Always($"TexRegistry: glGetTextureHandleARB minted nothing for \"{resref}\" -- "
                    + "its parts fall to the bound sampler path");
            return 0;
        }
        Gl.MakeTextureHandleResidentARB(v);
        return v;
    }

    /// <summary>Release a slot's handle. Caller holds <see cref="_lock"/>, on any thread: no GL here, because
    /// residency is per context and the game thread has the ENGINE's current. The value is queued for
    /// <see cref="ZeroDyingHandles"/>, which makes it non-resident in Alloy's context and zeroes the records' copies.</summary>
    private static void DropHandle(int i)
    {
        if (_handle[i] == 0) return;
        _dyingHandles.Enqueue(_handle[i]);
        _handle[i] = 0;
        TableSerial++;
    }

    // ---- budget ----

    /// <summary>ADR 0023's budget, read the same way: <c>texcache_mb</c> -1 is a quarter of probed VRAM, 0
    /// evicts an idle slot as soon as it is idle.</summary>
    private static long BudgetBytes()
    {
        int mb = Renderer.TexCache.V;
        if (mb > 0) return (long)mb << 20;
        if (mb == 0) return 0;
        long vram = AlloyRenderer.VramTotalMib > 0 ? AlloyRenderer.VramTotalMib : AlloyRenderer.VramFreeMib;
        return vram > 0 ? (vram << 20) / 4 : 512L << 20;
    }

    /// <summary>Trim the idle set back to the budget, oldest first off the LRU head. Game thread; the GL
    /// names go to the graveyard for the bracket.</summary>
    private static void EvictPastBudget()
    {
        long budget = BudgetBytes();
        lock (_lock)
            while (_idleHead >= 0 && _residentBytes + _idleBytes > budget)
                Retire(_idleHead);
    }

    /// <summary>Terminal per session (<c>docs/owned-resource-lifetimes.md</c>): every GL name and handle this
    /// registry held is gone with the context, so they are FORGOTTEN rather than deleted, graveyard included.
    /// A slot something still references reloads on the next <see cref="Tick"/>; an idle one is retired, its
    /// whole value having been the name it no longer has. The slots keep their resrefs.</summary>
    internal static void ContextLost()
    {
        lock (_lock)
        {
            // Ahead of the clear: the ripples' own textures are forgotten here, so the retires below cannot
            // queue a dead context's names into a graveyard this frame has already emptied.
            ArturoContextLost();
            _graveyard.Clear();
            // Nothing to zero in the records either: the context that owned them is gone, and the renderer's
            // own context-loss path rebuilds its tables. The queue is DROPPED rather than drained, so a
            // handle value the next context mints cannot be matched by a stale entry from the old one.
            _dyingHandles.Clear();
            for (int i = 1; i < _high; i++)
            {
                if (_state[i] == TexState.Free) continue;
                _handle[i] = 0;
                _name[i] = 0;
                _preview[i] = false;
                TableSerial++;
                if (_refs[i] <= 0) { Retire(i); continue; }   // Retire owns the byte accounting; do not pre-subtract
                _residentBytes -= _bytes[i];
                _bytes[i] = 0;
                // _cov is NOT cleared: it is a property of the content, the reload derives the same verdict,
                // and clearing it would make every draw re-classify as blended for the frames in between.
                _state[i] = TexState.Loading;
                _pend[i] = PendWaiting;
                lock (_gate) _waiting.Enqueue(i);   // lock order is always _lock -> _gate, never the reverse
                _reloads++;
            }
            if (_residentBytes < 0) _residentBytes = 0;
            if (_idleBytes < 0) _idleBytes = 0;
        }
    }
}
