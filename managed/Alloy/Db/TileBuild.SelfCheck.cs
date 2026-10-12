using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The off-device checks: a hand-built tile model placed twice at different rotations, which is the
/// whole perf claim stated as an assertion -- two transforms over ONE set of mesh ranges, each placement's
/// world bounds right, the lights filed per placement, and a texture swap dirtying only the tile that swapped.
/// Engine-free and GL-free, so <c>run-demo.sh</c> runs it in the stub host.</summary>
public static class TileBuildCheck
{
    /// <summary>The grid cells the checks place at, and the tileset height step they assume.</summary>
    private const int AX = 3, AY = 4, BX = 7, BY = 4;
    private const float Height = 5f;

    public static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TileBuild.SelfCheck");
        TileBuild.Attach(log);
        TileBuild.ResetAll();
        TileBuild.BorderWindow(false);
        TileBuild.InitGrid(default, 8, 8, 0, cellSize: 10f);
        // The height step is the TILESET's, not InitBSP's float. The zero case is a loud refusal rather than
        // a silent default, and is deliberately NOT exercised here: it would print a REFUSED line that reads
        // like a real one.
        TileBuild.SetTileset(Height);
        check.Expect(TileBuild.HeightTransition == Height, "the tileset's height step is what places a raised tile");

        ModelDef def = TileModel();
        TileBuild.Forget(def);

        // Placement A: no rotation. Placement B: one quarter turn. One model, so one merge plan.
        int asksBefore = TileBuild.ArenaAsks;
        int litBefore = TileBuild.LightsRegistered;
        int a = TileBuild.Place(default, def, TileBuild.CellPos(AX, AY, 1), 0, 0);
        int asksAfterA = TileBuild.ArenaAsks;
        int b = TileBuild.Place(default, def, TileBuild.CellPos(BX, BY, 0), 1, 0);

        check.Expect(a >= 0 && b >= 0, "two placements of one tile model both take a record");
        check.Expect(a != b, "each placement is its own tile record");

        TileBuild.MergePlan plan = TileBuild.Plan(def);
        check.Expect(plan.Meshes.Length == 2,
            $"the two mergeable nodes bucket by draw state into 2 meshes, not {plan.Meshes.Length}");
        check.Expect(asksAfterA - asksBefore == plan.Meshes.Length,
            "the first placement is the only one that offers the model's meshes to the arena");
        check.Expect(TileBuild.ArenaAsks == asksAfterA,
            $"the second placement offers NO new mesh ({TileBuild.ArenaAsks - asksAfterA} extra): one range per model, not per placement");

        // Two transforms, one geometry: the object records differ, the model does not.
        DbId oa = SceneDb.TileObj[a], ob = SceneDb.TileObj[b];
        check.Expect(!oa.IsNull && !ob.IsNull, "each placement has an object record");
        check.Expect(ReferenceEquals(SceneDb.ObjModel[oa.Index], SceneDb.ObjModel[ob.Index]),
            "both placements name the same ModelDef");
        check.Expect(SceneDb.ObjPos[oa.Index] == TileBuild.CellPos(AX, AY, 1),
            "the placement position is the grid cell's: (x*10+5, y*10+5, heightTransition*height)");
        check.Expect(MathF.Abs(SceneDb.ObjPos[oa.Index].Z - Height) < 1e-4f,
            "height index 1 lifts the tile by one height transition");
        check.Expect(SceneDb.ObjRot[oa.Index] != SceneDb.ObjRot[ob.Index],
            "the two placements carry different orientations");

        // A quarter turn about +Z takes +X to +Y: the one fact the orientation quaternion has to get right.
        Vector3 turned = Vector3.Transform(Vector3.UnitX, SceneDb.ObjRot[ob.Index]);
        check.Expect(MathF.Abs(turned.X) < 1e-4f && MathF.Abs(turned.Y - 1f) < 1e-4f,
            $"orient 1 is 90 degrees about +Z (+X -> {turned.X:0.###},{turned.Y:0.###})");

        // World bounds: the model box is x,y in [-5,5] and z in [0,4] (the floor plus the 4-high wall node).
        Vector3 lo = SceneDb.TileBmin[a], hi = SceneDb.TileBmax[a];
        Vector3 c = TileBuild.CellPos(AX, AY, 1);
        check.Expect(Near(lo, new Vector3(c.X - 5f, c.Y - 5f, c.Z)) && Near(hi, new Vector3(c.X + 5f, c.Y + 5f, c.Z + 4f)),
            $"placement A's world box is the model box at its cell (got {lo} .. {hi})");
        Vector3 lo2 = SceneDb.TileBmin[b], hi2 = SceneDb.TileBmax[b];
        Vector3 c2 = TileBuild.CellPos(BX, BY, 0);
        check.Expect(Near(lo2, new Vector3(c2.X - 5f, c2.Y - 5f, c2.Z)) && Near(hi2, new Vector3(c2.X + 5f, c2.Y + 5f, c2.Z + 4f)),
            $"a quarter turn of a square tile keeps the box square at its cell (got {lo2} .. {hi2})");

