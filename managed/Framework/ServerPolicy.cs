using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CX.Framework;

/// <summary>What a UI action does when the player takes it: the engine's own path, nothing, or a
/// <see cref="ServerPolicy.EventMinor"/> message the server answers.</summary>
public enum PolicyRoute { Engine, Deny, Server }

/// <summary>
/// The session's server policy (<c>docs/ui-policy-contract.md</c>): the server sends <c>key=value</c> lines, a key
/// naming a declared knob HOLDS it (<see cref="Knob.Enforce"/>), and any other key is a component property a window
/// reads through <see cref="Bool"/> / <see cref="Float"/> / <see cref="Str"/>. All of it is session state: it dies
/// at the next login and never reaches <c>cx.cfg</c>.
/// </summary>
public static class ServerPolicy
{
    /// <summary>The CX device major (the hotkeys family's; CLAUDE.md: reuse an opcode).</summary>
    public const byte DeviceMajor = 0x36;   // not-an-offset: a message opcode
    /// <summary>Server to client: the policy lines.</summary>
    public const byte PolicyMinor = 0x47;   // not-an-offset: a message opcode
    /// <summary>Client to server: an action routed <see cref="PolicyRoute.Server"/>, <c>action;arg;arg</c>.</summary>
    public const byte EventMinor = 0x48;    // not-an-offset: a message opcode
    /// <summary>Bounds one message's line count; a pathological payload is refused whole.</summary>
    public const int MaxLines = 512;
    /// <summary>The most component properties held at once; a new key past it is refused.</summary>
    public const int MaxProps = 4096;

    private static readonly Dictionary<string, string> _props = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Knob> _held = new();
    private static readonly List<string> _holdable = new();
    private static readonly object _lock = new();
    private static int _epoch;
    private static readonly Dictionary<string, int> _stamp = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(string Prefix, Func<string, string, bool> Set, Action<string> Release, Func<string> Report)> _handlers = new();

    /// <summary>Bumps on every change; a window re-reads its properties when it moves (a field compare per frame).</summary>
    public static int Epoch => _epoch;
    /// <summary>Refusals and applications go here.</summary>
    public static ExtenderLog? Log;
    /// <summary>The outbound half, set by the service owning the wire: <c>(minor, ascii)</c>. Null off-session.</summary>
    public static Action<byte, string>? Send;

    /// <summary>Let a server hold the knobs under <paramref name="prefix"/> (<c>inventory.</c>). A feature opts in; every
    /// other knob -- the renderer, audio, the hotkeys consent -- stays the player's alone.</summary>
    public static void AllowKnobs(string prefix)
    {
        lock (_lock) if (!_holdable.Contains(prefix)) _holdable.Add(prefix);
    }

    /// <summary>Hands every key under <paramref name="prefix"/> to a feature that parses its own values (<c>card.</c>). <paramref name="set"/>
    /// gets the rest of the key and the value, an empty value meaning release, and returns false when it refused; <paramref name="release"/>
    /// gets the prefix of a wildcard release or a new session; <paramref name="report"/> is one line of <c>/policy</c>.</summary>
    public static void Handle(string prefix, Func<string, string, bool> set, Action<string> release, Func<string> report)
    {
        lock (_lock) { _handlers.RemoveAll(h => h.Prefix == prefix); _handlers.Add((prefix, set, release, report)); }
    }

    private static bool Holdable(string key)
    {
        foreach (string p in _holdable) if (key.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Apply one policy message: lines of <c>key=value</c>. An empty value releases the key, <c>prefix.*=</c>
    /// releases every key under the prefix, <c>*=</c> releases everything. Returns the lines applied.</summary>
    public static int Apply(string payload)
    {
        string[] lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > MaxLines) { Refuse(RefusalKind.Line, "(whole message)", $"{lines.Length} lines", $"more than {MaxLines} lines"); return 0; }
        int n = 0;
        foreach (string line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) { Refuse(RefusalKind.Line, line, "", "no '=' in the line"); continue; }
            if (Set(line[..eq].Trim(), line[(eq + 1)..].Trim())) n++;
        }
        Log?.Info($"ServerPolicy: applied {n}/{lines.Length} line(s); {Count} key(s) held");
        return n;
    }

