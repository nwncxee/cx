using CX.Framework;
using CX.Ui;
using static CX.Services.Update.UpdateNoticeApprovedExamples;

namespace CX.Services.Update;

/// <summary>The consent question and the quiet badge on the main-menu gear, launch after launch, against throwaway
/// About groups and fixture release notices. Oracle: the tooltips and labels the examples quote, and the
/// fixture manifests' own versions and lines.</summary>
internal static class UpdateBadgeApprovedExamples
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UpdateBadgeApprovedExamples.SelfCheck");
        var ear = new ExtenderLog { Tap = _ => { } };

        // Given a fresh install where the player never answered the question about updates
        // When the player reaches the main menu
        // Then the gear shows the circled "!" with the tooltip "Should CX tell you about updates?", and no network request is made
        var k = Knobs("cx-upd-e1");
        var gear = new GearAttention();
        var src = new Source("v0140.txt");
        Launch(k, src, ear, gear).Check();
        check.Expect(gear.BadgeShown(atMainMenu: true) && gear.Tip == "Should CX tell you about updates?", $"E1 the gear asks ({gear.Tip})");
        check.Expect(src.Requests == 0, $"E1 no network request is made ({src.Requests})");

        // Given the player answered No
        // When the player starts the game again and reaches the main menu
        // Then no badge shows and no request is made
        k = Knobs("cx-upd-e2");
        k.Consent.Set(UpdateKnobs.No);
        gear = new GearAttention();
        src = new Source("v0140.txt");
        Launch(k, src, ear, gear).Check();
        check.Expect(!gear.BadgeShown(atMainMenu: true) && src.Requests == 0, $"E2 no badge and no request ({gear.Tip}, {src.Requests})");

        // Given the player answered Yes and a newer signed release is out
        // When the player reaches the main menu
        // Then the badge shows with the tooltip "An update is available: v0.14.0"
        k = Knobs("cx-upd-e3");
        k.Consent.Set(UpdateKnobs.Yes);
        gear = new GearAttention();
        Launch(k, new Source("v0140.txt"), ear, gear).Check();
        check.Expect(gear.BadgeShown(atMainMenu: true) && gear.Tip == "An update is available: v0.14.0", $"E3 the badge names the newer release ({gear.Tip})");

        // Given the player answered Yes and already runs the newest release
        // When the player reaches the main menu
        // Then no badge shows
        k = Knobs("cx-upd-e4");
        k.Consent.Set(UpdateKnobs.Yes);
        gear = new GearAttention();
        new UpdateNotice(k, "v0.14.0", "linux", Keys(), ear, new Source("v0140.txt").Fetch, _ => true, gear).Check();
        check.Expect(!gear.BadgeShown(atMainMenu: true), $"E4 no badge on the newest release ({gear.Tip})");

        // Given the badge shows for v0.14.0
        // When the player clicks the gear
        // Then the panel opens at General » About, showing v0.14.0, its changelog lines, Copy download link and Don't tell me about v0.14.0
        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x2200; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }
        var p = new KnobPanel();
        p.OpenAt(1920f, 1080f, KnobPanel.FacePx, body, small);
        p.Show(gear.Target ?? General.About);
        check.Expect(KnobPanel.KnobCategories[p.Category] == "General" && p._subs[p.Sub].Name == "About", $"E5 the gear opens General » About ({KnobPanel.KnobCategories[p.Category]} » {p._subs[p.Sub].Name})");
        k = Knobs("cx-upd-e5");
        k.Consent.Set(UpdateKnobs.Yes);
        gear = new GearAttention();
        Launch(k, new Source("v0140.txt"), ear, gear).Check();
        check.Expect(ReferenceEquals(gear.Target, k.Group), "E5 the badge's reason points the gear at its About group");
        p._subs = [new KnobPanel.SubMenu(k.Group)]; p.Sub = 0; p.LayoutRows();
        string shown = Shown(p);
        foreach (string want in new[] { "v0.14.0", "Shadows soften at dusk.", "The chat window remembers its size.", "Copy download link", "Don't tell me about v0.14.0" })
            check.Expect(shown.Contains(want, StringComparison.Ordinal), $"E5 About shows '{want}' ({shown})");

        // Given About was opened while v0.14.0 was the newest release
        // When the player reaches the main menu, this launch or a later one
        // Then no badge shows until a newer release comes out
        check.Expect(!gear.BadgeShown(atMainMenu: true), $"E6 opening About cleared the badge this launch ({gear.Tip})");
        gear = new GearAttention();
        Launch(k, new Source("v0140.txt"), ear, gear).Check();
        check.Expect(!gear.BadgeShown(atMainMenu: true), $"E6 a later launch shows no badge for v0.14.0 ({gear.Tip})");
        gear = new GearAttention();
        Launch(k, new Source("v0150.txt"), ear, gear).Check();
        check.Expect(gear.BadgeShown(atMainMenu: true), "E6 the badge returns once v0.15.0 is out");

        // Given the player chose Don't tell me about v0.14.0
        // When the player reaches the main menu while v0.14.0 is still the newest release
        // Then neither the badge nor the About row mentions it
        k = Knobs("cx-upd-e7");
        k.Consent.Set(UpdateKnobs.Yes);
        gear = new GearAttention();
        Launch(k, new Source("v0140.txt"), ear, gear).Check();
        k.Mute.Press();
        gear = new GearAttention();
        Launch(k, new Source("v0140.txt"), ear, gear).Check();
        p._subs = [new KnobPanel.SubMenu(k.Group)]; p.Sub = 0; p.LayoutRows();
        check.Expect(!gear.BadgeShown(atMainMenu: true), $"E7 no badge for the muted release ({gear.Tip})");
        check.Expect(!Shown(p).Contains("v0.14.0", StringComparison.Ordinal), $"E7 About does not mention v0.14.0 ({Shown(p)})");

        // Given the player muted v0.14.0
        // When v0.15.0 comes out and the player reaches the main menu
        // Then the badge shows again, naming v0.15.0
        gear = new GearAttention();
        Launch(k, new Source("v0150.txt"), ear, gear).Check();
        bool back = gear.BadgeShown(atMainMenu: true);
        p._subs = [new KnobPanel.SubMenu(k.Group)]; p.Sub = 0; p.LayoutRows();
        check.Expect(back && Shown(p).Contains("v0.15.0", StringComparison.Ordinal), $"E8 the badge is back for v0.15.0, and About names it ({back}; {Shown(p)})");

        // Given the player is in an area, in play
        // When an update is available
        // Then no badge shows anywhere, and it shows at the next main-menu visit
        k = Knobs("cx-upd-e9");
        k.Consent.Set(UpdateKnobs.Yes);
        gear = new GearAttention();
        Launch(k, new Source("v0140.txt"), ear, gear).Check();
        check.Expect(!gear.BadgeShown(atMainMenu: false), "E9 no badge in play");
        check.Expect(gear.BadgeShown(atMainMenu: true), "E9 the badge shows back at the main menu");

        // Given the previous session found v0.14.0 newer than the running v0.13.2
        // When the player starts the game
        // Then the first line of cx.log names both versions
        k = Knobs("cx-upd-e15");
        k.Consent.Set(UpdateKnobs.Yes);
        Launch(k, new Source("v0140.txt"), ear, new GearAttention()).Check();
        string first = UpdateNotice.BuildLine(Running, k.Found.V);
        check.Expect(first.StartsWith("NWNCX:EE build v0.13.2", StringComparison.Ordinal) && first.Contains("v0.14.0", StringComparison.Ordinal), $"E15 the first line names both versions ({first})");

        // Given the player answered Yes and the badge shows
        // When the player switches updates off in About
        // Then the badge goes away, and later launches make no request
        k = Knobs("cx-upd-e16");
        k.Consent.Set(UpdateKnobs.Yes);
        gear = new GearAttention();
        Launch(k, new Source("v0140.txt"), ear, gear).Check();
        bool before = gear.BadgeShown(atMainMenu: true);
        k.Consent.Set(UpdateKnobs.No);
        check.Expect(before && !gear.BadgeShown(atMainMenu: true), $"E16 switching off takes the badge away ({before}, {gear.Tip})");
        src = new Source("v0140.txt");
        Launch(k, src, ear, new GearAttention()).Check();
        check.Expect(src.Requests == 0, $"E16 a later launch makes no request ({src.Requests})");

        p.Close();
        log.Info(check.Ok ? "UpdateBadgeApprovedExamples.SelfCheck: passed" : "UpdateBadgeApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Every piece of text the panel's rows carry, wrapped lines joined back with a space.</summary>
    private static string Shown(KnobPanel p)
    {
        var sb = new System.Text.StringBuilder();
        foreach (KnobPanel.Row r in p.Rows)
        {
            sb.Append(r.Text).Append(" | ");
            foreach (string l in r.Label) sb.Append(l).Append(' ');
            foreach (string l in r.Help) sb.Append(l).Append(' ');
            sb.Append("| ");
        }
        return sb.ToString();
    }
}
