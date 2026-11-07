using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The tile's lights and flames: the two Scene vtable slots that re-colour and animate them, and the
/// registration that files a plan's light nodes while they are still reachable as the tile is built.</summary>
public static partial class TileBuild
{
    // ---- Scene vtable 0x40: SetTileLights ----

    /// <summary>Linux <c>Scene</c> +0x40 / Windows +0x38, <c>SetTileLights(Vector const&amp;, Vector const&amp;,
    /// Vector const&amp;)</c>: re-colour the tile's two MAIN lights, ported write for write from @0x55edf0 --
    /// colour, then <c>range</c>/<c>rangeAlt</c> 10/12 and 5/8, <c>castShadows</c> 1, <c>priority</c> 4. The
    /// engine's early-out on an unchanged colour is kept: it is what stops a re-load re-dirtying every tile.</summary>
    public static void SetTileLights(DbId scene, Vector3 pos, Vector3 c1, Vector3 c2)
    {
        _palCalls++;
        if (c1 == Vector3.Zero) _palBlack1++;
        if (c2 == Vector3.Zero) _palBlack2++;
        int t = At(scene, pos);
        if (t < 0) { _tileLightMiss++; return; }
        bool moved = false;
        for (int c = 0; c < SceneDb.TileLightCount[t]; c++)
        {
            int k = SceneDb.TileLightIds[t * SceneDb.TileLightStride + c];
            if (k < 0 || !SceneDb.Lights.LiveAt(k)) continue;
            int slot = SceneDb.LitMainSlot[k];
            if (slot != 0 && slot != 1) continue;
            Vector3 want = slot == 0 ? c1 : c2;
            if (SceneDb.LitColour[k] == want) continue;
            SceneDb.LitColour[k] = want;
            SceneDb.LitRange[k] = slot == 0 ? Main1Range : Main2Range;
            SceneDb.LitRangeAlt[k] = slot == 0 ? Main1RangeAlt : Main2RangeAlt;
            SceneDb.LitShadow[k] = true;
            SceneDb.LitPriority[k] = MainPriority;
            SceneDb.Lights.Mark(k, Dirty.Material);
            moved = true;
        }
        if (moved) SceneDb.Tiles.Mark(t, Dirty.Material);
    }

    // ---- Scene vtable 0x48: SetTileFlames ----

    /// <summary>Linux <c>Scene</c> +0x48 / Windows +0x40, <c>SetTileFlames(Vector const&amp;, CAurObject*,
    /// CAurObject*)</c>: the two <c>fx_flame01</c> "sourcelight" objects the tile's source-light rows asked
    /// for. The game layer mints them (<c>CNWCTile::SetSourceLightColor</c> @0x90d3c0), so CX only files which
    /// objects a tile's flames are. Called with two nulls first, to clear.</summary>
    public static void SetTileFlames(DbId scene, Vector3 pos, DbId flame1, DbId flame2)
    {
        int t = At(scene, pos);
        if (t < 0) return;
        ModelDef? def = SceneDb.TileModel[t];
        MergePlan? plan = def != null ? Plan(def) : null;
        PlaceFlame(t, ref SceneDb.TileFlame1[t], flame1, plan?.Sl1 ?? -1, plan?.Sl1Pos ?? Vector3.Zero, plan?.Sl1Rot ?? Quaternion.Identity);
        PlaceFlame(t, ref SceneDb.TileFlame2[t], flame2, plan?.Sl2 ?? -1, plan?.Sl2Pos ?? Vector3.Zero, plan?.Sl2Rot ?? Quaternion.Identity);
        SceneDb.Tiles.Mark(t, Dirty.Tree);
    }

