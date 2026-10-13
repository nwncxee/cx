using System;
using System.Collections.Generic;
using System.Text;

namespace CX.Framework;

/// <summary>Which part of the policy a refused line was meant for.</summary>
public enum RefusalKind { Line, Knob, Window, Card, Style, Model, Held, Property }

/// <summary>One refused policy line: what the server sent and why the client did not take it.
/// <see cref="Wire"/> is the <c>action;arg;arg</c> form a later change can send back as a <see cref="ServerPolicy.EventMinor"/> event.</summary>
public sealed record PolicyRefusal(int Seq, RefusalKind Kind, string Key, string Value, string Reason)
{
    /// <summary>The event form, <c>refused;kind;key;reason</c>, with <c>;</c> and line breaks in the text replaced so it parses back.</summary>
    public string Wire() => $"refused;{Kind.ToString().ToLowerInvariant()};{PolicyRefusals.Flat(Key, ',')};{PolicyRefusals.Flat(Reason, ',')}";
}

/// <summary>The refusals of this session and the ceiling on them. Replaced whole by a reset.</summary>
public sealed class PolicyRefusalState
{
    public readonly List<PolicyRefusal> List = new();
    public int Bytes;
    public int Dropped;
    public int Seq;
}

/// <summary>
/// Every policy line the client refused this session, kept for <c>/policy</c> (<c>docs/ui-policy-contract.md</c>). One entry per key, the
/// latest reason. Bounded in count and bytes: the oldest entry goes first and <see cref="Dropped"/> counts them, so a hostile server cannot
/// grow it. A key set again to a good value, or released, loses its entry.
/// </summary>
public static class PolicyRefusals
{
    /// <summary>Most entries held.</summary>
    public const int MaxEntries = 64;
    /// <summary>Most UTF-8 bytes of key, value and reason held in total.</summary>
    public const int MaxBytes = 8192;
    /// <summary>Longest key, value and reason kept, in characters; the rest is cut.</summary>
    public const int MaxKey = 96, MaxValue = 64, MaxReason = 160;

    private static PolicyRefusalState _s = new();
    private static readonly object _lock = new();

    /// <summary>Entries older than the ceiling allows, gone.</summary>
    public static int Dropped { get { lock (_lock) return _s.Dropped; } }

    /// <summary>Record that <paramref name="key"/> was refused. The same key, value and reason again changes nothing.</summary>
    public static void Add(RefusalKind kind, string key, string value, string reason)
    {
        key = Cut(key, MaxKey); value = Cut(value, MaxValue); reason = Cut(reason, MaxReason);
        lock (_lock)
        {
            PolicyRefusalState s = _s;
            for (int i = 0; i < s.List.Count; i++)
            {
                PolicyRefusal e = s.List[i];
                if (!string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Value == value && e.Reason == reason && e.Kind == kind) return;
                s.Bytes -= Size(e);
                s.List.RemoveAt(i);
                break;
            }
            var add = new PolicyRefusal(++s.Seq, kind, key, value, reason);
            s.List.Add(add);
            s.Bytes += Size(add);
            while (s.List.Count > MaxEntries || s.Bytes > MaxBytes)
            {
                s.Bytes -= Size(s.List[0]);
                s.List.RemoveAt(0);
                s.Dropped++;
            }
        }
    }

    /// <summary>The key was set to something good or released: its refusal no longer stands.</summary>
    public static void Forget(string key)
    {
        lock (_lock)
            for (int i = _s.List.Count - 1; i >= 0; i--)
                if (string.Equals(_s.List[i].Key, key, StringComparison.OrdinalIgnoreCase)) { _s.Bytes -= Size(_s.List[i]); _s.List.RemoveAt(i); }
    }

