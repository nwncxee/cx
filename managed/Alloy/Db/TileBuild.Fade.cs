using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>Fog of war and the two ramps: the per-tile reveal the BSP visitor stepped, the roof fade the camera's
/// follow target drives, and the one product each tile publishes so a walk only touches tiles that moved.</summary>
public static partial class TileBuild
{
    // ---- fog of war and the two ramps ----

    /// <summary>The free function <c>AuroraSetTileFoggedByWar</c> @0x453320, which
    /// <c>CNWCTile::SetTileExplored</c> @0x90de40 calls with <c>explored == 0</c> whenever the module's
    /// fog-of-war bits are set. While a tile is fogged its reveal ramp is held at 0, exactly as the
    /// @0x481000 BSP visitor holds the engine's.</summary>
    public static void SetFoggedByWar(DbId scene, Vector3 pos, bool fogged)
    {
        int t = At(scene, pos);
        if (t < 0) return;
        if (SceneDb.TileFogged[t] == fogged) return;
        SceneDb.TileFogged[t] = fogged;
        if (fogged) SceneDb.TileReveal[t] = 0f;
        SceneDb.Tiles.Mark(t, Dirty.Visible);
        Publish(t);
    }

    /// <summary>Linux <c>Scene</c> +0x80 / Windows +0x78, <c>CAurScene::ResetAreaTileFading()</c>: every tile
    /// back to fully drawn and fully revealed.</summary>
    public static void ResetFading(DbId scene)
    {
        for (int t = 1; t < SceneDb.Tiles.High; t++)
        {
            if (!SceneDb.Tiles.LiveAt(t) || SceneDb.TileScene[t] != scene) continue;
            SceneDb.TileFade[t] = 1f;
            SceneDb.TileFadeAcc[t] = 0f;
            SceneDb.TileReveal[t] = SceneDb.TileFogged[t] ? 0f : 1f;
            Publish(t);
        }
    }

    /// <summary>The fade plane's TARGET: the camera's FOLLOW target's position, which
    /// <c>TileFadeRamp.OcclusionPlane</c> reaches through <c>Camera::BehaviorHost</c> -&gt;
    /// <c>AsBehaviorCameraFollow()</c> -&gt; <c>Gob::Position</c> -- the followed character, NOT the scene's
    /// focus, which is only the fallback (a plane through the origin ramps the wrong roofs).</summary>
    public static bool FadeTarget(DbId scene, DbId cam, out Vector3 target)
    {
        target = default;
        if (SceneDb.Cameras.Valid(cam))
        {
            DbId follow = SceneDb.CamFollow[cam.Index];
            if (SceneDb.Objects.Valid(follow)) { target = SceneDb.ObjPos[follow.Index]; return true; }
        }
        if (!SceneDb.Scenes.Valid(scene)) return false;
        target = SceneDb.SceneFocus[scene.Index];
        return target != Vector3.Zero;
    }

