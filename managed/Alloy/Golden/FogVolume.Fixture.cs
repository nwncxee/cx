using System.Numerics;

namespace CX.Alloy;

/// <summary>The FSR sky golden's fixture: a volume marked built for the main view, so the resolve reads one.</summary>
internal static partial class FogVolume
{
    /// <summary>Mark this frame's whole-fog volume built (<paramref name="on"/>) over <paramref name="d"/> slices for
    /// view-projection <paramref name="vp"/>, or not built.</summary>
    internal static void FixtureBuilt(bool on, int d, Matrix4x4 vp)
    {
        _b.Built = on; _b.Veil = false; _b.D = d; _b.Vp = vp; _b.InvLog = 1f;
    }
}
