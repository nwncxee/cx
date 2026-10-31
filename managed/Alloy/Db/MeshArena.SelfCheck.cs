using System;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

public static partial class MeshArena
{
    /// <summary>A freed place while a submit that may still draw it is in flight, and the bytes one load
    /// sends. Oracles: hand-written vertex counts, the places they add up to, and the byte ranges those
    /// places and a static vertex's 48 bytes give.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MeshArena.SelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero)
        { check.Skip("in a session: the arena holds the world's meshes and no check may reset it"); return; }
        if (!ResetForCheck()) { check.Skip("the arena holds live meshes"); return; }
        try
        {
            HeldInFlight(check);
            check.Expect(ResetForCheck(), "the in-flight example left a mesh held");
            OwnBytesOnly(check);
        }
        finally { check.Expect(ResetForCheck(), "the upload-span example left a mesh held"); }
        log.Info(check.Ok ? "MeshArena.SelfCheck: passed" : "MeshArena.SelfCheck: FAILED");
    }

    /// <summary>B's place is not handed on while the submit that could still draw it is in flight: D lands
    /// after C instead. Three 1000-vertex meshes land at 0, 1000 and 2000, so the tail is 3000.</summary>
    private static void HeldInFlight(Checker check)
    {
        float[] v = Verts(1000);
        FlightForCheck(7, 6);   // submit 7 is the one being recorded; 6 and below have finished
        int a = Mesh(0xD378_0001, 1000, v), b = Mesh(0xD378_0002, 1000, v), c = Mesh(0xD378_0003, 1000, v);
        check.Expect(PlaceForCheck(a).Base == 0 && PlaceForCheck(b).Base == 1000 && PlaceForCheck(c).Base == 2000,
            $"the three meshes landed at {PlaceForCheck(a).Base}/{PlaceForCheck(b).Base}/{PlaceForCheck(c).Base}, not 0/1000/2000");
        Release(b);
        FrameEdgeForCheck();
        int d = Mesh(0xD378_0004, 800, v);
        check.Expect(PlaceForCheck(d).Base == 3000,
            $"D took vertex {PlaceForCheck(d).Base} while submit 7 may still draw B's 1000..2000 -- it must land at 3000");
        FlightForCheck(8, 7);   // the submit that could have drawn B has finished
        FrameEdgeForCheck();
        int e = Mesh(0xD378_0005, 800, v);
        check.Expect(PlaceForCheck(e).Base == 1000,
            $"E landed at vertex {PlaceForCheck(e).Base}, not in B's place at 1000 once the submit that held it had passed");
        foreach (int r in (int[])[a, c, d, e]) if (r > 0) Release(r);
        FlightForCheck(0, long.MaxValue);
        FrameEdgeForCheck();
    }

    /// <summary>One load sends its own bytes and no others: the fourth 1000-vertex static mesh dirties
    /// vertices 3000..3800 (144,000..182,400 B at 48 B a vertex) and indices 9..12 (18..24 B), and one placed
    /// in a freed gap below them dirties that gap alone.</summary>
    private static void OwnBytesOnly(Checker check)
    {
        float[] v = Verts(1000);
        FlightForCheck(0, long.MaxValue);   // nothing in flight: a freed place is the next mesh's at once
        check.Expect(PendingVertexBytesForCheck(Fmt.Static) == (0, 0L, 0L), "the reset arena has spans pending");
        int a = Mesh(0xE378_0001, 1000, v), b = Mesh(0xE378_0002, 1000, v), c = Mesh(0xE378_0003, 1000, v);
        check.Expect(a > 0 && b > 0 && c > 0, "the three meshes the example loads were refused");
        SentForCheck();
        int d = Mesh(0xE378_0004, 800, v);
        check.Expect(PendingVertexBytesForCheck(Fmt.Static) == (1, 144_000L, 182_400L),
            $"loading D sends {PendingVertexBytesForCheck(Fmt.Static)}, not one span of 144,000..182,400 B");
        check.Expect(PendingIndexBytesForCheck() == (1, 18L, 24L),
            $"loading D sends {PendingIndexBytesForCheck()} of the element arena, not one span of 18..24 B");
        Release(b);
        FrameEdgeForCheck();
        SentForCheck();
        int e = Mesh(0xE378_0005, 800, v);
        check.Expect(PendingVertexBytesForCheck(Fmt.Static) == (1, 48_000L, 86_400L),
            $"loading E into B's gap sends {PendingVertexBytesForCheck(Fmt.Static)}, not one span of 48,000..86,400 B");
        check.Expect(PendingIndexBytesForCheck() == (1, 6L, 12L),
            $"loading E into B's gap sends {PendingIndexBytesForCheck()} of the element arena, not one span of 6..12 B");
        foreach (int r in (int[])[a, c, d, e]) if (r > 0) Release(r);
        FrameEdgeForCheck();
    }
}
