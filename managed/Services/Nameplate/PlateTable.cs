using System;
using System.Collections.Generic;
using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;

using CX.Ui;

namespace CX.Services.Nameplate;

/// <summary>
/// The id-keyed table of overhead elements: what the hooks recorded, cold-resolved once, and what the frame is
/// allowed to draw. Preallocated end to end -- <see cref="Refresh"/> and everything downstream allocates nothing.
/// Two threads touch it: the hooks only ever APPEND to a fixed ring, doing every string resolve and colour parse
/// there, and the render thread drains it and owns the table. The join key is the object id, never a pointer.
/// </summary>
public sealed partial class PlateTable
{
    /// <summary>Objects we will track at once. Well past what a screenful of hover + Tab can produce; a
    /// full table refuses new candidates rather than growing on a frame.</summary>
    public const int MaxEntries = 64;

    /// <summary>Bubble lines kept per object. The config's <c>BubbleQueue</c> clamps to this.</summary>
    public const int MaxLines = 4;

    /// <summary>Wrapped lines kept per bubble line. The config's <c>BubbleLines</c> clamps to this. 32 is the
    /// figure cited for the engine's own bubble (unverified); it is far enough past a paragraph-length emote
    /// that nothing vanilla would have shown is cut by us.</summary>
    public const int MaxWrapped = 32;

    /// <summary>How long a bubble line holds at full opacity, as a fraction of its decay; the rest is the fade
    /// tail. The shape the top line already faded on, now applied to every line in the queue.</summary>
    public const float HoldFraction = 0.75f;

    /// <summary>Pending records the hooks may bank between two frames. Overflow is counted, not grown.</summary>
    private const int PendingRing = 128;

    /// <summary>Segments of an <c>ObjectTextBubbleOverride</c> we keep. The server end composes at most a damage
    /// row plus a status row, so this is generous headroom; a longer override keeps its first
    /// <see cref="MaxParas"/> segments. ponytail: the ceiling is the para pool, not anything engine-side --
    /// raise the const if a server ever sends more rows.</summary>
    public const int MaxParas = 6;

    /// <summary>One overhead chat line: the cold-resolved runes and per-rune colours, plus the wrap cache
    /// keyed on everything that can change its shape.</summary>
    public sealed class BubbleLine
    {
        /// <summary>Allocation-avoidance threshold, not a limit: a longer line grows both buffers ONCE, on the
        /// drain, and no rune is ever dropped. The pool is <c>MaxEntries * MaxLines</c> = 256 lines, so this
        /// costs ~2 MB; 1024 runes is past anything the chat channel realistically carries.</summary>
        private const int Prealloc = 1024;

        public readonly List<Rune> Runes = new(Prealloc);
        public uint[] Colours = new uint[Prealloc];
        public long PostedMs;
        /// <summary>Seconds at full opacity before the fade tail starts, from <see cref="PostedMs"/>.</summary>
        public float HoldSec;
        /// <summary>Length of the fade tail, seconds. Zero means the line drops on the frame the hold ends.</summary>
        public float FadeSec;
        public int Channel = -1;

        /// <summary>Per-line opacity 0..1, recomputed every frame from the clock. The line lives until this
        /// reaches 0 -- so a line past its hold is still drawn, fading, rather than vanishing.</summary>
        public float Alpha;
        /// <summary>Set once the line is in its fade tail, naturally or because a newcomer evicted it. A fading
        /// line no longer counts against the queue cap, which is what lets the cap hold a fading line beyond it.</summary>
        public bool Fading;
        /// <summary>The wrap dropped lines past the cap, so the last kept line ends in an ellipsis.</summary>
        public bool Truncated;
        /// <summary>Runes of the engine's <c>"[speaker]: "</c> prefix at the head of <see cref="Runes"/>
        /// (<see cref="PlateLayout.SpeakerPrefixLen"/>), or 0. Kept in the runes; a card with a name header wraps
        /// past it.</summary>
        public int Prefix;

        /// <summary>Put the line into its fade tail starting now. Idempotent: a line already fading keeps the
        /// tail it is part way through rather than restarting it.</summary>
        public void BeginFade(long nowMs, float fadeSec)
        {
            if (Fading) return;
            float age = (nowMs - PostedMs) / 1000f;
            HoldSec = age > 0f ? age : 0f;
            FadeSec = fadeSec;
            Fading = true;
        }