    /// <summary>The per-frame half, and the only hot method here: both per-tile ramps, over the dense table.
    /// The occlusion decision and the ramp arithmetic are <see cref="TileFadeRamp"/>'s
    /// (<see cref="TileFadeRamp.Plane"/>, <see cref="TileFadeRamp.TwoDBoxAbovePlane"/>,
    /// <see cref="TileFadeRamp.Step"/>) -- only the per-tile boxes differ. Allocation-free.</summary>
    public static void TickFade(DbId scene, Vector3 target, Vector3 camPos, float dt, int geomFadeMode,
                                DbId cam = default)
    {
        _fadeTicks++;
        int fading = 0, veiled = 0, classified = 0, out_ = 0;
        bool planeOk = TileFadeRamp.Plane(target, camPos, out float nx, out float ny, out float d);
        // ProcessTriMeshParts @0x480d40 takes the fade path for (mode == 1 && the plane stands) || mode == 4:
        // mode 4 ("always") fades the roof whatever the camera is doing, so it must not be gated on the
        // plane -- and with mode 4 there is no plane to test against, so every tilefade-1 tile fades out.
        bool live = TileFadeRamp.FadeLive(geomFadeMode, planeOk);
        bool always = live && !planeOk;
        // Only tiles IN FRONT of the camera are classified. TileFadeRamp.Tick's own cull takes all six
        // frustum planes (BoxVisible), as the engine's BSP visitor does; this culls against the
        // camera's near plane alone, which is the one that matters for the fault it prevents -- a roof
        // BEHIND the camera on the far side of the fade plane fading out unseen, and being transparent
        // already when the camera turns to it. SceneDb.CamVolume cannot serve here: a camera move CLEARS
        // CamVolumeSet (DbCamera.SetPosition/SetRotation) and the chase camera moves every frame, so at
        // animate time it is never set -- measured, "NO VIEW VOLUME YET" on every tick.
        bool cull = SceneDb.Cameras.Valid(cam);
        // DbCamera.Forward, not the raw column: the game turns the chase camera through its sub-object, so
        // CamRot stays identity and this near-plane test faced permanently straight down.
        Vector3 fwd = cull ? DbCamera.Forward(cam) : Vector3.Zero;
        if (cull && fwd.LengthSquared() < 1e-6f) cull = false;
        _culling = cull;
        for (int t = 1; t < SceneDb.Tiles.High; t++)
        {
            if (!SceneDb.Tiles.LiveAt(t) || SceneDb.TileScene[t] != scene) continue;
            int nb = SceneDb.TileFadeBoxN[t];
            bool inView = !cull || !BoxBehind(SceneDb.TileBmin[t], SceneDb.TileBmax[t], camPos, fwd);
            if (live && inView)
            {
                if (nb > 0) classified++;
                // CheckFadePlane @0x47bf30 walks the tile's tilefade-1 parts and fades it when ANY ONE of
                // them is not entirely beyond the plane -- part by part, never their union. A union box has
                // corners no part occupies, and those corners straddle planes every real part is clear of:
                // measured on the stock inn, the union faded 3 roof tiles where the engine faded 1.
                int baseAt = t * SceneDb.TileFadeBoxStride;
                bool fadingOut = always && nb > 0;
                if (!always)
                    for (int f = 0; f < nb; f++)
                    {
                        Vector3 lo = SceneDb.TileFadeBoxLo[baseAt + f], hi = SceneDb.TileFadeBoxHi[baseAt + f];
                        if (TileFadeRamp.TwoDBoxAbovePlane(lo.X, lo.Y, hi.X, hi.Y, nx, ny, d)) continue;
                        fadingOut = true;
                        break;
                    }
                if (fadingOut) out_++;
                TileFadeRamp.Step(ref SceneDb.TileFade[t], ref SceneDb.TileFadeAcc[t], fadingOut, dt);
            }
            else if (!live && SceneDb.TileFade[t] != 1f) { SceneDb.TileFade[t] = 1f; SceneDb.TileFadeAcc[t] = 0f; }

            float reveal = SceneDb.TileReveal[t];
            float want = SceneDb.TileFogged[t] ? 0f : 1f;
            if (reveal != want)
            {
                float step = dt * RevealRate;
                SceneDb.TileReveal[t] = want > reveal ? MathF.Min(want, reveal + step) : MathF.Max(want, reveal - step);
            }
            // Last, so a reveal that moved this tick reaches the parts this tick: publishing before the
            // reveal step left every fog-of-war ramp one frame behind its own value.
            Publish(t);
            if (SceneDb.TileFade[t] < 1f) fading++;
            if (SceneDb.TileReveal[t] < 1f) veiled++;
        }
        _fading = fading;
        _veiled = veiled;
        _classified = classified;
        _fadingOut = out_;
        _fadeMode = geomFadeMode;
        _planeOk = planeOk;
        _fadeTarget = target;
        if (fading > _fadingPeak) _fadingPeak = fading;
    }

    /// <summary>Is the world box entirely BEHIND the camera? The box's most forward corner is the one with
    /// the largest <c>dot(forward, corner - eye)</c>; when even that is negative, nothing of the box is in
    /// front of the eye.</summary>
    private static bool BoxBehind(Vector3 lo, Vector3 hi, Vector3 eye, Vector3 fwd)
    {
        float best = (fwd.X > 0f ? hi.X : lo.X) * fwd.X
                   + (fwd.Y > 0f ? hi.Y : lo.Y) * fwd.Y
                   + (fwd.Z > 0f ? hi.Z : lo.Z) * fwd.Z
                   - Vector3.Dot(fwd, eye);
        return best < 0f;
    }

