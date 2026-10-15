using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Render controls for server NUI windows, applied to nuklear's command queue before <see cref="NuiFlush"/> converts it:
/// opacity, the alpha of a window's body fill and border, and a fade in. Each scales the alpha byte of that window's own
/// commands. A window is keyed by its nuklear name; the keys are <c>nui.&lt;id&gt;.opacity | background | fadein | fadeout</c>.
/// </summary>
internal static unsafe class NuiWindows
{
    /// <summary>nuklear's <c>NK_WINDOW_HIDDEN</c>; <c>nk__begin</c> skips a window carrying it.</summary>
    private const uint WinHidden = 0x4000;   // not-an-offset: nk_window_flags NK_WINDOW_HIDDEN
    private const int NameBytes = 64;
    private const string JsonPrefix = "JsonWindow::";
    /// <summary>A command walk longer than this is a corrupt chain, not a window.</summary>
    private const int MaxCommands = 1 << 20;
    private const ulong FnvBasis = 14695981039346656037, FnvPrime = 1099511628211;   // not-an-offset: FNV-1a 64
    /// <summary>A window's commands are a few KiB; past this one is not copied for a fade out.</summary>
    private const int MaxSnapshot = 1 << 20;
    /// <summary>More than <c>nk_window</c>'s size, which is under 0x200 on both builds.</summary>
    private const int FakeWindowBytes = 0x400;   // not-an-offset: allocation size

    /// <summary>The <c>nk_command_type</c>s this pass reads by kind: the clip, the filled rectangle and the image.</summary>
    private const int CmdScissor = 1, CmdRectFilled = 5, CmdImage = 17;

    /// <summary>One window's resolved properties. <see cref="Epoch"/> is the policy epoch they were read at.</summary>
    internal sealed unsafe class Style
    {
        public string Id = "";
        public float Opacity = 1f, Background = 1f, FadeInMs, FadeOutMs;
        public int Epoch = -1;
        public long OpenedAt, LastFrame = -2, GhostAt;
        public bool Said;
        public bool Plain => Opacity >= 1f && Background >= 1f && FadeInMs <= 0f && FadeOutMs <= 0f;
        /// <summary>The window's last frame of commands (offsets relative to its start), kept while it has a fade out; and the
        /// scratch copy and stand-in window a ghost draws from. Unmanaged: nuklear reads them during the conversion.</summary>
        public byte* Snap, Work, Fake;
        public int SnapLen, SnapCap, WorkCap;
        public ulong SnapLast;
        public float Bx, By, Bw, Bh;
        public bool SnapValid;
    }

    /// <summary>Everything this file remembers, replaced whole when the context is lost or a session starts.</summary>
    private sealed class NuiWindowsState
    {
        public readonly Dictionary<ulong, Style> Styles = new();
        public readonly Dictionary<string, string> Probe = new(StringComparer.OrdinalIgnoreCase);
        public long Frame;
        public string ProbeText = "";
        public int Applied, KeysEpoch = -1;
        public bool HasKeys, Linked;
        /// <summary>While ghosts are linked: the real window list's last window (zero = the list was empty) and the real queue end.</summary>
        public IntPtr RealTail;
        public ulong RealAllocated;
        public ExtenderLog? Log;
    }

    private static NuiWindowsState _s = new();
    internal static void Init(ExtenderLog log) => _s.Log = log;

    /// <summary>The policy keys of one window. Cold: allocates, runs when a window is first seen and when the policy moves.</summary>
    internal static void Read(Style st, Func<string, string?> get)
    {
        st.Opacity = Math.Clamp(Num(get, st.Id, "opacity", 1f), 0f, 1f);
        st.Background = Math.Clamp(Num(get, st.Id, "background", 1f), 0f, 1f);
        st.FadeInMs = Math.Max(0f, Num(get, st.Id, "fadein", 0f));
        st.FadeOutMs = Math.Max(0f, Num(get, st.Id, "fadeout", 0f));
    }

    internal static string Key(string id, string prop) => "nui." + id + "." + prop;

