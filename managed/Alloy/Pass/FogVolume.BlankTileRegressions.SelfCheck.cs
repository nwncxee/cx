using System;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The volumetric fog over solid filler tiles. Oracle: a two-tile row laid out by hand, whose blank tile is
/// (1, 0), binary 10 = 2; and the stock surfacemat.2da's rows: Stone 4 is walked, Obscuring 2 and Nonwalk 7 are solid,
/// DeepWater 17 is neither walked nor solid.</summary>
internal static unsafe partial class FogVolume
{
    private static void BlankTileRegressions(Checker check)
    {
        check.Expect(TileBuild.Blank(Walk(2, 7, 7)), "a walkmesh of solid faces alone is a blank tile");
        check.Expect(!TileBuild.Blank(Walk(7, 4, 7)), "one walked face and the tile is not blank");
        check.Expect(!TileBuild.Blank(Walk(7, 17)), "deep water is not walked, and is not blank either: fog stands over a sea");
        check.Expect(!TileBuild.Blank(Walk()), "a walkmesh with no face says nothing, and is not blank");

        DbId scene = DbId.Make((int)DbId.IndexMask, 0xABD);
        DbId floor = SceneDb.Tiles.Alloc(), filler = SceneDb.Tiles.Alloc();
        Set(floor.Index, 0, blank: false);
        Set(filler.Index, 1, blank: true);
        Span<int> rows = stackalloc int[MaskEdge];
        (int w, int h) = DarkTiles(scene, rows);
        check.Expect(rows[0] == 2 && w == 2 && h == 1, $"an explored blank tile is dark in the fog's mask ({rows[0]}, {w} x {h})");

        check.Expect(!OutsideDark(scene, false, out int gw, out int gh) && gw == 2 && gh == 1, "an exterior with no ring keeps its fog past the edge");
        check.Expect(OutsideDark(scene, true, out _, out _), "an interior with no ring has dark air past its edge");
        DbId ring = SceneDb.Tiles.Alloc();
        Set(ring.Index, -1, blank: true);
        SceneDb.TileIsBorder[ring.Index] = true;
        check.Expect(OutsideDark(scene, false, out gw, out gh) && gw == 2 && gh == 1, "a ring of blank tiles alone is dark air, in a grid still 2 x 1");
        SceneDb.TileBlank[ring.Index] = false;
        check.Expect(!OutsideDark(scene, true, out _, out _), "a ring with one real tile keeps its fog");
        SceneDb.TileIsBorder[ring.Index] = false;
        SceneDb.Tiles.Free(ring);
        SceneDb.TileBlank[filler.Index] = false;
        SceneDb.Tiles.Free(floor);
        SceneDb.Tiles.Free(filler);

        void Set(int t, int gx, bool blank)
        {
            SceneDb.TileScene[t] = scene;
            SceneDb.TileGridX[t] = gx;
            SceneDb.TileGridY[t] = 0;
            SceneDb.TileFogged[t] = false;
            SceneDb.TileIsBorder[t] = false;
            SceneDb.TileBlank[t] = blank;
        }

        static ModelDef Walk(params int[] surfaces)
        {
            var faces = new FaceDef[surfaces.Length];
            for (int i = 0; i < faces.Length; i++) faces[i].Surface = surfaces[i];
            return new ModelDef { Nodes = [new NodeDef { Kind = NodeKind.Aabb, Mesh = new MeshDef { Faces = faces } }] };
        }
    }
}