    private static int _fadeTicks, _fading, _fadingPeak, _veiled, _fadeMode, _classified, _fadingOut, _fadeBoxSpill;
    /// <summary><see cref="SetTileLights"/> calls whose cell held no tile: a palette the area never applied.</summary>
    private static int _tileLightMiss;
    /// <summary><see cref="SetTileLights"/> calls, and how many handed main light 1 / 2 black (lightcolor.2da
    /// row 0): a black main light is the area's choice, not a lost palette. Since boot, these four: the area
    /// edge that resets the other tallies fires AFTER <c>LoadArea</c>'s tile loop, which is when they count.</summary>
    private static int _palCalls, _palBlack1, _palBlack2;
    private static int _modelsWithAabb, _modelsNoAabb, _aabbParts, _partsMade, _droppedReal, _realPlaced;
    /// <summary>Walkmesh objects made from a <c>.wok</c>, placements whose tile model already carried an
    /// <c>aabb</c> node (so no <c>.wok</c> is loaded), and the three outcomes that make no object: no readable
    /// resource (a named refusal), an EMPTY walkmesh (what a tileset's filler tiles ship), and one that parsed
    /// with nodes but no <c>aabb</c>.</summary>
    private static int _wokMade, _wokInModel, _wokMissing, _wokNoNode, _wokEmpty;
    private static int _wokSaidN;
    private static readonly System.Collections.Generic.HashSet<string> _wokSaidNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Generic.HashSet<string> _wokSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tile's WALKMESH, as its own invisible static object -- vanilla's floor and nothing else
    /// (<c>SceneBSPAddPart</c> @0x47ca10 files every <c>AsPartAABB</c> part at the BSP cell's <c>+0x90</c>, and
    /// <c>HitPartOnBSPNode</c> @0x47b790 casts against that). The geometry is NOT in the tile's <c>.mdl</c> but
    /// a separate <c>.wok</c> (restype 2016); a model with an <c>aabb</c> node loads none, so there is one.</summary>
    private static void Walkmesh(DbId scene, ModelDef tileDef, Vector3 pos, Quaternion rot, in Matrix4x4 place)
    {
        if (_wokSeen.Add(tileDef.Name) && _wokSeen.Count <= 12)
            _log?.Info($"TileBuild: walkmesh census: tile '{tileDef.Name}' modelAabb={Plan(tileDef).AabbNodes} "
                     + "(0 means its floor must come from the .wok)");
        // The self-check fixtures place tiles of their own, which have no .wok by design -- 1120 of them in a
        // run. They are not this session's world, so they are not on its census either: counted, they read as
        // the area's own missing floors (and the tally is session-long, because an area edge fires AFTER the
        // tile loop and would wipe the area's real numbers).
        bool fixture = tileDef.Name.StartsWith("cxtest", StringComparison.OrdinalIgnoreCase);
        if (Plan(tileDef).AabbNodes > 0) { if (!fixture) _wokInModel++; return; }
        ModelDef? wok = ModelCache.Acquire(tileDef.Name + ModelFeed.WokSuffix, out string? why);
        if (wok == null)
        {
            if (!fixture) { _wokMissing++; Said(tileDef.Name, why ?? "the cache would not hand one back"); }
            return;
        }
        // An empty walkmesh is a tileset's filler tile saying "no floor here", not a failure: the engine's own
        // loader ends with a surface mesh of no faces for exactly these. Counted apart from a refusal, and
        // never said out loud -- it was what made every area read as thousands of MISSING walkmeshes.
        if (wok.Nodes.Length == 0) { if (!fixture) _wokEmpty++; ModelCache.Release(wok); return; }
        bool any = false;
        foreach (NodeDef nd in wok.Nodes) if (nd.Kind == NodeKind.Aabb && nd.Aabb != null && nd.Aabb.Tree.Length > 0) { any = true; break; }
        if (!any) { _wokNoNode++; ModelCache.Release(wok); Said(tileDef.Name, "it parsed with no aabb node"); return; }
        DbId obj = NewObject(scene, wok, pos, rot, place, tile: false, walkmesh: true);
        if (obj.IsNull) { ModelCache.Release(wok); return; }
        // Filed with the cell's statics, so the tile's Drop (DeleteTile, SceneRevoke) frees it and releases the
        // .wok reference: a walkmesh lives exactly as long as its tile.
        Cell(pos, out int gx, out int gy);
        var key = CellKey(scene, gx, gy);
        if (!_statics.TryGetValue(key, out List<DbId>? list)) _statics[key] = list = new List<DbId>(2);
        list.Add(obj);
        _wokMade++;
    }

