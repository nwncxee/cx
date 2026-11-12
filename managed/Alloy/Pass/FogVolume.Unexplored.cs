using System;
using CX.Alloy.Db;

namespace CX.Alloy;

/// <summary>The fog of war as the volumetric fog reads it: one bit per tile of the area's grid, so the air over a
/// tile the player has not explored scatters no light and reads as black as the tile under it.</summary>
internal static unsafe partial class FogVolume
{
    /// <summary>The widest tile grid the mask covers, tiles a side: an area's largest edge, and the bits of one row.</summary>
    internal const int MaskEdge = 32;

    /// <summary>Fill <paramref name="rows"/> with <paramref name="scene"/>'s fog of war -- bit x of row y set where tile
    /// (x, y) is unexplored -- and answer the grid's size in tiles. Zero when no tile is hidden: nothing to mask.</summary>
    internal static (int W, int H) Unexplored(DbId scene, Span<int> rows)
    {
        rows.Clear();
        if (scene.IsNull) return default;
        int w = 0, h = 0;
        bool any = false;
        for (int t = 1; t < SceneDb.Tiles.High; t++)
        {
            if (!SceneDb.Tiles.LiveAt(t) || SceneDb.TileScene[t] != scene || SceneDb.TileIsBorder[t]) continue;
            int gx = SceneDb.TileGridX[t], gy = SceneDb.TileGridY[t];
            if ((uint)gx >= MaskEdge || (uint)gy >= (uint)rows.Length) continue;
            w = Math.Max(w, gx + 1);
            h = Math.Max(h, gy + 1);
            if (!SceneDb.TileFogged[t]) continue;
            rows[gy] |= 1 << gx;
            any = true;
        }
        return any ? (w, h) : default;
    }
}
