using System;
using System.IO;

namespace CX.Services.Anim;

/// <summary>The command line of <c>tools/anim/replay.sh</c>: <c>&lt;recording&gt;</c> prints each body's grades,
/// <c>--fixture &lt;out&gt;</c> writes the hand-checked recording the approved example replays. Public so the console
/// project can call it; everything it reaches is the engine-free leg core.</summary>
public static class LegReplayTool
{
    /// <summary>Runs the command <paramref name="args"/>; the exit code.</summary>
    public static int Run(string[] args, TextWriter to, TextWriter err)
    {
        if (args.Length == 1 && args[0] != "--fixture") return LegReplay.Run(args[0], to, err) ? 0 : 1;
        if (args.Length == 2 && args[0] == "--fixture")
        {
            try
            {
                using var file = new FileStream(args[1], FileMode.Create, FileAccess.Write);
                LegReplayFixture.Write(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { err.WriteLine($"replay: {e.Message}"); return 1; }
            return 0;
        }
        err.WriteLine("usage: replay.sh <recording> | replay.sh --fixture <out>");
        return 2;
    }
}
