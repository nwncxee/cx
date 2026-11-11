using System;
using System.Text;
using CX.Framework;
using CX.Services.CombatText;
using CX.Services.Nameplate;

namespace CX.Ui;

/// <summary>One frame's 2D order as a string of letters, bottom to top, and the parts of it that already held
/// before plates moved under the game's panels (plain regression tests, not approved-spec).</summary>
internal static class GuiFrameSelfCheck
{
    /// <summary>Appends a letter per thing drawn: Q then E for the game's quickbar and its Examine window, the
    /// order the engine lists those panels in, N for NUI, and whatever each CX window on a layer appends.</summary>
    private readonly struct LetterSink(StringBuilder drawn) : IGuiFrameSink
    {
        public bool Windows(UiLayer layer) { UiHost.DrawFrame(layer); return true; }
        public bool Panels() { drawn.Append("QE"); return true; }
        public bool Nui() { drawn.Append('N'); return true; }
    }

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
            GuiFrame.Draw(new LetterSink(drawn), true);
        });
        return drawn.ToString();
    }

    /// <summary>Oracle: the letters each fixture appends and the order the examples state, written here by hand.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GuiFrame.SelfCheck");
        string f = Letters();
        check.Expect(f.Split('p').Length == 2, $"E2 a plate nothing covers is drawn, once: {f}");
        check.Expect(f.IndexOf('c', StringComparison.Ordinal) > f.IndexOf('E', StringComparison.Ordinal),
            $"E6 the CX chat box draws over the Examine window: {f}");
        check.Expect(f.IndexOf('N', StringComparison.Ordinal) > f.IndexOf('p', StringComparison.Ordinal),
            $"a NUI window covers a plate: {f}");
        check.Expect(f.IndexOf('c', StringComparison.Ordinal) > f.IndexOf('p', StringComparison.Ordinal),
            $"the CX chat box covers a plate: {f}");
        check.Expect(f.EndsWith("Nu", StringComparison.Ordinal), $"a hover card draws over NUI, last: {f}");
        if (check.Ok) log.Info("GuiFrame.SelfCheck: passed");
    }
}
