using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary><b>CX builds the area's tiles</b>, replacing the engine's chain from <c>Scene::SpawnTile</c>
/// @0x474470 to <c>SceneBSPAddTileData</c> @0x47cd00, plus the tile lights, flames, texture swap and fog of war
/// (the walkmesh stays the game's). Each method answers one <c>Scene</c> vtable slot and is COLD -- area load or
/// a tile message. The merge runs once per tile MODEL in MODEL space: a placement is one matrix over it.</summary>
public static partial class TileBuild
{
    /// <summary><c>CNWTile::TILE_WIDTH</c>/<c>TILE_LENGTH</c> = 10.0 and the half-tile the grid-to-world
    /// conversion adds: <c>CNWTile::SetPosition</c> @0x732560 writes
    /// <c>pos = (x*10+5, y*10+5, heightTransition*height)</c>.</summary>
    public const float TileSize = 10f, TileHalf = 5f;

    /// <summary>The engine's own border ring (<c>CNWCArea::LoadBorder</c> walks -5..-1 and dim..dim+4).</summary>
    public const int EngineRing = 5;

    /// <summary>Reveal factors per second: the <c>DeltaT * 4.0</c> of the fog-of-war step in the BSP visitor
    /// (@0x481000), the same rate the two fade buckets move at.</summary>
    private const float RevealRate = 4f;

    /// <summary><c>tilefade 1</c> -- the class <c>CheckFadePlane</c> @0x47bf30 tests. 2 is the cap and 4 the
    /// no-shadow class; <c>ProcessStaticParts</c> files those two in side lists instead.</summary>
    private const int TileFadeFades = 1;

    /// <summary>The light-node controller ids, MEASURED off <c>MdlNodeLight::InternalParseField</c> @0x54baf0:
    /// the id the parser passes is the Linux <c>PartLight</c> field offset the sample lands in, which is why
    /// each one matches its <c>EngineLayout</c> row (<c>colour</c> 0x64, <c>range</c> 0x70, <c>rangeAlt</c>
    /// 0x78, <c>intensity</c> 0xa4).</summary>
    private const int CtlColour = 100, CtlRadius = 112, CtlShadowRadius = 120, CtlMultiplier = 164;

    /// <summary>What <c>Scene::SetTileLights</c> @0x55edf0 writes beside the colour, for each of the two main
    /// lights: <c>range</c>, <c>rangeAlt</c>, <c>castShadows</c> = 1 and <c>priority</c> = 4. Main light 1 gets
    /// 10/12, main light 2 gets 5/8 -- the two literal pairs in that function.</summary>
    private const float Main1Range = 10f, Main1RangeAlt = 12f, Main2Range = 5f, Main2RangeAlt = 8f;
    /// <inheritdoc cref="Main1Range"/>
    private const int MainPriority = 4;

    /// <summary>The eleven tile animations <c>CNWCTile::PlayAnimation</c> @0x80c770 names, in its switch order
    /// (every address in this file is an nm address, i.e. Ghidra - 0x100000). The game layer forwards the NAME
    /// to <c>Gob::PlayAnimation</c>, so CX keeps the enables instead -- <c>Day</c>/<c>Night</c> at load,
    /// <c>AnimLoop01..03</c> per the record's three bytes -- and <see cref="AnimName"/> inverts an index.</summary>
    private static readonly string[] AnimNames =
        ["tiledefault", "AnimLoop01", "AnimLoop02", "AnimLoop03", "on", "off", "fade", "Day", "Night", "Day2Night", "Night2Day"];

    /// <summary>(scene handle, grid cell) -&gt; tile slot: CX's <c>SceneBSPGetTileData</c>. Cold path only.</summary>
    private static readonly Dictionary<(uint Scene, int X, int Y), int> _byCell = new();

    private static int _spawned, _deleted, _replayed, _refusedModel, _refusedTable, _refusedLights;
    private static int _refusedBig, _refusedArena, _collisions, _outOfOrder, _cellTaken, _lightSpill, _refusedHeight;
    private static float _cellSize = 10f;

    private static int _arenaAsks, _planBuilds, _mergedNodes, _litRegistered, _swaps;
    private static int _gridW, _gridH, _ring = EngineRing;
    private static float _heightTransition;
    private static bool _borderWindow;

    /// <summary>Tile placements the DB holds, and the models they came from.</summary>
    public static int Placements => _spawned - _deleted;
    /// <inheritdoc cref="Placements"/>
    public static int Models => _plans.Count;
    /// <summary>Merged meshes offered to the arena: one per distinct tile mesh, never one per placement. The
    /// mesh census and <c>MeshChart</c>'s <c>built</c> count are held to this number.</summary>
    public static int ArenaAsks => _arenaAsks;
    public static int LightsRegistered => _litRegistered;

    // ---- Scene vtable: the grid ----

    /// <summary>Linux <c>Scene</c> +0x30 / Windows +0x28, <c>InitBSP(int, int, int, bool, float)</c>, which
    /// <c>CNWCArea::LoadArea</c> calls <c>(w, h, 5, true, 10.0f)</c>. There is no BSP -- the visible set is the
    /// grid over the per-tile bounds -- so this records the grid and the ring, widened to <c>alloy.border</c>.
    /// <paramref name="cellSize"/> is the BSP CELL SIZE, not the height step (<see cref="SetTileset"/>).</summary>
    public static void InitGrid(DbId scene, int w, int h, int border, float cellSize)
    {
        _gridW = w;
        _gridH = h;
        _cellSize = cellSize;
        _ring = border > 0 && border < AreaBorder.Ring ? AreaBorder.Ring : border;
        if (border > 0 && Math.Max(w, h) >= LargeArea) _ring = LargeRing;
    }

