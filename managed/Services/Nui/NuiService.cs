using System;
using CX.Engine;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Nui;

/// <summary>
/// NUI speed-ups. The off-screen cull hooks every leaf widget's per-frame <see cref="SymbolMap.NuiWidgetRender"/> and
/// skips a widget whose rect lies wholly outside its panel's clip: nuklear's own <c>nk_widget</c> would draw nothing
/// for it anyway, so the saving is the NUI layer's work before that point -- bound-value reads, text encoding, image
/// loads. Hot (every widget, every frame), allocation-free. Also arms the other <c>Nui*</c> fixes in this folder.
/// </summary>
public sealed unsafe class NuiService : IExtenderService
{
    // nuklear 4.08 as compiled, the same on both builds: nk_widget @0xde84f0, nk_layout_widget_space @0xdd8340,
    // nk_layout_space_push @0xde7830 (Windows rva 0xa86e40) and Windows' Layoutable::Render read these values.
    private const int NkCurrentOff = 0x6738, NkWinLayoutOff = 0xa8;  // not-an-offset: nuklear context ABI, not an engine class
    private const int NkOffsetXOff = 0x18, NkOffsetYOff = 0x20, NkAtXOff = 0x28, NkAtYOff = 0x2c, NkMaxXOff = 0x30;  // not-an-offset: nuklear panel ABI, not an engine class
    private const int NkClipOff = 0x44, NkRowTypeOff = 0x70, NkRowItemOff = 0xa0;  // not-an-offset: nuklear panel ABI, not an engine class
    /// <summary><c>NK_LAYOUT_STATIC_FREE</c>: the row type of <c>nk_layout_space_begin(ctx, NK_STATIC, h, INT_MAX)</c>,
    /// which every NUI group's <c>RenderInside</c> opens (Linux @0x84e0f0, Windows rva 0x952920).</summary>
    private const int NkLayoutStaticFree = 6;
    /// <summary>Screen px kept around the clip, so a skinned frame, focus ring or partly visible edge still draws.</summary>
    private const float CullMargin = 32f;

    private static ExtenderLog? _log;
    private static NuiWidgetRenderFn _renderOrig;
    private static IntPtr _statefulVptr;
    private static Knob<bool>? _enabled, _cull;
    private static long _seen, _skipped;

    /// <summary>What NUI is doing, for readers outside this folder.</summary>
    internal sealed class ActivityState
    {
        /// <summary>Widget draws since boot; it stops rising when no NUI window is open.</summary>
        public long Drawn;
        /// <summary>True once the widget hook is in, so <see cref="Drawn"/> means something.</summary>
        public bool Counting;
    }

    internal static readonly ActivityState Activity = new();

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        UiCategory.Declare();   // the UI groups declare in panel order whether or not the hook arms
        _enabled = NuiKnobs.Enabled;
        _cull = NuiKnobs.Cull;
        NuiKnobs.G.Status = Status;
        NuiItemIcons.SelfCheck(log);
        NuiScrollbars.SelfCheck(log);
        NuiSolver.SelfCheck(log);
        NuiScrollbarHide.SelfCheck(log);
        NuiLayoutErrors.SelfCheck(log);
        NuiItemIcons.Init(log, hooks);
        NuiScrollbars.Init(log, hooks);
        NuiScrollbarHide.Init(log, hooks);
        NuiSolver.Init(log, hooks);
        NuiLayoutErrors.Init(log, hooks);
        if (!EngineLayout.Require(log, "NuiService", "Nui::Layout::Layoutable")) return;
        IntPtr render = Native.Sym(SymbolMap.NuiWidgetRender);
        _statefulVptr = EngineAbi.Vtbl(Native.Sym(SymbolMap.NuiStatefulWidgetVtbl));
        if (render == IntPtr.Zero || _statefulVptr == IntPtr.Zero)
        {
            Fail("Widget::Render or the StatefulWidget vtable did not resolve");
            return;
        }
        if (*(IntPtr*)(_statefulVptr + EngineLayout.NuiRenderSlot) != render)
        {
            Fail("the StatefulWidget vtable's Render slot is not Widget::Render");
            return;
        }
        _renderOrig = hooks.InstallNuiWidgetRender(&OnWidgetRender);
        if (_renderOrig == null) Fail("Widget::Render did not hook");
        else Activity.Counting = true;
    }

    private static void Fail(string why)
    {
        _log?.Always($"NuiService: {why} -- the off-screen cull stands down");
        Notice.Show(_log, "NUI off-screen skip is off: " + why);
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void OnWidgetRender(IntPtr self, IntPtr ctx)
    {
        _seen++;
        Activity.Drawn++;
        if (_enabled!.V && _cull!.V && Offscreen(self, ctx)) { _skipped++; return; }
        _renderOrig(self, ctx);
    }

    /// <summary>True when the parent pushed this widget's rect and it lies wholly outside the panel clip plus
    /// <see cref="CullMargin"/>. Then it also credits the rect to <c>max_x</c>, the one thing the skipped
    /// <c>nk_panel_alloc_space</c> would have written (it feeds the horizontal scroll extent).</summary>
    private static bool Offscreen(IntPtr self, IntPtr ctx)
    {
        if (*(IntPtr*)self == _statefulVptr) return false;   // textedit: owns keyboard focus, writes its text back
        if (new Nui__Layout__Layoutable(self).NoPush != 0) return false;
        IntPtr win = *(IntPtr*)(ctx + NkCurrentOff);
        if (win == IntPtr.Zero) return false;
        byte* p = *(byte**)(win + NkWinLayoutOff);
        if (p == null || *(int*)(p + NkRowTypeOff) != NkLayoutStaticFree) return false;
        float* item = (float*)(p + NkRowItemOff), clip = (float*)(p + NkClipOff);
        float left = *(float*)(p + NkAtXOff) + item[0];
        float x = left - **(uint**)(p + NkOffsetXOff);
        float y = *(float*)(p + NkAtYOff) + item[1] - **(uint**)(p + NkOffsetYOff);
        if (x + item[2] + CullMargin > clip[0] && x - CullMargin < clip[0] + clip[2]
            && y + item[3] + CullMargin > clip[1] && y - CullMargin < clip[1] + clip[3]) return false;
        float edge = left + item[2];
        if (edge > *(float*)(p + NkMaxXOff)) *(float*)(p + NkMaxXOff) = edge;
        return true;
    }

    /// <summary>Bare <c>/nui</c>: how much the cull saved since the last ask.</summary>
    private static string Status()
    {
        string s = _renderOrig == null
            ? "Off-screen skip is not hooked (see cx.log)."
            : $"Skipped {_skipped:N0} of {_seen:N0} widget draws since the last /nui.";
        _seen = _skipped = 0;
        return s + " " + NuiSolver.Status() + " " + NuiLayoutErrors.Status();
    }
}