        // Lights: one node, one record per placement, positioned by the placement.
        check.Expect(TileBuild.LightsRegistered - litBefore == 4,
            $"two light nodes over two placements register four light records, not {TileBuild.LightsRegistered - litBefore}");
        check.Expect(SceneDb.TileLightCount[a] == 2 && SceneDb.TileLightCount[b] == 2,
            "each placement owns its own light slots");
        int ka = TileBuild.LightAt(a, 0);
        check.Expect(Near(SceneDb.LitPos[ka], new Vector3(c.X + 2f, c.Y, c.Z + 3f)),
            $"the light node at (2,0,3) in model space lands at the placed point (got {SceneDb.LitPos[ka]})");
        int kb = TileBuild.LightAt(b, 0);
        check.Expect(Near(SceneDb.LitPos[kb], new Vector3(c2.X, c2.Y + 2f, c2.Z + 3f)),
            $"the same node turns with its placement (got {SceneDb.LitPos[kb]})");
        // The engine picks the two main lights BY NAME, not by node order: a model whose first light node is
        // not <model>ml1 must not have it re-coloured.
        check.Expect(SceneDb.LitMainSlot[ka] == -1,
            "a light node NOT named <model>ml1/ml2 is not a main light, whatever its node order");
        int kml = -1, kother = -1;
        for (int q = 0; q < SceneDb.TileLightCount[a]; q++)
        {
            int kk = TileBuild.LightAt(a, q);
            if (SceneDb.LitMainSlot[kk] == 0) kml = kk; else kother = kk;
        }
        check.Expect(kml >= 0 && kother >= 0, "the model's ml1 node is found by name beside its unnamed light");
        check.Expect(SceneDb.LitTile[ka] == SceneDb.Tiles.IdAt(a), "a tile light names its placement");

        // SetTileLights re-colours only the main slots, and re-colouring to the same value changes nothing.
        SceneDb.Tiles.TakeDirty();
        SceneDb.Lights.TakeDirty();
        Vector3 wasOther = SceneDb.LitColour[kother];
        TileBuild.SetTileLights(default, c, new Vector3(0.25f, 0.5f, 0.75f), Vector3.Zero);
        check.Expect(Near(SceneDb.LitColour[kml], new Vector3(0.25f, 0.5f, 0.75f)), "ml1 takes the area's colour");
        check.Expect(MathF.Abs(SceneDb.LitRange[kml] - 10f) < 1e-4f && MathF.Abs(SceneDb.LitRangeAlt[kml] - 12f) < 1e-4f,
            "ml1 gets Scene::SetTileLights' 10/12 radii");
        check.Expect(SceneDb.LitPriority[kml] == 4, "a tileset main light has priority 4");
        check.Expect(SceneDb.LitColour[kother] == wasOther, "a light that is not ml1/ml2 is left alone");
        int dirtyAfter = SceneDb.Lights.DirtyCount;
        SceneDb.Lights.TakeDirty();
        TileBuild.SetTileLights(default, c, new Vector3(0.25f, 0.5f, 0.75f), Vector3.Zero);
        _ = dirtyAfter;
        check.Expect(dirtyAfter > 0 && SceneDb.Lights.DirtyCount == 0,
            "re-colouring to the colour it already has dirties nothing");

        // Texture swap: the tile that swapped is dirty, its neighbour is not.
        SceneDb.Tiles.TakeDirty();
        SceneDb.Objects.TakeDirty();
        TileBuild.ReplaceTileTexture(default, c, "tcn01_wall01", TileBuild.ReplaceSlot);
        bool aDirty = (SceneDb.Tiles.DirtyAt(a) & Dirty.Texture) != 0;
        bool bClean = (SceneDb.Tiles.DirtyAt(b) & Dirty.Texture) == 0;
        check.Expect(aDirty && bClean, "a texture swap dirties the tile that swapped and no other");
        check.Expect(SceneDb.TileReplaceTex[a] == "tcn01_wall01" && SceneDb.TileReplaceTex[b] == null,
            "the substitution is recorded per placement, so the shared mesh range is untouched");
        check.Expect((SceneDb.Objects.DirtyAt(oa.Index) & Dirty.Texture) != 0,
            "the swap reaches the placement's object record, which is what the renderer rebinds from");

        // Fog of war holds the reveal ramp at 0 and releases it.
        TileBuild.SetFoggedByWar(default, c, true);
        check.Expect(SceneDb.TileFogged[a] && SceneDb.TileReveal[a] == 0f, "an unexplored tile is fogged and unrevealed");
        TileBuild.SetFoggedByWar(default, c, false);
        TileBuild.TickFade(default, c, c + new Vector3(0f, -20f, 10f), 1f, geomFadeMode: 2);
        check.Expect(SceneDb.TileReveal[a] > 0f, "an explored tile ramps back in at 4.0/s");

        // The cell index is the engine's SceneBSPGetTileData: a position anywhere in the cell finds the tile.
        check.Expect(TileBuild.At(default, c + new Vector3(4.9f, -4.9f, 0f)) == a,
            "any point inside a tile's 10x10 cell resolves to that tile");
        check.Expect(TileBuild.At(default, TileBuild.CellPos(1, 1, 0)) < 0, "an empty cell resolves to nothing");

