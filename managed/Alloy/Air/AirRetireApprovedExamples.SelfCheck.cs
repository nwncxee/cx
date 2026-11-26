using System;
using CX.Framework;
using static CX.Alloy.AirFixture;

namespace CX.Alloy;

/// <summary>The Ground mist switch retires into the air's: its key leaves the registry and the file, and its rows leave
/// the Volumetric fog panel. Oracle, by hand: the retired key's spelling and the words a player would read on a row.</summary>
internal static class AirRetireApprovedExamples
{
    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirRetireApprovedExamples.SelfCheck");
        RetiredMistKeyGone(check);
        NoGroundMistRow(check);
        log.Info(check.Ok ? "AirRetireApprovedExamples.SelfCheck: passed" : "AirRetireApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given a cx.cfg holding the retired key vfog.mist
    // When the client starts
    // Then /knobs no longer lists it, and cx.cfg no longer holds it after the next save.
    private static void RetiredMistKeyGone(Checker check)
    {
        string? was = Settings.Raw(RetiredMist);
        Settings.Read($"{RetiredMist}=0\n");
        string composed = KnobFile.Compose();
        Settings.Forget(RetiredMist);
        if (was != null) Settings.Read($"{RetiredMist}={was}\n");
        check.Expect(VolumetricFog.G.Knobs.Count > 0 && KnobGroup.Find(RetiredMist) == null, "E86 /knobs, with Volumetric fog's rows declared, no longer lists vfog.mist");
        check.Expect(!composed.Contains(RetiredMist + "=", StringComparison.OrdinalIgnoreCase), "E86 the next save writes no vfog.mist line");
    }

    // Given the settings panel
    // When the player reads Volumetric fog's rows
    // Then no row or help text names Ground mist.
    private static void NoGroundMistRow(Checker check)
    {
        foreach (Knob k in VolumetricFog.G.Knobs)
            check.Expect(!k.Label.Contains("ground mist", StringComparison.OrdinalIgnoreCase) && !k.Help.Contains("ground mist", StringComparison.OrdinalIgnoreCase),
                $"E121 the Volumetric fog row {k.Key} ({k.Label}) names no Ground mist in its label or help");
    }
}
