using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>The shipped <c>CxCpOn</c> and <c>CxCp</c> reading what <see cref="Db.ContentTable"/> writes, across a second
    /// row, a second on-mask entry and past each row's end. Oracle: hand-written <c>.mtr</c> lines and their values.
    /// Its rows go on the live table, freed again before it returns: the goldens never run in a session.</summary>
    private static void ContentLanesSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string src = ShaderSource.Get("cx_conbuf.glsl");
        int from = src.IndexOf("#define CX_CON_ROWS", StringComparison.Ordinal);
        const string BlockEnd = "uvec4 uConLanes[]; };\n";
        int to = src.IndexOf(BlockEnd, StringComparison.Ordinal);
        string on = Function(src, "bool CxCpOn("), value = Function(src, "vec4 CxCp(");
        bool found = from >= 0 && to > from && on.Length > 0 && value.Length > 0;
        check.Expect(found, "cx_conbuf.glsl no longer declares the block and its two readers");
        if (!found) return;

        int small = Db.ContentTable.Alloc(int.MaxValue - 1), wide = Db.ContentTable.Alloc(int.MaxValue - 2);
        check.Expect(small >= 0 && wide >= 0, "the table would not allocate the golden's rows");
        if (small < 0 || wide < 0) return;
        Db.ContentTable.FillRow(small, Db.MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes(
            "parameter float p0 7\nparameter float p2 1 2 3 4\n"), "golden_small"),
            [new("p0", ContentStage.ParamKind.Float, 0), new("p1", ContentStage.ParamKind.Float, 1), new("p2", ContentStage.ParamKind.Vec4, 2)], null);
        var wps = new ContentStage.Param[131];
        for (int i = 0; i < wps.Length; i++) wps[i] = new($"p{i}", ContentStage.ParamKind.Float, i);
        Db.ContentTable.FillRow(wide, Db.MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes(
            "parameter float p0 -1\nparameter float p79 16711680\nparameter float p127 3\nparameter float p128 5\nparameter float p130 0.5\n"), "golden_wide"),
            wps, null);
        byte[] table = [];
        int n = Db.ContentTable.Snapshot(ref table);
        Db.ContentTable.Free(small, int.MaxValue - 1);
        Db.ContentTable.Free(wide, int.MaxValue - 2);

        (int Row, int Lane, bool On, float X, float W)[] cases =
        [
            (small, 0, true, 7f, 0f), (small, 1, false, 0f, 0f), (small, 2, true, 1f, 4f), (small, 3, false, 0f, 0f), (small, 158, false, 0f, 0f), (small, -1, false, 0f, 0f),
            (wide, 0, true, -1f, 0f), (wide, 1, false, 0f, 0f), (wide, 79, true, 16711680f, 0f), (wide, 127, true, 3f, 0f),
            (wide, 128, true, 5f, 0f), (wide, 129, false, 0f, 0f), (wide, 130, true, 0.5f, 0f), (wide, 131, false, 0f, 0f), (-1, 0, false, 0f, 0f),
        ];
        var input = new Vector2[cases.Length];
        for (int i = 0; i < cases.Length; i++) input[i] = new Vector2(cases[i].Row, cases[i].Lane);

        const string Label = "golden content lanes";
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(2, 3)
            + src[from..(to + BlockEnd.Length)] + Declaration(src, "int cxConRow") + on + value
            + "void main() { int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;\n"
            + "  cxConRow = int(uIn[i]); int l = int(uIn[i + 1]);\n"
            + "  vec4 v = CxCp(l); uOut[o] = CxCpOn(l) ? 1.0 : 0.0; uOut[o + 1] = v.x; uOut[o + 2] = v.w; }\n", Label);
        BufferId buf = vk.CreateBuffer(new BufferDesc(n, BufferUse.Storage | BufferUse.CopyDst, MemoryKind.Device, Label + " table"));
        float[]? got = null;
        if (!pipe.IsNull && !buf.IsNull)
        {
            var cl = new CommandList(Label, n + (1 << 12), log);
            cl.UpdateBuffer(buf, 0, table.AsSpan(0, n));
            if (vk.Submit(cl))
                got = RunCases(vk, log, pipe, cases.Length, MemoryMarshal.Cast<Vector2, float>(input), cases.Length * 3, Label,
                               [new BufferBinding(SsboSlots.Content, buf, 0, n)]);
        }
        vk.Destroy(pipe);
        vk.Destroy(buf);
        check.Expect(got != null, "the content lanes golden's stage was refused (logged above)");
        if (got == null) return;
        ReadOnlySpan<Vector3> answers = MemoryMarshal.Cast<float, Vector3>(got);
        for (int i = 0; i < cases.Length; i++)
        {
            var c = cases[i];
            Vector3 a = answers[i];
            check.Expect((a.X != 0f) == c.On && a.Y == c.X && a.Z == c.W,
                         $"row {c.Row} lane {c.Lane} reads on={a.X} ({a.Y}, {a.Z}), not on={c.On} ({c.X}, {c.W})");
        }
        log.Info($"GpuGolden: content lanes, {cases.Length} cases");
    }
}
