using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Db;

public static partial class MeshArena
{
    /// <summary>Approved-spec: V1 to V3, where a mesh lands and what the arena holds across loads and frees.
    /// Oracles: hand-written vertex counts and the places they add up to.</summary>
    internal static void ArenaSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MeshArena.ArenaSelfCheck");
        if (!ResetForCheck()) { check.Skip("the arena holds live meshes: the suite runs in run-demo, not in a session"); return; }
        try
        {
            ReuseGap(check);
            check.Expect(ResetForCheck(), "V1 left a mesh held");
            Hop(check);
            check.Expect(ResetForCheck(), "V2 left a mesh held");
            Full(check);
        }
        finally { check.Expect(ResetForCheck(), "V3 left a mesh held"); }
        log.Info(check.Ok ? "MeshArena.ArenaSelfCheck: passed" : "MeshArena.ArenaSelfCheck: FAILED");
    }

    private static readonly ushort[] Tri = [0, 1, 2];

    /// <summary>A static mesh of <paramref name="nv"/> vertices keyed by <paramref name="content"/>, every vertex the same.</summary>
    private static int Mesh(ulong content, int nv, float[] verts)
        => AcquireRaw(content, Fmt.Static, nv, verts.AsSpan(0, nv * StrideOf[(int)Fmt.Static]), Tri, 3, null, 0, null, 0, Vector3.Zero, Vector3.One);

    private static float[] Verts(int nv) => new float[nv * StrideOf[(int)Fmt.Static]];

    /// <summary>V1: B's gap takes D, A and C stay put; a mesh larger than any gap goes after C.</summary>
    private static void ReuseGap(Checker check)
    {
        float[] v = Verts(2000);
        int a = Mesh(0xA378_0001, 1000, v), b = Mesh(0xA378_0002, 1000, v), c = Mesh(0xA378_0003, 1000, v);
        int baseA = PlaceForCheck(a).Base, baseB = PlaceForCheck(b).Base, baseC = PlaceForCheck(c).Base;
        Release(b);
        FrameEdgeForCheck();
        int d = Mesh(0xA378_0004, 800, v);
        check.Expect(PlaceForCheck(d).Base == baseB, $"V1: D landed at vertex {PlaceForCheck(d).Base}, not in B's place at {baseB}");
        check.Expect(PlaceForCheck(a).Base == baseA && PlaceForCheck(c).Base == baseC, "V1: A or C moved when D was loaded");
        int e = Mesh(0xA378_0005, 2000, v);
        check.Expect(PlaceForCheck(e).Base >= baseC + 1000, $"V1: a mesh larger than any gap landed at {PlaceForCheck(e).Base}, before C's end at {baseC + 1000}");
        foreach (int r in (int[])[a, c, d, e]) if (r > 0) Release(r);
        FrameEdgeForCheck();
    }

    /// <summary>V2: a 3-million-vertex area left for another of the same size holds about one area; going back, the same.</summary>
    private static void Hop(Checker check)
    {
        const int Meshes = 60, Each = 50_000;
        float[] v = Verts(Each);
        var held = new int[Meshes];
        void Load(ulong salt)
        {
            for (int i = 0; i < Meshes; i++) held[i] = Mesh(salt + (ulong)i, Each, v);
        }
        void Leave()
        {
            foreach (int r in held) if (r > 0) Release(r);
            FrameEdgeForCheck();
        }
        Load(0xB378_0000);
        long one = VramBytes;
        Leave();
        Load(0xB378_1000);
        long second = VramBytes;
        check.Expect(second <= one + one / 10, $"V2: the second area holds {second >> 20} MiB against one area's {one >> 20} MiB");
        Leave();
        Load(0xB378_0000);
        check.Expect(VramBytes == second, $"V2: returning to the first area holds {VramBytes >> 20} MiB, not the {second >> 20} MiB the second held");
        Leave();
    }

    /// <summary>V3: a full arena refuses a mesh and counts it section-full; once a mesh is freed, one that fits its gap loads.</summary>
    private static void Full(Checker check)
    {
        const int Each = 65_536, Meshes = 128;
        float[] v = Verts(Each);
        var held = new int[Meshes];
        for (int i = 0; i < Meshes; i++) held[i] = Mesh(0xC378_0000 + (ulong)i, Each, v);
        check.Expect(Array.TrueForAll(held, r => r > 0), "V3: the arena refused a mesh before 8M vertices were held");
        int over = Mesh(0xC378_1000, 1000, v);
        check.Expect(over <= 0, "V3: a full arena loaded a mesh that does not fit");
        check.Expect(Stats(1).Contains("section-full", StringComparison.Ordinal), "V3: the period line does not count the refusal as section-full");
        Release(held[17]);
        FrameEdgeForCheck();
        int fits = Mesh(0xC378_1001, 1000, v);
        check.Expect(fits > 0, "V3: a mesh that fits a freed gap in a full arena was refused");
        foreach (int r in held) if (r > 0 && r != held[17]) Release(r);
        foreach (int r in (int[])[over, fits]) if (r > 0) Release(r);
        FrameEdgeForCheck();
    }
}