        /// <summary>Wrapped spans, valid while <see cref="WrapWidth"/> / <see cref="WrapEpoch"/> /
        /// <see cref="WrapScale"/> still match the frame.</summary>
        public TextWrapper.LineSpan[] Spans = new TextWrapper.LineSpan[MaxWrapped];
        public int SpanCount;
        public float WrapWidth = -1f, WrapScale = -1f;
        public int WrapEpoch = -1;
        /// <summary>Wrapped-line cap the cached spans were cut to. In the key because the cap is a LIVE knob
        /// (the settings slider): without it, dragging it rewraps nothing until the width or scale changes.</summary>
        public int WrapCap = -1;
        /// <summary>Runes the cached spans skip at the head -- <see cref="Prefix"/> or 0 -- in the key because the
        /// name header that decides it is a live knob.</summary>
        public int WrapSkip = -1;
        public float WrapPixelW;
    }

    /// <summary>One tracked object. A reference type held in a preallocated pool: <see cref="Used"/> is the
    /// only liveness bit, so an eviction costs no allocation and a re-sight reuses the slot.</summary>
    public sealed class Entry
    {
        public bool Used;
        public uint Id;
        public byte Type;
        public byte Reaction;

        // ---- cold-resolved, once per sighting ----
        /// <summary>The plate name, already through <see cref="PlateLayout.BreakName"/>: LF is the only
        /// break, and <see cref="NameStart"/>/<see cref="NameLen"/> index its lines. Set through
        /// <see cref="SetName"/> so the spans can never disagree with the string.</summary>
        public string Name = "";
        /// <summary>Spans of <see cref="Name"/>'s lines, so the frame measures and emits each one without a
        /// substring; capped at <see cref="MaxWrapped"/>. These are the WRAPPED lines once
        /// <see cref="EnsureNameWrap"/> has run: a wrap-introduced break is a span boundary and nothing else, so
        /// <see cref="Name"/> itself still carries only the author's breaks (the <c>/tell</c> prefill is safe).</summary>
        public readonly int[] NameStart = new int[MaxWrapped];
        public readonly int[] NameLen = new int[MaxWrapped];
        /// <summary>Per-char packed 0xRRGGBB for <see cref="Name"/>, or null when the name carried no colour
        /// tags (the common case, and the one that allocates nothing). Index-aligned with the string, so the
        /// wrap -- which only ever SLICES it -- keeps them in step. <see cref="PlateLayout.Plain"/> means
        /// "use whatever colour the plate was going to draw with".</summary>
        public uint[]? NameColours;
        public int NameLineCount;
        /// <summary>The name wrapped past <see cref="MaxWrapped"/> lines, so the last kept line ends in
        /// <see cref="Ellipsis"/> (already trimmed to leave room for it).</summary>
        public bool NameTruncated;
        /// <summary>Wrap-cache key for the name spans, mirroring <see cref="BubbleLine"/>'s: the width, the
        /// glyph-atlas epoch and <see cref="Metrics.Scale"/>. -1 means "unwrapped", which is what
        /// <see cref="SetName"/> resets them to.</summary>
        public float NameWrapWidth = -1f, NameWrapScale = -1f;
        public int NameWrapEpoch = -1;
        public string PortraitResref = "";

        // ---- the module's ObjectTextBubbleOverride --------------------
        /// <summary>1 replace, 2 prepend, 3 append, 0 none. Polled every frame in <see cref="Refresh"/>, not
        /// latched on the hook: a player starts sneaking WHILE hovered and the engine re-runs nothing.</summary>
        public int OverrideMode;
        /// <summary>The override verbatim, untrimmed -- the cache key the poll compares against.</summary>
        public string OverrideText = "";
        /// <summary>The override's LF-separated segments, colour-parsed. In <see cref="OverrideMode"/> 3 the FIRST
        /// is the splice that continues the plate's last line and the rest are lines below it; in mode 2 the LAST
        /// splices onto the name and the rest go above; in mode 1 they are the whole plate text. Reuses
        /// <see cref="BubbleLine"/> for runes, colours and wrap cache -- these never expire.</summary>
        public readonly BubbleLine[] Paras = new BubbleLine[MaxParas];
        public int ParaCount;

        // ---- the bubble queue, oldest first ----
        public readonly BubbleLine[] Lines = new BubbleLine[MaxLines];
        public int LineCount;

        // ---- per-frame, filled by Refresh ----
        public float AnchorX, AnchorY;
        public bool Plated;
        public int Damage = -1;
        /// <summary>The health bar's fill 0..1 (<see cref="NameplateNative.Health"/>), or -1 with no band.</summary>
        public float Health = -1f;
        public float Distance;
        public float DistanceFade;
        /// <summary>Set on the frame the entry is drawn as a bubble rather than a plate.</summary>
        public bool ShowBubble;
        /// <summary>Index of the newest live bubble line, or -1.</summary>
        public int TopLine = -1;

