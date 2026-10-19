using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

[InlineArray(NuiHit.TokenCap)]
internal struct NuiTokenBuf { private byte _e; }

[InlineArray(NuiHit.ImageCap)]
internal struct NuiImageBuf { private byte _e; }

/// <summary>
/// The NUI element under the pointer: which one (<see cref="Element"/>, with its server <see cref="Token"/> when it
/// declared a JSON <c>id</c>), which list row (<see cref="Row"/>, -1 outside a list), where it is on screen, and the
/// <see cref="Image"/> name when it drew one. A plain value: copying it allocates nothing.
/// </summary>
internal struct NuiHit
{
    internal const int TokenCap = 64, ImageCap = 96;

    public IntPtr Window, Element;
    public int Row;
    public float X, Y, W, H;
    internal int TokenLen, ImageLen;
    internal NuiTokenBuf TokenBytes;
    internal NuiImageBuf ImageBytes;

    /// <summary>True when the pointer at (<paramref name="px"/>, <paramref name="py"/>) lies inside <see cref="X"/>..<see cref="H"/>.</summary>
    public readonly bool Contains(float px, float py) => NuiHitTest.Contains(X, Y, W, H, px, py);
}

/// <summary>The byte views of a <see cref="NuiHit"/>, as extensions on <c>in</c> so a span can point into the value.</summary>
internal static class NuiHitSpans
{
    /// <summary>The element's JSON <c>id</c>, UTF-8; empty when it declared none (or one longer than <see cref="NuiHit.TokenCap"/>).</summary>
    public static ReadOnlySpan<byte> Token(this in NuiHit h) => ((ReadOnlySpan<byte>)h.TokenBytes)[..h.TokenLen];
    /// <summary>The image resref the element drew this frame (<c>@item:..</c>, <c>@look:..</c> or a plain resref), without the cache's type suffix.</summary>
    public static ReadOnlySpan<byte> Image(this in NuiHit h) => ((ReadOnlySpan<byte>)h.ImageBytes)[..h.ImageLen];
    internal static Span<byte> TokenBuffer(this ref NuiHit h) => h.TokenBytes;
    internal static Span<byte> ImageBuffer(this ref NuiHit h) => h.ImageBytes;
}

/// <summary>
/// Which NUI element is under the pointer. The leaf-widget hook knows each widget's rectangle and its window; this tests the
/// pointer against both, takes the topmost window as nuklear stacks them, and names the element by its JSON <c>id</c> and
/// list row. A stock client sees nothing: <c>id</c> is a key it already ignores. Published once per frame, at the NUI flush.
/// </summary>
internal static unsafe class NuiHitTest
{
    /// <summary>The flags that take a window out of hit tests (hidden, closed).</summary>
    private const uint WinOutOfPlay = 0x4400;  // not-an-offset: NK_WINDOW_HIDDEN | NK_WINDOW_CLOSED
    private const int MaxNodes = 100_000, MaxWindows = 256, NkWindowNameBytes = 64;

    private sealed class NuiHitState
    {
        public readonly object Gate = new();
        public NuiHit Pending, Published, Logged;
        public bool HasPending, HasPublished, Armed;
        public IntPtr Window, Space;
        public int* ArrayIndex;
        public NuiJsonWindowRenderFn WindowOrig;
        public IntPtr ImageOrig;
        public bool Capturing;
        public int CapLen, Depth, PendingDepth;
        public NuiImageBuf Cap;
        public NkSdlRenderFn* NkSlot;
        /// <summary>The engine's <c>nk_context*</c> global, and the one window <see cref="WatchTopWindow"/> follows: its nuklear name,
        /// whether it is followed, whether a pick is waiting, and whether it was last seen gone.</summary>
        public IntPtr* CtxSlot;
        public readonly byte[] WatchName = new byte[NkWindowNameBytes];
        public volatile bool Watching, WatchWant, WatchGone;
        /// <summary>Read nuklear's pointer even when SDL's is there: a self-check lays the pointer out in fake nuklear memory.</summary>
        public bool UseNkPointer;
        public ExtenderLog? Log;
        public Knob<bool>? Debug;
        public long Hits, TooLong;
    }

    private static NuiHitState _s = new();

