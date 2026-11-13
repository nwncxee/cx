using CX.Framework;
using CX.Services.Nui;

namespace CX.Services.NuiItems;

/// <summary>
/// Dragging an item between the server's NUI elements (approved examples DD6 and DD12). Oracle: the policy prefix, the knob key
/// and the gates typed out by hand here from the settled wire form (a drop is one
/// <c>drop;&lt;pressed id&gt;;&lt;id under the pointer&gt;</c> event under <c>cx_cap_drag</c> 1, draggable elements declared with
/// <c>drag.&lt;prefix&gt;*</c> lines), never read back from the code under test.
/// </summary>
internal static class NuiItemDragApprovedExamples
{
    /// <summary>The policy prefix a server declares its draggable elements under.</summary>
    private const string DragPrefix = "drag.";

    /// <summary>The player's own switch for the drag, in the settings tree.</summary>
    private const string DragKnob = "nui.drag";

    public static void Run(ExtenderLog log) => Passes(log);

    /// <summary>Runs the suite and logs its result; true when it passed.</summary>
    internal static bool Passes(ExtenderLog log)
    {
        var check = new Checker(log, "NuiItemDragApprovedExamples.SelfCheck");
        Capability(check);
        Declaration(check);
        Gesture(check);
        Knob(check);
        log.Info(check.Ok ? "NuiItemDragApprovedExamples.SelfCheck: passed" : "NuiItemDragApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given a dagger in the first cell of my pack and the last cell of the page empty
    // When I press on the dagger, move the pointer to the empty cell and release
    // Then the dagger sits in that cell, as if I had clicked it and then clicked the cell
    //
    // A server sends the drag policy only to a client that said it can drag, and a client says so only while its NUI
    // work and its drag knob are on and the element under the pointer is knowable. Oracle: the three gates, set here
    // by hand, with the hit test answered either way.
    private static void Capability(Checker check)
    {
        bool knobWas = NuiKnobs.Drag.V, nuiWas = NuiKnobs.Enabled.V;
        NuiKnobs.Enabled.Set(true);
        NuiKnobs.Drag.Set(true);
        check.Expect(ItemDragService.Live(armed: true), "DD6 with the NUI work on, the knob on and the hit test armed, the client advertises the drag");
        check.Expect(!ItemDragService.Live(armed: false), "DD6 with the hit test stood down it advertises nothing: a drop could not name where it landed");
        NuiKnobs.Enabled.Set(false);
        check.Expect(!ItemDragService.Live(armed: true), "DD6 the NUI master off withdraws it too");
        NuiKnobs.Enabled.Set(nuiWas);
        NuiKnobs.Drag.Set(knobWas);
    }

    // Given a dagger in the first cell of my pack and the last cell of the page empty
    // When I press on the dagger, move the pointer to the empty cell and release
    // Then the dagger sits in that cell, as if I had clicked it and then clicked the cell
    //
    // A drop can only name the cell it was released over when the server declared that cell draggable, so the drag
    // feature owns its prefix the way held.pick. is owned: it parses those lines itself and leaves none of them in the
    // session's loose properties. Oracle: the two lines below, typed by hand, and the count they must leave, 0.
    private static void Declaration(Checker check)
    {
        ServerPolicy.Clear();
        ItemDragPolicy.Reset();
        int applied = ServerPolicy.Apply("drag.cell*=1\ndrag.slot_1=1");
        check.Expect(applied == 2, $"DD6 both drag lines are applied ({applied} of 2)");
        check.Expect(ServerPolicy.Count == 0, $"DD6 the {DragPrefix} lines are parsed by the drag feature, not held as loose properties ({ServerPolicy.Count} held)");
        check.Expect(!ServerPolicy.TryGet("drag.cell*", out _), "DD6 a declared drag prefix is no component property");
        check.Expect(ItemDragPolicy.Draggable("cell_9") && ItemDragPolicy.Draggable("slot_1"), "DD6 the declared cells are the ones that drag");
        ServerPolicy.Clear();
        ItemDragPolicy.Reset();
    }

    // Given a dagger in the first cell of my pack and the last cell of the page empty
    // When I press on the dagger, move the pointer to the empty cell and release
    // Then the dagger sits in that cell, as if I had clicked it and then clicked the cell
    //
    // The gesture itself, and the line the server hears: the pressed cell, then the cell under the pointer, so the
    // server can run the pick and the place its click handler already makes. Oracle: the two ids and the wire text,
    // typed out by hand, with the pointer moved 40 px.
    private static void Gesture(Checker check)
    {
        var m = new ItemDragModel();
        m.Press("cell_1", 100, 100);
        m.Motion(140, 100);
        DragOutcome drop = m.Release("cell_9");
        check.Expect(drop.Kind == DragOutcomeKind.Drop && drop.Pressed == "cell_1" && drop.Over == "cell_9", "DD6 the release over the empty cell is a drop naming the pressed cell and that one");

        var sent = new System.Collections.Generic.List<string>();
        var was = ServerPolicy.Send;
        ServerPolicy.Send = (minor, ascii) => sent.Add($"{minor:x2}:{ascii}");
        ServerPolicy.Emit(ItemDragService.DropAction, drop.Pressed, drop.Over);
        ServerPolicy.Send = was;
        check.Expect(sent.Count == 1 && sent[0] == "48:drop;cell_1;cell_9", $"DD6 the server hears drop;cell_1;cell_9 ({(sent.Count == 0 ? "nothing sent" : sent[0])})");
    }

    // Given the extender with its NUI drag knob turned off
    // When I press on a dagger in my pack, move and release on an empty cell
    // Then the dagger is picked, as a click picks it, and has not moved
    //
    // The switch the capability stands behind: off, the server is never told this client drags, so it keeps sending
    // click to pick, click to place. Oracle: the key typed above, and the knob set by hand either way.
    private static void Knob(Checker check)
    {
        check.Expect(KnobGroup.Find(DragKnob) != null, $"DD12 the settings tree declares {DragKnob}, the switch the capability stands behind");
        bool was = NuiKnobs.Drag.V, nuiWas = NuiKnobs.Enabled.V;
        NuiKnobs.Enabled.Set(true);
        NuiKnobs.Drag.Set(false);
        check.Expect(!ItemDragService.Live(armed: true), "DD12 the drag knob off withdraws the capability, so a press stays a click");
        NuiKnobs.Drag.Set(true);
        check.Expect(ItemDragService.Live(armed: true), "DD12 ...and turning it back on offers the drag again");
        NuiKnobs.Drag.Set(was);
        NuiKnobs.Enabled.Set(nuiWas);
    }
}
