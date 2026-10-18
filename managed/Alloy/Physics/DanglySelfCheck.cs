using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Physics;

/// <summary>The dangly motion suite over SYNTHETIC nodes -- no engine, no GL, so <c>run-demo.sh</c> runs it.
/// Each fixture is small enough that every expected number below is hand-computed from the spec's formulas
/// and the fixture's own geometry, never from the code under test.</summary>
internal static class DanglySelfCheck
{
    internal static bool Run(ExtenderLog log)
    {
        var check = new Checker(log, "DanglySelfCheck");
        DanglyRigCheck.Cases(check);
        DanglyJointsCheck.Cases(check);
        VanillaDanglyCheck.Cases(check);
        log.Info(check.Ok ? "DanglySelfCheck: passed"
                          : "DanglySelfCheck: FAILED -- a dangly node would be classed, rigged, solved or "
                          + "drawn wrongly");
        return check.Ok;
    }

    /// <summary>A one-node model whose single dangly node carries <paramref name="pos"/>,
    /// <paramref name="cons"/> and the three authored solver terms. The node sits unrotated at the model
    /// root, so its authored frame is the model's.</summary>
    internal static ModelDef Node(Vector3[] pos, float[] cons, int[] tris,
                                  float displacement, float tightness, float period)
    {
        var mesh = new MeshDef { Positions = pos };
        if (tris.Length >= 3)
        {
            var faces = new FaceDef[tris.Length / 3];
            for (int f = 0; f < faces.Length; f++)
                faces[f] = new FaceDef { V0 = tris[f * 3], V1 = tris[f * 3 + 1], V2 = tris[f * 3 + 2] };
            mesh.Faces = faces;
        }
        var n = new NodeDef
        {
            Name = "dangly", Index = 0, Parent = -1, Kind = NodeKind.Dangly, Mesh = mesh,
            Dangly = new DanglyDef
            {
                Constraints = cons, Displacement = displacement, Tightness = tightness, Period = period,
            },
        };
        var m = new ModelDef { Name = "cx_selfcheck_dangly", Nodes = new[] { n } };
        m.NodeByName[n.Name] = 0;
        return m;
    }

    /// <summary>The rig of a fixture's one dangly node.</summary>
    internal static DanglyRig? Rig(ModelDef m) => DanglyRig.Build(m, m.Nodes[0]);

    /// <summary>Vegetation card: four anchors (constraint 0) on the ground and two vertices at
    /// <c>c = 112</c> 1.5 units up, every triangle in the <c>y = 0</c> plane so its drag tensor is exactly
    /// <c>diag(0, 1, 0)</c>. Census medians for vegetation: 0.3 / 1 / 0.8.</summary>
    internal static ModelDef Card() => Node(
        new[]
        {
            new Vector3(-0.15f, 0f, 0f), new Vector3(-0.05f, 0f, 0f),
            new Vector3(0.05f, 0f, 0f), new Vector3(0.15f, 0f, 0f),
            new Vector3(-0.1f, 0f, 1.5f), new Vector3(0.1f, 0f, 1.5f),
        },
        new[] { 0f, 0f, 0f, 0f, 112f, 112f },
        new[] { 0, 1, 4, 1, 2, 4, 2, 4, 5, 2, 3, 5 },
        0.3f, 1f, 0.8f);

