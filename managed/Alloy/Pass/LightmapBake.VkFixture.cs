using System;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>A golden's fixture area for the Vulkan bake: four charted triangles on owned slots, enrolled by the sweep
/// and baked, cached and adopted by the same frames the live bake runs.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>The fixture's rects in atlas texels: three a cell too wide for two to share the half scratch, so each
    /// bakes in a band of its own, and a small one whose placement is new at every bake.</summary>
    private static readonly (int W, int H)[] FixtureRects = [(1352, 60), (1352, 56), (1352, 52), (64, 32)];

    /// <summary>The first owned slot the fixture takes; one per rect from it.</summary>
    private const int FixtureSlot = 1000;

    /// <summary>The VDO slot the fixture's charts are filed under; one per rect from it, item 0.</summary>
    private const int FixtureVao = 70000;

    /// <summary>The fixture's mesh content keys; one per rect from it.</summary>
    private const ulong FixtureContent = 0x6c6d_6669_7800_0000UL;   // not-an-offset: "lmfix" -- a content key

    /// <summary>Frames a fixture bake may take before it is given up.</summary>
    private const int FixtureFrames = 900;

    /// <summary>Floats in a static arena vertex: position, normal, texture UV, chart UV, tangent and paint words.</summary>
    private const int FixtureStride = 12;

    /// <summary>A fixture area baked on <paramref name="vk"/> through the bake's own path, with the scratch the ladder
    /// gives now, its disk cache in <paramref name="cacheDir"/>: the stable rects' page bytes, null when it could not run,
    /// the rects the cache answered, and the bytes the bake's scratch took.</summary>
    internal static byte[]? FixtureBake(VkBackend vk, ExtenderLog log, string cacheDir, out int cacheHits, out long scratchBytes)
    {
        cacheHits = 0;
        scratchBytes = 0;
        int n = FixtureRects.Length;
        BakeVkState savedVk = _vkb;
        int savedPage = _bakePage, savedSweep = _sweepAt;
        Knob<bool> master = Lightmap.G.Master!;
        bool savedEdge = _noLoadEdge, savedHold = Hold, savedMaster = master.V;
        int savedVram = Lightmap.Vram.V, savedTiers = Lightmap.Tiers.V, savedDensity = Lightmap.Density.V;
        var ranges = new int[n];
        var placed = new bool[n];
        var charted = new bool[n];
        master.V = true; Lightmap.Vram.V = 192; Lightmap.Tiers.V = 1; Lightmap.Density.V = 8;
        Hold = false; _noLoadEdge = false;
        _vkb = new BakeVkState { Dev = vk };
        _bakePage = -1;
        Revoke();
        _cache.Attach(log);
        _cache.Retarget(cacheDir);
        try
        {
            if (!AlloyRenderer.VkFixtureEnsure(vk, 64, 64, log) || !MeshArena.ResetForCheck()) return null;
            for (int k = 0; k < n; k++)
            {
                ranges[k] = MeshArena.AcquireRaw(FixtureContent + (ulong)k, MeshArena.Fmt.Static, 3, FixtureTri(FixtureRects[k].W, FixtureRects[k].H),
                                                 FixtureIdx, 3, null, 0, null, 0, Vector3.Zero, new Vector3(1f, 1f, 0f));
                if (ranges[k] <= 0) return null;
            }
            MeshArena.VkPumpForCheck(vk);
            MeshArena.FakeResidentForCheck(MeshArena.Fmt.Static);
            float moved = Stopwatch.GetTimestamp() % 100_000;
            for (int k = 0; k < n; k++)
            {
                MeshChart.FileFixture(FixtureVao + k, 0, ranges[k], FixtureRects[k].W, FixtureRects[k].H, FixtureContent + (ulong)k,
                                      Vector3.Zero, new Vector3(1f, 1f, 0f));
                ranges[k] = 0; charted[k] = true;
                placed[k] = AlloyRenderer.PlaceFixture(FixtureSlot + k, FixtureVao + k, 0,
                                                       Matrix4x4.CreateTranslation(k == n - 1 ? moved : 10f * k, 0f, 0f));
                if (!placed[k]) return null;
            }
            SetHash = FixtureContent; _setN = 0; _sweepAt = FixtureSlot;
            int frame0 = _frame + 1;
            for (int f = 0; f < FixtureFrames && !FixtureIn(n); f++)
            {
                var cl = new CommandList("golden lightmap fixture", 1 << 16, log);
                if (!AlloyRenderer.VkFixtureFrame(vk, cl)) return null;
                VkRun(vk, cl, false, frame0 + f);
                scratchBytes = Math.Max(scratchBytes, _vkb.ScratchTook);
                if (!vk.Submit(cl) || !vk.WaitSubmitted()) return null;
            }
            if (!FixtureIn(n)) { log.Info($"LightmapBake.FixtureBake: the fixture did not land in {FixtureFrames} frames"); return null; }
            cacheHits = _landedHits;
            return FixturePages(vk, log, n - 1);
        }
        finally
        {
            Revoke();
            _bakePage = -1;
            VkFreeScratch();
            BakeVkState s = _vkb;
            if (s.Layers > 0) VkFreePages();
            vk.Destroy(s.Clamp); vk.Destroy(s.Chart);
            for (int k = 0; k < n; k++)
            {
                if (charted[k]) MeshChart.ForgetFixture(FixtureVao + k, 0);
                else if (ranges[k] > 0) MeshArena.Release(ranges[k]);
                if (placed[k]) AlloyRenderer.ClearFixture(FixtureSlot + k);
            }
            MeshArena.FrameEdgeForCheck();
            MeshArena.VkForgetForCheck();
            MeshArena.ResetForCheck();
            for (int w = 0; w < 500 && _cache.Outstanding > 0; w++) BlockingWait.Sleep(10, "self-check only: the cache thread writes the fixture's files before the folder goes back");
            _cache.Retarget(null);
            _vkb = savedVk; _bakePage = savedPage; _sweepAt = savedSweep;
            _noLoadEdge = savedEdge; Hold = savedHold;
            master.V = savedMaster; Lightmap.Vram.V = savedVram; Lightmap.Tiers.V = savedTiers; Lightmap.Density.V = savedDensity;
        }
    }

    /// <summary>One triangle over half of a <paramref name="w"/> x <paramref name="h"/> chart, in the static arena's layout.</summary>
    private static float[] FixtureTri(int w, int h)
    {
        var v = new float[3 * FixtureStride];
        ReadOnlySpan<Vector3> pos = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        ReadOnlySpan<Vector2> chart = [Vector2.Zero, new(w, 0f), new(0f, h)];
        for (int i = 0; i < 3; i++)
        {
            Span<float> o = v.AsSpan(i * FixtureStride, FixtureStride);
            o[0] = pos[i].X; o[1] = pos[i].Y; o[2] = pos[i].Z;
            o[5] = 1f;
            o[8] = chart[i].X; o[9] = chart[i].Y;
        }
        return v;
    }

    /// <summary>The fixture triangle's indices.</summary>
    private static readonly ushort[] FixtureIdx = [0, 1, 2];

    /// <summary>Every fixture rect has landed, no page is open, and nothing is left to read back or write to the cache.</summary>
    private static bool FixtureIn(int n)
    {
        for (int k = 0; k < n; k++)
        {
            int ri = _slotRect[FixtureSlot + k];
            if (ri < 0 || _rects[ri].State != RState.Landed) return false;
        }
        return _bakePage < 0 && _pending == 0 && ReadbackIdle && _cache.Outstanding == 0;
    }

    /// <summary>The cells of the first <paramref name="n"/> fixture rects read off the pages, A, B then C for each, in slot order.</summary>
    private static byte[]? FixturePages(VkBackend vk, ExtenderLog log, int n)
    {
        BakeVkState s = _vkb;
        var cl = new CommandList("golden lightmap fixture readback", 1 << 12, log);
        long total = 0;
        for (int k = 0; k < n; k++)
        {
            CellOf(_rects[_slotRect[FixtureSlot + k]], out _, out _, out int w, out int h);
            total += (long)w * h * TexelBytes;
        }
        BufferId rb = vk.CreateBuffer(new BufferDesc(total, BufferUse.CopyDst, MemoryKind.Readback, "golden lightmap fixture readback"));
        if (rb.IsNull) return null;
        try
        {
            long at = 0;
            for (int k = 0; k < n; k++)
            {
                ref Rect r = ref _rects[_slotRect[FixtureSlot + k]];
                CellOf(r, out int x, out int y, out int w, out int h);
                long plane = (long)w * h;
                cl.CopyImageToBuffer(new CopyToBufferCmd(s.PageA, new ImageRegion(0, x, y, r.Page), w, h, rb, at));
                cl.CopyImageToBuffer(new CopyToBufferCmd(s.PageB, new ImageRegion(0, x, y, r.Page), w, h, rb, at + plane));
                cl.CopyImageToBuffer(new CopyToBufferCmd(s.PageC, new ImageRegion(0, x, y, r.Page), w, h, rb, at + 2 * plane));
                at += plane * TexelBytes;
            }
            var bytes = new byte[total];
            return vk.Submit(cl) && vk.ReadBack(rb, bytes) ? bytes : null;
        }
        finally { vk.Destroy(rb); }
    }
}
