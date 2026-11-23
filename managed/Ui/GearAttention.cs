using CX.Framework;

namespace CX.Ui;

/// <summary>What the main-menu gear's badge asks the player to look at: a list of reasons, each with its tooltip and
/// the settings group a press on the gear opens.</summary>
public sealed class GearAttention
{
    /// <summary>The tooltip of the first reason, or null when the gear has nothing to say.</summary>
    public string? Tip => null;

    /// <summary>The group the first reason points the gear at, or null.</summary>
    public KnobGroup? Target => null;

    /// <summary>Whether the badge draws: at the main menu, while a reason stands.</summary>
    public bool BadgeShown(bool atMainMenu) => false;
}