        // ---- animation, advanced by Refresh and consumed by the draw ------------------------
        /// <summary>True on the frames the trigger still holds (hovered/Tab-plated, or a live bubble line).
        /// The entry outlives it: it is what <see cref="Alpha"/> eases toward, and only alpha reaching 0
        /// releases the slot.</summary>
        public bool Alive;
        /// <summary>Presence alpha 0..1, eased toward <see cref="Alive"/>. Multiplied into the draw alpha, so a
        /// plate fades in and out instead of popping.</summary>
        public float Alpha;
        /// <summary>Vertical slide residual in draw px, added to the placed box and eased to zero: positive
        /// means "still below where it belongs". Seeded on a newcomer and topped up whenever the measured box
        /// grows, which is how a bubble pushed up the stack animates instead of jumping.</summary>
        public float YOff;
        /// <summary>Last frame's measured box height, to detect the growth the slide absorbs. 0 = unmeasured.</summary>
        public float LastH;
        /// <summary>Set on a freshly claimed slot: the next draw seeds the entry slide and clears it.</summary>
        public bool AnimNew;
        /// <summary>Height of the plate stacked under this entry's bubble last frame (<c>nameplate.whiletalking</c>),
        /// 0 when none. Its drop is absorbed into <see cref="YOff"/>, so the bubble eases down instead of falling.</summary>
        public float LastStackH;
        /// <summary>Last frame drew a bubble. A stacked plate holds its anchor: a plate-only entry that starts talking
        /// lands the bubble on top of it rather than absorbing the growth (which would dip the plate).</summary>
        public bool LastBubble;
        /// <summary>Last frame's separation push in draw px (negative = shoved upward). Kept so a slot change
        /// can be absorbed into <see cref="YOff"/> (<c>PlateLayout.AbsorbPush</c>) instead of teleporting, and
        /// so a box that steps out of separation while <see cref="FadingOut"/> keeps the slot it died in.</summary>
        public float LastPush;

        /// <summary>True while this box is on its way OUT: the trigger has dropped and the presence alpha is
        /// easing to zero, or the bubble's newest line is already in its fade tail. Such a box is excluded from
        /// separation entirely, so sweeping the cursor across a crowd leaves no trail of dying plates shoving the
        /// live one. Specifically NOT "alpha &lt; 1": a box fading IN is arriving and takes part normally.</summary>
        public bool FadingOut => !Alive || (ShowBubble && (TopLine < 0 || Lines[TopLine].Fading));

        /// <summary>The instant this box's content was sent, the key the stack is ordered on. A bubble answers
        /// with its NEWEST line, so an object that keeps talking keeps its place at the bottom of the stack. A
        /// plate has no send time and answers <see cref="long.MaxValue"/> -- a live hover is happening now, so it
        /// sorts ahead of every message and sits nearest its own object.</summary>
        public long SortKey => ShowBubble && TopLine >= 0 ? Lines[TopLine].PostedMs : long.MaxValue;

        public Entry()
        {
            for (int i = 0; i < MaxLines; i++) Lines[i] = new BubbleLine();
            for (int i = 0; i < MaxParas; i++) Paras[i] = new BubbleLine();
        }

        /// <summary>Release the slot. Every animation field goes with it: a pooled entry that kept a
        /// mid-fade alpha would lend it to whatever object claims the slot next.</summary>
        public void Release()
        {
            Used = false; LineCount = 0; TopLine = -1; Damage = -1; Health = -1f; LastStackH = 0f; LastBubble = false;
            OverrideMode = 0; OverrideText = ""; ParaCount = 0;
            Alive = false; ShowBubble = false;
            Alpha = 0f; YOff = 0f; LastH = 0f; LastPush = 0f; AnimNew = true;
            // The pooled lines outlive the entry, so their fade state has to go too: a slot that kept a
            // half-faded line would lend it to whatever object claims the slot next.
            for (int i = 0; i < MaxLines; i++) { Lines[i].Alpha = 0f; Lines[i].Fading = false; }
        }
    }

    private readonly Entry[] _entries = new Entry[MaxEntries];
    private readonly Entry[] _visible = new Entry[MaxEntries];
    private int _visibleCount;

    /// <summary>The entries the frame should draw, compacted by the last <see cref="Refresh"/>.</summary>
    public Entry[] Visible => _visible;
    public int VisibleCount => _visibleCount;

    /// <summary>How many candidates were refused because the table was full. Nonzero says the ceiling is
    /// real; it is reported by <c>/nameplate</c>.</summary>
    public long Overflow { get; private set; }

    public PlateTable() { for (int i = 0; i < MaxEntries; i++) _entries[i] = new Entry(); }

    // ---- the hook-side ring -------------------------------------------------------------------

