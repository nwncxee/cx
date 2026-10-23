using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The lightmap bake and its read. Every charted static instance gets a rectangle on a 4096x4096
/// page triple -- <b>A</b> irradiance (BC6H), <b>B</b> dominant direction (BC7), <b>C</b> mover shares -- baked page-major,
/// light-major: per (page, light) the shadow is rendered from the STATIC set into one scratch cube, then each
/// rect it reaches is drawn in UV space with the colour pass's own per-light body.</summary>
internal static unsafe partial class LightmapBake
{
    internal const int PageSize = 4096;
    internal const int MaxPages = 32;
    /// <summary>A rect's side is capped at a quarter page: the density scales down past it.</summary>
    private const int MaxRect = PageSize / 2;
    /// <summary>Texels between a rect and its cell's edge: the dilation reaches two, so two rects never bleed.</summary>
    private const int Gutter = 2;
    /// <summary>The compressed planes' block side: every cell, union and upload is whole blocks.</summary>
    internal const int Block = 4;
    /// <summary>Background: 4, not 2 -- a step is about 1 ms wall (the cube), and at 2 a 120-light page was 60
    /// frames of pacing for 0.02 ms of GPU per step, which the refinement waves paid per wave.</summary>
    private const int HoldLightsPerFrame = 32, BackgroundLightsPerFrame = 4;
    /// <summary>Frames the queue must stand still before a page's bake opens: rects enrol as parts do, a
    /// few a frame, and a page finished per trickle is a finish pass and a readback per frame.</summary>
    private const int SettleFrames = 30, HoldSettleFrames = 4;
    internal const uint BindChart = SsboSlots.Chart;
    internal const uint UnitA = 20, UnitB = 21, UnitC = 22;
    /// <summary>Bytes per page texel over the three planes: A (BC6H irradiance) and B (BC7 direction) at one
    /// each, C (RGBA8 mover shares, raw: a block would leak one channel's share into another's) at four.</summary>
    internal const int TexelBytes = 6;
    /// <summary>One compressed plane's layer, in bytes: 16 a 4x4 block.</summary>
    private const int LayerBlockBytes = PageSize * PageSize;
    /// <summary>The compressed planes' formats.</summary>
    private const uint FormatA = Gl.COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT, FormatB = Gl.COMPRESSED_RGBA_BPTC_UNORM;
    /// <summary>The encoder's work group side, in blocks (<c>cx_bakeenc_c</c>'s <c>local_size</c>).</summary>
    private const int EncGroup = 8;
    /// <summary>One resident page over its three planes, in MiB.</summary>
    private const int PageMiB = (int)((long)PageSize * PageSize * TexelBytes >> 20);
    private const int MaxRects = 1 << 16;
    private const int LogCap = 6;

    /// <summary>Raised under the loading screen; the bake then takes the hold's pacing.</summary>
    internal static bool Hold;

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("LightmapBake");
    /// <summary>The <c>LoadArea</c> chain did not take, so nothing can ever close the static set. Not a
    /// <see cref="StandDown"/>, which <c>ContextLost</c> forgets: a missing symbol does not come back.</summary>
    private static bool _noLoadEdge;
    internal static bool Enabled => Lightmap.G.Master is { V: true } && !_down.Failed && !_noLoadEdge;

    /// <summary>Per ownership slot: the rect it reads, -1 = never seen (enrol on sight), -2 = seen and refused.</summary>
    private static readonly int[] _slotRect = new int[AlloyRenderer.MaxOwned];
    /// <summary>The CxChartBuf mirror, per slot: (page, u0, v0, s) with page -1 for unbaked, then the pick --
    /// the four set indices page C's bytes belong to (-1 = none).</summary>
    internal const int RecFloats = 8;
    private static readonly float[] _rec = new float[AlloyRenderer.MaxOwned * RecFloats];
    private static int _dirtyLo = int.MaxValue, _dirtyHi = -1;
    /// <summary>The page, scratch and readback names and the flags that say what they hold, in one object:
    /// <see cref="ContextLost"/> replaces it.</summary>
    private sealed class BakeGlState
    {
        public uint BufChart;
        public bool BufFilled;
        public int Layers;
        /// <summary>The <c>lightmap.vram</c> value the pool was sized for: another one reallocates it.</summary>
        public int PoolMib;
        public uint TexA, TexB, TexC, ScrA, ScrB, ScrC, ScrZ, Fbo, Cube, Pbo;
        /// <summary>The encoder's output, one RGBA32UI texel per block of a page: what a cell copies from. BlkRead is
        /// the readback's own alias of a page's blocks, apart so a readback never lands in an encode in flight.</summary>
        public uint BlkA, BlkB, BlkRead;
        public uint ProgBake, ProgEnc;
        public bool Built;
        public IntPtr Fence;
        public int PboBytes;
        public GpuQuery QBake;
    }

    private static BakeGlState _gl = new();

    /// <summary>Depth steps the scratch depth holds: light l of a page writes (l + 1) / this under GREATER,
    /// so the pushed triangles (<see cref="BakeGs"/>) that overlap along a shared edge add a light ONCE -- the
    /// second fragment on the texel fails the test. The bake stays additive with no clear per light.</summary>
    private const int BakeDepthSteps = 1 << 16;
    private static int _cubeSize;
    private static int _bDepth;
    private static int _bSlot, _bRect, _bLight, _bColor, _bMode, _bCubeTex, _bDynTex, _bCubeCount, _bSoftWorld, _bChan;
    private static int _eRegion;

    /// <summary>The bake in flight: one page, its rect list and light list, a cursor over the lights.</summary>
    private static int _bakePage = -1, _bakeLight, _bakeSteps;
    private static readonly List<int> _bakeRects = new();
    private static readonly List<int> _bakeLights = new();
    /// <summary>Each page rect's pick, parallel to <see cref="_bakeRects"/>: which channel a light step writes.</summary>
    private static readonly List<Pick> _bakePicks = new();
    private static readonly List<int> _pendingRead = new();
    /// <summary>The readback's page rectangle: the union of the rects that landed, which is what the finish
    /// pass touched and what the PBO holds (one ReadPixels pair, not one per rect).</summary>
    private static int _ux0, _uy0, _uw, _uh;
    /// <summary>The page the readback in flight came off: the blob header carries it, so the area plan can
    /// upload the union whole. Captured with the rectangle -- the bake has moved on by the collect.</summary>
    private static int _uPage;
    /// <summary>Union readback buffers, a small ring: one is handed to the cache thread per landing and comes
    /// back when its last file is written (<see cref="BakeCache.Busy"/>). A landing with no free buffer goes
    /// uncached rather than allocate: a fresh page-sized buffer per landing was gigabytes of garbage a period
    /// and a swap storm.</summary>
    private const int ReadBufs = 4;
    private static readonly byte[][] _readBufs = new byte[ReadBufs][];
    private static long _pageT0;

