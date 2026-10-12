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
        NuiModelView.SelfCheck(log);
        NuiScrollbars.SelfCheck(log);
        NuiSolver.SelfCheck(log);
        NuiScrollbarHide.SelfCheck(log);
        NuiLayoutErrors.SelfCheck(log);
        NuiHitTest.SelfCheck(log);
        NuiItemIcons.Init(log, hooks);
        NuiModelView.Init(log, hooks);
        NuiScrollbars.Init(log, hooks);
        NuiScrollbarHide.Init(log, hooks);
        NuiSolver.Init(log, hooks);
        NuiLayoutErrors.Init(log, hooks);
        NuiHitTest.Init(log, hooks);
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
        if (!_enabled!.V) { _renderOrig(self, ctx); return; }
        bool known = NuiNuklear.TryBox(self, ctx, out var box);
        if (known && _cull!.V && Offscreen(self, in box)) { _skipped++; return; }
        if (NuiHitTest.Armed && NuiHitTest.OverCurrent(ctx) && (known || NuiHitTest.Query(self, ctx, out box)) && NuiHitTest.Inside(ctx, in box))
        {
            NuiHitTest.Begin();
            _renderOrig(self, ctx);
            NuiHitTest.Record(self, in box);
            return;
        }
        _renderOrig(self, ctx);
    }

    /// <summary>True when the parent pushed this widget's rect and it lies wholly outside the panel clip plus
    /// <see cref="CullMargin"/>. Then it also credits the rect to <c>max_x</c>, the one thing the skipped
    /// <c>nk_panel_alloc_space</c> would have written (it feeds the horizontal scroll extent).</summary>
    private static bool Offscreen(IntPtr self, in NuiNuklear.Box b)
    {
        if (*(IntPtr*)self == _statefulVptr) return false;   // textedit: owns keyboard focus, writes its text back
        if (b.X + b.W + CullMargin > b.ClipX && b.X - CullMargin < b.ClipX + b.ClipW
            && b.Y + b.H + CullMargin > b.ClipY && b.Y - CullMargin < b.ClipY + b.ClipH) return false;
        float edge = b.Left + b.W;
        if (edge > *(float*)(b.Panel + NuiNuklear.MaxXOff)) *(float*)(b.Panel + NuiNuklear.MaxXOff) = edge;
        return true;
    }

    /// <summary>Bare <c>/nui</c>: how much the cull saved since the last ask.</summary>
    private static string Status()
    {
        string s = _renderOrig == null
            ? "Off-screen skip is not hooked (see cx.log)."
            : $"Skipped {_skipped:N0} of {_seen:N0} widget draws since the last /nui.";
        _seen = _skipped = 0;
        return s + " " + NuiSolver.Status() + " " + NuiLayoutErrors.Status() + " " + NuiHitTest.Status();
    }
}