    private struct Pending
    {
        public uint Id;
        public byte Type;
        public byte Reaction;
        public bool IsPlate;
        public string? Name;
        public uint[]? NameColours;
        public string? Portrait;
        public ColorRun[]? Runs;
        public float DecaySec;
        public int Channel;
        public long PostedMs;
    }

    private readonly Pending[] _pending = new Pending[PendingRing];
    private int _pendHead, _pendTail;
    private readonly object _pendLock = new();

    private void Bank(in Pending p)
    {
        lock (_pendLock)
        {
            int next = (_pendHead + 1) % PendingRing;
            if (next == _pendTail) { Overflow++; return; }   // full: drop the newest, never grow on a hook
            _pending[_pendHead] = p;
            _pendHead = next;
        }
    }

    /// <summary>COLD, from the <c>ShowFeedbackBubbleText</c> path: this object wants a plate. Name, portrait
    /// and reaction are resolved here, on the hook's thread, and never re-read on a frame.</summary>
    public void Note(IntPtr obj, byte type)
    {
        if (obj == IntPtr.Zero) return;
        Pending p = default;
        p.Id = NameplateNative.IdOf(obj);
        p.Type = type;
        p.Reaction = NameplateNative.Reaction(obj, type);
        p.IsPlate = true;
        p.Name = ResolveName(obj, type, p.Id, out uint[]? cols);
        p.NameColours = cols;
        p.Portrait = NameplateNative.Portrait(obj);
        Bank(in p);
    }

    /// <summary>The creature's stats name, else the engine's own object name -- placeables, doors and items
    /// have no creature stats and would otherwise plate as an empty box. COLD.</summary>
    private static string ResolveName(IntPtr obj, byte type, uint id, out uint[]? colours)
    {
        string n = NameplateNative.CreatureName(obj, type);
        if (n.Length == 0) n = Native.ObjectName(id);
        // COLD, and the only place a name acquires either its colours or its line breaks. Strip first: the
        // break rules count spaces, and a `<cRGB>` tag's raw bytes can BE spaces.
        n = PlateLayout.StripColours(n, out uint[]? cols);
        return PlateLayout.BreakName(n, cols, out colours);
    }

    /// <summary>Set an entry's name and re-index its lines together. The one writer of <see cref="Entry.Name"/>:
    /// a slot that kept the previous tenant's spans would draw this one's name in that one's shape. The spans
    /// written here are the author's breaks alone, and <see cref="EnsureNameWrap"/> replaces them on the first
    /// measure. A re-set of the SAME name is a no-op, so a re-entered hook rewraps and allocates nothing.</summary>
    internal static void SetName(Entry e, string name, uint[]? colours = null)
    {
        if (string.Equals(e.Name, name, StringComparison.Ordinal)) { e.NameColours = colours; return; }
        e.Name = name;
        e.NameColours = colours;
        e.NameLineCount = PlateLayout.SplitLines(name, e.NameStart, e.NameLen, MaxWrapped);
        e.NameTruncated = false;
        e.NameWrapWidth = -1f;   // invalidate the wrap cache: the content changed
    }

    /// <summary>Scratch for <see cref="SetOverride"/>'s segment spans. Render thread only -- the poll that
    /// calls it lives in <see cref="Refresh"/>, and nothing on the hook side touches the override.</summary>
    private static readonly int[] _segStart = new int[MaxParas], _segLen = new int[MaxParas];

    /// <summary>Adopt the module's <c>ObjectTextBubbleOverride</c> for this entry: split it into segments and
    /// colour-parse each. A re-set of the same (mode, text) is a no-op, so the per-frame poll pays nothing until a
    /// server flips a status. COLD-on-change but on the RENDER thread: there is no hook to do it on, the object
    /// updates carrying it naming no object. Each segment parses alone, so a <c>&lt;cRGB&gt;</c> cannot leak.</summary>
    internal static void SetOverride(Entry e, int mode, string text)
    {
        if (e.OverrideMode == mode && string.Equals(e.OverrideText, text, StringComparison.Ordinal)) return;
        e.OverrideMode = mode;
        e.OverrideText = text;
        e.ParaCount = 0;
        if (mode is < 1 or > 3 || text.Length == 0) return;

        int n = PlateLayout.SplitOverride(text, _segStart, _segLen, MaxParas);
        for (int i = 0; i < n; i++)
            Fill(e.Paras[i], ColorRunParser.Parse(text.Substring(_segStart[i], _segLen[i])), -1, 0f, 0L);
        e.ParaCount = n;
    }

