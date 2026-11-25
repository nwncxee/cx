using CX.Framework;

namespace CX.Services.Update;

/// <summary>A Linux machine with no OpenSSL boots with the update check on: the check asks nothing and says why.
/// Oracle: a loader that refuses or accepts each library by name, and a fetch that counts its calls.</summary>
internal static class UpdateTlsSelfCheck
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UpdateTls.SelfCheck");

        var (asked, lines, tried) = Ask(linux: true, loads: _ => false);
        check.Expect(asked == 0, $"Linux with no libssl: nothing is fetched ({asked})");
        check.Expect(lines.Count == 1 && lines[0].Contains("libssl", StringComparison.Ordinal), $"Linux with no libssl: one line names it ({string.Join(" | ", lines)})");
        check.Expect(tried.Contains("libssl.so.3") && tried.Contains("libssl.so.1.1"), $"both libraries .NET accepts are tried ({string.Join(", ", tried)})");

        (asked, lines, _) = Ask(linux: true, loads: n => n == "libssl.so.3");
        check.Expect(asked == 1 && lines.Count == 0, $"Linux with OpenSSL 3: the check fetches, silently ({asked}, {lines.Count})");

        (asked, lines, _) = Ask(linux: true, loads: n => n == "libssl.so.1.1");
        check.Expect(asked == 1 && lines.Count == 0, $"Linux with OpenSSL 1.1: the check fetches ({asked}, {lines.Count})");

        (asked, _, _) = Ask(linux: true, loads: n => n == "libssl.so.1.0.2");
        check.Expect(asked == 0, $"Linux with only OpenSSL 1.0.2, which .NET refuses: nothing is fetched ({asked})");

        (asked, lines, tried) = Ask(linux: false, loads: _ => false);
        check.Expect(asked == 1 && lines.Count == 0 && tried.Count == 0, $"Windows: its own TLS, no libssl asked for ({asked}, {lines.Count}, {tried.Count})");

        log.Info(check.Ok ? "UpdateTls.SelfCheck: passed" : "UpdateTls.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Builds the source under the given loader and asks it once.</summary>
    private static (int Asked, List<string> Lines, List<string> Tried) Ask(bool linux, Func<string, bool> loads)
    {
        int asked = 0;
        var lines = new List<string>();
        var tried = new List<string>();
        Func<string, byte[]?> src = UpdateService.Source(linux, n => { tried.Add(n); return loads(n); }, _ => { asked++; return null; },
            new ExtenderLog { Tap = lines.Add });
        src("https://example.invalid/");
        return (asked, lines, tried);
    }
}