        // Delete is the one lifetime edge: the record, its object and its lights go together.
        int liveTiles = SceneDb.Tiles.Live, liveObjs = SceneDb.Objects.Live, liveLights = SceneDb.Lights.Live;
        DbId held = SceneDb.Tiles.IdAt(a), heldObj = oa, heldLight = SceneDb.Lights.IdAt(kml);
        TileBuild.DeleteTile(default, c);
        check.Expect(SceneDb.Tiles.Live == liveTiles - 1 && SceneDb.Objects.Live == liveObjs - 1
                     && SceneDb.Lights.Live == liveLights - 2,
            $"a delete takes the tile, its object and BOTH its lights, in one edge "
          + $"(tiles {SceneDb.Tiles.Live}/{liveTiles - 1}, objects {SceneDb.Objects.Live}/{liveObjs - 1}, "
          + $"lights {SceneDb.Lights.Live}/{liveLights - 2})");
        check.Expect(TileBuild.At(default, c) < 0, "a deleted tile's cell is empty");
        check.Expect(!SceneDb.Tiles.Valid(held) && !SceneDb.Objects.Valid(heldObj) && !SceneDb.Lights.Valid(heldLight),
            "a handle taken before the delete is dead the moment it happens (ADR 0028)");

        RampCheck(check, def);
        SettledCheck(check, def);
        FlameCheck(check, def);
        OrientCheck(check);
        ReplaceTexCheck(check);
        LifoCheck(check, def);
        BorderCheck(check, def);
        SceneMoveCheck(check, def);

