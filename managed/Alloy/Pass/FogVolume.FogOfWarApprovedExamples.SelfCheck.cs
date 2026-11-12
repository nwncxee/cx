using System;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The volumetric fog under fog of war. Oracle: a 3 x 2 interior laid out by hand below, whose unexplored
/// tiles are (2, 0) and (1, 1): row 0 is binary 100 = 4, row 1 is binary 010 = 2.</summary>
internal static unsafe partial class FogVolume
{
    private static void FogOfWarApprovedExamples(Checker check)
    {
        DbId scene = DbId.Make((int)DbId.IndexMask, 0xABC);
        Span<DbId> made = stackalloc DbId[7];
        int n = 0;
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 3; x++)
                made[n++] = Tile(scene, x, y, fogged: (x == 2 && y == 0) || (x == 1 && y == 1), border: false);
        made[n++] = Tile(scene, 5, 1, fogged: true, border: true);
        Span<int> rows = stackalloc int[MaskEdge];

        // Given an interior area with volumetric fog on, where I have not explored two of its six tiles
        // When the fog volume is built
        // Then the air over exactly those two tiles is marked unexplored, and scatters no light
        (int w, int h) = DarkTiles(scene, rows);
        check.Expect(rows[0] == 4 && rows[1] == 2, $"E1 the unexplored tiles (2, 0) and (1, 1) are the mask's bits ({rows[0]}, {rows[1]})");
        check.Expect(w == 3 && h == 2, $"E1 the mask spans the area's own 3 x 2 tiles, not its border ring ({w} x {h})");
        int others = 0;
        for (int y = 2; y < rows.Length; y++) others |= rows[y];
        check.Expect(others == 0, "E1 no other row is marked");

        // Given the same area
        // When I explore both tiles
        // Then no air is masked: the fog is drawn as it is in an area with no fog of war
        for (int i = 0; i < n; i++) SceneDb.TileFogged[made[i].Index] = SceneDb.TileIsBorder[made[i].Index];
        (w, h) = DarkTiles(scene, rows);
        check.Expect(w == 0 && h == 0 && rows[0] == 0 && rows[1] == 0, $"E2 an explored area masks nothing ({w} x {h})");

        // Given no world scene
        // Then nothing is masked either
        SceneDb.TileFogged[made[0].Index] = true;
        (w, h) = DarkTiles(default, rows);
        check.Expect(w == 0 && rows[0] == 0, "E3 no world scene masks nothing");
        for (int i = 0; i < n; i++) { SceneDb.TileFogged[made[i].Index] = false; SceneDb.Tiles.Free(made[i]); }

        static DbId Tile(DbId scene, int gx, int gy, bool fogged, bool border)
        {
            DbId id = SceneDb.Tiles.Alloc();
            int t = id.Index;
            SceneDb.TileScene[t] = scene;
            SceneDb.TileGridX[t] = gx;
            SceneDb.TileGridY[t] = gy;
            SceneDb.TileFogged[t] = fogged;
            SceneDb.TileIsBorder[t] = border;
            return id;
        }
    }
}
