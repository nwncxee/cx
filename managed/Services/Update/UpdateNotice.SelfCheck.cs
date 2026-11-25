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
        new UpdateNotice(k, Running, "linux", Keys(), new ExtenderLog { Tap = lines.Add }, _ => { asked++; return null; }, _ => true, gear).Check();
        check.Expect(asked > 0 && !gear.BadgeShown(true) && lines.Count == 0, $"offline: asked, no badge, nothing logged ({asked}, {string.Join(" | ", lines)})");

        check.Expect(!UpdateNotice.Newer("v0.14.0", "v0.14.0"), "the same version is not newer");
        check.Expect(UpdateNotice.Newer("v0.14.0", "v0.13.11-3-g27e6276a"), "a minor step past a describe string is newer");
        check.Expect(!UpdateNotice.Newer("v0.9.0", "v0.13.2") && UpdateNotice.Newer("v0.13.10", "v0.13.9"), "parts compare as numbers, not text");
        check.Expect(UpdateNotice.Newer("v1.0.0", "v0.99.99") && !UpdateNotice.Newer("v0.99.99", "v1.0.0"), "the major part comes first");
        check.Expect(!UpdateNotice.Newer("v9.9.9", "unknown"), "a running build with no version is told nothing");

        const string Good = "caf90169eefa5f807d577486b9f795ab86ae2983c5c20806cff959117e90af18";
        foreach (string bad in new[] { Good[1..], Good.ToUpperInvariant(), "g" + Good[1..] })
        {
            UpdateManifest? m = UpdateManifest.Parse($"cx-update 1\nversion v0.14.0\nsha256 {bad} cx-linux-x64-v0.14.0.zip\nsha256 {Good} cx-windows-x64-v0.14.0.zip\n");
            check.Expect(m != null && !m.Sums.ContainsKey("cx-linux-x64-v0.14.0.zip") && m.Sums["cx-windows-x64-v0.14.0.zip"] == Good,
                         $"a sha256 line that is not 64 lowercase hex digits names no checksum, and the notice still reads ({bad})");
        }

        check.Expect(UpdateNotice.BuildLine("v0.14.0", "v0.14.0") == "NWNCX:EE build v0.14.0" && UpdateNotice.BuildLine("v0.14.0", null) == "NWNCX:EE build v0.14.0",
                     "the first line names no release the running build has caught up with");
        check.Expect(UpdateNotice.CrashLine("v0.13.2", "v0.14.0") == $"A newer release, v0.14.0, is out: {Product.Url}\n"
                     && UpdateNotice.CrashLine("v0.14.0", "v0.14.0") == "" && UpdateNotice.CrashLine("v0.14.0", null) == "",
                     $"the crash report names a newer release, and only a newer one ({UpdateNotice.CrashLine("v0.13.2", "v0.14.0")})");

        check.Expect(UpdateService.OsName(true) == "windows" && UpdateService.OsName(false) == "linux",
                     $"a Windows install reads the windows zip's checksum, any other the linux one ({UpdateService.OsName(true)}, {UpdateService.OsName(false)})");

        KnobPanel p = Panel();
        string About(KnobGroup g) => Shown(p, g);

        k = Knobs("cx-upd-mutenow");
        k.Consent.Set(UpdateKnobs.Yes);
        gear = new GearAttention();
        Launch(k, new Source("v0140.txt"), new ExtenderLog { Tap = _ => { } }, gear).Check();
        k.Mute.Press();
        check.Expect(!gear.BadgeShown(true), $"muting takes the badge down in the same launch ({gear.Tip})");
        string muted = About(k.Group);
        check.Expect(!muted.Contains("v0.14.0", StringComparison.Ordinal), $"muting takes the release out of About in the same launch ({muted})");
        check.Expect(!muted.Contains("Copy download link", StringComparison.Ordinal) && !muted.Contains("Don't tell me", StringComparison.Ordinal),
                     $"with no release to tell about, About shows neither button ({muted})");

        k = Knobs("cx-upd-lines");
        k.Consent.Set(UpdateKnobs.Yes);
        Launch(k, new Source("v0140.txt"), new ExtenderLog { Tap = _ => { } }, new GearAttention()).Check();
        About(k.Group);
        string[] help = p.Rows.FirstOrDefault(r => r.Text == "Copy download link")?.Help ?? [];
        check.Expect(help.Contains("v0.14.0 is out.") && help.Contains("Shadows soften at dusk.") && help.Contains("The chat window remembers its size."),
                     $"each changelog line starts its own line ({string.Join(" / ", help)})");

        p.Category = Array.IndexOf(KnobPanel.KnobCategories, "Graphics");
        p.Show(General.About);
        check.Expect(KnobPanel.KnobCategories[p.Category] == "General" && p._subs[p.Sub].Name == "About",
                     $"the gear opens About from a panel left on Graphics ({KnobPanel.KnobCategories[p.Category]} » {p._subs[p.Sub].Name})");
        p.Close();

        log.Info(check.Ok ? "UpdateNotice.SelfCheck: passed" : "UpdateNotice.SelfCheck: FAILED");
        return check.Ok;
    }
}
