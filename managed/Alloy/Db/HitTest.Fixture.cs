using System;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

public static unsafe partial class HitTestCheck
{
    /// <summary>Synthetic scenes built straight into the real tables, so the hit test is checkable without
    /// the part-tree builder or the parser. Every record is freed again by <see cref="Tear"/>, so one run
    /// leaves the database as it found it.</summary>
    internal static class Fixture
    {
        /// <summary>The value a trigger part reports at <c>CHitInfo+0x70</c> in the fixture.</summary>
        internal const long TriggerPayload = 0x5157A11C;   // not-an-offset: a fixture sentinel, any distinctive value

        internal struct World
        {
            internal DbId Scene, Near, Mid, Far, Door, Effect, Creature, StaticProp, Trigger, Spun, Floor, BigTrigger;
        }

        private static DbId[] _objs = Array.Empty<DbId>();
        private static DbId[] _scenes = Array.Empty<DbId>();

        /// <summary>A unit cube centred on the origin: 8 vertices, 12 faces, surface id 0.</summary>
        private static MeshDef Cube(float half, int surface)
        {
            var m = new MeshDef();
            var p = new Vector3[8];
            for (int i = 0; i < 8; i++)
                p[i] = new Vector3((i & 1) == 0 ? -half : half, (i & 2) == 0 ? -half : half, (i & 4) == 0 ? -half : half);
            m.Positions = p;
            m.BMin = new Vector3(-half); m.BMax = new Vector3(half);
            // Six quads, wound so every face's plane normal points OUT -- which polyhit's one-sidedness needs.
            int[,] quads =
            {
                { 0, 2, 3, 1 }, { 4, 5, 7, 6 },   // -z, +z
                { 0, 1, 5, 4 }, { 2, 6, 7, 3 },   // -y, +y
                { 0, 4, 6, 2 }, { 1, 3, 7, 5 },   // -x, +x
            };
            var faces = new FaceDef[12];
            for (int qi = 0; qi < 6; qi++)
                for (int t = 0; t < 2; t++)
                {
                    int i0 = quads[qi, 0], i1 = quads[qi, t == 0 ? 1 : 2], i2 = quads[qi, t == 0 ? 2 : 3];
                    Vector3 n = Vector3.Normalize(Vector3.Cross(p[i1] - p[i0], p[i2] - p[i0]));
                    faces[qi * 2 + t] = new FaceDef
                    {
                        V0 = i0, V1 = i1, V2 = i2, Surface = surface,
                        Adj0 = -1, Adj1 = -1, Adj2 = -1,
                        PlaneNormal = n, PlaneDistance = -Vector3.Dot(n, p[i0]),
                    };
                }
            m.Faces = faces;
            return m;
        }

        private static ModelDef Model(string name, byte cls, MeshDef mesh, NodeKind kind)
        {
            var nd = new NodeDef { Name = name + "a", Index = 0, Kind = kind, Mesh = mesh };
            // An Aabb node carries the file's own tree, which is what the engine's PartAABB walks instead of
            // deriving one: two leaves over the quad's two faces is the smallest honest version of it.
            if (kind == NodeKind.Aabb && mesh.Faces.Length == 2)
                nd.Aabb = new AabbDef
                {
                    Tree = new[]
                    {
                        new AabbNode { BMin = mesh.BMin, BMax = mesh.BMax, Left = 1, Right = 2, Face = -1 },
                        Leaf(mesh, 0), Leaf(mesh, 1),
                    },
                };
            return new ModelDef { Name = name, Classification = cls, Nodes = new[] { nd }, BMin = mesh.BMin, BMax = mesh.BMax };
        }

        private static AabbNode Leaf(MeshDef m, int f)
        {
            ref FaceDef d = ref m.Faces[f];
            Vector3 lo = Vector3.Min(m.Positions[d.V0], Vector3.Min(m.Positions[d.V1], m.Positions[d.V2]));
            Vector3 hi = Vector3.Max(m.Positions[d.V0], Vector3.Max(m.Positions[d.V1], m.Positions[d.V2]));
            return new AabbNode { BMin = lo, BMax = hi, Left = -1, Right = -1, Face = f };
        }

