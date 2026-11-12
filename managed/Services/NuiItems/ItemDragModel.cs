using System;

namespace CX.Services.NuiItems;

/// <summary>What a release or a cancel asks the service to do.</summary>
internal enum DragOutcomeKind
{
    /// <summary>Nothing to do: no gesture was under way.</summary>
    None,
    /// <summary>The press never moved: it stays the click the engine already took.</summary>
    Click,
    /// <summary>A drop the server is told about.</summary>
    Drop,
    /// <summary>The gesture was abandoned: tell the server nothing, put the image down.</summary>
    Cancel,
}

/// <summary>A release's or a cancel's verdict: what to do, and for a drop the two element ids it names.</summary>
internal readonly struct DragOutcome
{
    public readonly DragOutcomeKind Kind;
    public readonly string Pressed, Over;

    private DragOutcome(DragOutcomeKind kind, string pressed = "", string over = "")
    {
        Kind = kind; Pressed = pressed; Over = over;
    }

    public static readonly DragOutcome None = new(DragOutcomeKind.None);
    public static DragOutcome Click(string pressed) => new(DragOutcomeKind.Click, pressed);
    public static DragOutcome Drop(string pressed, string over) => new(DragOutcomeKind.Drop, pressed, over);
    public static DragOutcome Cancel(string pressed) => new(DragOutcomeKind.Cancel, pressed);
}

/// <summary>
/// The press, move, release and cancel rules of a drag, with no engine in them: a press on a declared element arms the
/// gesture, a pointer that travels <see cref="SlopPx"/> from where it went down turns it into a drag, and the release
/// decides. A press that never moved stays a click, and a release over nothing -- or an Escape -- cancels and tells the
/// server nothing, so a gesture the player abandons leaves the pack as it was.
/// </summary>
internal sealed class ItemDragModel
{
    /// <summary>How far the pointer travels, in unscaled px, before a press becomes a drag.</summary>
    internal const int DefaultSlopPx = 4;

    /// <summary>The travel this model treats as a drag; the caller scales it for the display.</summary>
    internal int SlopPx = DefaultSlopPx;

    /// <summary>The element the press went down on, empty when no gesture is under way.</summary>
    public string Pressed { get; private set; } = "";
    /// <summary>True once the pointer has travelled past the slop: the image is being carried.</summary>
    public bool Dragging { get; private set; }

    private int _x, _y;

    /// <summary>True while a press is held, moved or not.</summary>
    public bool Armed => Pressed.Length != 0;

    /// <summary>A press went down on <paramref name="id"/> at <paramref name="x"/>, <paramref name="y"/>. False when the id is
    /// empty, which is a press on no element and arms nothing.</summary>
    public bool Press(string id, int x, int y)
    {
        Pressed = id;
        Dragging = false;
        _x = x; _y = y;
        return Armed;
    }

    /// <summary>The pointer moved. True on the frame the gesture becomes a drag, so the caller shows the image once.</summary>
    public bool Motion(int x, int y)
    {
        if (!Armed || Dragging) return false;
        if (Math.Abs(x - _x) < SlopPx && Math.Abs(y - _y) < SlopPx) return false;
        Dragging = true;
        return true;
    }

    /// <summary>The button came up over the element <paramref name="overId"/>, empty when over no NUI element.</summary>
    public DragOutcome Release(string overId)
    {
        if (!Armed) return DragOutcome.None;
        string pressed = Pressed;
        bool dragged = Dragging;
        Forget();
        if (!dragged) return DragOutcome.Click(pressed);
        return overId.Length == 0 ? DragOutcome.Cancel(pressed) : DragOutcome.Drop(pressed, overId);
    }

    /// <summary>Escape, or anything else that abandons the gesture. The release that follows finds nothing armed.</summary>
    public DragOutcome Cancel()
    {
        if (!Armed) return DragOutcome.None;
        string pressed = Pressed;
        Forget();
        return DragOutcome.Cancel(pressed);
    }

    private void Forget()
    {
        Pressed = "";
        Dragging = false;
    }
}