    private static float Num(Func<string, string?> get, string id, string prop, float def)
    {
        string? s = get(Key(id, prop));
        if (s == null) return def;
        if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)) return f;
        PolicyRefusals.Add(RefusalKind.Window, Key(id, prop), s, "not a number");
        return def;
    }

    private static string? Lookup(string key)
        => ServerPolicy.TryGet(key, out string v) ? v : _s.Probe.TryGetValue(key, out string? pv) ? pv : null;

    /// <summary>The debug knob's <c>key=value;key=value</c> text as a fallback under the server's own keys, so the live
    /// harness can drive the same keys without a server.</summary>
    internal static void SetProbe(string text)
    {
        if (text == _s.ProbeText) return;
        _s.ProbeText = text;
        _s.Probe.Clear();
        foreach (string line in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) _s.Probe[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        foreach (Style st in _s.Styles.Values) st.Epoch = -1;
    }

    /// <summary>Does any window have a property to apply? False skips the walk.</summary>
    private static bool AnyKeys()
    {
        if (_s.Probe.Count > 0) return true;
        int epoch = ServerPolicy.Epoch;
        if (epoch != _s.KeysEpoch) { _s.KeysEpoch = epoch; _s.HasKeys = ServerPolicy.HasPrefix("nui."); }
        return _s.HasKeys;
    }

    internal static ulong Hash(byte* name)
    {
        ulong h = FnvBasis;
        for (int i = 0; i < NameBytes - 1 && name[i] != 0; i++) h = (h ^ name[i]) * FnvPrime;
        return h;
    }

    /// <summary>Apply every property to this frame's windows. Hot while a policy is held, allocation-free once each
    /// window name has been seen. Once per frame, before the conversion and outside its retry loop.</summary>
    internal static void Apply(IntPtr ctx, int atlas, bool enabled, string probe)
    {
        if (!enabled) return;
        SetProbe(probe);
        if (!AnyKeys()) return;
        var c = new NkContext(ctx);
        long now = Stopwatch.GetTimestamp();
        long frame = ++_s.Frame;
        int epoch = ServerPolicy.Epoch;
        byte* mem = (byte*)c.MemPtr;
        ulong allocated = c.MemAllocated;
        uint seq = c.Seq;
        for (IntPtr w = c.WindowBegin; w != IntPtr.Zero; w = new NkWindow(w).Next)
        {
            var win = new NkWindow(w);
            if (win.Seq != seq || (win.Flags & WinHidden) != 0 || win.BufBegin >= win.BufEnd) continue;
            Style st = StyleOf(win, epoch);
            bool opened = st.LastFrame != frame - 1;
            st.LastFrame = frame;
            if (opened) st.OpenedAt = now;
            if (st.Plain) continue;
            if (st.FadeOutMs > 0f) Snapshot(st, mem, win, allocated);
            float f = st.Opacity;
            if (st.FadeInMs > 0f)
            {
                float t = (now - st.OpenedAt) * 1000f / Stopwatch.Frequency / st.FadeInMs;
                f *= Math.Clamp(t, 0f, 1f);
            }
            if (f >= 1f && st.Background >= 1f) continue;
            int found = ScaleWindow(mem, allocated, w, f, st.Background);
            _s.Applied++;
            if (!st.Said && st.Background < 1f)
            {
                st.Said = true;
                _s.Log?.Info($"NuiWindows: '{st.Id}' background {st.Background}: {(found != 0 ? "chrome up to the content fill" : "no engine chrome, nothing faded")}");
            }
        }
        LinkGhosts(c, mem, now, frame, atlas);
    }


    /// <summary>A closed window's ghost: its last commands, copied into scratch memory outside the queue and linked as
    /// one more window on top for the length of the fade out. A stand-in <c>nk_window</c> carries them, and the queue's
    /// end is lifted for the conversion so offsets that reach past it still walk. <see cref="Finish"/> undoes both.</summary>
    private static void LinkGhosts(NkContext c, byte* mem, long now, long frame, int atlas)
    {
        IntPtr tail = IntPtr.Zero;
        for (IntPtr w = c.WindowBegin; w != IntPtr.Zero; w = new NkWindow(w).Next) tail = w;
        IntPtr prev = tail;
        foreach (Style st in _s.Styles.Values)
        {
            if (st.FadeOutMs <= 0f || !st.SnapValid) continue;
            if (st.LastFrame == frame) { st.GhostAt = 0; continue; }
            if (st.LastFrame == frame - 1 && st.GhostAt == 0) st.GhostAt = now;
            if (st.GhostAt == 0) continue;
            float t = (now - st.GhostAt) * 1000f / Stopwatch.Frequency / st.FadeOutMs;
            if (t >= 1f) { st.GhostAt = 0; st.SnapValid = false; continue; }
            IntPtr fake = BuildGhost(st, mem, c.Seq, atlas, st.Opacity * (1f - t));
            if (!_s.Linked) { _s.Linked = true; _s.RealTail = tail; _s.RealAllocated = c.MemAllocated; c.MemAllocated = ulong.MaxValue; }
            if (prev == IntPtr.Zero) c.WindowBegin = fake; else new NkWindow(prev).Next = fake;
            prev = fake;
        }
    }

    /// <summary>Put the window list and the queue end back as the engine left them. After the conversion, before the clear.</summary>
    internal static void Finish(IntPtr ctx)
    {
        if (!_s.Linked) return;
        var c = new NkContext(ctx);
        if (_s.RealTail == IntPtr.Zero) c.WindowBegin = IntPtr.Zero; else new NkWindow(_s.RealTail).Next = IntPtr.Zero;
        c.MemAllocated = _s.RealAllocated;
        _s.Linked = false;
    }

    private static void Grow(ref byte* p, ref int cap, int need)
    {
        if (cap >= need) return;
        if (p != null) NativeMemory.Free(p);
        cap = Math.Max(need, cap * 2);
        p = (byte*)NativeMemory.Alloc((nuint)cap);
    }

    /// <summary>Copy the window's commands out, each <c>next</c> made relative to the first. Before the frame's scaling, so
    /// the copy keeps the colours the window drew with.</summary>
    private static void Snapshot(Style st, byte* mem, NkWindow win, ulong allocated)
    {
        ulong begin = win.BufBegin, end = Math.Min(win.BufEnd, allocated);
        if (end <= begin || end - begin > MaxSnapshot) { st.SnapValid = false; return; }
        int len = (int)(end - begin);
        Grow(ref st.Snap, ref st.SnapCap, len);
        Buffer.MemoryCopy((void*)new NkQueue(mem).Begin(win).Ptr, st.Snap, len, len);
        int pos = 0;
        var snap = new NkQueue(st.Snap);
        NkCommand cmd = snap.Head;
        while (pos < len)
        {
            ulong rel = cmd.Next - begin;
            cmd.Next = rel;
            if (rel <= (ulong)pos || rel >= (ulong)len) break;
            pos = (int)rel;
            cmd = snap.After(cmd);
        }
        st.SnapLen = len;
        st.SnapLast = win.BufLast - begin;
        st.Bx = win.Bounds[0]; st.By = win.Bounds[1]; st.Bw = win.Bounds[2]; st.Bh = win.Bounds[3];
        st.SnapValid = true;
    }

    /// <summary>The stand-in window over a scaled copy of the snapshot. Images other than the font atlas are blanked: their
    /// textures die with the window, and a name the engine has freed draws garbage.</summary>
    private static IntPtr BuildGhost(Style st, byte* mem, uint seq, int atlas, float factor)
    {
        Grow(ref st.Work, ref st.WorkCap, st.SnapLen);
        if (st.Fake == null) st.Fake = (byte*)NativeMemory.AllocZeroed(FakeWindowBytes);
        Buffer.MemoryCopy(st.Snap, st.Work, st.SnapLen, st.SnapLen);
        ulong at = (ulong)(st.Work - mem);
        int pos = 0;
        var work = new NkQueue(st.Work);
        NkCommand cmd = work.Head;
        while (pos < st.SnapLen)
        {
            ulong rel = cmd.Next;
            NkCommand following = work.After(cmd);
            cmd.Next = at + rel;
            if (cmd.Type == CmdImage && new NkCommandImage(cmd.Ptr).Handle != atlas) new NkCommandImage(cmd.Ptr).Color.A = 0;
            if (rel <= (ulong)pos || rel >= (ulong)st.SnapLen) break;
            pos = (int)rel;
            cmd = following;
        }
        var fake = new NkWindow((IntPtr)st.Fake);
        fake.Seq = seq;
        fake.Flags = 0;
        fake.BufBegin = at;
        fake.BufEnd = at + (ulong)st.SnapLen;
        fake.BufLast = at + st.SnapLast;
        fake.Next = IntPtr.Zero;
        fake.Bounds[0] = st.Bx; fake.Bounds[1] = st.By; fake.Bounds[2] = st.Bw; fake.Bounds[3] = st.Bh;
        ScaleWindow(mem, ulong.MaxValue, (IntPtr)st.Fake, factor, st.Background);
        return (IntPtr)st.Fake;
    }

    private static Style StyleOf(NkWindow win, int epoch)
    {
        ulong h;
        fixed (byte* n = win.Name) h = Hash(n);
        if (!_s.Styles.TryGetValue(h, out Style? st))
        {
            st = new Style();
            fixed (byte* n = win.Name) st.Id = NameOf(n);
            _s.Styles[h] = st;
            _s.Log?.Info($"NuiWindows: window '{st.Id}' seen");
        }
        if (st.Epoch != epoch) { Read(st, Lookup); st.Epoch = epoch; }
        return st;
    }

    private static string NameOf(byte* n)
    {
        int len = 0;
        while (len < NameBytes - 1 && n[len] != 0) len++;
        return IdOf(System.Text.Encoding.UTF8.GetString(n, len));
    }

    /// <summary>NUI begins its windows as <c>JsonWindow::&lt;id&gt;</c>; the id is what the server chose.</summary>
    internal static string IdOf(string name) => name.StartsWith(JsonPrefix, StringComparison.Ordinal) ? name[JsonPrefix.Length..] : name;

    /// <summary>Scale one window's commands: every colour by <paramref name="all"/>, the engine's own chrome by
    /// <paramref name="background"/> too. <paramref name="mem"/> is the queue base. Returns 1 if the window has engine chrome.</summary>
    internal static int ScaleWindow(byte* mem, ulong allocated, IntPtr window, float all, float background)
    {
        var win = new NkWindow(window);
        ulong off = win.BufBegin, end = Math.Min(win.BufEnd, allocated);
        ulong chromeEnd = 0;
        var queue = new NkQueue(mem);
        bool chrome = background < 1f && FindChromeEnd(queue, win, end, out chromeEnd);
        NkCommand cmd = queue.Begin(win);
        for (int n = 0; off < end && n < MaxCommands; n++)
        {
            float f = all;
            if (chrome && off <= chromeEnd) f *= background;
            if (f < 1f) ScaleCommand(cmd, f);
            ulong next = cmd.Next;
            if (next <= off) break;
            off = next;
            cmd = queue.After(cmd);
        }
        return chrome ? 1 : 0;
    }

    /// <summary>The engine draws a window's title bar, frame and body before it first clips to the content, then fills the content
    /// rectangle once more. A window with no such commands before its first scissor (no title bar, border or fill) has no chrome, and what
    /// follows is the server's. The chrome ends at that content fill, or at the scissor when no fill follows it.</summary>
    private static bool FindChromeEnd(NkQueue queue, NkWindow win, ulong end, out ulong at)
    {
        at = 0;
        ulong off = win.BufBegin;
        NkCommand cmd = queue.Begin(win);
        bool drawn = false;
        for (int n = 0; off < end && n < MaxCommands; n++)
        {
            int type = cmd.Type;
            ulong next = cmd.Next;
            if (type == CmdScissor)
            {
                if (!drawn) return false;
                at = off;
                if (next > off && next < end && queue.After(cmd).Type == CmdRectFilled)
                {
                    var fill = new NkCommandRectFilled(queue.After(cmd).Ptr);
                    var clip = new NkCommandScissor(cmd.Ptr);
                    if (fill.X == clip.X && fill.Y == clip.Y && fill.W == clip.W && fill.H == clip.H) at = next;
                }
                return true;
            }
            drawn = true;
            if (next <= off) break;
            off = next;
            cmd = queue.After(cmd);
        }
        return false;
    }

    /// <summary>Scale the alpha byte of every colour in one command.</summary>
    internal static void ScaleCommand(NkCommand cmd, float f)
    {
        foreach (ref NkColor c in cmd.Colours) Alpha(ref c, f);
    }

    private static void Alpha(ref NkColor c, float f) => c.A = (byte)(c.A * f + 0.5f);

    internal static string Stats() => _s.Styles.Count == 0 ? "nui windows: none seen" : $"nui windows: {_s.Styles.Count} seen, {_s.Applied} styled draws";

    /// <summary>Off-device. Oracle: command layouts laid out byte by byte from nuklear's structs, and alpha values worked
    /// out by hand.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var saved = _s;
        _s = new() { Log = saved.Log };
        try
        {
            var st = new Style { Id = "inv" };
            var kv = new Dictionary<string, string> { [Key("inv", "opacity")] = "0.5", [Key("inv", "background")] = "2", [Key("inv", "fadein")] = "150" };
            Read(st, k => kv.TryGetValue(k, out string? v) ? v : null);
            check.Expect(st.Opacity == 0.5f && st.Background == 1f && st.FadeInMs == 150f, "opacity read, background clamped to 1, fade in read");
            kv[Key("inv", "opacity")] = "banana";
            Read(st, k => kv.TryGetValue(k, out string? v) ? v : null);
            check.Expect(st.Opacity == 1f, "a bad number falls back to opaque");
            check.Expect(new Style().Plain && !new Style { Opacity = 0.9f }.Plain, "a default window needs no pass");

            check.Expect(IdOf("JsonWindow::cxladder_nui") == "cxladder_nui" && IdOf("ContentIndex::Browser") == "ContentIndex::Browser", "a server window's id is its name past the JsonWindow:: prefix");
            byte* name = stackalloc byte[NameBytes];
            name[0] = (byte)'a'; name[1] = (byte)'b';
            check.Expect(Hash(name) == 0x089c4407b545986a, "FNV-1a 64 of 'ab'");

            // Commands laid out byte by byte from nuklear's structs, literal offsets: type @0, next @8; a filled rect's
            // box @0x12 and colour @0x1a; a stroked rect's box @0x14 and colour @0x1c; text colours @0x18, @0x1c;
            // an image's colour @0x30; a scissor's box @0x10. Three commands: a window-sized fill, its bounds, a text.
            const int S = CmdSlot;
            byte[] q = new byte[8 * S];
            fixed (byte* mem = q)
            {
                Head(q, 0, 5); Box(q, 0, 0x12, 100, 200, 300, 150); AlphaAt(q, 0, 0x1a) = 200;
                Head(q, 1, 4); Box(q, 1, 0x14, 100, 200, 300, 150); AlphaAt(q, 1, 0x1c) = 100;
                Head(q, 2, 16); AlphaAt(q, 2, 0x18) = 80; AlphaAt(q, 2, 0x1c) = 255;
                byte* win = stackalloc byte[0x200];
                new Span<byte>(win, 0x200).Clear();
                var wnd = new NkWindow((IntPtr)win);
                wnd.BufBegin = 0; wnd.BufEnd = 3 * S;
                wnd.Bounds[0] = 100; wnd.Bounds[1] = 200; wnd.Bounds[2] = 300; wnd.Bounds[3] = 150;
                check.Expect(ScaleWindow(mem, 4 * S, (IntPtr)win, 0.5f, 1f) == 0, "no background asked, none reported found");
                check.Expect(AlphaAt(q, 0, 0x1a) == 100 && AlphaAt(q, 1, 0x1c) == 50 && AlphaAt(q, 2, 0x18) == 40 && AlphaAt(q, 2, 0x1c) == 128, "opacity 0.5 halves every colour's alpha (200->100, 100->50, 80->40, 255->128)");
                AlphaAt(q, 0, 0x1a) = 200; AlphaAt(q, 1, 0x1c) = 100; AlphaAt(q, 2, 0x18) = 80; AlphaAt(q, 2, 0x1c) = 255;
                check.Expect(ScaleWindow(mem, 4 * S, (IntPtr)win, 1f, 0f) == 0, "a window with no chrome before its first clip has no engine chrome");
                check.Expect(AlphaAt(q, 0, 0x1a) == 200 && AlphaAt(q, 1, 0x1c) == 100 && AlphaAt(q, 2, 0x18) == 80, "background 0 leaves the server's window-sized fill, its stroked bounds and its text");
                AlphaAt(q, 0, 0x1a) = 200; AlphaAt(q, 1, 0x1c) = 100;
                ScaleWindow(mem, 4 * S, (IntPtr)win, 0.5f, 0.5f);
                check.Expect(AlphaAt(q, 0, 0x1a) == 100 && AlphaAt(q, 1, 0x1c) == 50 && AlphaAt(q, 2, 0x1c) == 128, "opacity alone fades what the server drew (200*0.5, 100*0.5)");
                wnd.BufEnd = S;
                AlphaAt(q, 0, 0x1a) = 200; AlphaAt(q, 1, 0x1c) = 100;
                ScaleWindow(mem, 4 * S, (IntPtr)win, 0.5f, 1f);
                check.Expect(AlphaAt(q, 0, 0x1a) == 100 && AlphaAt(q, 1, 0x1c) == 100, "the walk stops at the window's end");
                // A skinned window: header image, title text, body fill, frame image, content scissor, the same rect filled, a scissor, a widget text.
                Array.Clear(q);
                int[] kinds = [17, 16, 5, 17, 1, 5, 1, 16];
                for (int i = 0; i < 8; i++) Head(q, i, kinds[i]);
                AlphaAt(q, 0, 0x30) = 255; AlphaAt(q, 1, 0x1c) = 255; Box(q, 2, 0x12, 100, 200, 300, 150); AlphaAt(q, 2, 0x1a) = 255;
                AlphaAt(q, 3, 0x30) = 255;
                Box(q, 4, 0x10, 104, 204, 292, 142);
                Box(q, 5, 0x12, 104, 204, 292, 142); AlphaAt(q, 5, 0x1a) = 255;
                AlphaAt(q, 7, 0x1c) = 255;
                wnd.BufBegin = 0; wnd.BufEnd = 8 * S;
                check.Expect(ScaleWindow(mem, 8 * S, (IntPtr)win, 1f, 0f) == 1, "the skinned chrome is found by the content fill");
                check.Expect(AlphaAt(q, 0, 0x30) == 0 && AlphaAt(q, 1, 0x1c) == 0 && AlphaAt(q, 2, 0x1a) == 0 && AlphaAt(q, 3, 0x30) == 0 && AlphaAt(q, 5, 0x1a) == 0,
                             "background 0 clears the title bar, frame, body and content fill");
                check.Expect(AlphaAt(q, 7, 0x1c) == 255, "...and leaves the widget text");
                // A server fill the size of the window right after the content fill's scissor is the server's.
                Array.Clear(q);
                Head(q, 0, 1); Box(q, 0, 0x10, 100, 200, 300, 150);
                Head(q, 1, 5); Box(q, 1, 0x12, 100, 200, 300, 150); AlphaAt(q, 1, 0x1a) = 255;
                Head(q, 2, 1);
                wnd.BufBegin = 0; wnd.BufEnd = 3 * S;
                check.Expect(ScaleWindow(mem, 8 * S, (IntPtr)win, 1f, 0f) == 0 && AlphaAt(q, 1, 0x1a) == 255, "a transparent window starts at a clip: its first fill is the server's, even at the window's size");
            }
        }
        finally { _s = saved; }
        log.Info(check.Ok ? "NuiWindows.SelfCheck: passed" : "NuiWindows.SelfCheck: FAILED");
    }

    /// <summary>Bytes one hand-built command takes in the self-check's queue, past the largest command's fields.</summary>
    private const int CmdSlot = 80;

    private static void Head(byte[] q, int i, int type)
    {
        BitConverter.TryWriteBytes(q.AsSpan(i * CmdSlot), type);
        BitConverter.TryWriteBytes(q.AsSpan(i * CmdSlot + 8), (ulong)((i + 1) * CmdSlot));
    }

    private static void Box(byte[] q, int i, int at, short x, short y, ushort w, ushort h)
    {
        Span<byte> c = q.AsSpan(i * CmdSlot + at);
        BitConverter.TryWriteBytes(c, x);
        BitConverter.TryWriteBytes(c[2..], y);
        BitConverter.TryWriteBytes(c[4..], w);
        BitConverter.TryWriteBytes(c[6..], h);
    }

    private static ref byte AlphaAt(byte[] q, int i, int colourAt) => ref q[i * CmdSlot + colourAt + 3];
}
