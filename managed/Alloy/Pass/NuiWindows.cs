using System;
using System.Collections.Generic;
using System.Diagnostics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Render controls for server NUI windows, applied to nuklear's command queue before <see cref="NuiFlush"/> converts it:
/// opacity, the alpha of a window's body fill and border, and a fade in. Each scales the alpha byte of that window's own
/// commands. A window is keyed by its nuklear name; the keys are <c>nui.&lt;id&gt;.opacity | background | fadein</c>.
/// </summary>
internal static unsafe class NuiWindows
{
    /// <summary>nuklear's <c>NK_WINDOW_HIDDEN</c>; <c>nk__begin</c> skips a window carrying it.</summary>
    private const uint WinHidden = 0x4000;   // not-an-offset: nk_window_flags NK_WINDOW_HIDDEN
    private const int NameBytes = 64;
    /// <summary>A command walk longer than this is a corrupt chain, not a window.</summary>
    private const int MaxCommands = 1 << 20;
    private const ulong FnvBasis = 14695981039346656037, FnvPrime = 1099511628211;   // not-an-offset: FNV-1a 64
    private const float BodyTolerance = 2f;

    /// <summary>nuklear's command types (<c>nk_command_type</c>) that carry colours.</summary>
    private const int CmdLine = 2, CmdCurve = 3, CmdRect = 4, CmdRectFilled = 5, CmdRectMulti = 6, CmdCircle = 7, CmdCircleFilled = 8,
                      CmdArc = 9, CmdArcFilled = 10, CmdTriangle = 11, CmdTriangleFilled = 12, CmdPolygon = 13, CmdPolygonFilled = 14,
                      CmdPolyline = 15, CmdText = 16, CmdImage = 17;

    /// <summary>One window's resolved properties. <see cref="Epoch"/> is the policy epoch they were read at.</summary>
    internal sealed class Style
    {
        public string Id = "";
        public float Opacity = 1f, Background = 1f, FadeInMs;
        public int Epoch = -1;
        public long OpenedAt, LastFrame = -2;
        public bool Plain => Opacity >= 1f && Background >= 1f && FadeInMs <= 0f;
    }

    /// <summary>Everything this file remembers, replaced whole when the context is lost or a session starts.</summary>
    private sealed class NuiWindowsState
    {
        public readonly Dictionary<ulong, Style> Styles = new();
        public readonly Dictionary<string, string> Probe = new(StringComparer.OrdinalIgnoreCase);
        public long Frame;
        public string ProbeText = "";
        public int Applied, KeysEpoch = -1;
        public bool HasKeys;
        public ExtenderLog? Log;
    }

    private static NuiWindowsState _s = new();
    internal static void Init(ExtenderLog log) => _s.Log = log;

    /// <summary>The policy keys of one window. Cold: allocates, runs when a window is first seen and when the policy moves.</summary>
    internal static void Read(Style st, Func<string, string?> get)
    {
        st.Opacity = Clamp01(Num(get(Key(st.Id, "opacity")), 1f));
        st.Background = Clamp01(Num(get(Key(st.Id, "background")), 1f));
        st.FadeInMs = Math.Max(0f, Num(get(Key(st.Id, "fadein")), 0f));
    }

    internal static string Key(string id, string prop) => "nui." + id + "." + prop;

