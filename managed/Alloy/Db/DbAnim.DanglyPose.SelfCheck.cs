using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Alloy.Physics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>A dangly part whose animation holds its parent off the rest pose: the fixtures, the drawn-shape
/// reader, and the regression checks for the cases that already held before the posed rule existed. Oracle:
/// the fixture's own rest positions and the stock solve run beside the part from its first frame.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Frames a check runs at 60 Hz: two seconds, long enough for any solve to settle.</summary>
    private const int PoseFrames = 120;
    private const float PoseDt = 1f / 60f;

    /// <summary>The approved examples over this fixture, in a file of their own.</summary>
    static partial void DanglyPoseApprovedExamples(Checker check);

    private static void DanglyPoseChecks(Checker check)
    {
        DanglyPoseApprovedExamples(check);
        PoseHandsBack(check);
        PoseClosedCurtainUnchanged(check);
        PoseStandingFlagUnchanged(check);
        PoseWornUnchanged(check);
    }

    /// <summary>One seated object of the pose fixture: its three parts, the flap's rig and mesh, and the stock
    /// solve that runs beside it as the oracle.</summary>
    private sealed class PoseSubject
    {
        internal DbId Obj;
        internal int Root, Hinge, Flap;
        internal DanglyRig Rig = null!;
        internal MeshDef Mesh = null!;
        internal DanglyDef Def = null!;
        internal VanillaDanglyState Stock = null!;
        internal readonly Vector3[] Drawn = Array.Empty<Vector3>(), Oracle = Array.Empty<Vector3>();
        internal PoseSubject(int verts) { Drawn = new Vector3[verts]; Oracle = new Vector3[verts]; }
    }

    /// <summary>A door model: root dummy, a hinge dummy three units up, and a hanging cloth strip under the
    /// hinge, pinned on its top row with the authored numbers a curtain flap carries (0.11 / 1 / 1, moving
    /// vertices at 131). <paramref name="vegetation"/> stands the strip on its bottom row instead, with a
    /// plant card's numbers (0.3 / 1 / 0.8, moving vertices at 112).</summary>
    private static ModelDef PoseModel(bool vegetation)
    {
        const int cols = 2, rows = 7;
        var pos = new Vector3[cols * rows];
        var cons = new float[cols * rows];
        var nrm = new Vector3[cols * rows];
        var uv = new Vector2[cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int i = r * cols + c;
                pos[i] = new Vector3(-0.15f + 0.3f * c, 0f, vegetation ? 0.25f * r : -0.5f * r);
                cons[i] = r == 0 ? 0f : vegetation ? 112f : 131f;
                nrm[i] = -Vector3.UnitY;
                uv[i] = new Vector2(c, r / (rows - 1f));
            }
        var idx = new uint[(rows - 1) * 6];
        var faces = new FaceDef[(rows - 1) * 2];
        int ti = 0, fi = 0;
        for (int r = 0; r < rows - 1; r++)
        {
            int a = r * cols, b = a + 1, d = a + cols, e = d + 1;
            foreach (int v in stackalloc[] { a, b, d, d, b, e }) idx[ti++] = (uint)v;
            faces[fi++] = new FaceDef { V0 = a, V1 = b, V2 = d };
            faces[fi++] = new FaceDef { V0 = d, V1 = b, V2 = e };
        }
        var mesh = new MeshDef { Positions = pos, Normals = nrm, Indices = idx, Faces = faces };
        mesh.Uv[0] = uv;
        var dangly = vegetation
            ? new DanglyDef { Constraints = cons, Displacement = 0.3f, Tightness = 1f, Period = 0.8f }
            : new DanglyDef { Constraints = cons, Displacement = 0.11f, Tightness = 1f, Period = 1f };
        var m = new ModelDef { Name = "cx_selfcheck_posed", AnimScale = 1f, BMin = new Vector3(-1f, -1f, -1f),
                               BMax = new Vector3(1f, 1f, 4f) };
        m.Nodes = new[]
        {
            new NodeDef { Name = "door", Index = 0, Parent = -1, Children = new[] { 1 }, Kind = NodeKind.Dummy },
            new NodeDef { Name = "hinge", Index = 1, Parent = 0, Children = new[] { 2 }, Kind = NodeKind.Dummy,
                          Position = new Vector3(0f, 0f, vegetation ? 0f : 3f) },
            new NodeDef { Name = "flap", Index = 2, Parent = 1, Children = Array.Empty<int>(),
                          Kind = NodeKind.Dangly, Mesh = mesh, Dangly = dangly },
        };
        for (int k = 0; k < m.Nodes.Length; k++) m.NodeByName[m.Nodes[k].Name] = k;
        m.Nodes[2].Rig = DanglyRig.Build(m, m.Nodes[2]);
        return m;
    }

    /// <summary>Seat the pose fixture as one object of three parts; <paramref name="creature"/> flags the
    /// object as a creature, which is what a worn part rides on.</summary>
    private static PoseSubject? PoseSeat(bool vegetation, bool creature)
    {
        ModelDef m = PoseModel(vegetation);
        NodeDef flap = m.Nodes[2];
        if (flap.Rig == null) return null;
        DbId obj = SceneDb.Objects.Alloc();
        if (!SceneDb.Parts.AllocRange(3, out int first)) { SceneDb.Objects.Free(obj); return null; }
        int o = obj.Index;
        SceneDb.ObjModel[o] = m;
        SceneDb.ObjPos[o] = Vector3.Zero;
        SceneDb.ObjRot[o] = Quaternion.Identity;
        SceneDb.ObjScale[o] = 1f;
        SceneDb.ObjVisible[o] = true;
        SceneDb.ObjFlags[o] = creature ? ObjFlag.Creature : ObjFlag.None;
        SceneDb.ObjPartFirst[o] = first;
        SceneDb.ObjPartCount[o] = 3;
        for (int k = 0; k < 3; k++)
        {
            int p = first + k;
            NodeDef n = m.Nodes[k];
            SceneDb.PartObj[p] = obj;
            SceneDb.PartModel[p] = m;
            SceneDb.PartMeshDef[p] = n.Mesh;
            SceneDb.PartNode[p] = k;
            SceneDb.PartParent[p] = k == 0 ? -1 : p - 1;
            SceneDb.PartPos[p] = n.Position;
            SceneDb.PartRot[p] = n.Orientation;
            SceneDb.PartScale[p] = 1f;
            SceneDb.PartEnabled[p] = true;
            SceneDb.PartMerged[p] = false;
            SceneDb.PartAlpha[p] = 1f;
            SceneDb.PartBoneBase[p] = -1;
            SceneDb.PartKindOf[p] = PartTree.Kind(n.Kind);
            SceneDb.PartMesh[p] = n.Mesh != null ? MeshArena.Acquire(n.Mesh, n.Rig!.Skin) : -1;
        }
        var s = new PoseSubject(flap.Mesh!.Positions.Length)
        {
            Obj = obj, Root = first, Hinge = first + 1, Flap = first + 2, Rig = flap.Rig, Mesh = flap.Mesh,
            Def = flap.Dangly!, Stock = new VanillaDanglyState(flap.Mesh.Positions.Length),
        };
        PoseAt(s, Quaternion.Identity, Quaternion.Identity, Vector3.Zero);
        return s;
    }

    /// <summary>Pose the object: <paramref name="hinge"/> is the animation's turn of the hinge off its rest,
    /// <paramref name="whole"/> and <paramref name="at"/> turn and place the whole object.</summary>
    private static void PoseAt(PoseSubject s, Quaternion hinge, Quaternion whole, Vector3 at)
    {
        NodeDef h = SceneDb.PartModel[s.Hinge]!.Nodes[1];
        SceneDb.PartRot[s.Hinge] = Quaternion.Normalize(h.Orientation * hinge);
        Matrix4x4 root = Matrix4x4.CreateFromQuaternion(whole) * Matrix4x4.CreateTranslation(at);
        Matrix4x4 hw = Matrix4x4.CreateFromQuaternion(SceneDb.PartRot[s.Hinge])
                     * Matrix4x4.CreateTranslation(SceneDb.PartPos[s.Hinge]) * root;
        SceneDb.PartWorld[s.Root] = root;
        SceneDb.PartWorld[s.Hinge] = hw;
        SceneDb.PartWorld[s.Flap] = hw;
    }

    /// <summary>One frame: the dispatch moves the flap, the oracle stock solve moves beside it, and both shapes
    /// are read back in the flap's own frame.</summary>
    private static void PoseFrame(PoseSubject s, bool windy)
    {
        DanglyPart(s.Flap, PoseDt, windy);
        PoseRead(s);
        Matrix4x4 w = SceneDb.PartWorld[s.Flap];
        VanillaDangly.Run(s.Mesh.Positions, s.Def.Constraints, s.Def.Displacement, s.Def.Tightness, s.Def.Period,
                          s.Stock, new Vector3(w.M41, w.M42, w.M43), RotOf(in w), Vector3.Zero, PoseDt,
                          s.Oracle);
    }

    /// <summary>The flap as drawn, in its own frame: its private vertex buffer on the stock path, its bones
    /// applied to the rest mesh on the rigged one, the rest mesh when it holds neither.</summary>
    private static void PoseRead(PoseSubject s)
    {
        Vector3[] rest = s.Mesh.Positions;
        if (DynRange(s.Flap) >= 0) { _dyn[s.Flap].Span[..rest.Length].CopyTo(s.Drawn); return; }
        int at = SceneDb.PartBoneBase[s.Flap];
        SkinDef? skin = s.Rig.Skin;
        if (at < 0 || skin == null) { rest.CopyTo(s.Drawn, 0); return; }
        Span<Matrix4x4> bones = AlloyRenderer.DbBoneCell(at, s.Rig.BoneCount);
        for (int i = 0; i < rest.Length; i++)
        {
            Vector4 wt = skin.Weights[i];
            uint slots = skin.Bones[i];
            Vector3 p = Vector3.Zero;
            for (int k = 0; k < 4; k++)
            {
                float f = k switch { 0 => wt.X, 1 => wt.Y, 2 => wt.Z, _ => wt.W };
                int b = (int)((slots >> (8 * k)) & 0xFF);
                if (f > 0f && b < bones.Length) p += Vector3.Transform(rest[i], bones[b]) * f;
            }
            s.Drawn[i] = p;
        }
    }

    /// <summary>The largest distance between two shapes of one mesh.</summary>
    private static float PoseGap(ReadOnlySpan<Vector3> a, ReadOnlySpan<Vector3> b)
    {
        float m = 0f;
        for (int i = 0; i < a.Length; i++) m = MathF.Max(m, (a[i] - b[i]).Length());
        return m;
    }

    /// <summary>The knobs the pose checks read, held at their declared defaults for the run so the user's own
    /// cx.cfg cannot steer them, then put back.</summary>
    private sealed class PoseKnobs
    {
        private readonly Knob[] _knobs =
        {
            Dangly.Motion, Dangly.Sway, Dangly.Swing, Dangly.Jiggle, Dangly.Collide, Dangly.Tilt, Dangly.Breeze,
            Dangly.WindGain, Dangly.Cage, Dangly.Strand, Dangly.Still,
        };
        private readonly string[] _had;
        private readonly bool _interior = FrameInputs.Interior, _skinning;

        internal PoseKnobs()
        {
            _had = new string[_knobs.Length];
            for (int k = 0; k < _knobs.Length; k++) { _had[k] = _knobs[k].Format(); _knobs[k].Reset(); }
            Dangly.Collide.Set(false);
            FrameInputs.Interior = false;
            _skinning = AlloyRenderer.SelfCheckSkinning(true);
        }

        internal void Restore()
        {
            for (int k = 0; k < _knobs.Length; k++) _knobs[k].Parse(_had[k]);
            FrameInputs.Interior = _interior;
            AlloyRenderer.SelfCheckSkinning(_skinning);
        }
    }

    private static void PoseDrop(PoseSubject s)
    {
        DanglyForget(s.Obj);
        for (int p = s.Root; p <= s.Flap; p++)
        {
            int range = SceneDb.PartMesh[p];
            if (range >= 0) MeshArena.Release(range);
            SceneDb.PartMesh[p] = -1;
            SceneDb.PartModel[p] = null;
            SceneDb.PartMeshDef[p] = null;
            DrawRecipe.Assign(p, -1);
        }
        SceneDb.ObjFlags[s.Obj.Index] = ObjFlag.None;
        SceneDb.Parts.FreeRange(s.Root, 3);
        SceneDb.Objects.Free(s.Obj);
    }

    /// <summary>Run <paramref name="body"/> over a freshly seated fixture with the knobs at their defaults,
    /// and drop both afterwards whatever it does.</summary>
    private static void PoseRun(Checker check, string what, bool vegetation, bool creature,
                                Action<PoseSubject> body)
    {
        PoseSubject? s = PoseSeat(vegetation, creature);
        if (s == null) { check.Expect(false, $"{what}: the pose fixture seats and rigs its flap"); return; }
        var knobs = new PoseKnobs();
        try { body(s); }
        finally { knobs.Restore(); PoseDrop(s); }
    }

    /// <summary>The turn an opening curtain's hinge takes, about the door's width axis.</summary>
    private static Quaternion PoseOpen(float degrees)
        => Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees * MathF.PI / 180f);

    /// <summary>Tested unasked: a curtain that has closed and stopped moving goes back to the CX motion, and a
    /// loop that only crosses the rest pose does not.</summary>
    private static void PoseHandsBack(Checker check)
        => PoseRun(check, "hand-back", false, false, s =>
        {
            for (int f = 0; f < 30; f++) PoseFrame(s, windy: false);
            PoseAt(s, PoseOpen(20f), Quaternion.Identity, Vector3.Zero);
            for (int f = 0; f < PoseFrames; f++) PoseFrame(s, windy: false);
            PoseAt(s, Quaternion.Identity, Quaternion.Identity, Vector3.Zero);
            PoseFrame(s, windy: false);
            check.Expect(DynRange(s.Flap) >= 0, "a curtain that has just closed is still on the stock solve");
            for (int f = 0; f < 5 * PoseFrames; f++) PoseFrame(s, windy: false);
            check.Expect(SceneDb.PartBoneBase[s.Flap] >= 0 && DynRange(s.Flap) < 0,
                         "a closed curtain whose flaps have stopped moves the CX way again");
        });

    // E3, a regression check: a curtain nobody opens never meets the posed rule.
    private static void PoseClosedCurtainUnchanged(Checker check)
        => PoseRun(check, "E3", false, false, s =>
        {
            bool rigged = true;
            for (int f = 0; f < PoseFrames; f++)
            {
                PoseFrame(s, windy: true);
                rigged &= SceneDb.PartBoneBase[s.Flap] >= 0 && DynRange(s.Flap) < 0;
            }
            check.Expect(rigged, "E3 a closed curtain in a breeze moves the CX way every frame");
            check.Expect(PoseGap(s.Drawn, s.Mesh.Positions) > 0f, "E3 ...and the breeze does move it");
        });

    // E4, a regression check: a standing flag whose whole object is turned and placed is not posed.
    private static void PoseStandingFlagUnchanged(Checker check)
        => PoseRun(check, "E4", false, false, s =>
        {
            PoseAt(s, Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f),
                   new Vector3(40f, 12f, 0f));
            bool rigged = true;
            for (int f = 0; f < PoseFrames; f++)
            {
                PoseFrame(s, windy: true);
                rigged &= SceneDb.PartBoneBase[s.Flap] >= 0 && DynRange(s.Flap) < 0;
            }
            check.Expect(rigged, "E4 a flag whose whole object is turned and moved still waves the CX way");
        });

    // E6, a regression check: a part a creature carries keeps the CX way whatever its animation does.
    private static void PoseWornUnchanged(Checker check)
        => PoseRun(check, "E6", false, true, s =>
        {
            for (int f = 0; f < 30; f++) PoseFrame(s, windy: false);
            PoseAt(s, PoseOpen(60f), Quaternion.Identity, Vector3.Zero);
            bool rigged = true;
            for (int f = 0; f < PoseFrames; f++)
            {
                PoseFrame(s, windy: false);
                rigged &= SceneDb.PartBoneBase[s.Flap] >= 0 && DynRange(s.Flap) < 0;
            }
            check.Expect(rigged, "E6 a cloak on a creature that sits or falls moves the CX way every frame");
        });
}
