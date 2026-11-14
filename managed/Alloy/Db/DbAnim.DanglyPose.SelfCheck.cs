using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Alloy.Physics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>A dangly part whose animation holds its parent off the rest pose: the door fixture, the drawn-shape
/// reader, and the checks that a closed curtain, a placed flag and a worn cloak keep the CX motion.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Frames a check runs at 60 Hz: two seconds, long enough for any solve to settle.</summary>
    private const int PoseFrames = 120;
    /// <summary>One 60 Hz frame, seconds.</summary>
    private const float PoseDt = 1f / 60f;

    /// <summary>The approved examples over this fixture, in a file of their own.</summary>
    static partial void DanglyPoseApprovedExamples(Checker check);

    /// <summary>The pose suite: the approved examples, then the checks nobody had to approve.</summary>
    private static void DanglyPoseChecks(Checker check)
    {
        PoseSkinLiteral(check);
        DanglyPoseApprovedExamples(check);
        PoseHandsBack(check);
        PoseDropsStraight(check);
        PoseRecycleForgets(check);
        PoseClosedCurtainUnchanged(check);
        PoseStandingFlagUnchanged(check);
        PoseWornUnchanged(check);
    }

    /// <summary>One seated door: its object and first part (the root; the hinge and the flap follow), the
    /// flap's rig and mesh, and the stock solve that runs beside it as the oracle.</summary>
    private sealed class PoseSubject
    {
        internal DbId Obj;
        internal int Root;
        internal int Hinge => Root + 1;
        internal int Flap => Root + 2;
        internal DanglyRig Rig = null!;
        internal MeshDef Mesh = null!;
        internal DanglyDef Def = null!;
        internal VanillaDanglyState Stock = null!;
        internal readonly Vector3[] Drawn, Oracle;
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
    /// are read back in the flap's own frame. Returns how far the drawn shape strays from the oracle's.</summary>
    private static float PoseFrame(PoseSubject s, bool windy)
    {
        DanglyPart(s.Flap, PoseDt, windy);
        PoseRead(s);
        Matrix4x4 w = SceneDb.PartWorld[s.Flap];
        VanillaDangly.Run(s.Mesh.Positions, s.Def.Constraints, s.Def.Displacement, s.Def.Tightness, s.Def.Period,
                          s.Stock, new Vector3(w.M41, w.M42, w.M43), RotOf(in w), Vector3.Zero, PoseDt,
                          s.Oracle);
        return PoseGap(s.Drawn, s.Oracle);
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
        for (int i = 0; i < rest.Length; i++) s.Drawn[i] = skin.Skin(i, rest[i], bones);
    }

    /// <summary>The largest distance between two shapes of one mesh.</summary>
    private static float PoseGap(ReadOnlySpan<Vector3> a, ReadOnlySpan<Vector3> b)
    {
        float m = 0f;
        for (int i = 0; i < a.Length; i++) m = MathF.Max(m, (a[i] - b[i]).Length());
        return m;
    }

    /// <summary>The flap is on the CX motion this frame: a bone range and no private vertex range.</summary>
    private static bool PoseRigged(PoseSubject s) => SceneDb.PartBoneBase[s.Flap] >= 0 && DynRange(s.Flap) < 0;

    /// <summary>Two seconds of frames; true when the flap was on the CX motion in every one.</summary>
    private static bool PoseRiggedFor(PoseSubject s, bool windy)
    {
        bool rigged = true;
        for (int f = 0; f < PoseFrames; f++)
        {
            PoseFrame(s, windy);
            rigged &= PoseRigged(s);
        }
        return rigged;
    }

    /// <summary>Run <paramref name="body"/> over a freshly seated door, with the dangly knobs at their declared
    /// defaults so the user's own cx.cfg cannot steer it, then put the knobs back and drop the door.</summary>
    private static void PoseRun(Checker check, string what, bool vegetation, bool creature,
                                Action<PoseSubject> body)
    {
        ModelDef m = PoseModel(vegetation);
        NodeDef flap = m.Nodes[2];
        int first = -1;
        DbId obj = default;
        if (flap.Rig != null) obj = SeatDangly(m, creature ? ObjFlag.Creature : ObjFlag.None, out first);
        if (obj.IsNull) { check.Expect(false, $"{what}: the pose fixture seats and rigs its flap"); return; }
        var s = new PoseSubject(flap.Mesh!.Positions.Length)
        {
            Obj = obj, Root = first, Rig = flap.Rig!, Mesh = flap.Mesh, Def = flap.Dangly!,
            Stock = new VanillaDanglyState(flap.Mesh.Positions.Length),
        };
        Knob[] knobs =
        {
            Dangly.Motion, Dangly.Sway, Dangly.Swing, Dangly.Jiggle, Dangly.Collide, Dangly.Tilt, Dangly.Breeze,
            Dangly.WindGain, Dangly.Cage, Dangly.Strand, Dangly.Still,
        };
        var had = new string[knobs.Length];
        for (int k = 0; k < knobs.Length; k++) { had[k] = knobs[k].FormatStored(); knobs[k].Reset(); }
        Dangly.Collide.Set(false);
        bool interior = FrameInputs.Interior, skinning = AlloyRenderer.SelfCheckSkinning(true);
        FrameInputs.Interior = false;
        try
        {
            PoseAt(s, Quaternion.Identity, Quaternion.Identity, Vector3.Zero);
            body(s);
        }
        finally
        {
            for (int k = 0; k < knobs.Length; k++) knobs[k].Parse(had[k]);
            FrameInputs.Interior = interior;
            AlloyRenderer.SelfCheckSkinning(skinning);
            DanglyForget(obj);
            DropDangly(obj, first, m.Nodes.Length);
        }
    }

    /// <summary>The turn an opening curtain's hinge takes, about the door's width axis.</summary>
    private static Quaternion PoseOpen(float degrees)
        => Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees * MathF.PI / 180f);

    /// <summary>Oracle: one vertex worked by hand. A quarter of it rides a bone that lifts by (1, 2, 3) and the
    /// rest stays on the identity, so (1, 0, 0) lands on (1.25, 0.5, 0.75); slot bytes read low byte first.</summary>
    private static void PoseSkinLiteral(Checker check)
    {
        var skin = new SkinDef { Weights = new[] { new Vector4(0.25f, 0.75f, 0f, 0f) }, Bones = new uint[] { 1u } };
        ReadOnlySpan<Matrix4x4> bones = stackalloc[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(1f, 2f, 3f) };
        Vector3 p = skin.Skin(0, new Vector3(1f, 0f, 0f), bones);
        check.Expect((p - new Vector3(1.25f, 0.5f, 0.75f)).Length() < 1e-6f,
                     $"a vertex a quarter on a lifted bone lands a quarter of the lift away, at {p}");
    }

    /// <summary>Tested unasked: a curtain that has closed and stopped moving goes back to the CX motion without
    /// a jump. Oracle: the hand-stated centimetre bound against the frame before it changes hands.</summary>
    private static void PoseHandsBack(Checker check)
        => PoseRun(check, "hand-back", false, false, s =>
        {
            for (int f = 0; f < 30; f++) PoseFrame(s, windy: false);
            PoseAt(s, PoseOpen(20f), Quaternion.Identity, Vector3.Zero);
            for (int f = 0; f < PoseFrames; f++) PoseFrame(s, windy: false);
            PoseAt(s, Quaternion.Identity, Quaternion.Identity, Vector3.Zero);
            PoseFrame(s, windy: false);
            check.Expect(DynRange(s.Flap) >= 0, "a curtain that has just closed is still on the stock solve");
            var before = new Vector3[s.Drawn.Length];
            float jump = -1f;
            for (int f = 0; f < 5 * PoseFrames && jump < 0f; f++)
            {
                s.Drawn.CopyTo(before, 0);
                PoseFrame(s, windy: false);
                if (SceneDb.PartBoneBase[s.Flap] >= 0) jump = PoseGap(s.Drawn, before);
            }
            check.Expect(jump >= 0f && PoseRigged(s), "a closed curtain whose flaps have stopped moves the CX way again");
            check.Expect(jump <= 0.01f, $"...and its flaps do not jump as they change hands: moved {jump:0.0000}");
        });

    /// <summary>Tested unasked: a pole an animation drops straight down, without turning it, carries its cloth.
    /// Oracle: the stock solve run beside it.</summary>
    private static void PoseDropsStraight(Checker check)
        => PoseRun(check, "drop", false, false, s =>
        {
            for (int f = 0; f < 30; f++) PoseFrame(s, windy: false);
            float worst = 0f;
            for (int f = 1; f <= 30; f++)
            {
                SceneDb.PartPos[s.Hinge] = new Vector3(0f, 0f, 3f - 0.1f * f);
                PoseAt(s, Quaternion.Identity, Quaternion.Identity, Vector3.Zero);
                worst = MathF.Max(worst, PoseFrame(s, windy: false));
            }
            check.Expect(worst <= PoseSame, $"a dropping pole's cloth moves as the stock solve's does, strayed {worst:0.0000}");
        });

    /// <summary>Tested unasked: a part index handed to a new tenant does not inherit the old tenant's hold.
    /// Oracle: the one-path invariant, a bone range and no vertex range on the first frame at rest.</summary>
    private static void PoseRecycleForgets(Checker check)
        => PoseRun(check, "recycle", false, false, s =>
        {
            PoseFrame(s, windy: false);
            PoseAt(s, PoseOpen(20f), Quaternion.Identity, Vector3.Zero);
            for (int f = 0; f < 10; f++) PoseFrame(s, windy: false);
            DanglyPartRecycled(s.Flap);
            PoseAt(s, Quaternion.Identity, Quaternion.Identity, Vector3.Zero);
            PoseFrame(s, windy: false);
            check.Expect(PoseRigged(s), "a re-tenanted part at rest moves the CX way from its first frame");
        });

    /// <summary>E3, a regression check: a curtain nobody opens keeps the CX motion. Oracle: the one-path
    /// invariant every frame, and the rest mesh, which a breeze must move it off.</summary>
    private static void PoseClosedCurtainUnchanged(Checker check)
        => PoseRun(check, "E3", false, false, s =>
        {
            check.Expect(PoseRiggedFor(s, windy: true), "E3 a closed curtain in a breeze moves the CX way every frame");
            check.Expect(PoseGap(s.Drawn, s.Mesh.Positions) > 0f, "E3 ...and the breeze does move it");
        });

    /// <summary>E4, a regression check: a standing flag whose whole object is turned and placed keeps the CX
    /// motion. Oracle: the one-path invariant every frame.</summary>
    private static void PoseStandingFlagUnchanged(Checker check)
        => PoseRun(check, "E4", false, false, s =>
        {
            PoseAt(s, Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f),
                   new Vector3(40f, 12f, 0f));
            check.Expect(PoseRiggedFor(s, windy: true),
                         "E4 a flag whose whole object is turned and moved still waves the CX way");
        });

    /// <summary>E6, a regression check: a part a creature carries keeps the CX motion whatever its animation
    /// does. Oracle: the one-path invariant every frame.</summary>
    private static void PoseWornUnchanged(Checker check)
        => PoseRun(check, "E6", false, true, s =>
        {
            for (int f = 0; f < 30; f++) PoseFrame(s, windy: false);
            PoseAt(s, PoseOpen(60f), Quaternion.Identity, Vector3.Zero);
            check.Expect(PoseRiggedFor(s, windy: false),
                         "E6 a cloak on a creature that sits or falls moves the CX way every frame");
        });
}
