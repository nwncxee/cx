namespace CX.Framework;

/// <summary>Save my log's edges no approved example names: a press before this launch wrote a line, a line still in the
/// writer's buffer, and a log folder named by a relative path. Oracle: the literals written here and the files' own bytes.</summary>
internal static class LogReportEdges
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LogReportEdges.SelfCheck");
        string root = Path.Combine(Path.GetTempPath(), $"cx-logreport-edges-{Environment.ProcessId}");
        LogReport.LogReportState was = LogReport.State;
        var heard = new List<string>();
        string? copied = null;
        void Rig(LogParts parts)
        {
            heard.Clear();
            copied = null;
            LogReport.State = new LogReport.LogReportState
            {
                Save = parts.SaveReport,
                Copy = s => { copied = s; return true; },
                Log = new ExtenderLog { Tap = heard.Add },
            };
        }
        static string? Read(string p)
        {
            if (!File.Exists(p)) return null;
            using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(f).ReadToEnd();
        }

        try
        {
            string d = Path.Combine(root, "before");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "cx.log"), "last launch\n");
            var parts = new LogParts(d);
            Rig(parts);
            LogReport.Press();
            check.Expect(Read(Path.Combine(d, LogReport.FileName)) == null && copied == null,
                         $"a press before this launch wrote a line saves nothing and copies nothing: copied={copied ?? "nothing"}");
            check.Expect(heard.Exists(l => l.Contains("was not saved", StringComparison.Ordinal)),
                         $"a press with nothing to save says so: [{string.Join("|", heard)}]");

            d = Path.Combine(root, "buffered");
            parts = new LogParts(d);
            parts.Write("held in the buffer");
            Rig(parts);
            LogReport.Press();
            check.Expect(Read(Path.Combine(d, LogReport.FileName)) == "held in the buffer\n",
                         $"a line not yet flushed is in the copy: {Read(Path.Combine(d, LogReport.FileName)) ?? "no file"}");
            parts.Close();

            d = Path.Combine(root, "relative");
            parts = new LogParts(Path.GetRelativePath(Environment.CurrentDirectory, d));
            parts.Write("one");
            Rig(parts);
            LogReport.Press();
            check.Expect(copied == Path.Combine(d, LogReport.FileName), $"a relative log folder still copies the full path: {copied ?? "nothing"}");
            parts.Close();
        }
        finally
        {
            LogReport.State = was;
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
        if (check.Ok) log.Info("LogReportEdges.SelfCheck: passed");
        else log.Info("LogReportEdges.SelfCheck: FAILED");
        return check.Ok;
    }
}