    /// <summary>Hanging banner: a 5 x 4 grid pinned on its top row at <c>c = 255</c> (the other pin value),
    /// 1.2 wide and 1.5 deep. Census medians for cloth: 0.3 / 0.1 / 0.2.</summary>
    internal static ModelDef Banner()
    {
        var pos = new Vector3[20];
        var cons = new float[20];
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 5; col++)
            {
                pos[row * 5 + col] = new Vector3(-0.6f + col * 0.3f, 0f, -0.5f * row);
                cons[row * 5 + col] = row == 0 ? 255f : 112f;
            }
        var tris = new int[4 * 3 * 6];
        int t = 0;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 4; col++)
            {
                int a = row * 5 + col;
                tris[t++] = a; tris[t++] = a + 1; tris[t++] = a + 5;
                tris[t++] = a + 1; tris[t++] = a + 6; tris[t++] = a + 5;
            }
        return Node(pos, cons, tris, 0.3f, 0.1f, 0.2f);
    }

    /// <summary>Rigging: a rope of five two-vertex levels, 2 units long and 0.04 wide, pinned at the top.
    /// Census medians for rigging: 0.1 / 0.1 / 0.1.</summary>
    internal static ModelDef Rope()
    {
        var pos = new Vector3[10];
        var cons = new float[10];
        for (int lvl = 0; lvl < 5; lvl++)
        {
            pos[lvl * 2] = new Vector3(-0.02f, 0f, -0.5f * lvl);
            pos[lvl * 2 + 1] = new Vector3(0.02f, 0f, -0.5f * lvl);
            cons[lvl * 2] = cons[lvl * 2 + 1] = lvl == 0 ? 0f : 112f;
        }
        var tris = new int[4 * 6];
        int t = 0;
        for (int lvl = 0; lvl < 4; lvl++)
        {
            int a = lvl * 2;
            tris[t++] = a; tris[t++] = a + 1; tris[t++] = a + 2;
            tris[t++] = a + 1; tris[t++] = a + 3; tris[t++] = a + 2;
        }
        return Node(pos, cons, tris, 0.1f, 0.1f, 0.1f);
    }

    /// <summary>A hanging vine strand as the city tilesets ship it (<c>tcm02_c01_04</c>, <c>hanger_03</c>): one
    /// two-sided quad 0.41 wide and 2.07 long, its top edge pinned and its bottom corners at <c>c = 190</c>,
    /// turned off the axes so its face normal is (0.828, 0.560, 0). Authored 0.3 / 0.1 / 0.2.</summary>
    internal static ModelDef Strip() => Node(
        new[]
        {
            new Vector3(0.11f, -0.17f, 1.03f), new Vector3(0.12f, -0.17f, -1.04f),
            new Vector3(-0.11f, 0.17f, -1.04f), new Vector3(-0.12f, 0.17f, 1.03f),
            new Vector3(-0.11f, 0.17f, -1.04f), new Vector3(0.12f, -0.17f, -1.04f),
            new Vector3(0.11f, -0.17f, 1.03f), new Vector3(-0.12f, 0.17f, 1.03f),
        },
        new[] { 0f, 190f, 190f, 0f, 190f, 190f, 0f, 0f },
        new[] { 0, 1, 2, 2, 3, 0, 4, 5, 6, 6, 7, 4 },
        0.3f, 0.1f, 0.2f);

    /// <summary>A blob: four anchors round the equator and two vertices at <c>c = 150</c>, one above and one
    /// below, so mobility does not correlate with height. Authored stiff and slack: 0.3 / 3 / 8.</summary>
    internal static ModelDef Blob() => Node(
        new[]
        {
            new Vector3(0.2f, 0f, 0f), new Vector3(0f, 0.2f, 0f),
            new Vector3(-0.2f, 0f, 0f), new Vector3(0f, -0.2f, 0f),
            new Vector3(0f, 0f, 0.4f), new Vector3(0f, 0f, -0.2f),
        },
        new[] { 0f, 0f, 0f, 0f, 150f, 150f },
        new[] { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4, 1, 0, 5, 2, 1, 5, 3, 2, 5, 0, 3, 5 },
        0.3f, 3f, 8f);

    /// <summary>A body part: twelve anchors in three rings and a four-vertex flap at <c>c = 112</c> on the
    /// middle ring's height, so a quarter of the mesh moves and mobility does not track height. Census
    /// medians for body parts: 0.03 / 3.5 / 7.</summary>
    internal static ModelDef BodyPart()
    {
        var pos = new Vector3[16];
        var cons = new float[16];
        for (int ring = 0; ring < 3; ring++)
        {
            float z = ring * 0.5f;
            pos[ring * 4] = new Vector3(0.1f, 0f, z);
            pos[ring * 4 + 1] = new Vector3(0f, 0.1f, z);
            pos[ring * 4 + 2] = new Vector3(-0.1f, 0f, z);
            pos[ring * 4 + 3] = new Vector3(0f, -0.1f, z);
        }
        pos[12] = new Vector3(0.2f, 0f, 0.5f);
        pos[13] = new Vector3(0.3f, 0f, 0.5f);
        pos[14] = new Vector3(0.2f, 0.1f, 0.5f);
        pos[15] = new Vector3(0.3f, 0.1f, 0.5f);
        for (int i = 12; i < 16; i++) cons[i] = 112f;
        return Node(pos, cons, new[] { 12, 13, 14, 13, 15, 14 }, 0.03f, 3.5f, 7f);
    }

    /// <summary>Every vertex pinned: nothing the joints can move.</summary>
    internal static ModelDef AllAnchors() => Node(
        new[]
        {
            new Vector3(-0.1f, 0f, 0f), new Vector3(0.1f, 0f, 0f),
            new Vector3(-0.1f, 0f, 0.5f), new Vector3(0.1f, 0f, 0.5f),
        },
        new[] { 0f, 0f, 255f, 255f },
        new[] { 0, 1, 2, 1, 3, 2 },
        0.3f, 1f, 0.8f);

    /// <summary>Is <paramref name="got"/> within <paramref name="tol"/> of <paramref name="want"/>?</summary>
    internal static bool Near(float got, float want, float tol) => MathF.Abs(got - want) <= tol;

    /// <summary>Is <paramref name="got"/> within <paramref name="rel"/> (a fraction) of
    /// <paramref name="want"/>?</summary>
    internal static bool Rel(float got, float want, float rel)
        => MathF.Abs(got - want) <= rel * MathF.Abs(want);

    internal static bool Near(Vector3 got, Vector3 want, float tol) => (got - want).Length() <= tol;
}
