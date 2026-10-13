using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The non-mesh consumers' inputs, asserted over a synthetic database. Each property here fails
/// SILENTLY in a live session if it is wrong -- a light filed under the wrong kind is an unlit area, a
/// lightmap key that moves with a rebuilt matrix is a bake that never hits.</summary>
internal static partial class DbInputs
{
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbInputs.SelfCheck");
        Reset();
        try
        {
            KindsAndLifetime(check);
            LightmapKeys(check);
            SkyOrder(check);
            FlickerChecks(check);
        }
        finally { Reset(); }
        log.Info(check.Ok ? "DbInputs.SelfCheck: passed"
                          : "DbInputs.SelfCheck: FAILED -- a light, emitter, sky or highlight input would be "
                          + "wrong, which is a dark area or a glow around nothing rather than an error");
        return check.Ok;
    }

    /// <summary>A light part files under lights, an emitter under emitters, a mesh under neither -- and a
    /// destroy takes the entry with it, which is the whole lifetime contract in one assertion.</summary>
    private static void KindsAndLifetime(Checker check)
    {
        ModelDef m = KindModel();
        DbId obj = SceneDb.Objects.Alloc();
        DbId hl = SceneDb.Parts.Alloc(), he = SceneDb.Parts.Alloc(), hm = SceneDb.Parts.Alloc();
        int pl = hl.Index, pe = he.Index, pm = hm.Index;
        int[] parts = { pl, pe, pm };
        int[] nodes = { 1, 2, 3 };
        for (int k = 0; k < 3; k++)
        {
            DbFeed.InitPart(parts[k]);
            SceneDb.PartObj[parts[k]] = obj;
            SceneDb.PartModel[parts[k]] = m;
            SceneDb.PartNode[parts[k]] = nodes[k];
            SceneDb.PartEnabled[parts[k]] = true;
            SceneDb.PartWorld[parts[k]] = Matrix4x4.CreateTranslation(2f, 3f, 4f);
        }
        DbFeed.InitObject(obj.Index);
        SceneDb.ObjVisible[obj.Index] = true;

        PartCreated(pl); PartCreated(pe); PartCreated(pm);
        check.Expect(LightCount == 1 && EmitterCount == 1,
                     $"a light files as a light and an emitter as an emitter (lights {LightCount}, emitters {EmitterCount})");
        check.Expect(Light(0, out DbLight l) && l.Part == pl, "the light names its own part");
        check.Expect(l.Position == new Vector3(2f, 3f, 4f),
                     "a light's position is its part's world translation, not a node read");
        check.Expect(l.Colour == new Vector3(0.25f, 0.5f, 0.75f) && l.Radius == 7f && l.Multiplier == 2f,
                     "colour, radius and multiplier come off the node's own controllers");
        check.Expect(l.Enabled, "a light on an enabled part of a visible object is on");
        SceneDb.ObjVisible[obj.Index] = false;
        check.Expect(Light(0, out l) && !l.Enabled, "...and off when its object is invisible");
        SceneDb.ObjVisible[obj.Index] = true;
        check.Expect(Emitter(0, out DbEmitter e) && e.Part == pe && e.Def != null,
                     "the emitter carries its model-declared parameters");

        check.Expect(!IsBeam(pm), "a plain mesh is not a beam");

        PartDestroyed(pl);
        check.Expect(LightCount == 0 && EmitterCount == 1,
                     "a destroyed light leaves the table and takes nothing else with it");
        PartDestroyed(pe);
        check.Expect(EmitterCount == 0, "...and so does a destroyed emitter");
        // A chunk emitter with no spawner registered refuses LOUDLY rather than spawning nothing quietly.
        int before = ChunkSpawnsRefused;
        SpawnChunk(default, "cx_chunk", Matrix4x4.Identity);
        check.Expect(ChunkSpawnsRefused == before + 1, "a chunk spawn with no spawner is a counted refusal");

        SceneDb.Parts.Free(hl); SceneDb.Parts.Free(he); SceneDb.Parts.Free(hm);
        SceneDb.Objects.Free(obj);
        SceneDb.Parts.TakeDirty(); SceneDb.Objects.TakeDirty();
    }

    /// <summary>The bake key is CONTENT plus PLACEMENT: the same mesh at the same place keys the same across
    /// instances and across areas (which an address pair never did), a different place keys differently, and a
    /// matrix that moved less than the record's own step does NOT -- or every quaternion rebuild would miss a
    /// bake that is still valid.</summary>
    private static void LightmapKeys(Checker check)
    {
        ModelDef m = KindModel();
        DbId obj = SceneDb.Objects.Alloc();
        DbId h1 = SceneDb.Parts.Alloc(), h2 = SceneDb.Parts.Alloc();
        int a = h1.Index, b = h2.Index;
        foreach (int p in new[] { a, b })
        {
            DbFeed.InitPart(p);
            SceneDb.PartObj[p] = obj;
            SceneDb.PartModel[p] = m;
            SceneDb.PartNode[p] = 3;
            DrawRecipe.Assign(p, DrawRecipe.Intern(m, 3));
            SceneDb.PartWorld[p] = Matrix4x4.CreateTranslation(10f, 20f, 30f);
        }
        ulong ka = LightmapKey(a), kb = LightmapKey(b);
        check.Expect(ka != 0 && ka == kb,
                     "two instances of one mesh at one placement share a bake key");
        SceneDb.PartWorld[b] = Matrix4x4.CreateTranslation(10f, 20f, 31f);
        check.Expect(LightmapKey(b) != ka, "a different placement is a different key");
        // R1-31: a perturbation 100x UNDER the step proves nothing. These two straddle it -- 0.4 of a step
        // must not move the key and 1.6 of one must, which is what "quantised to the record's own step"
        // actually claims.
        SceneDb.PartWorld[b] = Matrix4x4.CreateTranslation(10f, 20f, 30f + 0.4e-4f);
        check.Expect(LightmapKey(b) == ka,
                     "a placement that moved less than the record's own step does not move the key");
        SceneDb.PartWorld[b] = Matrix4x4.CreateTranslation(10f, 20f, 30f + 1.6e-4f);
        check.Expect(LightmapKey(b) != ka,
                     "...and one that moved more than the step does");
        SceneDb.PartNode[b] = 1;
        DrawRecipe.Assign(b, SceneDb.PartRecipe[a]);
        check.Expect(LightmapKey(b) != ka, "a different node of one model is a different key");

        DrawRecipe.Assign(a, -1); DrawRecipe.Assign(b, -1);
        SceneDb.Parts.Free(h1); SceneDb.Parts.Free(h2);
        SceneDb.Objects.Free(obj);
        SceneDb.Parts.TakeDirty(); SceneDb.Objects.TakeDirty();
    }

    /// <summary>The scene's three sky slots keep their ORDER (the engine draws current, fading-in, cross-fade,
    /// and a swap paints the wrong sky over the right one). Oracle: the handles written in.</summary>
    private static void SkyOrder(Checker check)
    {
        DbId scene = SceneDb.Scenes.Alloc();
        int s = scene.Index;
        var a = DbId.Make(101, 1);
        var b = DbId.Make(102, 1);
        var c = DbId.Make(103, 1);
        // Written through the slot switch and read back off the columns the passes read: that switch is where
        // a draw order can actually be transposed, and either half alone would only be an array round trip.
        SceneDb.SetSceneSky(scene, 0, a); SceneDb.SetSceneSky(scene, 1, b); SceneDb.SetSceneSky(scene, 2, c);
        check.Expect(SceneDb.SceneSky0[s].Value == a.Value && SceneDb.SceneSky1[s].Value == b.Value
                     && SceneDb.SceneSky2[s].Value == c.Value && SceneDb.SceneSky3[s].IsNull,
                     "each sky slot is filed under its own column, so the three keep their draw order");
        check.Expect(SceneDb.SceneSky(scene, 2).Value == c.Value && SceneDb.SceneSky(scene, 4).IsNull,
                     "...and the reader answers that same column, with no slot past the fourth");

        SceneDb.Scenes.Free(scene);
        SceneDb.Scenes.TakeDirty();
    }

    /// <summary>A model with one node of each kind the inputs sort on: a light (with its three controllers), an
    /// emitter, and a plain mesh.</summary>
    private static ModelDef KindModel()
    {
        var m = new ModelDef { Name = "cx_kinds", ContentHash = 0xBEEF1234UL };
        var root = new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy };
        var light = new NodeDef
        {
            Name = "lamp", Index = 1, Parent = 0, Kind = NodeKind.Light,
            Light = new LightDef { Priority = 1, Dynamic = true },
            Controllers = new[]
            {
                new ControllerTrack { Type = CtlColor, Columns = 3, Times = new[] { 0f }, Values = new[] { 0.25f, 0.5f, 0.75f } },
                new ControllerTrack { Type = CtlRadius, Columns = 1, Times = new[] { 0f }, Values = new[] { 7f } },
                new ControllerTrack { Type = CtlMultiplier, Columns = 1, Times = new[] { 0f }, Values = new[] { 2f } },
            },
        };
        var emit = new NodeDef
        {
            Name = "puff", Index = 2, Parent = 0, Kind = NodeKind.Emitter,
            Emitter = new EmitterDef { Update = "fountain", Render = "normal", Texture = "cx_spark" },
        };
        var mesh = new NodeDef
        {
            Name = "body", Index = 3, Parent = 0, Kind = NodeKind.TriMesh,
            Mesh = new MeshDef
            {
                Positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
                Indices = new uint[] { 0, 1, 2 },
                BMin = new Vector3(-1f, -1f, -1f), BMax = Vector3.One,
                Textures = new[] { "cx_white", "", "", "" },
            },
        };
        m.Nodes = new[] { root, light, emit, mesh };
        m.NodeByName["root"] = 0; m.NodeByName["lamp"] = 1; m.NodeByName["puff"] = 2; m.NodeByName["body"] = 3;
        return m;
    }
}
