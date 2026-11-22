using CX.Framework;

namespace CX.Rhi;

/// <summary>A command list that outgrows its buffer doubles it, so a long frame grows a handful of times.</summary>
internal static class CommandListGrowthSelfCheck
{
    /// <summary>A hundred draws into a 64-byte list. Oracle: at most 256 bytes a draw is at most 25,600 bytes, and
    /// doubling from 64 passes that in nine growths, where growing by the need alone takes about a hundred.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "CommandList.GrowthSelfCheck");
        var cl = new CommandList("growth self-check", 64);
        for (int i = 0; i < 100; i++) cl.Draw(3);
        check.Expect(cl.Length <= 25600 && cl.Growths <= 9, $"a full list doubles: {cl.Growths} growths for {cl.Length} bytes");
        log.Info(check.Ok ? "CommandList.GrowthSelfCheck: passed" : "CommandList.GrowthSelfCheck: FAILED");
    }
}
