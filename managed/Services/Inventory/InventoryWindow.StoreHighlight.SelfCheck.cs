using System.Collections.Generic;
using CX.Framework;

namespace CX.Services.Inventory;

/// <summary>The store highlight's regression tests: where a release leaves the cell to the server, or buys nothing, nothing
/// lights. These held before the grid lit a store drag; the ring, the page tabs and an item bigger than a page are edges
/// the mutants found, red before the fix. Oracle: the approved examples, read off the frame (<see cref="LitFrame"/>), and
/// the cells a release buys into (<see cref="Buy"/>).</summary>
internal sealed partial class InventoryWindow
{
    private const uint StoreTall = 303;

    private static void StoreUnlit(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        (uint, int, int) drag = default;
        bool button = false;
        w.StoreDrag = () => drag;
        w.EngineDragging = () => drag.Item2 > 0 || button;
        w.MouseMove(5000f, 5000f);
        int overAtRest = LitFrame(b).Over;

        // Given a store is open and the CX inventory is showing
        // When the drag is over a place where the item does not fit, such as a taken cell
        // Then no cells are lit, since letting go there still buys the item and the server picks its cell
        drag = (StoreRing, 1, 1);
        w.MouseMove(b.Cx(0), b.Cy(1));
        Lit onSword = LitFrame(b);
        drag = (StoreHelm, 2, 2);
        w.MouseMove(b.Cx(1), b.Cy(0));
        Lit overlaps = LitFrame(b);
        check.Expect(onSword.None && overlaps.None,
                     $"a store ring on the sword, or a helmet whose footprint covers it, lights no cell (ring {onSword.Cells}, helmet {overlaps.Cells})");

        // Given a store is open and the CX inventory is showing
        // When the drag is over the window frame, a worn slot, or a backpack page still loading
        // Then nothing in the window is lit, and no worn slot lights up as taking the item
        w.MouseMove(w.X + w.W / 2f, w.Y + 10f * w.S);
        Lit title = LitFrame(b);
        w.MouseMove(b.SlotX("head"), b.SlotY("head"));
        Lit head = LitFrame(b);
        w.Top = w._target = InventoryModel.Rows;
        w.Model.Fresh[1] = false;
        w.MouseMove(b.Cx(4), b.Cy(5));
        Lit loading = LitFrame(b);
        w.Model.Fresh[1] = true;
        w.Top = w._target = 0f;
        check.Expect(title.None && title.Over == overAtRest && head.None && head.Over == overAtRest && loading.None && loading.Over == overAtRest,
                     $"a store helmet over the title bar, the head slot or a page still loading lights nothing (title {title.Cells} {title.Over}, head {head.Cells} {head.Over}, loading {loading.Cells} {loading.Over}; {overAtRest} at rest)");

        // Given a quickbar button dragged over the CX inventory, with no store item in the drag
        // When it is over a free place in the backpack grid
        // Then nothing is lit, since letting go there buys nothing
        drag = default;
        button = true;
        w.MouseMove(b.Cx(4), b.Cy(5));
        Lit quickbar = LitFrame(b);
        check.Expect(quickbar.None && quickbar.Over == overAtRest, $"a quickbar button over a free cell lights nothing ({quickbar.Cells}, {quickbar.Over})");

        drag = (StoreRing, 1, 1);
        w.MouseMove(b.Cx(4), b.Cy(5));
        Lit ring = LitFrame(b);
        check.Expect(ring.Green.Count == 1 && ring.Green.Contains((4, 5)) && ring.Red.Count == 0, $"a store ring over a free cell lights that one cell ({ring.Cells})");

        drag = (StoreHelm, 2, 2);
        w.MouseMove(w.Sx(w._tabs.X0) + 3f * w.S, b.Cy(5));
        Lit tabs = LitFrame(b);
        check.Expect(tabs.None, $"a store helmet over the page tabs, beside free cells, lights nothing ({tabs.Cells})");

        foreach ((uint, int, int) odd in new[] { (StoreTall, 1, InventoryModel.Rows + 1), (StoreTall, InventoryModel.Cols + 1, 1) })
        {
            drag = odd;
            w.MouseMove(b.Cx(4), b.Cy(5));
            Lit big = LitFrame(b);
            b.Acts.Clear();
            w.MouseButton(false, 1, b.Cx(4), b.Cy(5), 0);
            check.Expect(big.None && b.Acts is [{ Kind: InvActKind.Buy, X: AutoCell, Y: AutoCell }],
                         $"a {odd.Item2}x{odd.Item3} store item, bigger than a page, lights nothing and is bought for the server to place ({big.Cells}, {string.Join(" ", b.Acts)})");
        }
    }

    /// <summary>A footprint bigger than a page, which a hak's base item table can give: the window's own drag of it lights
    /// only cells of the grid, on the page it is dragged on, with the next page in view below.</summary>
    private static void FootprintEdge(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        w.Layout(1f, true, true, 4000, 4000);
        w.X = 0f; w.Y = 0f;
        w.Model.Items[0] = new InvItem { Oid = Sword, X = 0, Y = 0, W = InventoryModel.Cols + 1, H = InventoryModel.Rows + 1 };
        w.Model.Count = 1;
        w.Top = w._target = 1f;
        w.MouseButton(true, 1, b.Cx(0), b.Cy(0), 0);
        w.MouseMove(b.Cx(1), b.Cy(1));
        Lit lit = LitFrame(b);
        var cells = new List<(int X, int Y)>(lit.Green);
        cells.AddRange(lit.Red);
        check.Expect(cells.Count == InventoryModel.Cols * (InventoryModel.Rows - 1) && cells.TrueForAll(c => c.X < InventoryModel.Cols && c.Y < InventoryModel.Rows - 1),
                     $"an item wider than the grid and taller than a page lights the grid's columns on its own page only ({lit.Cells})");
    }
}