    /// <summary>An area this many tiles along its longer side keeps only <see cref="LargeRing"/> tiles of
    /// border, trimming even the engine's own five: on a 27x24 city the 15-tile ring was 2430 of 3078
    /// placements, where three already fill the view from inside a big area.
    /// ponytail: two constants, not knobs; promote them if a builder needs a wider ring on a big map.</summary>
    internal const int LargeArea = 20, LargeRing = 3;
    /// <summary>Ring tiles the engine asked for past <see cref="LargeRing"/>, not placed.</summary>
    private static int _ringSkipped;

    /// <summary>Rings past the trimmed ring, by grid cell: the distance outside the area on the far axis.</summary>
    private static bool PastRing(Vector3 pos)
    {
        Cell(pos, out int gx, out int gy);
        int dx = gx < 0 ? -gx : gx >= _gridW ? gx - _gridW + 1 : 0;
        int dy = gy < 0 ? -gy : gy >= _gridH ? gy - _gridH + 1 : 0;
        return Math.Max(dx, dy) > _ring;
    }

    /// <summary>The area's tileset height step: <c>CNWTileSet::GetHeightTransition</c> @0x5e10c0, the
    /// multiplier <c>CNWCArea::LoadArea</c> passes to <c>CNWCTile::SetPosition</c> for the record's height
    /// index. There is NO sane default -- a wrong step puts every raised tile at the wrong altitude -- so
    /// until the seam calls this, every spawn refuses loudly and the step is reported unset.</summary>
    public static void SetTileset(float heightTransition)
    {
        if (heightTransition > 0f) { _heightTransition = heightTransition; return; }
        Refuse($"CNWTileSet::GetHeightTransition answered {heightTransition} -- a tile's height index cannot be placed");
    }

    /// <summary>The height step, or 0 when the seam has not named one yet.</summary>
    public static float HeightTransition => _heightTransition;

    /// <summary>Open or close the <c>LoadBorder</c>/<c>ReloadBorder</c> window: inside it a spawn is a ring
    /// tile and replays outward to the widened ring. <see cref="AreaBorder"/> calls this from inside its own
    /// window, so the ring needs no second hook; when it stands down the ring is the engine's five tiles and
    /// the window staying shut is the right answer.</summary>
    public static void BorderWindow(bool open) => _borderWindow = open;

    /// <summary>The scene handle the live comparison places its throwaway tiles under
    /// (<see cref="TileCompare"/>). A real allocation, so it cannot collide with a scene the seam makes --
    /// <c>DbId.Make(MaxScenes - 1, 0)</c> would have, the moment 255 scenes existed.</summary>
    public static DbId CompareScene
    {
        get
        {
            if (!SceneDb.Scenes.Valid(_compareScene)) _compareScene = SceneDb.Scenes.Alloc();
            return _compareScene;
        }
    }
    private static DbId _compareScene;

    /// <summary>The world position of a grid cell, as <c>CNWTile::SetPosition</c> computes it.</summary>
    public static Vector3 CellPos(int gx, int gy, int height)
        => new(gx * TileSize + TileHalf, gy * TileSize + TileHalf, _heightTransition * height);

    /// <summary>The grid cell a world position names (the inverse of <see cref="CellPos"/>, and the key
    /// <c>SceneBSPGetTileData</c> resolves a <c>Vector const&amp;</c> with).</summary>
    public static void Cell(Vector3 p, out int gx, out int gy)
    {
        gx = (int)MathF.Floor((p.X - TileHalf) / TileSize + 0.5f);
        gy = (int)MathF.Floor((p.Y - TileHalf) / TileSize + 0.5f);
    }

    /// <summary>The tile placement at <paramref name="p"/>, or -1. CX's <c>SceneBSPGetTileData</c>.</summary>
    public static int At(DbId scene, Vector3 p)
    {
        Cell(p, out int gx, out int gy);
        return At(scene, gx, gy);
    }

    /// <inheritdoc cref="At(DbId, Vector3)"/>
    public static int At(DbId scene, int gx, int gy)
        => _byCell.TryGetValue(CellKey(scene, gx, gy), out int t) && SceneDb.Tiles.LiveAt(t) ? t : -1;

    /// <summary>A cell of one scene. The FULL handle, generation included: a scene index is recycled at every area
    /// change, and an index-only key let the old area's late teardown remove the new area's cells and statics.</summary>
    private static (uint Scene, int X, int Y) CellKey(DbId scene, int gx, int gy) => (scene.Value, gx, gy);

    // ---- Scene vtable 0x38: SpawnTile ----

    /// <summary>Linux <c>Scene</c> +0x38 / Windows +0x30, <c>SpawnTile(char const*, Vector const&amp;, int, int,
    /// Quaternion*)</c>: the tile at <paramref name="pos"/> from <paramref name="model"/>,
    /// <paramref name="orient"/> quarter turns about +Z, non-zero <paramref name="border"/> a ring tile. <b>A
    /// non-null <paramref name="rot"/> is a STATIC placeable</b>: off the grid, a plain object record. -1 refuses.</summary>
    public static int SpawnTile(DbId scene, string model, Vector3 pos, int orient, int border, Quaternion? rot = null)
    {
        ModelDef? def = ModelCache.Acquire(model);
        if (def == null) { _refusedModel++; Refuse($"no model '{model}'"); return -1; }
        WarmMaterials?.Invoke(def);
        int t = -1;
        if (rot != null) { StaticObject = PlaceStatic(scene, def, pos, rot.Value); t = StaticObject.IsNull ? -1 : StaticTile; }
        else { StaticObject = default; t = PlaceWithRing(scene, def, pos, orient, border, null); }
        // Acquire's reference served the lookup; each placement took one of its own in Hold.
        ModelCache.Release(def);
        return t;
    }

