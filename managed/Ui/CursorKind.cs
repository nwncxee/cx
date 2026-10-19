namespace CX.Ui;

/// <summary>The pointer shape a hit-region requests. <see cref="None"/> means no UI window is under the
/// pointer, so the arbiter defers and the engine's own cursor stands.</summary>
public enum CursorKind
{
    None,
    Default,
    Move,
    Resize,
    Link,
}
