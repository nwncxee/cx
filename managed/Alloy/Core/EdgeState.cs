namespace CX.Alloy;

/// <summary>Last frame's view of the two session edges a lifecycle tick compares against: in a module, and the
/// render device available. A tick acts on the change, not on the level.</summary>
internal sealed class EdgeState
{
    public bool InModule, Available;
}
