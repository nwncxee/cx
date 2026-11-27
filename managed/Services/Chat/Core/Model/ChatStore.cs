using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>
/// The session's chat backlog: a fixed ring of <see cref="ChatLine"/>s that overwrites its oldest, beside a list of
/// every tell, which is never overwritten. Tab views walk both newest-first in arrival order, skipping non-matching
/// lines, until the caller's buffer fills -- no per-tab buffers, no allocation in the walk.
/// </summary>
public sealed class ChatStore
{
    /// <summary>Backlog depth for every line but a tell; tells are kept on top of it until <see cref="Clear"/>.</summary>
    public const int Capacity = 2048;

    /// <summary>Room for tells before the list first doubles.</summary>
    private const int FirstTellRoom = 64;

    /// <summary>A kept tell, and how many lines the ring had taken when it arrived, which orders it among them.</summary>
    private readonly record struct Kept(ChatLine Line, long Mark);

    private readonly ChatLine[] _ring = new ChatLine[Capacity];
    private int _next;   // next write slot
    private int _size;   // valid entries, <= Capacity
    private long _written;   // lines the ring has ever taken
    private Kept[] _tells = new Kept[FirstTellRoom];
    private int _tellCount;
    private volatile int _version;
    private readonly List<Action<ChatLine>> _observers = new();

    /// <summary>Monotonic content version, bumped on every <see cref="Append"/> and <see cref="Clear"/>.
    /// The layout cache keys on this to detect stale wrap results.</summary>
    public int Version => _version;

    /// <summary>Register an ingest observer, invoked (in add order) for every <see cref="Append"/>.</summary>
    public void AddObserver(Action<ChatLine> observer) => _observers.Add(observer);

    /// <summary>Store <paramref name="line"/> (a tell for the session, anything else overwriting the oldest when
    /// full), then notify observers.</summary>
    public void Append(ChatLine line)
    {
        if ((line.Channel & InboundChannel.Tell) != 0) KeepTell(line);
        else
        {
            _ring[_next] = line;
            _next = (_next + 1) % Capacity;
            if (_size < Capacity) _size++;
            _written++;
        }
        _version++;
        for (int i = 0; i < _observers.Count; i++) _observers[i](line);
    }

    /// <summary>Append a tell to the session's list, growing it by doubling; the count is published last, so a
    /// concurrent <see cref="View(uint,uint?,ChatLine[],int,string?)"/> never reads a slot not yet written.</summary>
    private void KeepTell(ChatLine line)
    {
        Kept[] tells = _tells;
        if (_tellCount == tells.Length) Array.Resize(ref tells, tells.Length * 2);
        tells[_tellCount] = new Kept(line, _written);
        _tells = tells;
        _tellCount++;
    }

    /// <summary>Number of lines currently retained, tells included.</summary>
    public int Count => _size + _tellCount;

    /// <summary>Drop all retained lines (a session boundary). Zero-alloc; drops the ChatLine refs so
    /// the GC can reclaim the prior session's lines.</summary>
    public void Clear()
    {
        Array.Clear(_ring, 0, _ring.Length);
        _next = 0;
        _size = 0;
        _tellCount = 0;
        Array.Clear(_tells, 0, _tells.Length);  // mutant-ok: only lets the GC take the last session's tells
        _version++;
    }

    /// <summary>
    /// Fill <paramref name="dest"/> newest-first with lines matching the tab predicate, up to
    /// <c>dest.Length</c>; returns the count written. A line matches when its channel bit is set in
    /// <paramref name="channelMask"/> and (when <paramref name="playerId"/> is given) its player id equals it.
    /// Zero-alloc: the only "slice" is the caller-owned <paramref name="dest"/>.
    /// </summary>
    public int View(uint channelMask, uint? playerId, ChatLine[] dest, string? tellPinName = null)
        => View(channelMask, playerId, dest, 0, tellPinName);

    /// <summary>
    /// As <see cref="View(uint,uint?,ChatLine[],string?)"/>, but first skips the <paramref name="skip"/> newest
    /// matching lines. A set <paramref name="tellPinName"/> also takes the player's tells to that name and that
    /// sender's lines from an id before a relog (<see cref="ChatLine.TellRecipient"/>, <see cref="ChatLine.SenderName"/>).
    /// </summary>
    public int View(uint channelMask, uint? playerId, ChatLine[] dest, int skip, string? tellPinName = null)
    {
        int n = 0, skipped = 0;
        bool hasPin = tellPinName != null && tellPinName.Length > 0;
        Kept[] tells = _tells;
        int t = Math.Min(_tellCount, tells.Length) - 1;  // mutant-ok: a list grown after the read above is longer still
        int seen = 0;
        while (n < dest.Length)
        {
            int idx = (_next - 1 - seen) % Capacity;
            if (idx < 0) idx += Capacity;
            bool ring = seen < _size;
            ChatLine line;
            if (t >= 0 && (!ring || tells[t].Mark > _written - 1 - seen)) line = tells[t--].Line;
            else if (ring) { line = _ring[idx]; seen++; }
            else break;
            if (line is null) continue;  // mutant-ok: only a Clear racing ingest leaves a counted slot empty

            if ((channelMask & line.Channel) == 0) continue;
            if (playerId.HasValue && line.PlayerId != playerId.Value
                && !(hasPin && (line.TellRecipient == tellPinName || line.SenderName == tellPinName)))
                continue;

            if (skipped < skip) { skipped++; continue; }
            dest[n++] = line;
        }
        return n;
    }