    /// <summary>Say the first walkmesh refusal out loud: a tileset whose <c>.wok</c>s CX cannot read has no
    /// floor anywhere, and the symptom (a creature nine metres up, a fogged frame) names nothing.</summary>
    private static void Said(string model, string why)
    {
        // The self-check fixtures run first and have no .wok by design; letting them spend the budget means
        // the real tileset's first failure is never named, which is the one that matters.
        if (model.StartsWith("cxtest", StringComparison.OrdinalIgnoreCase)) return;
        // DISTINCT names: a tileset's filler tiles repeat hundreds of times and would otherwise spend the
        // whole budget saying the same thing, which is exactly what hid the real failures once.
        if (_wokSaidNames.Contains(model)) return;
        _wokSaidNames.Add(model);
        if (_wokSaidN >= 8) return;
        _wokSaidN++;
        _log?.Always($"TileBuild: no walkmesh for tile '{model}' -- {why}.");
        if (_wokSaidN < 8) return;
        _log?.Always($"TileBuild: a tile walkmesh could not be loaded ({_wokMissing} with no .wok resource, "
                   + $"{_wokNoNode} parsed with no aabb node). Those tiles have NO FLOOR: a creature's "
                   + "ComputeZValue cast misses and the engine keeps its Z=9 sentinel.");
    }

    private static bool _planeOk, _culling;
    private static Vector3 _fadeTarget;

    /// <summary>Live NON-border tile placements: the area's own tiles, the ones a floor cast needs.</summary>
    private static int LiveReal()
    {
        int n = 0;
        for (int t = 1; t < SceneDb.Tiles.High; t++) if (SceneDb.Tiles.LiveAt(t) && !SceneDb.TileIsBorder[t]) n++;
        return n;
    }

    /// <summary>Live <c>aabb</c> parts in the whole database, counted on demand rather than tallied (a tally
    /// cannot be read against the area edge that resets it): whether the walkmeshes EXIST, independently of
    /// whether the hit set files them.</summary>
    private static int AabbLive(out int boxed)
    {
        int n = 0;
        boxed = 0;
        for (int p = 1; p < SceneDb.Parts.High; p++)
        {
            if (!SceneDb.Parts.LiveAt(p) || SceneDb.PartKindOf[p] != PartKind.Aabb) continue;
            n++;
            if (SceneDb.HitPartBMin[p].X <= SceneDb.HitPartBMax[p].X) boxed++;
        }
        return n;
    }

    /// <summary>Tiles whose model has <c>tilefade 1</c> geometry at all -- the population that CAN fade. Zero
    /// here with a zero peak says the tileset, not the ramp.</summary>
    private static int FadeCapable()
    {
        int n = 0;
        for (int t = 1; t < SceneDb.Tiles.High; t++) if (SceneDb.Tiles.LiveAt(t) && SceneDb.TileFadeBoxN[t] > 0) n++;
        return n;
    }

    /// <summary>Hand the tile's two ramps to the draw, and KEEP THEM APART. The camera-occlusion fade is an
    /// ALPHA -- the engine multiplies <c>TileData+0x70</c> into the part's own -- so it goes to
    /// <see cref="SceneDb.PartFade"/>. The fog-of-war reveal is FOG: under <c>TileData+0xc8 &lt; 1</c> the
    /// engine scales the fog colour by <c>reveal^2</c>, so it goes to <see cref="SceneDb.PartReveal"/>.</summary>
    private static void Publish(int t)
    {
        float fade = SceneDb.TileFade[t], reveal = SceneDb.TileReveal[t];
        // the common case: a still tile costs two compares, not a part walk
        if (_published[t] == fade && _publishedReveal[t] == reveal) return;
        _published[t] = fade;
        _publishedReveal[t] = reveal;
        DbId obj = SceneDb.TileObj[t];
        if (obj.IsNull || !SceneDb.Objects.Valid(obj)) return;
        int o = obj.Index, first = SceneDb.ObjPartFirst[o], n = SceneDb.ObjPartCount[o];
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            bool moved = false;
            if (SceneDb.PartFade[p] != fade) { SceneDb.PartFade[p] = fade; moved = true; }
            if (SceneDb.PartReveal[p] != reveal) { SceneDb.PartReveal[p] = reveal; moved = true; }
            if (moved) SceneDb.TouchPart(p, Dirty.Material);
        }
    }

    /// <summary>The fade product each tile last published, so <see cref="TickFade"/> walks a tile's parts only
    /// when it moved. Seeded at -1 so the first publish always lands.</summary>
    private static readonly float[] _published = NewPublished(), _publishedReveal = NewPublished();
    private static float[] NewPublished() { var a = new float[SceneDb.MaxTiles]; Array.Fill(a, -1f); return a; }
}
