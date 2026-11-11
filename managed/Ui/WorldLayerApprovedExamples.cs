using System;
using CX.Framework;

namespace CX.Ui;

/// <summary>What the game's own panels cover (approved-spec, E1, E3, E4, E5). Oracle: the letters of
/// <see cref="GuiFrameSelfCheck.Letters"/>, written by hand there, in the order each example states here.</summary>
internal static class WorldLayerApprovedExamples
{
    private static bool Under(string frame, char below, char above)
    {
        int b = frame.IndexOf(below, StringComparison.Ordinal), a = frame.IndexOf(above, StringComparison.Ordinal);
        return b >= 0 && a >= 0 && b < a;
    }

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "WorldLayerApprovedExamples.SelfCheck");
        string f = GuiFrameSelfCheck.Letters();

        // Given the Examine window is open over a creature on screen
        // When that creature's nameplate shows
        // Then the Examine window covers the nameplate
        check.Expect(Under(f, 'p', 'E'), $"E1 the Examine window draws after the plates' window: {f}");

        // Given the Examine window is open over a creature on screen
        // When that creature speaks and its chat bubble shows
        // Then the Examine window covers the chat bubble
        check.Expect(Under(f, 'p', 'E'), $"E3 a chat bubble draws in the plates' window, under the Examine window: {f}");

        // Given a creature stands behind the game's own quickbar on screen
        // When that creature's nameplate shows
        // Then the quickbar covers the nameplate
        check.Expect(Under(f, 'p', 'Q'), $"E4 the game's quickbar draws after the plates' window: {f}");

        // Given the Examine window is open over a creature on screen
        // When that creature takes damage and the damage number shows
        // Then the Examine window covers the damage number
        check.Expect(Under(f, 'd', 'E'), $"E5 the Examine window draws after the damage numbers' window: {f}");

        if (check.Ok) log.Info("WorldLayerApprovedExamples.SelfCheck: passed");
    }
}
