using System.Text.RegularExpressions;
using CX.Framework;
using CX.Ui;
using static CX.Services.Update.UpdateNoticeApprovedExamples;

namespace CX.Services.Update;

/// <summary>The checksums a signed notice names, and the release key About shows. <c>sums*.txt</c> are signed by a
/// throwaway key in <c>sums_signers</c>; <c>sums-tampered.txt</c> has one checksum changed after signing. Oracle: the
/// checksums written into the fixtures by hand, and the fingerprints <c>ssh-keygen -lf</c> printed for each key.</summary>
internal static class UpdateChecksumApprovedExamples
{
    private const string LinuxSum = "caf90169eefa5f807d577486b9f795ab86ae2983c5c20806cff959117e90af18";
    private const string WindowsSum = "340d600392818df2413382dc7d8325c360d83ea49a262d31760348484bbc10b5";
    private const string MoonglowSum = "36f7cb3378553c5873cc54bca483155033ee15290acd9af10d58b96296f59a28";
    private const string CurrentFingerprint = "SHA256:oeiAe2Ez3GXZrj5b9d5ADo7NOYqSrBHTxTZlK0a8JlM";
    private const string NextFingerprint = "SHA256:GErPRaZGR8m3+ocu5WbM+vSEagS5QKpf2E+B5BSwWgA";

    /// <summary>Any SHA-256 written as hex, so an absence check does not hang on the row's wording.</summary>
    private static readonly Regex AnySum = new("[0-9a-f]{64}", RegexOptions.CultureInvariant);

    /// <summary>One launch's About, declared as the service declares it, with what it copied and requested.</summary>
    private sealed class Launched
    {
        public UpdateAbout A = null!;
        public string Copied = "";
        public readonly List<string> Asked = [];
    }

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UpdateChecksumApprovedExamples.SelfCheck");
        var lines = new List<string>();
        var ear = new ExtenderLog { Tap = lines.Add };
        string sumsSigners = System.Text.Encoding.UTF8.GetString(Fixture("sums_signers"));
        string fixtureSigners = System.Text.Encoding.UTF8.GetString(Fixture("allowed_signers"));
        KnobPanel p = Panel();
        Launched Open(string name, string manifest, string os, string signers, string consent = UpdateKnobs.Yes,
                      Func<string, byte[]?>? fetch = null, GearAttention? gear = null)
        {
            var l = new Launched();
            var src = new Source(manifest);
            var g = new KnobGroup(name, "General", "About", null, null, true, Effect.Live, null, null, "", register: false);
            l.A = UpdateService.Declare(g, Running, os, signers, ear, url => { l.Asked.Add(url); return (fetch ?? src.Fetch)(url); },
                                        s => { l.Copied = s; return true; }, gear ?? new GearAttention());
            l.A.Knobs.Consent.Set(consent);
            l.A.Notice.Check();
            return l;
        }
        bool Has(Knob k) => p.Rows.Any(r => ReferenceEquals(r.K, k));

        Launched linux = Open("cx-upd-sum-linux", "sums.txt", "linux", sumsSigners);
        string onLinux = Shown(p, linux.A.Knobs.Group);

        // CX-694 E1
        // Given a signed notice for v0.14.0 that names the SHA-256 of cx-linux-x64-v0.14.0.zip and cx-windows-x64-v0.14.0.zip
        // When a player on Linux opens General » About
        // Then the release row shows the Linux zip's SHA-256, and Copy checksum puts exactly that value on the clipboard
        check.Expect(onLinux.Contains(LinuxSum, StringComparison.Ordinal) && !onLinux.Contains(WindowsSum, StringComparison.Ordinal),
                     $"CX-694 E1 the release row shows the Linux zip's SHA-256, not the Windows one ({onLinux})");
        linux.A.Knobs.CopySum.Press();
        check.Expect(linux.Copied == LinuxSum, $"CX-694 E1 Copy checksum copies exactly the Linux zip's SHA-256 ({linux.Copied})");