    private static float Num(string? s, float def)
        => s != null && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)
           && float.IsFinite(f) ? f : def;

    private static float Clamp01(float f) => f < 0f ? 0f : f > 1f ? 1f : f;

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
    internal static void Apply(IntPtr ctx)
    {
        if (!CX.Services.Nui.NuiKnobs.Enabled.V) return;
        SetProbe(CX.Services.Nui.NuiKnobs.PolicyProbe.V);
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
            float f = st.Opacity;
            if (st.FadeInMs > 0f)
            {
                float t = (now - st.OpenedAt) * 1000f / Stopwatch.Frequency / st.FadeInMs;
                f *= Clamp01(t);
            }
            ScaleWindow(mem, allocated, w, f, st.Background);
            _s.Applied++;
        }
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
        return System.Text.Encoding.UTF8.GetString(n, len);
    }

    /// <summary>Scale one window's commands: every colour by <paramref name="all"/>, and the window's body fill and
    /// border by <paramref name="background"/> as well. <paramref name="mem"/> is the queue's base, which a command
    /// offset is added to.</summary>
    internal static void ScaleWindow(byte* mem, ulong allocated, IntPtr window, float all, float background)
    {
        var win = new NkWindow(window);
        float bx = win.Bounds[0], by = win.Bounds[1], bw = win.Bounds[2], bh = win.Bounds[3];
        ulong off = win.BufBegin, end = Math.Min(win.BufEnd, allocated);
        bool body = false, border = false;
        for (int n = 0; off < end && n < MaxCommands; n++)
        {
            byte* cmd = mem + off;
            int type = *(int*)cmd;
            float f = all;
            if (background < 1f)
            {
                if (!body && (type == CmdRectFilled || type == CmdImage) && IsBody(cmd, type, bx, by, bw, bh)) { body = true; f *= background; }
                else if (!border && type == CmdRect && IsBounds(cmd, bx, by, bw, bh)) { border = true; f *= background; }
            }
            if (f < 1f) ScaleCommand(cmd, type, f);
            ulong next = *(ulong*)(cmd + 8);
            if (next <= off) break;
            off = next;
        }
    }

    /// <summary>A rect-shaped command's x, y, w, h: the filled rect and the image keep them at +0x12, the stroked rect at +0x14.</summary>
    private static void Rect(byte* cmd, int type, out float x, out float y, out float w, out float h)
    {
        int at = type == CmdRect ? 0x14 : 0x12;
        x = *(short*)(cmd + at); y = *(short*)(cmd + at + 2); w = *(ushort*)(cmd + at + 4); h = *(ushort*)(cmd + at + 6);
    }

    /// <summary>The window body: full width, ending where the window ends (a title bar above it is not part of it).</summary>
    private static bool IsBody(byte* cmd, int type, float bx, float by, float bw, float bh)
    {
        Rect(cmd, type, out float x, out float y, out float w, out float h);
        return MathF.Abs(x - bx) <= BodyTolerance && MathF.Abs(w - bw) <= BodyTolerance
            && y >= by - BodyTolerance && MathF.Abs(y + h - (by + bh)) <= BodyTolerance;
    }

    private static bool IsBounds(byte* cmd, float bx, float by, float bw, float bh)
    {
        Rect(cmd, CmdRect, out float x, out float y, out float w, out float h);
        return MathF.Abs(x - bx) <= BodyTolerance && MathF.Abs(y - by) <= BodyTolerance
            && MathF.Abs(w - bw) <= BodyTolerance && MathF.Abs(h - bh) <= BodyTolerance;
    }

    /// <summary>Scale the alpha byte of every colour in one command. Offsets are <c>nk_convert</c>'s own reads, per type.</summary>
    internal static void ScaleCommand(byte* cmd, int type, float f)
    {
        switch (type)
        {
            case CmdLine: case CmdRectFilled: case CmdCircle: Alpha(cmd + 0x1a, f); break;
            case CmdCurve: Alpha(cmd + 0x22, f); break;
            case CmdRect: case CmdTriangleFilled: Alpha(cmd + 0x1c, f); break;
            case CmdRectMulti: Alpha(cmd + 0x18, f); Alpha(cmd + 0x1c, f); Alpha(cmd + 0x20, f); Alpha(cmd + 0x24, f); break;
            case CmdCircleFilled: Alpha(cmd + 0x18, f); break;
            case CmdArc: case CmdArcFilled: Alpha(cmd + 0x20, f); break;
            case CmdTriangle: Alpha(cmd + 0x1e, f); break;
            case CmdPolygon: case CmdPolygonFilled: case CmdPolyline: Alpha(cmd + 0x10, f); break;
            case CmdText: Alpha(cmd + 0x18, f); Alpha(cmd + 0x1c, f); break;
            case CmdImage: Alpha(cmd + 0x30, f); break;
        }
    }

    private static void Alpha(byte* rgba, float f) => rgba[3] = (byte)(rgba[3] * f + 0.5f);

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

            byte* name = stackalloc byte[NameBytes];
            name[0] = (byte)'a'; name[1] = (byte)'b';
            check.Expect(Hash(name) == 0x089c4407b545986a, "FNV-1a 64 of 'ab'");

            // A window of three commands in a 0x100-byte queue: a filled rect covering the window, a stroked rect
            // around it, a text with fg and bg. Header = type int @0, next u64 @8.
            byte* mem = stackalloc byte[0x100];
            for (int i = 0; i < 0x100; i++) mem[i] = 0;
            byte* a = mem + 0x10, b = mem + 0x50, t = mem + 0x90;
            *(int*)a = 5; *(ulong*)(a + 8) = 0x50; Put(a + 0x12, 100, 200, 300, 150); a[0x1a + 3] = 200;
            *(int*)b = 4; *(ulong*)(b + 8) = 0x90; Put(b + 0x14, 100, 200, 300, 150); b[0x1c + 3] = 100;
            *(int*)t = 16; *(ulong*)(t + 8) = 0xe0; t[0x18 + 3] = 80; t[0x1c + 3] = 255;
            byte* win = stackalloc byte[0x200];
            for (int i = 0; i < 0x200; i++) win[i] = 0;
            *(ulong*)(win + 0x90) = 0x10; *(ulong*)(win + 0x98) = 0xe0;
            float* bounds = (float*)(win + 0x4c);
            bounds[0] = 100; bounds[1] = 200; bounds[2] = 300; bounds[3] = 150;
            ScaleWindow(mem, 0x100, (IntPtr)win, 0.5f, 1f);
            check.Expect(a[0x1d] == 100 && b[0x1f] == 50 && t[0x1b] == 40 && t[0x1f] == 128, "opacity 0.5 halves every colour's alpha (200->100, 100->50, 80->40, 255->128)");
            a[0x1d] = 200; b[0x1f] = 100; t[0x1b] = 80; t[0x1f] = 255;
            ScaleWindow(mem, 0x100, (IntPtr)win, 1f, 0f);
            check.Expect(a[0x1d] == 0 && b[0x1f] == 0 && t[0x1b] == 80 && t[0x1f] == 255, "background 0 clears the body fill and the border, and nothing else");
            a[0x1d] = 200; b[0x1f] = 100;
            ScaleWindow(mem, 0x100, (IntPtr)win, 0.5f, 0.5f);
            check.Expect(a[0x1d] == 50 && b[0x1f] == 25 && t[0x1f] == 128, "opacity and background compound (200*0.25, 100*0.25)");
            *(ulong*)(win + 0x98) = 0x50;
            a[0x1d] = 200; b[0x1f] = 100;
            ScaleWindow(mem, 0x100, (IntPtr)win, 0.5f, 1f);
            check.Expect(a[0x1d] == 100 && b[0x1f] == 100, "the walk stops at the window's end");
        }
        finally { _s = saved; }
        log.Info(check.Ok ? "NuiWindows.SelfCheck: passed" : "NuiWindows.SelfCheck: FAILED");
    }

    private static void Put(byte* at, short x, short y, ushort w, ushort h)
    {
        *(short*)at = x; *(short*)(at + 2) = y; *(ushort*)(at + 4) = w; *(ushort*)(at + 6) = h;
    }
}
