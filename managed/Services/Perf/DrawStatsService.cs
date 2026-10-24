using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Services.Perf;

/// <summary>
/// Per-frame draw-path histogram (ADR 0006), opt-in via <c>alloy.drawhooks=stats</c> in <c>cx.cfg</c> (off = no hooks,
/// zero cost; config and not a chat command, because it and <c>DrawFunnelService</c> hook the same symbols, so which
/// one arms is decided once at install). Call-through hooks on <c>GLRender::RenderVertexData</c> and
/// <c>Scene::RenderDrawBucket</c> group draws by (VDO, GL program) and log the top ones. Hot, no allocation.
/// </summary>
public sealed unsafe class DrawStatsService : IExtenderService
{
    private const int Groups = 512, Pairs = 8192, LogEvery = 900;

    /// <summary>True when <c>alloy.drawhooks=stats</c> armed the hooks (read by the frame tick).</summary>
    public static bool Enabled;

    private static ExtenderLog? _log;
    private static RenderDrawBucketFn _origBucket;
    private static int _bucket = -1;

    // period accumulators (reset each log)
    private static int _frames, _draws, _indexed, _bucketDraws;
    private static long _idxSum;
    // group table: key = vdo ^ (program rotated); value rows in parallel arrays
    private static readonly ulong[] _gKey = new ulong[Groups];
    private static readonly int[] _gDraws = new int[Groups];
    private static readonly int[] _gItems = new int[Groups];
    private static readonly long[] _gIdx = new long[Groups];
    private static readonly IntPtr[] _gVdo = new IntPtr[Groups];
    private static readonly int[] _gProg = new int[Groups];
    private static int _gOver;
    // (vdo,item,program) first-seen set for unique-member counts
    private static readonly ulong[] _pKey = new ulong[Pairs];
    private static int _pOver;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        // ahead of the stand-down -- the host called EndFrame every frame regardless, and EndFrame
        // guards itself on Enabled. Registering after the return would silently change that.
        FrameRegistry.Global.Register(FramePhase.PostPresent, 0, EndFrame, "DrawStatsService.endFrame", log);
        if (Alloy.Renderer.DrawHooks.Boot != Alloy.DrawFunnelService.DrawModeStats) return;
        _log = log;
        // Chained, not Install()ed: RenderVertexData has several subscribers. Lowest priority --
        // we only count what actually reached the GPU.
        _drawSlot = hooks.ChainGlRenderVertexData(&OnVdoDraw, DrawPriority);
        _origBucket = hooks.InstallRenderDrawBucket(&OnBucket);
        Enabled = _drawSlot != null;
        log.Info(Enabled
            ? "DrawStatsService: draw-path histogram armed ([drawstats] every 900 frames)"
            : "DrawStatsService: RenderVertexData unresolved -- stats unavailable");
    }

    [UnmanagedCallersOnly]
    private static void OnBucket(IntPtr scene, int id)
    {
        int prev = _bucket;
        _bucket = id;
        if (_origBucket != null) _origBucket(scene, id);
        _bucket = prev;
    }

    /// <summary>Chain priority on RenderVertexData: below the shadow cull and the draw funnels.</summary>
    internal const int DrawPriority = 10;
    private static GlRenderVertexDataFn* _drawSlot;

    /// <summary>Post-draw observer: calls the rest of the chain first, so CURRENT_PROGRAM is valid
    /// (and a swallowed draw is never counted as drawn).</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoDraw(IntPtr vdo, uint item, uint prim, uint size, uint offset)
    {
        GlRenderVertexDataFn next = *_drawSlot;
        if (next != null) next(vdo, item, prim, size, offset);
        Observe(vdo, item, (int)prim, size, offset);
    }

    private static void Observe(IntPtr vdo, uint item, int prim, uint size, uint offset)
    {
        if (!Enabled) return;
        if (vdo == IntPtr.Zero) return;

        _draws++;
        if (_bucket >= 0) _bucketDraws++;
        int idx = 0;
        var v = new VertexDataObject(vdo);
        if (v.Mode == 1)
        {
            _indexed++;
            idx = size != uint.MaxValue ? (int)size
                : new VdoElemItem(v.ElemItems)[(int)item].Count;
            _idxSum += idx;
        }
        int prog = GlProbe.CurrentProgram();

        ulong key = (ulong)vdo ^ ((ulong)(uint)prog << 40) ^ 1;   // ^1 so key 0 stays "empty"
        int slot = (int)(key % Groups);
        for (int probe = 0; probe < 16; probe++, slot = (slot + 1) % Groups)
        {
            if (_gKey[slot] == key) { _gDraws[slot]++; _gIdx[slot] += idx; goto pairs; }
            if (_gKey[slot] == 0)
            {
                _gKey[slot] = key; _gDraws[slot] = 1; _gItems[slot] = 0; _gIdx[slot] = idx;
                _gVdo[slot] = vdo; _gProg[slot] = prog;
                goto pairs;
            }
        }
        _gOver++;
        return;
    pairs:
        ulong pk = key ^ ((ulong)item << 48) ^ ((ulong)item * 0x9e3779b97f4a7c15UL);
        int ps = (int)(pk % Pairs);
        for (int probe = 0; probe < 16; probe++, ps = (ps + 1) % Pairs)
        {
            if (_pKey[ps] == pk) return;
            if (_pKey[ps] == 0) { _pKey[ps] = pk; _gItems[slot]++; return; }
        }
        _pOver++;
    }

    /// <summary>Frame tick, called by UiHostService after present. Logs + resets every ~900 frames.</summary>
    public static void EndFrame()
    {
        if (!Enabled) return;
        if (Gl.GetIntegerv == null && _log != null) Gl.Init(_log);   // GL context exists here, not at service init
        if (++_frames < LogEvery || _draws == 0) return;
        int f = _frames;
        // cold path: allocation + sort are fine here
        var order = new int[Groups];
        int n = 0;
        for (int i = 0; i < Groups; i++) if (_gKey[i] != 0) order[n++] = i;
        Array.Sort(order, 0, n, Comparer<int>.Create((a, b) => _gDraws[b] - _gDraws[a]));
        _log?.Info($"DrawStatsService: [drawstats] {(float)_draws / f:F0} draws/frame " +
                   $"({100f * _indexed / _draws:F0}% indexed, {100f * _bucketDraws / _draws:F0}% in-bucket, " +
                   $"avg {(float)_idxSum / (_indexed > 0 ? _indexed : 1):F0} idx), " +
                   $"{n} (vdo,prog) groups{(_gOver + _pOver > 0 ? $", hash-over {_gOver}/{_pOver}" : "")}");
        for (int i = 0; i < n && i < 12; i++)
        {
            int s = order[i];
            _log?.Info($"DrawStatsService: [drawstats]   vdo=0x{(ulong)_gVdo[s]:x} prog={_gProg[s]}: " +
                       $"{(float)_gDraws[s] / f:F1} draws/frame, {_gItems[s]} items, avg {(float)_gIdx[s] / _gDraws[s]:F0} idx");
        }
        _frames = 0; _draws = 0; _indexed = 0; _bucketDraws = 0; _idxSum = 0;
        _gOver = 0; _pOver = 0;
        Array.Clear(_gKey); Array.Clear(_pKey);
    }
}
