using System;
using System.Collections.Generic;
using System.IO;

namespace CX.Framework;

/// <summary>
/// The boot read of <c>cx.cfg</c> (ADR 0025): one flat <c>key=value</c> file, read once pre-main, that every
/// <see cref="Knob{T}"/> binds its saved value from and <see cref="KnobFile"/> writes back. The PATH is latched
/// here, not lazily: <see cref="EngineLink.ConfigPath"/> answers differently before and after the engine exists and
/// boot knobs are read at both moments, so <see cref="Load"/> runs once, pre-main, beside <c>nwn.ini</c>.
/// </summary>
public static class Settings
{
    /// <summary>The one settings file (ADR 0025): read here once, pre-main; written back by
    /// <see cref="KnobFile"/> from the knob registry.</summary>
    public const string FileName = "cx.cfg";

    private static readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private static string _path = "";

    /// <summary>The file <see cref="Load"/> actually read, resolved at that one pre-main moment. Reported
    /// rather than re-resolved: <see cref="EngineLink.ConfigPath"/> answers differently once the engine exists,
    /// so asking it again at command time would name a file nothing reads.</summary>
    public static string Path => _path;

    /// <summary>True once <see cref="Load"/> has run. A read before that gets defaults, which is a wiring
    /// bug rather than a user error -- hence the log line.</summary>
    public static bool Loaded { get; private set; }

    /// <summary>Read the file. Once, from the host, before any service is constructed. Missing or unreadable
    /// file means "every default stands", and a missing one is created by the next <see cref="KnobFile"/> write;
    /// a malformed line is skipped, per <see cref="ConfigFile.ForEachPair"/>.</summary>
    public static void Load(ExtenderLog? log)
    {
        if (Loaded) return;
        Loaded = true;
        // ReadTextNow, ungated: waiting for the engine user dir here would mean the capability gates never
        // see their own settings.
        Read(ConfigFile.ReadTextNow(FileName, log, out _path));
        if (_values.Count > 0) log?.Info($"settings: {_values.Count} key(s) from {_path}");
        // On-device only: off-device the path is the real user's, and the demo never writes it.
        if (!File.Exists(_path) && EngineLink.InClient()) KnobFile.ArmFirstRun();
    }

    /// <summary>Keys an older CX wrote for a setting that is gone: dropped on read, so the next write leaves them out.</summary>
    private static readonly string[] RetiredKeys = ["general.logs", "walls.margin", "walls.armin", "walls.armout", "walls.armradius", "walls.fade", "walls.floor", "walls.radius"];

    /// <summary>Take every pair of <paramref name="text"/>, in <c>cx.cfg</c>'s own format, into the loaded table,
    /// less the <see cref="RetiredKeys"/>.</summary>
    internal static void Read(string text) => ConfigFile.ForEachPair(text, (k, v) =>
    {
        if (Array.FindIndex(RetiredKeys, r => string.Equals(r, k, StringComparison.OrdinalIgnoreCase)) < 0) _values[k] = v;
    });

    /// <summary>Drop <paramref name="key"/> from the loaded table: a self-check takes back what it fed <see cref="Read"/>.</summary>
    internal static void Forget(string key) => _values.Remove(key);

    /// <summary>One key straight from the file, before <see cref="Load"/>: what the host's first line reads.</summary>
    public static string? Peek(string key)
    {
        string? found = null;
        ConfigFile.ForEachPair(ConfigFile.ReadTextNow(FileName, null, out _), (k, v) =>
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) found = v;
        });
        return found;
    }

    /// <summary>The value as loaded, or null when unset. The knob registry's bind: a <see cref="Knob{T}"/>
    /// parses this itself.</summary>
    public static string? Raw(string key) => _values.TryGetValue(key, out string? v) ? v : null;

    /// <summary>Every loaded pair, verbatim: what <see cref="KnobFile"/> preserves when no declaration
    /// claims the key.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Pairs => _values;

    /// <summary>Every set key starting with <paramref name="prefix"/> (case-folded), with its value RAW --
    /// not lowercased, because the values are paths.</summary>
    public static List<(string Key, string Value)> Prefixed(string prefix)
    {
        var r = new List<(string, string)>();
        foreach (var kv in _values)
            if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && kv.Value.Length > 0)
                r.Add((kv.Key.ToLowerInvariant(), kv.Value));
        r.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return r;
    }

    /// <summary>The prefixed read, off-device. Runs against the live table, so it uses keys no real setting
    /// claims and leaves none behind.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        _values["cx-selfcheck-pfx.Mixed"] = "/Case/Kept.ttf";
        var pf = Prefixed("cx-selfcheck-pfx.");
        check.Expect(pf.Count == 1 && pf[0].Key == "cx-selfcheck-pfx.mixed" && pf[0].Value == "/Case/Kept.ttf",
            "a prefixed read keeps the value's case and folds the key");
        _values.Remove("cx-selfcheck-pfx.Mixed");
        check.Expect(Raw("cx-selfcheck-pfx.Mixed") == null, "the check leaves no probe behind");

        log.Info(check.Ok ? "Settings.SelfCheck: passed" : "Settings.SelfCheck: FAILED");
    }
}