    /// <summary>The object a grid tile hands the game layer as its gob, or null: <c>SpawnPartsForTile</c>
    /// @0x463450 makes one only for a non-border tile whose model has a <c>&lt;resref&gt;a</c> node, and
    /// <c>CNWCTile</c> plays its Day/Night/AnimLoop animations on it (<c>CNWCTile::PlayAnimation</c>
    /// @0x80c770).</summary>
    public static DbId AnimatedTileObject(int t)
    {
        if ((uint)t >= (uint)SceneDb.MaxTiles || SceneDb.TileIsBorder[t]) return default;
        ModelDef? def = SceneDb.TileModel[t];
        if (def == null || def.Find(def.Name + "a") < 0) return default;
        DbId o = SceneDb.TileObj[t];
        return SceneDb.Objects.Valid(o) ? o : default;
    }

    /// <summary>Reads the <c>.mtr</c>s a spawned model's meshes name before its part tree is built, so a
    /// bitmap that names a material resolves to it rather than to a missing texture of that name (white, for the
    /// session: a city's static tents, 2026-10-09). Set by the resource layer (<c>MtrFeed</c>), as
    /// <see cref="MtrDef.Bytes"/> is; null draws the mesh's own names.</summary>
    public static Action<ModelDef>? WarmMaterials;

    /// <summary>The object the last <see cref="SpawnTile"/> made when it was a STATIC placeable (a non-null
    /// <c>Quaternion*</c>), else null. The seam has to hand the game layer a <c>CAurObject*</c> for it, which
    /// a tile slot cannot name -- and -1 must not read as a refusal when the spawn succeeded.</summary>
    public static DbId StaticObject { get; private set; }

    /// <summary>What <see cref="SpawnTile"/> returns for a successful static placeable: not a tile slot, and
    /// distinct from the -1 a refusal returns.</summary>
    public const int StaticTile = -2;

    /// <summary>One <see cref="ModelCache"/> reference per PLACEMENT, so a tile model stays resident
    /// exactly as long as something stands on it. A model the cache does not hold -- the self-checks' -- is
    /// not referenced, and is told apart by identity rather than by a flag.</summary>
    private static void Hold(ModelDef def)
    {
        if (ReferenceEquals(ModelCache.FindModel(def.Name), def)) ModelCache.Acquire(def.Name);
    }

    /// <inheritdoc cref="Hold"/>
    private static void Unhold(ModelDef? def)
    {
        if (def != null && ReferenceEquals(ModelCache.FindModel(def.Name), def)) ModelCache.Release(def);
    }

    /// <summary>The mesh census -- meshes against placements for a tile area: one note per merged mesh per
    /// placement. Only when <see cref="PartTree.Build"/> did NOT build a part range; when it does, the part
    /// tree counts its own placements and this would double them.</summary>
    private static void Census(MergePlan plan, bool ours, bool add)
    {
        if (!ours) return;
        foreach (MeshDef m in plan.Meshes)
            if (add) ModelCache.NotePlacement(m); else ModelCache.DropPlacement(m);
    }

    /// <summary>The <c>Quaternion*</c> path: a static object placed into whatever cell it stands in. One
    /// object record, the model's merge plan, and its lights filed with no tile (so a tile's own light run
    /// stays contiguous and <see cref="SetTileLights"/> cannot re-colour a placeable's lamp).</summary>
    public static DbId PlaceStatic(DbId scene, ModelDef def, Vector3 pos, Quaternion rot)
    {
        MergePlan plan = Plan(def);
        Matrix4x4 place = Matrix4x4.CreateFromQuaternion(rot);
        place.Translation = pos;
        DbId obj = NewObject(scene, def, pos, rot, place, tile: false);
        if (obj.IsNull) return default;
        Hold(def);
        Census(plan, SceneDb.ObjPartCount[obj.Index] == 0, add: true);
        RegisterLights(default, obj, plan, place, null);
        Cell(pos, out int gx, out int gy);
        var key = CellKey(scene, gx, gy);
        if (!_statics.TryGetValue(key, out List<DbId>? list)) _statics[key] = list = new List<DbId>(2);
        list.Add(obj);
        _statics0++;
        return obj;
    }

    /// <summary>Statics by cell: what a <see cref="DeleteTile"/> at that cell takes with the tile, as the
    /// engine's own teardown does (their <c>Gob+0x1a8</c> is that cell's <c>TileData</c>).</summary>
    private static readonly Dictionary<(uint Scene, int X, int Y), List<DbId>> _statics = new();
    private static int _statics0;

