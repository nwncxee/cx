namespace CX.Framework;

/// <summary>The logs an older CX left beside nwn.ini and in the home folder go away when the folders settle, and only
/// those: against a user directory and a home folder of the check's own. Oracle: the file names and lines written here,
/// and the files' own bytes.</summary>
internal static class StaleLogsApprovedExamples
{
    /// <summary>A line as an older CX wrote it: its timestamp, then the <c>[cx]</c> tag.</summary>
    private const string OldLine = "2026-11-01T20:15:00.0000000+01:00 [cx] host: an older launch\n";

    private static readonly string[] Parts = ["cx.1.log", "cx.2.log", "cx.3.log", "cx.4.log"];

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "StaleLogsApprovedExamples.SelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) return check.Skip("on-device: the check settles the folders to its own and a game session has its real ones");
        if (OperatingSystem.IsWindows()) return check.Skip("the stub host's throwaway home is Linux's; Windows is checked live");
        string root = Path.Combine(Path.GetTempPath(), $"cx-stalelogs-{Environment.ProcessId}");
        int case_ = 0;
        string Fresh(string name)
        {
            string d = Path.Combine(root, (++case_).ToString(), name);
            Directory.CreateDirectory(d);
            return d;
        }
        try
        {
            // Given a cx.log an older CX wrote beside nwn.ini, or in the home folder on Linux
            // When the player starts the game
            // Then that old cx.log is gone, and the cx.log in the logs folder holds this launch
            string ud = Fresh("Neverwinter Nights"), home = Fresh("home");
            File.WriteAllText(Path.Combine(ud, "cx.log"), OldLine);
            File.WriteAllText(Path.Combine(home, "cx.log"), OldLine);
            var parts = new LogParts(Path.Combine(ud, "logs"));
            parts.Write("launch line");
            UserFolders.Settle(UserFoldersApprovedExamples.Aliases(ud), ud, Path.Combine(ud, "logs"), log, parts.MoveTo, home);
            parts.Write("menu line");
            parts.Close();
            check.Expect(!File.Exists(Path.Combine(ud, "cx.log")), "S1 the old cx.log beside nwn.ini is gone");
            check.Expect(!File.Exists(Path.Combine(home, "cx.log")), "S1 the old cx.log in the home folder is gone");
            string? now = UserFoldersApprovedExamples.Read(Path.Combine(ud, "logs", "cx.log"));
            check.Expect(now == "launch line\nmenu line\n", $"S1 the cx.log in the logs folder holds this launch: {now}");
            UserFolders.Reset();

            // Given cx.1.log to cx.4.log an older CX left in the same place
            // When the player starts the game
            // Then they are gone too
            ud = Fresh("Neverwinter Nights"); home = Fresh("home");
            foreach (string p in Parts) { File.WriteAllText(Path.Combine(ud, p), OldLine); File.WriteAllText(Path.Combine(home, p), OldLine); }
            UserFolders.Settle(UserFoldersApprovedExamples.Aliases(ud), ud, Path.Combine(ud, "logs"), log, home: home);
            foreach (string p in Parts)
            {
                check.Expect(!File.Exists(Path.Combine(ud, p)), $"S2 the old {p} beside nwn.ini is gone");
                check.Expect(!File.Exists(Path.Combine(home, p)), $"S2 the old {p} in the home folder is gone");
            }
            UserFolders.Reset();

            // Given a funchook.log an older CX wrote beside nwn.ini
            // When the player starts the game
            // Then it is gone, and only the logs folder has a funchook.log
            ud = Fresh("Neverwinter Nights"); home = Fresh("home");
            File.WriteAllText(Path.Combine(ud, "funchook.log"), "[funchook] an older launch's trace\n");
            Directory.CreateDirectory(Path.Combine(ud, "logs"));
            File.WriteAllText(Path.Combine(ud, "logs", "funchook.log"), "[funchook] this launch's trace\n");
            UserFolders.Settle(UserFoldersApprovedExamples.Aliases(ud), ud, Path.Combine(ud, "logs"), log, home: home);
            check.Expect(!File.Exists(Path.Combine(ud, "funchook.log")), "S3 the old funchook.log beside nwn.ini is gone");
            check.Expect(UserFoldersApprovedExamples.Read(Path.Combine(ud, "logs", "funchook.log")) == "[funchook] this launch's trace\n",
                "S3 the logs folder keeps this launch's funchook.log");
            UserFolders.Reset();

            // Given old logs an older CX left
            // When the player starts the game
            // Then cx.log says once which old logs it removed, and from where
            ud = Fresh("Neverwinter Nights"); home = Fresh("home");
            File.WriteAllText(Path.Combine(ud, "cx.log"), OldLine);
            File.WriteAllText(Path.Combine(ud, "funchook.log"), "[funchook] an older launch's trace\n");
            File.WriteAllText(Path.Combine(home, "cx.1.log"), OldLine);
            var heard = new List<string>();
            UserFolders.Settle(UserFoldersApprovedExamples.Aliases(ud), ud, Path.Combine(ud, "logs"), new ExtenderLog { Tap = heard.Add }, home: home);
            string[] named = [Path.Combine(ud, "cx.log"), Path.Combine(ud, "funchook.log"), Path.Combine(home, "cx.1.log")];
            int said = heard.Count(l => named.All(n => l.Contains(n, StringComparison.Ordinal)));
            int any = heard.Count(l => named.Any(n => l.Contains(n, StringComparison.Ordinal)));
            check.Expect(said == 1 && any == 1, $"S4 cx.log names each removed old log with its folder once, not {said} of {any} lines: {string.Join(" | ", heard)}");
            UserFolders.Reset();

            // Given nwn.ini's [Alias] sets LOGS to the folder beside nwn.ini itself
            // When the player starts the game
            // Then cx.log and its parts there are this launch's and the earlier launches', and none is removed
            ud = Fresh("Neverwinter Nights"); home = Fresh("home");
            File.WriteAllText(Path.Combine(ud, "cx.log"), OldLine);
            File.WriteAllText(Path.Combine(ud, "cx.1.log"), OldLine);
            File.WriteAllText(Path.Combine(ud, "funchook.log"), "[funchook] this launch's trace\n");
            UserFolders.Settle(UserFoldersApprovedExamples.Aliases(ud, ("LOGS", ud)), ud, Path.Combine(ud, "logs"), log, home: home);
            check.Expect(UserFoldersApprovedExamples.Read(Path.Combine(ud, "cx.log")) == OldLine, "S5 with LOGS beside nwn.ini, cx.log there stays");
            check.Expect(UserFoldersApprovedExamples.Read(Path.Combine(ud, "cx.1.log")) == OldLine, "S5 with LOGS beside nwn.ini, cx.1.log there stays");
            check.Expect(File.Exists(Path.Combine(ud, "funchook.log")), "with LOGS beside nwn.ini, this launch's funchook.log there stays");
            UserFolders.Reset();

            // Given a file named cx.log in the home folder whose first line is not a CX log line
            // When the player starts the game
            // Then it stays where it is
            ud = Fresh("Neverwinter Nights"); home = Fresh("home");
            File.WriteAllText(Path.Combine(home, "cx.log"), "my own notes\n" + OldLine);
            File.WriteAllText(Path.Combine(ud, "cx.log"), OldLine);
            UserFolders.Settle(UserFoldersApprovedExamples.Aliases(ud), ud, Path.Combine(ud, "logs"), log, home: home);
            check.Expect(UserFoldersApprovedExamples.Read(Path.Combine(home, "cx.log")) == "my own notes\n" + OldLine, "S6 a cx.log CX did not write stays");
            check.Expect(!File.Exists(Path.Combine(ud, "cx.log")), "S6 while the old one beside nwn.ini still goes");
        }
        finally
        {
            UserFolders.Reset();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        log.Info(check.Ok ? "StaleLogsApprovedExamples.SelfCheck: passed" : "StaleLogsApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }
}
