using CX.Framework;
using CX.Services.Chat;
using CX.Services.Chat.Core;
using CX.Services.Panels;
using static CX.Ui.FontRegistryApprovedExamples;

namespace CX.Ui;

/// <summary>The font rows' cases no approved example names, found by the mutation pass: markup edges, a chat window's
/// saved weight, the rows' visibility, a cfg name over a family, and what migration leaves alone. Oracle: the fixtures'
/// own tables and the literals written here.</summary>
internal static class FontRegistryRegressions
{
    private const string DejaVu = "DejaVu Sans";

    /// <summary>Runs over the approved suite's fixture folder and catalog, which <paramref name="dir"/> holds.</summary>
    internal static void SelfCheck(Checker check, string dir)
    {
        Example(check, "R1 <w=900> asks for 900, and <w=950> is no tag", () =>
            PanelMarkup.TryParse("<w=900>a</w><w=950>b", out Run[] r, out _) && r.Length == 2 && r[0].Weight == 900 && r[1].Weight == 0 && r[1].Text == "<w=950>b");

        Example(check, "R2 text after a closed <b> is back at no weight", () =>
            PanelMarkup.TryParse("<b>a</b>b", out Run[] r, out _) && r.Length == 2 && r[0].Weight == 700 && r[1].Weight == 0);

        Example(check, "R3 a run's own weight wins over its block's", () =>
            PanelParser.TryParse("[{\"key\":\"k\",\"blocks\":[{\"text\":\"<b>x</b>y\",\"weight\":300}]}]", out PanelDecl[] c, out _)
            && c.Length == 1 && c[0].Blocks[0].Runs is [{ Weight: 700 }, { Weight: 300 }]);

        Example(check, "R4 a chat window's weight and style are saved and read back", () =>
        {
            ChatConfig a = ChatConfig.From(new[] { new KeyValuePair<string, string>("w0.weight", "700"), new KeyValuePair<string, string>("w0.style", "2") });
            List<KeyValuePair<string, string>> saved = a.Pairs();
            ChatWindow w = Chat("");
            a.Windows[0].ApplyTo(w);
            return w.Weight == 700 && w.Style == FontRegistry.StyleItalic && WindowConfig.Capture(w) is { Weight: 700, Style: 2 }
                && saved.Contains(new KeyValuePair<string, string>("w0.weight", "700")) && saved.Contains(new KeyValuePair<string, string>("w0.style", "2"));
        });

        Example(check, "R5 the UI's weight and style rows show only for an installed family", () =>
            WithUi(FontRegistry.DefaultName, 400, FontRegistry.StyleNormal, () => !UiKnobs.Weight.ShowIf!() && !UiKnobs.Style.ShowIf!())
            && WithUi(DejaVu, 400, FontRegistry.StyleNormal, () => UiKnobs.Weight.ShowIf!() && UiKnobs.Style.ShowIf!()));

        Example(check, "R6 an older chat window typeface that is a family keeps its weight as UI", () =>
            ChatConfig.From(new[] { new KeyValuePair<string, string>("w0.typeface", DejaVu) }).Windows[0] is { Typeface: DejaVu, Weight: null, Style: null });

        Example(check, "R7 a migrated typeface keeps a weight the file sets", () =>
        {
            Settings.Read("cx-fontcheck-r7.typeface=DejaVu Sans Bold\ncx-fontcheck-r7.weight=400\n");
            try { return Window("cx-fontcheck-r7") is { Typeface.V: DejaVu, Weight.V: 400 }; }
            finally { foreach (string k in new[] { "typeface", "weight", "style" }) Settings.Forget("cx-fontcheck-r7." + k); }
        });

        Example(check, "R8 a weight row of its own no longer follows the UI", () =>
        {
            TextStyle w = Window("cx-fontcheck-r8");
            bool before = w.FollowsUi;
            w.Weight.V = 700;
            return before && !w.FollowsUi && ChatService.FollowsUi(Chat("")) && !ChatService.FollowsUi(Chat("", 700)) && !ChatService.FollowsUi(Chat("", 0, 2));
        });

        Example(check, "R9 a cfg name shadows an installed family, and draws its own file at any weight", () =>
        {
            Settings.Read($"font.{DejaVu}={Path.Combine(dir, "DejaVuSans-ExtraLight.ttf")}\n");
            FontRegistry.UseCatalog(FontRegistry.UseCatalog(FontCatalog.Empty));
            try { return FontRegistry.FaceKey(DejaVu, 700, FontRegistry.StyleNormal) == DejaVu && Drawn(DejaVu) == "DejaVuSans-ExtraLight.ttf#0@200"; }
            finally
            {
                Settings.Forget("font." + DejaVu);
                FontRegistry.Undeclare(DejaVu);
                FontRegistry.UseCatalog(FontRegistry.UseCatalog(FontCatalog.Empty));
            }
        });
    }
}