    /// <summary>One object record for a placed model: the columns every placement shares.</summary>
    private static DbId NewObject(DbId scene, ModelDef def, Vector3 pos, Quaternion rot, in Matrix4x4 place,
                                 bool tile, bool walkmesh = false)
    {
        DbId obj = SceneDb.Objects.Alloc();
        if (obj.IsNull) { _refusedTable++; Refuse("the object table is full -- a placement has no object record"); return default; }
        int o = obj.Index;
        DbFeed.InitObject(o);
        // ...and the animation runtime's per-object state, which is where the COMPOSE's staleness lives. A
        // recycled slot keeps the previous tenant's _from* snapshot and compose generation, so without this
        // ComposeIndex early-returns on a brand-new object and its parts keep the OLD tenant's world
        // matrices -- which is a static whose hit box sits wherever the last object stood. It is why the
        // first area load worked (fresh slots) and every area after it did not.
        DbAnim.ObjectCreated(obj);
        SceneDb.ObjKindOf[o] = ObjKind.Model;
        SceneDb.ObjModel[o] = def;
        SceneDb.ObjPos[o] = pos;
        SceneDb.ObjRot[o] = rot;
        SceneDb.ObjScale[o] = 1f;
        SceneDb.ObjWorld[o] = place;
        // Never moves once placed, so a visible-set entry rather than a per-frame route. ObjFlag.Tile is
        // receiver class 1 and the LOD/weld skip, which is right for tileset geometry and WRONG for a Static
        // placeable, so the caller says which this is (seam-feed's ObjFlag contract).
        SceneDb.ObjFlags[o] = (tile ? ObjFlag.Tile : ObjFlag.None) | ObjFlag.Static;
        SceneDb.ObjRunRoot[o] = obj;
        MergePlan plan = Plan(def);
        Vector3 c = Vector3.Transform((plan.BMin + plan.BMax) * 0.5f, place);
        SceneDb.ObjSphere[o] = new Vector4(c, (plan.BMax - plan.BMin).Length() * 0.5f);
        // CX-114's tile path: reference expansion, the collapse and the merge, in the order
        // SpawnPartsForTile and Scene::SpawnTile run them -- but with an IDENTITY placement, so the merged
        // geometry stays model-space and one arena copy serves every placement (CONTRACTS.md). The
        // <resref>a subtree is the animated half the collapse keeps under the root.
        // A walkmesh model is one `node aabb` and nothing else: no merge, no collapse, no `<resref>a`
        // subtree to keep -- the plain tree builder is the whole of it.
        bool built = walkmesh ? PartTree.Build(obj, def) : PartTree.BuildTile(obj, def, def.Name + "a");
        if (!built) { SceneDb.ObjPartFirst[o] = 0; SceneDb.ObjPartCount[o] = 0; }
        // Vanilla never RENDERS a walkmesh: it reaches the BSP cell's +0x90 slot through SceneBSPAddPart's
        // AsPartAABB branch and is only ever hit-tested. Invisible keeps it out of the visible set (the feed's
        // own check: "an invisible object yields nothing") while the hit test's STATIC pass, which applies no
        // object gates at all, still sees it -- which is exactly vanilla's split.
        if (walkmesh) SceneDb.ObjVisible[o] = false;
        for (int k = 0; k < SceneDb.ObjPartCount[o]; k++)
        {
            int pk = SceneDb.ObjPartFirst[o] + k;
            SceneDb.PartFade[pk] = 1f;
            if (SceneDb.PartKindOf[pk] == PartKind.Aabb) _aabbParts++;
        }
        _partsMade += SceneDb.ObjPartCount[o];
        if (Plan(def).AabbNodes > 0) _aabbWanted++;
        // The create contract, in its order (CONTRACTS.md "LIFECYCLE WIRING"), and all three were missing:
        //   * the animation runtime has to know the record exists before anything poses it;
        //   * the hit index files it as STATIC -- a tile and a static placeable are the area's static set,
        //     which is what a click-to-move floor cast and the camera's spring arm test against;
        //   * and it JOINS the scene. Writing ObjScene by hand (as this did) leaves the scene with no
        //     members, so GetAurSceneObjectCount answers 0, the draw-order slots are no-ops and -- the one
        //     that bites -- a scene destroy frees no tile at all, because its sweep walks the member list.
        // JoinNew, not Join: the record was allocated three lines up, so the membership test is known.
        DbAnim.ObjectCreated(obj);
        HitFeed.NoteObject(obj, isStatic: true);
        DbScene.JoinNew(scene, obj);
        if (!scene.IsNull && SceneDb.ObjScene[o].Value != scene.Value) NoteNotJoined(scene, obj, def);
        SceneDb.Objects.Mark(o, Dirty.Created | Dirty.Xform | Dirty.Tree);
        return obj;
    }

    /// <summary>The spawn with the model in hand: <see cref="Place"/> plus the ring replay. This is the entry
    /// a tile build goes through; <see cref="Place"/> itself places exactly one tile and is what the live
    /// comparison uses, so a comparison never widens the ring.</summary>
    public static int PlaceWithRing(DbId scene, ModelDef def, Vector3 pos, int orient, int border, Quaternion? rot = null)
    {
        // the engine's border answers 0 to the game layer either way, so a skipped ring tile is invisible to it
        if (border != 0 && _ring < EngineRing && PastRing(pos)) { _ringSkipped++; return -1; }
        int t = Place(scene, def, pos, orient, border, rot);
        if (t < 0) return -1;
        if (_borderWindow && border != 0) ReplayOutward(scene, def, pos, orient, border, rot);
        return t;
    }

    /// <summary>Place exactly one tile from a model already in hand -- no ring replay. The live comparison
    /// and <see cref="ReplayOutward"/> call this (one <see cref="ModelCache"/> reference serves the whole strip).</summary>
    public static int Place(DbId scene, ModelDef def, Vector3 pos, int orient, int border, Quaternion? rot = null)
    {
        if (_heightTransition <= 0f)
        { _refusedHeight++; Refuse("no tileset height step yet (TileBuild.SetTileset) -- a tile cannot be placed"); return -1; }
        Cell(pos, out int gx, out int gy);
        var cellKey = CellKey(scene, gx, gy);
        // The engine reuses the cell's TileData rather than making a second one (SpawnTile asks
        // SceneBSPGetTileData first), so a spawn onto an occupied cell replaces that placement here too --
        // dropping it first, because leaving it would make its record, object and lights unreachable.
        if (_byCell.TryGetValue(cellKey, out int had) && SceneDb.Tiles.LiveAt(had)) { _cellTaken++; Drop(had); }
        DbId id = SceneDb.Tiles.Alloc();
        if (id.IsNull) { _refusedTable++; Refuse($"the tile table is full at {SceneDb.MaxTiles} placements"); return -1; }
        int t = id.Index;
        MergePlan plan = Plan(def);
        Quaternion q = rot ?? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, orient * 90f * (MathF.PI / 180f));

