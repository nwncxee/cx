namespace CX.Framework;

/// <summary>General » About's Save my log for a bug report (approved examples R1-R8), pressed on a log of the check's own
/// in a folder of its own, so a boot never writes cx-report.log beside the player's cx.log. Oracle: the literals written
/// here and the files' own bytes.</summary>
internal static class LogReportApprovedExamples
{
    private const string CopyLabel = "Copy my settings for a bug report";
    private const string SaveLabel = "Save my log for a bug report";

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LogReportApprovedExamples.SelfCheck");
        string root = Path.Combine(Path.GetTempPath(), $"cx-logreport-{Environment.ProcessId}");
        LogReport.LogReportState was = LogReport.State;
        int case_ = 0;
        string Fresh()
        {
            string d = Path.Combine(root, (++case_).ToString());
            Directory.CreateDirectory(d);
            return d;
        }
        static string? Read(string d, string name)
        {
            string p = Path.Combine(d, name);
            if (!File.Exists(p)) return null;
            using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(f).ReadToEnd();
        }
        var heard = new List<string>();
        string? copied = null;
        bool clipboardSays = true;
        LogParts Rig(string d, params string[] lines)
        {
            var parts = new LogParts(d);
            foreach (string l in lines) parts.Write(l);
            parts.Flush();
            heard.Clear();
            copied = null;
            clipboardSays = true;
            LogReport.State = new LogReport.LogReportState
            {
                Save = parts.SaveReport,
                Copy = s => { copied = s; return clipboardSays; },
                Log = new ExtenderLog { Tap = heard.Add },
            };
            return parts;
        }
        static void Press()
        {
            foreach (Knob k in General.About.Knobs)
                if (k is KnobButton b && k.Label == SaveLabel) { b.Press(); return; }
        }

        try
        {
            // Given CX has written lines to cx.log this launch
            // When the player presses Save my log for a bug report in General, About
            // Then cx-report.log appears beside cx.log
            // And it holds every line cx.log held at the press
            string d = Fresh();
            var parts = Rig(d, "one", "two");
            Press();
            check.Expect(Read(d, LogReport.FileName) == "one\ntwo\n", $"R1 the press saves cx.log as it stands: {Read(d, LogReport.FileName) ?? "no file"}");

            // Given the player pressed Save my log for a bug report
            // When the game goes on and cx.log gets more lines
            // Then cx-report.log stays exactly as it was at the press
            parts.Write("three");
            parts.Flush();
            check.Expect(Read(d, "cx.log") == "one\ntwo\nthree\n" && Read(d, LogReport.FileName) == "one\ntwo\n",
                         $"R2 cx-report.log stays still while cx.log goes on: {Read(d, LogReport.FileName) ?? "no file"}");
            parts.Close();

            // Given the player pressed Save my log for a bug report
            // When they paste
            // Then they paste the full path of cx-report.log
            d = Fresh();
            parts = Rig(d, "one");
            Press();
            check.Expect(copied == Path.GetFullPath(Path.Combine(d, LogReport.FileName)), $"R3 the clipboard holds the full path: {copied ?? "nothing"}");
            parts.Close();

            // Given a cx-report.log from an earlier press
            // When the player presses Save my log for a bug report again
            // Then cx-report.log holds cx.log as it is at the new press, and nothing of the earlier copy is added twice
            d = Fresh();
            parts = Rig(d, "one");
            Press();
            parts.Write("two");
            parts.Flush();
            Press();
            check.Expect(Read(d, LogReport.FileName) == "one\ntwo\n", $"R4 a second press replaces the copy: {Read(d, LogReport.FileName) ?? "no file"}");
            parts.Close();

            // Given cx.log moved to the game's user folder after the launch began
            // When the player presses Save my log for a bug report
            // Then cx-report.log appears beside cx.log in the folder it moved to
            string from = Fresh(), to = Fresh();
            parts = Rig(from, "early");
            parts.MoveTo(to);
            parts.Write("late");
            parts.Flush();
            Press();
            check.Expect(Read(to, LogReport.FileName) == "early\nlate\n" && Read(from, LogReport.FileName) == null,
                         $"R5 the copy lands where cx.log moved: there={Read(to, LogReport.FileName) ?? "no file"} before={Read(from, LogReport.FileName) ?? "no file"}");
            parts.Close();

            // Given chat commands are on
            // When the player types /about savelog
            // Then cx-report.log is saved and its path copied, the same as pressing the button
            d = Fresh();
            parts = Rig(d, "one");
            bool commands = General.Commands.V;
            General.Commands.Set(true);
            bool ran = CommandRegistry.Global.TryDispatch("/about savelog", out string? reply);
            General.Commands.Set(commands);
            check.Expect(ran && Read(d, LogReport.FileName) == "one\n" && copied == Path.GetFullPath(Path.Combine(d, LogReport.FileName)),
                         $"R6 /about savelog saves and copies: ran={ran} reply={reply} copied={copied ?? "nothing"}");
            parts.Close();

            // Given the settings panel
            // When the player opens General, About
            // Then it shows Copy my settings for a bug report and, under it, Save my log for a bug report
            int copyAt = -1, saveAt = -1, i = 0;
            foreach (Knob k in General.About.Knobs)
            {
                if (k.Tier == Tier.User && k.Label == CopyLabel) copyAt = i;
                if (k.Tier == Tier.User && k.Label == SaveLabel) saveAt = i;
                i++;
            }
            check.Expect(copyAt >= 0 && saveAt == copyAt + 1, $"R7 About shows Save my log right under Copy my settings: copy at {copyAt}, save at {saveAt}");

            // Given the clipboard refuses the copy
            // When the player presses Save my log for a bug report
            // Then cx-report.log is still saved
            // And cx.log names its full path and says the clipboard refused it
            d = Fresh();
            parts = Rig(d, "one");
            clipboardSays = false;
            Press();
            string full = Path.GetFullPath(Path.Combine(d, LogReport.FileName));
            check.Expect(Read(d, LogReport.FileName) == "one\n", "R8 a refused clipboard still saves cx-report.log");
            check.Expect(heard.Exists(l => l.Contains(full, StringComparison.Ordinal) && l.Contains("clipboard refused", StringComparison.Ordinal)),
                         $"R8 cx.log names the path and the refusal: [{string.Join("|", heard)}]");
            parts.Close();
        }
        finally
        {
            LogReport.State = was;
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
        if (check.Ok) log.Info("LogReportApprovedExamples.SelfCheck: passed");
        else log.Info("LogReportApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }
}
