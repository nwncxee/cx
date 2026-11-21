namespace CX.Framework;

/// <summary>General » About's <c>Save my log for a bug report</c>: cx-report.log, a still copy of cx.log as it stands,
/// beside it. A browser refuses to upload a file that changes after it was picked, and cx.log changes every few seconds.</summary>
internal static class LogReport
{
    internal const string FileName = "cx-report.log";

    /// <summary>What a press saves from, copies with and reports to; a self-check swaps in its own for the press.</summary>
    internal sealed class LogReportState
    {
        public Func<LogParts.Saved> Save = ExtenderLog.SaveReport;
        public Func<string, bool> Copy = CX.Ui.SelectableText.Copy;
        public ExtenderLog? Log;
    }

    internal static LogReportState State = new();

    /// <summary>The press: save the copy, put its path on the clipboard, and say in cx.log where it is.</summary>
    internal static void Press()
    {
    }
}
