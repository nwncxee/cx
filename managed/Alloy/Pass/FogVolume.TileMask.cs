using System;
using CX.Alloy.Db;

namespace CX.Alloy;

/// <summary>The tiles whose air the volumetric fog leaves dark, one bit per tile of the area's grid: a tile still
/// under fog of war, and a blank one (<see cref="TileBuild.Blank"/>) -- the solid filler an area is padded with.</summary>
internal static unsafe partial class FogVolume
{
    /// <summary>The widest tile grid the mask covers, tiles a side: an area's largest edge, and the bits of one row.</summary>
    internal const int MaskEdge = 32;

    /// <summary>Fill <paramref name="rows"/> with <paramref name="scene"/>'s dark tiles -- bit x of row y set where tile
    /// (x, y) is unexplored or blank -- and answer the grid's size in tiles. Zero when no tile is dark: nothing to mask.</summary>
    internal static (int W, int H) DarkTiles(DbId scene, Span<int> rows)
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
            if (!SceneDb.TileFogged[t] && !SceneDb.TileBlank[t]) continue;
            rows[gy] |= 1 << gx;
            any = true;
        }
        return any ? (w, h) : default;
    }

    /// <summary>Is the air past the edge of <paramref name="scene"/>'s grid dark, and how large is the grid? Dark where
    /// the border ring is blank tiles alone, or where an <paramref name="interior"/> has no ring: nothing stands there.</summary>
    internal static bool OutsideDark(DbId scene, bool interior, out int w, out int h)
    {
        w = h = 0;
        if (scene.IsNull) return false;
        bool ring = false, lit = false;
        for (int t = 1; t < SceneDb.Tiles.High; t++)
        {
            if (!SceneDb.Tiles.LiveAt(t) || SceneDb.TileScene[t] != scene) continue;
            if (SceneDb.TileIsBorder[t]) { ring = true; lit |= !SceneDb.TileBlank[t]; continue; }
            int gx = SceneDb.TileGridX[t], gy = SceneDb.TileGridY[t];
            if ((uint)gx >= MaskEdge || (uint)gy >= MaskEdge) continue;
            w = Math.Max(w, gx + 1);
            h = Math.Max(h, gy + 1);
        }
        return w > 0 && (ring ? !lit : interior);
    }
}
