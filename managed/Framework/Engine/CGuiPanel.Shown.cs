namespace CX.Engine;

/// <summary>A panel's state word, read as the engine reads it.</summary>
public readonly unsafe partial struct CGuiPanel
{
    /// <summary>On screen: the state word is exactly 1 (active, not hidden), the engine's own test in <c>Hide</c>,
    /// <c>UnHide</c>, <c>Activate</c> and the inventory key's <c>ToggleDisplay</c>.</summary>
    public bool Shown
    {
        get
        {
#pragma warning disable RS0030 // Shown is the one reader of the raw state word
            return State == 1;
#pragma warning restore RS0030
        }
    }
}
