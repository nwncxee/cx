using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// What is left of the per-area skybox pass: the player's switch and the period line's counters. The sky the
/// Alloy frame draws comes from the scene database (<c>AlloyRenderer.DbSky</c>), and <c>SkyStage</c> compiles
/// the custom fragment stage a sky material names.
/// </summary>
internal static class SkyBox
{
    private static int _hosted;

    /// <summary>The player's own switch (<c>effects.sky</c>), live rather than a relaunch.</summary>
    internal static bool Disabled => !Effects.Sky.V;

    /// <summary>Zeroed with the rest of the period report's counters: a cumulative one would keep printing
    /// a healthy number long after the sky stopped being drawn, which is the opposite of the question.</summary>
    internal static void ResetPeriod() { _hosted = 0; SkyTwin.ResetPeriod(); }

    /// <summary>One part's sky program was bound this period.</summary>
    internal static void NoteHosted() => _hosted++;

    internal static string Stats() => Disabled ? "sky off (/effects sky 0)"
                                              : $"sky {_hosted} hosted draw(s) this period; programs: {SkyStage.Stats()}";
}
