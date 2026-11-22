using System;
using CX.Framework;

namespace CX.Alloy;

public sealed partial class AlloyRenderer
{
    /// <summary>The MiB a frame line gives after <paramref name="clause"/> ("in system RAM", "held by idle models"),
    /// read as "N MiB" or "NMiB"; null when the line has no such clause.</summary>
    internal static long? ClauseMiB(string line, string clause)
    {
        int at = line.IndexOf(clause + " ", StringComparison.Ordinal);
        if (at < 0) return null;
        int i = at + clause.Length + 1, start = i;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i == start) return null;
        int unit = i < line.Length && line[i] == ' ' ? i + 1 : i;
        if (!line.AsSpan(unit).StartsWith("MiB", StringComparison.Ordinal)) return null;
        return long.Parse(line.AsSpan(start, i - start), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The count a frame line gives after <paramref name="clause"/>, read as "clause N" with no unit after
    /// it; null when the line has no such clause.</summary>
    internal static long? ClauseCount(string line, string clause)
    {
        int at = line.IndexOf(clause + " ", StringComparison.Ordinal);
        if (at < 0) return null;
        int i = at + clause.Length + 1, start = i;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i == start || (i < line.Length && char.IsAsciiLetter(line[i]))) return null;
        return long.Parse(line.AsSpan(start, i - start), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Approved-spec: the frame line's two new memory clauses stay off a line no frame has read the device for.
    /// Oracles: hand-written device and system-RAM figures in MiB, and a line with a budget, through the same writer, as
    /// the control.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void MemoryClausesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.MemoryClausesSelfCheck");
        NoDeviceReadNoClauses(check);
        log.Info(check.Ok ? "AlloyRenderer.MemoryClausesSelfCheck: passed" : "AlloyRenderer.MemoryClausesSelfCheck: FAILED");
    }

    // Given no frame has read the device
    // When the frame line is written
    // Then neither in system RAM nor held by idle models appears
    private static void NoDeviceReadNoClauses(Checker check)
    {
        const long MiB = 1 << 20;
        (string, long)[] top = [("golden vram first", 48 * MiB)];
        string read = FrameVram(900 * MiB, 2000 * MiB, 8000 * MiB, 96 * MiB, top);
        check.Expect(ClauseMiB(read, "in system RAM") == 96,
                     $"control: a line read off a device with 96MiB in system RAM does not show in system RAM 96 MiB: \"{read}\"");
        string unread = FrameVram(900 * MiB, 0, 0, 96 * MiB, top);
        check.Expect(!unread.Contains("in system RAM", StringComparison.Ordinal) && !unread.Contains("held by idle models", StringComparison.Ordinal),
                     $"no frame has read the device, yet the line shows a memory clause: \"{unread}\"");
    }
}
