using System.Runtime.InteropServices;

namespace CX.Framework;

/// <summary>The engine string cap's edges no approved example names: a NUL-terminated text past the cap, and a log
/// attached again hearing the next cut. Oracle: the literal 10 MiB length and the bytes written here.</summary>
internal static unsafe class NativeCapEdges
{
    private const int TenMiB = 10 * 1024 * 1024;

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "NativeCapEdges.SelfCheck");
        var first = new List<string>();
        var second = new List<string>();
        byte* buf = (byte*)NativeMemory.Alloc(TenMiB + 2);
        try
        {
            NativeMemory.Fill(buf, TenMiB + 1, (byte)'x');
            buf[TenMiB + 1] = 0;

            Native.Attach(new ExtenderLog { Tap = first.Add });
            int n = Native.Chars(buf).Length;
            check.Expect(n == TenMiB && first.Count == 1, $"a NUL-terminated text past 10 MiB is cut at 10 MiB and logged (length {n}, lines {first.Count})");

            Native.Attach(new ExtenderLog { Tap = second.Add });
            Native.Chars(buf);
            check.Expect(second.Count == 1, $"a log attached again hears the next cut (lines {second.Count})");
        }
        finally
        {
            NativeMemory.Free(buf);
            Native.Attach(log);
        }
        log.Info(check.Ok ? "NativeCapEdges.SelfCheck: passed" : "NativeCapEdges.SelfCheck: FAILED");
    }
}
