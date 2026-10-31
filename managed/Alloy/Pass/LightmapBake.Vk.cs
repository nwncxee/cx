using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The bake on Vulkan: the same flow as GL's -- the cache, the pack, page-major and light-major steps, the
/// finish and the readback -- over Vulkan images. A light step's static cube is queued in the shadow bracket and
/// baked with the frame's own views, so the steps run one frame behind the queue that names them.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>Light steps one Vulkan frame takes at most: the scratch cubes the shadow bracket bakes them into.
    /// ponytail: the hold's 32 a frame would need 32 cubes resident; 8 keeps the array at 8 x 6 faces.</summary>
    private const int VkCubes = 8;

    /// <summary>Everything the Vulkan bake owns on one device, replaced whole with the device.</summary>
    private sealed class BakeVkState
    {
        public VkBackend? Dev;
        public bool Built, Failed, Live;
        public ImageId PageA, PageB, PageC, ScrA, ScrB, ScrC, ScrZ, BlkA, BlkB;
        public SamplerId Clamp;
        public int Layers, PoolMib;
        /// <summary>The pages were re-packed from zero: plane C's layers clear at the next frame.</summary>
        public bool ClearDue;
        public BufferId Chart;
        public bool ChartFilled;
        /// <summary>The readback buffer, the generation of the copy last queued into it, and whether one is in flight
        /// (fenced) or being copied out (draining).</summary>
        public BufferId Read;
        public long ReadBytes;
        public int ReadGen;
        public bool ReadQueued, Draining;
        /// <summary>The light steps the shadow bracket queued cubes for: page, first light, static set, and each
        /// step's light index into the set.</summary>
        public readonly int[] QLight = new int[VkCubes];
        public int QN, QPage = -1, QFrom, CubeSize;
        public ulong QSet;
        /// <summary>This frame's list while <see cref="VkRun"/> records.</summary>
        public CommandList? Cl;
        public readonly List<AlloyRenderer.VkLmDraw> Draws = new();
    }

    private static BakeVkState _vkb = new();

    /// <summary>Resident page layers on the backend that bakes them.</summary>
    private static int Layers
    {
        get => _vkb.Layers;
        set => _vkb.Layers = value;
    }

    /// <summary>A readback's copy is queued and its submit not yet known done.</summary>
    private static bool Fenced => _vkb.ReadQueued;

    /// <summary>A finished readback is still being copied out, a slice a frame.</summary>
    private static bool Draining => _vkb.Draining;

    /// <summary>The record buffer the Vulkan lit and deferred stages read on <see cref="BindChart"/>; null before the
    /// first upload, and in the A/B's frames, whose GL half has no records to read.</summary>
    internal static BufferId VkChart => _vkb.ChartFilled ? _vkb.Chart : default;

    /// <summary><c>uLmPages</c> for a Vulkan lit bind: the resident layers when the lightmap reads this frame, else 0.
    /// The A/B's frames read none, as GL's half does.</summary>
    internal static int VkLivePages
        => Enabled && !FrameInputs.Blind && _vkb.Layers > 0 && _vkb.ChartFilled ? _vkb.Layers : 0;

    /// <summary>The page array a lit stage's sampler <paramref name="name"/> reads, and its sampler; null for any other name.</summary>
    internal static ImageId VkPage(string name, out SamplerId sampler)
    {
        sampler = _vkb.Clamp;
        return _vkb.Layers == 0 ? default : name switch { "uLmA" => _vkb.PageA, "uLmB" => _vkb.PageB, "uLmC" => _vkb.PageC, _ => default };
    }

    /// <summary>From the shadow bracket, after its own views: queue the static cubes of the page's next light steps,
    /// baked with the frame's flush. Nothing when no page is open -- the colour frame opens one, and the next bracket
    /// queues its lights.</summary>
    internal static void VkQueueCubes()
    {
        BakeVkState q = _vkb;
        q.QN = 0;
        if (!Enabled || _bakePage < 0 || q.Layers == 0 || !q.Built) return;
        int size = Math.Max(CasterPass.CubeFaceSize, 64);
        int steps = Math.Min(Hold ? HoldLightsPerFrame : BackgroundLightsPerFrame, VkCubes);
        for (int k = 0; k < steps && _bakeLight + k < _bakeLights.Count; k++)
        {
            int l = _bakeLights[_bakeLight + k];
            Vector4 pr = _setPosR[l];
            if (!CasterPass.VkBakeCube(k, VkCubes, size, new Vector3(pr.X, pr.Y, pr.Z), BakeNear, pr.W)) { _bakeSkips++; break; }
            q.QLight[k] = l;
            q.QN = k + 1;
        }
        q.QPage = _bakePage; q.QFrom = _bakeLight; q.QSet = SetHash; q.CubeSize = size;
    }

    /// <summary>The Vulkan frame's bake slot, after the shadow flush: <see cref="Run"/>'s work recorded into
    /// <paramref name="cl"/>.</summary>
    internal static void VkRun(VkBackend vk, CommandList cl, bool capturing, int frame)
    {
        if (_vkb.Dev != vk) _vkb = new BakeVkState { Dev = vk };
        if (!Enabled) { AbortReadback(); return; }
        if (capturing) return;
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        _vkb.Cl = cl;
        try
        {
            RunInner(frame);
            // the pages leave this slot readable: a lit bind inside the colour pass cannot place their barrier
            BakeVkState s = _vkb;
            if (s.Layers > 0) cl.ReadImages([s.PageA, s.PageB, s.PageC]);
        }
        finally { _vkb.Cl = null; _allocRun += GC.GetAllocatedBytesForCurrentThread() - a0; }
    }

    /// <summary>The scratch planes, the block images and the programs, once per device. Any refusal latches the bake
    /// off for the session with its reason; a cached bake is not read either, the pool being part of the build.</summary>
    private static bool VkEnsure()
    {
        BakeVkState s = _vkb;
        if (_down.Failed || s.Failed) return false;
        if (s.Built) return true;
        VkBackend vk = s.Dev!;
        if (!AlloyRenderer.VkLmReady(vk, _log!)) { s.Failed = true; return _down.Fail(_log, "the Vulkan bake or encoder program was refused", "every surface keeps the real-time loop"); }
        s.ScrA = VkImage(vk, ImageKind.Tex2D, Format.Rgba16F, PageSize, 1, ImageUse.ColorTarget | ImageUse.Sampled, "vk lightmap scratch A");
        s.ScrB = VkImage(vk, ImageKind.Tex2D, Format.Rgba16F, PageSize, 1, ImageUse.ColorTarget | ImageUse.Sampled, "vk lightmap scratch B");
        s.ScrC = VkImage(vk, ImageKind.Tex2D, Format.Rgba16F, PageSize, 1, ImageUse.ColorTarget | ImageUse.Sampled, "vk lightmap scratch C");
        s.ScrZ = VkImage(vk, ImageKind.Tex2D, Format.D16, PageSize, 1, ImageUse.DepthTarget, "vk lightmap scratch depth");
        s.BlkA = VkImage(vk, ImageKind.Tex2D, Format.Rgba32Ui, PageSize / Block, 1, ImageUse.Storage | ImageUse.CopySrc, "vk lightmap blocks A");
        s.BlkB = VkImage(vk, ImageKind.Tex2D, Format.Rgba32Ui, PageSize / Block, 1, ImageUse.Storage | ImageUse.CopySrc, "vk lightmap blocks B");
        s.Clamp = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Nearest, Wrap.Clamp));
        s.Chart = vk.CreateBuffer(new BufferDesc((long)_rec.Length * 4, BufferUse.Storage, MemoryKind.Upload, "vk lightmap records"));
        if (s.ScrA.IsNull || s.ScrB.IsNull || s.ScrC.IsNull || s.ScrZ.IsNull || s.BlkA.IsNull || s.BlkB.IsNull || s.Clamp.IsNull || s.Chart.IsNull)
        {
            s.Failed = true;
            return _down.Fail(_log, "the Vulkan scratch pages, blocks or record buffer could not be allocated (out of video memory?)", "every surface keeps the real-time loop");
        }
        s.Built = true;
        _log?.Info($"LightmapBake: armed on Vulkan -- page pool up to lightmap.vram {Lightmap.Vram.V} MiB, {VkCubes} scratch cubes, "
                 + $"{Math.Min(Hold ? HoldLightsPerFrame : BackgroundLightsPerFrame, VkCubes)} light steps per frame");
        return true;
    }

    private static ImageId VkImage(VkBackend vk, ImageKind kind, Format f, int size, int layers, ImageUse use, string label)
        => vk.CreateImage(new ImageDesc(kind, f, size, size, 1, 1, layers, 1, use, label));

    /// <summary><see cref="Pool"/> on Vulkan: the three page arrays, sized once per <c>lightmap.vram</c> value.</summary>
    private static bool VkPool()
    {
        BakeVkState s = _vkb;
        VkBackend vk = s.Dev!;
        if (s.Layers > 0 && s.PoolMib == Lightmap.Vram.V) { VkClearPages(); return true; }
        if (s.Layers > 0) { VkFreePages(); Revoke(); }
        int n = PoolPages(Lightmap.Vram.V, AlloyRenderer.VramTotalMib, AlloyRenderer.VramFreeMib);
        s.PageA = VkImage(vk, ImageKind.Tex2DArray, Format.Bc6U, PageSize, n, ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "vk lightmap irradiance");
        s.PageB = VkImage(vk, ImageKind.Tex2DArray, Format.Bc7, PageSize, n, ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "vk lightmap direction");
        s.PageC = VkImage(vk, ImageKind.Tex2DArray, Format.Rgba8, PageSize, n,
                          ImageUse.Sampled | ImageUse.Storage | ImageUse.ColorTarget | ImageUse.CopyDst | ImageUse.CopySrc, "vk lightmap mover shares");
        if (s.PageA.IsNull || s.PageB.IsNull || s.PageC.IsNull)
        {
            vk.Destroy(s.PageA); vk.Destroy(s.PageB); vk.Destroy(s.PageC);
            s.PageA = s.PageB = s.PageC = default;
            _log?.Always($"LightmapBake: a {n}-page pool ({n * PageMiB} MiB) was refused by the Vulkan device ({AlloyRenderer.VramFreeMib} MiB free)");
            return _down.Fail(_log, "the page pool could not be allocated (out of video memory?)", "every surface keeps the real-time loop");
        }
        s.Layers = n; s.PoolMib = Lightmap.Vram.V; s.ClearDue = true;
        VkClearPages();
        _log?.Info($"LightmapBake: page pool {n} x {PageMiB} MiB = {n * PageMiB} MiB resident on Vulkan (lightmap.vram {Lightmap.Vram.V})");
        return true;
    }

    /// <summary>Plane C's layers to no mover share, once the pages are re-packed. ponytail: A and B are not zeroed --
    /// no record points outside a landed cell and every cell is encoded whole, so only a readback's gaps see them.</summary>
    private static void VkClearPages()
    {
        BakeVkState s = _vkb;
        if (!s.ClearDue || s.Cl == null) return;
        s.ClearDue = false;
        for (int l = 0; l < s.Layers; l++)
        {
            ReadOnlySpan<ColorAttachment> c = [new(s.PageC, 0, l, LoadOp.Clear, StoreOp.Store, default)];
            s.Cl.BeginRenderPass(new RenderPassDesc(c));
            s.Cl.EndRenderPass();
        }
    }

    private static void VkFreePages()
    {
        BakeVkState s = _vkb;
        s.Dev!.Destroy(s.PageA); s.Dev.Destroy(s.PageB); s.Dev.Destroy(s.PageC);
        _log?.Info($"LightmapBake: {s.Layers} Vulkan pages freed ({s.Layers * PageMiB} MiB)");
        s.PageA = s.PageB = s.PageC = default; s.Layers = 0;
    }

    /// <summary>The page's light steps whose cubes this frame's flush baked, then its finish bands, then the next
    /// page's coverage: <see cref="Step"/>'s loop with the cube already rendered.</summary>
    private static void VkSteps()
    {
        BakeVkState q = _vkb;
        int bands = Math.Min(Hold ? HoldLightsPerFrame : BackgroundLightsPerFrame, VkCubes);
        if (_bakePage < 0) { if (!BeginPage()) return; }
        else if (q.QN > 0 && q.QPage == _bakePage && q.QFrom == _bakeLight && q.QSet == SetHash && !CasterPass.VkBakeCubes.IsNull)
            for (int k = 0; k < q.QN && _bakeLight < _bakeLights.Count; k++)
            {
                if (!VkBakeLight(q.QLight[k], k)) break;
                _bakeLight++; _bakeSteps++;
            }
        q.QN = 0;
        for (int k = 0; k < bands && _bakePage >= 0 && _bakeLight >= _bakeLights.Count; k++)
            if (!FinishPage()) break;
    }

    /// <summary>The page's coverage on Vulkan: the scratch planes cleared and every rect drawn at coverage. False when
    /// the uniform arena refused it, and the page reopens next frame.</summary>
    private static bool VkCoverage()
    {
        BakeVkState s = _vkb;
        List<AlloyRenderer.VkLmDraw> d = s.Draws;
        d.Clear();
        for (int k = 0; k < _bakeRects.Count; k++) VkDraw(_bakeRects[k], Channels, d);
        return AlloyRenderer.VkLmBake(s.Cl!, s.ScrA, s.ScrB, s.ScrC, s.ScrZ, 0, default, default, 0, default, s.CubeSize,
                               System.Runtime.InteropServices.CollectionsMarshal.AsSpan(d));
    }

    /// <summary>Light <paramref name="l"/> over the page against slot <paramref name="cube"/> of the bake's cubes.</summary>
    private static bool VkBakeLight(int l, int cube)
    {
        BakeVkState s = _vkb;
        Vector4 pr = _setPosR[l];
        List<AlloyRenderer.VkLmDraw> d = s.Draws;
        d.Clear();
        for (int k = 0; k < _bakeRects.Count; k++)
        {
            ref Rect r = ref _rects[_bakeRects[k]];
            if (Touches(l, r.C, r.R)) VkDraw(_bakeRects[k], _bakePicks[k].ChannelOf(l), d);
        }
        return AlloyRenderer.VkLmBake(s.Cl!, s.ScrA, s.ScrB, s.ScrC, s.ScrZ, _bakeLight + 1, pr, _setCol[l], cube,
                                      CasterPass.VkBakeCubes, s.CubeSize, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(d));
    }

    /// <summary><see cref="DrawRect"/>'s draw on Vulkan, into <paramref name="d"/>.</summary>
    private static void VkDraw(int ri, int chan, List<AlloyRenderer.VkLmDraw> d)
    {
        ref Rect r = ref _rects[ri];
        if (r.Slot < 0 || !MeshChart.Range(r.Vao, r.Item, 0, out int baseVertex, out nint off, out int count, out _)) return;
        int range = MeshChart.RangeOf(r.Vao, r.Item);
        if (range < 0) return;
        d.Add(new AlloyRenderer.VkLmDraw(r.Slot, r.X, r.Y, r.S, 1f / (MeshChart.AtlasTexelsPerUnit * r.S), chan,
                                         (int)Db.MeshArena.PubFormat(range), baseVertex, (int)(off / sizeof(ushort)), count));
    }

    /// <summary>One encode band of the finish on Vulkan; false when the uniform arena refused it.</summary>
    private static bool VkEncode(int page, int bx, int by, int bw, int bh)
    {
        BakeVkState s = _vkb;
        return AlloyRenderer.VkLmEncode(s.Cl!, s.ScrA, s.ScrB, s.ScrC, s.BlkA, s.BlkB, s.PageC, page, bx, by, bw, bh);
    }

    /// <summary>A baked rect's cell from the block images into the pages, block for texel-block.</summary>
    private static void VkCopyCell(int page, int cx, int cy, int cw, int ch)
    {
        BakeVkState s = _vkb;
        s.Cl!.CopyImage(new CopyImageCmd(s.BlkA, new ImageRegion(0, cx / Block, cy / Block, 0), s.PageA, new ImageRegion(0, cx, cy, page), cw / Block, ch / Block, 1));
        s.Cl.CopyImage(new CopyImageCmd(s.BlkB, new ImageRegion(0, cx / Block, cy / Block, 0), s.PageB, new ImageRegion(0, cx, cy, page), cw / Block, ch / Block, 1));
    }

    /// <summary>The page region's three planes into the readback buffer, A and B as raw blocks and C as texels: the
    /// blob layout GL's readback files. Collected once a later frame finds its submit done.</summary>
    private static void VkQueueReadback(int page, int x0, int y0, int w, int h)
    {
        BakeVkState s = _vkb;
        long bytes = (long)w * h * TexelBytes, plane = (long)w * h;
        if (s.Cl == null) return;
        if (s.Read.IsNull || s.ReadBytes < bytes)
        {
            s.Dev!.Destroy(s.Read);
            s.ReadBytes = Math.Max(bytes, 16L << 20);
            s.Read = s.Dev.CreateBuffer(new BufferDesc(s.ReadBytes, BufferUse.CopyDst, MemoryKind.Readback, "vk lightmap readback"));
            if (s.Read.IsNull) { s.ReadBytes = 0; return; }
        }
        s.Cl.CopyImageToBuffer(new CopyToBufferCmd(s.PageA, new ImageRegion(0, x0, y0, page), w, h, s.Read, 0));
        s.Cl.CopyImageToBuffer(new CopyToBufferCmd(s.PageB, new ImageRegion(0, x0, y0, page), w, h, s.Read, plane));
        s.Cl.CopyImageToBuffer(new CopyToBufferCmd(s.PageC, new ImageRegion(0, x0, y0, page), w, h, s.Read, 2 * plane));
        s.ReadQueued = true;
        s.Cl.WhenDone(ReadLanded, ++s.ReadGen);
    }

    /// <summary><see cref="OnReadLanded"/>, made once: queuing a readback allocates no delegate.</summary>
    private static readonly Action<int, bool> ReadLanded = OnReadLanded;

    /// <summary>The frame list carrying the readback's copy finished on the GPU (or never ran): the copy-out starts,
    /// a slice a frame from the next. A readback aborted or replaced since is dropped.</summary>
    private static void OnReadLanded(int gen, bool ran)
    {
        BakeVkState s = _vkb;
        if (gen != s.ReadGen || !s.ReadQueued) return;
        if (ran) { StartCopyOut(); return; }
        s.ReadQueued = false;
        _uncached += _pendingRead.Count; _pendingRead.Clear(); _uSnap = false;
    }

    /// <summary>One slice of the finished readback out of the buffer into the ring slot; false = refused (logged).</summary>
    private static bool VkReadSlice(long from, long end)
        => _vkb.Dev!.ReadMapped(_vkb.Read, from, _readBufs[_uBuf].AsSpan((int)from, (int)(end - from)));

    /// <summary>A cache hit's cell (or an adopted union's band) into the pages: A and B as block rows, C as texels,
    /// each read out of a union <paramref name="uw"/> texels wide from (<paramref name="ux"/>, <paramref name="uy"/>).</summary>
    private static void VkUpload(byte[] data, int page, int x, int y, int w, int h, int ux, int uy, int uw, int uh)
    {
        BakeVkState s = _vkb;
        VkBackend vk = s.Dev!;
        long a = UnionOffset(0, ux, uy, uw, uh), b = UnionOffset(1, ux, uy, uw, uh), c = UnionOffset(2, ux, uy, uw, uh);
        // a block row is uw * Block bytes apart; the span ends at the last row's last block
        int blk = (h / Block - 1) * uw * Block + w * Block, tex = ((h - 1) * uw + w) * 4;
        vk.UploadRegion(s.PageA, page, x, y, w, h, uw, data.AsSpan((int)a, blk));
        vk.UploadRegion(s.PageB, page, x, y, w, h, uw, data.AsSpan((int)b, blk));
        vk.UploadRegion(s.PageC, page, x, y, w, h, uw, data.AsSpan((int)c, tex));
    }

    /// <summary><see cref="UploadRecords"/> on Vulkan: the whole table staged once, then the dirty range -- staged
    /// when it is large, inline in the frame's list when not.</summary>
    private static void VkUploadRecords()
    {
        BakeVkState s = _vkb;
        if (s.Chart.IsNull || s.Cl == null) return;
        ReadOnlySpan<byte> all = System.Runtime.InteropServices.MemoryMarshal.AsBytes(_rec.AsSpan());
        if (!s.ChartFilled) s.ChartFilled = s.Dev!.Upload(s.Chart, 0, all);
        else if (_dirtyHi >= _dirtyLo)
        {
            int at = _dirtyLo * RecFloats * 4, n = (_dirtyHi - _dirtyLo + 1) * RecFloats * 4;
            if (n > InlineRecordBytes) s.Dev!.Upload(s.Chart, at, all.Slice(at, n));
            else s.Cl.UpdateBuffer(s.Chart, at, all.Slice(at, n));
        }
        _dirtyLo = int.MaxValue; _dirtyHi = -1;
        if (!s.Live && s.ChartFilled && _landed > 0)
        {
            s.Live = true;
            _log?.Always($"LightmapBake: the Vulkan lightmap is LIVE -- {_landed} rects landed ({_landedHits} from the cache) on {s.Layers} pages, "
                       + "read by the lit and deferred stages");
        }
    }

    /// <summary>A dirty record range past this goes through the staged upload rather than the frame's own list.</summary>
    private const int InlineRecordBytes = 64 << 10;
}
