using System;
using System.Collections.Generic;
using System.Text;

namespace CX.Framework;

/// <summary>Which part of the policy a refused line was meant for.</summary>
public enum RefusalKind { Line, Knob, Window, Card, Style, Model, Held, Property }

/// <summary>One refused policy line: what the server sent and why the client did not take it.</summary>
public sealed record PolicyRefusal(int Seq, RefusalKind Kind, string Key, string Value, string Reason);

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

    /// <summary>The refusals of this session and the ceiling on them. Replaced whole by a reset.</summary>
    private sealed class PolicyRefusalState
    {
        public readonly List<PolicyRefusal> Entries = new();
        public int HeldBytes;
        public int DroppedCount;
        public int LastSeq;
    }

    /// <summary>Told of each refusal that is new, after the table lock is released, on whatever thread refused.</summary>
    internal static Action<PolicyRefusal>? Added;

    private static PolicyRefusalState _s = new();
    private static readonly object _lock = new();

    /// <summary>Entries older than the ceiling allows, gone.</summary>
    public static int Dropped { get { lock (_lock) return _s.DroppedCount; } }

    /// <summary>Record that <paramref name="key"/> was refused. The same key, value and reason again changes nothing.</summary>
    public static void Add(RefusalKind kind, string key, string value, string reason)
    {
        PolicyRefusal add;
        lock (_lock)
        {
            PolicyRefusalState s = _s;
            for (int i = 0; i < s.Entries.Count; i++)
            {
                PolicyRefusal e = s.Entries[i];
                if (!Holds(e.Key, key, MaxKey, true)) continue;
                if (e.Kind == kind && Holds(e.Value, value, MaxValue, false) && Holds(e.Reason, reason, MaxReason, false)) return;
                s.HeldBytes -= Size(e);
                s.Entries.RemoveAt(i);
                break;
            }
            key = Cut(key, MaxKey); value = Cut(value, MaxValue); reason = Cut(reason, MaxReason);
            add = new PolicyRefusal(++s.LastSeq, kind, key, value, reason);
            s.Entries.Add(add);
            s.HeldBytes += Size(add);
            while (s.Entries.Count > MaxEntries || s.HeldBytes > MaxBytes)
            {
                s.HeldBytes -= Size(s.Entries[0]);
                s.Entries.RemoveAt(0);
                s.DroppedCount++;
            }
        }
        Added?.Invoke(add);
    }

    /// <summary>The key was set to something good or released: its refusal no longer stands.</summary>
    public static void Forget(string key) => Remove(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Drops every refusal whose key starts with <paramref name="prefix"/>; an empty prefix is a new session.</summary>
    public static void ForgetPrefix(string prefix)
    {
        if (prefix.Length == 0) { lock (_lock) _s = new PolicyRefusalState(); return; }
        Remove(e => e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static void Remove(Func<PolicyRefusal, bool> drop)
    {
        lock (_lock)
            for (int i = _s.Entries.Count - 1; i >= 0; i--)
                if (drop(_s.Entries[i])) { _s.HeldBytes -= Size(_s.Entries[i]); _s.Entries.RemoveAt(i); }
    }

    /// <summary>The refusals held, oldest first.</summary>
    public static PolicyRefusal[] All()
    {
        lock (_lock) return _s.Entries.ToArray();
    }

    /// <summary>The <c>/policy</c> lines for the refusals: nothing when there are none.</summary>
    public static void Report(StringBuilder sb)
    {
        lock (_lock)
        {
            if (_s.Entries.Count == 0 && _s.DroppedCount == 0) return;
            sb.Append("\nrefused: ").Append(_s.Entries.Count).Append(" line(s)");
            if (_s.DroppedCount > 0) sb.Append(", ").Append(_s.DroppedCount).Append(" older one(s) dropped");
            foreach (PolicyRefusal e in _s.Entries)
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

    /// <summary>Is <paramref name="held"/> what <see cref="Cut"/> would make of <paramref name="raw"/>? Compared in place, so a refusal that repeats allocates nothing.</summary>
    private static bool Holds(string held, string raw, int max, bool ignoreCase)
    {
        int n = Math.Min(raw.Length, max);
        if (held.Length != n) return false;
        for (int i = 0; i < n; i++)
        {
            char c = raw[i] < ' ' || raw[i] == ';' ? ' ' : raw[i];
            if (c != held[i] && !(ignoreCase && char.ToUpperInvariant(c) == char.ToUpperInvariant(held[i]))) return false;
        }
        return true;
    }

    private static string Cut(string s, int max)
    {
        if (s.Length > max) s = s[..max];
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c < ' ' || c == ';' ? ' ' : c);
        return sb.ToString();
    }

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
        check.Expect(All().Length == 2 && All()[1].Seq == got[1].Seq, "the same refusal read again adds nothing");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) Add(RefusalKind.Held, "HELD.opacity", "abc", "not a number");
        check.Expect(GC.GetAllocatedBytesForCurrentThread() - before == 0, "the same refusal again allocates nothing");
        ServerPolicy.Set("held.opacity", "0.5");
        check.Expect(All().Length == 1, "a good value clears the key's refusal");
        var text = new StringBuilder();
        PolicyRefusals.Report(text);
        check.Expect(text.ToString() == "\nrefused: 1 line(s)\n  [line] no equals sign= -- no '=' in the line", "the report lists them");
        ServerPolicy.Clear();
        ServerPolicy.Apply("card.t={\"blocks\":{}}\ncardstyle={\"glow\":\"#12345\"}");
        got = All();
        check.Expect(got.Length == 2 && got[0].Kind == RefusalKind.Card && got[0].Key == "card.t" && got[0].Value == "{\"blocks\":{}}" && got[0].Reason.Length > 0
                     && got[1].Kind == RefusalKind.Style && got[1].Key == "cardstyle" && got[1].Value == "{\"glow\":\"#12345\"}" && got[1].Reason.Length > 0,
                     "a card that does not parse and a style that does not parse are refused, each under its own kind");
        ServerPolicy.Apply("card.t={\"blocks\":[]}\ncardstyle={\"glow\":\"#12345678\"}");
        check.Expect(All().Length == 0, "a good card and a good style clear their refusals");
        ServerPolicy.Apply("card.t={\"blocks\":{}}\ncardstyle={\"glow\":\"#12345\"}\ncard.*=");
        got = All();
        check.Expect(got.Length == 1 && got[0].Kind == RefusalKind.Style, "releasing the cards clears the card refusal and leaves the style's");
        ServerPolicy.Apply("cardstyle=");
        check.Expect(All().Length == 0, "releasing the style clears its refusal");
        ServerPolicy.Set("model.k.fitzone", "x");
        ServerPolicy.Float("model.k.fitzone", 0f);
        ServerPolicy.Set("nui.w.opacity", "y");
        ServerPolicy.Float("nui.w.opacity", 1f);
        got = All();
        check.Expect(got.Length == 2 && got[0].Kind == RefusalKind.Model && got[0].Key == "model.k.fitzone" && got[1].Kind == RefusalKind.Window && got[1].Key == "nui.w.opacity",
                     "a model key and a window key a reader could not use are recorded under their kinds");
        ServerPolicy.Set("model.k.fitzone", "0.5");
        check.Expect(All().Length == 1 && All()[0].Kind == RefusalKind.Window, "a good re-send of a model key clears its refusal");
        ServerPolicy.Set("model.k.fitzone", "x");
        ServerPolicy.Float("model.k.fitzone", 0f);
        ServerPolicy.Apply("model.k.*=\nnui.w.*=");
        check.Expect(All().Length == 0, "releasing a model's keys and a window's keys clears their refusals");
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
