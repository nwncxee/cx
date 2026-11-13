using CX.Framework;

namespace CX.Services.NuiItems;

/// <summary>
/// The press, move, release and cancel rules of a drag, and the elements a server declares draggable. Oracle: ids, points
/// and declarations typed in by hand, and the 4 px slop written out as literals either side of it.
/// </summary>
internal static class ItemDragSelfCheck
{
    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "ItemDragSelfCheck");
        Declarations(check);
        Gesture(check);
        Cancels(check);
        Slop(check);
        log.Info(check.Ok ? "ItemDragSelfCheck: passed" : "ItemDragSelfCheck: FAILED");
    }

    /// <summary>Oracle: the keys and ids typed here; an id key is exact, a <c>*</c> key covers what starts with it.</summary>
    private static void Declarations(Checker check)
    {
        ItemDragPolicy.Reset();
        ServerPolicy.Clear();
        ServerPolicy.Apply("drag.cell*=1\ndrag.slot_1=1");
        check.Expect(ItemDragPolicy.Count == 2 && ItemDragPolicy.Draggable("slot_1") && ItemDragPolicy.Draggable("CELL_9"),
            "an id key and a prefix key both answer, without regard to case");
        check.Expect(!ItemDragPolicy.Draggable("slot_12") && !ItemDragPolicy.Draggable("other") && !ItemDragPolicy.Draggable(""),
            "an id key is not a prefix, and an undeclared element does not drag");
        ServerPolicy.Apply("drag.cell*=");
        check.Expect(!ItemDragPolicy.Draggable("cell_9") && ItemDragPolicy.Draggable("slot_1"), "releasing one key leaves the other");
        ServerPolicy.Apply("drag.*=");
        check.Expect(ItemDragPolicy.Count == 0, "drag.*= releases every declaration");
        ItemDragPolicy.Reset();
        ServerPolicy.Clear();
    }

    /// <summary>Oracle: the ids and the two points typed here, 40 px apart, well past the slop.</summary>
    private static void Gesture(Checker check)
    {
        var m = new ItemDragModel();
        check.Expect(m.Press("cell_1", 100, 100) && m.Armed && !m.Dragging, "a press on a declared element arms the gesture, carrying nothing yet");
        check.Expect(m.Motion(140, 100) && m.Dragging, "the pointer past the slop starts carrying the item");
        check.Expect(!m.Motion(150, 100), "once carrying, further motion starts nothing a second time");
        DragOutcome drop = m.Release("cell_9");
        check.Expect(drop.Kind == DragOutcomeKind.Drop && drop.Pressed == "cell_1" && drop.Over == "cell_9", "a release over another element is a drop naming both");
        check.Expect(!m.Armed && m.Release("cell_9").Kind == DragOutcomeKind.None, "the gesture is over: a second release does nothing");

        m.Press("cell_1", 100, 100);
        DragOutcome click = m.Release("cell_1");
        check.Expect(click.Kind == DragOutcomeKind.Click && click.Pressed == "cell_1", "a press and release in place is the click the engine already took");
        check.Expect(!m.Press("", 100, 100) && !m.Armed, "a press on no element arms nothing");
        check.Expect(!m.Motion(400, 400), "with nothing armed, motion starts nothing");
    }

    /// <summary>Oracle: the ids typed here; a cancel names the element it abandoned and no second one.</summary>
    private static void Cancels(Checker check)
    {
        var m = new ItemDragModel();
        m.Press("cell_1", 100, 100);
        m.Motion(140, 100);
        DragOutcome off = m.Release("");
        check.Expect(off.Kind == DragOutcomeKind.Cancel && off.Pressed == "cell_1" && off.Over.Length == 0, "a release over no element cancels and names no target");

        m.Press("cell_1", 100, 100);
        m.Motion(140, 100);
        check.Expect(m.Cancel().Kind == DragOutcomeKind.Cancel && !m.Armed, "Escape cancels what is being carried");
        check.Expect(m.Release("cell_9").Kind == DragOutcomeKind.None, "the release after an Escape moves nothing");
        check.Expect(m.Cancel().Kind == DragOutcomeKind.None, "a cancel with nothing armed is nothing");
    }

    /// <summary>Oracle: 4 px, the model's slop, with 3 px and 4 px either side of it as literals.</summary>
    private static void Slop(Checker check)
    {
        var m = new ItemDragModel();
        m.Press("cell_1", 100, 100);
        check.Expect(!m.Motion(103, 103) && !m.Dragging, "3 px of travel is still a click waiting to happen");
        check.Expect(m.Motion(100, 104) && m.Dragging, "4 px of travel, on either axis, is a drag");
        var wide = new ItemDragModel { SlopPx = 20 };
        wide.Press("cell_1", 100, 100);
        check.Expect(!wide.Motion(110, 110) && wide.Motion(100, 125), "a model told to use a wider slop holds the press until the pointer passes it");
    }
}
