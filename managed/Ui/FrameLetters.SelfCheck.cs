using System.Text;

namespace CX.Ui;

/// <summary>A frame's 2D order as letters, bottom to top, on either render path: <paramref name="panels"/> for the game's
/// panels, N for NUI, <paramref name="overlays"/> for its overlay scenes and cursor, and whatever each CX window appends.</summary>
internal readonly struct FrameLetters(StringBuilder drawn, string panels, string overlays) : IGuiFrameSink, INkFrameSink
{
    /// <summary>One Alloy frame of the registered windows into <paramref name="drawn"/>, cleared first.</summary>
    internal static string Alloy(StringBuilder drawn, string panels, string overlays)
    {
        drawn.Clear();
        GuiFrame.Draw(new FrameLetters(drawn, panels, overlays), true);
        return drawn.ToString();
    }

    /// <summary>One Alloy-off frame of the registered windows into <paramref name="drawn"/>, cleared first, at CX's two
    /// sites in the order the engine reaches them: the world view (none at a menu, <paramref name="world"/> false),
    /// its panels, nuklear's flush, its overlay scenes.</summary>
    internal static string Gl(StringBuilder drawn, string panels, string overlays, bool world = true)
    {
        drawn.Clear();
        var sink = new FrameLetters(drawn, panels, overlays);
        bool under = false;
        if (world) NkFrame.World(sink, ref under);
        drawn.Append(panels);
        NkFrame.Flush(sink, under);
        drawn.Append(overlays);
        return drawn.ToString();
    }

    public bool Windows(UiLayer layer) { Windows(layer, layer); return true; }
    public void Windows(UiLayer from, UiLayer to) { for (UiLayer l = from; l <= to; l++) UiHost.DrawFrame(l); }
    bool IGuiFrameSink.Panels() { drawn.Append(panels); return true; }
    bool IGuiFrameSink.Nui() { drawn.Append('N'); return true; }
    void INkFrameSink.Nui() => drawn.Append('N');
    bool IGuiFrameSink.Overlays() { drawn.Append(overlays); return true; }
}
