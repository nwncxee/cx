using System.Reflection;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Update;

/// <summary>A release notice counts only when a built-in key signed it: fixture manifests signed by ssh-keygen with
/// throwaway keys, judged against those keys' public halves. Oracle: the manifests' own text, the releases address
/// written out here, and ssh-keygen's signatures.</summary>
internal static class UpdateNoticeApprovedExamples
{
    /// <summary>The version the checks run as: older than every fixture release but <c>old.txt</c>.</summary>
    internal const string Running = "v0.13.2";

    /// <summary>A fixture's bytes, embedded from <c>Services/Update/Fixtures</c>.</summary>
    internal static byte[] Fixture(string name)
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("update/" + name)
                         ?? throw new InvalidOperationException("no fixture " + name);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>The two fixture keys, current and next, as an allowed-signers file names them.</summary>
    internal static ReleaseKey[] Keys() => ReleaseKeys.Parse(System.Text.Encoding.UTF8.GetString(Fixture("allowed_signers")));

    /// <summary>A release source that serves one fixture manifest and its signature, and counts requests.</summary>
    internal sealed class Source(string manifest)
    {
        public int Requests;
        public string Manifest = manifest;
        public byte[]? Fetch(string url)
        {
            Requests++;
            return url.EndsWith(".sig", StringComparison.Ordinal) ? Fixture(Manifest + ".sig") : Fixture(Manifest);
        }
    }

    /// <summary>One launch: a throwaway About group's knobs answered Yes, a notice over a fixture source.</summary>
    internal static UpdateNotice Launch(UpdateKnobs k, Source src, ExtenderLog log, GearAttention gear, Func<string, bool>? copy = null)
        => new(k, Running, "linux", Keys(), log, src.Fetch, copy ?? (_ => true), gear);

    /// <summary>A knob panel open at 1080p over a test face, for reading what a group's rows show.</summary>
    internal static KnobPanel Panel()
    {
        var face = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x2200; cp++) face.SetTestGlyph(cp, cp == ' ' ? 5f : 8f);
        var p = new KnobPanel();
        p.OpenAt(1920f, 1080f, KnobPanel.FacePx, face, face);
        return p;
    }

    /// <summary>Open <paramref name="g"/> on <paramref name="p"/>; each row's text, label and help, rows split by " | ".</summary>
    internal static string Shown(KnobPanel p, KnobGroup g)
    {
        p._subs = [new KnobPanel.SubMenu(g)]; p.Sub = 0; p.LayoutRows();
        return string.Join(" | ", p.Rows.Select(r => r.Text + " " + string.Join(" ", r.Label ?? []) + " " + string.Join(" ", r.Help ?? [])));
    }

    /// <summary>A throwaway group, never registered, with the notice's knobs on it.</summary>
    internal static UpdateKnobs Knobs(string name)
        => UpdateKnobs.Declare(new KnobGroup(name, "General", "About", null, null, true, Effect.Live, null, null, "", register: false));

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UpdateNoticeApprovedExamples.SelfCheck");
        var lines = new List<string>();
        var ear = new ExtenderLog { Tap = lines.Add };

        // Given a correctly signed release notice that also names another web address
        // When the player presses Copy download link
        // Then the clipboard holds https://github.com/nwncxee/cx/releases
        var k = Knobs("cx-upd-e10");
        k.Consent.Set(UpdateKnobs.Yes);
        string copied = "";
        var gear = new GearAttention();
        var n = Launch(k, new Source("url.txt"), ear, gear, s => { copied = s; return true; });
        n.Check();
        check.Expect(n.Notice?.Version == "v99.1.0", $"E10 the signed notice is taken ({n.Notice?.Version})");
        k.Copy.Press();
        check.Expect(copied == "https://github.com/nwncxee/cx/releases", $"E10 the clipboard holds the releases page ({copied})");

        // Given a release notice for v9.9.9 that neither built-in key signed
        // When the player reaches the main menu
        // Then no badge shows, and cx.log says the update notice was refused
        k = Knobs("cx-upd-e11");
        k.Consent.Set(UpdateKnobs.Yes);
        gear = new GearAttention();
        lines.Clear();
        n = Launch(k, new Source("forged.txt"), ear, gear);
        n.Check();
        check.Expect(!gear.BadgeShown(atMainMenu: true), "E11 no badge shows at the main menu");
        check.Expect(lines.Exists(l => l.Contains("update notice was refused", StringComparison.Ordinal)), $"E11 cx.log says the update notice was refused ({string.Join(" | ", lines)})");

        // Given a release notice for a newer release, signed by the "next" key
        // When the player reaches the main menu
        // Then the update badge shows
        k = Knobs("cx-upd-e12");
        k.Consent.Set(UpdateKnobs.Yes);
        gear = new GearAttention();
        n = Launch(k, new Source("next.txt"), ear, gear);
        n.Check();
        check.Expect(gear.BadgeShown(atMainMenu: true) && gear.Tip == UpdateNotice.NewTip(n.Notice?.Version ?? ""), $"E12 the update badge shows ({gear.Tip})");

        // Given a correctly signed release notice naming a version older than the running one
        // When the player reaches the main menu
        // Then no badge shows
        k = Knobs("cx-upd-e13");
        k.Consent.Set(UpdateKnobs.Yes);
        gear = new GearAttention();
        n = Launch(k, new Source("old.txt"), ear, gear);
        n.Check();
        check.Expect(!gear.BadgeShown(atMainMenu: true), $"E13 no badge shows ({gear.Tip})");

        log.Info(check.Ok ? "UpdateNoticeApprovedExamples.SelfCheck: passed" : "UpdateNoticeApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }
}
