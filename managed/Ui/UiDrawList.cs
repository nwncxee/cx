using System;
using CX.Framework;

namespace CX.Ui;

/// <summary>Which program a recorded UI draw replays through: what it samples and how it mixes in the vertex colour.</summary>
public enum UiDrawKind : byte { Text, Portrait, Icon, Skin, IconPlt }

/// <summary>One recorded UI draw: a vertex range of its <see cref="UiDrawList"/> and every piece of state the replay
/// sets for it. The clip rect is y-down from the top-left, in px.</summary>
public struct UiCmd
{
    public UiDrawKind Kind;
    /// <summary>Sample through the linear-clamp sampler object rather than the texture's own filter.</summary>
    public bool Sampler, Clip;
    public uint Tex, Pal;
    public int First, Count, RowsAt;
    public float Alpha, OffX, OffY, U1, V0;
    public int Cx, Cy, Cw, Ch;
    /// <summary>Fade to <see cref="Hidden"/> of its alpha where the world frame's depth covers point (Wx, Wy, Wz), read
    /// at UI pixel (Ax, Ay): the scene test of a draw anchored to something in the world.</summary>
    public bool Occl;
    public float Hidden, Wx, Wy, Wz, Ax, Ay;
}

/// <summary>A frame's UI draws as data: vertices in one stream, commands over ranges of it, PLT rows beside them.
/// Recording issues no GL, so the draws can be replayed in whichever context draws the frame. Adjacent commands
/// with the same state merge into one draw. Grows by doubling (cold); a steady frame allocates nothing.</summary>
public sealed class UiDrawList
{
    private const int Floats = VertexBatch.Floats;

    public float[] Verts = new float[64 * 1024];
    public UiCmd[] Cmds = new UiCmd[512];
    public int[] Rows = new int[256];
    public int VertN, CmdN, RowN;
    /// <summary>The viewport the draws were laid out against: the replay's ortho and its scissor flip.</summary>
    public int ViewW, ViewH;

    public void Reset(int w, int h) { VertN = CmdN = RowN = 0; ViewW = w; ViewH = h; }

    /// <summary>Append <paramref name="count"/> vertices of <paramref name="verts"/> under <paramref name="c"/>'s state
    /// (its First/Count/RowsAt are filled in here). A span shorter than the count clamps it.</summary>
    public void Add(in UiCmd c, ReadOnlySpan<float> verts, int count, ReadOnlySpan<int> rows = default)
    {
        if (count > verts.Length / Floats) count = verts.Length / Floats;
        if (count <= 0) return;
        int floats = count * Floats;
        if (VertN + floats > Verts.Length) Array.Resize(ref Verts, Math.Max(Verts.Length * 2, VertN + floats));
        verts[..floats].CopyTo(Verts.AsSpan(VertN));
        UiCmd cmd = c;
        cmd.First = VertN / Floats;
        cmd.Count = count;
        VertN += floats;
        if (rows.Length > 0)
        {
            if (RowN + rows.Length > Rows.Length) Array.Resize(ref Rows, Math.Max(Rows.Length * 2, RowN + rows.Length));
            rows.CopyTo(Rows.AsSpan(RowN));
            cmd.RowsAt = RowN;
            RowN += rows.Length;
        }
        else if (CmdN > 0 && SameState(in Cmds[CmdN - 1], in cmd)) { Cmds[CmdN - 1].Count += count; return; }
        if (CmdN == Cmds.Length) Array.Resize(ref Cmds, Cmds.Length * 2);
        Cmds[CmdN++] = cmd;
    }

    /// <summary>Would one draw of both be the same draw? PLT commands never merge: their rows are per command.</summary>
    private static bool SameState(in UiCmd a, in UiCmd b)
        => a.Kind == b.Kind && a.Kind != UiDrawKind.IconPlt && a.Tex == b.Tex && a.Sampler == b.Sampler
        && a.Alpha == b.Alpha && a.OffX == b.OffX && a.OffY == b.OffY && a.U1 == b.U1 && a.V0 == b.V0
        && a.Clip == b.Clip && (!a.Clip || (a.Cx == b.Cx && a.Cy == b.Cy && a.Cw == b.Cw && a.Ch == b.Ch))
        && a.Occl == b.Occl && (!a.Occl || (a.Hidden == b.Hidden && a.Wx == b.Wx && a.Wy == b.Wy && a.Wz == b.Wz
                                            && a.Ax == b.Ax && a.Ay == b.Ay));

    /// <summary>Merging, ranges, clamping, rows and growth, off-device. Oracle: hand-written vertex counts and
    /// offsets for a known sequence of appends.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var l = new UiDrawList();
        Span<float> quad = stackalloc float[6 * Floats];
        for (int i = 0; i < quad.Length; i++) quad[i] = i;
        l.Reset(800, 600);
        var text = new UiCmd { Kind = UiDrawKind.Text, Alpha = 1f };
        l.Add(text, quad, 6);
        l.Add(text, quad, 6);
        check.Expect(l.CmdN == 1 && l.Cmds[0].First == 0 && l.Cmds[0].Count == 12, "two same-state batches did not merge into one 12-vertex draw");
        check.Expect(l.VertN == 96 && l.Verts[48 + 47] == 47f, "the second batch's vertices did not land after the first");
        l.Add(text with { Clip = true, Cx = 1, Cy = 2, Cw = 3, Ch = 4 }, quad, 6);
        check.Expect(l.CmdN == 2 && l.Cmds[1].First == 12 && l.Cmds[1].Count == 6, "a clipped batch merged into an unclipped one");
        l.Add(text with { Clip = true, Cx = 1, Cy = 2, Cw = 3, Ch = 5 }, quad, 6);
        check.Expect(l.CmdN == 3, "batches under different clip rects merged");
        Span<int> rows = stackalloc int[10];
        for (int i = 0; i < 10; i++) rows[i] = 100 + i;
        var plt = new UiCmd { Kind = UiDrawKind.IconPlt, Tex = 7, Pal = 9, Alpha = 1f };
        l.Add(plt, quad, 6, rows);
        l.Add(plt, quad, 6, rows);
        check.Expect(l.CmdN == 5 && l.Cmds[3].RowsAt == 0 && l.Cmds[4].RowsAt == 10 && l.Rows[19] == 109, "PLT commands merged or lost their rows");
        l.Add(text, quad, 50);
        check.Expect(l.Cmds[l.CmdN - 1].Count == 6, "a count past the span was not clamped to it");
        l.Add(text, quad, 0);
        check.Expect(l.CmdN == 6, "an empty batch added a command");
        l.Reset(800, 600);
        for (int i = 0; i < 2000; i++) l.Add(new UiCmd { Kind = UiDrawKind.Icon, Tex = (uint)i }, quad, 6);
        check.Expect(l.CmdN == 2000 && l.VertN == 2000 * 48 && l.Cmds[1999].First == 1999 * 6 && l.Verts[1999 * 48 + 5] == 5f,
                     "growth past the initial capacity lost commands or vertices");
        l.Reset(800, 600);
        var plate = text with { Occl = true, Hidden = 0.3f, Wx = 10f, Ax = 40f };
        l.Add(text, quad, 6);
        l.Add(plate, quad, 6);
        l.Add(plate with { Wx = 11f }, quad, 6);
        l.Add(plate with { Wx = 11f }, quad, 6);
        check.Expect(l.CmdN == 3 && l.Cmds[2].Count == 12, "draws anchored to different world points merged, or one point's did not");
        log.Info(check.Ok ? "UiDrawList.SelfCheck: passed" : "UiDrawList.SelfCheck: FAILED");
    }
}