    /// <summary>One key. False when a knob refused the value.</summary>
    public static bool Set(string key, string value)
    {
        if (key == "*" || key.EndsWith(".*", StringComparison.Ordinal))
        {
            ReleasePrefix(key == "*" ? "" : key[..^1]);
            return true;
        }
        foreach (var h in _handlers)
            if (key.StartsWith(h.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                bool ok = h.Set(key[h.Prefix.Length..], value);
                if (ok) PolicyRefusals.Forget(key);
                return ok;
            }
        if (KnobGroup.Find(key) is { } k)
        {
            if (!Holdable(key)) return Refuse(RefusalKind.Knob, key, value, "not a knob a server may hold");
            lock (_lock)
            {
                if (value.Length == 0) { k.Release(); _held.Remove(k); }
                else if (!k.Enforce(value)) return Refuse(RefusalKind.Knob, key, value, "the knob does not take that value");
                else if (!_held.Contains(k)) _held.Add(k);
                _epoch++;
            }
            PolicyRefusals.Forget(key);
            return true;
        }
        lock (_lock)
        {
            if (value.Length > 0 && _props.Count >= MaxProps && !_props.ContainsKey(key))
                return Refuse(RefusalKind.Property, key, value, $"{MaxProps} properties are held already");
            if (value.Length == 0) _props.Remove(key); else _props[key] = value;
            _stamp[key] = ++_epoch;
            if (_stamp.Count > 2 * MaxProps) ForgetStamps();
        }
        PolicyRefusals.Forget(key);
        return true;
    }

    /// <summary>A line the client did not take: logged and listed by <c>/policy</c>. Always false, for the caller to return.</summary>
    private static bool Refuse(RefusalKind kind, string key, string value, string reason)
    {
        Log?.Info($"ServerPolicy: refused '{key}': {reason}");
        PolicyRefusals.Add(kind, key, value, reason);
        return false;
    }

    /// <summary>Drops the stamps of keys no longer held, once they outnumber the properties a server may hold twice over.
    /// A reader waiting on a released key that a server has since flooded past sees its answer as never sent.</summary>
    private static void ForgetStamps()
    {
        var drop = new List<string>();
        foreach (string k in _stamp.Keys) if (!_props.ContainsKey(k)) drop.Add(k);
        foreach (string k in drop) _stamp.Remove(k);
    }

    /// <summary>Stamps kept now, held keys and released ones. For the self-check only.</summary>
    internal static int Stamps { get { lock (_lock) return _stamp.Count; } }

    private static void ReleasePrefix(string prefix)
    {
        foreach (var h in _handlers) h.Release(prefix);
        PolicyRefusals.ForgetPrefix(prefix);
        lock (_lock)
        {
            for (int i = _held.Count - 1; i >= 0; i--)
                if (_held[i].Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { _held[i].Release(); _held.RemoveAt(i); }
            var drop = new List<string>();
            foreach (string k in _props.Keys) if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) drop.Add(k);
            foreach (string k in drop) { _props.Remove(k); _stamp[k] = _epoch + 1; }
            _epoch++;
        }
    }

    /// <summary>A new session: every hold released, every property gone.</summary>
    public static void Clear() => ReleasePrefix("");

    /// <summary>Keys held right now (knobs and properties).</summary>
    public static int Count { get { lock (_lock) return _held.Count + _props.Count; } }

    /// <summary>Does any component property start with <paramref name="prefix"/>? Cold: a scan of the held properties.</summary>
    public static bool HasPrefix(string prefix)
    {
        lock (_lock)
        {
            foreach (string k in _props.Keys) if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    public static bool TryGet(string key, out string value)
    {
        lock (_lock) return _props.TryGetValue(key, out value!);
    }

    /// <summary>The <see cref="Epoch"/> at which a line last set or released the property <paramref name="key"/>, or 0 when none ever has.
    /// A reader that waits on one key compares it, so another key's change does not look like an answer.</summary>
    public static int Stamp(string key)
    {
        lock (_lock) return _stamp.TryGetValue(key, out int e) ? e : 0;
    }

    /// <summary>The whole number at <paramref name="key"/>, or <paramref name="def"/> when it is absent or not one.</summary>
    public static int Int(string key, int def)
        => TryGet(key, out string v) ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : Bad(key, v, "not a whole number", def) : def;

    /// <summary><see cref="Int(string, int)"/> held to <paramref name="lo"/>..<paramref name="hi"/>.</summary>
    public static int Int(string key, int def, int lo, int hi) => Math.Clamp(Int(key, def), lo, hi);

    public static string Str(string key, string def) => TryGet(key, out string v) ? v : def;

    public static bool Bool(string key, bool def)
    {
        if (!TryGet(key, out string v)) return def;
        return v.ToLowerInvariant() switch { "1" or "true" or "on" or "yes" => true, "0" or "false" or "off" or "no" => false, _ => Bad(key, v, "not 1/0, true/false, on/off or yes/no", def) };
    }

    public static float Float(string key, float def)
        => TryGet(key, out string v) ? float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f) ? f : Bad(key, v, "not a number", def) : def;

    /// <summary><see cref="Float(string, float)"/> held to <paramref name="lo"/>..<paramref name="hi"/>.</summary>
    public static float Float(string key, float def, float lo, float hi) => Math.Clamp(Float(key, def), lo, hi);

    /// <summary>A held property a reader could not use: record why, and give the reader its default.</summary>
    private static T Bad<T>(string key, string value, string reason, T def)
    {
        PolicyRefusals.Add(PolicyRefusals.KindOf(key), key, value, reason);
        return def;
    }

    /// <summary>The route for <paramref name="action"/> (<c>&lt;action&gt;.route</c> = engine | deny | server).</summary>
    public static PolicyRoute Route(string action)
        => Str(action + ".route", "engine").ToLowerInvariant() switch { "deny" => PolicyRoute.Deny, "server" => PolicyRoute.Server, _ => PolicyRoute.Engine };

    /// <summary>Hand an action to the server (<see cref="EventMinor"/>). False when no wire is up.</summary>
    public static bool Emit(string action, params string[] args)
    {
        if (Send == null) return false;
        var sb = new StringBuilder(action);
        foreach (string a in args) sb.Append(';').Append(a);
        Send(EventMinor, sb.ToString());
        return true;
    }

    /// <summary>The <c>/policy</c> report: every held key.</summary>
    public static string Report()
    {
        var sb = new StringBuilder();
        lock (_lock)
        {
            sb.Append($"server policy: {_held.Count} knob(s) held, {_props.Count} propert(ies)");
            foreach (Knob k in _held) sb.Append('\n').Append("  ").Append(k.Key).Append('=').Append(k.Format()).Append(" (yours ").Append(k.FormatStored()).Append(')');
            foreach (var kv in _props) sb.Append('\n').Append("  ").Append(kv.Key).Append('=').Append(kv.Value);
            foreach (var h in _handlers) sb.Append('\n').Append("  ").Append(h.Report());
            PolicyRefusals.Report(sb);
        }
        return sb.ToString();
    }

    /// <summary>Off-device: holds apply and release on a throwaway knob, properties read typed, prefix and total
    /// release work, and the player's value survives a hold without the file ever seeing the server's.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        PolicyRefusals.SelfCheck(log);
        var check = new Checker(log);
        var g = new KnobGroup("cx-policy-selfcheck", "Test", "Test", null, null, true, Effect.Live, null, null, "", register: false);
        var t = g.Toggle("gold", "gold", true, help: "x");
        check.Expect(!Set("ui.font", "12"), "a knob no feature opted in cannot be held");
        check.Expect(t.Enforce("0") && !t.V && t.Enforced, "a hold sets V");
        t.Set(true);
        check.Expect(!t.V && t.FormatStored() == "1", "a player set under a hold moves the stored value only");
        t.Release();
        check.Expect(t.V && !t.Enforced, "release restores the player's value");
        check.Expect(!t.Enforce("banana") && !t.Enforced, "a bad value is refused");

        lock (_lock) { _props.Clear(); _epoch = 0; }
        int n = Apply("inv.gold.visible=0\ninv.title=Pack\nbad line\nother.x=2.5\n");
        check.Expect(n == 3 && Epoch > 0, "three lines applied, the malformed one skipped");
        check.Expect(!Bool("inv.gold.visible", true) && Str("inv.title", "") == "Pack" && Float("other.x", 0f) == 2.5f, "typed reads");
        check.Expect(Route("inv.drop") == PolicyRoute.Engine, "no route key = engine");
        Set("inv.drop.route", "server");
        check.Expect(Route("inv.drop") == PolicyRoute.Server, "route key read");
        Set("inv.*", "");
        check.Expect(!TryGet("inv.title", out _) && TryGet("other.x", out _), "prefix release");
        Set("*", "");
        check.Expect(Count == 0, "total release");
        int took = 0;
        for (int i = 0; i < 4097; i++) if (Set("p" + i, "1")) took++;
        check.Expect(took == 4096 && Count == 4096 && Set("p0", "2"), "4096 properties are held, the next new one is refused, a held one still changes");
        Set("*", "");
        for (int i = 0; i < 10_000; i++) { Set("q" + i, "1"); Set("q" + i, ""); }
        check.Expect(Stamps <= 8192, "the stamps of released keys stop growing at twice the property limit");
        Clear();
        log.Info(check.Ok ? "ServerPolicy.SelfCheck: passed" : "ServerPolicy.SelfCheck: FAILED");
    }
}
