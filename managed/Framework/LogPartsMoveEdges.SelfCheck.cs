namespace CX.Framework;

/// <summary>A log move whose new folder cannot be made goes on in the old file: no line lost, and this launch's own
/// cx.log not rotated into cx.1.log. Approved example E22, two clients moving into one LOGS folder, held when it was
/// written and is a regression test here. Oracle: the literals written here and the files' own bytes.</summary>
internal static class LogPartsMoveEdges
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LogPartsMoveEdges.SelfCheck");
        string root = Path.Combine(Path.GetTempPath(), $"cx-logmove-{Environment.ProcessId}");
        try
        {
            string logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "a-file"), "");
            var parts = new LogParts(logs);
            parts.Write("before");
            parts.MoveTo(Path.Combine(root, "a-file", "logs"));
            parts.Write("after");
            parts.Close();
            check.Expect(UserFoldersApprovedExamples.Read(Path.Combine(logs, "cx.log")) == "before\nafter\n",
                $"a refused move keeps writing the same cx.log: {UserFoldersApprovedExamples.Read(Path.Combine(logs, "cx.log"))}");
            check.Expect(!File.Exists(Path.Combine(logs, "cx.1.log")), "a refused move does not rotate this launch's own log");

            // Given two clients running at once with LOGS set to D:\NWNLogs
            // When both reach the main menu
            // Then each launch's lines are in a cx.log part of their own, with no line of one inside the other's
            string shared = Path.Combine(root, "NWNLogs");
            var one = new LogParts(Path.Combine(root, "one", "logs"));
            var two = new LogParts(Path.Combine(root, "two", "logs"));
            one.Write("one: launch");
            two.Write("two: launch");
            one.MoveTo(shared);
            one.Write("one: menu");
            two.MoveTo(shared);
            two.Write("two: menu");
            one.Write("one: later");
            one.Close(); two.Close();
            string[] got = Directory.GetFiles(shared, "cx*.log").Select(f => UserFoldersApprovedExamples.Read(f) ?? "").ToArray();
            bool apart = got.All(t => !(t.Contains("one:") && t.Contains("two:")));
            bool whole = got.Any(t => t == "one: launch\none: menu\none: later\n") && got.Any(t => t == "two: launch\ntwo: menu\n");
            check.Expect(apart && whole, $"E22 each client's launch is whole in a part of its own: {string.Join(" | ", got)}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        log.Info(check.Ok ? "LogPartsMoveEdges.SelfCheck: passed" : "LogPartsMoveEdges.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
