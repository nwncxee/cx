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
