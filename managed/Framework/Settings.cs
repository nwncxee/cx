using System;
using System.Collections.Generic;
using System.IO;

namespace CX.Framework;

/// <summary>
/// The boot read of <c>cx.cfg</c> (ADR 0025): one flat <c>key=value</c> file, read once pre-main, that every
/// <see cref="Knob{T}"/> binds its saved value from and <see cref="KnobFile"/> writes back. The PATH is latched
/// here, not lazily: <see cref="Native.ConfigPath"/> answers differently before and after the engine exists and
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
    /// rather than re-resolved: <see cref="Native.ConfigPath"/> answers differently once the engine exists,
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
        ConfigFile.ForEachPair(ConfigFile.ReadTextNow(FileName, log, out _path), (k, v) => _values[k] = v);
        if (_values.Count > 0) log?.Info($"settings: {_values.Count} key(s) from {_path}");
        // On-device only: off-device the path is the real user's, and the demo never writes it.
        if (!File.Exists(_path) && Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) KnobFile.Arm();
        // Off-device (run-demo: no engine symbols resolve) the log stays open -- it is the self-check gate.
        // general.logs is General.Logs's key, read here before any knob exists (spec §2): the launch lines.
        bool logs = Raw("general.logs") is string raw && KnobCodec<bool>.Parse!(raw) is (true, true)
                    || Native.Sym(SymbolMap.AppManager) == IntPtr.Zero;
        if (!logs) log?.Info($"settings: general.logs=0 -- silent from here; set general.logs=1 in {_path} (or /general logs 1) for diagnostics");
        ExtenderLog.Enabled = logs;
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
