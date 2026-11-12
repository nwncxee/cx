using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>Pure send-decision for Tell tabs (ADR 0002): typed target beats pin; pin resolves via
/// <see cref="IPlayerResolver"/>; unresolvable targets drop (never rerouted).</summary>
public static class TellTarget
{
    /// <summary>Resolve the Tell target for a tab. <paramref name="typedName"/> (from a <c>/t "Name"</c>
    /// command) beats the tab pin; either resolves via <paramref name="roster"/>. Returns a PlayerId, or
    /// null (= drop).</summary>
    public static uint? Resolve(Tab tab, string? typedName, IPlayerResolver? roster)
    {
        if (!string.IsNullOrEmpty(typedName))
            return roster?.Resolve(typedName);
        if (tab.PinnedName.Length > 0)
            return roster?.Resolve(tab.PinnedName);
        return null;
    }

    /// <summary>Same precedence as <see cref="Resolve"/> but returns the effective player name
    /// (for name-based send). No client-side presence gate: the server validates the name,
    /// so stale tabs and cross-area players are not silently dropped.</summary>
    public static string? ResolveName(Tab tab, string? typedName, IPlayerResolver? roster)
    {
        return !string.IsNullOrEmpty(typedName) ? typedName
            : tab.PinnedName.Length > 0 ? tab.PinnedName : null;
    }

    /// <summary>Who a click on a chat line's portrait tells: the speaker the line named when it arrived, under
    /// the id that name has on the roster now (null while offline). A line's own id is its speaker's connection
    /// at arrival and names nobody, or somebody else, after a relog; only a line that carries no name falls back to it.</summary>
    public static (string? Name, uint? PlayerId) OfLine(string speaker, uint linePlayerId, IPlayerResolver? roster)
    {
        if (speaker.Length > 0) return (speaker, roster?.Resolve(speaker));
        string? name = roster?.NameOf(linePlayerId);
        return (name, name != null ? linePlayerId : null);
    }

    /// <summary>Outbound tells awaiting their server echo, oldest first: (canonical recipient, text).</summary>
    private static readonly ConcurrentQueue<(string Name, string Text)> _pending = new();
    private const int MaxPending = 8;

    /// <summary>Record a tell about to go out so its echo can be tagged with the recipient. The echo the
    /// server returns is a plain tell from yourself; nothing in it names who it went to. <paramref name="wire"/>
    /// is the line in the engine's own byte space (one char per wire byte, <c>Native.EngineBytes</c> read Latin1),
    /// which is how ingest reads the echo back.</summary>
    public static void Sent(string recipient, string wire)
    {
        _pending.Enqueue((recipient, wire));
        while (_pending.Count > MaxPending) _pending.TryDequeue(out _);
    }

    /// <summary>Recipient for an arriving self-tell line, or "" when none is pending. Matches by text so a
    /// dropped echo (target gone before the server fanned out) cannot shift every later echo onto the
    /// wrong pin; entries older than the match are discarded as dropped.</summary>
    // ponytail: identical texts to two targets in flight resolve oldest-first; fine for humans typing.
    public static string TakeEcho(string raw)
    {
        foreach (var e in _pending)
            if (raw.Contains(e.Text, StringComparison.Ordinal))
            {
                while (_pending.TryDequeue(out var d)) if (d.Equals(e)) return e.Name;
                break;
            }
        return "";
    }

    /// <summary>Off-device proof of the three AC cases + edge cases. Oracle: hand-written names and ids
    /// against a fake roster.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        // Echo ledger: text-matched, oldest dropped entries discarded, nothing pending = "".
        while (_pending.TryDequeue(out _)) { }
        check.Expect(TakeEcho("Me: [Tell] hi") == "", "no pending echo = empty");
        Sent("Alice", "hi"); Sent("Bob", "yo"); Sent("Carol", "sup");
        check.Expect(TakeEcho("<cRGB>Me</c>: [Tell] yo") == "Bob", "echo matched by text, not by order");
        check.Expect(TakeEcho("Me: [Tell] hi") == "", "entries older than a match are dropped echoes");
        check.Expect(TakeEcho("Me: [Tell] sup") == "Carol", "later entry survives");
        check.Expect(TakeEcho("Me: [Tell] sup") == "", "consumed once");
        for (int i = 0; i < 20; i++) Sent("X" + i, "t" + i);
        check.Expect(TakeEcho("t0") == "" && TakeEcho("t19") == "X19", "ledger is capped");
        Sent("Zośka", "cze\u009c\u00e6");   // cp1250 bytes of "cześć", as EngineBytes hands them over
        check.Expect(TakeEcho("Me: [Powiedz] cze\u009c\u00e6") == "Zośka", "byte-space text matches its byte-per-char echo");