    /// <summary>Runnable self-check: ring wrap (oldest overwritten), backward filtered newest-first
    /// walk, and observer firing on Append.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        var store = new ChatStore();

        int fired = 0;
        store.AddObserver(_ => fired++);

        // Fill past capacity so the ring wraps; encode ordinal in RawText/PlayerId.
        int total = Capacity + 5;
        for (int k = 0; k < total; k++)
            store.Append(Line(channel: 1u, playerId: (uint)k, raw: "line" + k));

        ok &= fired == total;                 // observer fired once per append
        ok &= store.Count == Capacity;        // saturated, not overgrown

        var buf = new ChatLine[Capacity];
        int got = store.View(0xffffffff, null, buf);
        ok &= got == Capacity;
        ok &= buf[0].RawText == "line" + (total - 1);   // newest first
        ok &= buf[Capacity - 1].RawText == "line" + (total - Capacity); // oldest surviving = line5 (0..4 overwritten)

        // Channel filter: mix TALK (0x01) and SHOUT (0x04), ask only for SHOUT.
        var s2 = new ChatStore();
        s2.Append(Line(0x01, 10, "a"));
        s2.Append(Line(0x04, 11, "b"));
        s2.Append(Line(0x01, 12, "c"));
        s2.Append(Line(0x04, 13, "d"));
        int n2 = s2.View(0x04, null, buf);               // SHOUT flag only
        ok &= n2 == 2 && buf[0].RawText == "d" && buf[1].RawText == "b";

        // PlayerId filter narrows within a channel mask.
        int np = s2.View(0xffffffff, 12u, buf);
        ok &= np == 1 && buf[0].RawText == "c";

        // Scroll offset skips the newest N matches; over-scroll returns 0 (caller clamps).
        int ns = s2.View(0xffffffff, null, buf, skip: 1);
        ok &= ns == 3 && buf[0].RawText == "c";          // "d" (newest) skipped
        ok &= s2.View(0xffffffff, null, buf, skip: 4) == 0;  // past the oldest

        // Clear drops the whole backlog (session boundary).
        s2.Clear();
        ok &= s2.Count == 0 && s2.View(0xffffffff, null, buf) == 0;

        // Two-sided Tell conversation: tellPinName includes outbound echoes whose TellRecipient matches.
        var s3 = new ChatStore();
        s3.Append(Line(0x002, 42, "from-alice"));                              // incoming from Alice (pid=42)
        s3.Append(Line(0x002, 1, "to-alice", tellRecip: "Alice"));             // outbound echo TO Alice (pid=1=you)
        s3.Append(Line(0x002, 99, "from-bob"));                                // incoming from Bob (pid=99)
        s3.Append(Line(0x002, 1, "to-bob", tellRecip: "Bob"));                 // outbound echo TO Bob (pid=1=you)
        // View with pid=42 (Alice) + tellPinName="Alice": gets incoming from Alice + outbound to Alice.
        int nt = s3.View(0x002, 42u, buf, "Alice");
        ok &= nt == 2 && buf[0].RawText == "to-alice" && buf[1].RawText == "from-alice";
        // Without tellPinName: only incoming from Alice (old behavior).
        int nt2 = s3.View(0x002, 42u, buf);
        ok &= nt2 == 1 && buf[0].RawText == "from-alice";
        // Bob's outbound echo doesn't leak into Alice's view.
        int nt3 = s3.View(0x002, 42u, buf, "Alice");
        ok &= nt3 == 2;  // still only 2 (from-alice + to-alice), not to-bob

        log.Info(ok ? "ChatStore.SelfCheck: passed" : "ChatStore.SelfCheck: FAILED");
    }

    private static ChatLine Line(uint channel, uint playerId, string raw, string tellRecip = "") =>
        new ChatLine(channel, "", playerId, "", Array.Empty<ColorRun>(), DateTime.UtcNow, raw, default, tellRecip);
}
