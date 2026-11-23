using CX.Framework;
using CX.Ui;
using static CX.Services.Update.UpdateNoticeApprovedExamples;

namespace CX.Services.Update;

/// <summary>The notice where no approved example reaches: no network, and the version order. Oracle: version pairs
/// ordered by hand here, and a source that answers nothing.</summary>
internal static class UpdateNoticeSelfCheck
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UpdateNotice.SelfCheck");
        var lines = new List<string>();
        var k = Knobs("cx-upd-offline");
        k.Consent.Set(UpdateKnobs.Yes);
        var gear = new GearAttention();
        int asked = 0;
        new UpdateNotice(k, Running, Keys(), new ExtenderLog { Tap = lines.Add }, _ => { asked++; return null; }, _ => true, gear).Check();
        check.Expect(asked > 0 && !gear.BadgeShown(true) && lines.Count == 0, $"offline: asked, no badge, nothing logged ({asked}, {string.Join(" | ", lines)})");

        check.Expect(!UpdateNotice.Newer("v0.14.0", "v0.14.0"), "the same version is not newer");
        check.Expect(UpdateNotice.Newer("v0.14.0", "v0.13.11-3-g27e6276a"), "a minor step past a describe string is newer");
        check.Expect(!UpdateNotice.Newer("v0.9.0", "v0.13.2") && UpdateNotice.Newer("v0.13.10", "v0.13.9"), "parts compare as numbers, not text");
        check.Expect(UpdateNotice.Newer("v1.0.0", "v0.99.99") && !UpdateNotice.Newer("v0.99.99", "v1.0.0"), "the major part comes first");
        check.Expect(!UpdateNotice.Newer("v9.9.9", "unknown"), "a running build with no version is told nothing");

        log.Info(check.Ok ? "UpdateNotice.SelfCheck: passed" : "UpdateNotice.SelfCheck: FAILED");
        return check.Ok;
    }
}
