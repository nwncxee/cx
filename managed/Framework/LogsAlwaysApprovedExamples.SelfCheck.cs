using System;

namespace CX.Framework;

/// <summary>cx.log is always written and no setting turns it off: not the panel, not the chat command, not a
/// cx.cfg an older CX left behind. Oracle: the row labels, key and reply written here as literals, and the
/// log file's own bytes.</summary>
public static class LogsAlwaysApprovedExamples
{
    /// <summary>A key no real setting claims, fed beside the old one to show the fed file was read at all.</summary>
    private const string Probe = "cx-selfcheck-logs.keep";

    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        NoPanelRow(check);
        NoCommand(check, log);
        NoCopiedLine(check);
        OldKeyLeaves(check);

        log.Always(check.Ok ? "LogsAlwaysApprovedExamples.SelfCheck: passed" : "LogsAlwaysApprovedExamples.SelfCheck: FAILED");
    }

    // Given the settings panel
    // When the player opens General
    // Then it shows Enable chat commands, and no Write cx.log for bug reports row
    private static void NoPanelRow(Checker check)
    {
        bool commands = false, logs = false;
        foreach (Knob k in General.G.Knobs)
        {
            if (k.Tier != Tier.User && k.Tier != Tier.Advanced) continue;
            commands |= k.Label == "Enable chat commands";
            logs |= k.Label == "Write cx.log for bug reports";
        }
        check.Expect(commands, "L2 General shows Enable chat commands");
        check.Expect(!logs, "L2 General shows no Write cx.log for bug reports row");
    }

    // Given chat commands are on
    // When the player types /general logs 0
    // Then CX answers that General has no logs setting
    // And cx.log goes on getting lines
    private static void NoCommand(Checker check, ExtenderLog log)
    {
        bool was = General.Commands.V;
        General.Commands.Set(true);
        bool ran = CommandRegistry.Global.TryDispatch("/general logs 0", out string? reply);
        General.Commands.Set(was);
        check.Expect(ran && reply != null && reply.StartsWith("[general] unknown knob \"logs\"", StringComparison.Ordinal),
            $"L3 /general logs 0 answers that General has no logs setting, not: {reply}");
        string mark = $"LogsAlwaysApprovedExamples: L3 still writing {Guid.NewGuid():N}";
        log.Info(mark);
        check.ExpectFlushed();
        check.Expect(ExtenderLog.TailHas(mark), "L3 cx.log goes on getting lines after /general logs 0");
    }

    // Given any cx.cfg
    // When the player presses Copy my settings for a bug report
    // Then the copied text has no general.logs line
    private static void NoCopiedLine(Checker check)
    {
        string copied = KnobSurface.Report();
        check.Expect(copied.Contains("general.commands=", StringComparison.Ordinal), "L4 the copied text carries the General settings");
        check.Expect(!copied.Contains("general.logs", StringComparison.Ordinal), "L4 the copied text has no general.logs line");
    }

    // Given cx.cfg holds general.logs=0
    // When the player changes any setting
    // Then cx.cfg is written again without the general.logs line
    private static void OldKeyLeaves(Checker check)
    {
        Settings.Read($"general.logs=0\n{Probe}=1\n");
        string written = KnobFile.Compose();
        Settings.Forget("general.logs");
        Settings.Forget(Probe);
        check.Expect(written.Contains($"\n{Probe}=1\n", StringComparison.Ordinal), "L5 the fed cx.cfg was read: its other key is written back");
        check.Expect(!written.Contains("general.logs", StringComparison.Ordinal), "L5 cx.cfg is written again without the general.logs line");
        Settings.Read("General.Logs=1\n");
        check.Expect(Settings.Raw("general.logs") == null, "the old key is dropped on read whatever its case");
    }
}
