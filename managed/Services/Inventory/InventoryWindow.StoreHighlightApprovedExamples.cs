using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Ui;
using static CX.Ui.PanelChrome;

namespace CX.Services.Inventory;

/// <summary>The grid shows where a store item the engine drags will land. Oracle: the approved examples, read
/// off the frame the window emits, and the cell the release buys into (<see cref="Buy"/>).</summary>
internal sealed partial class InventoryWindow
{
    /// <summary>One emitted frame: the grid cells filled green or red (local rows, as <see cref="Bench.Cy"/> counts them) and
    /// the count of green or red quads in the window's over batch, where the worn slots light.</summary>
    private sealed record Lit(List<(int X, int Y)> Green, List<(int X, int Y)> Red, int Over)
    {
        public bool None => Green.Count == 0 && Red.Count == 0;
        public string Cells => $"green [{string.Join(" ", Green)}] red [{string.Join(" ", Red)}]";
    }

    private static Lit LitFrame(Bench b)
    {
        var atlas = new GlyphAtlas { LineHeight = 18, Ascent = 14 };
        for (int cp = 32; cp < 127; cp++) atlas.SetTestGlyph(cp, 8f);
        var under = new VertexBatch(0f, 0f); var over = new VertexBatch(0f, 0f); var gu = new VertexBatch(0f, 0f);
        var go = new VertexBatch(0f, 0f); var lu = new VertexBatch(0f, 0f); var lo = new VertexBatch(0f, 0f);
        b.W.Emit(under, over, gu, go, lu, lo, null, atlas, atlas, new InvIcon[64]);
        var green = new List<(int, int)>();
        var red = new List<(int, int)>();
        float s = b.W.S;
        foreach ((float x0, float y0, uint rgb) in Quads(go))
        {
            var at = ((int)MathF.Round(((x0 - 1f) / s - Pad) / Cell), (int)MathF.Round(((y0 - 1f) / s - (TitleH + Pad + EquipH + Gap)) / Cell));
            if (rgb == Ok && !green.Contains(at)) green.Add(at);
            else if (rgb == Warn && !red.Contains(at)) red.Add(at);
        }
        int lit = 0;
        foreach ((_, _, uint rgb) in Quads(over)) if (rgb == Ok || rgb == Warn) lit++;
        green.Sort(); red.Sort();
        return new Lit(green, red, lit);
    }

    /// <summary>Each quad's top-left corner and colour.</summary>
    private static List<(float X, float Y, uint Rgb)> Quads(VertexBatch b)
    {
        ReadOnlySpan<float> v = b.AsSpan();
        var quads = new List<(float, float, uint)>();
        for (int q = 0; q + 6 * VertexBatch.Floats <= v.Length; q += 6 * VertexBatch.Floats)
        {
            uint rgb = ((uint)MathF.Round(v[q + 4] * 255f) << 16) | ((uint)MathF.Round(v[q + 5] * 255f) << 8) | (uint)MathF.Round(v[q + 6] * 255f);
            quads.Add((v[q], v[q + 1], rgb));
        }
        return quads;
    }

    private static void StoreHighlight(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        (uint, int, int) drag = default;
        w.StoreDrag = () => drag;
        w.EngineDragging = () => drag.Item2 > 0;
        w.MouseMove(5000f, 5000f);
        int overAtRest = LitFrame(b).Over;

        // Given a store is open and the CX inventory is showing
        // When the player drags a store item over a free place in the backpack grid
        // Then the cells the item will land in are lit green, centred on the pointer, as for a drag inside the window
        drag = (StoreHelm, 2, 2);
        w.MouseMove(b.Cx(4), b.Cy(5));
        Lit free = LitFrame(b);
        check.Expect(free.Green.Count == 4 && free.Green.Contains((3, 4)) && free.Green.Contains((4, 4)) && free.Green.Contains((3, 5)) && free.Green.Contains((4, 5)) && free.Red.Count == 0,
                     $"a store helmet over cell (4, 5) lights (3, 4) to (4, 5) green, the cells it is bought into ({free.Cells})");
        check.Expect(free.Over == overAtRest, $"a store helmet over the grid lights no worn slot ({free.Over} lit quads, {overAtRest} at rest)");

        // Given a store item lit over the grid
        // When the pointer leaves the window, or the player lets go
        // Then the highlight is gone
        w.MouseMove(w.X + w.W + 200f, w.Y + 10f);
        Lit left = LitFrame(b);
        w.MouseMove(b.Cx(4), b.Cy(5));
        bool relit = !LitFrame(b).None;
        w.MouseButton(false, 1, b.Cx(4), b.Cy(5), 0);
        drag = default;
        Lit gone = LitFrame(b);
        check.Expect(relit && left.None && gone.None,
                     $"the highlight goes off the window and on the release (back over the grid {relit}, off the window {left.Cells}, after the release {gone.Cells})");
    }
}