        SceneDb.TileScene[t] = scene;
        SceneDb.TileModel[t] = def;
        SceneDb.TileGridX[t] = gx;
        SceneDb.TileGridY[t] = gy;
        SceneDb.TileHeight[t] = _heightTransition > 0f ? (int)MathF.Round(pos.Z / _heightTransition) : 0;
        SceneDb.TileOrient[t] = orient & 3;
        SceneDb.TileSetId[t] = -1;   // the area message's tile id arrives separately, in NoteRecord
        SceneDb.TilePos[t] = pos;
        SceneDb.TileIsBorder[t] = border != 0;
        SceneDb.TileFogged[t] = false;
        SceneDb.TileFade[t] = 1f;
        SceneDb.TileFadeAcc[t] = 0f;
        SceneDb.TileReveal[t] = 1f;
        SceneDb.TileReplaceTex[t] = null;
        SceneDb.TileFlame1[t] = default;
        SceneDb.TileFlame2[t] = default;

        Matrix4x4 place = Matrix4x4.CreateFromQuaternion(q);
        place.Translation = pos;
        // TileBmin/Bmax is the engine's TileData+0x58/+0x64 equivalent: the box over what lands in
        // TileData+0x30. The fade box is the tilefade-1 subset, which is what CheckFadePlane tests.
        Bounds(plan.FiledBMin, plan.FiledBMax, place, out SceneDb.TileBmin[t], out SceneDb.TileBmax[t]);
        if (plan.HasFade) Bounds(plan.FadeBMin, plan.FadeBMax, place, out SceneDb.TileFadeBmin[t], out SceneDb.TileFadeBmax[t]);
        else { SceneDb.TileFadeBmin[t] = Vector3.Zero; SceneDb.TileFadeBmax[t] = Vector3.Zero; }
        // One PLACED box per tilefade-1 part, at spawn: the occlusion test reads them every frame and must
        // not compose a matrix to do it.
        int nf = plan.FadeLo.Length;
        if (nf > SceneDb.TileFadeBoxStride)
        {
            _fadeBoxSpill++;
            Refuse($"{def.Name}: {nf} tilefade-1 parts, past the {SceneDb.TileFadeBoxStride} the fade tracks "
                 + "-- the rest do not occlude");
            nf = SceneDb.TileFadeBoxStride;
        }
        SceneDb.TileFadeBoxN[t] = (byte)nf;
        for (int f = 0; f < nf; f++)
            Bounds(plan.FadeLo[f], plan.FadeHi[f], place,
                   out SceneDb.TileFadeBoxLo[t * SceneDb.TileFadeBoxStride + f],
                   out SceneDb.TileFadeBoxHi[t * SceneDb.TileFadeBoxStride + f]);

        DbId obj = NewObject(scene, def, pos, q, place, tile: true);
        if (obj.IsNull)
        {
            // The object table refused: unwind rather than leaving a tile record with no geometry, no
            // reference released and a published fade (#15).
            SceneDb.Tiles.Free(id);
            return -1;
        }
        SceneDb.TileObj[t] = obj;
        // SpawnPartsForTile's last act before it collapses the tree: the whole part tree's textures turn by
        // orient quarter turns. Recorded on the object because the recipe is shared across orientations.
        SceneDb.ObjTexRotate[obj.Index] = (byte)(orient & 3);
        Walkmesh(scene, def, pos, q, place);
        Hold(def);
        Census(plan, SceneDb.ObjPartCount[obj.Index] == 0, add: true);

