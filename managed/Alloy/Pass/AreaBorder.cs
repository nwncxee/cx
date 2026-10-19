using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The area border ring, widened. The engine pads every area with five tiles of unwalkable edge
/// geometry (<c>CNWCArea::LoadBorder</c> @0x70a5c0, hard-coded strip loops); this widens the WINDOW that tells
/// the database's tile build a spawn is a ring tile, out to <c>border</c> tiles (<c>cx.cfg</c>, default 15).
/// Sound because the edge model comes from the nearest real tile's types -- widened, not re-derived.</summary>
internal static unsafe class AreaBorder
{
    /// <summary>The engine's own ring: LoadBorder walks <c>-5..-1</c> past each near edge and <c>dim..dim+4</c>
    /// past each far one, and LoadArea sizes the BSP with <c>InitBSP(w, h, 5)</c>.</summary>
    internal const int EngineRing = 5;
    internal const int DefaultRing = 15;
    /// <summary><c>CNWTile::TILE_WIDTH</c> / <c>TILE_LENGTH</c> @0x1311850 = 10.0, and the half-tile the border
    /// loop adds (DAT_013ee9e8 = 5.0): grid <c>(i, j)</c> spawns at <c>(i*10+5, j*10+5)</c>.</summary>
    internal const float TileSize = 10f, TileHalf = 5f;

    private static ExtenderLog? _log;
    private static int _ring = DefaultRing;
    /// <summary>The CNWCArea whose LoadBorder/ReloadBorder is on the stack, else zero: the only window in
    /// which a SpawnTile or DeleteTile is a ring tile.</summary>
    private static IntPtr _area;
    private static int _areas;
    private static AreaLoadBorderFn _origLoad;
    private static AreaReloadBorderFn _origReload;

    internal static int Ring => _ring;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // Restart knob: Boot -- the ring is binned at area load from what this session started with.
        _ring = Renderer.Border.Boot;
        if (_ring == EngineRing) { log.Info("AreaBorder: alloy.border=5 is the engine's own ring -- nothing to widen"); return; }
        _origLoad = hooks.InstallAreaLoadBorder(&OnLoadBorder);
        _origReload = hooks.InstallAreaReloadBorder(&OnReloadBorder);
        if (_origLoad == null || _origReload == null)
        {
            // No window means no way to tell a ring spawn from a real tile, so the ring stays the engine's.
            _ring = EngineRing;
            log.Info("AreaBorder: a border hook did not install -- the ring stays the engine's");
            return;
        }
        log.Info($"AreaBorder: armed -- border ring {_ring} tiles (cx.cfg border=N)");
    }

    [UnmanagedCallersOnly]
    private static int OnLoadBorder(IntPtr area, IntPtr screen, float progress)
    {
        _areas++;
        _area = area;
        CX.Alloy.Db.TileBuild.BorderWindow(true);   // CX-116: the same window widens the DB ring
        int r = _origLoad(area, screen, progress);
        CX.Alloy.Db.TileBuild.BorderWindow(false);
        _area = IntPtr.Zero;
        return r;
    }

    [UnmanagedCallersOnly]
    private static void OnReloadBorder(IntPtr area)
    {
        _area = area;
        CX.Alloy.Db.TileBuild.BorderWindow(true);
        _origReload(area);
        CX.Alloy.Db.TileBuild.BorderWindow(false);
        _area = IntPtr.Zero;
    }

    /// <summary>The outward axes a ring tile replays along: -1/+1 on x when it sits on the engine ring's
    /// outermost west/east column, likewise on y, else 0. Only the outermost tile of a strip replays, and a
    /// corner tile replays along both -- which is what makes every outer cell the replay of exactly ONE
    /// engine tile (its clamp onto the engine ring). The database's tile build asks it per spawn.</summary>
    internal static void Steps(Vector3 p, int w, int h, out int sx, out int sy)
    {
        int ix = (int)MathF.Floor((p.X - TileHalf) / TileSize + 0.5f);
        int iy = (int)MathF.Floor((p.Y - TileHalf) / TileSize + 0.5f);
        sx = ix == -EngineRing ? -1 : ix == w + EngineRing - 1 ? 1 : 0;
        sy = iy == -EngineRing ? -1 : iy == h + EngineRing - 1 ? 1 : 0;
    }

    internal static string Describe() => $"ring {_ring} ({_areas} areas)";

    internal static string Stats() => _ring == EngineRing ? "" : $"border {Describe()}";

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        // Replay the engine's own 5-ring of a 4x3 area out to ring 7 and count how many times each outer cell
        // is produced: exactly once each, and never a cell the engine already spawned.
        const int w = 4, h = 3, ring = 7, extra = ring - EngineRing;
        var hits = new int[w + 2 * ring, h + 2 * ring];
        for (int ix = -EngineRing; ix < w + EngineRing; ix++)
            for (int iy = -EngineRing; iy < h + EngineRing; iy++)
            {
                if (ix >= 0 && ix < w && iy >= 0 && iy < h) continue;   // a real tile, not the engine's ring
                Steps(new Vector3(ix * TileSize + TileHalf, iy * TileSize + TileHalf, 0f), w, h, out int sx, out int sy);
                for (int i = 0; i <= (sx != 0 ? extra : 0); i++)
                    for (int j = 0; j <= (sy != 0 ? extra : 0); j++)
                        if (i != 0 || j != 0) hits[ix + sx * i + ring, iy + sy * j + ring]++;
            }
        bool once = true, engineUntouched = true;
        for (int x = -ring; x < w + ring; x++)
            for (int y = -ring; y < h + ring; y++)
            {
                bool outer = x < -EngineRing || y < -EngineRing || x >= w + EngineRing || y >= h + EngineRing;
                int n = hits[x + ring, y + ring];
                if (outer && n != 1) once = false;
                if (!outer && n != 0) engineUntouched = false;
            }
        check.Expect(once, "every outer ring cell is the replay of exactly one engine tile");
        check.Expect(engineUntouched, "no replay lands on a cell the engine spawns itself");
        Steps(new Vector3(TileHalf, TileHalf, 0f), w, h, out int cx, out int cy);
        check.Expect(cx == 0 && cy == 0, "a real tile has no outward axis");
        if (check.Ok) log.Info("AreaBorder.SelfCheck: ok");
    }
}
