using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Alloy's GUI path: <see cref="GuiCapture"/> turns every GUI scene into layers of draw records and each flush hands
/// them to <see cref="GuiPassVk"/>. The gate is its own, not <see cref="AlloyGate"/>'s world verdict -- the GUI has
/// no world frame to wait for, main menu included.
/// </summary>
internal static unsafe class GuiPass
{
    /// <summary>Ahead of <c>UiHostService</c>'s window roll-call (20) on the <c>nk_sdl_render</c> chain: the panels sit
    /// under the CX windows and NUI, as they do in the engine's order.</summary>
    private const int NkPriority = 40;
    /// <summary>The GUI program's vertex-colour location (<c>cx_gui_v.glsl</c>); nothing else in Alloy binds it.</summary>
    internal const uint LocColor = 3;

    private static ExtenderLog? _log;
    private static bool _armed, _failed, _live, _dropSaid;
    private static int* _screenW, _screenH;
    private static NkSdlRenderFn* _nkSlot;
    private static long _flushes;

    /// <summary>Is the GUI Alloy's right now? Asked once per scene before it is listed; everything it names exists
    /// by then, so a scene is never listed into a flush that cannot draw it.</summary>
    internal static bool Capturing => _armed && !_failed && Renderer.Gui.V && Renderer.On;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _screenW = (int*)Native.Sym(SymbolMap.ScreenWidth);
        _screenH = (int*)Native.Sym(SymbolMap.ScreenHeight);
        if (_screenW == null || _screenH == null)
        { log.Always("GuiPass: g_nScreenWidth/Height unresolved -- the game's GUI panels are NOT DRAWN"); return; }
        Db.DbGui.Install(log);
        Db.TextFontBake.Install(log);
        Db.TextSlots.Install(log);
        Db.TextQuads.Install(log);
        TextAtlas.Install(log);
        GuiCapture.Attach(log);
        ScreenOwner.Install(log, hooks);
        _nkSlot = hooks.ChainNkSdlRender(&OnNk, NkPriority);
        FrameRegistry.Global.Register(FramePhase.PrePresent, 5, OnPresent, "GuiPass.flush", log);
        _armed = true;
        log.Info("GuiPass: armed -- GUI scenes are listed and drawn by Alloy on Vulkan "
               + (_nkSlot != null ? "(flushed before NUI and at the present)" : "(nk_sdl_render unhooked: flushed at the present, above NUI)")
               + "; /alloy gui 0 turns the game's panels OFF, it does not hand them to the engine");
    }

    /// <summary>The panels, just before nuklear flushes (and before the CX windows chained under it).</summary>
    [UnmanagedCallersOnly]
    private static void OnNk(float aa)
    {
        Flush();
        (*_nkSlot)(aa);
    }

    /// <summary>The overlay views drawn after NUI, and the one-time program build.</summary>
    private static void OnPresent()
    {
        // Cold: a font's atlas lands on the frames after it is first named, as a Vulkan image.
        if (TextAtlas.Due) TextAtlas.Pump();
        // NUI's layers wait for SeamDraw's flush, which puts them over the panels; flushed here, its frame clear
        // erased them.
        _frameColour = false;
        _seqAtFrame = PostChain.ResolveSeq;
    }

    /// <summary>Has this frame's colour been cleared, and the world-resolve count at the last present -- a
    /// frame whose count moved had a world frame paint the whole screen, so it needs no clear.</summary>
    private static bool _frameColour;
    private static int _seqAtFrame;

    /// <summary>Does this flush owe the engine's swallowed frame clear -- the first flush of a frame no world frame
    /// covered (the menus)? Asked once per frame: the second ask is false whoever made the first.</summary>
    internal static bool TakeFrameClear()
    {
        if (_frameColour) return false;
        _frameColour = true;
        return ScreenOwner.Owned && PostChain.ResolveSeq == _seqAtFrame;
    }

    /// <summary>Our own mechanism failed: said once. Nothing draws the game's panels after this -- the scene
    /// database has no engine traversal underneath -- so it is a hard failure naming the way out.</summary>
    private static void Fail(string why)
    {
        _failed = true;
        _log?.Always($"GuiPass: {why} -- the game's GUI panels are NOT DRAWN for the rest of the session");
        AlloyGate.Fail($"the interface could not be drawn ({why})");
    }

    /// <summary>The frame's closing flush: the first <paramref name="panels"/> listed layers, CX's below-NUI windows,
    /// the rest (NUI), then the above-NUI windows. A flush that cannot draw still puts the windows over the composite.</summary>
    internal static void FlushWithWindows(int panels)
    {
        int n = GuiCapture.LayerN;
        bool drawn = !_failed && Draw(0, Math.Clamp(panels, 0, n), n, true);
        UiScene.Spent();
        GuiCapture.Reset();
        if (drawn) return;
        if (n > 0) Dropped();
        ReplayWindowsVk();
    }

    /// <summary>A Vulkan flush that could not draw: CX's windows still go over the composite on their own, both layers.</summary>
    private static void ReplayWindowsVk() { if (_log != null) GuiPassVk.ReplayLayersNow(_log); }

    /// <summary>Issue every layer captured since the last flush, then forget them. A flush that cannot run drops what
    /// it holds -- the engine did not draw those scenes either -- and says so once.</summary>
    private static void Flush()
    {
        if (GuiCapture.LayerN == 0) return;
        if (_failed || !Draw(0, GuiCapture.LayerN, GuiCapture.LayerN, false)) Dropped();
        GuiCapture.Reset();
    }

    /// <summary>The screen the GUI draws into, from the engine's own globals: false until they resolve.</summary>
    internal static bool Screen(out int w, out int h)
    {
        w = h = 0;
        if (_screenW == null || _screenH == null) return false;
        w = *_screenW; h = *_screenH;
        return w > 0 && h > 0;
    }

    private static void Dropped()
    {
        if (_dropSaid) return;
        _dropSaid = true;
        _log?.Always("GuiPass: a GUI flush could not draw on Vulkan -- that frame's GUI is NOT DRAWN");
    }

    /// <summary>Layers <paramref name="from"/>..<paramref name="mid"/>, then (with <paramref name="windows"/>) CX's below-NUI
    /// windows, layers <paramref name="mid"/>..<paramref name="to"/>, and the above-NUI windows: one Vulkan flush.</summary>
    private static bool Draw(int from, int mid, int to, bool windows)
    {
        int w = *_screenW, h = *_screenH;
        if (PresentTarget.Width != w || PresentTarget.Height != h) PresentTarget.Ensure(w, h);
        if (!PresentTarget.Composed) { if (GlContextService.WorkDue) GlContextService.RunInBracketHere(); Db.MeshArena.Pump(); }
        return VulkanFlush(from, mid, to, windows);
    }

    /// <summary>The flush on Vulkan: <see cref="GuiPassVk"/> records and submits it, with no GL.</summary>
    private static bool VulkanFlush(int from, int mid, int to, bool windows)
    {
        // every flush, the world frame's included: the model views' texture work is Vulkan's here and needs no bracket
        CX.Services.Nui.NuiModelView.Pump();
        bool ok = _log != null && GuiPassVk.Flush(from, mid, to, windows, _log);
        PresentTarget.HandBack();
        if (!ok) return false;
        _flushes++;
        if (!_live) { _live = true; _log?.Info($"GuiPass: LIVE on Vulkan -- {GuiCapture.LayerN} GUI scene(s), {GuiCapture.DrawN} draw(s) this flush"); }
        return true;
    }

    /// <summary>The GUI's resources die with the device.</summary>
    internal static void ContextLost()
    {
        CX.Ui.TextRig.ContextLost();   // the windows' VAO and programs went with it
        CX.Ui.TextRig.VkContextLost();
        UiScene.ContextLost();
        TextAtlas.ContextLost();   // CX-119: every font atlas went with the context
        GuiPassVk.ContextLost();   // the Vulkan device went with the context, and every image and program on it
    }

    internal static string Stats() => !_armed ? "gui: not armed" : _failed ? "gui: NOT DRAWN (FAILED)"
        : !Renderer.Gui.V ? "gui: off (/alloy gui 0, the game's panels are NOT DRAWN)"
        : $"gui: Alloy -- {_flushes} flushes"
          + "; " + GuiCapture.Stats() + "; " + ScreenOwner.Stats() + "; " + PresentTarget.Stats()
          + (GuiPassVk.Stats() is { Length: > 0 } vk ? "; " + vk : "");
}
