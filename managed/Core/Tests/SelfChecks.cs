using System.Reflection;
using System.Runtime.CompilerServices;
using CX.Framework;
using Xunit;

namespace CX.Core.Tests;

/// <summary>Every self-check suite the core declares, each run as a test of its own; a FAIL line it logs fails it.</summary>
public sealed class SelfChecks
{
    /// <summary>The folder this test run's home and data home point at, so no suite writes beside a player's files.</summary>
    private static readonly string Home = Path.Combine(Path.GetTempPath(), $"cx-core-tests-{Environment.ProcessId}");

    /// <summary>Points HOME and XDG_DATA_HOME at <see cref="Home"/> before any suite reads them.</summary>
    [ModuleInitializer]
    internal static void Isolate()
    {
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable("HOME", Home);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Home, ".local", "share"));
    }

    /// <summary>The full names of the core's suites, found by their attribute as the host finds them.</summary>
    public static TheoryData<string> Suites()
    {
        var data = new TheoryData<string>();
        foreach (MethodInfo m in Methods()) data.Add(Name(m));
        return data;
    }

    private static string Name(MethodInfo m) => m.DeclaringType!.FullName + "." + m.Name;

    private static IEnumerable<MethodInfo> Methods() =>
        typeof(Checker).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.GetCustomAttribute<SelfCheckSuiteAttribute>() != null)
            .OrderBy(Name, StringComparer.Ordinal);

    /// <summary>Runs one suite on the real log, as the host does, and fails with every FAIL line it wrote there.</summary>
    [Theory]
    [MemberData(nameof(Suites))]
    public void Run(string suite)
    {
        MethodInfo m = Methods().Single(x => Name(x) == suite);
        string file = Path.Combine(UserFolders.Logs, "cx.log");
        ExtenderLog.FlushNow();
        long from = File.Exists(file) ? new FileInfo(file).Length : 0;
        object? said = m.Invoke(null, [new ExtenderLog()]);
        ExtenderLog.FlushNow();
        using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        f.Seek(Math.Min(from, f.Length), SeekOrigin.Begin);
        var fails = new StreamReader(f).ReadToEnd().Split('\n').Where(l => l.Contains(" FAIL", StringComparison.Ordinal)).ToList();
        if (fails.Count > 0 || said is false)
            Assert.Fail(fails.Count > 0 ? string.Join("\n", fails) : suite + " returned false");
    }
}
