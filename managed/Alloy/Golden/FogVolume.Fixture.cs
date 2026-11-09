using System.Numerics;

namespace CX.Alloy;

/// <summary>The FSR sky golden's fixture: a volume marked built for the main view, so the resolve reads one.</summary>
internal static partial class FogVolume
{
    /// <summary>Mark this frame's whole-fog volume built over <paramref name="d"/> slices, for an identity view-projection;
    /// <see cref="NotBuilt"/> takes it down.</summary>
    internal static void FixtureBuilt(int d)
    {
        _b.Built = true; _b.Veil = false; _b.D = d; _b.Vp = Matrix4x4.Identity; _b.InvLog = 1f;
    }
}
