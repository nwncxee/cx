using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>dof</c> group: depth of field's settings.</summary>
internal static class Dof
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, NeverBlur;
    internal static readonly Knob<float> Amount, DeadZone;
    internal static readonly Knob<int> Focus;

    static Dof()
    {
        G = KnobGroup.Declare("dof", "Graphics", "Image", master: "Depth of field", masterDefault: true, order: 332, title: "Depth of field");
        Enabled = G.Master!;
        Amount = G.Slider("amount", "Amount", 0f, 1f, 0.05f, 0f);
        DeadZone = G.Slider("deadzone", "Dead zone", 0f, 1f, 0.1f, 0f);
        Focus = G.Dropdown("focus", "Focus type", new[] { 0, 1 }, 0, new[] { "Player Character", "Camera Pitch" });
        NeverBlur = G.Toggle("neverblur", "Never blur the player", false);
    }
}
