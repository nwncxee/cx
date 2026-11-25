namespace CX.Framework;

/// <summary>The rows of <see cref="EngineLayout"/> an area's tiles are read through: the area's tile array and tileset,
/// each tile's id, and the corner and edge terrain its tile data names.</summary>
public static unsafe partial class EngineLayout
{
    // Every displacement below is IDENTICAL on both builds. Linux: CNWCArea::LoadArea @0x70e010 stores the set at +0x290
    // and the width*height*0x60 tile array at +0x298, CNWCArea::GetTile(int,int) @0x7045d0 indexes it, CNWTile::SetID @0x632550
    // writes +0x14, CNWTileData::GetCornerType @0x632ee0 and GetEdgeType @0x6334c0 read the strings. Windows: LoadArea
    // rva 0x7dba70, SetID rva 0x2c58a0, GetCornerType rva 0x2c5d20 and GetEdgeType rva 0x2c5dc0 read the same.

    /// <summary>CNWCArea -&gt; the tileset its tiles index, set by <c>LoadArea</c> from the area's tileset resref.</summary>
    public static readonly int AreaTileSet = Field<CX.Engine.CNWTileSet>("CNWCArea", "tileSet");
    /// <summary>CNWCArea -&gt; its tiles, width times height records laid row by row; null before a load.</summary>
    public static readonly int AreaTiles = Field<CX.Engine.CNWTile>("CNWCArea", "tiles");
    /// <summary>CNWTile: one record of the area's tile array.</summary>
    public static readonly int TileStride = Field("CNWTile", "stride");
    /// <summary>CNWTile -&gt; its id in the tileset, the index <c>CNWTileSet::GetTileData</c> takes.</summary>
    public static readonly int TileId = Field<int>("CNWTile", "id");
    /// <summary>CNWTileData -&gt; its four corners' terrain names as the .set spells them; a raised one ends in <c>+</c>.</summary>
    public static readonly int TileDataCorners = Field<CX.Engine.ExoStringRec>("CNWTileData", "corners", count: 4);
    /// <summary>CNWTileData -&gt; its four edges' crosser names (<c>Stream</c>, <c>Road</c>); empty where none crosses.</summary>
    public static readonly int TileDataEdges = Field<CX.Engine.ExoStringRec>("CNWTileData", "edges", count: 4);
}
