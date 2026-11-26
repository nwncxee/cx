using System;
using System.IO;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>The occluder fade is gone: no walls group in the panel, no walls key in cx.cfg, /knobs or the copied
/// settings, and no fade line in cx.log. Oracle: the row labels, keys and log words written here as literals, and the
/// log file's own bytes.</summary>
internal static class OccluderFadeGoneApprovedExamples
{
    /// <summary>The three keys the fade had.</summary>
    private static readonly string[] FadeKeys = ["walls.fade", "walls.floor", "walls.radius"];
    /// <summary>The three rows the fade had.</summary>
    private static readonly string[] FadeRows = ["Fade objects that hide you", "Faded opacity", "Fade ray width"];
    /// <summary>A key no real setting claims, fed beside the old ones to show the fed file was read at all.</summary>
    private const string Probe = "cx-selfcheck-fadegone.keep";

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        CameraKnobs.Declare();

        NoPanelRows(check);
        OldKeysLeave(check);
        NoListing(check);

        log.Always(check.Ok ? "OccluderFadeGoneApprovedExamples.SelfCheck: passed" : "OccluderFadeGoneApprovedExamples.SelfCheck: FAILED");
    }

    /// <summary>R4 on its own: only a host where Alloy armed can show it, so the stub skips it alone.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void LogSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "OccluderFadeGoneApprovedExamples.LogSelfCheck:");
        if (NoLogLine(check)) return;
        log.Always(check.Ok ? "OccluderFadeGoneApprovedExamples.LogSelfCheck: passed" : "OccluderFadeGoneApprovedExamples.LogSelfCheck: FAILED");
    }

    // Given CX with Alloy on, or with Alloy off
    // When the player opens the CX settings at the Camera tab
    // Then it has no Walls group, and no row called Fade objects that hide you, Faded opacity or Fade ray width
    // Partner: the Free camera group is still there, with its Enter the free camera button
    private static void NoPanelRows(Checker check)
    {
        bool walls = false, free = false, enter = false;
        foreach (KnobGroup g in KnobGroup.All)
        {
            if (g.Submenu != "Camera") continue;
            walls |= g.Name == "walls" || g.Title == "Walls";
            if (g.Name == "free" && g.Title == "Free camera")
            {
                free = true;
                foreach (Knob k in g.Knobs) enter |= k is KnobButton && k.Label == "Enter the free camera";
            }
            foreach (Knob k in g.Knobs)
                check.Expect(Array.IndexOf(FadeRows, k.Label) < 0, $"R1 the Camera tab has no {k.Label} row");
        }
        check.Expect(!walls, "R1 the Camera tab has no Walls group");
        check.Expect(free && enter, "R1 the Camera tab still has Free camera and its Enter the free camera button");
    }

    // Given a cx.cfg that holds walls.fade, walls.floor and walls.radius, one of them in mixed case
    // When the client reads it and writes it back
    // Then the three keys are gone, and every other key in the file is written back
    private static void OldKeysLeave(Checker check)
    {
        string?[] stored = Array.ConvertAll(FadeKeys, k => KnobGroup.Find(k)?.FormatStored());
        Settings.Read($"walls.fade=0\nWalls.Floor=0.3\nwalls.radius=2\n{Probe}=1\n");
        string written = KnobFile.Compose();
        bool[] held = Array.ConvertAll(FadeKeys, k => Settings.Raw(k) != null);
        for (int i = 0; i < FadeKeys.Length; i++)
        {
            Settings.Forget(FadeKeys[i]);
            if (stored[i] != null) KnobGroup.Find(FadeKeys[i])!.Parse(stored[i]!);
        }
        Settings.Forget(Probe);

        check.Expect(written.Contains($"\n{Probe}=1\n", StringComparison.Ordinal), "R2 the fed cx.cfg was read: its other key is written back");
        for (int i = 0; i < FadeKeys.Length; i++)
        {
            check.Expect(!held[i], $"R2 {FadeKeys[i]} is dropped on read");
            check.Expect(!written.Contains(FadeKeys[i], StringComparison.OrdinalIgnoreCase), $"R2 cx.cfg is written again without {FadeKeys[i]}");
        }
    }

    // Given any cx.cfg
    // When the player lists /knobs or copies their settings for a bug report
    // Then no walls key appears, and the free camera keys do
    private static void NoListing(Checker check)
    {
        bool was = General.Commands.V;
        General.Commands.Set(true);
        bool ran = CommandRegistry.Global.TryDispatch("/knobs", out string? listed);
        General.Commands.Set(was);
        string copied = KnobSurface.Report();
        check.Expect(ran && listed != null, "R3 /knobs answers");
        foreach ((string what, string text) in new[] { ("/knobs", listed ?? ""), ("the copied settings", copied) })
        {
            check.Expect(text.Contains("free.speed=", StringComparison.Ordinal), $"R3 {what} lists the free camera keys");
            check.Expect(!text.Contains("walls.", StringComparison.OrdinalIgnoreCase), $"R3 {what} has no walls key");
        }
    }

    // Given Alloy is on
    // When the client boots into an area and the player reads cx.log
    // Then no line mentions walls.fade or the fade's ray casts
    private static bool NoLogLine(Checker check)
    {
        check.ExpectFlushed();
        string text;
        try
        {
            using var f = new FileStream(Path.Combine(ExtenderLog.Dir, "cx.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            text = new StreamReader(f).ReadToEnd();
        }
        catch (IOException e) { check.Expect(false, $"R4 cx.log reads: {e.Message}"); return false; }
        if (!text.Contains("AlloyRenderer: armed", StringComparison.Ordinal))
        {
            return check.Skip("R4 needs Alloy armed, and this host's cx.log says it never armed");
        }
        check.Expect(!text.Contains("walls.fade", StringComparison.Ordinal), "R4 no cx.log line mentions walls.fade");
        check.Expect(!text.Contains("parts tagged per frame", StringComparison.Ordinal), "R4 no cx.log line counts the fade's ray casts");
        return false;
    }
}
