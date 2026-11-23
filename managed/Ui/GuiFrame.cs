namespace CX.Ui;

/// <summary>What draws one step of the frame's 2D order. Each answers false to refuse the rest of the frame.</summary>
internal interface IGuiFrameSink
{
    /// <summary>CX's own windows on <paramref name="layer"/>.</summary>
    bool Windows(UiLayer layer);
    /// <summary>The game's own panels, bottom to top.</summary>
    bool Panels();
    /// <summary>Every NUI window.</summary>
    bool Nui();
    /// <summary>The game's two overlay scenes, <c>sceneGUI_Overlay</c> then the cursor's <c>sceneGUI_MouseOverlay</c>.</summary>
    bool Overlays();
}

/// <summary>The frame's 2D order, bottom to top, said once: whoever draws a frame's GUI walks it through here.</summary>
internal static class GuiFrame
{
    /// <summary>CX's world-anchored windows, the game's panels, CX's windows under NUI, NUI, the windows over it, then
    /// the game's overlay scenes, the cursor last, as the engine draws them after NUI. <paramref name="windows"/> false
    /// leaves CX's windows out: a flush of panels alone.</summary>
    internal static bool Draw<T>(in T sink, bool windows) where T : struct, IGuiFrameSink
        => (!windows || sink.Windows(UiLayer.UnderGui))
           && sink.Panels()
           && (!windows || sink.Windows(UiLayer.BelowNui))
           && sink.Nui()
           && (!windows || sink.Windows(UiLayer.AboveNui))
           && sink.Overlays();
}

/// <summary>What draws one step of the Alloy-off frame, whose 2D the engine draws around CX's two draw sites.</summary>
internal interface INkFrameSink
{
    /// <summary>CX's own windows on the layers <paramref name="from"/> to <paramref name="to"/>, drawn at once.</summary>
    void Windows(UiLayer from, UiLayer to);
    /// <summary>The rest of nuklear's chain and its own flush: every NUI window.</summary>
    void Nui();
}

/// <summary>The Alloy-off frame's 2D order, said once. The engine draws the world view, its panels, nuklear's flush,
/// then its overlay scenes; CX's world-anchored windows go on the world, the rest around NUI.</summary>
internal static class NkFrame
{
    /// <summary>The world view's tail: the world-anchored windows, under every panel. Only the frame's first world
    /// view draws them; <paramref name="drawn"/> says so to the flush.</summary>
    internal static void World<T>(in T sink, ref bool drawn) where T : struct, INkFrameSink
    {
        if (drawn) return;
        drawn = true;
        sink.Windows(UiLayer.UnderGui, UiLayer.UnderGui);
    }

    /// <summary>Nuklear's flush: the world-anchored windows when no world view drew them (a menu), then CX's layers
    /// below NUI, NUI, and the layer over it.</summary>
    internal static void Flush<T>(in T sink, bool worldDrawn) where T : struct, INkFrameSink
    {
        sink.Windows(worldDrawn ? UiLayer.BelowNui : UiLayer.UnderGui, UiLayer.BelowNui);
        sink.Nui();
        sink.Windows(UiLayer.AboveNui, UiLayer.AboveNui);
    }
}
