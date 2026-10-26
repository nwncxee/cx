using System.Numerics;
using System;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary><b>The world view's draw</b> -- what <c>SeamDraw.Pass</c> calls. Thin on purpose: once the
/// ownership table is fed from the database, the four-digit radix order (<c>RebuildOrder</c>), the visibility,
/// the route and staging walks and <c>DrawOwned</c> are the ones Alloy already runs -- the argument of
/// ADR 0028. Re-sorting is gated on <see cref="DbSetGen"/>: nothing created or destroyed, nothing re-sorted.</summary>
public sealed unsafe partial class AlloyRenderer
{
    private static bool _dbPassArmed;
    private static long _dbPasses, _dbEmptyPasses;
    private static bool _saidDbEmpty, _saidCam, _saidExtent, _saidTiles;
    private static Vector3 _dbCamPos;
    private static nint _dbCamBeh;
    private static uint _dbCamFollow;

    /// <summary>The scene the last world pass drew: what the post-process passes read, having no scene
    /// argument of their own.</summary>
    internal static DbId DbPassScene;

    /// <summary>Register the world pass with <c>SeamDraw</c>. Called at Initialize; a no-op when Alloy is
    /// switched off at boot.</summary>
    internal static void DbArmPass(ExtenderLog log)
    {
        if (!Renderer.On || _dbPassArmed) return;
        _dbPassArmed = true;
        SeamDraw.Pass = DbWorldPass;
        log.Info("AlloyRenderer: the DB-mode world pass is armed -- Alloy draws the CX scene database's "
               + "records, and no engine Scene is read for any of it");
    }

    /// <summary>One world view. The scene and camera are the view the game asked for; the frame inputs the
    /// passes read were filled by the seam from the SNAPSHOT's matrices before this was called, so nothing
    /// here re-derives a camera.</summary>
    private static void DbWorldPass(DbId scene, DbId camera, in Db.FrameHoist h)
    {
        if (!AlloyGate.Alloy) return;
        _dbPasses++;
        DbPassScene = scene;
        // The camera chain, once: five separate things can leave the world camera at the origin, and this
        // names which.
        if (!_saidCam)
        {
            _saidCam = true;
            int ci = camera.Index;
            DbId cobj = SceneDb.Cameras.Valid(camera) ? SceneDb.CamObj[ci] : default;
            _log?.Info($"AlloyRenderer: [db] camera cam={camera.Value} valid={SceneDb.Cameras.Valid(camera)} "
                       + $"camScene={(SceneDb.Cameras.Valid(camera) ? SceneDb.CamScene[ci].Value : 0)} scene={scene.Value} "
                       + $"animOn={(SceneDb.Scenes.Valid(scene) && SceneDb.SceneAnimOn[scene.Index])} "
                       + $"camObj={cobj.Value} objValid={SceneDb.Objects.Valid(cobj)} "
                       + $"behavior=0x{(SceneDb.Cameras.Valid(camera) ? SceneDb.CamBehavior[ci] : 0):x} "
                       + $"camPos={SceneDb.CamPos[ci]} objPos={(SceneDb.Objects.Valid(cobj) ? SceneDb.ObjPos[cobj.Index] : default)} "
                       + $"objWorld.t=({(SceneDb.Objects.Valid(cobj) ? SceneDb.ObjWorld[cobj.Index].Translation : default)}) "
                       + $"fovy={DbCamera.ViewAngleOf(camera):F2} cameras={SceneDb.Cameras.Live}");
        }
        PassGen++;
        DbChartFrame();
        // The order and the area index, rebuilt only on a slot-set edge.
        long tOrder = FrameZones.Now;
        int live = DbOrder();
        FrameZones.Add(FrameZones.Zone.DbOrder, tOrder);
        if (live == 0)
        {
            _dbEmptyPasses++;
            // Said once, and only once the database actually holds something: an empty database at the main
            // menu is not a fault, an empty ORDER over a populated one is.
            if (!_saidDbEmpty && SceneDb.Parts.Live > 0)
            {
                _saidDbEmpty = true;
                _log?.Always($"AlloyRenderer: the scene database holds {SceneDb.Parts.Live} parts and the draw "
                           + "order is EMPTY -- nothing of this world is drawn. Either no part reached a draw "
                           + "recipe (no model), or none reached the mesh arena (no geometry resident).");
            }
            return;
        }
        // The light rig, from the database. Before the draw and after the order, exactly where the engine
        // walk sat in the engine path: the passes read FrameInputs' light set, and a frame that gathered after
        // them would light with the previous frame's rig.
        // CX-120: the keyhole column, before the lights and the draw. The route gate reads
        // SceneDb.PartDissolve, and until this wrote it the inn's roof never opened and the PC sat under it.
        // Order: after DbOrder (it walks the order) and before the stage that reads the column into the record.
        long tKeyhole = FrameZones.Now;
        DbKeyhole.Write(SceneDb.Cameras.Valid(camera) ? SceneDb.CamFacade[camera.Index] : 0, _order, _orderCount, _oDbPart, camera);
        FrameZones.Add(FrameZones.Zone.Keyhole, tKeyhole);
        long tLights = FrameZones.Now;
        // CX-188: the radius multiplier and the area's palette come off the frame's HOIST, read on the game
        // thread at the publish. They used to be SceneInputs.LatchAtten() (an engine call) and
        // SceneInputs.CurrentArea() (a walk of g_pAppManager -> module -> area, dereferenced again inside
        // Admit and the bake) -- reads of memory the game thread simulates, done from whichever thread draws.
        DbLights.Walk(scene, in h);
        FrameZones.Add(FrameZones.Zone.Lwalk, tLights);
        LatchFrameTarget();   // per-CONTEXT GL state: it has to be read before the switch below
        // A refused Enter is not handled here -- each pass inside refuses on its own Enter and says why.
        bool oneBracket = !Renderer.OnVulkan && GlContextService.Enter();   // a Vulkan view issues no GL
        try { DbDrawView(scene, camera, in h); }
        finally { if (oneBracket) GlContextService.Leave(); }
        // The world frame is in the present image now. With the GUI Alloy's, its flush draws over it there and
        // one blit hands the pair back. With the GUI stood down (/alloy gui 0, or a flush that cannot draw)
        // nothing composites over it, and whatever the engine's context still draws before the present --
        // nuklear's own chain -- has to land OVER the world rather than under a blit that follows it.
        if (!GuiPass.Capturing) PresentTarget.HandBackIfOwed();
    }