        // CX-694 E2
        // Given a correctly signed notice whose checksum line was changed after signing
        // When the player reaches the main menu
        // Then no badge shows, and cx.log says the update notice was refused
        var gear = new GearAttention();
        lines.Clear();
        Open("cx-upd-sum-e2", "sums-tampered.txt", "linux", sumsSigners, gear: gear);
        check.Expect(!gear.BadgeShown(atMainMenu: true), $"CX-694 E2 no badge shows ({gear.Tip})");
        check.Expect(lines.Exists(l => l.Contains("update notice was refused", StringComparison.Ordinal)),
                     $"CX-694 E2 cx.log says the update notice was refused ({string.Join(" | ", lines)})");

        // CX-694 E3
        // Given a correctly signed notice for a newer release that names no checksums
        // When the player opens General » About
        // Then the row shows the version and its changes, with no checksum and no Copy checksum button
        Launched e3 = Open("cx-upd-sum-e3", "v0140.txt", "linux", fixtureSigners);
        string shown = Shown(p, e3.A.Knobs.Group);
        check.Expect(shown.Contains("v0.14.0", StringComparison.Ordinal) && shown.Contains("Shadows soften at dusk.", StringComparison.Ordinal)
                     && shown.Contains("The chat window remembers its size.", StringComparison.Ordinal),
                     $"CX-694 E3 the row shows the version and its changes ({shown})");
        check.Expect(!Has(e3.A.Knobs.CopySum) && !AnySum.IsMatch(shown), $"CX-694 E3 no checksum and no Copy checksum button ({shown})");

        // CX-694 E4
        // Given a CX build from before this change, and a signed notice for a newer release that names checksums
        // When the player reaches the main menu
        // Then the update badge shows as it does today
        (string Version, string[] Changes)? older = ReleasedParse(System.Text.Encoding.UTF8.GetString(Fixture("sums.txt")));
        check.Expect(older is { Version: "v0.14.0" } o
                     && o.Changes.SequenceEqual(["Shadows soften at dusk.", "The chat window remembers its size."])
                     && UpdateNotice.Newer(o.Version, Running),
                     $"CX-694 E4 a released build's parser reads the notice as v0.14.0 with its two changes, newer than {Running} ({older?.Version}; {string.Join(" / ", older?.Changes ?? [])})");

        // CX-694 E6
        // Given a signed notice for v0.14.0 that names the SHA-256 of the Linux, Windows and Moonglow plugin zips
        // When a player on Windows opens General » About
        // Then the row shows the Windows zip's SHA-256, and Copy checksum puts exactly that value on the clipboard
        Launched windows = Open("cx-upd-sum-windows", "sums.txt", "windows", sumsSigners);
        shown = Shown(p, windows.A.Knobs.Group);
        check.Expect(shown.Contains(WindowsSum, StringComparison.Ordinal), $"CX-694 E6 the row shows the Windows zip's SHA-256 ({shown})");
        windows.A.Knobs.CopySum.Press();
        check.Expect(windows.Copied == WindowsSum, $"CX-694 E6 Copy checksum copies exactly the Windows zip's SHA-256 ({windows.Copied})");

        // CX-694 E7
        // Given a signed notice for v0.14.0 that names checksums for the Linux, Windows and Moonglow plugin zips
        // When a player on Linux opens General » About
        // Then the row shows one checksum, the Linux zip's, and neither the Windows nor the Moonglow plugin checksum
        string[] sums = [.. AnySum.Matches(onLinux).Select(m => m.Value).Distinct()];
        check.Expect(sums.SequenceEqual([LinuxSum]) && !onLinux.Contains(MoonglowSum, StringComparison.Ordinal),
                     $"CX-694 E7 the row shows the Linux checksum alone ({string.Join(", ", sums)})");

        // CX-694 E8
        // Given a correctly signed notice for a newer release that names only the Windows zip's checksum
        // When a player on Linux opens General » About
        // Then the row shows the version and its changes, with no checksum and no Copy checksum button
        Launched e8 = Open("cx-upd-sum-e8", "sums-windows-only.txt", "linux", sumsSigners);
        shown = Shown(p, e8.A.Knobs.Group);
        check.Expect(shown.Contains("v0.14.0", StringComparison.Ordinal) && shown.Contains("Shadows soften at dusk.", StringComparison.Ordinal)
                     && shown.Contains("The chat window remembers its size.", StringComparison.Ordinal),
                     $"CX-694 E8 the row shows the version and its changes ({shown})");
        check.Expect(!Has(e8.A.Knobs.CopySum) && !AnySum.IsMatch(shown), $"CX-694 E8 no checksum and no Copy checksum button ({shown})");

