using CX.Framework;
using CX.Services.Anim;

namespace CX.Alloy;

/// <summary>
/// Every Alloy pass's lifecycle, declared once: a new pass writes whatever methods it actually has and adds
/// one entry here. Order is <see cref="FrameGraph"/>'s. A <c>null</c> slot is a pass that genuinely has no
/// such concern; where a slot is missing and probably should not be, the line says so.
/// </summary>
internal static class RenderPasses
{
    /// <summary>Fill the registry and seal it. Once, from <c>AlloyRenderer.Initialize</c>.</summary>
    internal static void Register(ExtenderLog log)
    {
        var r = PassRegistry.Global;
        if (r.Sealed) return;

        // #211: not a pass -- the texture table every pass draws from. Registered here anyway, because what
        // it needs IS a pass lifecycle: both edges, an Attach for its boot gate, and a SelfCheck.
        r.Register(new PassLifecycle
        {
            Name = "ownedtex",
            Attach = l => { OwnedTextures.Attach(l); KnobSurface.Note(AlloyRenderer.VramNote); },
            Install = OwnedTextures.Install,
            SelfCheck = l => { OwnedTextures.SelfCheck(l); BcnEncode.SelfCheck(l); DdsReader.SelfCheck(l); RasterReader.SelfCheck(l); TexStream.SelfCheck(l); ResSources.SelfCheck(l); TexLoad.SelfCheck(l); ModelLoad.SelfCheck(l); AlloyRenderer.SelfCheckHandles(l); AlloyRenderer.SelfCheckNameRetired(l); OwnedTextures.SelfCheckCache(l); },
            // #259: no Revoke. Its resources are keyed on CONTENT, so a part-table wipe
            // invalidates nothing in them -- and since they are only ever populated at the engine's upload
            // funnel, wiping them on an edge the engine does not rebuild across would leave every texture
            // borrowed for the rest of the session. The engine's own DeleteTextures IS the world edge here.
            ContextLost = OwnedTextures.ContextLost,
        }, log);

        // Nor is this one: the shared-VDO capacity pin.
        r.Register(new PassLifecycle
        {
            Name = "enginepools",
            Install = EnginePools.Install,
            SelfCheck = l => StagePool.SelfCheck(l),
        }, log);

        // Automatic mesh LOD: not a draw pass either -- the detail thresholds and the level picker over the
        // mesh arena's generated runs. Its Revoke is the world edge: a new area's triangle tally is its own.
        r.Register(new PassLifecycle
        {
            Name = "meshlod",
            Attach = MeshLod.Attach,
            SelfCheck = l => MeshLod.SelfCheck(l),
            Revoke = MeshLod.Revoke,
            Stats = MeshLod.Stats,
        }, log);

        // #673: the charter -- MeshLod's sibling with its own vertex arena and a disk ledger. Same three edges.
        r.Register(new PassLifecycle
        {
            Name = "meshchart",
            Attach = MeshChart.Attach,
            SelfCheck = l => MeshChart.SelfCheck(l),
            Revoke = MeshChart.Revoke,
            ContextLost = MeshChart.ContextLost,
            Stats = MeshChart.Stats,
        }, log);

        // #674: the lightmap bake -- the pages, the scratch pair and the per-slot records. Revoke rewinds the
        // packing (every rect names an instance of an area that is gone); ContextLost drops the names.
        r.Register(new PassLifecycle
        {
            Name = "lightmap",
            Attach = LightmapBake.Attach,
            Install = LightmapBake.Install,
            SelfCheck = l => { LightmapBake.SelfCheck(l); LightmapBake.HoldSelfCheck(l); LightmapBake.AreaSelfCheck(l); BakeCache.SelfCheck(l); },
            Revoke = LightmapBake.Revoke,
            ContextLost = LightmapBake.ContextLost,
            Stats = LightmapBake.Stats,
        }, log);

        // #129: FrameSnapshot first, because FrameGraph's own check asserts that the ownable-bucket list AGREES
        // with whether Alloy water can draw -- so water's boot knobs have to be read before that check runs.
        r.Register(new PassLifecycle
        {
            Name = "water",
            Attach = FrameSnapshot.Attach,
            SelfCheck = l => FrameSnapshot.SelfCheck(l),
            Stats = FrameSnapshot.Stats,
        }, log);
        // CX-51: the screen-space reflections. No edges of its own -- the pass's targets are the device's.
        r.Register(new PassLifecycle { Name = "ssr", Stats = Ssr.Stats }, log);
        // CX-29: not a pass and not Alloy's -- the pose layer runs under either renderer. Registered for its
        // clause on the [alloy] period line only.
        r.Register(new PassLifecycle { Name = "pose", Stats = PoseService.Stats }, log);
        r.Register(new PassLifecycle
        {
            Name = "sky",
            // The sky's GL objects are one program per custom fragment stage the content names; it captures
            // nothing from the world, so it has no Revoke.
            Attach = SkyStage.Attach,
            SelfCheck = l => SkyStage.SelfCheck(l),
            ContextLost = SkyStage.Forget,
            Stats = _ => SkyBox.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "native",
            // Content stages' native versions and the images and passes they declare, for every host (sky, material
            // pairs, post): GL objects only, dropped at the module edge with the programs that read them.
            SelfCheck = StageResources.SelfCheck,
            ContextLost = StageResources.Forget,
            Stats = _ => StageResources.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            // Not a pass: the second-story fade's arithmetic, which the tile stream runs, checked first in the log.
            Name = "tilefade",
            SelfCheck = l => TileFadeRamp.SelfCheck(l),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "grass",
            Install = GrassField.Install,
            ContextLost = GrassField.ContextLost,
            SelfCheck = GrassField.SelfCheck,
            // #175 found this one: GrassField.Forget had NO caller anywhere, so grass carried its patch
            // table and slot map straight across a part-table wipe. The registry is what makes that visible.
            Revoke = GrassField.Forget,
            Stats = _ => GrassField.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "border",
            Install = AreaBorder.Install,
            SelfCheck = AreaBorder.SelfCheck,
            Stats = _ => AreaBorder.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "beams",
            Install = Beams.Install,
            SelfCheck = Beams.SelfCheck,
            Revoke = Beams.Forget,
            Stats = _ => Beams.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "halos",
            Install = Halos.Install,
            SelfCheck = Halos.SelfCheck,
            Revoke = Halos.Revoke,
            Stats = _ => Halos.Stats(),
        }, log);
        r.Register(new PassLifecycle
        {
            // CX-86: registered BEFORE "particles" so its SelfCheck runs first -- the pass reads the pool this
            // one owns, and a failing simulation check should be the first line in the log, not the second.
            Name = "emitsim",
            Install = EmitterSim.Install,
            SelfCheck = EmitterSim.SelfCheck,
            Revoke = EmitterSim.Reset,
            ContextLost = EmitterSim.Reset,
            Stats = f => EmitterSim.Stats(f),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "particles",
            Install = Particles.Install,
            SelfCheck = Particles.SelfCheck,
            ContextLost = Particles.ContextLost,
            Revoke = Particles.Forget,
            Stats = f => Particles.Stats(f),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "weather",
            // No Revoke: the area's soak, map and prints reset on the engine's own per-area SetWeather/StartWeather
            // call (Weather.Enter), and the shelter cache goes with the caster cover bake it was baked from.
            Install = Weather.Install,
            SelfCheck = Weather.SelfCheck,
            ContextLost = Weather.ContextLost,
            Stats = _ => Weather.Stats(),
        }, log);
        // CX-273: the ground decals' layer -- two frame-sized targets and an FBO, so it goes with the context.
        r.Register(new PassLifecycle
        {
            Name = "decals",
            Attach = DecalLayer.Attach,
            SelfCheck = DecalLayer.SelfCheck,
            ContextLost = DecalLayer.Forget,
            Stats = DecalLayer.Stats,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "light",
            Attach = AlloyLights.Attach,
            SelfCheck = l => AlloyLights.SelfCheck(l),
            ContextLost = AlloyLights.Forget,
            Revoke = SceneInputs.Revoke,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "probe",
            Attach = EnvProbe.Attach,
            SelfCheck = EnvProbe.SelfCheck,
            ContextLost = EnvProbe.Forget,
            // #209: was missing. The re-bake keys on a BSP POINTER, so a recycled address carried the
            // previous area's cube into the new one.
            Revoke = EnvProbe.Revoke,
        }, log);
        // #341: the fog's TARGET, which is a look decision and therefore a set of dials -- the ramp itself
        // stays the area author's. No frame slot: every stage uploads it through UploadFeatureTerms.
        r.Register(new PassLifecycle
        {
            Name = "fog",
            SelfCheck = SceneFog.SelfCheck,
            Revoke = SceneFog.Revoke,
        }, log);
        // The volumetric fog's volume: three frame-independent 3D textures and two programs, so the context takes them;
        // a new area leaves last frame's medium describing another place.
        r.Register(new PassLifecycle
        {
            Name = "vfog",
            Attach = FogVolume.Attach,
            SelfCheck = FogVolume.SelfCheck,
            Revoke = FogVolume.Revoke,
            ContextLost = FogVolume.Forget,
            Stats = FogVolume.Stats,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "post",
            Attach = PostChain.Attach,
            SelfCheck = l => PostChain.SelfCheck(l),
            ContextLost = PostChain.Forget,
            Stats = f => "post " + PostChain.Stats(f),
        }, log);
        // #375: the display-space stack after the resolve; its dials are the post group's. No Revoke -- it
        // captures nothing from the world; its inputs are the engine's option globals, read per frame.
        // #388: the camera velocity target the accumulator reprojects through. Revoke resets the previous
        // view-projection so the first frame after an area transition paints zero rather than a jump.
        r.Register(new PassLifecycle
        {
            Name = "motion",
            Attach = MotionVectors.Attach,
            SelfCheck = l => MotionVectors.SelfCheck(l),
            Revoke = MotionVectors.Revoke,
            Stats = MotionVectors.Stats,
        }, log);
        // #493: the vis-ID target the prepass writes beside its depth; a frame-sized GL object, so it goes
        // with the context.
        r.Register(new PassLifecycle
        {
            Name = "idtarget",
            SelfCheck = l => IdTarget.SelfCheck(l),
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "deferred",
            Attach = DeferredLights.Attach,
            Stats = DeferredLights.Stats,
        }, log);
        // CX-188 step 3: the per-pixel shadow visibility record -- a frame-sized texture pair, so it goes with
        // the context too.
        r.Register(new PassLifecycle
        {
            Name = "defshadow",
            Attach = DeferredShadows.Attach,
            ContextLost = DeferredShadows.Forget,
            Stats = DeferredShadows.Stats,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "pairtiles",
            Attach = PairShadows.Attach,
            SelfCheck = l => PairShadows.SelfCheck(l),
            ContextLost = PairShadows.Forget,
            Stats = PairShadows.Stats,
        }, log);
        r.Register(new PassLifecycle { Name = "a2c", Stats = CoverageLayer.Stats }, log);
        r.Register(new PassLifecycle { Name = "oit", Stats = OitPass.Stats }, log);
        // The sky-visibility map. Its depth view belongs to CasterPass (an ortho caster render); what is
        // registered here is the map baked from it -- a GL texture and a compute program, so it goes with
        // the context, and the area edge is what makes the bake another area's.
        r.Register(new PassLifecycle
        {
            Name = "skyview",
            Attach = SkyView.Attach,
            SelfCheck = SkyView.SelfCheck,
            ContextLost = SkyView.Forget,
            Revoke = CasterPass.SkyAreaEdge,
            Stats = _ => SkyView.Stats(),
        }, log);
        // Not a pass: the render seam's device every migrated pass records against. Registered ahead of them so
        // a lost context drops it before any of them rebuilds.
        r.Register(new PassLifecycle
        {
            Name = "rhi",
            ContextLost = AlloyDevice.Forget,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "ao",
            Attach = SsaoPass.Attach,
            SelfCheck = l => SsaoPass.SelfCheck(l),
            // No ContextLost: everything the estimate owns is the Vulkan device's, replaced whole with it.
            Stats = SsaoPassVk.Stats,
        }, log);
        r.Register(new PassLifecycle
        {
            Name = "poststack",
            Attach = PostStack.Attach,
            SelfCheck = l => PostStack.SelfCheck(l),
            ContextLost = PostStack.Forget,
            // #389: the TAA history is another area's after a transition; the first frame resolves plain.
            Revoke = TaaStage.Reset,
            Stats = PostStack.Stats,
        }, log);
        // #179: the Alloy world pass. Its dials are the alloy group's (ADR 0025), read straight off the
        // knobs. No Attach/Install slots -- AlloyRenderer IS the service that drives this walk, and
        // it owns those edges directly. ContextLost is in the table anyway (#209): driving the walk is no
        // reason to be absent from it, and being absent is how this pass kept its own GL names through a
        // context loss while every pass it calls dropped theirs. No Revoke -- RevokeAll IS its world edge.
        r.Register(new PassLifecycle
        {
            Name = "draw",
            ContextLost = AlloyRenderer.ContextLost,
            SelfCheck = l => { CullPass.SelfCheck(l); AlloyRenderer.EnrollSelfCheck(l); TargetShape.SelfCheck(l); },
        }, log);
        // CX-54 step 3: not a pass either -- the NWSync store, read by Alloy so a worker can fetch a manifest
        // resource without the main-thread resource manager. It wants the same four slots: a boot gate, the two
        // engine hooks that tell it which manifest is live, a check and a line on the period report.
        r.Register(new PassLifecycle
        {
            Name = "nwsyncread",
            Attach = NwSyncReader.Attach,
            Install = NwSyncReader.Install,
            SelfCheck = l => NwSyncReader.SelfCheck(l),
            // No ContextLost/Revoke: it owns no GL name and captures nothing from the world. Its one lifetime
            // edge is the manifest, and the engine announces that itself.
            Stats = _ => NwSyncReader.Note(),
        }, log);

        // CX-109/CX-110: not a draw pass -- the per-view camera step, the wind clock and the environment-map
        // choice. Registered here for the same reason ownedtex is: what they need IS a pass lifecycle (a boot
        // gate, engine hooks, off-device checks, a world edge and a line on the period report). Their
        // Install runs inside PassRegistry's walk, i.e. only on a client where the Alloy pass can arm.
        r.Register(new PassLifecycle
        {
            Name = "view",
            Attach = l => { AlloyCamera.Attach(l); AlloyWind.Attach(l); AlloyEnvMap.Attach(l); },
            Install = (l, h) => { AlloyCamera.Install(l, h); AlloyWind.Install(l, h); AlloyEnvMap.Install(l, h); },
            SelfCheck = l => { AlloyCamera.SelfCheck(l); AlloyWind.SelfCheck(l); AlloyEnvMap.SelfCheck(l); },
            // The world edge drops the gusts the departed gobs registered, and makes the environment map
            // re-adopt its name (the engine frees AND RELOADS textures across an area transition, so nothing
            // about the previous area's answer may be assumed -- AlloyEnvMap's remarks). No ContextLost:
            // none of the three owns a GL name.
            Revoke = () => { AlloyWind.Revoke(); AlloyEnvMap.Revoke(); },
            Stats = f => Join(AlloyCamera.Stats(f), AlloyWind.Stats(f)),
        }, log);

        // CX-120 seam-scene: not a draw pass -- the CX scene/camera records behind the Aurora seam, the
        // frame's view snapshot and the render thread. Attach resolves the three window reads the slots need
        // (SeamFrame) and the thread is started by the seam's own boot switch, not here: the knob is Boot and
        // the 4.6 context has to exist first.
        r.Register(new PassLifecycle
        {
            Name = "scenedb",
            Attach = l =>
            {
                CX.Alloy.Db.DbScene.Attach(l); CX.Alloy.Db.DbCamera.Attach(l);
                CX.Alloy.Db.SceneShell.Attach(l); CX.Alloy.Db.SceneFeed.Attach(l);
                CX.Alloy.Db.RenderSnapshot.Attach(l);
                CX.Alloy.Seam.SceneSlots.Attach(l); CX.Alloy.Seam.CameraSlots.Attach(l);
                CX.Alloy.Seam.SeamFrame.Attach(l);
            },
            SelfCheck = l => CX.Alloy.Db.DbSceneSelfCheck.Run(l),
            Stats = f => Join(Join(CX.Alloy.Db.DbScene.Stats(f), CX.Alloy.Db.DbCamera.Stats(f)),
                              Join(CX.Alloy.Db.SeamDraw.Stats(f),
                                   CX.Alloy.Db.RenderSnapshot.Stats(f))),
        }, log);

        // CX-114: not a draw pass -- CX's part-tree builder and the ONE model-space vertex arena every part
        // draws from. Its Pump is the frame edge where frees land and both mirrors go up, so it owns a
        // ContextLost (forget the names, keep the mirrors) and no Revoke: every range is keyed on CONTENT, so
        // an area change invalidates nothing in it and the next area re-uses every mesh it shares.
        r.Register(new PassLifecycle
        {
            Name = "mesharena",
            Attach = l => { CX.Alloy.Db.MeshArena.Attach(l); CX.Alloy.Db.PartTree.Attach(l); },
            SelfCheck = l => CX.Alloy.Db.DbSelfCheck.Run(l),
            ContextLost = CX.Alloy.Db.MeshArena.ContextLost,
            // the live comparison is per AREA, so the world edge re-arms it
            Revoke = () => { CX.Alloy.Db.PartTree.AreaEdge(); },
            Stats = f => Join(CX.Alloy.Db.PartTree.Stats(f), CX.Alloy.Db.MeshArena.Stats(f)),
        }, log);

        // CX-116: not a draw pass -- the DB-side tile build. Registered here for the same reason buckets is:
        // what it needs IS a pass lifecycle (a log, off-device checks, the world edge and a line on the
        // period report).
        r.Register(new PassLifecycle
        {
            Name = "dbtiles",
            Attach = CX.Alloy.Db.TileBuild.Attach,
            SelfCheck = l => CX.Alloy.Db.TileBuildCheck.SelfCheck(l),
            Revoke = CX.Alloy.Db.TileBuild.Revoke,
            Stats = _ => CX.Alloy.Db.TileBuild.Stats() + CX.Alloy.Db.DbLightSet.StatsClause(),
        }, log);

        // CX-117: not a draw pass either -- CX's own texture table, the master PLT palette and the
        // per-instance colour rows (ADR 0028). Registered here for ownedtex's reason: a boot gate, a
        // ContextLost, off-device checks and a line on the period report. It owns no engine hook: in Alloy
        // mode the Aurora seam (CX-120) routes the game's texture and palette calls into it.
        r.Register(new PassLifecycle
        {
            Name = "texreg",
            Attach = l => { Db.TexRegistry.Attach(l); Db.PltColors.Attach(l); Db.PltVerify.Attach(l); Db.ContentTable.Attach(l); },
            SelfCheck = l => { Db.TexRegistry.SelfCheck(l); Seam.TexSeam.SelfCheck(l); Db.ContentTable.SelfCheck(l); },
            // No Revoke: every slot is keyed on CONTENT (a resref, a kind, a cell), so a world edge
            // invalidates nothing in it -- which is the whole point of the registry replacing the engine's
            // per-area CAurTexture population.
            ContextLost = () => { Db.TexRegistry.ContextLost(); Db.PltPalettes.ContextLost(); Db.PltColors.ContextLost(); Db.ContentTable.ContextLost(); },
            Stats = _ => Join(Join(Db.TexRegistry.Note() + ", " + Db.PltPalettes.Note() + ", " + Db.PltColors.Note(),
                       Join(Db.PltImage.Note(), Db.ContentTable.Note())),
                       Join(Join(Seam.TexSeam.Note(), Db.PltVerify.Note()),
                       AlloyRenderer.StaleAuditNote())),
        }, log);

        // CX-303: the content stages -- not a pass either, the programs a material's own fragment stage runs in,
        // translated (ContentStage). Registered for a log, the off-device checks, the device loss that takes their
        // pipelines and a line on the period report.
        r.Register(new PassLifecycle
        {
            Name = "content",
            Attach = ContentPrograms.Attach,
            SelfCheck = l => { ContentStage.SelfCheck(l); ContentPrograms.SelfCheck(l); Db.ContentRows.SelfCheck(l); },
            ContextLost = ContentPrograms.ContextLost,
            Stats = _ => ContentPrograms.Note(),
        }, log);

        // CX-113: the model cache -- not a pass, the content-keyed table every CX-owned object takes its model
        // from, plus the engine-side feed that reads the bytes (ModelFeed) and the live comparison against the
        // engine's own animation tables (ModelVerify). Its world edge is the area: the census is reported
        // there and the resident set is trimmed back to its budget. No ContextLost -- it owns no GL name, and
        // the arena ranges are CX-114's, handed back through ModelCache.Evicting.
        r.Register(new PassLifecycle
        {
            Name = "modelcache",
            Attach = l => { ModelFeed.Attach(l); ModelVerify.Attach(l); MtrFeed.Attach(l); },
            SelfCheck = l => CX.Alloy.Mdl.ModelCache.SelfCheck(l),
            Revoke = CX.Alloy.Mdl.ModelCache.AreaEdge,
            Stats = f => Join(Join(CX.Alloy.Mdl.ModelCache.Stats(f), ModelFeed.Note()), MtrFeed.Stats()),
        }, log);

        // CX-120 seam-anim: the animation runtime, the behaviour solve, the dangly and skin poses and the
        // transform step over the scene database. Not a pass either -- it runs on the GAME thread, ahead of
        // everything the renderer does -- but its self-check belongs in this chain for the usual reason: a
        // sampler, an event window or a bone palette that is wrong is a misrender nothing errors on.
        r.Register(new PassLifecycle
        {
            Name = "dbanim",
            Attach = CX.Alloy.Db.DbAnim.Bind,
            SelfCheck = l => CX.Alloy.Db.DbAnim.SelfCheck(l),
            Stats = CX.Alloy.Db.DbAnim.Period,
        }, log);

        // CX-112: the parser's live check -- not a pass either, a diagnostic that compares CX's own parse of
        // every model the engine reads against the engine's tree. Its world edge is the area, where the
        // tally is reported and the next area starts clean.
        r.Register(new PassLifecycle
        {
            Name = "mdlparse",
            Install = MdlParseCheck.Install,
            SelfCheck = l => MdlParseCheckSelfCheck(l),
            Revoke = MdlParseCheck.AreaEdge,
            Stats = MdlParseCheck.Stats,
        }, log);

        // CasterPass is deliberately NOT here yet. It runs in the SHADOW bracket, installs through its own
        // service, and its stand-down is a latched Disarm rather than a context edge -- so it does not fit
        // these slots without changing what they mean. Folding the shadow pass in is #176's work, which is
        // where the folder seam between it and the colour passes goes away.

        r.Seal();
    }

    /// <summary>The MDL parser's off-device suite, adapted to the repo's one assert.</summary>
    private static void MdlParseCheckSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MdlParser.SelfCheck");
        CX.Alloy.Mdl.MdlSelfCheck.Run((ok, why) => check.Expect(ok, why));
        if (check.Ok) log.Info("MdlParser.SelfCheck: passed");
    }

    /// <summary>Two period-line clauses, comma-joined, with an empty one costing nothing.</summary>
    private static string Join(string a, string b)
        => a.Length == 0 ? b : b.Length == 0 ? a : a + ", " + b;
}
