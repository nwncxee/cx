using System;
using System.Text;
using CX.Framework;
using CX.Services.Nui;

namespace CX.Services.NuiItems;

/// <summary>
/// Dragging an item between the elements of a server's NUI windows. The rules live in <see cref="ItemDragModel"/>; this
/// reads the element under the pointer from the NUI hit test, carries the image through the held-image primitive, and
/// hands a finished drop to the server as <c>drop;&lt;pressed id&gt;;&lt;id under the pointer&gt;</c>.
/// </summary>
internal static class ItemDragService
{
    /// <summary>The <see cref="ServerPolicy.EventMinor"/> action a finished drop is sent as.</summary>
    internal const string DropAction = "drop";

    /// <summary>The gesture under way, and the log that follows it.</summary>
    internal sealed class DragState
    {
        public readonly ItemDragModel Model = new();
        public ExtenderLog? Log;
    }

    private static readonly DragState _s = new();
    private static ItemDragModel Model => _s.Model;
    private static ExtenderLog? _log => _s.Log;

    /// <summary>
    /// Would a client whose hit test is <paramref name="armed"/> advertise the drag? The player's NUI work and the drag knob
    /// both have to be on, and the element under the pointer has to be knowable: without the hit test a drop could not name
    /// where it landed, so the server is better told nothing and left to its click-to-pick, click-to-place.
    /// </summary>
    internal static bool Live(bool armed) => armed && NuiKnobs.Enabled.V && NuiKnobs.Drag.V;

    /// <summary>True while the player, the server and the hooks all allow a drag.</summary>
    private static bool On => Live(NuiHitTest.Armed) && ItemDragPolicy.Count != 0;

    public static void Init(ExtenderLog log)
    {
        _s.Log = log;
        ItemDragPolicy.Init();
        Capabilities.Register("drag", 1, static () => Live(NuiHitTest.Armed));
        NuiKnobs.Enabled.Changed += Capabilities.Refresh;
        NuiKnobs.Drag.Changed += Capabilities.Refresh;
    }

    /// <summary>A left press landed on NUI: arm the gesture when the element under the pointer was declared draggable.</summary>
    internal static void OnPress(int x, int y)
    {
        if (!On) return;
        string id = ElementUnderPointer();
        if (id.Length != 0 && ItemDragPolicy.Draggable(id)) Model.Press(id, x, y);
    }

    /// <summary>The pointer moved while a press is held.</summary>
    internal static void OnMotion(int x, int y)
    {
        if (Model.Armed && Model.Motion(x, y)) _log?.Info($"[drag] carrying {Model.Pressed}");
    }

    /// <summary>The left button came up. A drag that travelled is a drop; a press that stayed put is the click the engine took.</summary>
    internal static void OnRelease() => Act(Model.Release(ElementUnderPointer()));

    /// <summary>Escape, or anything else that abandons the gesture.</summary>
    internal static void OnCancel() => Act(Model.Cancel());

    /// <summary>True while an item is being carried, so a caller can leave the gesture alone.</summary>
    internal static bool Dragging => Model.Dragging;

    private static void Act(DragOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case DragOutcomeKind.Drop:
                _log?.Info($"[drag] drop {outcome.Pressed} -> {outcome.Over}");
                ServerPolicy.Emit(DropAction, outcome.Pressed, outcome.Over);
                break;
            case DragOutcomeKind.Cancel:
                _log?.Info($"[drag] cancelled on {outcome.Pressed}");
                HeldImageService.CancelPick();
                break;
        }
    }

    /// <summary>The id of the element the pointer was last published over, empty when over none. Allocation-free until the id is one.</summary>
    private static string ElementUnderPointer()
    {
        if (!NuiHitTest.TryGet(out NuiHit hit)) return "";
        Span<char> id = stackalloc char[NuiHit.TokenCap];
        int n = Encoding.UTF8.GetChars(hit.Token(), id);
        return n == 0 ? "" : new string(id[..n]);
    }
}