    /// <summary>One flame slot, ported from <c>Scene::SetSingleTileFlame</c> @0x55ef30: an unchanged slot does
    /// nothing; otherwise the old object leaves (its lights re-shade) and the new one takes the
    /// <c>sl1</c>/<c>sl2</c> PART's pose. CX's parts are MODEL-space, so the flame's world pose is the tile
    /// placement composed with the part's. No <c>sl</c> part: the object is left be and the refusal counted.</summary>
    private static void PlaceFlame(int t, ref DbId slot, DbId want, int node, Vector3 nodePos, Quaternion nodeRot)
    {
        if (slot == want) return;
        if (!slot.IsNull) { MarkLightsOf(slot); slot = default; }
        if (want.IsNull) return;
        // VANILLA SKIPS, SILENTLY. Scene::SetSingleTileFlame @0x55ef30 guards the whole placement on
        // `srcPart != 0 && flame != 0`, so a tile whose model has no sl1/sl2 node clears the slot and never
        // attaches the object -- the flame simply does not appear. That is content, not a failure of ours
        // (Kingmaker's tilesets do it), so it is counted on the period line and not said out loud.
        if (node < 0) { _flamesNoSl++; return; }
        if (!SceneDb.Objects.Valid(want)) return;
        int o = want.Index;
        Quaternion tileRot = SceneDb.TileOrient[t] == 0 && SceneDb.TileModel[t] == null
            ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitZ, SceneDb.TileOrient[t] * 90f * (MathF.PI / 180f));
        SceneDb.ObjPos[o] = SceneDb.TilePos[t] + Vector3.Transform(nodePos, tileRot);
        SceneDb.ObjRot[o] = Quaternion.Normalize(tileRot * nodeRot);
        SceneDb.ObjScene[o] = SceneDb.TileScene[t];
        SceneDb.ObjVisible[o] = true;
        Matrix4x4 w = Matrix4x4.CreateFromQuaternion(SceneDb.ObjRot[o]);
        w.Translation = SceneDb.ObjPos[o];
        SceneDb.ObjWorld[o] = w;
        SceneDb.Objects.Mark(o, Dirty.Xform | Dirty.Visible);
        MarkLightsOf(want);
        slot = want;
        _flames++;
    }

    /// <summary>The engine's <c>UpdateStaticLighting</c> over a flame gob's own lights, as a dirty mark: the
    /// static lighting a flame contributes has to be recomputed where it used to stand and where it now does.</summary>
    private static void MarkLightsOf(DbId obj)
    {
        if (!_objLights.TryGetValue(obj.Value, out List<int>? list)) return;
        foreach (int k in list) if (SceneDb.Lights.LiveAt(k)) SceneDb.Lights.Mark(k, Dirty.Xform | Dirty.Material);
    }

    private static int _flames, _flamesNoSl, _modelPulled, _swapOther;

    /// <summary>Linux <c>Scene</c> +0xf0 / Windows +0xe8, <c>GetNumTileFlames(Vector const&amp;)</c>.</summary>
    public static int NumTileFlames(DbId scene, Vector3 pos)
    {
        int t = At(scene, pos);
        if (t < 0) return 0;
        return (SceneDb.TileFlame1[t].IsNull ? 0 : 1) + (SceneDb.TileFlame2[t].IsNull ? 0 : 1);
    }

    /// <summary>Linux <c>Scene</c> +0xf8 / Windows +0xf0, <c>GetNumTileLights(Vector const&amp;)</c>.</summary>
    public static int NumTileLights(DbId scene, Vector3 pos)
    {
        int t = At(scene, pos);
        return t < 0 ? 0 : SceneDb.TileLightCount[t];
    }

    // ---- light registration ----

    /// <summary>File the tile's lights into <see cref="SceneDb.Lights"/> AS THE TILE IS BUILT: the plan's
    /// light nodes are still reachable here, where a part-tree walk of the engine's collapsed tile could not
    /// reach them.</summary>
    private static void RegisterLights(DbId tile, DbId obj, MergePlan plan, in Matrix4x4 place, int[]? into)
    {
        if (into != null) Array.Fill(into, -1);
        if (plan.Lights.Length == 0) { if (into != null) SceneDb.TileLightCount[tile.Index] = 0; return; }
        int count = 0;
        foreach (PlanLight pl in plan.Lights)
        {
            if (into != null && count >= SceneDb.TileLightStride)
            { _lightSpill++; Refuse($"a tile model has more than {SceneDb.TileLightStride} light nodes -- the rest are not registered"); break; }
            DbId id = SceneDb.Lights.Alloc();
            if (id.IsNull) { _refusedLights++; Refuse($"the light table is full at {SceneDb.MaxLights} lights -- tile lights are being dropped"); break; }
            int k = id.Index;
            SceneDb.LitObj[k] = obj;
            // The part that instances the light node, when the part tree has one. Registered with DbInputs as
            // well, because the renderer has to see ONE light list however a light arrived -- and a tileset's
            // lights do not hang off an object's part tree the way a gob's do.
            int lp = PartOfNode(obj, pl.Node);
            SceneDb.LitPart[k] = lp;
            if (lp >= 0) DbInputs.RegisterTileLight(lp, k);
            else _noPartForLight++;
            SceneDb.LitTile[k] = tile;
            SceneDb.LitPos[k] = Vector3.Transform(pl.Pos, place);
            SceneDb.LitColour[k] = pl.Colour;
            SceneDb.LitRange[k] = pl.Range;
            SceneDb.LitRangeAlt[k] = pl.RangeAlt;
            SceneDb.LitMultiplier[k] = pl.Multiplier;
            SceneDb.LitPriority[k] = pl.Priority;
            SceneDb.LitShadow[k] = pl.Shadow;
            SceneDb.LitAmbientOnly[k] = pl.AmbientOnly;
            SceneDb.LitDynamic[k] = pl.Dynamic;
            SceneDb.LitMainSlot[k] = pl.MainSlot;
            SceneDb.LitDef[k] = pl.Def;
            if (into != null) into[count] = k;
            else { if (!_objLights.TryGetValue(obj.Value, out List<int>? l)) _objLights[obj.Value] = l = new List<int>(2); l.Add(k); }
            count++;
            _litRegistered++;
        }
        if (into != null) SceneDb.TileLightCount[tile.Index] = count;
    }

    /// <summary>A static placeable's lights, by its object handle: the list that frees them when the
    /// placeable goes, so an area change leaks no row per placeable lamp.</summary>
    private static readonly Dictionary<uint, List<int>> _objLights = new();
    /// <summary>One tile's light slots while they are being filled: the game thread is the only writer, so a
    /// single buffer serves and the fill allocates nothing.</summary>
    private static readonly int[] _lightScratch = new int[SceneDb.TileLightStride];
    private static int _noPartForLight;

    /// <summary>Free every light row <paramref name="obj"/> owns (a tile's through its slots, a static's
    /// through <see cref="_objLights"/>), symmetrically with <see cref="DbInputs"/>.</summary>
    private static void FreeLights(DbId obj, int tileSlot)
    {
        if (tileSlot >= 0)
        {
            int baseAt = tileSlot * SceneDb.TileLightStride;
            for (int c = 0; c < SceneDb.TileLightCount[tileSlot]; c++)
            {
                DropLight(SceneDb.TileLightIds[baseAt + c]);
                SceneDb.TileLightIds[baseAt + c] = -1;
            }
            SceneDb.TileLightCount[tileSlot] = 0;
            return;
        }
        if (!_objLights.Remove(obj.Value, out List<int>? list)) return;
        foreach (int k in list) DropLight(k);
    }

    private static void DropLight(int k)
    {
        if (k < 0 || !SceneDb.Lights.LiveAt(k)) return;
        int lp = SceneDb.LitPart[k];
        if (lp >= 0) DbInputs.PartDestroyed(lp);
        SceneDb.LitPart[k] = -1;
        SceneDb.LitTile[k] = default;
        SceneDb.LitDef[k] = null;
        SceneDb.Lights.Free(SceneDb.Lights.IdAt(k));
    }

    /// <summary>The light rows a tile owns, as slot indices (-1 for an empty slot).</summary>
    public static int LightAt(int tileSlot, int which)
        => (uint)which < (uint)SceneDb.TileLightCount[tileSlot]
           ? SceneDb.TileLightIds[tileSlot * SceneDb.TileLightStride + which] : -1;

    /// <summary>The part in <paramref name="obj"/>'s range that instances model node
    /// <paramref name="node"/>, or -1 while <see cref="PartTree.Build"/> is a stub. Scanned rather than
    /// indexed: reference expansion means the range is not node-for-node.</summary>
    private static int PartOfNode(DbId obj, int node)
    {
        if (obj.IsNull || !SceneDb.Objects.Valid(obj)) return -1;
        int o = obj.Index, first = SceneDb.ObjPartFirst[o], n = SceneDb.ObjPartCount[o];
        for (int k = 0; k < n; k++)
            if (SceneDb.PartNode[first + k] == node && ReferenceEquals(SceneDb.PartModel[first + k], SceneDb.ObjModel[o]))
                return first + k;
        return -1;
    }
}