        TileBuild.ResetAll();
        TileBuild.Forget(def);
        log.Info(check.Ok ? "TileBuild.SelfCheck: passed"
                          : "TileBuild.SelfCheck: FAILED -- the DB tile build would not match the engine's");
        return check.Ok;
    }

    /// <summary>A placed tile never changes scene through the seam: a stale game pointer on a recycled tile shell
    /// used to carry it into a GUI scene, leaving a hole with no floor. Oracle: the hand-written verdict for
    /// each case, and a non-tile record carrying a leaked Tile flag that must not be guarded.</summary>
    private static void SceneMoveCheck(Checker check, ModelDef def)
    {
        TileBuild.ResetAll();
        TileBuild.InitGrid(default, 8, 8, 0, 10f);
        DbId world = SceneDb.Scenes.Alloc(), gui = SceneDb.Scenes.Alloc();
        int t = TileBuild.Place(world, def, TileBuild.CellPos(1, 1, 0), 0, 0);
        check.Expect(t >= 0, "a tile places into a live scene");
        DbId obj = t >= 0 ? SceneDb.TileObj[t] : default;
        check.Expect(TileBuild.OwnScene(obj).Value == world.Value, "a placed tile's object belongs to its world scene");
        check.Expect(TileBuild.RefuseMove(obj, gui, "[self-check, on purpose] Scene::ChildAdd"),
            "a join into another scene is refused");
        check.Expect(TileBuild.RefuseMove(obj, default, "[self-check, on purpose] Scene::ChildRemove"),
            "a removal from its own scene is refused");
        check.Expect(!TileBuild.RefuseMove(obj, world, "[self-check, on purpose] Scene::ChildAdd"),
            "a join into its own scene is allowed");
        DbId text = SceneDb.Objects.Alloc();
        DbFeed.InitObject(text.Index);
        SceneDb.ObjFlags[text.Index] = ObjFlag.Tile;
        check.Expect(!TileBuild.RefuseMove(text, gui, "[self-check, on purpose] Scene::ChildAdd"),
            "a record that only carries a leaked Tile flag is no tile, and moves freely");
        SceneDb.Objects.Free(text);
        TileBuild.ResetAll();
        SceneDb.Scenes.Free(world);
        SceneDb.Scenes.Free(gui);
        TileBuild.ResetAudit();
    }

    /// <summary>The two per-tile ramps have to stay APART: the camera fade is an alpha
    /// (<see cref="SceneDb.PartFade"/>), the fog-of-war reveal is a fog term
    /// (<see cref="SceneDb.PartReveal"/>). Folding them into one float made an unexplored tile transparent.</summary>
    private static void RampCheck(Checker check, ModelDef def)
    {
        TileBuild.ResetAll();
        TileBuild.InitGrid(default, 8, 8, 0, 10f);
        TileBuild.SetTileset(Height);
        Vector3 c = TileBuild.CellPos(AX, AY, 0);
        int t = TileBuild.Place(default, def, c, 0, 0);
        if (t < 0) { check.Expect(false, "the ramp check could not place its tile"); return; }
        int o = SceneDb.TileObj[t].Index, first = SceneDb.ObjPartFirst[o], n = SceneDb.ObjPartCount[o];

        TileBuild.SetFoggedByWar(default, c, true);
        check.Expect(SceneDb.TileReveal[t] == 0f, "an unexplored tile is unrevealed");
        check.Expect(SceneDb.TileFade[t] == 1f, "fog of war does not touch the camera fade");
        if (n > 0)
        {
            check.Expect(SceneDb.PartReveal[first] == 0f, "the reveal reaches the part's own column");
            check.Expect(SceneDb.PartFade[first] == 1f,
                "the reveal is NOT folded into PartFade -- an unexplored tile is fogged, not transparent");
        }

        // A camera looking along -y at the tile, with the roof between: the fade ramps, the reveal does not.
        TileBuild.SetFoggedByWar(default, c, false);
        for (int f = 0; f < 10; f++) TileBuild.TickFade(default, c, c + new Vector3(0f, -30f, 12f), 0.1f, geomFadeMode: 1);
        check.Expect(SceneDb.TileReveal[t] == 1f, "an explored tile ends fully revealed");
        if (n > 0) check.Expect(SceneDb.PartReveal[first] == 1f, "the reveal column follows the tile's");

        // Mode 4 ("always") fades without a plane verdict; mode 2 ("never") fades at all times not.
        float was = SceneDb.TileFade[t];
        for (int f = 0; f < 5; f++) TileBuild.TickFade(default, c, c, 0.1f, geomFadeMode: 4);
        bool hasFadeGeom = SceneDb.TileFadeBmin[t] != SceneDb.TileFadeBmax[t];
        check.Expect(!hasFadeGeom || SceneDb.TileFade[t] < was,
            "geom-fade mode 4 fades a tilefade-1 tile with no plane to test");
        for (int f = 0; f < 40; f++) TileBuild.TickFade(default, c, c, 0.1f, geomFadeMode: 2);
        check.Expect(SceneDb.TileFade[t] == 1f, "geom-fade mode 2 puts every tile back to opaque");
        TileBuild.ResetAll();
    }

    /// <summary>A SETTLED tile restages nothing. The engine-side half of this is the <c>dbfeed</c> line's
    /// <c>static-restage N/frame</c>, which counts records of <see cref="ObjFlag.Static"/> objects the last
    /// fold rewrote; this is the half that can be pinned off-device -- the per-frame tile path marks no part
    /// once the ramps have settled, so nothing downstream has anything to fold.</summary>
    private static void SettledCheck(Checker check, ModelDef def)
    {
        TileBuild.ResetAll();
        TileBuild.InitGrid(default, 8, 8, 0, 10f);
        TileBuild.SetTileset(Height);
        Vector3 c = TileBuild.CellPos(AX, AY, 0);
        int t = TileBuild.Place(default, def, c, 0, 0);
        if (t < 0) { check.Expect(false, "the settled check could not place its tile"); return; }
        Vector3 cam = c + new Vector3(0f, -30f, 12f);

        // Let the ramps finish, then clear the dirty lists: this is the "settled area" state.
        for (int f = 0; f < 40; f++) TileBuild.TickFade(default, c, cam, 0.1f, geomFadeMode: 1);
        SceneDb.Parts.TakeDirty();
        SceneDb.Tiles.TakeDirty();
        SceneDb.Objects.TakeDirty();

        for (int f = 0; f < 10; f++) TileBuild.TickFade(default, c, cam, 1f / 60f, geomFadeMode: 1);
        check.Expect(SceneDb.Parts.DirtyCount == 0,
            $"a settled tile marks no part per frame ({SceneDb.Parts.DirtyCount} after 10 frames) -- a mark "
          + "here is a record restaged downstream every frame");
        check.Expect(SceneDb.Tiles.DirtyCount == 0 && SceneDb.Objects.DirtyCount == 0,
            "...and no tile or object record either");

        // And the ramp still MOVES when it should: the guard must be change-driven, not a mute.
        TileBuild.SetFoggedByWar(default, c, true);
        check.Expect(SceneDb.Parts.DirtyCount > 0 || SceneDb.ObjPartCount[SceneDb.TileObj[t].Index] == 0,
            "a real change still reaches the parts");
        TileBuild.ResetAll();
    }

    /// <summary>A flame is PLACED, at its model's <c>sl1</c> node, turned with the tile: the engine copies the
    /// sl part's pose into the flame object (<c>Scene::SetSingleTileFlame</c> @0x55ef30).</summary>
    private static void FlameCheck(Checker check, ModelDef def)
    {
        TileBuild.ResetAll();
        TileBuild.InitGrid(default, 8, 8, 0, 10f);
        TileBuild.SetTileset(Height);
        Vector3 c = TileBuild.CellPos(AX, AY, 0);
        int t = TileBuild.Place(default, def, c, 1, 0);          // one quarter turn
        if (t < 0) { check.Expect(false, "the flame check could not place its tile"); return; }

        DbId flame = SceneDb.Objects.Alloc();
        SceneDb.ObjKindOf[flame.Index] = ObjKind.Model;
        TileBuild.SetTileFlames(default, c, flame, default);
        check.Expect(SceneDb.TileFlame1[t] == flame, "the tile holds the flame it was given");
        // sl1 is at (0,3,1) in model space; a quarter turn about +Z takes it to (-3,0,1).
        check.Expect(Near(SceneDb.ObjPos[flame.Index], c + new Vector3(-3f, 0f, 1f)),
            $"the flame stands at its sl1 node, turned with the tile (got {SceneDb.ObjPos[flame.Index]}, want {c + new Vector3(-3f, 0f, 1f)})");
        check.Expect(SceneDb.ObjScene[flame.Index] == SceneDb.TileScene[t], "the flame joins the tile's scene");

        TileBuild.SetTileFlames(default, c, default, default);
        check.Expect(SceneDb.TileFlame1[t].IsNull, "clearing the flames empties the slot");
        SceneDb.Objects.Free(flame);
        TileBuild.ResetAll();
    }

    /// <summary>A square tile's box cannot catch a sign error in the orientation quaternion, so this one is
    /// asymmetric: a 4x1 strip along +x must land along +y after one quarter turn.</summary>
    private static void OrientCheck(Checker check)
    {
        TileBuild.ResetAll();
        TileBuild.InitGrid(default, 8, 8, 0, 10f);
        TileBuild.SetTileset(Height);
        ModelDef strip = StripModel();
        TileBuild.Forget(strip);
        Vector3 c = TileBuild.CellPos(1, 1, 0);
        int t = TileBuild.Place(default, strip, c, 1, 0);
        if (t < 0) { check.Expect(false, "the orientation check could not place its tile"); return; }
        Vector3 lo = SceneDb.TileBmin[t], hi = SceneDb.TileBmax[t];
        check.Expect(Near(lo, c + new Vector3(-1f, 0f, 0f)) && Near(hi, c + new Vector3(0f, 4f, 0f)),
            $"a +x strip turns to +y at orient 1, not -y (got {lo} .. {hi})");

        // The TEXTURE rotation rides the same orient (SetPartTextureRotate @0x561670), per placement, because
        // one recipe serves all four orientations of a tile model.
        check.Expect(SceneDb.ObjTexRotate[SceneDb.TileObj[t].Index] == 1,
            "the placement's quarter turns did not reach its object -- every tile texture draws unrotated");
        int t0 = TileBuild.Place(default, strip, TileBuild.CellPos(3, 1, 0), 0, 0);
        check.Expect(t0 >= 0 && SceneDb.ObjTexRotate[SceneDb.TileObj[t0].Index] == 0,
            "an unrotated placement of the same model reports a quarter turn -- the angle is not per placement");

        // The record's pair IS the engine's (cos, sin) of orient x 90 degrees: exact, so a rotated tile's UVs
        // land on the texel the engine's own m_texture would, and signed the same way.
        for (int q = 0; q < 4; q++)
        {
            (float cos, float sin) = AlloyRenderer.DbTexRot(q);
            float rad = q * 90f * (MathF.PI / 180f);
            check.Expect(MathF.Abs(cos - MathF.Cos(rad)) < 1e-6f && MathF.Abs(sin - MathF.Sin(rad)) < 1e-6f,
                $"orient {q} stages ({cos}, {sin}), not (cos, sin) of {q * 90} degrees");
        }
        check.Expect(AlloyRenderer.DbTexRot(1) == (0f, 1f),
            "orient 1 stages a NEGATIVE sine -- the pair is the engine's m_texture columns (c,s),(-s,c), unnegated");

        // The recycle: a slot that kept the last tenant's turns would rotate a tile its placement did not.
        TileBuild.DeleteTile(default, c);
        int t2 = TileBuild.Place(default, strip, c, 0, 0);
        check.Expect(t2 >= 0 && SceneDb.ObjTexRotate[SceneDb.TileObj[t2].Index] == 0,
            "a tile built on a recycled object slot inherited the last tenant's texture rotation");

        TileBuild.ResetAll();
        TileBuild.Forget(strip);
    }

    /// <summary>The light slots must survive the free list going LIFO: two tiles placed, the first deleted,
    /// a third placed -- the recycled rows land out of order, which a contiguous run could not express.</summary>
    private static void LifoCheck(Checker check, ModelDef def)
    {
        TileBuild.ResetAll();
        TileBuild.InitGrid(default, 8, 8, 0, 10f);
        TileBuild.SetTileset(Height);
        // Three tiles, then delete the MIDDLE one: its two rows go back on the free list, and the next tile
        // takes them in whatever order the list hands them out. The old code required the two rows of one
        // tile to be CONSECUTIVE and refused the second otherwise, which after any area change left every
        // two-main-light tile with one light.
        Vector3 c1 = TileBuild.CellPos(1, 1, 0), c2 = TileBuild.CellPos(2, 1, 0),
                c3 = TileBuild.CellPos(3, 1, 0), c4 = TileBuild.CellPos(4, 1, 0);
        TileBuild.Place(default, def, c1, 0, 0);
        TileBuild.Place(default, def, c2, 0, 0);
        TileBuild.Place(default, def, c3, 0, 0);
        TileBuild.DeleteTile(default, c2);
        string before = TileBuild.Stats();
        int t4 = TileBuild.Place(default, def, c4, 0, 0);
        if (t4 < 0) { check.Expect(false, "the LIFO check could not place its fourth tile"); return; }
        int k0 = TileBuild.LightAt(t4, 0), k1 = TileBuild.LightAt(t4, 1);
        check.Expect(SceneDb.TileLightCount[t4] == 2,
            $"a tile built on recycled rows still gets BOTH its lights (rows {k0}, {k1})");
        check.Expect(k0 >= 0 && k1 >= 0 && SceneDb.Lights.LiveAt(k0) && SceneDb.Lights.LiveAt(k1),
            $"both recycled light rows are live ({k0}, {k1})");
        check.Expect(!TileBuild.Stats().Contains("LIGHTS DROPPED") && !before.Contains("LIGHTS DROPPED"),
            "nothing about a recycled row is refused");
        int mains = 0;
        for (int q = 0; q < SceneDb.TileLightCount[t4]; q++)
            if (SceneDb.LitMainSlot[TileBuild.LightAt(t4, q)] == 0) mains++;
        check.Expect(mains == 1, "the ml1 light is still found on a tile whose rows came off the free list");
        TileBuild.ResetAll();
    }

    /// <summary>The texture swap has to REACH the geometry, not just be recorded: a tile part whose resolved
    /// slot 0 is the tileset's <c>replace_tex</c> placeholder shows the palette row's name instead, per
    /// placement, slot 0 only, and the reference goes when the tile does.</summary>
    private static void ReplaceTexCheck(Checker check)
    {
        TileBuild.ResetAll();
        TileBuild.InitGrid(default, 8, 8, 0, 10f);
        TileBuild.SetTileset(Height);
        ModelDef def = ReplaceModel();
        TileBuild.Forget(def);
        Vector3 c1 = TileBuild.CellPos(1, 1, 0), c2 = TileBuild.CellPos(2, 1, 0);
        int t1 = TileBuild.Place(default, def, c1, 0, 0);
        int t2 = TileBuild.Place(default, def, c2, 0, 0);
        if (t1 < 0 || t2 < 0) { check.Expect(false, "the replace-texture check could not place its tiles"); return; }

        int o1 = SceneDb.TileObj[t1].Index, o2 = SceneDb.TileObj[t2].Index;
        int ph = -1, pf = -1;
        for (int p = SceneDb.ObjPartFirst[o1]; p < SceneDb.ObjPartFirst[o1] + SceneDb.ObjPartCount[o1]; p++)
        {
            if (SceneDb.PartTexName(p, 0).Equals(TileBuild.ReplaceSlot, StringComparison.OrdinalIgnoreCase)) ph = p;
            else if (SceneDb.PartTexName(p, 0).Length > 0) pf = p;
        }
        check.Expect(ph >= 0 && pf >= 0, "the fixture's placeholder part and its ordinary part are both built");
        if (ph < 0 || pf < 0) { TileBuild.ResetAll(); TileBuild.Forget(def); return; }

        TileBuild.ReplaceTileTexture(default, c1, "tcn01_floor09", TileBuild.ReplaceSlot);
        check.Expect(Shows(ph, 0) == "tcn01_floor09",
            $"the swap did not reach the placeholder part (shows \"{Shows(ph, 0)}\")");
        check.Expect(SceneDb.PartTexId[pf * 4] == 0, "the swap re-pointed a part that does not carry the placeholder");
        for (int s = 1; s < 4; s++)
            check.Expect(SceneDb.PartTexId[ph * 4 + s] == 0,
                $"the swap wrote slot {s}; SceneBSPGetTileTextures reads slot 0 and nothing else");
        int pOther = -1;
        for (int p = SceneDb.ObjPartFirst[o2]; p < SceneDb.ObjPartFirst[o2] + SceneDb.ObjPartCount[o2]; p++)
            if (SceneDb.PartTexName(p, 0).Equals(TileBuild.ReplaceSlot, StringComparison.OrdinalIgnoreCase)) pOther = p;
        check.Expect(pOther >= 0 && SceneDb.PartTexId[pOther * 4] == 0,
            "the swap reached the OTHER placement too -- it is per placement, over one shared mesh range");

        // A second row: the override moves, and TexRegistry.Swap is the one call that releases the first.
        TileBuild.ReplaceTileTexture(default, c1, "tcn01_floor12", TileBuild.ReplaceSlot);
        check.Expect(Shows(ph, 0) == "tcn01_floor12", $"a re-swap left the first name in place (\"{Shows(ph, 0)}\")");
        // A blank palette row drops it, so the recorded null and the drawn texture cannot disagree.
        TileBuild.ReplaceTileTexture(default, c1, "", TileBuild.ReplaceSlot);
        check.Expect(SceneDb.TileReplaceTex[t1] == null && SceneDb.PartTexId[ph * 4] == 0,
            "a blank palette row recorded no replacement but left the part showing the old one");

        TileBuild.ReplaceTileTexture(default, c1, "tcn01_floor09", TileBuild.ReplaceSlot);
        TileBuild.DeleteTile(default, c1);
        check.Expect(SceneDb.PartTexId[ph * 4] == 0,
            "a deleted tile left its override (and its registry reference) behind -- TileBuild.FreeObject "
          + "never reaches ObjectSlots.ReleaseRecord, so the release has to be its own");

        TileBuild.ResetAll();
        TileBuild.Forget(def);
    }

    /// <summary>What part <paramref name="p"/>'s slot <paramref name="slot"/> actually draws: its override
    /// when it has one, else its own texture.</summary>
    private static string Shows(int p, int slot)
    {
        int ov = SceneDb.PartTexId[p * 4 + slot];
        return ov != 0 ? TexRegistry.ResrefOf(new TexId((uint)ov)) : SceneDb.PartTexName(p, slot);
    }

    /// <summary>Two quads on different textures, one of them the tileset's <c>replace_tex</c> placeholder, so
    /// they bucket apart and the swap has exactly one part to find.</summary>
    private static ModelDef ReplaceModel()
    {
        var def = new ModelDef { Name = "cxtest_rep", Classification = 2, ContentHash = 7731449 };
        var root = new NodeDef { Name = "cxtest_rep", Index = 0, Parent = -1, Kind = NodeKind.Dummy };
        NodeDef hole = Quad("hole", 1, 0, TileBuild.ReplaceSlot, -5f, -5f, 0f, 5f, 0f);
        NodeDef floor = Quad("floor", 2, 0, "tcn01_floor01", 0f, -5f, 5f, 5f, 0f);
        root.Children = [1, 2];
        def.Nodes = [root, hole, floor];
        foreach (NodeDef n in def.Nodes) def.NodeByName[n.Name] = n.Index;
        def.BMin = new Vector3(-5f, -5f, 0f);
        def.BMax = new Vector3(5f, 5f, 0f);
        return def;
    }

    /// <summary>A 4x1 strip along +x, for <see cref="OrientCheck"/>.</summary>
    private static ModelDef StripModel()
    {
        var def = new ModelDef { Name = "cxtest_strip", Classification = 2, ContentHash = 991162 };
        var root = new NodeDef { Name = "cxtest_strip", Index = 0, Parent = -1, Kind = NodeKind.Dummy };
        NodeDef q = Quad("strip", 1, 0, "tcn01_floor01", 0f, 0f, 4f, 1f, 0f);
        root.Children = [1];
        def.Nodes = [root, q];
        foreach (NodeDef n in def.Nodes) def.NodeByName[n.Name] = n.Index;
        def.BMin = new Vector3(0f, 0f, 0f);
        def.BMax = new Vector3(4f, 1f, 0f);
        return def;
    }

    /// <summary>The widened ring, against the DB: the outermost engine-ring tile of a strip replays outward,
    /// and the cells it lands on are exactly the ones <see cref="AreaBorder"/>'s own check proves for the
    /// engine's scene -- so the two rings are the same ring.</summary>
    private static void BorderCheck(Checker check, ModelDef def)
    {
        const int w = 4, h = 3;
        TileBuild.ResetAll();
        TileBuild.InitGrid(default, w, h, 1, Height);
        TileBuild.BorderWindow(true);
        int ring = AreaBorder.Ring;
        int placed = 0;
        for (int ix = -TileBuild.EngineRing; ix < w + TileBuild.EngineRing; ix++)
            for (int iy = -TileBuild.EngineRing; iy < h + TileBuild.EngineRing; iy++)
            {
                if (ix >= 0 && ix < w && iy >= 0 && iy < h) continue;
                if (TileBuild.PlaceWithRing(default, def, TileBuild.CellPos(ix, iy, 0), 0, 1) >= 0) placed++;
            }
        TileBuild.BorderWindow(false);
        int want = (w + 2 * ring) * (h + 2 * ring) - w * h;
        check.Expect(placed == (w + 2 * TileBuild.EngineRing) * (h + 2 * TileBuild.EngineRing) - w * h,
            $"the engine's own ring is spawned cell by cell ({placed} tiles)");
        check.Expect(SceneDb.Tiles.Live == want,
            $"a ring of {ring} covers every outer cell exactly once ({SceneDb.Tiles.Live} tiles, want {want})");
        check.Expect(TileBuild.At(default, TileBuild.CellPos(-ring, -ring, 0)) >= 0,
            "the outermost corner cell of the widened ring holds a tile");
        check.Expect(TileBuild.At(default, TileBuild.CellPos(-ring - 1, 0, 0)) < 0,
            "nothing is placed past the ring");

        // a large area trims the ring BELOW the engine's own five: the outer engine cells are skipped, not placed
        const int big = TileBuild.LargeArea;
        TileBuild.ResetAll();
        TileBuild.InitGrid(default, big, big, 1, Height);
        TileBuild.BorderWindow(true);
        for (int ix = -TileBuild.EngineRing; ix < big + TileBuild.EngineRing; ix++)
            for (int iy = -TileBuild.EngineRing; iy < big + TileBuild.EngineRing; iy++)
                if (ix < 0 || ix >= big || iy < 0 || iy >= big)
                    TileBuild.PlaceWithRing(default, def, TileBuild.CellPos(ix, iy, 0), 0, 1);
        TileBuild.BorderWindow(false);
        int r = TileBuild.LargeRing;
        check.Expect(SceneDb.Tiles.Live == (big + 2 * r) * (big + 2 * r) - big * big,
            $"a {big}x{big} area keeps a ring of {r} ({SceneDb.Tiles.Live} tiles)");
        check.Expect(TileBuild.At(default, TileBuild.CellPos(-r, -r, 0)) >= 0
                     && TileBuild.At(default, TileBuild.CellPos(big + r - 1, 0, 0)) >= 0
                     && TileBuild.At(default, TileBuild.CellPos(-r - 1, 0, 0)) < 0
                     && TileBuild.At(default, TileBuild.CellPos(big + r, 0, 0)) < 0,
            "the trimmed ring stops at the same distance on the near and the far side");
        TileBuild.ResetAll();
    }

    private static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-3f;

    /// <summary>A hand-built tile model: a 10x10 floor and a 4-high wall on two different textures (so they
    /// bucket apart), a third node that an animation names (so it does NOT merge), a second node on the
    /// FLOOR's texture (so one bucket does merge two nodes), and one light node at (2,0,3).</summary>
    private static ModelDef TileModel()
    {
        var def = new ModelDef { Name = "cxtest_tile", Classification = 2, ContentHash = 13330086132014113046 };
        var nodes = new System.Collections.Generic.List<NodeDef>();

        var root = new NodeDef { Name = "cxtest_tile", Index = 0, Parent = -1, Kind = NodeKind.Dummy };
        nodes.Add(root);

        nodes.Add(Quad("floor", 1, 0, "tcn01_floor01", -5f, -5f, 5f, 5f, 0f));
        nodes.Add(Quad("floor_b", 2, 0, "tcn01_floor01", -5f, -5f, 0f, 0f, 0f));
        NodeDef wall = Quad("wall", 3, 0, "tcn01_wall01", -5f, 4.9f, 5f, 5f, 0f);
        // the wall's quad is vertical: lift its far edge to 4.0 so the model box has height
        wall.Mesh!.Positions[2] = new Vector3(5f, 5f, 4f);
        wall.Mesh.Positions[3] = new Vector3(-5f, 5f, 4f);
        Rebound(wall.Mesh);
        nodes.Add(wall);
        nodes.Add(Quad("animloop01", 4, 0, "tcn01_floor01", -1f, -1f, 1f, 1f, 0.1f));

        // The FIRST light node is deliberately NOT named <model>ml1: node order must not decide which light
        // Scene::SetTileLights re-colours.
        nodes.Add(Light("mainlight01", 5, new Vector3(2f, 0f, 3f)));
        nodes.Add(Light("cxtest_tileml1", 6, new Vector3(-2f, 0f, 3f)));
        // <model>sl1: where ProcessTilePart says a flame stands. A dummy, as the tilesets author it.
        nodes.Add(new NodeDef
        {
            Name = "cxtest_tilesl1", Index = 7, Parent = 0, Kind = NodeKind.Dummy,
            Position = new Vector3(0f, 3f, 1f),
        });

        root.Children = [1, 2, 3, 4, 5, 6, 7];
        def.Nodes = nodes.ToArray();
        // one animation that names animloop01: the node it names must stay its own part
        def.Anims =
        [
            new AnimDef
            {
                Name = "AnimLoop01", Length = 1f,
                Nodes = [new AnimNode { Name = "animloop01", Controllers = [new ControllerTrack { Type = Ctl.Position, Columns = 3, Times = [0f, 1f], Values = [0f, 0f, 0f, 0f, 0f, 1f] }] }],
            },
        ];
        foreach (NodeDef n in def.Nodes) def.NodeByName[n.Name] = n.Index;
        def.BMin = new Vector3(-5f, -5f, 0f);
        def.BMax = new Vector3(5f, 5f, 4f);
        return def;
    }

    private static NodeDef Light(string name, int index, Vector3 pos)
    {
        var n = new NodeDef
        {
            Name = name, Index = index, Parent = 0, Kind = NodeKind.Light, Position = pos,
            Light = new LightDef { Shadow = true, Priority = 4 },
        };
        n.Controllers =
        [
            new ControllerTrack { Type = 100, Columns = 3, Times = [0f], Values = [1f, 0.9f, 0.8f] },
            new ControllerTrack { Type = 112, Columns = 1, Times = [0f], Values = [9f] },
            new ControllerTrack { Type = 164, Columns = 1, Times = [0f], Values = [1f] },
        ];
        return n;
    }

    private static NodeDef Quad(string name, int index, int parent, string tex,
                                float x0, float y0, float x1, float y1, float z)
    {
        var mesh = new MeshDef
        {
            Positions = [new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            Indices = [0, 1, 2, 0, 2, 3],
            Textures = [tex, "", "", ""],
        };
        mesh.Uv[0] = [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY];
        Rebound(mesh);
        return new NodeDef { Name = name, Index = index, Parent = parent, Kind = NodeKind.TriMesh, Mesh = mesh };
    }

    private static void Rebound(MeshDef m)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (Vector3 p in m.Positions) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        m.BMin = lo;
        m.BMax = hi;
        m.Average = (lo + hi) * 0.5f;
        m.Radius = (hi - lo).Length() * 0.5f;
    }
}
