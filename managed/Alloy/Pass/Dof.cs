using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>dof</c> group: depth of field's settings. They mimic the game's own, with its defaults and ranges, and
/// the game's own are not read.</summary>
internal static class Dof
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, NeverBlur;
    internal static readonly Knob<float> Amount, DeadZone;
    internal static readonly Knob<int> Focus;

    /// <summary><see cref="Focus"/>'s values, the stock shader's <c>DOF_FOCUS_TYPE_*</c>.</summary>
    internal const int FocusPlayer = 0, FocusPitch = 1, FocusDynamic = 2, FocusMouse = 3;

    static Dof()
    {
        G = KnobGroup.Declare("dof", "Graphics", "Image", master: "Depth of field",
                              masterHelp: "Blurs what is nearer or farther than the focus, keeping the interface sharp.",
                              masterDefault: false, order: 332, title: "Depth of field");
        G.PanelStatus = DepthOfField.PanelLine;
        Enabled = G.Master!;
        Amount = G.Slider("amount", "Amount", 0f, 5f, 0.05f, 1f,
                          help: "How quickly the blur grows with distance from the focus.");
        DeadZone = G.Slider("deadzone", "Dead zone", 0f, 10f, 0.1f, 3f, " m",
                            help: "How deep the sharp band around the focus is.");
        Focus = G.Dropdown("focus", "Focus type", new[] { FocusPlayer, FocusPitch, FocusDynamic, FocusMouse }, FocusPitch,
                           new[] { "Player Character", "Camera Pitch", "Dynamic", "Mouse" },
                           help: "What the camera focuses on. Camera Pitch looks farther away the lower the camera sits. "
                               + "Dynamic reads the distance ahead of the camera. Mouse focuses on what is under the mouse.");
        NeverBlur = G.Toggle("neverblur", "Never blur the player", true,
                             help: "Keeps your character inside the sharp band, wherever the focus is.");
    }
}