        // A BORDER tile carries no lights, no emitters and no ml*/sl* parts at all: ProcessStaticParts
        // @0x570970 deletes every non-trimesh part when its `border` argument is non-zero.
        if (border == 0)
        {
            Array.Fill(SceneDb.TileLightIds, -1, t * SceneDb.TileLightStride, SceneDb.TileLightStride);
            RegisterLights(id, obj, plan, place, _lightScratch);
            Array.Copy(_lightScratch, 0, SceneDb.TileLightIds, t * SceneDb.TileLightStride, SceneDb.TileLightStride);
        }
        else SceneDb.TileLightCount[t] = 0;
        // Compose NOW, not at the next transform pass. A tile is static and the engine casts against it in
        // the SAME message batch that built the area (CNWCArea::LoadArea's tile loop, then the creature
        // update that calls CNWCCreature::ComputeZValue @0x71d4f0), so a part whose world matrix is still
        // identity is a floor at the ORIGIN -- which is why an index-free sweep of the object table found no
        // static record covering the ruins' spawn point and the cast fell back on the 9.0 sentinel.
        DbAnim.ObjectWorld(obj);
        // ...and the hit stream's world box and grid cell, NOW. HitBounds.Refresh normally runs in the
        // fold, which is a frame away -- but the engine casts against these tiles inside the SAME message
        // batch that built the area, and a static never moves again, so a box filed a frame late is a floor
        // that does not exist for the one cast that needed it.
        if (!obj.IsNull) HitBounds.Refresh(obj.Index);
        if (border == 0) _realPlaced++;
        _byCell[cellKey] = t;
        _published[t] = 1f;
        _publishedReveal[t] = 1f;
        SceneDb.Tiles.Mark(t, Dirty.Created | Dirty.Xform);
        _spawned++;
        return t;
    }

    /// <summary>The widened ring: the outermost engine ring tile of every strip is replayed further out, to
    /// <c>alloy.border</c> tiles. Sound because the edge model is chosen per row/column from the nearest real
    /// tile, so every tile along a strip's outward axis is the same model at the same height.</summary>
    private static void ReplayOutward(DbId scene, ModelDef def, Vector3 pos, int orient, int border, Quaternion? rot)
    {
        AreaBorder.Steps(pos, _gridW, _gridH, out int sx, out int sy);
        int extra = _ring - EngineRing;
        if (extra <= 0 || (sx == 0 && sy == 0)) return;
        for (int i = 0; i <= (sx != 0 ? extra : 0); i++)
            for (int j = 0; j <= (sy != 0 ? extra : 0); j++)
            {
                if (i == 0 && j == 0) continue;
                var q = new Vector3(pos.X + sx * i * TileSize, pos.Y + sy * j * TileSize, pos.Z);
                if (Place(scene, def, q, orient, border, rot) >= 0) _replayed++;
            }
    }

    // ---- Scene vtable 0x50: ReplaceTileTexture ----

    /// <summary>Linux <c>Scene</c> +0x50 / Windows +0x48, <c>ReplaceTileTexture(Vector const&amp;, char const*,
    /// char const*)</c>: swap one texture NAME (<paramref name="oldTex"/> is the tileset's <c>"replace_tex"</c>
    /// placeholder) across the tile's meshes. Per PLACEMENT, so it leaves the shared merge plan alone: the name
    /// goes on the placement's own parts as a slot-0 override, matched by NAME, and the tile is marked.</summary>
    public static void ReplaceTileTexture(DbId scene, Vector3 pos, string newTex, string oldTex)
    {
        int t = At(scene, pos);
        if (t < 0) return;
        string? was = SceneDb.TileReplaceTex[t];
        SceneDb.TileReplaceTex[t] = string.IsNullOrEmpty(newTex) ? null : newTex;
        if (was == SceneDb.TileReplaceTex[t]) return;
        _swaps++;
        SceneDb.Tiles.Mark(t, Dirty.Texture);
        SceneDb.Touch(SceneDb.TileObj[t], Dirty.Texture);
        // An EMPTY name drops the override rather than keeping the last one: the engine's own empty-row call
        // is a no-op (AurTextureGet("") answers no bitmap and AurTextureReplaceTileReferences returns 0), so
        // there is no vanilla behaviour to match, and the recorded null has to mean what it says.
        _swapParts += Seam.ObjectTex.ReplaceByTexName(SceneDb.TileObj[t], oldTex, SceneDb.TileReplaceTex[t]);
    }

    /// <summary>Tile parts whose slot 0 the texture swap actually re-pointed. Zero with a non-zero
    /// <c>_swaps</c> means the tileset's placeholder is spelled something other than
    /// <see cref="ReplaceSlot"/> -- the swap recorded and drew nothing.</summary>
    private static int _swapParts;

    /// <summary>The placeholder name the tileset authors and the engine always passes as the texture to
    /// replace.</summary>
    public const string ReplaceSlot = "replace_tex";

    // ---- Scene vtable 0x58: DeleteTile ----

    /// <summary>Linux <c>Scene</c> +0x58 / Windows +0x50, <c>DeleteTile(Vector const&amp;)</c>. The record, its
    /// object, its part range and its lights die here and nowhere else (ADR 0028: CX produces the edge), so
    /// there is no sweep and no tombstone.</summary>
    public static void DeleteTile(DbId scene, Vector3 pos)
    {
        int t = At(scene, pos);
        if (t < 0) return;
        Drop(t);
        if (_borderWindow)
        {
            AreaBorder.Steps(pos, _gridW, _gridH, out int sx, out int sy);
            int extra = _ring - EngineRing;
            for (int i = 0; i <= (sx != 0 ? extra : 0); i++)
                for (int j = 0; j <= (sy != 0 ? extra : 0); j++)
                {
                    if (i == 0 && j == 0) continue;
                    int o = At(scene, new Vector3(pos.X + sx * i * TileSize, pos.Y + sy * j * TileSize, pos.Z));
                    if (o >= 0) Drop(o);
                }
        }
    }

    private static void Drop(int t)
    {
        if (!SceneDb.TileIsBorder[t]) _droppedReal++;
        DbId id = SceneDb.Tiles.IdAt(t);
        var cell = CellKey(SceneDb.TileScene[t], SceneDb.TileGridX[t], SceneDb.TileGridY[t]);
        if (_statics.Remove(cell, out List<DbId>? statics))
            foreach (DbId s in statics) { FreeLights(s, -1); FreeObject(s); }
        FreeLights(SceneDb.TileObj[t], t);
        FreeObject(SceneDb.TileObj[t]);
        SceneDb.TileObj[t] = default;
        SceneDb.TileModel[t] = null;
        _byCell.Remove(cell);
        SceneDb.Tiles.Free(id);
        _deleted++;
    }

    private static void FreeObject(DbId obj)
    {
        if (obj.IsNull || !SceneDb.Objects.Valid(obj)) return;
        // The destroy contract's first two steps: drop the scene's references to it SYNCHRONOUSLY (its
        // member list and the shell's count word are read by game code without waiting for a frame), then
        // tell the animation runtime before the record stops being addressable.
        DbScene.ObjectDied(obj);
        DbAnim.ObjectRetired(obj);
        // An animated tile's gob shell (AnimatedTileObject): the game only ever zeroes its copy
        // (CNWCTile::UnloadModel), so the shell is poisoned here or a late call reads a freed record.
        nint tileShell = SceneDb.ObjFacade[obj.Index];
        if (tileShell != 0) { CX.Alloy.Seam.ObjectSlots.FreeShellLists(tileShell); CX.Alloy.Seam.Facade.Retire(tileShell); }
        int fo = obj.Index, ff = SceneDb.ObjPartFirst[fo];
        for (int k = 0; k < SceneDb.ObjPartCount[fo]; k++)
            if (SceneDb.PartKindOf[ff + k] == PartKind.Aabb) _aabbFreed++;
        FreeLights(obj, -1);
        ModelDef? def = SceneDb.ObjModel[obj.Index];
        // HasPlan, not Plan: a model whose plan has already gone (an eviction) has had its census dropped
        // with it, and rebuilding the plan here would mint new MeshDefs and drop placements nobody noted.
        if (def != null && HasPlan(def)) Census(Plan(def), SceneDb.ObjPartCount[obj.Index] == 0, add: false);
        // The texture references the swap took, BEFORE the parts holding them go: this free path is its own
        // and never reached ObjectSlots.ReleaseRecord, so without this a swapped tile leaked one registry
        // reference per part at every area change -- a slot pinned resident for the session.
        CX.Alloy.Seam.ObjectTex.Free(obj);
        PartTree.Free(obj);
        Unhold(def);
        SceneDb.ObjModel[obj.Index] = null;
        SceneDb.ObjKindOf[obj.Index] = ObjKind.Free;
        SceneDb.Objects.Free(obj);
    }

    // ---- bounds, stats, refusals ----

    /// <summary>A model-space box placed by a transform: the eight corners' world extent. The engine's
    /// equivalent is the union <c>SpawnTile</c> builds into <c>TileData+0x58</c>/<c>+0x64</c> over its trimesh
    /// parts' world boxes -- the same box, from the model rather than from baked vertices.</summary>
    public static void Bounds(Vector3 lo, Vector3 hi, in Matrix4x4 m, out Vector3 outLo, out Vector3 outHi)
    {
        var a = new Vector3(float.MaxValue);
        var b = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var p = new Vector3((c & 1) == 0 ? lo.X : hi.X, (c & 2) == 0 ? lo.Y : hi.Y, (c & 4) == 0 ? lo.Z : hi.Z);
            p = Vector3.Transform(p, m);
            a = Vector3.Min(a, p);
            b = Vector3.Max(b, p);
        }
        outLo = a;
        outHi = b;
    }

    /// <summary>The animation name the game layer's index selects, for the seam to forward
    /// (<c>CNWCTile::PlayAnimation</c>'s switch).</summary>
    public static string AnimName(int which) => (uint)which < (uint)AnimNames.Length ? AnimNames[which] : "";

    /// <summary>One clause of the Alloy period line. The two numbers the tile build is judged on are
    /// <c>meshes</c> (arena asks, one per distinct tile mesh) against <c>placements</c>.</summary>
    public static string Stats()
        => $"dbtiles {Placements} placements / {_plans.Count} models / {_arenaAsks} meshes, {_litRegistered} lights"
           + (_ringSkipped > 0 ? $", border ring {_ring} ({_ringSkipped} ring tiles skipped)" : "")
           + (_fadeTicks > 0
               ? $", fade {_classified} classified and {_fadingOut} fading out, {_fading} mid-ramp / "
                 + $"{_fadingPeak} peak over {_fadeTicks} ticks (mode {_fadeMode}, plane "
                 + (_planeOk ? "ok" : "down") + (_culling ? ", front cull on" : ", NO CAMERA FACING")
                 + $", {FadeCapable()} can fade, target {_fadeTarget:0.#})"
               : ", FADE NOT TICKED")
           + (_veiled > 0 ? $", {_veiled} fogged" : "")
           + (_heightTransition <= 0f ? ", HEIGHT STEP UNSET" : "")
           + (_statics0 > 0 ? $", {_statics0} off-grid statics" : "")
           + (_flames > 0 ? $", {_flames} flames placed" : "")
           + (_replayed > 0 ? $", {_replayed} ring replays" : "")
           + (_mergedNodes > 0 ? $", {_mergedNodes} nodes merged away" : "")
           + AuditStats()
           + (_mirroredMembers > 0 ? $", {_mirroredMembers} mirrored members rewound" : "")
           + (_swaps > 0 ? $", {_swaps} texture swaps re-pointing {_swapParts} part(s)"
                         + (_swapParts == 0 ? " -- NO PART CARRIES THE PLACEHOLDER, so every swap drew nothing" : "") : "")
           + (_cellTaken > 0 ? $", {_cellTaken} cells respawned" : "")
           + Refusals();

    private static string Refusals()
    {
        string s = "";
        if (_refusedModel > 0) s += $", {_refusedModel} NO MODEL";
        if (_refusedTable > 0) s += $", {_refusedTable} TABLE FULL";
        if (_refusedHeight > 0) s += $", {_refusedHeight} NO HEIGHT STEP";
        if (_refusedLights > 0) s += $", {_refusedLights} LIGHTS DROPPED";
        if (_lightSpill > 0) s += $", {_lightSpill} LIGHT SLOTS SPILLED";
        if (_refusedBig > 0) s += $", {_refusedBig} MESHES PAST THE 16-BIT INDEX";
        if (_refusedArena > 0) s += $", {_refusedArena} ARENA REFUSALS";
        if (_collisions > 0) s += $", {_collisions} BUCKET KEY COLLISIONS";
        if (_outOfOrder > 0) s += $", {_outOfOrder} NODES OUT OF ORDER";
        if (_flamesNoSl > 0) s += $", {_flamesNoSl} flames with no sl node (vanilla skips these too)";
        if (_fadeBoxSpill > 0) s += $", {_fadeBoxSpill} TILEFADE PARTS PAST THE FADE TABLE";
        if (_tileLightMiss > 0) s += $", {_tileLightMiss} TILE PALETTES FOR A CELL WITH NO TILE (their main lights keep the model's colour)";
        s += $", tile palettes {_palCalls} (main1 black {_palBlack1}, main2 black {_palBlack2})";
        s += $", wok {_wokMade} made + {_wokInModel} already in the model"
           + (_wokEmpty > 0 ? $" + {_wokEmpty} empty by design" : "")
           + (_wokMissing + _wokNoNode > 0 ? $" / {_wokMissing} MISSING + {_wokNoNode} with no aabb node" : "")
           + $", walkmesh {_modelsWithAabb}/{_modelsWithAabb + _modelsNoAabb} models, {AabbLive(out int boxed)} aabb live ({boxed} with hit bounds), {_aabbParts} made of {_partsMade} parts, "
           + $"interior {_realPlaced} placed / {_droppedReal} dropped, {LiveReal()} live";
        if (_swapOther > 0) s += $", {_swapOther} UNKNOWN TEXTURE SWAPS";
        if (_modelPulled > 0) s += $", {_modelPulled} TILES DROPPED WITH THEIR MODEL";
        if (_noPartForLight > 0) s += $", {_noPartForLight} lights with no part record";
        return s;
    }

    private static readonly HashSet<string> _said = new();
    private static CX.Framework.ExtenderLog? _log;

    /// <summary>Attach the log so a refusal is a named line (no silent fallbacks: once per cause, loudly).</summary>
    public static void Attach(CX.Framework.ExtenderLog log)
    {
        _log = log;
        if (_evictWired) return;
        _evictWired = true;
        // A model leaving the cache takes its merge plan's arena ranges with it (ADR 0028 section 3:
        // eviction, not refcount zero, is the edge). Added, not assigned: CX-114 points the same hook at
        // the arena.
        ModelCache.Evicting += Forget;
    }

    private static bool _evictWired;

    private static void Refuse(string why)
    {
        if (!_said.Add(why)) return;
        _log?.Always($"TileBuild: REFUSED -- {why}");
    }

    /// <summary>The world edge: an area change drops every placement, its objects and its lights. The merge
    /// plans are kept, because they are keyed on model CONTENT and the next area re-uses every tile model it
    /// shares with this one (ADR 0028).</summary>
    public static void Revoke()
    {
        // NOT a teardown in DB mode. ADR 0028: a tile record dies when the game says so -- DeleteTile, or
        // the scene it belongs to being destroyed (SceneRevoke) -- never on a renderer-side world edge.
        // Measured: AlloyRenderer.AreaEdge fires mid-load, AFTER CNWCArea::LoadArea's tile loop and BEFORE
        // LoadBorder, and this wiped exactly the real tiles. One ladder: "dropping 1155 (15 aabb)", then
        // "dropping 19 (19 aabb)" -- the whole of area 2's walkmesh-carrying geometry -- leaving 1171
        // placements with 1 aabb part, which is the floor every HitCheck then failed to find and the +9
        // focus Z of CX-185.
        if (!Renderer.On) DropAll();
        ResetTallies();
    }

    /// <summary>The scene edge: every tile placement of <paramref name="scene"/> dies with it. This is the
    /// DB-mode teardown, and it is scoped -- a GUI panel's scene dying must not take the world's tiles.</summary>
    public static void SceneRevoke(DbId scene)
    {
        for (int t = 1; t < SceneDb.Tiles.High; t++)
            if (SceneDb.Tiles.LiveAt(t) && SceneDb.TileScene[t] == scene) Drop(t);
        ResetTallies();
    }

    /// <summary>Drop every placement whatever the mode: the off-device suites' teardown between cases, and
    /// the one entry that is not a lifetime edge. Nothing in a session calls it.</summary>
    public static void ResetAll()
    {
        DropAll();
        ResetTallies();
    }

    private static void DropAll()
    {
        for (int t = 1; t < SceneDb.Tiles.High; t++) if (SceneDb.Tiles.LiveAt(t)) Drop(t);
        foreach (List<DbId> statics in _statics.Values) foreach (DbId s in statics) FreeObject(s);
        _statics.Clear();
        _byCell.Clear();
    }

    /// <summary>The per-area tallies the period line reports. The merge plans are NOT cleared: they key on
    /// the ModelDef instance, which the model cache keeps resident past its last reference and re-hands to
    /// the next area naming the same resref. A plan dies with its model, in Forget.</summary>
    private static void ResetTallies()
    {
        _spawned = _deleted = _replayed = _statics0 = 0;
        _refusedModel = _refusedTable = _refusedLights = _refusedHeight = 0;
        _refusedBig = _refusedArena = _collisions = _outOfOrder = _cellTaken = _lightSpill = 0;
        _flames = _flamesNoSl = _noPartForLight = 0;
        _aabbParts = _partsMade = _aabbWanted = _aabbFreed = _droppedReal = _realPlaced = 0;
        _litRegistered = _swaps = _swapParts = 0;
    }

    /// <summary>Drop the merge plans too: a model release (the cache's refcount reaching 0) invalidates the
    /// plan's arena ranges, so the plan goes with it.</summary>
    public static void Forget(ModelDef def)
    {
        // A plan normally goes only when its model is evicted, which a placement's reference prevents --
        // but ModelCache.Clear() (a module unload or a source change) evicts regardless. A placement still
        // standing on this model is then a record whose geometry is gone, so it is dropped here rather than
        // left pointing at a released arena range (#18).
        if (!_plans.Remove(def, out MergePlan? plan)) return;
        for (int t = 1; t < SceneDb.Tiles.High; t++)
            if (SceneDb.Tiles.LiveAt(t) && ReferenceEquals(SceneDb.TileModel[t], def)) { _modelPulled++; Drop(t); }
        // The two plan-scoped counters go with the plan, so the period line's "meshes" is what the table
        // actually holds rather than everything it ever held (#17).
        _arenaAsks -= plan.Meshes.Length;
        foreach (int[] members in plan.Members) if (members.Length > 1) _mergedNodes -= members.Length - 1;
        foreach (int r in plan.Ranges) if (r >= 0) MeshArena.Release(r);
    }
}