        /// <summary>A two-triangle quad in the z = <paramref name="z"/> plane, normal +z, centred on
        /// (<paramref name="cx"/>, 0): the shape a tile walkmesh part has.</summary>
        private static MeshDef Quad(float cx, float z, float half, int surface)
        {
            var m = new MeshDef
            {
                Positions = new[]
                {
                    new Vector3(cx - half, -half, z), new Vector3(cx + half, -half, z),
                    new Vector3(cx + half, half, z), new Vector3(cx - half, half, z),
                },
            };
            m.BMin = new Vector3(cx - half, -half, z - 0.01f);
            m.BMax = new Vector3(cx + half, half, z + 0.01f);
            Vector3 n = Vector3.UnitZ;
            m.Faces = new[]
            {
                new FaceDef { V0 = 0, V1 = 1, V2 = 2, Surface = surface, Adj0 = -1, Adj1 = -1, Adj2 = -1,
                              PlaneNormal = n, PlaneDistance = -z },
                new FaceDef { V0 = 0, V1 = 2, V2 = 3, Surface = surface, Adj0 = -1, Adj1 = -1, Adj2 = -1,
                              PlaneNormal = n, PlaneDistance = -z },
            };
            return m;
        }

        /// <summary>One object with one mesh part at <paramref name="pos"/>, oriented and scaled.</summary>
        private static DbId Obj(DbId scene, ModelDef model, Vector3 pos, Quaternion rot, float scale,
                               byte cls, bool isStatic, long trigger = 0)
        {
            DbId o = SceneDb.Objects.Alloc();
            int i = o.Index;
            DbFeed.InitObject(i);   // the seam-feed contract: a record's feed columns are seeded at allocation
            SceneDb.ObjKindOf[i] = ObjKind.Model;
            SceneDb.ObjModel[i] = model;
            SceneDb.ObjScene[i] = scene;
            DbScene.Members(scene).Add(o);
            SceneDb.ObjPos[i] = pos; SceneDb.ObjRot[i] = rot; SceneDb.ObjScale[i] = scale;
            SceneDb.ObjWorld[i] = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rot)
                                  * Matrix4x4.CreateTranslation(pos);
            SceneDb.ObjVisible[i] = true;
            SceneDb.ObjTint[i] = Vector4.One;
            SceneDb.HitObjAlpha[i] = 1f;
            SceneDb.HitObjFadeAlpha[i] = 1f;
            SceneDb.HitObjClass[i] = cls;
            SceneDb.HitObjStatic[i] = isStatic;

            DbId p = SceneDb.Parts.Alloc();
            int pi = p.Index;
            DbFeed.InitPart(pi);
            SceneDb.ObjPartFirst[i] = pi; SceneDb.ObjPartCount[i] = 1;
            SceneDb.PartObj[pi] = o;
            SceneDb.PartModel[pi] = model;
            SceneDb.PartNode[pi] = 0;
            SceneDb.PartParent[pi] = -1;
            SceneDb.PartPos[pi] = Vector3.Zero; SceneDb.PartRot[pi] = Quaternion.Identity; SceneDb.PartScale[pi] = 1f;
            SceneDb.PartWorld[pi] = SceneDb.ObjWorld[i];
            SceneDb.PartEnabled[pi] = true; SceneDb.PartMerged[pi] = false;
            SceneDb.PartAlpha[pi] = 1f;
            SceneDb.PartMesh[pi] = -1;
            // The PRODUCER path, exercised rather than bypassed: NoteObject is exactly what the seam must
            // call, and nothing else seeds the classification, the alphas or the part boxes.
            HitFeed.NoteObject(o, isStatic);
            HitFeed.NoteTrigger(pi, trigger);
            HitBounds.Refresh(i);
            return o;
        }

        private static DbId NewScene()
        {
            DbId s = SceneDb.Scenes.Alloc();
            SceneDb.SceneIsWorld[s.Index] = true;
            SceneDb.SceneObjects[s.Index]?.Clear();
            Array.Resize(ref _scenes, _scenes.Length + 1);
            _scenes[^1] = s;
            return s;
        }