    /// <summary>COLD, from the <c>SetPopUpBubbleText</c> path when the text is a chat line rather than the
    /// plate's own: queue it against this object. The Aurora colour grammar is parsed here.</summary>
    public void NoteBubble(IntPtr obj, byte type, string text, float decaySec, int channel, long nowMs)
    {
        if (obj == IntPtr.Zero || text.Length == 0) return;
        Pending p = default;
        p.Id = NameplateNative.IdOf(obj);
        p.Type = type;
        p.Reaction = NameplateNative.Reaction(obj, type);
        p.IsPlate = false;
        p.Name = ResolveName(obj, type, p.Id, out uint[]? cols);
        p.NameColours = cols;
        p.Portrait = NameplateNative.Portrait(obj);
        p.Runs = ColorRunParser.Parse(text);   // the Aurora colour grammar, parsed HERE: cold, off the frame
        p.DecaySec = decaySec;
        p.Channel = channel;
        p.PostedMs = nowMs;
        Bank(in p);
    }

    /// <summary>Drop everything. The module-unload edge: every id names a dead world. The cold-resolved
    /// strings go with it -- a pooled entry that kept its portrait resref would lend it to whatever object
    /// claims the slot next (an object whose own <c>GetPortrait</c> returns "" never overwrites it).</summary>
    public void Clear()
    {
        lock (_pendLock) { _pendHead = 0; _pendTail = 0; }
        for (int i = 0; i < MaxEntries; i++)
        {
            Entry e = _entries[i];
            e.Release(); SetName(e, ""); e.PortraitResref = "";
        }
        _visibleCount = 0;
    }

    // ---- draining -----------------------------------------------------------------------------

    /// <summary>Apply everything the hooks banked, without walking the table. The frame calls this even on the
    /// frames it decides not to draw: the ring is fixed-size, so a skipped drain would let it fill and turn
    /// <see cref="Overflow"/> into a count of frames rather than of refused candidates.</summary>
    /// <param name="animMs">the fade a line evicted by a newcomer gets (<c>nameplate.anim</c>).</param>
    public void Drain(int queueCap, float animMs)
    {
        while (true)
        {
            Pending p;
            lock (_pendLock)
            {
                if (_pendTail == _pendHead) return;
                p = _pending[_pendTail];
                _pendTail = (_pendTail + 1) % PendingRing;
            }
            Apply(in p, queueCap, animMs);
        }
    }

    private Entry? Find(uint id)
    {
        for (int i = 0; i < MaxEntries; i++)
            if (_entries[i].Used && _entries[i].Id == id) return _entries[i];
        return null;
    }

    private Entry? Claim(uint id)
    {
        for (int i = 0; i < MaxEntries; i++)
            if (!_entries[i].Used)
            {
                Entry e = _entries[i];
                e.Release();                          // clears the previous tenant's animation state too
                e.Used = true; e.Id = id;
                SetName(e, ""); e.PortraitResref = "";   // a portraitless newcomer never overwrites these
                return e;
            }
        Overflow++;
        return null;
    }

    private void Apply(in Pending p, int queueCap, float animMs = 0f)
    {
        Entry? e = Find(p.Id) ?? Claim(p.Id);
        if (e == null) return;
        e.Type = p.Type;
        e.Reaction = p.Reaction;
        if (p.Name != null) SetName(e, p.Name, p.NameColours);
        if (p.Portrait != null && p.Portrait.Length != 0) e.PortraitResref = p.Portrait;
        if (p.IsPlate || p.Runs == null) return;

        int cap = queueCap < 1 ? 1 : (queueCap > MaxLines ? MaxLines : queueCap);
        // Evict by starting a FADE, not by overwriting: a line pushed out of the queue leaves the way it would
        // have died of old age. A fading line no longer counts against the cap, so it can be held past it --
        // MaxLines is the real ceiling. Loop, not a single step: a cap dragged down mid-life leaves more than
        // one line over it, and the oldest must go first.
        while (LiveCount(e) >= cap)
        {
            int oldest = -1;
            for (int i = 0; i < e.LineCount; i++) if (!e.Lines[i].Fading) { oldest = i; break; }
            if (oldest < 0) break;
            e.Lines[oldest].BeginFade(p.PostedMs, animMs / 1000f);
        }

        // Only when the array itself is full is a line dropped hard -- the newcomer is never refused, and the
        // casualty is the oldest, which is already deep in its fade.
        if (e.LineCount >= MaxLines)
        {
            BubbleLine gone = e.Lines[0];
            for (int i = 0; i + 1 < e.LineCount; i++) e.Lines[i] = e.Lines[i + 1];
            e.Lines[e.LineCount - 1] = gone;
            e.LineCount--;
        }
        BubbleLine ln = e.Lines[e.LineCount];
        Fill(ln, p.Runs, p.Channel, p.DecaySec, p.PostedMs);
        if (NameplateNative.SpeakerPrefixed(p.Type)) ln.Prefix = PlateLayout.SpeakerPrefixLen(ln.Runes);
        e.LineCount++;
    }