    /// <summary>The view's draw inside ONE 4.6 bracket (ADR 0032): the shadow bake, the simulation slots and
    /// the colour pass, which used to open and close a bracket each across a gap that issues no engine GL.
    /// Enter is reentrant, so their own pairs are depth counters. Nothing here may issue ENGINE GL.</summary>
    private static void DbDrawView(DbId scene, DbId camera, in Db.FrameHoist h)
    {
        // CX-120: the shadow half of the frame anchor. SceneInputs.FrameStart used to be reached through
        // RspShim, which is the engine's RenderSinglePass and does not run -- so without this call the sun
        // cascades were never baked and [zones] Shadow read 0,0. Position: after the light rig, before
        // anything draws.
        long tShadow = FrameZones.Now;
        SceneInputs.DbShadowSlot(camera);
        FrameZones.Add(FrameZones.Zone.Shadow, tShadow);
        // CX-120: the particle simulation and the beam enrolment, where the engine path runs them -- the sim
        // before the frame anchor so this frame's draw gets this frame's particles (#261), the beams from the
        // database's own `beaming` parts rather than the engine's bucket 15. Both are pure database reads, so
        // neither needs the engine's GL context and both are safe on the render thread.
        EmitterSim.StepDb(scene, DbFeed.FrameSeq);
        Beams.EnrollDb(scene);
        Halos.EnrollDb(scene, h.RadiusMult);
        // CX-120 bring-up: the whole drawn set's world extent against the camera, once, after the order is
        // built. "303 draws and a flat frame" is a question about WHERE things are, and no counter carries a
        // position. One pass over the order; this is a one-shot.
        // At pass 600, not pass 1: the first world pass happens before the chase behaviour has placed the
        // camera, so a one-shot there measures the boot state and says nothing about the settled frame. That
        // trap cost two runs.
        if (!_saidExtent && _orderCount > 0 && _dbPasses > 600)
        {
            _saidExtent = true;
            var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
            var wlo = new Vector3(float.MaxValue); var whi = new Vector3(float.MinValue);
            int withMesh = 0, inScene = 0, otherScene = 0, noRange = 0;
            for (int k = 0; k < _orderCount; k++)
            {
                int i = _order[k];
                int part = _oDbPart[i];
                if (part < 0) continue;
                Vector3 t = SceneDb.PartWorld[part].Translation;
                lo = Vector3.Min(lo, t); hi = Vector3.Max(hi, t);
                withMesh++;
                if (DrawRecipe.Range(_oDbRecipe[i]) < 0) noRange++;
                DbId po = SceneDb.PartObj[part];
                bool mine = SceneDb.Objects.Valid(po) && SceneDb.ObjScene[po.Index].Value == scene.Value;
                if (mine) { inScene++; wlo = Vector3.Min(wlo, t); whi = Vector3.Max(whi, t); }
                else otherScene++;
            }
            // Is the transform chain right? Put a few parts' mesh centres through model*view*proj on the CPU
            // and say whether they land in the clip box. Inside + nothing shaded = the vertex data or the VAO;
            // outside = the matrices. No other measurement separates those two.
            ref readonly AlloyView wv = ref AlloyCamera.WorldView;
            Matrix4x4 vp = wv.View * wv.Proj;
            int inBox = 0, behind = 0, tested = 0;
            for (int k = 0; k < _orderCount && tested < 400; k++)
            {
                int i = _order[k];
                int part = _oDbPart[i];
                if (part < 0) continue;
                DbId po = SceneDb.PartObj[part];
                if (!SceneDb.Objects.Valid(po) || SceneDb.ObjScene[po.Index].Value != scene.Value) continue;
                tested++;
                Vector3 c = Vector3.Transform(DrawRecipe.Centre(_oDbRecipe[i]), SceneDb.PartWorld[part]);
                Vector4 clip = Vector4.Transform(new Vector4(c, 1f), vp);
                if (clip.W <= 0f) { behind++; continue; }
                if (MathF.Abs(clip.X) <= clip.W && MathF.Abs(clip.Y) <= clip.W
                    && clip.Z >= -clip.W && clip.Z <= clip.W) inBox++;
            }
            _log?.Info($"AlloyRenderer: [db] clip test over {tested} world parts: inBox={inBox} behind={behind} "
                       + $"| view.M41..43=({wv.View.M41:F1},{wv.View.M42:F1},{wv.View.M43:F1}) "
                       + $"proj.M11={wv.Proj.M11:F3} proj.M34={wv.Proj.M34:F2} valid={wv.Valid}");
            _log?.Info($"AlloyRenderer: [db] order {_orderCount}: inScene={inScene} otherScene={otherScene} "
                       + $"noRange={noRange} | world extent lo=({wlo.X:F1},{wlo.Y:F1},{wlo.Z:F1}) "
                       + $"hi=({whi.X:F1},{whi.Y:F1},{whi.Z:F1})");
            DbId pl = SceneDb.CamFollow[camera.Index];
            Vector3 target = SceneDb.Objects.Valid(pl) ? SceneDb.ObjPos[pl.Index] : new Vector3(float.NaN);
            _log?.Info($"AlloyRenderer: [db] extent of {withMesh} ordered parts "
                       + $"lo=({lo.X:F1},{lo.Y:F1},{lo.Z:F1}) hi=({hi.X:F1},{hi.Y:F1},{hi.Z:F1}) "
                       + $"| eye=({_eyePos.X:F1},{_eyePos.Y:F1},{_eyePos.Z:F1}) "
                       + $"followTarget=({target.X:F1},{target.Y:F1},{target.Z:F1}) "
                       + $"near={AlloyCamera.WorldView.Near:F2} far={AlloyCamera.WorldView.Far:F2}");
        }
        // CX-114 bring-up: the tiles render flat while an object model renders correctly, so the question is
        // which MeshDef a TILE part's recipe resolved against the one its part column carries, and whether the
        // UV lane the arena uploaded for it is real. One-shot, after the camera has settled.
        if (!_saidTiles && _orderCount > 0 && _dbPasses > 600)
        {
            _saidTiles = true;
            int shown = 0;
            for (int k = 0; k < _orderCount && shown < 4; k++)
            {
                int i = _order[k];
                int part = _oDbPart[i];
                if (part < 0) continue;
                DbId po = SceneDb.PartObj[part];
                if (!SceneDb.Objects.Valid(po) || (SceneDb.ObjFlags[po.Index] & ObjFlag.Tile) == 0) continue;
                shown++;
                int rec = _oDbRecipe[i];
                var mdl = SceneDb.PartModel[part];
                var pm = SceneDb.PartMeshDef[part];
                var rm = DrawRecipe.Mesh(rec);
                int rRange = DrawRecipe.Range(rec), pRange = SceneDb.PartMesh[part];
                string uvs = "-";
                if (pm != null)
                    uvs = $"uv0={pm.Uv[0].Length} uv1={pm.Uv[1].Length} verts={pm.Positions.Length} "
                        + (pm.Uv[0].Length > 0 ? $"first=({pm.Uv[0][0].X:F3},{pm.Uv[0][0].Y:F3}) " : "")
                        + $"tex0='{pm.Textures[0]}' tex1='{pm.Textures[1]}' lm={pm.Lightmapped}";
                string arena = "-";
                if (MeshArena.VertexProbe(rRange, 0, out _, out _, out Vector2 au, out Vector2 ac)
                    && MeshArena.VertexProbe(rRange, 1, out _, out _, out Vector2 bu, out _))
                    arena = $"uv[0]=({au.X:F3},{au.Y:F3}) uv[1]=({bu.X:F3},{bu.Y:F3}) chart0=({ac.X:F3},{ac.Y:F3})";
                _log?.Info($"AlloyRenderer: [db tile] part={part} model='{mdl?.Name}' node={SceneDb.PartNode[part]} "
                           + $"kind={SceneDb.PartKindOf[part]} merged={SceneDb.PartMerged[part]} "
                           + $"| partMesh={(pm == null ? "NULL" : pm.Positions.Length + "v")} recipeMesh={(rm == null ? "NULL" : rm.Positions.Length + "v")} "
                           + $"same={ReferenceEquals(pm, rm)} partRange={pRange} recipeRange={rRange} "
                           + $"| partMeshDef: {uvs} | recipeTex0={DrawRecipe.Tex(rec, 0).Value} handle={(DrawRecipe.TexHandle(rec, 0) != 0 ? "yes" : "NO")} "
                           + $"| arena@recipeRange: {arena}");
            }
        }
        DbPcProbe(camera);
        if (SceneDb.Cameras.Valid(camera))
        {
            _dbCamPos = DbCamera.PosOf(camera);
            _dbCamBeh = SceneDb.CamBehavior[camera.Index];
            _dbCamFollow = SceneDb.CamFollow[camera.Index].Value;
        }
        _dbPassScene = scene;
        bool prev = _inColourPass;
        _inColourPass = true;
        DrawOwned();
        _inColourPass = prev;
    }

    /// <summary>The DB world pass's clause on the period line.</summary>
    internal static string DbPassNote()
        => $"dbpass: {_dbPasses} views, order {_orderCount} | eye=({_eyePos.X:F1},{_eyePos.Y:F1},{_eyePos.Z:F1}) "
         + $"cam=({_dbCamPos.X:F1},{_dbCamPos.Y:F1},{_dbCamPos.Z:F1}) beh=0x{_dbCamBeh:x} "
         + $"follow={_dbCamFollow} | {DbSkyNote()} | {DbLights.Stats()} | {DbKeyhole.Stats()}{DbNoDrawNote()}"
         + (_dbEmptyPasses > 0 ? $", {_dbEmptyPasses} EMPTY" : "")
         + (_dbOrphan > 0 ? $", {_dbOrphan} orphaned parts" : "")
         + (_dbEnv > 0 ? $", {_dbEnv} env-mapped records" : "");
}
