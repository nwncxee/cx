using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The area's HEIGHT STEP, the one engine fact the CX tile build cannot derive, read as a FIELD
/// (<see cref="EngineLayout.TileSetHeightTransition"/>, per-build) where the area names its tileset --
/// <c>CNWTileSetManager::GetTileSet</c>, which <c>CNWCArea::LoadArea</c> calls before the spawn loop, since
/// <c>Scene::SpawnTile</c> names no tileset. Chained read-only: <c>ModelLoad</c> owns the symbol.</summary>
internal static unsafe class TileSetSeam
{
    private static TileSetGetFn* _slot;
    private static float _last;

    /// <summary>Chain the tileset resolve. False = the symbol or the layout row is missing, which stands DB
    /// mode's tile build down rather than letting every tile refuse one at a time with no cause named.</summary>
    internal static bool Install(ExtenderLog log, HookService hooks)
    {
        if (!EngineLayout.Require(log, "TileSetSeam", "CNWTileSet")) return false;
        _slot = hooks.ChainTileSetGet(&OnGetTileSet, Priority);
        if (_slot == null)
        {
            log.Always("TileSetSeam: CNWTileSetManager::GetTileSet did not chain -- the area's height step "
                     + "cannot be read, so EVERY tile spawn would refuse. DB mode's tile build stands down.");
            return false;
        }
        return true;
    }

    /// <summary>Below <c>ModelLoad</c>'s, which serves the call: this one only observes the answer.</summary>
    private const int Priority = 5;

    [UnmanagedCallersOnly]
    private static IntPtr OnGetTileSet(IntPtr mgr, IntPtr resref)
    {
        IntPtr tileset = (*_slot)(mgr, resref);
        if (tileset != IntPtr.Zero && Renderer.On)
        {
            float step = new CNWTileSet(tileset).HeightTransition;
            if (step != _last)
            {
                _last = step;
                TileBuild.SetTileset(step);
            }
        }
        return tileset;
    }
}
