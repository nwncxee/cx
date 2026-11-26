using CX.Framework;

namespace CX.Services.Inventory;

/// <summary>CX-722 F3's examples that already hold, as plain regression tests: a store drag lights nothing where the
/// release leaves the cell to the server, and a drag that buys nothing lights nothing. They held before the grid lit a
/// store drag at all (the window lit only its own drags), so they guard the change that lights it. Oracle: the approved
/// examples, read off the frame (<see cref="LitFrame"/>), and the cells a release buys into (<see cref="Buy"/>).</summary>
internal sealed partial class InventoryWindow
{
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
    }
}
