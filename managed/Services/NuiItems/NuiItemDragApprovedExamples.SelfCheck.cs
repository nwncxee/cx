using CX.Framework;

namespace CX.Services.NuiItems;

/// <summary>
/// Dragging an item between the server's NUI elements (approved example DD6). Oracle: the capability name and version and the
/// policy prefix, written out by hand here from the settled wire form (a drop is one
/// <c>drop;&lt;pressed id&gt;;&lt;id under the pointer&gt;</c> event under <c>cx_cap_drag</c> 1, draggable elements declared with
/// <c>drag.&lt;prefix&gt;*</c> lines), never read back from the code under test.
/// </summary>
internal static class NuiItemDragApprovedExamples
{
    /// <summary>The capability a client that can drag advertises, and the version the contract gives it.</summary>
    private const string DragCapability = "cx_cap_drag";
    private const int DragVersion = 1;

    /// <summary>The policy prefix a server declares its draggable elements under.</summary>
    private const string DragPrefix = "drag.";

    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "NuiItemDragApprovedExamples.SelfCheck");
        Capability(check);
        Declaration(check);
        log.Info(check.Ok ? "NuiItemDragApprovedExamples.SelfCheck: passed" : "NuiItemDragApprovedExamples.SelfCheck: FAILED");
    }

    // Given a dagger in the first cell of my pack and the last cell of the page empty
    // When I press on the dagger, move the pointer to the empty cell and release
    // Then the dagger sits in that cell, as if I had clicked it and then clicked the cell
    //
    // A server sends the drag policy only to a client that said it can drag, so the login set carries the capability
    // at version 1. Oracle: the name and the version typed above.
    private static void Capability(Checker check)
    {
        var sent = Capabilities.Collect("0.7.0");
        bool drag = false;
        foreach (var (name, value) in sent)
            if (name == DragCapability && value == DragVersion) drag = true;
        check.Expect(drag, $"DD6 the login set advertises {DragCapability}={DragVersion} ({sent.Count} propert(ies) sent, none of them that)");
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
        int applied = ServerPolicy.Apply("drag.cell*=1\ndrag.slot_1=1");
        check.Expect(applied == 2, $"DD6 both drag lines are applied ({applied} of 2)");
        check.Expect(ServerPolicy.Count == 0, $"DD6 the {DragPrefix} lines are parsed by the drag feature, not held as loose properties ({ServerPolicy.Count} held)");
        check.Expect(!ServerPolicy.TryGet("drag.cell*", out _), "DD6 a declared drag prefix is no component property");
        ServerPolicy.Clear();
    }
}
