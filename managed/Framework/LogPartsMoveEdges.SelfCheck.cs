namespace CX.Framework;

/// <summary>A log move whose new folder cannot be made goes on in the old file: no line lost, and this launch's own
/// cx.log not rotated into cx.1.log. Oracle: the literals written here and the file's own bytes.</summary>
internal static class LogPartsMoveEdges
{
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
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        log.Info(check.Ok ? "LogPartsMoveEdges.SelfCheck: passed" : "LogPartsMoveEdges.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
