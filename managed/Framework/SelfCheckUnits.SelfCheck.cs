using System.Reflection;

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

    /// <summary>The units the session check's two hand-built suites ran, in order.</summary>
    private static readonly List<string> Ran = [];

    private static void FastSuite(ExtenderLog _) => Ran.Add("fast");

    private static void SlowSuite(ExtenderLog _) => Ran.Add("slow");

    /// <summary>A live client's boot leaves out a suite marked out of session, the self-check host runs it, and every
    /// mist suite is so marked. Oracle: two hand-built suites, one of each kind, and the mist suites a player's boot timed.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void SessionSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SelfCheckUnits.SessionSelfCheck");
        const BindingFlags Own = BindingFlags.Static | BindingFlags.NonPublic;
        var suites = new List<Suite>
        {
            new("fast", typeof(SelfCheckUnits).GetMethod(nameof(FastSuite), Own)!, InSession: true),
            new("slow", typeof(SelfCheckUnits).GetMethod(nameof(SlowSuite), Own)!, InSession: false),
        };
        Ran.Clear();
        RunAll(suites, log, session: true);
        check.Expect(string.Join(",", Ran) == "fast", $"a live client's boot ran {string.Join(",", Ran)} (want fast alone)");
        Ran.Clear();
        RunAll(suites, log, session: false);
        check.Expect(string.Join(",", Ran) == "fast,slow", $"the self-check host ran {string.Join(",", Ran)} (want fast,slow)");
        string[] timed = ["MistPool", "MistWind", "MistParting", "MistPlacement", "MistBlast", "GroundMistSelfCheck", "MistLook", "MistSwitch"];
        var found = Discover(SuitePhase.BeforeServices).Where(s => timed.Any(t => s.Name.Contains(t, StringComparison.Ordinal))).ToList();
        foreach (string t in timed) check.Expect(found.Any(s => s.Name.Contains(t, StringComparison.Ordinal)), $"no suite named {t} is registered");
        foreach (Suite s in found) check.Expect(!s.InSession, $"{s.Name} runs in a live client's boot (want it left out)");
        log.Info(check.Ok ? "SelfCheckUnits.SessionSelfCheck: passed" : "SelfCheckUnits.SessionSelfCheck: FAILED");
    }
}
