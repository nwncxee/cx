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
}

/// <summary>The frame's 2D order, bottom to top, said once: whoever draws a frame's GUI walks it through here.</summary>
internal static class GuiFrame
{
    /// <summary>The game's panels, CX's windows under NUI, NUI, then the windows over it.
    /// <paramref name="windows"/> false leaves CX's windows out: a flush of panels alone.</summary>
    internal static bool Draw<T>(in T sink, bool windows) where T : struct, IGuiFrameSink
        => sink.Panels()
           && (!windows || sink.Windows(UiLayer.BelowNui))
           && sink.Nui()
           && (!windows || sink.Windows(UiLayer.AboveNui));
}
