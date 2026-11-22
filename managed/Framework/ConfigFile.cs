using System;
using System.IO;

namespace CX.Framework;

/// <summary>
/// The CX <c>*.cfg</c> read helpers: the pre-main deferral rule (<see cref="Ready"/>), the <c>sowx</c>-stem read
/// fallback (<see cref="ReadPath"/>), the ungated read (<see cref="ReadTextNow"/>) and the flat <c>key=value</c>
/// walk (<see cref="ForEachPair"/>). Our loader runs BEFORE the engine's entry point, when
/// <see cref="EngineLink.UserDirectory"/> is still null, so no resolved path is cached: every read resolves per use.
/// </summary>
public static class ConfigFile
{
    /// <summary>True when the engine user dir is resolvable, i.e. when a deferred config read may latch. The
    /// one expression of the pre-main rule (HotkeyStore's sectioned cfg).</summary>
    public static bool Ready => EngineLink.UserDirectory() != null;

    /// <summary>Read a cfg's text WITHOUT the deferral gate, "" when absent or unreadable; <paramref name="path"/>
    /// is the file consulted (<see cref="ReadPath"/>). Only for a decision that must be made pre-main (the boot
    /// settings).</summary>
    public static string ReadTextNow(string fileName, ExtenderLog? log, out string path)
    {
        path = ReadPath(fileName, log);
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception e) { log?.Info($"config: {fileName} unreadable -- {e.Message}"); return ""; }
    }

    /// <summary>The stem every cfg carried before the CX rename; a pre-rename install has its files under it.</summary>
    private const string LegacyStem = "sowx";

    /// <summary>The path a READ takes: the <c>cx</c> file when it exists, else the <c>sowx</c>-stem
    /// sibling a pre-rename install left, else the <c>cx</c> path (absent). Saves never come here, so the
    /// first write lands under the new stem and the legacy file stops being consulted -- one read-fallback,
    /// not a copy step.</summary>
    public static string ReadPath(string fileName, ExtenderLog? log)
    {
        string p = EngineLink.ConfigPath(fileName);
        if (File.Exists(p) || !fileName.StartsWith("cx", StringComparison.Ordinal)) return p;
        string legacy = EngineLink.ConfigPath(LegacyStem + fileName.Substring(2));
        if (!File.Exists(legacy)) return p;
        log?.Always($"config: {fileName} absent, reading pre-rename {legacy} (#530; the next save writes {fileName})");
        return legacy;
    }

    /// <summary>
    /// Walk a flat <c>key=value</c> file: per line, trim, split at the FIRST <c>=</c>, trim both halves, hand
    /// them to <paramref name="onPair"/>. Lines with no <c>=</c>, an empty key, or nothing but whitespace are
    /// skipped -- malformed input degrades to "fewer keys applied", never an exception.
    /// </summary>
    public static void ForEachPair(string text, Action<string, string> onPair)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq < 1) continue;   // -1 = no separator, 0 = empty key; both malformed
            onPair(line.Substring(0, eq).TrimEnd(), line.Substring(eq + 1).TrimStart());
        }
    }

    /// <summary>Off-device proof of the line walker's degrade behaviour.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var seen = new System.Collections.Generic.List<(string, string)>();
        ForEachPair("a=1\nb=two\n  c  =  three  \n", (k, v) => seen.Add((k, v)));
        check.Expect(seen.Count == 3, "well-formed file yields one pair per line");
        check.Expect(seen.Count == 3 && seen[0] == ("a", "1") && seen[1] == ("b", "two"), "keys and values round-trip");
        check.Expect(seen.Count == 3 && seen[2] == ("c", "three"), "surrounding whitespace trimmed from key and value");

        seen.Clear();
        ForEachPair("nosep\n=leadingeq\n\n   \nk=v\r\nempty=\nx=a=b\n", (k, v) => seen.Add((k, v)));
        check.Expect(seen.Count == 3, "malformed lines skipped, not thrown on");
        check.Expect(seen.Count == 3 && seen[0] == ("k", "v"), "CR stripped alongside the newline");
        check.Expect(seen.Count == 3 && seen[1] == ("empty", ""), "empty value is a legal pair");
        check.Expect(seen.Count == 3 && seen[2] == ("x", "a=b"), "split at the FIRST separator only");

        ForEachPair("", (_, _) => check.Expect(false, "empty text yields no pairs"));

        log.Info(check.Ok ? "ConfigFile.SelfCheck: passed" : "ConfigFile.SelfCheck: FAILED");
    }
}