        /// <summary>The semantics fixture: one record per gate the cast has.</summary>
        internal static World Build()
        {
            var w = new World();
            w.Scene = NewScene();
            MeshDef cube = Cube(0.5f, 0);
            ModelDef plain = Model("box", 0, cube, NodeKind.TriMesh);
            ModelDef door = Model("door", HitTest.ClassDoor, cube, NodeKind.TriMesh);
            ModelDef fx = Model("fx", HitTest.ClassEffect, cube, NodeKind.TriMesh);
            ModelDef chr = Model("chr", HitTest.ClassCharacter, cube, NodeKind.TriMesh);
            ModelDef trig = Model("trig", 0, Cube(1f, HitQuery.TriggerSurface), NodeKind.Trigger);

            // One LANE per gate, so no cast can be answered by a record it was not aiming at: the ordering
            // trio sits on y=0 and every other record owns its own x column, cast along y.
            w.Near = Obj(w.Scene, plain, new Vector3(-10, 0, 0.5f), Quaternion.Identity, 1f, 0, false);
            w.Mid = Obj(w.Scene, plain, new Vector3(0, 0, 0.5f), Quaternion.Identity, 1f, 0, false);
            w.Far = Obj(w.Scene, plain, new Vector3(10, 0, 0.5f), Quaternion.Identity, 1f, 0, false);
            w.Door = Obj(w.Scene, door, new Vector3(30, 0, 0.5f), Quaternion.Identity, 1f, HitTest.ClassDoor, false);
            w.Creature = Obj(w.Scene, chr, new Vector3(50, 0, 0.5f), Quaternion.Identity, 1f, HitTest.ClassCharacter, false);
            w.StaticProp = Obj(w.Scene, plain, new Vector3(70, 0, 0.5f), Quaternion.Identity, 1f, 0, true);
            w.Trigger = Obj(w.Scene, trig, new Vector3(90, 0, 0.5f), Quaternion.Identity, 1f, 0, false, TriggerPayload);
            w.Effect = Obj(w.Scene, fx, new Vector3(110, 0, 0.5f), Quaternion.Identity, 1f, HitTest.ClassEffect, false);
            w.Spun = Obj(w.Scene, plain, new Vector3(130, 0, 0.5f),
                         Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6f), 3f, 0, false);
            // The tile walkmesh: a PartAABB-shaped part (a mesh WITH an AabbDef), in the static set, lying in
            // the z=0 plane -- the surface a click-to-move pick lands on.
            w.Floor = Obj(w.Scene, Model("floor", HitTest.ClassTile, Quad(150f, 0f, 6f, 2), NodeKind.Aabb),
                          new Vector3(0, 0, 0), Quaternion.Identity, 1f, HitTest.ClassTile, true);
            // The same 2m trigger prism at scale 4: PartTrigger::HitCheckGeom ignores the accumulated scale
            // entirely, so this must still only cover its UNSCALED extent.
            w.BigTrigger = Obj(w.Scene, trig, new Vector3(200, 0, 0.5f), Quaternion.Identity, 4f, 0, false,
                               TriggerPayload);
            _objs = new[] { w.Near, w.Mid, w.Far, w.Door, w.Effect, w.Creature, w.StaticProp, w.Trigger,
                            w.Spun, w.Floor, w.BigTrigger };
            return w;
        }

        /// <summary>One walkmesh FLOOR record on its own, added to the world the fixture already built: the
        /// shape <c>CNWCCreature::ComputeZValue</c>'s probe has to find, for the recycled-slot case.</summary>
        internal static DbId Floor(DbId scene, Vector3 pos)
        {
            DbId o = Obj(scene, Model("recyclefloor", HitTest.ClassTile, Quad(0f, 0f, 6f, 2), NodeKind.Aabb),
                         pos, Quaternion.Identity, 1f, HitTest.ClassTile, true);
            Array.Resize(ref _objs, _objs.Length + 1);
            _objs[^1] = o;
            return o;
        }

        /// <summary>Free one record the way <c>TileBuild.FreeObject</c> does: its parts, then its object, both
        /// left marked <see cref="Dirty.Destroyed"/> and NOT unfiled by hand -- the hit stream's fold owns
        /// that edge, and a case that unfiled here would be testing itself instead of the fold.</summary>
        internal static void Kill(DbId o)
        {
            if (!SceneDb.Objects.Valid(o)) return;
            int first = SceneDb.ObjPartFirst[o.Index];
            for (int p = first; p < first + SceneDb.ObjPartCount[o.Index]; p++)
            {
                SceneDb.HitPartBMin[p] = new Vector3(1f); SceneDb.HitPartBMax[p] = new Vector3(-1f);
                SceneDb.HitPartTrigger[p] = 0;
                SceneDb.Parts.Free(SceneDb.Parts.IdAt(p));
            }
            SceneDb.Objects.Free(o);
        }

        /// <summary>Re-place an object and refresh its bounds: the change-driven path, exercised by the
        /// fixture itself rather than by a special case in the test.</summary>
        internal static void Move(DbId o, Vector3 pos)
        {
            int i = o.Index;
            SceneDb.ObjPos[i] = pos;
            SceneDb.ObjWorld[i] = Matrix4x4.CreateScale(SceneDb.ObjScale[i])
                                  * Matrix4x4.CreateFromQuaternion(SceneDb.ObjRot[i])
                                  * Matrix4x4.CreateTranslation(pos);
            int first = SceneDb.ObjPartFirst[i];
            for (int p = first; p < first + SceneDb.ObjPartCount[i]; p++) SceneDb.PartWorld[p] = SceneDb.ObjWorld[i];
            HitBounds.Refresh(i);
        }

        internal struct GuiWorld { internal DbId Scene, Cam, Play, Options, Exit; }

        /// <summary>The pixels the GUI fixture's controls sit at, and which control each belongs to.</summary>
        internal static (int, int, DbId, string)[] GuiTargets = Array.Empty<(int, int, DbId, string)>();

        private static DbId[] _cams = Array.Empty<DbId>();

        /// <summary>A GUI panel as DB mode builds one: a scene with no tile grid (so it is not a world
        /// scene), an ORTHO camera with its own rect, and three control quads at known pixels. The world
        /// placement is the inverse of the engine's ortho pick mapping, one hundredth of a pixel per unit,
        /// so a pick at a control's pixel landing on that control proves the round trip.</summary>
        internal static GuiWorld BuildGui(float vw, float vh)
        {
            var w = new GuiWorld { Scene = NewScene() };
            SceneDb.SceneIsWorld[w.Scene.Index] = false;
            w.Cam = NewCamera(w.Scene, vw, vh);
            // Controls are 80x24 px buttons stacked down the middle, in the panel's own pixel grid.
            var targets = new (int, int, DbId, string)[3];
            string[] names = { "Play", "Options", "Exit" };
            var ids = new DbId[3];
            for (int k = 0; k < 3; k++)
            {
                int px = 400, py = 200 + k * 120;
                ids[k] = GuiControl(w.Scene, names[k], px, py, 80, 24);
                targets[k] = (px, py, ids[k], names[k]);
            }
            w.Play = ids[0]; w.Options = ids[1]; w.Exit = ids[2];
            GuiTargets = targets;
            _objs = ids;
            return w;
        }

        /// <summary>One more control on the GUI fixture's panel, joined after everything already on it.</summary>
        internal static DbId GuiExtra(DbId scene, string name, int px, int py, int pw, int ph)
        {
            DbId o = GuiControl(scene, name, px, py, pw, ph);
            Array.Resize(ref _objs, _objs.Length + 1);
            _objs[^1] = o;
            return o;
        }

        /// <summary>One control: a quad in the z=0 plane covering the given pixel rect, mapped through the
        /// engine's ortho scale. Its own classification is 0 -- a GUI model is not an effect, and the hit
        /// test's effect gate would drop it if it were.</summary>
        private static DbId GuiControl(DbId scene, string name, int px, int py, int pw, int ph)
        {
            float k = 0.01f;
            float cx = (px + 0.5f) * k, cy = (py + 0.5f) * k;
            MeshDef quad = Quad(0f, 0f, 1f, 0);
            // Place and size the quad so it covers [px-pw/2, px+pw/2] x [py-ph/2, py+ph/2] in pixels.
            quad.Positions = new[]
            {
                new Vector3(cx - pw * k * 0.5f, cy - ph * k * 0.5f, 0f),
                new Vector3(cx + pw * k * 0.5f, cy - ph * k * 0.5f, 0f),
                new Vector3(cx + pw * k * 0.5f, cy + ph * k * 0.5f, 0f),
                new Vector3(cx - pw * k * 0.5f, cy + ph * k * 0.5f, 0f),
            };
            quad.BMin = new Vector3(cx - pw * k * 0.5f, cy - ph * k * 0.5f, -0.001f);
            quad.BMax = new Vector3(cx + pw * k * 0.5f, cy + ph * k * 0.5f, 0.001f);
            return Obj(scene, Model(name, 0, quad, NodeKind.TriMesh), Vector3.Zero, Quaternion.Identity, 1f,
                       0, false);
        }

        /// <summary>An ortho camera with its own viewport rect, as <c>CGuiPanel::CreateSceneView</c> makes
        /// one: view angle 0 (the ortho branch) and a Z above the controls looking down world -Z.</summary>
        private static DbId NewCamera(DbId scene, float vw, float vh)
        {
            DbId c = SceneDb.Cameras.Alloc();
            int i = c.Index;
            SceneDb.CamScene[i] = scene;
            // Above the controls, looking down world -Z: the ortho segment runs pos.z-near down to
            // pos.z-near-far, so a camera at z=0 would start BEHIND a control in the z=0 plane.
            SceneDb.CamPos[i] = new Vector3(0f, 0f, 10f);
            SceneDb.CamRot[i] = Quaternion.Identity;
            SceneDb.CamViewX[i] = 0f; SceneDb.CamViewY[i] = 0f;
            SceneDb.CamViewW[i] = vw; SceneDb.CamViewH[i] = vh;
            SceneDb.CamViewAngle[i] = 0f;
            SceneDb.CamNear[i] = 0.1f; SceneDb.CamFar[i] = 100f;
            SceneDb.CamObj[i] = default;
            SceneDb.CamFacade[i] = 0;
            Array.Resize(ref _cams, _cams.Length + 1);
            _cams[^1] = c;
            return c;
        }

        /// <summary>The benchmark fixture: <paramref name="n"/> unit boxes on a lattice over a 500m square,
        /// which is the footprint of a large persistent-world exterior.</summary>
        internal static World BuildGrid(int n)
        {
            var w = new World { Scene = NewScene() };
            MeshDef cube = Cube(0.5f, 0);
            ModelDef plain = Model("benchbox", 0, cube, NodeKind.TriMesh);
            int side = (int)MathF.Ceiling(MathF.Sqrt(n));
            var ids = new DbId[n];
            for (int i = 0; i < n; i++)
            {
                float x = i % side * (500f / side) + 2f, y = i / side * (500f / side) + 2f;
                ids[i] = Obj(w.Scene, plain, new Vector3(x, y, 0.5f), Quaternion.Identity, 1f, 0, false);
            }
            _objs = ids;
            HitTest.Casts = 0; HitTest.CellVisits = 0; HitTest.ObjTests = 0;
            HitTest.PartTests = 0;
            return w;
        }

        /// <summary>Free every record the fixture made, so a later self-check and the live session start from
        /// an empty database.</summary>
        internal static void Tear()
        {
            foreach (DbId o in _objs)
            {
                if (!SceneDb.Objects.Valid(o)) continue;
                HitGrid.Remove(o.Index);
                int first = SceneDb.ObjPartFirst[o.Index];
                for (int p = first; p < first + SceneDb.ObjPartCount[o.Index]; p++)
                {
                    // A recycled part slot must not inherit this fixture's geometry: the box is the one hit
                    // column a later tenant reads before anything re-derives it.
                    SceneDb.HitPartBMin[p] = new Vector3(1f); SceneDb.HitPartBMax[p] = new Vector3(-1f);
                    SceneDb.HitPartTrigger[p] = 0;
                    SceneDb.Parts.Free(SceneDb.Parts.IdAt(p));
                }
                SceneDb.Objects.Free(o);
            }
            foreach (DbId c in _cams) if (SceneDb.Cameras.Valid(c)) SceneDb.Cameras.Free(c);
            foreach (DbId sc in _scenes)
                if (SceneDb.Scenes.Valid(sc)) { SceneDb.SceneObjects[sc.Index]?.Clear(); SceneDb.Scenes.Free(sc); }
            _cams = Array.Empty<DbId>();
            GuiTargets = Array.Empty<(int, int, DbId, string)>();
            _objs = Array.Empty<DbId>();
            _scenes = Array.Empty<DbId>();
            SceneDb.Objects.TakeDirty();
            SceneDb.Parts.TakeDirty();
            SceneDb.Scenes.TakeDirty();
        }
    }
}
