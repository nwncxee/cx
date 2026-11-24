using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace CX.Framework;

/// <summary>The units a boot runs, each a registered suite or one service's <c>Initialize</c>, and the one seam
/// the mutant runner's overlay fills to boot only some of them. Without the overlay every unit runs.</summary>
public static partial class SelfCheckUnits
{
    /// <summary>The unit running now, or null between units; the runner's switch reads it to learn each mutant's reach.</summary>
    public static string? Current;

    /// <summary>Filled only by the mutant runner's overlay: clears <paramref name="run"/> for a unit its boot leaves out.</summary>
    static partial void Admit(string unit, ref bool run);

    /// <summary>Whether this boot runs <paramref name="unit"/>.</summary>
    public static bool Admits(string unit)
    {
        bool run = true;
        Admit(unit, ref run);
        return run;
    }

    /// <summary>Runs <paramref name="body"/> as <paramref name="unit"/>; the caller has asked <see cref="Admits"/>.</summary>
    public static void Run(string unit, Action body)
    {
        Current = unit;
        try { body(); }
        finally { Current = null; }
    }

    /// <summary>A registered suite: its unit name, its method, and whether a live client's boot runs it.</summary>
    public readonly record struct Suite(string Name, MethodInfo Method, bool InSession = true);

    /// <summary>Every method marked <see cref="SelfCheckSuiteAttribute"/> for <paramref name="phase"/>, by name, since
    /// <c>GetTypes()</c> order is unstable.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "cx_managed.csproj roots this assembly (TrimmerRootAssembly), so no type is trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "cx_managed.csproj roots this assembly (TrimmerRootAssembly), so no method is trimmed.")]
    public static List<Suite> Discover(SuitePhase phase) =>
        new[] { Assembly.GetExecutingAssembly(), typeof(Checker).Assembly }.Distinct().SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(m => (m, a: m.GetCustomAttribute<SelfCheckSuiteAttribute>()))
            .Where(x => x.a?.Phase == phase)
            .Select(x => new Suite(x.m.DeclaringType!.FullName!.Replace('+', '.') + "." + x.m.Name, x.m, x.a!.InSession))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Runs each of <paramref name="suites"/> this boot admits, as its own unit; a live client's boot
    /// (<paramref name="session"/>) skips each suite marked out of session.</summary>
    public static void RunAll(List<Suite> suites, ExtenderLog log, bool session)
    {
        foreach (Suite s in suites)
        {
            if (!Admits(s.Name)) continue;
            if (session && !s.InSession) { new Checker(log, s.Name).Skip("in a session: too slow for a player's boot; the self-check host runs it"); continue; }
            Run(s.Name, () => s.Method.Invoke(null, [log]));
        }
    }
}
