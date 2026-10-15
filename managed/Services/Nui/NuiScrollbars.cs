using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// NUI scrollbars (windows, groups, lists, text edits): a minimum thumb length and a gliding mouse wheel, from two
/// hooks on the functions only <c>nk_do_scrollbarv/h</c> call. <see cref="SymbolMap.NkScrollbarBehavior"/> grows a
/// too-short thumb in place (and the two empty page-click rects) before the hit-test, scales the drag, and glides a
/// wheel step; <see cref="SymbolMap.NkDrawScrollbar"/> then places it, as the caller re-derives only position. Hot.
/// </summary>
internal static unsafe class NuiScrollbars
{
    /// <summary><c>nk_widget_state</c> ACTIVED: the behavior's drag arm ran.</summary>
    private const uint StateActived = 0x20;  // not-an-offset: nuklear state bit
    /// <summary>Seconds for a glide to close 63% of its remaining distance.</summary>
    private const float GlideTau = 0.06f;
    /// <summary>A scrollbar unseen this long has closed; its glide slot is free.</summary>
    private const float SlotIdle = 0.5f;
    private const int Slots = 8;

    private struct Glide
    {
        public float X, Y, W, H, Cur, Dest;
        public int Axis;
        public long Seen;
        public bool Live;
    }

    private static ExtenderLog? _log;
    private static Knob<bool>? _enabled, _smooth;
    private static Knob<float>? _thumb;
    private static NkScrollbarBehaviorFn _behaviorOrig;
    private static NkDrawScrollbarFn _drawOrig;
    private static readonly Glide[] _glides = new Glide[Slots];
    /// <summary>What the behavior hook decided, for the draw that follows it in the same <c>nk_do_scrollbar*</c> call,
    /// keyed by that call's thumb rect (a local both calls are handed).</summary>
    private static IntPtr _latchThumb;
    private static int _latchAxis;
    private static float _latchPos;

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _enabled = NuiKnobs.Enabled;
        _smooth = NuiKnobs.Smooth;
        _thumb = NuiKnobs.Thumb;
        if (!SymbolMap.Require(log, "NuiScrollbars", SymbolMap.NkScrollbarBehavior, SymbolMap.NkDrawScrollbar))
        {
            Fail("nk_scrollbar_behavior or nk_draw_scrollbar did not resolve");
            return;
        }
        _behaviorOrig = hooks.InstallNkScrollbarBehavior(&OnBehavior);
        _drawOrig = hooks.InstallNkDrawScrollbar(&OnDraw);
        if (_behaviorOrig == null || _drawOrig == null) Fail("nk_scrollbar_behavior or nk_draw_scrollbar did not hook");
    }

    private static void Fail(string why)
    {
        _log?.Always($"NuiScrollbars: {why} -- NUI scrollbars stay stock");
        Notice.Show(_log, "NUI scrollbar fixes are off: " + why);
    }

    [UnmanagedCallersOnly]
    private static float OnBehavior(IntPtr state, IntPtr input, int has, IntPtr track, IntPtr thumb, IntPtr empty0, IntPtr empty1,
                                    float offset, float target, float step, int axis)
    {
        _latchThumb = IntPtr.Zero;
        if (!_enabled!.V || input == IntPtr.Zero)
            return _behaviorOrig(state, input, has, track, thumb, empty0, empty1, offset, target, step, axis);
        int p = axis == 0 ? 1 : 0, l = p + 2;
        float* s = (float*)track, c = (float*)thumb;
        float range = target - s[l];

        bool grow = Grow(s, c, (float*)empty0, (float*)empty1, p, l, offset, target, range,
                         out float start, out float span, out float len);
        var ctx = new NkContext(input);
        Span<float> mouseDelta = ctx.Delta;
        float savedDelta = mouseDelta[p];
        if (grow && span > 1f) mouseDelta[p] = savedDelta * (range / span) * (s[l] / target);

        Span<float> wheel = ctx.Wheel;
        float savedWheel = wheel[p];
        ref Glide g = ref Slot(s, axis);
        if (g.Live && MathF.Floor(g.Cur) != offset) g.Live = false;
        float based = g.Live ? g.Cur : offset;
        bool glide = _smooth!.V && range > 0 && has != 0 && savedWheel != 0
                     && ctx.LeftDown == 0 && ctx.LeftClicked == 0;
        if (glide)
        {
            g.Dest = Math.Clamp((g.Live ? g.Dest : based) + step * -savedWheel, 0f, range);
            if (!g.Live) g.Seen = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 60;
            g.Cur = based;
            g.Live = true;
            wheel[p] = 0;
        }

        float r = _behaviorOrig(state, input, has, track, thumb, empty0, empty1, offset, target, step, axis);
        mouseDelta[p] = savedDelta;
        wheel[p] = savedWheel;

        if (g.Live && (r != offset || (*(uint*)state & StateActived) != 0)) g.Live = false;
        else if (g.Live) r = Advance(ref g, range);

        if (grow)
        {
            float frac = range > 0 ? Math.Clamp(r / range, 0f, 1f) : 0f;
            if ((*(uint*)state & StateActived) != 0)
                ctx.Clicked[p] = start + frac * span + len * 0.5f;
            _latchThumb = thumb; _latchAxis = p; _latchPos = start + frac * span;
        }
        return r;
    }

    /// <summary>A thumb shorter than <see cref="NuiKnobs.Thumb"/> times its own thickness is grown in place to that
    /// (capped at the track inside its border and padding), placed where the offset puts it on the shortened travel,
    /// and the two empty rects re-cut around it. The border+padding along the axis is read back from where the engine
    /// put the thumb (<c>start = track + off/target*len + border + padding</c>), so no style is needed here.</summary>
    private static bool Grow(float* s, float* c, float* e0, float* e1, int p, int l, float offset, float target, float range,
                             out float start, out float span, out float len)
    {
        start = span = len = 0;
        if (_thumb!.V <= 0 || range <= 0 || target <= 0) return false;
        float pad = c[p] - s[p] - offset / target * s[l];
        float inner = s[l] - 2 * pad;
        len = MathF.Min(_thumb.V * c[l == 3 ? 2 : 3], inner);
        if (c[l] >= len || inner <= 0) return false;
        start = s[p] + pad;
        span = inner - len;
        c[p] = start + Math.Clamp(offset / range, 0f, 1f) * span;
        c[l] = len;
        e0[l] = MathF.Max(c[p] - s[p], 0);
        e1[p] = c[p] + len;
        e1[l] = MathF.Max(s[p] + s[l] - e1[p], 0);
        return true;
    }

    [UnmanagedCallersOnly]
    private static void OnDraw(IntPtr output, uint state, IntPtr style, IntPtr track, IntPtr thumb)
    {
        if (_latchThumb == thumb && thumb != IntPtr.Zero) ((float*)thumb)[_latchAxis] = _latchPos;
        _latchThumb = IntPtr.Zero;
        _drawOrig(output, state, style, track, thumb);
    }

    /// <summary>One frame of the glide: exponential approach, snapped home inside half a pixel.</summary>
    private static float Advance(ref Glide g, float range)
    {
        long now = Stopwatch.GetTimestamp();
        float dt = Math.Clamp((now - g.Seen) / (float)Stopwatch.Frequency, 0f, 0.1f);
        g.Seen = now;
        g.Dest = Math.Clamp(g.Dest, 0f, range);
        g.Cur += (g.Dest - g.Cur) * (1f - MathF.Exp(-dt / GlideTau));
        if (MathF.Abs(g.Dest - g.Cur) < 0.5f) { g.Cur = g.Dest; g.Live = false; }
        return g.Cur;
    }

    /// <summary>Off-device: the grown thumb on a 100 px track (border+padding 2, content 10x the track) sits flush at
    /// both ends, keeps the thickness multiple, re-cuts both empty rects; a thumb already long enough is untouched;
    /// a glide lands on its target. The hooks themselves are engine calls -- live-unverified here.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Knob<float>? saved = _thumb;
        _thumb = NuiKnobs.Thumb;
        float was = _thumb.V;
        try
        {
            _thumb.V = 2f;
            float* s = stackalloc float[4], c = stackalloc float[4], e0 = stackalloc float[4], e1 = stackalloc float[4];
            s[0] = 0; s[1] = 10; s[2] = 12; s[3] = 100;
            float target = 1000, range = target - s[3];
            void Engine(float off) { c[0] = 2; c[2] = 8; c[1] = s[1] + off / target * s[3] + 2; c[3] = s[3] * s[3] / target - 4; }
            Engine(0);
            bool g = Grow(s, c, e0, e1, 1, 3, 0, target, range, out float start, out float span, out float len);
            check.Expect(g && len == 16 && c[3] == 16 && c[1] == 12 && start == 12 && span == 96 - 16, $"top: 2x the 8 px thickness, flush inside the pad ({c[1]}, {c[3]})");
            check.Expect(e0[3] == 2 && e1[1] == 28 && e1[3] == 110 - 28, "the empties re-cut around it");
            Engine(range);
            Grow(s, c, e0, e1, 1, 3, range, target, range, out _, out _, out _);
            check.Expect(MathF.Abs(c[1] + c[3] - (s[1] + s[3] - 2)) < 1e-3f, $"bottom: flush with the far pad ({c[1] + c[3]})");
            c[3] = 40;
            check.Expect(!Grow(s, c, e0, e1, 1, 3, 0, target, range, out _, out _, out _) && c[3] == 40, "a long enough thumb is left alone");

            var glide = new Glide { Cur = 0, Dest = 300, Live = true, Seen = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 60 };
            float r = 0;
            for (int i = 0; i < 200 && glide.Live; i++) { glide.Seen -= Stopwatch.Frequency / 60; r = Advance(ref glide, range); }
            check.Expect(!glide.Live && r == 300, $"a glide lands on its target ({r})");
        }
        finally { _thumb.V = was; _thumb = saved; }
        log.Info(check.Ok ? "NuiScrollbars.SelfCheck: passed" : "NuiScrollbars.SelfCheck: FAILED");
    }

    /// <summary>The glide slot of the scrollbar whose track rect this is (a scrollbar's track does not move while it
    /// scrolls), or the stalest slot, reset.</summary>
    private static ref Glide Slot(float* s, int axis)
    {
        long now = Stopwatch.GetTimestamp();
        int free = 0;
        long oldest = long.MaxValue;
        for (int i = 0; i < Slots; i++)
        {
            ref Glide g = ref _glides[i];
            if (g.Axis == axis && g.X == s[0] && g.Y == s[1] && g.W == s[2] && g.H == s[3]
                && (now - g.Seen) / (float)Stopwatch.Frequency < SlotIdle)
            {
                if (!g.Live) g.Seen = now;
                return ref g;
            }
            if (g.Seen < oldest) { oldest = g.Seen; free = i; }
        }
        ref Glide n = ref _glides[free];
        n = new Glide { X = s[0], Y = s[1], W = s[2], H = s[3], Axis = axis, Seen = now };
        return ref n;
    }
}