    /// <summary>Period counters.</summary>
    private static int _landed, _landedHits, _pending, _refused, _pastCap, _bakeSkips, _uncached, _logLeft = LogCap;
    private static double _pageMs, _pageMsN;
    private static bool _rekeyPending;
    private static long _drewBaked, _drewNoRect, _drewRefused, _drewPending, _drewLandedUnbaked;
    private static int _frame, _queuedAt, _newAt;
    private static bool _repacked;
    /// <summary>Area-wide density halvings (0..<see cref="MaxAreaHalve"/>): an area whose rects would not fit the
    /// page cap at the knob's density is baked at a half, a quarter ... of it -- every rect, not the last ones
    /// packed (run 28: a 16k-part area landed 5.9k rects and refused 35k past the cap, viz 43 all magenta).
    /// Decided at Pack from the demand the settle window gathered; a later overflow bumps it and repacks.</summary>
    private static int _areaHalve;
    private const int MaxAreaHalve = 4;
    /// <summary>Shelf packing lands about this share of a page's texels.</summary>
    private const float PackShare = 0.8f;
    /// <summary>Texel area (with gutters) of every rect placed so far this area.</summary>
    private static long _placedArea;
    /// <summary>The per-area "known differences" line went out with this area's first page.</summary>
    private static bool _knownLogged;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        Array.Fill(_slotRect, -1);
        Array.Fill(_rec, -1f);
        _cache.Attach(log);
    }

    /// <summary>The GLSL the lit stage composes for its read: the record buffer by ownership slot, the
    /// page arrays and the decode, the buffer on <see cref="BindChart"/>; the sampler units are
    /// <see cref="UnitA"/>/<see cref="UnitB"/>. A content override that never declares the buffer simply never
    /// reads the lightmap.</summary>
    internal static string ChartBufDecl => ShaderSource.Get("cx_chartbuf.glsl");

    /// <summary>The record buffer alone, for a G-buffer reader: the twin read its draw's record there, and the
    /// reader fetches the same row by the slot the G-buffer keeps (cx_gbuf).</summary>
    internal static void BindChartBuffer() => Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindChart, _gl.BufChart);

    /// <summary>From the lit program bind: the record buffer, the pages and the two uniforms. Pages 0 when
    /// nothing is resident, so a record that somehow reads a page is ignored rather than sampled.</summary>
    internal static void BindForDraw(int locPages, int locMovers)
    {
        if (locPages < 0) return;
        bool live = Enabled && !FrameInputs.Blind && _gl.Layers > 0 && _gl.BufChart != 0 && _gl.BufFilled;
        // a shared point: left alone, the stage's unconditional uChart read lands in a compute pass's scratch
        Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, BindChart, _gl.BufChart);
        if (live)
        {
            Gl.ActiveTexture(Gl.TEXTURE0 + UnitA); Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _gl.TexA);
            Gl.ActiveTexture(Gl.TEXTURE0 + UnitB); Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _gl.TexB);
            Gl.ActiveTexture(Gl.TEXTURE0 + UnitC); Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _gl.TexC);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }
        Gl.Uniform1i(locPages, live ? _gl.Layers : 0);
        if (locMovers >= 0) Gl.Uniform1i(locMovers, MoverMode);
    }

    /// <summary>The light walk's <c>uLmMovers</c>: 0 = a baked receiver skips static lights outright, 1 = a hot
    /// one takes its mover's shadow back out of the baked term, 2 = the ray test alone (the
    /// <c>lightmap.movertaps</c> diagnostic). The deferred pass reads the lanes off the G-buffer and binds no
    /// pages, so it needs this one number without the rest of <see cref="BindForDraw"/>.</summary>
    internal static int MoverMode => !Lightmap.Movers.V ? 0 : Lightmap.MoverTaps.V ? 1 : 2;

    /// <summary>The staging loop's one call per charted draw: a slot the bake has not seen enrols -- its key
    /// is computed against the settled static set and it either shares a landed rect, joins a rect's queue or
    /// takes a new one. Before the set settles nothing enrols (the slot is re-offered next frame).</summary>
    internal static void Enrol(int i, int vao, int item)
    {
        // the per-frame census of what viz 43 shows: a charted draw is baked, or unbaked for one of four reasons
        if (_rec[i * RecFloats] >= 0f) _drewBaked++;
        else if (_slotRect[i] == -1) _drewNoRect++;
        else if (_slotRect[i] == -2) _drewRefused++;
        else if (_rects[_slotRect[i]].State == RState.Landed) _drewLandedUnbaked++;
        else _drewPending++;
        // a plan answer still due: a rect minted now refuses the whole plan (the sweep picks the slot up after)
        if (_slotRect[i] != -1 || !Enabled || SetHash == 0 || AreaAwaited) return;
        Enrol(i, vao, item, Baseline);
    }

    /// <summary>Enrol at a tier: the baseline from the sweep and the staging loop, a finer one from
    /// <see cref="RefineSweep"/>. The slot's record is left as it is -- a refining slot keeps reading its baseline
    /// rect until the fine one lands, and a slot falling back to its landed baseline takes it at once.</summary>
    private static void Enrol(int i, int vao, int item, int tier)
    {
        if (!MeshChart.Atlas(vao, item, out int w, out int h, out ulong content, out Vector3 bmin, out Vector3 bmax)) return;
        if (!AlloyRenderer.SlotPlacement(i, out _, out _, out Matrix4x4 model)) return;
        // the instance's world sphere off the CHART's bounds: the engine's part bbox is unpopulated on tile
        // parts (min == max), and a light reaches what the mesh actually spans
        float sx = new Vector3(model.M11, model.M12, model.M13).Length();
        Vector3 c = Vector3.Transform((bmin + bmax) * 0.5f, model);
        float r = (bmax - bmin).Length() * 0.5f * MathF.Max(sx, 1e-3f);
        if (_win.On && !InWindow(c, r, _win.C, Lightmap.Window.V)) return;
        int halved = tier + _areaHalve;
        int density = RectDensity(Lightmap.Density.V, FrameInputs.Interior, AlloyRenderer.TileSlot(i));
        float s = density / MeshChart.AtlasTexelsPerUnit * MathF.Max(sx, 1e-3f) / (1 << halved);
        int rw = (int)MathF.Ceiling(w * s), rh = (int)MathF.Ceiling(h * s);
        if (rw > MaxRect || rh > MaxRect) { s *= (float)MaxRect / Math.Max(rw, rh); rw = (int)MathF.Ceiling(w * s); rh = (int)MathF.Ceiling(h * s); }
        if (rw <= 0 || rh <= 0) { _slotRect[i] = -2; return; }
        ulong key0 = BakeKey(content, model, c, r, density), key = Halve(key0, halved);
        LogKeySample(content, model, c, r, key);
        if (_byKey.TryGetValue(key, out int ri))
        {
            _slotRect[i] = ri;
            (_owners[ri] ??= new List<int>(2)).Add(i);
            if (_rects[ri].Slot < 0) _rects[ri].Slot = i;
            if (_rects[ri].State == RState.Landed) WriteRecord(i, ri);
            if (_rects[ri].Adopted && !_rects[ri].Claimed) Claimed(ri);
            Enrolled(minted: false);   // CX-54: a standing rect is not an arrival -- see Enrolled
            return;
        }
        if (_free.Count == 0 && _rectN == MaxRects) { _slotRect[i] = -2; _refused++; return; }
        ri = NewRect();
        _rects[ri] = new Rect { Key = key, Key0 = key0, Slot = i, Vao = vao, Item = item, Page = -1, W = rw, H = rh, S = s, C = c, R = r, State = RState.Packing, Content = content, Halved = halved, Tier = tier };
        (_owners[ri] ??= new List<int>(2)).Clear();
        _owners[ri]!.Add(i);
        _byKey[key] = ri;
        _slotRect[i] = ri;
        _toPack.Add(ri); _newAt = _frame; if (_areaAdopted) _areaNew++;
        Enrolled(minted: true);
    }

    /// <summary>Diagnostic: does this slot have a baked lightmap record? A class-1 part with none is
    /// lit by nothing if the live loop also passes it over, which is what a black tile looks like.</summary>
    internal static bool HasRecordForSelfCheck(int i)
        => (uint)i < (uint)_slotRect.Length && _rec[i * RecFloats] >= 0f;

    /// <summary>A slot was vacated (or the table wiped): its record reads unbaked from the next upload.</summary>
    internal static void Vacate(int i)
    {
        if ((uint)i >= (uint)_slotRect.Length) return;
        int ri = _slotRect[i];
        _slotRect[i] = -1;
        if (ri >= 0) Disown(ri, i);
        if (_rec[i * RecFloats] >= 0f) SetRecord(i, -1f, 0f, 0f, 0f, Pick.None);
    }

    /// <summary>Retire a rect that cannot be drawn: its readers forget it and enrol again next frame.</summary>
    private static void Drop(int ri, bool queued = true)
    {
        if (queued) _pending--; _refused++;
        var o = _owners[ri];
        if (o != null) { for (int k = 0; k < o.Count; k++) _slotRect[o[k]] = -1; o.Clear(); }
        Kill(ri);
    }

    /// <summary>A rect is dead: unkeyed, unowned, its entry free. The owners are the caller's to re-point.</summary>
    private static void Kill(int ri)
    {
        ref Rect r = ref _rects[ri];
        r.State = RState.PastCap; r.Slot = -1;
        _byKey.Remove(r.Key);
        _free.Push(ri);
    }

    private static void Disown(int ri, int i)
    {
        var o = _owners[ri];
        if (o != null) o.Remove(i);
        if (_rects[ri].Slot == i) _rects[ri].Slot = o != null && o.Count > 0 ? o[0] : -1;
    }

    /// <summary>The bake key: the chart, the density, the placement at 1 cm / 1e-3, the touching static lights
    /// and the shadow look. The placement is the record's matrix AND the world centre: a tile part's vertices
    /// sit in world space under an identity matrix, so without the centre every placement of one tile model
    /// would share a bake as well as a chart.</summary>
    private static ulong BakeKey(ulong content, in Matrix4x4 m, Vector3 c, float r, int density)
    {
        ulong h = Mix(FnvOffset, (long)content);
        h = Mix(h, density);
        h = Mix(h, Q(c.X, 0.01f)); h = Mix(h, Q(c.Y, 0.01f)); h = Mix(h, Q(c.Z, 0.01f));
        h = Mix(h, Q(m.M41, 0.01f)); h = Mix(h, Q(m.M42, 0.01f)); h = Mix(h, Q(m.M43, 0.01f));
        h = Mix(h, Q(m.M11, 1e-3f)); h = Mix(h, Q(m.M12, 1e-3f)); h = Mix(h, Q(m.M13, 1e-3f));
        h = Mix(h, Q(m.M21, 1e-3f)); h = Mix(h, Q(m.M22, 1e-3f)); h = Mix(h, Q(m.M23, 1e-3f));
        h = Mix(h, Q(m.M31, 1e-3f)); h = Mix(h, Q(m.M32, 1e-3f)); h = Mix(h, Q(m.M33, 1e-3f));
        h = Mix(h, (long)TouchingLights(c, r));
        return Mix(h, (long)ShadowLook());
    }

    /// <summary>Every dial the bake stage reads: a change to any of them is a different look, so a different key.</summary>
    private static ulong ShadowLook()
    {
        ulong h = FnvOffset;
        h = Mix(h, CasterPass.CubeFaceSize);
        // the floor KNOB, not AlloyLights.ShadowFloor: that is the knob scaled by the area ambient, which
        // drifts -- keyed at 1e-3 it moved every session's keys off the last one's files (0 of 7426 hit, run 7)
        h = Mix(h, Q(AlloyLights.ShadowSoft, 0.01f)); h = Mix(h, Q(Shadows.Floor.V, 1e-3f));
        h = Mix(h, Q(AlloyLights.ShadowNearWorld, 0.01f));
        Vector2 at = AlloyLights.Atten;
        h = Mix(h, Q(at.X, 1e-3f)); h = Mix(h, Q(at.Y, 1e-3f));
        h = Mix(h, Q(Lighting.Gain.V, 1e-3f)); h = Mix(h, Q(Lighting.Radius.V, 1e-3f)); h = Mix(h, Q(Lighting.Falloff.V, 1e-3f));
        h = Mix(h, Q(Lighting.Hot.V, 1e-3f)); h = Mix(h, Q(Lighting.HotSize.V, 1e-3f));
        h = Mix(h, Q(Lighting.Wrap.V, 1e-3f)); h = Mix(h, Q(Lighting.WrapNdl.V, 1e-3f)); h = Mix(h, Q(Lighting.Sss.V, 1e-3f));
        h = Mix(h, Q(AlloyLights.ReachUniform, 1e-3f));
        return h;
    }

    private static void SetRecord(int i, float page, float x, float y, float s, Pick pick)
    {
        int o = i * RecFloats;
        _rec[o] = page; _rec[o + 1] = x; _rec[o + 2] = y; _rec[o + 3] = s;
        _rec[o + 4] = pick.L0; _rec[o + 5] = pick.L1; _rec[o + 6] = pick.L2; _rec[o + 7] = pick.L3;
        if (i < _dirtyLo) _dirtyLo = i;
        if (i > _dirtyHi) _dirtyHi = i;
    }

    private static void WriteRecord(int i, int ri)
    {
        ref Rect r = ref _rects[ri];
        SetRecord(i, r.Page, r.X, r.Y, r.S, PickOf(r.C, r.R));
    }

    /// <summary>A rect's four mover lights (<see cref="PickLights"/>), by value.</summary>
    private struct Pick
    {
        internal int L0, L1, L2, L3;
        internal static readonly Pick None = new() { L0 = -1, L1 = -1, L2 = -1, L3 = -1 };
        internal readonly int ChannelOf(int l) => l == L0 ? 0 : l == L1 ? 1 : l == L2 ? 2 : l == L3 ? 3 : Channels;
    }

    private static Pick PickOf(Vector3 c, float r)
    {
        Span<int> p = stackalloc int[Channels];
        PickLights(c, r, p);
        return new Pick { L0 = p[0], L1 = p[1], L2 = p[2], L3 = p[3] };
    }

    /// <summary>A rect landed: every slot reading it takes the page.</summary>
    private static void Land(int ri, bool hit)
    {
        _rects[ri].State = RState.Landed;
        _landed++; if (hit) _landedHits++;
        if (Held && _firstLandT == 0) _firstLandT = Stopwatch.GetTimestamp();
        var o = _owners[ri];
        if (o != null) for (int k = 0; k < o.Count; k++) WriteRecord(o[k], ri);
    }

    private static void UploadRecords()
    {
        if (_gl.BufChart == 0)
        {
            uint b; Gl.CreateBuffers(1, &b); if (b == 0) return;
            _gl.BufChart = b; _gl.BufFilled = false;
        }
        fixed (float* p = _rec)
        {
            if (!_gl.BufFilled) { Gl.NamedBufferData(_gl.BufChart, (IntPtr)((long)_rec.Length * 4), p, Gl.DYNAMIC_DRAW); _gl.BufFilled = true; }
            else if (_dirtyHi >= _dirtyLo)
                fixed (float* q = _rec.AsSpan((int)((long)_dirtyLo * RecFloats)))
                Gl.NamedBufferSubData(_gl.BufChart, (IntPtr)((long)_dirtyLo * RecFloats * 4), (IntPtr)((long)(_dirtyHi - _dirtyLo + 1) * RecFloats * 4), q);
        }
        _dirtyLo = int.MaxValue; _dirtyHi = -1;
    }

    /// <summary>One fenced PBO readback of a block-aligned page region and the rects it holds:
    /// <paramref name="rects"/> becomes the blob's table at the collect. A page bake reads what it just encoded;
    /// <see cref="AreaSnap"/> reads a resident page back whole, off the same one-at-a-time fence.</summary>
    private static void QueueReadback(int page, int x0, int y0, int w, int h, List<int> rects)
    {
        int bytes = w * h * TexelBytes, plane = w * h;
        if (bytes <= 0 || rects.Count == 0) return;
        if (_gl.Pbo == 0) { uint b; Gl.CreateBuffers(1, &b); _gl.Pbo = b; if (b == 0) return; }
        if (bytes > _gl.PboBytes) { Gl.NamedBufferData(_gl.Pbo, (IntPtr)bytes, null, Gl.STREAM_READ); _gl.PboBytes = bytes; }
        Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, _gl.Pbo);
        // the blocks leave through an uncompressed alias: NVIDIA's compressed read of a page that has taken a
        // CopyImageSubData stalls the thread ~94 ms whatever the region. A one-texel-per-block copy is the same bits.
        ReadBlocks(_gl.TexA, page, x0, y0, w, h, 0);
        ReadBlocks(_gl.TexB, page, x0, y0, w, h, plane);
        Gl.GetTextureSubImage(_gl.TexC, 0, x0, y0, page, w, h, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, plane * 4, (void*)(2L * plane));
        Gl.BindBuffer(Gl.PIXEL_PACK_BUFFER, 0);
        _ux0 = x0; _uy0 = y0; _uw = w; _uh = h; _uPage = page;
        _pendingRead.Clear(); _pendingRead.AddRange(rects);
        _gl.Fence = Gl.FenceSync(Gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
    }

    /// <summary>One plane's blocks of a page region into the bound pack buffer at <paramref name="at"/>, through
    /// <see cref="BakeGlState.BlkRead"/>: GL orders the second plane's copy after the first one's read.</summary>
    private static void ReadBlocks(uint tex, int page, int x0, int y0, int w, int h, int at)
    {
        Gl.CopyImageSubData(tex, Gl.TEXTURE_2D_ARRAY, 0, x0, y0, page, _gl.BlkRead, Gl.TEXTURE_2D, 0, 0, 0, 0, w, h, 1);
        Gl.GetTextureSubImage(_gl.BlkRead, 0, 0, 0, 0, w / Block, h / Block, 1, Gl.RGBA_INTEGER, Gl.UNSIGNED_INT, w * h, (void*)(long)at);
    }

    /// <summary>Bytes copied out of the mapped PBO per frame. A host read out of the mapped PBO is
    /// uncached/write-combined memory at 1-3 GB/s, so this is deliberately small -- 4 MiB is 2-4 ms, the
    /// worst-frame clause's own floor -- and the map stays open across frames instead of one memcpy a page.
    /// A whole-page snapshot then takes tens of frames, which costs nothing: it is once per area.</summary>
    private const long ReadCopyPerFrame = 4L << 20, HoldReadCopyPerFrame = 48L << 20;
    /// <summary>The PBO's mapping while a copy is in flight, the ring slot it feeds and how far it has got.
    /// Non-null = the PBO is mapped, so nothing may queue another readback into it.</summary>
    private static byte* _uMap;
    private static long _uDone;
    private static int _uBuf;
    /// <summary>No readback is in flight at all: no fence pending, no copy still draining the map, and no
    /// landing stashed behind one.</summary>
    private static bool ReadbackIdle => _gl.Fence == IntPtr.Zero && _uMap == null && _defW == 0;

    /// <summary>A page landed while a readback was still draining: its region waits here and is queued the
    /// moment the PBO is free, so the slicing never turns a landing into a cache miss. One slot -- a second
    /// landing inside one window drops the older stash, counted uncached as it would have been before.</summary>
    private static int _defPage, _defX0, _defY0, _defW, _defH;
    private static readonly List<int> _defRects = new();

    private static void StashReadback(int page, int x0, int y0, int w, int h, List<int> rects)
    {
        if (_defW > 0) _uncached += _defRects.Count;
        _defPage = page; _defX0 = x0; _defY0 = y0; _defW = w; _defH = h;
        _defRects.Clear(); _defRects.AddRange(rects);
    }

    /// <summary>Drop the stashed rects that no longer describe the pixels the stash would read -- one the pack
    /// has killed, re-landed or moved to another page -- and count them uncached; returns what is left.
    /// Without this a blob carries the NEW tenant's key over the OLD tenant's pixels, which the next session
    /// adopts as a bake: a permanently wrong-lit surface. <see cref="EvictPage"/> is the live path.</summary>
    private static int StashValid()
    {
        for (int k = _defRects.Count - 1; k >= 0; k--)
        {
            int ri = _defRects[k];
            if ((uint)ri < (uint)_rectN && _rects[ri].Page == _defPage && _rects[ri].State == RState.Landed) continue;
            _defRects.RemoveAt(k); _uncached++;
        }
        return _defRects.Count;
    }

    /// <summary>Queue the stashed landing now the PBO is free, of the rects that still hold their place on the
    /// page (<see cref="StashValid"/>).</summary>
    private static void IssueStashed()
    {
        if (_defW == 0 || _gl.Fence != IntPtr.Zero || _uMap != null) return;
        if (StashValid() == 0) { _defW = 0; return; }
        QueueReadback(_defPage, _defX0, _defY0, _defW, _defH, _defRects);
        if (_gl.Fence == IntPtr.Zero) _uncached += _defRects.Count;
        _defW = 0; _defRects.Clear();
    }

    /// <summary>A fenced readback that has completed is mapped once and copied out over as many frames as its
    /// slice budget takes, then sliced per rect and handed to the cache thread. Polled, never waited on.</summary>
    private static void CollectReadback()
    {
        if (_uMap != null) { CopySlice(); return; }
        if (_gl.Fence == IntPtr.Zero) return;
        uint st = Gl.ClientWaitSync(_gl.Fence, 0, 0);
        if (st != Gl.ALREADY_SIGNALED && st != Gl.CONDITION_SATISFIED) return;
        Gl.DeleteSync(_gl.Fence); _gl.Fence = IntPtr.Zero;
        long total = (long)_uw * _uh * TexelBytes;
        int b = -1;
        for (int i = 0; i < ReadBufs && b < 0; i++) if (_cache.Busy[i] == 0) b = i;
        if (b < 0) { _uncached += _pendingRead.Count; _pendingRead.Clear(); _uSnap = false; return; }
        byte* p = (byte*)Gl.MapNamedBufferRange(_gl.Pbo, IntPtr.Zero, (IntPtr)total, Gl.MAP_READ_BIT);
        if (p == null) { _uncached += _pendingRead.Count; _pendingRead.Clear(); _uSnap = false; return; }
        // uninitialized and pinned: the zeroing of a 96 MiB array is pure cost when every byte is about to be
        // overwritten, and a buffer this size handed to the cache thread has no business being relocatable
        // uninitialized: zeroing an array whose every byte is about to be overwritten is pure cost. NOT pinned
        // -- these grow to 96 MiB and the pinned heap is never compacted, so four of them growing in place
        // would leave holes there for the session.
        if (_readBufs[b] == null || _readBufs[b]!.Length < total)
            _readBufs[b] = GC.AllocateUninitializedArray<byte>((int)Math.Max(total, 1 << 20));
        _uMap = p; _uDone = 0; _uBuf = b;
        CopySlice();
    }

    /// <summary>Pure: how far a sliced copy stands after one slice -- never past the total.</summary>
    private static long SliceEnd(long done, long total, long per) => Math.Min(done + per, total);

    /// <summary>One frame's share of a copy: the budget, whatever the union's size. Under the hold the backdrop
    /// hides a far bigger one.</summary>
    private static long CopyPer(bool hold) => hold ? HoldReadCopyPerFrame : ReadCopyPerFrame;

    /// <summary>One frame's slice of the open map's copy; the last one unmaps and files the blob.</summary>
    private static void CopySlice()
    {
        long total = (long)_uw * _uh * TexelBytes;
        long end = SliceEnd(_uDone, total, CopyPer(Hold));
        new ReadOnlySpan<byte>(_uMap, (int)total)[(int)_uDone..(int)end].CopyTo(_readBufs[_uBuf].AsSpan((int)_uDone));
        _uDone = end;
        if (_uDone < total) return;
        if (Gl.UnmapNamedBuffer != null) Gl.UnmapNamedBuffer(_gl.Pbo);
        _uMap = null;
        var table = new BakeCache.Entry[_pendingRead.Count];   // cold: one per page bake
        for (int k = 0; k < _pendingRead.Count; k++)
        {
            ref Rect r = ref _rects[_pendingRead[k]];
            table[k] = new BakeCache.Entry { Key = r.Key, X = r.X - _ux0, Y = r.Y - _uy0, W = r.W, H = r.H };
            r.Unioned = true;
        }
        AreaUnionBaked(_cache.Save(_readBufs[_uBuf]!, _uBuf, _uPage, _ux0, _uy0, _uw, _uh, table), _uPage, _ux0, _uy0, _uw, _uh);
        _pendingRead.Clear(); _uSnap = false;
    }

    /// <summary>The map and the stash go with the pages: a copy in flight and a landing waiting behind it are
    /// both abandoned, and their rects go uncached.</summary>
    private static void AbortReadback(bool gl)
    {
        if (_defW > 0) { _uncached += _defRects.Count; _defW = 0; _defRects.Clear(); }
        if (_uMap == null) return;
        if (gl && Gl.UnmapNamedBuffer != null) Gl.UnmapNamedBuffer(_gl.Pbo);
        _uMap = null; _uDone = 0; _uncached += _pendingRead.Count;
    }

    /// <summary>Once per area: every term of the key, so two sessions' logs say which one moved when the
    /// second misses the first's files. The rect sampled is the LOWEST key of the area, not the first one
    /// enrolled -- enrolment order moves with a streamed texture, so two runs sampled different rects and the
    /// A/B could not tell a moved key term from a moved sample. Held until the hold's close.</summary>
    private static bool _keyReported;
    private static bool _keyHave;
    private static ulong _keyContent, _keyKey;
    private static Matrix4x4 _keyM;
    private static Vector3 _keyC;
    private static float _keyR;

    /// <summary>Whether <paramref name="key"/> replaces the area's sample. Pure: first one in, then strictly
    /// lower keys.</summary>
    private static bool KeySampleBeats(bool have, ulong held, ulong key) => !have || key < held;

    private static void LogKeySample(ulong content, in Matrix4x4 m, Vector3 c, float r, ulong key)
    {
        if (_keyReported || !KeySampleBeats(_keyHave, _keyKey, key)) return;
        _keyHave = true;
        _keyContent = content; _keyM = m; _keyC = c; _keyR = r; _keyKey = key;
    }

    /// <summary>Emit the area's key sample, once. Called at the hold's close and again on the world edge, so a
    /// run with the hold off still writes the line.</summary>
    private static void FlushKeySample()
    {
        if (_keyReported || !_keyHave) return;
        _keyReported = true;
        Vector3 c = _keyC; float r = _keyR;
        int n = 0; for (int i = 0; i < _setN; i++) if (Touches(i, c, r)) n++;
        Vector2 at = AlloyLights.Atten;
        _log?.Info($"LightmapBake: key sample (lowest key of the area) -- content {_keyContent:x16} at ({c.X:F2},{c.Y:F2},{c.Z:F2}) r {r:F2} m41 ({_keyM.M41:F2},{_keyM.M42:F2},{_keyM.M43:F2}) density {Lightmap.Density.V}"
                   + $" touching {n} of {_setN} static lights = {TouchingLights(c, r):x16}, look {ShadowLook():x16} (cube {CasterPass.CubeFaceSize}, soft {AlloyLights.ShadowSoft:F3}, floor {AlloyLights.ShadowFloor:F4}, near {AlloyLights.ShadowNearWorld:F3}, atten {at.X:F4}/{at.Y:F4}, reach {AlloyLights.ReachUniform:F3})"
                   + $" -> key {_keyKey:x16}");
    }

    /// <summary>Cache answers: a hit's cell is uploaded straight out of its blob's union (C row-strided, A and B
    /// a block row a call) into the resident pages at the packed offset and lands; a miss queues the rect.
    /// Hits uploaded per frame, in bytes: 1282 re-keyed rects answered in one frame was a 795 ms frame of
    /// TexSubImage3D (run 21). Under the hold the backdrop hides the hitch, so a page lands in four frames.</summary>
    private const long AdoptBytesPerFrame = 8L << 20, HoldAdoptBytesPerFrame = 32L << 20;
    private static void Adopt()
    {
        long uploaded = 0, budget = Hold ? HoldAdoptBytesPerFrame : AdoptBytesPerFrame;
        long t0 = Held ? Stopwatch.GetTimestamp() : 0;
        int adopted = 0;
        bool strided = false;
        while (uploaded < budget && _cache.TryTake(out int ri, out ulong key, out byte[]? data, out int x, out int y, out int uw, out int uh))
        {
            StampProbe();
            // the key guards a stale answer: a Revoke between the probe and the answer re-numbers the rects
            if (ri >= _rectN || _rects[ri].State != RState.Probing || _rects[ri].Key != key) continue;
            ref Rect r = ref _rects[ri];
            if (data == null) { r.State = RState.Queued; _queuedAt = _frame; continue; }
            CellOf(r, out int cx, out int cy, out int cw, out int ch);
            int ux = x - Gutter, uy = y - Gutter;
            uploaded += (long)cw * ch * TexelBytes;
            strided = true;
            Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, uw);
            {
                // ponytail: a block row a call; UNPACK_COMPRESSED_BLOCK_* would take the cell in one if this shows
                for (int by = 0; by < ch; by += Block)
                {
                    fixed (byte* pa = data.AsSpan((int)UnionOffset(0, ux, uy + by, uw, uh))) Gl.CompressedTextureSubImage3D(_gl.TexA, 0, cx, cy + by, r.Page, cw, Block, 1, FormatA, cw * Block, pa);
                    fixed (byte* pb = data.AsSpan((int)UnionOffset(1, ux, uy + by, uw, uh))) Gl.CompressedTextureSubImage3D(_gl.TexB, 0, cx, cy + by, r.Page, cw, Block, 1, FormatB, cw * Block, pb);
                }
                fixed (byte* pc = data.AsSpan((int)UnionOffset(2, ux, uy, uw, uh))) Gl.TextureSubImage3D(_gl.TexC, 0, cx, cy, r.Page, cw, ch, 1, Gl.RGBA, Gl.UNSIGNED_BYTE, pc);
            }
            Land(ri, true); _pending--; adopted++;
        }
        if (strided) Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, 0);
        if (t0 != 0) StampAdopt(Stopwatch.GetTimestamp() - t0, adopted, uploaded, uploaded >= budget);
    }

    private static uint PageArray(uint format, int layers)
    {
        uint t; Gl.CreateTextures(Gl.TEXTURE_2D_ARRAY, 1, &t);
        Gl.DrainErrors();
        Gl.TextureStorage3D(t, 1, format, PageSize, PageSize, layers);
        if (Gl.GetError() != 0) { Gl.DeleteTextures(1, &t); return 0; }
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        return t;
    }

    private static uint Scratch()
    {
        uint t; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        Gl.DrainErrors();
        Gl.TextureStorage2D(t, 1, Gl.RGBA16F, PageSize, PageSize);
        if (Gl.GetError() != 0) { Gl.DeleteTextures(1, &t); return 0; }
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.NEAREST);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.NEAREST);
        return t;
    }

    private static uint Blocks()
    {
        uint t; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        Gl.DrainErrors();
        Gl.TextureStorage2D(t, 1, Gl.RGBA32UI, PageSize / Block, PageSize / Block);
        if (Gl.GetError() != 0) { Gl.DeleteTextures(1, &t); return 0; }
        return t;
    }

    private static uint ScratchDepth()
    {
        uint t; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        Gl.DrainErrors();
        Gl.TextureStorage2D(t, 1, Gl.DEPTH_COMPONENT16, PageSize, PageSize);
        if (Gl.GetError() != 0) { Gl.DeleteTextures(1, &t); return 0; }
        return t;
    }

    private static uint ScratchCube(int size)
    {
        uint t; Gl.CreateTextures(Gl.TEXTURE_CUBE_MAP_ARRAY, 1, &t);
        Gl.DrainErrors();
        Gl.TextureStorage3D(t, 1, Gl.DEPTH_COMPONENT24, size, size, 6);
        if (Gl.GetError() != 0) { Gl.DeleteTextures(1, &t); return 0; }
        Gl.TextureParameteri(t, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_S, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_T, Gl.CLAMP_TO_EDGE);
        Gl.TextureParameteri(t, Gl.TEXTURE_COMPARE_MODE, (int)Gl.COMPARE_REF_TO_TEXTURE);
        Gl.TextureParameteri(t, Gl.TEXTURE_COMPARE_FUNC, (int)Gl.LEQUAL);
        return t;
    }

    /// <summary>Build once per context: entry points, texture units, the programs, the scratch pair, the FBO
    /// and the cube. Any refusal latches the pass off for the context with its reason.</summary>
    private static bool Ensure()
    {
        if (_down.Failed) return false;
        if (_gl.Built) return true;
        _gl.Built = true;
        // CopyImageSubData is what the encoder's blocks reach the pages with (GL 4.3 core)
        if (Gl.DrawElementsBaseVertex == null || Gl.DrawBuffers == null || Gl.CopyImageSubData == null
            || Gl.ClearBufferfv == null || Gl.FramebufferTextureLayer == null || Gl.GenFramebuffers == null
            || Gl.DispatchCompute == null || Gl.BindImageTexture == null || Gl.MemoryBarrier == null || Gl.Uniform4iv == null
            || Gl.CompressedTextureSubImage3D == null || Gl.GetTextureSubImage == null
            || Gl.ClearNamedBufferSubData == null || Gl.NamedBufferStorage == null)
            return _down.Fail(_log, "a GL entry point the bake needs is missing", "every surface keeps the real-time loop");
        int units = 0; Gl.GetIntegerv(Gl.MAX_TEXTURE_IMAGE_UNITS, &units);
        if (units <= (int)UnitC)
            return _down.Fail(_log, $"{units} fragment texture units, the pages need {UnitC + 1}", "every surface keeps the real-time loop");
        _gl.ProgBake = GlProgram.Graphics(BakeVs, BakeGs, BakeFs, _log, "LightmapBake bake program");
        _gl.ProgEnc = GlProgram.Compute(EncCs, _log, "LightmapBake encoder");
        if (_gl.ProgBake == 0 || _gl.ProgEnc == 0) return _down.Fail(_log, "the bake or encoder program failed to link", "every surface keeps the real-time loop");
        _bSlot = GlProgram.Loc(_gl.ProgBake, "uSlot"); _bRect = GlProgram.Loc(_gl.ProgBake, "uRect"); _bDepth = GlProgram.Loc(_gl.ProgBake, "uBakeDepth");
        _bLight = GlProgram.Loc(_gl.ProgBake, "uBakeLight"); _bColor = GlProgram.Loc(_gl.ProgBake, "uBakeColor"); _bMode = GlProgram.Loc(_gl.ProgBake, "uBakeMode");
        _bCubeTex = GlProgram.Loc(_gl.ProgBake, "uLocalShadow"); _bDynTex = GlProgram.Loc(_gl.ProgBake, "uLocalDynShadow");
        _bCubeCount = GlProgram.Loc(_gl.ProgBake, "uLocalCubes"); _bSoftWorld = GlProgram.Loc(_gl.ProgBake, "uLocalSoftWorld");
        _bChan = GlProgram.Loc(_gl.ProgBake, "uBakeChan");
        _eRegion = GlProgram.Loc(_gl.ProgEnc, "uRegion");
        int prevProg; Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prevProg);
        Gl.UseProgram(_gl.ProgBake);
        Gl.Uniform1i(_bCubeTex, (int)AlloyRenderer.CubeUnit);
        Gl.Uniform1i(_bDynTex, (int)AlloyRenderer.CubeDynUnit);
        Gl.UseProgram((uint)prevProg);
        uint f; Gl.GenFramebuffers(1, &f); _gl.Fbo = f;
        _gl.ScrA = Scratch(); _gl.ScrB = Scratch(); _gl.ScrC = Scratch(); _gl.ScrZ = ScratchDepth();
        _gl.BlkA = Blocks(); _gl.BlkB = Blocks(); _gl.BlkRead = Blocks();
        _cubeSize = Math.Max(CasterPass.CubeFaceSize, 64);
        _gl.Cube = ScratchCube(_cubeSize);
        if (_gl.Fbo == 0 || _gl.ScrA == 0 || _gl.ScrB == 0 || _gl.ScrC == 0 || _gl.ScrZ == 0 || _gl.BlkA == 0 || _gl.BlkB == 0 || _gl.BlkRead == 0 || _gl.Cube == 0)
            return _down.Fail(_log, "the scratch pages or cube could not be allocated (out of video memory?)", "every surface keeps the real-time loop");
        _log?.Info($"LightmapBake: armed -- page pool up to lightmap.vram {Lightmap.Vram.V} MiB, scratch cube {_cubeSize}, {(Hold ? HoldLightsPerFrame : BackgroundLightsPerFrame)} light steps per frame");
        return true;
    }

    /// <summary>The world edge: every rect, record and page assignment describes an area that is gone. The
    /// GL names stay (the pages are re-packed from zero), the cache thread's queue is drained.</summary>
    internal static void Revoke()
    {
        DropHold();
        FlushKeySample();
        // CX-56: the last chance to write this area's plan. The hold's release writes it when there is a hold;
        // with lightmap.hold 0, or after a skip the background bake then finished, this edge is the only one.
        AreaWrite(BaselineIn);
        AreaForget();
        _areaHalve = 0; _refineAt = 0;
        Repack();
        _landed = _landedHits = _refused = _pastCap = _bakeSkips = _uncached = 0; _pageMs = 0; _pageMsN = 0;
        _logLeft = LogCap; _knownLogged = false; _keyReported = false; _keyHave = false; _keyKey = 0;
        // CX-56: _loadArmed is deliberately untouched -- it says the CURRENT area's lights are all spawned,
        // which a revoke does not change, so the set this reopens re-closes on the very next walk.
        SetHash = 0; _setN = 0; _stN = 0; _seen.Clear(); _seenPtr.Clear(); Reopen(); _rekeyPending = false;
    }

    /// <summary>The context is gone: forget every name; the next frame rebuilds and re-packs.</summary>
    internal static void ContextLost()
    {
        AbortReadback(gl: false);   // the PBO's name is void, so the mapping is too -- but its rects still count
        _gl = new BakeGlState();
        _down.Forget();
        Revoke();
    }

    internal static string Describe()
        => !Lightmap.G.Master!.V ? "" : _noLoadEdge ? "bake OFF: CNWCArea::LoadArea did not resolve, so nothing can close the static set"
         : _down.Failed ? $"bake FAILED: {_down.Reason}"
         : (LoadRefusals > 0 ? $"bake: {LoadRefusals} area load(s) the engine refused; " : "bake: ")
           + $"pages {PagesUsed}/{_gl.Layers}, {_landed} rects landed ({_landedHits} from cache), {_pending} pending, {_pastCap} past the cap; "
           + $"static lights {CensusStatic} dynamic {CensusDynamic}";

    /// <summary>One clause of the Alloy period line.</summary>
    internal static string Stats(int frames)
    {
        if (!Enabled || (_rectN == 0 && CensusStatic == 0)) { PhaseReset(); return ""; }
        frames = Math.Max(frames, 1);
        string s = $"lightmap pages {PagesUsed}/{_gl.Layers}, bakes {_landed} landed ({_landedHits} cached) / {_pending} pending / {_refused + _pastCap} refused"
                   + (_pastCap > 0 ? $" ({_pastCap} past the cap)" : "") + $", static {CensusStatic} of {CensusStatic + CensusDynamic} lights skipped on baked surfaces"
                   + (_pageMsN > 0 ? $", {_pageMs / _pageMsN:F0} ms wall per page" : "")
                   + (_gl.QBake.Measured ? $", {_gl.QBake.Avg / 1e6:F2} ms GPU per light step (~{_gl.QBake.Avg / 1e6 * _bakeSteps / Math.Max(_pageMsN, 1):F0} ms per page)" : "")
                   + (_bakeSkips > 0 ? $", {_bakeSkips} steps skipped (no gather)" : "")
                   + (_cache.Stale > 0 ? $", {_cache.Stale} stale bake blobs dropped" : "")
                   + (_uncached > 0 ? $", {_uncached} landed uncached (readback busy)" : "")
                   + TierStats()
                   + $", charted draws/frame {_drewBaked / frames} baked / {_drewNoRect / frames} no rect / {_drewPending / frames} pending / {_drewRefused / frames} refused"
                   + (_drewLandedUnbaked > 0 ? $" / {_drewLandedUnbaked / frames} LANDED BUT UNBAKED" : "")
                   + $", census static {CensusStatic} dynamic {CensusDynamic}"
                   + $", alloc bake {_allocRun >> 20} MiB / cache thread {_cache.Allocated >> 20} MiB" 
                   + (_areaKey != 0 ? $", area cache {(_areaAdopted ? "hit" : "miss")} ({_areaAdoptedRects} adopted, {_areaNew} new)" : "")
                   + (_areaKeyless + _areaLate + _areaDrift > 0 ? $", area cache refused {_areaKeyless} keyless / {_areaLate} late / {_areaDrift} drifted" : "")
                   + (_areaSnapped + _areaSnapLost + _areaSnapCapped > 0 ? $", area plan pages read back whole {_areaSnapped} ({_areaSnapLost} lost to the edge, {_areaSnapCapped} plan(s) past the union cap)" : "")
                   + PhaseStats()
                   + (_cslEdges > 0 ? $", {_cslEdges} ComputeStaticLighting edges" : "");
        _drewBaked = _drewNoRect = _drewRefused = _drewPending = _drewLandedUnbaked = 0;
        _gl.QBake.Reset(); _bakeSkips = 0; _allocRun = 0; _evicted = 0; _cache.Allocated = 0; _cslEdges = 0; _bakeSteps = 0; _pageMs = 0; _pageMsN = 0;
        return s;
    }

    /// <summary>Landed rects per tier and the pages of each class. Cold: once a period.</summary>
    private static string TierStats()
    {
        if (Baseline == 0) return "";
        Span<int> per = stackalloc int[MaxTiers]; per.Clear();
        for (int ri = 0; ri < _rectN; ri++) if (_rects[ri].State == RState.Landed) per[_rects[ri].Tier]++;
        int fine = 0, basePages = 0;
        for (int p = 0; p < _gl.Layers; p++) { if (_pageClass[p] == ClassFine) fine++; else if (_pageClass[p] == ClassBase) basePages++; }
        var sb = new System.Text.StringBuilder(", tiers");
        for (int t = 0; t <= Baseline; t++) sb.Append(t == 0 ? " " : " / ").Append(per[t]);
        sb.Append($" rects landed, pages {basePages} baseline + {fine} fine");
        if (_evicted > 0) sb.Append($", {_evicted} evicted");
        return sb.ToString();
    }

}