        // CX-694 E9
        // Given the notice from E1
        // When a player on Linux opens General » About
        // Then the checksum is shown beside the name cx-linux-x64-v0.14.0.zip
        string? sumRow = onLinux.Split(" | ").FirstOrDefault(r => r.Contains(LinuxSum, StringComparison.Ordinal));
        check.Expect(sumRow != null && sumRow.Contains("cx-linux-x64-v0.14.0.zip", StringComparison.Ordinal),
                     $"CX-694 E9 the checksum's row names cx-linux-x64-v0.14.0.zip ({sumRow ?? onLinux})");

        // CX-694 E10
        // Given a correctly signed notice for a newer release that names checksums
        // When the player opens General » About and presses Copy checksum
        // Then CX has requested only cx-update and cx-update.sig
        string[] asked = [.. linux.Asked.Select(u => u[(u.LastIndexOf('/') + 1)..]).Order(StringComparer.Ordinal)];
        check.Expect(asked.SequenceEqual(["cx-update", "cx-update.sig"]), $"CX-694 E10 CX requested only cx-update and cx-update.sig ({string.Join(", ", linux.Asked)})");

        // CX-695 E1
        // Given any CX build
        // When the player opens General » About
        // Then it shows the fingerprint of each release key the build trusts, written as SHA256:<base64>, exactly as
        // ssh-keygen's verify prints it after "with ED25519 key", and Copy release key puts the build's allowed_signers
        // text on the clipboard, exactly as packaging/allowed_signers held it at build time
        Launched keyed = Open("cx-upd-key-e1", "v0140.txt", "linux", fixtureSigners, UpdateKnobs.Ask);
        shown = Shown(p, keyed.A.Knobs.Group);
        check.Expect(shown.Contains(CurrentFingerprint, StringComparison.Ordinal) && shown.Contains(NextFingerprint, StringComparison.Ordinal),
                     $"CX-695 E1 About shows the fingerprint of each key the build trusts ({shown})");
        keyed.A.Key.Copy.Press();
        check.Expect(keyed.Copied == fixtureSigners, $"CX-695 E1 Copy release key copies the allowed_signers text exactly ({keyed.Copied})");

        // CX-695 E4
        // Given Tell me about new CX releases is Not answered yet or No, and the machine is offline
        // When the player opens General » About
        // Then it shows the fingerprint and Copy release key, and CX makes no request
        foreach (string answer in new[] { UpdateKnobs.Ask, UpdateKnobs.No })
        {
            Launched off = Open("cx-upd-key-e4-" + answer, "v0140.txt", "linux", fixtureSigners, answer, fetch: _ => null);
            shown = Shown(p, off.A.Knobs.Group);
            check.Expect(shown.Contains(CurrentFingerprint, StringComparison.Ordinal) && Has(off.A.Key.Copy),
                         $"CX-695 E4 with the consent at {answer}, About shows the fingerprint and Copy release key ({shown})");
            check.Expect(off.Asked.Count == 0, $"CX-695 E4 with the consent at {answer}, CX made no request ({string.Join(", ", off.Asked)})");
        }

        p.Close();
        log.Info(check.Ok ? "UpdateChecksumApprovedExamples.SelfCheck: passed" : "UpdateChecksumApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The notice parser every released CX build carries, frozen as <c>UpdateManifest.Parse</c> read at
    /// 0346132d (v0.13.x), so a notice the new <c>sign.sh</c> writes is judged by the code already on players' disks.</summary>
    private static (string Version, string[] Changes)? ReleasedParse(string text)
    {
        string[] lines = text.Replace("\r", "").Split('\n');
        if (lines.Length == 0 || lines[0] != "cx-update 1") return null;
        string? version = null;
        var changes = new List<string>();
        foreach (string l in lines.AsSpan(1))
        {
            if (l.StartsWith("version ", StringComparison.Ordinal)) version ??= l[8..].Trim();
            else if (l.StartsWith("change ", StringComparison.Ordinal)) changes.Add(l[7..].Trim());
        }
        return version != null && UpdateManifest.Parse3(version) != null ? (version, [.. changes]) : null;
    }
}
