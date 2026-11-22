namespace CX.Framework;

public static partial class SelfCheckUnits
{
    /// <summary>The registry finds suites in both assemblies: the core's and the extender's. Oracle: the names of two
    /// hand-picked suites, one declared in each, as their own attributes mark them.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void RegistrySelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SelfCheckUnits.RegistrySelfCheck");
        var names = Discover(SuitePhase.BeforeServices).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        check.Expect(names.Contains("CX.Framework.HexColour.SelfCheck"), "the core's HexColour suite is registered");
        check.Expect(names.Contains("CX.Alloy.RendererContract.PinnedEndSelfCheck"), "the extender's RendererContract suite is registered");
        log.Info(check.Ok ? "SelfCheckUnits.RegistrySelfCheck: passed" : "SelfCheckUnits.RegistrySelfCheck: FAILED");
    }
}
