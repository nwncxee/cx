using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Ui;

/// <summary>A deeper cascade level hides the levels under it: each level is painted whole, frame, text and icons,
/// before the next one starts. Oracle: the level rects the layout reports, against each paint's vertices.</summary>
internal static class CascadeMenuLayersSelfCheck
{
    /// <summary>One paint the owner received: its vertices' bounds and its icons.</summary>
    private sealed record Paint(float X0, float Y0, float X1, float Y1, int Vertices, IconQuad[] Icons);

    /// <summary>Records each skin pass opened and each paint, as an owner's draw calls would run them.</summary>
    private struct Recorder : ICascadeSink
    {
        public List<Paint> Paints;
        public int Begins;
        public void Begin() => Begins++;
        public void Paint(VertexBatch b, ReadOnlySpan<IconQuad> icons)
        {
            float[] v = b.ToArray();
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i + 1 < v.Length; i += VertexBatch.Floats)
            {
                x0 = MathF.Min(x0, v[i]); y0 = MathF.Min(y0, v[i + 1]);
                x1 = MathF.Max(x1, v[i]); y1 = MathF.Max(y1, v[i + 1]);
            }
            Paints.Add(new Paint(x0, y0, x1, y1, v.Length / VertexBatch.Floats, icons.ToArray()));
        }
    }

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "CascadeMenu.Layers.SelfCheck");
        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        for (char ch = 'a'; ch <= 'z'; ch++) atlas.SetTestGlyph(ch, 6f);
        for (char ch = '0'; ch <= '9'; ch++) atlas.SetTestGlyph(ch, 6f);
        atlas.SetTestGlyph('>', 6f);
        var st = CascadeStyle.Current;
        st.AnimMs = 0;

        // Examples: none, because found work: nested submenus that overlap show the shallower level's labels and icons through the deeper one
        // Given three open levels in a viewport too narrow for them side by side, each with an icon row
        // When the menu renders
        // Then it paints each level whole, root first, and every paint lies inside its own level
        var root = new[] { new CascadeItem("root0", "ir_a") { HasChildren = true }, new CascadeItem("root1", "ir_b") };
        var child = new[] { new CascadeItem("child0", "ir_c") { HasChildren = true }, new CascadeItem("child1") };
        var grand = new[] { new CascadeItem("grand0", "ir_d"), new CascadeItem("grand1") };
        var m = new CascadeMenu { Drill = false };
        m.OpenAt(root, 10f, 10f, atlas, 260f, 600f);
        m.OpenChild(0, 0, child);
        m.OpenChild(1, 0, grand);
        var b = new VertexBatch(atlas.SolidU, atlas.SolidV);
        var rec = new Recorder { Paints = new List<Paint>() };
        m.Render(ref rec, b, atlas, 15f, in st);
        bool overlap = m.Level(2).X < m.Level(0).X + m.Level(0).W;
        bool whole = rec.Paints.Count == 3 && rec.Begins == 3;
        for (int i = 0; whole && i < 3; i++)
        {
            CascadeLevel lv = m.Level(i);
            Paint p = rec.Paints[i];
            whole = p.X0 >= lv.X - 1f && p.Y0 >= lv.Y - 1f && p.X1 <= lv.X + lv.W + 1f && p.Y1 <= lv.Y + lv.H + 1f
              && p.Icons.Length == (i == 0 ? 2 : 1) && p.Icons[0].Resref == (i == 0 ? "ir_a" : i == 1 ? "ir_c" : "ir_d");
        }
        check.Expect(overlap, $"the deepest level is clamped over the root: grand x {m.Level(2).X}, root right {m.Level(0).X + m.Level(0).W}");
        var seen = new System.Text.StringBuilder();
        for (int i = 0; i < rec.Paints.Count && i < m.Depth; i++)
            seen.Append($" [{rec.Paints[i].X0},{rec.Paints[i].Y0}..{rec.Paints[i].X1},{rec.Paints[i].Y1} icons {rec.Paints[i].Icons.Length} in {m.Level(i).X},{m.Level(i).Y} {m.Level(i).W}x{m.Level(i).H}]");
        check.Expect(whole, $"each level is painted whole, root first: {rec.Paints.Count} paints, {rec.Begins} skin passes;{seen}");

        if (check.Ok) log.Info("CascadeMenu.Layers.SelfCheck: passed");
    }
}
