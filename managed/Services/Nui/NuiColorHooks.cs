using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>Where NUI text meets <see cref="NuiColorText"/>: the baked font's width sees no tokens, so alignment and
/// wrapping see none; <c>nk_convert</c>'s text case, under either renderer, draws one colour piece at a time; edit fields
/// and text widgets mark the commands they push, and <c>nk_clear</c> ends the frame. Hot, allocation-free.</summary>
internal static unsafe class NuiColorHooks
{
    /// <summary>The trampolines and the context slot, set once at install; <see cref="Live"/> only when all five hooked.</summary>
    private sealed class HookState
    {
        public NkFontTextWidthFn Width;
        public IntPtr WidthTarget;
        public NkDrawListAddTextFn AddText;
        public NkEditBufferFn Edit;
        public NuiTextWidgetDrawFn Block;
        public NkClearFn Clear;
        public IntPtr* CtxSlot;
        public bool Live;
    }

    private static readonly HookState H = new();

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        if (!SymbolMap.Require(log, "NuiColorText", SymbolMap.NkFontTextWidth, SymbolMap.NkDrawListAddText,
                SymbolMap.NkEditBuffer, SymbolMap.NuiTextWidgetDraw, SymbolMap.NkClear, SymbolMap.Nuklear)
            || !EngineLayout.Require(log, "NuiColorText", "NkContext", "NkUserFont"))
        {
            Fail(log, "a nuklear text function or field did not resolve");
            return;
        }
        H.CtxSlot = (IntPtr*)Native.Sym(SymbolMap.Nuklear);
        H.WidthTarget = SymbolMap.Resolve(SymbolMap.NkFontTextWidth, log);
        H.Width = hooks.InstallNkFontTextWidth(&OnWidth);
        H.Edit = hooks.InstallNkEditBuffer(&OnEdit);
        H.Block = hooks.InstallNuiTextWidgetDraw(&OnBlock);
        H.Clear = hooks.InstallNkClear(&OnClear);
        H.AddText = hooks.InstallNkDrawListAddText(&OnAddText);
        H.Live = H.Width != null && H.Edit != null && H.Block != null && H.Clear != null && H.AddText != null;
        if (!H.Live) Fail(log, "a nuklear text function did not hook");
    }

    private static void Fail(ExtenderLog log, string why)
    {
        log.Always($"NuiColorText: {why} -- NUI text shows colour tokens as the stock client does");
        Notice.Show(log, "Colour tokens in NUI text are off: " + why);
    }

    /// <summary>The client's one nuklear context; its slot resolved before any hook went in.</summary>
    private static NkContext Ctx => new(*H.CtxSlot);

    /// <summary>The font's own width, through the trampoline when the font is the baked atlas's, so measuring a piece
    /// never comes back through <see cref="OnWidth"/>.</summary>
    private static TextMeasure Measure(IntPtr font)
    {
        var f = new NkUserFont(font);
        return new TextMeasure(f.UserData, f.Height, f.Width == H.WidthTarget ? H.Width : (NkFontTextWidthFn)f.Width);
    }

    [UnmanagedCallersOnly]
    private static float OnWidth(IntPtr userData, float height, byte* text, int len)
    {
        if (!H.Live || text == null || len <= 0) return H.Width(userData, height, text, len);
        return NuiColorText.Width(Native.Bytes((IntPtr)text, len), new TextMeasure(userData, height, H.Width));
    }

    [UnmanagedCallersOnly]
    private static void OnAddText(IntPtr list, IntPtr font, NkRect r, byte* text, int len, float height, uint fg)
    {
        var bytes = Native.Bytes((IntPtr)text, len);
        Span<ColorPiece> pieces = stackalloc ColorPiece[NuiColorText.MaxPieces];
        int n = !H.Live || bytes.IsEmpty ? -1 : NuiColorText.PlanAt(Ctx.OffsetOf(text), bytes, fg, Measure(font), pieces);
        if (n < 0)
        {
            H.AddText(list, font, r, text, len, height, fg);
            return;
        }
        for (int k = 0; k < n; k++)
        {
            var p = pieces[k];
            var at = new NkRect { X = r.X + p.X, Y = r.Y, W = r.W - p.X, H = r.H };
            fixed (byte* start = bytes.Slice(p.Start, p.Length)) H.AddText(list, font, at, start, p.Length, height, p.Color);
        }
    }

    [UnmanagedCallersOnly]
    private static uint OnEdit(IntPtr ctx, uint flags, IntPtr edit, IntPtr filter)
    {
        var c = new NkContext(ctx);
        ulong from = c.MemAllocated;
        NuiColorText.EnterEdit();
        uint r = H.Edit(ctx, flags, edit, filter);
        NuiColorText.LeaveEdit();
        NuiColorText.Mark(from, c.MemAllocated, edit: true);
        return r;
    }

    [UnmanagedCallersOnly]
    private static void OnBlock(IntPtr arg0, IntPtr arg1, IntPtr arg2)
    {
        ulong from = Ctx.MemAllocated;
        H.Block(arg0, arg1, arg2);
        NuiColorText.Mark(from, Ctx.MemAllocated, edit: false);
    }

    [UnmanagedCallersOnly]
    private static void OnClear(IntPtr ctx)
    {
        H.Clear(ctx);
        NuiColorText.EndFrame();
    }
}