    /// <summary>True once the window hook, the flush chain and the row global all resolved.</summary>
    internal static bool Armed => _s.Armed;

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        var s = _s;
        s.Log = log;
        s.Debug = NuiKnobs.HitDebug;
        if (!SymbolMap.Require(log, "NuiHitTest", SymbolMap.NuiJsonWindowRender, SymbolMap.NuiArrayIndex, SymbolMap.NkSdlRender, SymbolMap.NkLayoutWidgetSpace)
            || !EngineLayout.Require(log, "NuiHitTest", "Nui::JSON::JsonWindow", "Nui::JSON::IdNode"))
        {
            log.Always("NuiHitTest: its symbols or layout rows did not resolve -- the pointer's NUI element is unknown");
            return;
        }
        s.ArrayIndex = (int*)Native.Sym(SymbolMap.NuiArrayIndex);
        s.Space = Native.Sym(SymbolMap.NkLayoutWidgetSpace);
        s.CtxSlot = (IntPtr*)Native.Sym(SymbolMap.Nuklear);
        NuiImageSpot.Init();
        s.WindowOrig = hooks.InstallNuiJsonWindowRender(&OnWindowRender);
        s.NkSlot = hooks.ChainNkSdlRender(&OnFlush, FlushPriority);
        s.ImageOrig = OperatingSystem.IsWindows()
            ? hooks.Install(SymbolMap.NuiGetImageWithCache, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&OnGetImageWin)
            : hooks.Install(SymbolMap.NuiGetImageWithCache, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&OnGetImage);
        s.Armed = s.ArrayIndex != null && s.Space != IntPtr.Zero && s.WindowOrig != null && s.NkSlot != null;
        if (!s.Armed) log.Always("NuiHitTest: JsonWindow::Render, nk_sdl_render or the row global did not resolve -- the pointer's NUI element is unknown");
    }

    /// <summary>Outermost on <c>nk_sdl_render</c>: runs before every other subscriber, once per frame, after NUI built its widgets.</summary>
    private const int FlushPriority = 1000;

    [UnmanagedCallersOnly]
    private static void OnWindowRender(IntPtr self)
    {
        var s = _s;
        IntPtr outer = s.Window;
        s.Window = self;
        s.WindowOrig(self);
        s.Window = outer;
    }

    /// <summary>Itanium order for a member returning by value: (ret, this, key, loader). While a hit candidate renders, keeps the last key it asked for.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnGetImage(IntPtr ret, IntPtr window, IntPtr key, IntPtr loader)
    {
        Keep(key);
        return ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)_s.ImageOrig)(ret, window, key, loader);
    }

    private static void Keep(IntPtr key)
    {
        var s = _s;
        if (!s.Capturing) return;
        var (buf, len, _) = Native.StdString((byte*)key);
        int n = (int)Math.Min(len, (uint)NuiHit.ImageCap);
        new ReadOnlySpan<byte>((void*)buf, n).CopyTo(s.Cap);
        s.CapLen = ResrefLength(((ReadOnlySpan<byte>)s.Cap)[..n]);
    }

    /// <summary>Win64 order for the same member: (this, ret, key, loader).</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnGetImageWin(IntPtr window, IntPtr ret, IntPtr key, IntPtr loader)
    {
        Keep(key);
        return ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)_s.ImageOrig)(window, ret, key, loader);
    }

    /// <summary>The length of the resref in an image-cache key, which is the resref, a dot and the type number.</summary>
    internal static int ResrefLength(ReadOnlySpan<byte> key)
    {
        int dot = key.LastIndexOf((byte)'.');
        return dot > 0 ? dot : key.Length;
    }

    /// <summary>Called before a widget the pointer is over renders: its image look-up, if it makes one, is kept for <see cref="Record"/>.</summary>
    internal static void Begin()
    {
        var s = _s;
        s.CapLen = 0;
        s.Depth++;
        s.Capturing = s.ImageOrig != IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static void OnFlush(float aa)
    {
        Publish(_s);
        TrackWindow(_s);
        NuiImageSpot.Track();
        (*_s.NkSlot)(aa);
    }

    /// <summary>Follow the topmost NUI window under the pointer as of the next frame: <see cref="TakeWindowGone"/> turns true once
    /// nuklear no longer builds a window of that name. Nothing is followed when the pointer is over none.</summary>
    internal static void WatchTopWindow()
    {
        var s = _s;
        s.Watching = false;
        s.WatchGone = false;
        s.WatchWant = s.CtxSlot != null;
    }

    /// <summary>Stop following the window.</summary>
    internal static void StopWatching()
    {
        var s = _s;
        s.WatchWant = s.Watching = s.WatchGone = false;
    }

    /// <summary>True once, when the followed window stopped being built.</summary>
    internal static bool TakeWindowGone()
    {
        var s = _s;
        if (!s.WatchGone) return false;
        s.WatchGone = false;
        return true;
    }

    /// <summary>The pointer where the held image is drawn, which is SDL's. Nuklear's own position when SDL's is not there.</summary>
    private static bool HeldPointer(IntPtr ctx, out float x, out float y)
    {
        if (EngineFn.SdlGetMouseState == null || _s.UseNkPointer) return Pointer(ctx, out x, out y);
        int ix = 0, iy = 0;
        EngineFn.SdlGetMouseState(&ix, &iy);
        x = ix; y = iy;
        return true;
    }

    /// <summary>Once per frame at the flush, when every window begun this frame carries the context's <c>seq</c>: pick the window to
    /// follow if one was asked for, and say when the followed one is no longer among them.</summary>
    private static void TrackWindow(NuiHitState s)
    {
        if (!s.WatchWant && !s.Watching) return;
        IntPtr ctx = *s.CtxSlot;
        if (ctx == IntPtr.Zero) return;
        var c = new NkContext(ctx);
        uint seq = c.Seq;
        if (s.WatchWant)
        {
            s.WatchWant = false;
            if (!HeldPointer(ctx, out float mx, out float my)) return;
            IntPtr top = IntPtr.Zero;
            int n = 0;
            for (IntPtr w = c.WindowBegin; w != IntPtr.Zero && n < MaxWindows; w = new NkWindow(w).Next, n++)
            {
                var win = new NkWindow(w);
                Span<float> r = win.Bounds;
                if (win.Seq == seq && (win.Flags & WinOutOfPlay) == 0 && Contains(r[0], r[1], r[2], r[3], mx, my)) top = w;
            }
            if (top == IntPtr.Zero) { s.Log?.Info($"NuiHitTest: the pointer ({mx},{my}) is over no NUI window -- none followed"); return; }
            new NkWindow(top).Name.CopyTo(s.WatchName);
            s.Watching = true;
            s.Log?.Info($"NuiHitTest: following {System.Text.Encoding.UTF8.GetString(s.WatchName.AsSpan(0, Array.IndexOf(s.WatchName, (byte)0) is var z and >= 0 ? z : NkWindowNameBytes))}");
            return;
        }
        int seen = 0;
        for (IntPtr w = c.WindowBegin; w != IntPtr.Zero && seen < MaxWindows; w = new NkWindow(w).Next, seen++)
        {
            var win = new NkWindow(w);
            if (win.Seq == seq && (win.Flags & WinOutOfPlay) == 0 && win.Name.SequenceEqual(s.WatchName)) return;
        }
        s.Watching = false;
        s.WatchGone = true;
        s.Log?.Info("NuiHitTest: the followed window is gone");
    }

    /// <summary>The latest published frame's hit. False when nothing was under the pointer. Safe from any thread; a
    /// reader sees the frame before the one being built, so it lags the pointer by one frame.</summary>
    internal static bool TryGet(out NuiHit hit)
    {
        var s = _s;
        lock (s.Gate)
        {
            hit = s.Published;
            return s.HasPublished;
        }
    }

    /// <summary>The pointer in nuklear's layout space, or false when no context.</summary>
    internal static bool Pointer(IntPtr ctx, out float x, out float y)
    {
        x = y = 0;
        if (ctx == IntPtr.Zero) return false;
        Span<float> pos = new NkContext(ctx).MousePos;
        x = pos[0];
        y = pos[1];
        return true;
    }

    /// <summary>The half-open rectangle test every hit check shares: left and top edges in, right and bottom edges out.</summary>
    internal static bool Contains(float x, float y, float w, float h, float px, float py) => px >= x && px < x + w && py >= y && py < y + h;

    /// <summary>True when the pointer is over the current window and no window nuklear stacks above it covers the point. False for
    /// nearly every widget of a frame, which is what keeps the rest of the test off the hot path.</summary>
    internal static bool OverCurrent(IntPtr ctx)
    {
        if (!Pointer(ctx, out float mx, out float my)) return false;
        IntPtr cur = new NkContext(ctx).Current;
        if (cur == IntPtr.Zero) return false;
        Span<float> r = new NkWindow(cur).Bounds;
        if (!Contains(r[0], r[1], r[2], r[3], mx, my)) return false;
        return !CoveredAbove(ctx, mx, my);
    }

    /// <summary>True when the pointer is inside the widget's rectangle and its panel's clip. A rectangle the panel clips away (scrolled out) never hits.</summary>
    internal static bool Inside(IntPtr ctx, in NuiNuklear.Box b)
    {
        if (!Pointer(ctx, out float mx, out float my)) return false;
        return Contains(b.X, b.Y, b.W, b.H, mx, my) && Contains(b.ClipX, b.ClipY, b.ClipW, b.ClipH, mx, my);
    }

    /// <summary>Where the engine's own row layout will put the widget about to render, for a row that is not a free-space one (a list's
    /// template row, a plain grid): <c>nk_layout_widget_space</c> with <c>modify</c> 0, which writes nothing, after the row advance a full
    /// row would get first. False for a child the parent did not push.</summary>
    internal static bool Query(IntPtr self, IntPtr ctx, out NuiNuklear.Box b)
    {
        b = default;
        IntPtr win = new NkContext(ctx).Current;
        if (win == IntPtr.Zero || new Nui__Layout__Layoutable(self).NoPush != 0) return false;
        byte* p = (byte*)new NkWindow(win).Layout;
        if (p == null) return false;
        float* r = stackalloc float[4];
        var row = NuiNuklear.StartRow(p);
        if (OperatingSystem.IsWindows()) ((delegate* unmanaged<float*, IntPtr, IntPtr, int, void>)_s.Space)(r, ctx, win, 0);
        else ((delegate* unmanaged<float*, IntPtr, int, void>)_s.Space)(r, ctx, 0);
        NuiNuklear.EndRow(p, in row);
        b.X = r[0]; b.Y = r[1]; b.W = r[2]; b.H = r[3];
        NuiNuklear.ReadClip(p, ref b);
        return true;
    }

    /// <summary>True when a visible window above <c>ctx-&gt;current</c> in nuklear's list contains the point: windows later in the list draw on top.</summary>
    internal static bool CoveredAbove(IntPtr ctx, float mx, float my)
    {
        IntPtr cur = new NkContext(ctx).Current;
        if (cur == IntPtr.Zero) return false;
        IntPtr w = new NkWindow(cur).Next;
        for (int i = 0; w != IntPtr.Zero && i < MaxWindows; i++, w = new NkWindow(w).Next)
        {
            var win = new NkWindow(w);
            if ((win.Flags & WinOutOfPlay) != 0) continue;
            Span<float> r = win.Bounds;
            if (Contains(r[0], r[1], r[2], r[3], mx, my)) return true;
        }
        return false;
    }

    /// <summary>Called after a widget the pointer is over has rendered: records it as this frame's candidate. A widget that renders
    /// others (a list, its cells) finishes after them, so the deepest one wins; of two at the same depth, the later (the one drawn on top).</summary>
    internal static void Record(IntPtr element, in NuiNuklear.Box b)
    {
        var s = _s;
        int depth = s.Depth--;
        s.Capturing = false;
        if (s.HasPending && depth < s.PendingDepth) return;
        s.PendingDepth = depth;
        ref NuiHit p = ref s.Pending;
        s.Hits++;
        p.Window = s.Window;
        p.Element = element;
        p.Row = *s.ArrayIndex;
        p.X = b.X; p.Y = b.Y; p.W = b.W; p.H = b.H;
        ((ReadOnlySpan<byte>)s.Cap)[..s.CapLen].CopyTo(p.ImageBuffer());
        p.ImageLen = s.CapLen;
        int n = s.Window == IntPtr.Zero ? 0 : ResolveId(s.Window, element, p.TokenBuffer());
        if (n < 0) { s.TooLong++; n = 0; }
        p.TokenLen = n;
        s.HasPending = true;
    }

    /// <summary>Hands a finished frame to readers and starts the next one empty.</summary>
    private static void Publish(NuiHitState s)
    {
        if (!s.HasPending && !s.HasPublished) return;
        lock (s.Gate)
        {
            s.HasPublished = s.HasPending;
            if (s.HasPending) s.Published = s.Pending;
            s.HasPending = false;
        }
        s.Depth = 0;
        if (s.Debug?.V == true) LogChange(s);
    }

    private static void LogChange(NuiHitState s)
    {
        if (!s.HasPublished) { s.Logged.Element = IntPtr.Zero; return; }
        ref NuiHit h = ref s.Published;
        if (h.Element == s.Logged.Element && h.Row == s.Logged.Row) return;
        s.Logged = h;
        s.Log?.Always($"NuiHitTest: \"{Encoding.UTF8.GetString(h.Token())}\" image=\"{Encoding.UTF8.GetString(h.Image())}\" element={h.Element:x} row={h.Row} rect={h.X:0.#},{h.Y:0.#} {h.W:0.#}x{h.H:0.#}");
    }

    /// <summary>The JSON <c>id</c> a window's id map holds for <paramref name="element"/>, copied into <paramref name="dst"/>. Zero when
    /// the element declared none, -1 when the id does not fit. Walks the map: call it only for the one widget under the pointer.</summary>
    internal static int ResolveId(IntPtr window, IntPtr element, Span<byte> dst)
    {
        IntPtr head = new Nui__JSON__JsonWindow(window).IdsHead;
        bool win = OperatingSystem.IsWindows();
        IntPtr node = win ? (head == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)head) : head, end = win ? head : IntPtr.Zero;
        for (int i = 0; node != IntPtr.Zero && node != end && i < MaxNodes; i++, node = *(IntPtr*)node)
        {
            var n = new Nui__JSON__IdNode(node);
            if (n.Element != element) continue;
            var (buf, len, _) = Native.StdString((byte*)Unsafe.AsPointer(ref n.Key));
            if (len > (uint)dst.Length) return -1;
            new ReadOnlySpan<byte>((void*)buf, (int)len).CopyTo(dst);
            return (int)len;
        }
        return 0;
    }

    internal static string Status()
    {
        var s = _s;
        if (!s.Armed) return "The pointer's NUI element is not tracked (see cx.log).";
        string now = TryGet(out NuiHit h) ? $"\"{Encoding.UTF8.GetString(h.Token())}\" row {h.Row} at {h.X:0.#},{h.Y:0.#} {h.W:0.#}x{h.H:0.#}" : "none";
        return $"Under the pointer: {now}.";
    }

    /// <summary>The geometry, the stacking test, the id-map walk and the published hit over hand-built nuklear and NUI memory. The
    /// oracle is arithmetic done by hand on the numbers written below (a panel at 10,20 with an item at 5,6 scrolled by 3,4 puts the
    /// rectangle at 12,22) and the ids and rows put into the fake map; none of it calls the code under test to get an expected value.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        if (EngineLayout.NuiIdsHead == 0 || EngineLayout.NuiIdNodeElement == 0 || EngineLayout.NuiLayoutableNoPush == 0)
        {
            check.Skip("no NUI layout rows for this build");
            return;
        }
        const int Size = 0x8000;  // not-an-offset: the fake nuklear/NUI memory is this big
        using var scratch = new NuiScratch();
        byte* mem = (byte*)scratch.Block(Size);
        var saved = _s;
        try
        {
            IntPtr ctx = (IntPtr)mem, win = scratch.Block(0x400), panel = scratch.Block(0x400), above = scratch.Block(0x400);
            IntPtr widget = scratch.Block(0x400);
            uint* scrollX = (uint*)scratch.Block(8), scrollY = (uint*)scratch.Block(8);
            var pn = new NkPanel(panel);
            *scrollX = 3; *scrollY = 4;
            new NkContext(ctx).Current = win;
            new NkWindow(win).Layout = panel;
            new NkWindow(win).Next = above;
            pn.OffsetX = (IntPtr)scrollX;
            pn.OffsetY = (IntPtr)scrollY;
            pn.AtX = 10; pn.AtY = 20;
            pn.RowType = 6;
            Span<float> clip = pn.Clip;
            clip[0] = 0; clip[1] = 0; clip[2] = 200; clip[3] = 300;
            Span<float> item = pn.RowItem;
            item[0] = 5; item[1] = 6; item[2] = 100; item[3] = 20;

            bool got = NuiNuklear.TryBox(widget, ctx, out var box);
            check.Expect(got && box.X == 12 && box.Y == 22 && box.W == 100 && box.H == 20 && box.Left == 15, "a panel at 10,20, item 5,6 100x20, scrolled 3,4 is the rectangle 12,22 100x20");
            new Nui__Layout__Layoutable(widget).NoPush = 1;
            check.Expect(!NuiNuklear.TryBox(widget, ctx, out _), "a child the parent did not push has no rectangle");
            new Nui__Layout__Layoutable(widget).NoPush = 0;
            pn.RowType = 0;
            check.Expect(!NuiNuklear.TryBox(widget, ctx, out _), "a panel outside a free-space row has no rectangle");
            pn.RowType = 6;

            void Mouse(float x, float y) { Span<float> m = new NkContext(ctx).MousePos; m[0] = x; m[1] = y; }
            Span<float> wb = new NkWindow(win).Bounds;
            wb[0] = 0; wb[1] = 0; wb[2] = 220; wb[3] = 320;
            Mouse(50, 30);
            check.Expect(Inside(ctx, in box), "a point inside the rectangle and the clip hits");
            Mouse(112, 30);
            check.Expect(!Inside(ctx, in box), "the right edge is exclusive");
            Mouse(11.9f, 30);
            check.Expect(!Inside(ctx, in box), "left of the rectangle misses");
            var scrolledOut = box;
            scrolledOut.X = 250;
            Mouse(260, 30);
            check.Expect(!Inside(ctx, in scrolledOut), "a rectangle the panel clips away never hits");
            Mouse(230, 30);
            check.Expect(!OverCurrent(ctx), "a point outside the current window is not over it");

            Mouse(50, 30);
            Span<float> r = new NkWindow(above).Bounds;
            r[0] = 40; r[1] = 20; r[2] = 30; r[3] = 30;
            check.Expect(CoveredAbove(ctx, 50, 30) && !OverCurrent(ctx), "a window stacked above that holds the point covers it");
            new NkWindow(above).Flags = 0x400;
            check.Expect(!CoveredAbove(ctx, 50, 30) && OverCurrent(ctx), "a hidden or closed window above covers nothing");
            new NkWindow(above).Flags = 0;
            Mouse(80, 30);
            check.Expect(!CoveredAbove(ctx, 80, 30), "a point outside the upper window is not covered");

            pn.RowIndex = 2; pn.RowColumns = 2; pn.RowHeight = 24; pn.RowItemOffset = 9;
            var rowSave = NuiNuklear.StartRow((byte*)panel);
            check.Expect(rowSave.Moved && pn.RowIndex == 0 && pn.AtY == 44 && pn.RowItemOffset == 0,
                "a full row starts the next one: index 0, at_y 20 + height 24, offset 0");
            NuiNuklear.EndRow((byte*)panel, in rowSave);
            check.Expect(pn.RowIndex == 2 && pn.AtY == 20 && pn.RowItemOffset == 9, "and the panel is put back as it was");
            pn.RowIndex = 1;
            rowSave = NuiNuklear.StartRow((byte*)panel);
            check.Expect(!rowSave.Moved && pn.RowIndex == 1 && pn.AtY == 20, "a row with a column left is not advanced");
            IntPtr map = scratch.Block(0x800);
            BuildMap(scratch.Block(0x100), scratch.Block(0x100), scratch.Block(0x100), map, (IntPtr)widget, "slot_head", (IntPtr)0x2222, "x");
            Span<byte> id = stackalloc byte[NuiHit.TokenCap];
            int n = ResolveId(map, widget, id);
            check.Expect(n == 9 && Encoding.UTF8.GetString(id[..n]) == "slot_head", "the id map names the first element slot_head");
            n = ResolveId(map, (IntPtr)0x2222, id);
            check.Expect(n == 1 && id[0] == (byte)'x', "and the second x");
            check.Expect(ResolveId(map, (IntPtr)0x3333, id) == 0, "an element with no id has no token");
            check.Expect(ResolveId(map, widget, id[..4]) == -1, "an id that does not fit is refused, not cut");

            check.Expect(ResrefLength("@item:7f001234.7"u8) == 14 && ResrefLength("cxicon"u8) == 6 && ResrefLength("@look:0003.0"u8) == 10,
                "an image-cache key loses its type suffix and a bare resref keeps all of it");

            int row = 7;
            var s = new NuiHitState { Window = map, ArrayIndex = &row, UseNkPointer = true };
            _s = s;
            Begin();
            Record(widget, in box);
            check.Expect(!TryGet(out _), "a hit is not visible before the frame's flush");
            Publish(s);
            check.Expect(TryGet(out NuiHit h) && h.Element == widget && h.Row == 7 && h.Token().SequenceEqual("slot_head"u8) && h.X == 12 && h.Y == 22 && h.W == 100 && h.H == 20,
                "the flush publishes the element, row, token and rectangle");
            check.Expect(h.Contains(50, 30) && !h.Contains(112, 30), "the hit's rectangle is half-open");
            Publish(s);
            check.Expect(!TryGet(out _), "a frame with no hit publishes none");

            var cell = box;
            cell.X = 20; cell.W = 30;
            Begin();
            Begin();
            Record((IntPtr)0x2222, in cell);
            Record(widget, in box);
            Publish(s);
            check.Expect(TryGet(out h) && h.Element == 0x2222 && h.Token().SequenceEqual("x"u8) && h.Row == 7 && h.X == 20,
                "a widget that renders others finishes after them and does not displace the innermost one");
            Begin();
            Record(widget, in box);
            Begin();
            Record((IntPtr)0x2222, in cell);
            Publish(s);
            check.Expect(TryGet(out h) && h.Element == 0x2222, "of two siblings at one depth the later one wins");

            IntPtr ctxCell = ctx;
            s.CtxSlot = &ctxCell;
            new NkContext(ctx).WindowBegin = win;
            new NkContext(ctx).Seq = 5;
            new NkWindow(win).Seq = 5; new NkWindow(above).Seq = 5;
            "JsonWindow::a"u8.CopyTo(new NkWindow(win).Name);
            "JsonWindow::b"u8.CopyTo(new NkWindow(above).Name);
            Mouse(50, 30);
            WatchTopWindow();
            TrackWindow(s);
            check.Expect(s.Watching && s.WatchName.AsSpan(0, 13).SequenceEqual("JsonWindow::b"u8), "the window stacked on top at the pointer is the one followed");
            TrackWindow(s);
            check.Expect(!TakeWindowGone(), "a window begun this frame is not gone");
            new NkWindow(win).Seq = 4;
            TrackWindow(s);
            check.Expect(!TakeWindowGone(), "another window dropping out does not end the follow");
            new NkWindow(above).Seq = 4;
            TrackWindow(s);
            check.Expect(TakeWindowGone() && !s.Watching && !TakeWindowGone(), "a window not begun this frame is gone, once");
            new NkWindow(win).Seq = 5;
            Mouse(300, 300);
            WatchTopWindow();
            TrackWindow(s);
            check.Expect(!s.Watching, "a pointer over no window follows none");
        }
        finally
        {
            _s = saved;
        }
        if (check.Ok) log.Info("NuiHitTest.SelfCheck: passed");
    }

    /// <summary>Two id-map nodes in the running OS's container layout: libstdc++'s null-ended chain from the before-begin
    /// pointer, MSVC's circular list from a sentinel. Keys short enough to live inline in the std::string.</summary>
    private static void BuildMap(IntPtr nodeA, IntPtr nodeB, IntPtr nodeSentinel, IntPtr window, IntPtr elementA, string idA, IntPtr elementB, string idB)
    {
        bool win = OperatingSystem.IsWindows();
        byte* a = (byte*)nodeA, b = (byte*)nodeB, sentinel = (byte*)nodeSentinel;
        void Node(byte* node, IntPtr next, IntPtr element, string id)
        {
            *(IntPtr*)node = next;
            var view = new Nui__JSON__IdNode((IntPtr)node);
            view.Element = element;
            var key = new NuiStdString((byte*)Unsafe.AsPointer(ref view.Key));
            byte* chars = key.Inline;
            for (int i = 0; i < id.Length; i++) chars[i] = (byte)id[i];
            key.Set(chars, (ulong)id.Length);
        }
        Node(a, (IntPtr)b, elementA, idA);
        Node(b, win ? (IntPtr)sentinel : IntPtr.Zero, elementB, idB);
        *(IntPtr*)sentinel = (IntPtr)a;
        new Nui__JSON__JsonWindow(window).IdsHead = win ? (IntPtr)sentinel : (IntPtr)a;
    }
}
