using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The edges of the wave field's two <c>solid_noise</c> candidates, beside the approved four: a name the engine
/// holds is not a mirror, so the standing turns on whether each one's mirror is up rather than on whether the
/// resman has a name at all. Oracle: the four up/down standings of two names that both exist.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Both names present and each mirror's standing varied: the engine's wins only while its own
    /// mirror is up, and two names with no mirror between them are no field at all.</summary>
    internal static void VkWaveNoiseEdgesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.VkWaveNoiseEdgesSelfCheck");
        int engine = 17, own = 34;   // GL names, distinct so a check can say which one answered

        check.Expect(FrameSnapshot.WhichNoise(engine, false, own, true) == own,
            "an engine name whose mirror is not up loses to CX's own copy, which has one");
        check.Expect(FrameSnapshot.WhichNoise(engine, true, own, false) == engine,
            "...and CX's own name without a mirror does not displace the engine's, which has one");
        check.Expect(FrameSnapshot.WhichNoise(engine, false, own, false) == 0,
            "two names and no mirror behind either is no wave field, whatever the resman holds");
        check.Expect(FrameSnapshot.WhichNoise(engine, true, own, true) != own,
            "the engine's is never passed over while its mirror is up");
        log.Info(check.Ok ? "AlloyRenderer.VkWaveNoiseEdgesSelfCheck: passed"
                          : "AlloyRenderer.VkWaveNoiseEdgesSelfCheck: FAILED");
    }
}
