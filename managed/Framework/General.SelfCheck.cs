namespace CX.Framework;

public static partial class General
{
    /// <summary>/settings is registered whatever general.commands says. Oracle: the command's literal name.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "General.SelfCheck");
        check.Expect(CommandRegistry.Global.Knows("/settings"), "/settings dispatches regardless of general.commands");
        log.Info(check.Ok ? "General.SelfCheck: passed" : "General.SelfCheck: FAILED");
    }
}