    /// <summary>Lines still counted against the queue cap -- everything not already in its fade tail.</summary>
    private static int LiveCount(Entry e)
    {
        int n = 0;
        for (int i = 0; i < e.LineCount; i++) if (!e.Lines[i].Fading) n++;
        return n;
    }

    /// <summary>Flatten already-parsed colour runs into the line's rune + per-rune-colour buffers. Called
    /// from the drain, so it must not parse or allocate: the runs arrive pre-parsed from the hook thread and
    /// the buffers are preallocated.</summary>
    private static void Fill(BubbleLine ln, ColorRun[] runs, int channel, float decaySec, long postedMs)
    {
        ln.Runes.Clear();
        ln.Channel = channel;
        ln.HoldSec = decaySec * HoldFraction;
        ln.FadeSec = decaySec - ln.HoldSec;
        ln.PostedMs = postedMs;
        ln.Alpha = 1f;
        ln.Fading = false;
        ln.Truncated = false;
        ln.Prefix = 0;
        ln.WrapWidth = -1f;   // invalidate the wrap cache: the content changed

        int need = 0;
        for (int i = 0; i < runs.Length; i++) need += runs[i].Text.Length;
        if (ln.Colours.Length < need + 1) ln.Colours = new uint[need + 1];

        int n = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            uint rgb = Pack(runs[i].R, runs[i].G, runs[i].B);
            foreach (Rune r in runs[i].Text.EnumerateRunes())
            {
                if (n >= ln.Colours.Length) break;
                ln.Runes.Add(r);
                ln.Colours[n++] = rgb;
            }
        }
    }

    private static uint Pack(float r, float g, float b)
        => ((uint)(r * 255f + 0.5f) << 16) | ((uint)(g * 255f + 0.5f) << 8) | (uint)(b * 255f + 0.5f);

    // ---- the per-frame walk -------------------------------------------------------------------

    /// <summary>HOT. Drains the hook ring, then walks every live entry: re-resolves the id to a live object,
    /// evicts on a miss, reads the anchor / flags / damage byte, applies the distance fade, and leaves the
    /// survivors compacted into <see cref="Visible"/>. Allocates nothing.</summary>
    /// <param name="dtMs">ms since the previous refresh, driving every ease; <paramref name="maxDist"/> is metres (0 = unlimited).</param>
    public void Refresh(long nowMs, long dtMs, float animMs, float maxDist, float fadeDist, int queueCap,
                        float vpW, float vpH)
    {
        Drain(queueCap, animMs);
        _visibleCount = 0;
        if (!NameplateNative.Ready) return;

        bool hasCam = NameplateNative.CameraPosition(out float cx, out float cy, out float cz);
        bool hasFwd = NameplateNative.CameraForward(out float fx, out float fy, out float fz);
        // The rect the engine projected into -- NOT necessarily the one we draw in (vpW/vpH).
        if (!NameplateNative.ViewportRect(out float rx, out float ry, out float rw, out float rh))
        { rx = 0f; ry = 0f; rw = vpW; rh = vpH; }
        float gui = Metrics.GameUiScale;
        uint playerArea = NameplateNative.PlayerAreaId();
        float step = PlateLayout.FadeStep(dtMs, animMs);

        for (int i = 0; i < MaxEntries; i++)
        {
            Entry e = _entries[i];
            if (!e.Used) continue;

            IntPtr obj = NameplateNative.GameObject(e.Id);
            if (obj == IntPtr.Zero) { e.Release(); continue; }   // despawn: the object is gone, nothing to fade

            ExpireLines(e, nowMs);
            e.Plated = NameplateNative.Plated(obj, e.Type);

            // The override changes under a live plate -- a player starts sneaking mid-hover and the engine
            // re-runs nothing we hook. So it is POLLED, not latched: a mode read plus a byte-vs-cached compare,
            // both allocation-free, and only the change edge reaches SetOverride.
            int om = NameplateNative.OverrideMode(obj);
            if (om != e.OverrideMode || !NameplateNative.OverrideTextIs(obj, e.OverrideText))
                SetOverride(e, om, NameplateNative.OverrideText(obj));
            e.Alive = (e.Plated || e.LineCount > 0)
                      && !NameplateNative.Retired(obj)                                       // acceptance §9
                      && !(e.Type == NameplateNative.TypeCreature && playerArea != 0
                           && NameplateNative.CreatureAreaId(obj, e.Type) != playerArea);    // acceptance §10

            // Advance BEFORE any per-frame gate: an entry that goes off-screen mid-fade must still reach 0
            // and release its slot, or the table leaks until the object despawns.
            PlateLayout.MoveToward(ref e.Alpha, e.Alive ? 1f : 0f, step);
            PlateLayout.SettleOffset(ref e.YOff, dtMs, animMs);
            if (!e.Alive && e.Alpha <= 0f) { e.Release(); continue; }

            if (!NameplateNative.AnchorRaw(obj, out float ax, out float ay)) continue;
            PlateLayout.ToDrawSpace(ax, ay, rx, ry, rw, rh, gui, vpW, vpH, out e.AnchorX, out e.AnchorY);
            if (e.AnchorX < 0f || e.AnchorY < 0f || e.AnchorX > vpW || e.AnchorY > vpH) continue;

            e.DistanceFade = 1f;
            e.Distance = 0f;
            if (hasCam)
            {
                NameplateNative.Position(obj, out float ox, out float oy, out float oz);
                float dx = ox - cx, dy = oy - cy, dz = oz - cz;
                // The engine's projection never checks the sign of its denominator, so an object BEHIND the
                // camera lands on a mirrored anchor that passes the screen-rect test above.
                if (hasFwd && dx * fx + dy * fy + dz * fz <= 0f) continue;
                e.Distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                e.DistanceFade = PlateLayout.DistanceFade(e.Distance, maxDist, fadeDist);
                if (e.DistanceFade <= 0f) continue;
            }

            e.Damage = NameplateNative.DamageLevel(obj, e.Type);
            e.Health = NameplateNative.Health(obj, e.Type, e.Damage);
            // A live chat line wins over the plate, and un-hovering does not cut it short.
            // (Vanilla enforces that through bubble+0x14c, which our own reset clears -- see the hook.)
            // Latched while alive: a dying bubble must not flip to plate chrome half way through its fade.
            if (e.Alive) e.ShowBubble = e.TopLine >= 0;
            _visible[_visibleCount++] = e;
        }
    }

    /// <summary>Every slot, live or not -- the <c>/nameplate probe</c> door, so the report can show entries the
    /// per-frame gates rejected. Nothing else may walk this: <see cref="Visible"/> is the drawable set.</summary>
    public Entry[] Entries => _entries;

    /// <summary>Age every bubble line into its own alpha, drop the ones that reached zero and pick the newest
    /// survivor. A line past its hold is NOT dropped -- it stays, fading, which is the whole point. Hot, no
    /// allocation; the compaction is a stable swap-to-boundary so the queue keeps its posted order.</summary>
    private static void ExpireLines(Entry e, long nowMs)
    {
        int live = 0;
        for (int i = 0; i < e.LineCount; i++)
        {
            BubbleLine ln = e.Lines[i];
            ln.Alpha = PlateLayout.BubbleAlpha(nowMs, ln.PostedMs, ln.HoldSec, ln.FadeSec);
            if (ln.Alpha < 1f) ln.Fading = true;
            if (ln.Alpha > 0f)
            {
                if (live != i) { BubbleLine tmp = e.Lines[live]; e.Lines[live] = ln; e.Lines[i] = tmp; }
                live++;
            }
        }
        e.LineCount = live;
        e.TopLine = live - 1;
    }

    /// <summary>COLD-on-change: (re)wrap a bubble line to <paramref name="pixelW"/>. Keyed on the width, the face
    /// stamp (<paramref name="epoch"/>, which moves on a face pick and every re-bake) and
    /// <see cref="Metrics.Scale"/>, since a new face changes every advance; a real change pays an allocation.</summary>
    /// <param name="skip">runes at the head the wrap leaves out; the spans stay indices into the whole rune list.</param>
    public static void EnsureWrap(BubbleLine ln, GlyphAtlas atlas, float pixelW, int epoch, int maxLines, int skip = 0)
    {
        int cap = maxLines < 1 ? 1 : (maxLines > MaxWrapped ? MaxWrapped : maxLines);
        if (skip < 0 || skip >= ln.Runes.Count) skip = 0;
        if (ln.WrapWidth == pixelW && ln.WrapEpoch == epoch && ln.WrapScale == Metrics.Scale
            && ln.WrapCap == cap && ln.WrapSkip == skip) return;
        ln.WrapWidth = pixelW; ln.WrapEpoch = epoch; ln.WrapScale = Metrics.Scale; ln.WrapCap = cap; ln.WrapSkip = skip;

        List<Rune> src = skip == 0 ? ln.Runes : ln.Runes.GetRange(skip, ln.Runes.Count - skip);
        List<TextWrapper.LineSpan> spans = TextWrapper.WrapRunes(src, pixelW, atlas.AdvanceOfCached);
        int n = spans.Count < cap ? spans.Count : cap;
        for (int i = 0; i < n; i++) ln.Spans[i] = new TextWrapper.LineSpan(spans[i].Start + skip, spans[i].End + skip);
        ln.SpanCount = n;
        ln.Truncated = spans.Count > n;
        ln.WrapPixelW = FitSpans(ln, pixelW, atlas.AdvanceOfCached);
    }

    /// <summary>COLD-on-change: (re)wrap the entry's NAME to <paramref name="pixelW"/> on top of the author's own
    /// breaks, with <see cref="EnsureWrap"/>'s key discipline. The wrap honours the LF breaks
    /// <see cref="PlateLayout.BreakName"/> put in and only ever ADDS more, written as spans over the unchanged
    /// string. It speaks RUNE indices while the spans hold CHAR ones. <paramref name="pixelW"/> &lt;= 0: no wrap.</summary>
    public static void EnsureNameWrap(Entry e, Func<Rune, float> advanceOf, float pixelW, int epoch)
    {
        if (e.NameWrapWidth == pixelW && e.NameWrapEpoch == epoch && e.NameWrapScale == Metrics.Scale) return;
        e.NameWrapWidth = pixelW; e.NameWrapEpoch = epoch; e.NameWrapScale = Metrics.Scale;

        e.NameTruncated = false;
        if (e.Name.Length == 0) { e.NameLineCount = 0; return; }
        if (!(pixelW > 0f))
        {
            e.NameLineCount = PlateLayout.SplitLines(e.Name, e.NameStart, e.NameLen, MaxWrapped);
            return;
        }

        var runes = new List<Rune>(e.Name.Length);
        var at = new List<int>(e.Name.Length + 1);
        for (int ci = 0; ci < e.Name.Length; )
        {
            Rune.DecodeFromUtf16(e.Name.AsSpan(ci), out Rune r, out int used);
            runes.Add(r); at.Add(ci); ci += used;
        }
        at.Add(e.Name.Length);

        List<TextWrapper.LineSpan> spans = TextWrapper.WrapRunes(runes, pixelW, advanceOf);
        int keep = spans.Count < MaxWrapped ? spans.Count : MaxWrapped;
        e.NameTruncated = spans.Count > keep;

        if (e.NameTruncated && keep > 0)
        {
            // Trim on RUNES, not chars: a char-level trim can split a surrogate pair.
            TextWrapper.LineSpan last = spans[keep - 1];
            float dots = Ellipsis.Length * advanceOf(new Rune('.'));
            float w = 0f;
            for (int k = last.Start; k < last.End; k++) w += advanceOf(runes[k]);
            int end = last.End;
            while (end > last.Start && w + dots > pixelW) w -= advanceOf(runes[--end]);
            spans[keep - 1] = new TextWrapper.LineSpan(last.Start, end);
        }

        int n = 0;
        for (int i = 0; i < keep; i++)
        {
            int cs = at[spans[i].Start], ce = at[spans[i].End];
            if (ce <= cs && !(e.NameTruncated && i == keep - 1)) continue;   // the wrap ate the whole line
            e.NameStart[n] = cs; e.NameLen[n] = ce - cs; n++;
        }
        e.NameLineCount = n;
    }

    /// <summary>The glyphs a cut line ends in. Three ASCII dots, not U+2026: the atlas bakes named ranges and
    /// the ellipsis codepoint is not guaranteed to be in one of them.</summary>
    public const string Ellipsis = "...";

    /// <summary>Measure the wrapped spans, trimming the last one to make room for <see cref="Ellipsis"/> when
    /// the line was cut, and return the widest kept line. Pure -- the advance comes from the caller -- so the
    /// self-check exercises the trim without an atlas.</summary>
    internal static float FitSpans(BubbleLine ln, float pixelW, Func<Rune, float> advanceOf)
    {
        int n = ln.SpanCount;
        if (n <= 0) return 0f;

        float dots = Ellipsis.Length * advanceOf(new Rune('.'));
        if (ln.Truncated)
        {
            TextWrapper.LineSpan last = ln.Spans[n - 1];
            float w = 0f;
            for (int k = last.Start; k < last.End; k++) w += advanceOf(ln.Runes[k]);
            int end = last.End;
            while (end > last.Start && w + dots > pixelW) w -= advanceOf(ln.Runes[--end]);
            ln.Spans[n - 1] = new TextWrapper.LineSpan(last.Start, end);
        }

        float widest = 0f;
        for (int i = 0; i < n; i++)
        {
            float w = 0f;
            for (int k = ln.Spans[i].Start; k < ln.Spans[i].End; k++) w += advanceOf(ln.Runes[k]);
            if (ln.Truncated && i == n - 1) w += dots;
            if (w > widest) widest = w;
        }
        return widest;
    }
}