        var roster = new FakeRoster();
        roster.Players["Alice"] = 42u;
        roster.Players["Bob"] = 99u;

        // Pinned + online -> resolves to the live id
        var pinned = new Tab("Tell:Alice", 0x002) { PinnedName = "Alice" };
        check.Expect(Resolve(pinned, null, roster) == 42u, "pinned online resolves");

        // Pinned + offline -> null (drop, never reroute)
        var offline = new Tab("Tell:Eve", 0x002) { PinnedName = "Eve" };
        check.Expect(Resolve(offline, null, roster) == null, "pinned offline drops");

        // Typed target beats pin: /t "Bob" on an Alice-pinned tab sends to Bob
        check.Expect(Resolve(pinned, "Bob", roster) == 99u, "typed target beats pin");

        // Typed target offline -> drop (not rerouted to pin)
        check.Expect(Resolve(pinned, "Eve", roster) == null, "typed offline drops even with valid pin");

        // Unpinned + no typed target -> null
        var unpinned = new Tab("Tell", 0x002);
        check.Expect(Resolve(unpinned, null, roster) == null, "unpinned with no target is null");

        // Unpinned + typed target -> resolves
        check.Expect(Resolve(unpinned, "Alice", roster) == 42u, "unpinned with typed target resolves");

        // Case-sensitive: engine uses strcmp, so "alice" != "Alice" -> drop
        var casePin = new Tab("Tell:alice", 0x002) { PinnedName = "alice" };
        check.Expect(Resolve(casePin, null, roster) == null, "case-sensitive: wrong case drops");

        // Null resolver -> drops safely
        check.Expect(Resolve(pinned, null, null) == null, "null resolver drops pinned");
        check.Expect(Resolve(unpinned, "Alice", null) == null, "null resolver drops typed");

        // Empty PinnedName = unpinned
        var emptyPin = new Tab("Tell", 0x002) { PinnedName = "" };
        check.Expect(Resolve(emptyPin, null, roster) == null, "empty pin = unpinned");

        // ResolveName: same precedence, no client-side presence gate (server validates)
        check.Expect(ResolveName(pinned, null, roster) == "Alice", "ResolveName pinned online");
        check.Expect(ResolveName(pinned, "Bob", roster) == "Bob", "ResolveName typed beats pin");
        check.Expect(ResolveName(offline, null, roster) == "Eve", "ResolveName offline still returns name");
        check.Expect(ResolveName(unpinned, null, roster) == null, "ResolveName unpinned no target");

        // A portrait click after the speaker relogged: Alice spoke as 42, came back as 57, and Bob took 42.
        var relog = new FakeRoster();
        relog.Players["Alice"] = 57u;
        relog.Players["Bob"] = 42u;
        check.Expect(OfLine("Alice", 42u, relog) == ("Alice", 57u), "a line's speaker is told under the id she has now");
        relog.Players.Remove("Alice");
        check.Expect(OfLine("Alice", 42u, relog) == ("Alice", null), "an offline speaker keeps her name and has no id");
        check.Expect(OfLine("", 42u, relog) == ("Bob", 42u), "a line with no name falls back to its id");
        check.Expect(OfLine("", 7u, relog) == (null, null), "a line with no name and a dead id names nobody");
        check.Expect(OfLine("Alice", 42u, null) == ("Alice", null), "no roster still names the speaker");

        log.Info(check.Ok ? "TellTarget.SelfCheck: passed" : "TellTarget.SelfCheck: FAILED");
    }

    /// <summary>In-memory fake for off-device tests. Case-sensitive (matching the engine's strcmp).</summary>
    private sealed class FakeRoster : IPlayerResolver
    {
        public readonly Dictionary<string, uint> Players = new();
        public uint? Resolve(string name) => Players.TryGetValue(name, out uint id) ? id : null;
        public string? NameOf(uint playerId)
        {
            foreach (var kv in Players) if (kv.Value == playerId) return kv.Key;
            return null;
        }
    }
}
