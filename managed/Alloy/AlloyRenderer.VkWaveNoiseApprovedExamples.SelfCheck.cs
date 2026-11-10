using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The Vulkan wave field's approved examples (E1 to E4): which of the two <c>solid_noise</c> mirrors the water
/// class draws with, and when there is none and it sits out. Oracle: the four standings of the two mirrors,
/// written out by hand with the name the field is owed beside each.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Approved-spec: the engine's mirror answers where it is up, CX's own copy answers the frames before
    /// it arrives, and only both missing leaves the class without a field.</summary>
    internal static void VkWaveNoiseSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.VkWaveNoiseSelfCheck");
        int engine = 17, own = 34;   // GL names, distinct so an example can say which one answered
        int none = 0;

        // Given the engine's solid_noise mirrored on Vulkan, and CX's own copy resident too
        // When the water class resolves its wave field
        // Then it takes the engine's mirror
        check.Expect(FrameSnapshot.WhichNoise(engine, true, own, true) == engine,
            "E1: with both mirrors up the field takes the engine's own noise");

        // Given the engine's mirrored and CX's own not resident
        check.Expect(FrameSnapshot.WhichNoise(engine, true, none, false) == engine,
            "E2: the engine's mirror answers with no copy behind it");

        // Given the engine's solid_noise not yet mirrored and CX's own copy resident
        // Then it takes CX's own copy, and the class draws rather than sitting out
        check.Expect(FrameSnapshot.WhichNoise(none, false, own, true) == own,
            "E3: before the engine's noise arrives the field takes CX's own copy of the resref");

        // Given neither mirrored
        // Then there is no wave field, and the class sits out
        check.Expect(FrameSnapshot.WhichNoise(none, false, none, false) == none,
            "E4: with neither mirror up there is no wave field and the class sits out");
        log.Info(check.Ok ? "AlloyRenderer.VkWaveNoiseSelfCheck: passed" : "AlloyRenderer.VkWaveNoiseSelfCheck: FAILED");
    }
}