    /// <summary>Drops every refusal whose key starts with <paramref name="prefix"/>; an empty prefix is a new session.</summary>
    public static void ForgetPrefix(string prefix)
    {
        if (prefix.Length == 0) { lock (_lock) _s = new PolicyRefusalState(); return; }
        lock (_lock)
            for (int i = _s.List.Count - 1; i >= 0; i--)
                if (_s.List[i].Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { _s.Bytes -= Size(_s.List[i]); _s.List.RemoveAt(i); }
    }

    /// <summary>The refusals held, oldest first.</summary>
    public static PolicyRefusal[] All()
    {
        lock (_lock) return _s.List.ToArray();
    }

    /// <summary>The <c>/policy</c> lines for the refusals: nothing when there are none.</summary>
    public static void Report(StringBuilder sb)
    {
        lock (_lock)
        {
            if (_s.List.Count == 0 && _s.Dropped == 0) return;
            sb.Append("\nrefused: ").Append(_s.List.Count).Append(" line(s)");
            if (_s.Dropped > 0) sb.Append(", ").Append(_s.Dropped).Append(" older one(s) dropped");
            foreach (PolicyRefusal e in _s.List)
                sb.Append("\n  [").Append(e.Kind.ToString().ToLowerInvariant()).Append("] ").Append(e.Key).Append('=').Append(e.Value).Append(" -- ").Append(e.Reason);
        }
    }

    /// <summary>The part of the policy a key belongs to, by its prefix.</summary>
    public static RefusalKind KindOf(string key)
        => key.StartsWith("model.", StringComparison.OrdinalIgnoreCase) ? RefusalKind.Model
         : key.StartsWith("nui.", StringComparison.OrdinalIgnoreCase) ? RefusalKind.Window
         : key.StartsWith("cardstyle", StringComparison.OrdinalIgnoreCase) ? RefusalKind.Style
         : key.StartsWith("card.", StringComparison.OrdinalIgnoreCase) ? RefusalKind.Card
         : key.StartsWith("held.", StringComparison.OrdinalIgnoreCase) ? RefusalKind.Held
         : RefusalKind.Property;

    internal static string Flat(string s, char with)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c < ' ' || c == ';' ? with : c);
        return sb.ToString();
    }

    private static string Cut(string s, int max) => Flat(s.Length > max ? s[..max] : s, ' ');

    private static int Size(PolicyRefusal e) => Encoding.UTF8.GetByteCount(e.Key) + Encoding.UTF8.GetByteCount(e.Value) + Encoding.UTF8.GetByteCount(e.Reason);

    /// <summary>
    /// Oracle: hand-written expected refusals for a hand-written payload, and the ceilings as literals. Mutates and then clears the
    /// session's policy, as <see cref="ServerPolicy.SelfCheck"/> does.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        ServerPolicy.Clear();
        ServerPolicy.Apply("no equals sign\nheld.opacity=abc\ninv.ok=1");
        PolicyRefusal[] got = All();
        check.Expect(got.Length == 1 && got[0].Kind == RefusalKind.Line && got[0].Key == "no equals sign" && got[0].Reason == "no '=' in the line",
                     "a line with no = is refused whole, the other two are kept");
        ServerPolicy.Float("held.opacity", 0.9f);
        got = All();
        check.Expect(got.Length == 2 && got[1].Kind == RefusalKind.Held && got[1].Value == "abc" && got[1].Reason == "not a number",
                     "a held key that is not a number is refused when read");
        ServerPolicy.Float("held.opacity", 0.9f);
        check.Expect(All().Length == 2, "the same refusal read again adds nothing");
        check.Expect(got.Length == 2 && got[1].Wire() == "refused;held;held.opacity;not a number", "the event form");
        ServerPolicy.Set("held.opacity", "0.5");
        check.Expect(All().Length == 1, "a good value clears the key's refusal");
        var text = new StringBuilder();
        PolicyRefusals.Report(text);
        check.Expect(text.ToString() == "\nrefused: 1 line(s)\n  [line] no equals sign= -- no '=' in the line", "the report lists them");
        ServerPolicy.Clear();
        ServerPolicy.Apply(string.Join('\n', System.Linq.Enumerable.Repeat("a=1", 513)));
        check.Expect(All().Length == 1 && All()[0].Key == "(whole message)" && All()[0].Reason == "more than 512 lines", "a message over 512 lines is refused whole");
        ServerPolicy.Clear();
        check.Expect(All().Length == 0, "a new session holds none");
        for (int i = 0; i < 1000; i++) Add(RefusalKind.Property, "k" + i, "v", "r");
        check.Expect(All().Length == 64 && Dropped == 936 && All()[0].Key == "k936", "1000 refusals keep the newest 64");
        ServerPolicy.Clear();
        string big = new('x', 5000);
        for (int i = 0; i < 100; i++) Add(RefusalKind.Property, big + i, big, big);
        PolicyRefusal[] fat = All();
        int bytes = 0;
        foreach (PolicyRefusal e in fat) bytes += Encoding.UTF8.GetByteCount(e.Key + e.Value + e.Reason);
        check.Expect(bytes <= 8192 && fat.Length <= 64 && fat[0].Key.Length == 96 && fat[0].Value.Length == 64 && fat[0].Reason.Length == 160,
                     "long text is cut and the total stays under 8192 bytes");
        ServerPolicy.Clear();
        log.Info(check.Ok ? "PolicyRefusals.SelfCheck: passed" : "PolicyRefusals.SelfCheck: FAILED");
    }
}
