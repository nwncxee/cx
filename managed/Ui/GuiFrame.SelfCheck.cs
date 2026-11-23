using System;
using System.Text;
using CX.Framework;
using CX.Services.CombatText;
using CX.Services.Nameplate;

namespace CX.Ui;

/// <summary>One frame's 2D order as a string of letters, bottom to top, and the parts of it that already held
/// before plates moved under the game's panels: plain regression tests, the examples E2 and E6 among them.</summary>
internal static class GuiFrameSelfCheck
{
    /// <summary>Q then E for the game's quickbar and its Examine window, the order the engine lists those panels in.</summary>
    private const string Panels = "QE";
    /// <summary>O then M for the game's overlay and its cursor.</summary>
    private const string Overlays = "OM";

    /// <summary>A frame with the two world-anchored windows the services make (p = nameplates and chat bubbles,
    /// d = damage numbers), a CX chat box (c) and a hover card over NUI (u), each drawing its letter.</summary>
    internal static string Letters()
    {
        var drawn = new StringBuilder();
        UiHost.Alone(() =>
        {
            Window d = CombatTextService.NewWindow(), p = NameplateService.NewWindow();
            d.DrawContent = () => drawn.Append('d');
            p.DrawContent = () => drawn.Append('p');
            _ = new Window(0, 0, 100, 100) { DrawContent = () => drawn.Append('c') };
            _ = new Window(0, 0, 50, 50) { Layer = UiLayer.AboveNui, DrawContent = () => drawn.Append('u') };
            FrameLetters.Alloy(drawn, Panels, Overlays);
        });
        return drawn.ToString();
    }

    /// <summary>Oracle: the letters each fixture appends and the order the examples state, written here by hand.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GuiFrame.SelfCheck");
        string f = Letters();
        // Given the Examine window is open and a creature stands beside it on screen
        // When that creature's nameplate shows
        // Then the whole nameplate is visible
        check.Expect(f.Split('p').Length == 2, $"E2 a plate nothing covers is drawn, once: {f}");
        // Given the Examine window is open and the CX chat box overlaps it
        // When the frame draws
        // Then the CX chat box covers the Examine window, as it does today
        check.Expect(f.IndexOf('c', StringComparison.Ordinal) > f.IndexOf('E', StringComparison.Ordinal),
            $"E6 the CX chat box draws over the Examine window: {f}");
        check.Expect(f.IndexOf('N', StringComparison.Ordinal) > f.IndexOf('p', StringComparison.Ordinal),
            $"a NUI window covers a plate: {f}");
        check.Expect(f.IndexOf('c', StringComparison.Ordinal) > f.IndexOf('p', StringComparison.Ordinal),
            $"the CX chat box covers a plate: {f}");
        check.Expect(f.Contains("Nu", StringComparison.Ordinal), $"a hover card draws right over NUI: {f}");
        check.Expect(f.EndsWith('M') && f.IndexOf('M', StringComparison.Ordinal) > f.IndexOf('u', StringComparison.Ordinal)
                     && f.IndexOf('M', StringComparison.Ordinal) > f.IndexOf('N', StringComparison.Ordinal),
            $"the cursor draws over a CX window above NUI and over NUI, last: {f}");
        check.Expect(f.IndexOf('O', StringComparison.Ordinal) > f.IndexOf('u', StringComparison.Ordinal),
            $"the game's overlay scene draws over the CX windows above NUI, as the engine draws it after NUI: {f}");
        string menu = "", twice = "";
        var drawn = new StringBuilder();
        UiHost.Alone(() =>
        {
            NameplateService.NewWindow().DrawContent = () => drawn.Append('p');
            _ = new Window(0, 0, 50, 50) { Layer = UiLayer.AboveNui, DrawContent = () => drawn.Append('u') };
            menu = FrameLetters.Gl(drawn, Panels, Overlays, world: false);
            drawn.Clear();
            var sink = new FrameLetters(drawn, Panels, Overlays);
            bool under = false;
            NkFrame.World(sink, ref under);
            NkFrame.World(sink, ref under);
            NkFrame.Flush(sink, under);
            twice = drawn.ToString();
        });
        check.Expect(menu == "QEpNuOM", $"with Alloy off and no world view, the world-anchored windows draw at nuklear's flush, under NUI and a window above it: {menu}");
        check.Expect(twice == "pNu", $"with Alloy off a second world view in one frame draws the world-anchored windows no second time: {twice}");
        if (check.Ok) log.Info("GuiFrame.SelfCheck: passed");
    }
}
