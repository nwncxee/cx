using CX.Framework;
using CX.Services.Chat;
using CX.Services.Dialog;
using CX.Services.Panels;

namespace CX.Ui;

internal static partial class FontRegistryApprovedExamples
{
    /// <summary>The DejaVu Sans fixtures as a hak's resources: regular, bold, oblique and bold oblique, each under a resref.</summary>
    private static readonly (string ResRef, string Fixture)[] HakDejaVu =
    {
        ("hakdv_r", "DejaVuSans.ttf"), ("hakdv_b", "DejaVuSans-Bold.ttf"), ("hakdv_i", "DejaVuSans-Oblique.ttf"), ("hakdv_bi", "DejaVuSans-BoldOblique.ttf"),
    };

    /// <summary>Server content holding the named resrefs of <see cref="HakDejaVu"/>, each from a hak.</summary>
    private static FontCatalog Hak(params string[] resrefs)
        => FontCatalog.FromContent(HakDejaVu.Where(h => resrefs.Contains(h.ResRef)).Select(h => (h.ResRef, FontSource.Server, Fixture(h.Fixture))).ToArray());

    /// <summary>Runs <paramref name="body"/> with <paramref name="installed"/> as the machine's fonts and <paramref name="content"/>
    /// as the loaded module's, putting both back after.</summary>
    private static bool WithFonts(FontCatalog installed, FontCatalog content, Func<bool> body)
    {
        FontCatalog wasInstalled = FontRegistry.UseCatalog(installed);
        FontCatalog wasContent = FontRegistry.UseContent(content);
        try { return body(); }
        finally { FontRegistry.UseContent(wasContent); FontRegistry.UseCatalog(wasInstalled); }
    }

    /// <summary>Releases every font row a check held, whatever it held.</summary>
    private static void ReleaseFontHolds()
    {
        string[] keys = { "ui.typeface", "ui.weight", "ui.style", "dialog.typeface", "dialog.weight", "dialog.style" };
        ServerPolicy.Apply(string.Join('\n', keys.Select(k => k + "=")));
        foreach (string k in keys) PolicyRefusals.Forget(k);
    }

    /// <summary>Fonts a hak, the module or NWSync supply: listed by family, drawn from their own bytes, held by a server's policy.</summary>
    private static void Content(Checker check, string dir)
    {
        // a machine whose only installed family is Noto Sans, so DejaVu Sans comes from the hak alone
        FontCatalog noto = FontCatalog.Scan(null, new[] { Path.Combine(dir, "narrow") });
        FontCatalog all = FontCatalog.Scan(null, new[] { dir });
        string[] hakFour = { "hakdv_r", "hakdv_b", "hakdv_i", "hakdv_bi" };

        // E53 Given a hak supplies oldfell.ttf, whose family name is Old Fell When the module has loaded Then the UI's, every
        // element's and every chat window's Typeface list offers Old Fell once, by that name and not by its resref, and picking it draws oldfell.ttf
        Example(check, "E53 a hak's font is offered by its family", () => WithFonts(noto, Hak("hakdv_r"), () =>
        {
            TextStyle w = Window("cx-fontcheck-e53");
            string[]? gear = Gear(Chat("")).Find(r => r.Text == "Typeface")?.Choices;
            bool once(string[] c) => c.Count(n => n == DejaVu) == 1 && !c.Any(n => n.Contains("hakdv", StringComparison.OrdinalIgnoreCase));
            return once(UiKnobs.Typeface.Choices) && once(w.Typeface.Choices) && gear != null && once(gear)
                && Drawn(FontRegistry.FaceKey(DejaVu, 400, FontRegistry.StyleNormal)) == "hakdv_r.ttf#0@400";
        }));

        // E54 Given a hak supplies Old Fell as oldfell_r.ttf at 400, oldfell_b.ttf at 700 and oldfell_i.ttf at 400 italic When
        // the player picks Old Fell, then Default weight 700, then Default style Italic at weight 400 Then the Default weight and
        // Default style rows show beside it, and the UI draws oldfell_b.ttf, then oldfell_i.ttf, by E10's rule
        Example(check, "E54 a hak's family takes the weight and style rows", () => WithFonts(noto, Hak("hakdv_r", "hakdv_b", "hakdv_i"), () =>
            WithUi(DejaVu, 700, FontRegistry.StyleNormal, () =>
                UiKnobs.FontWeight.ShowIf!() && UiKnobs.FontStyle.ShowIf!() && Drawn(FontRegistry.UiFace()) == "hakdv_b.ttf#0@700")
            && WithUi(DejaVu, 400, FontRegistry.StyleItalic, () => Drawn(FontRegistry.UiFace()) == "hakdv_i.ttf#0@400i")));

        // E55 Given a hak supplies Old Fell only as oldfell_b.ttf, at 700 When the UI asks weight 300, or a card asks for w=900
        // or Italic Then every run draws oldfell_b.ttf as it is, with no thickened, thinned or invented letters
        Example(check, "E55 a hak's font of one weight draws its own", () => WithFonts(noto, Hak("hakdv_b"), () =>
            WithUi(DejaVu, 300, FontRegistry.StyleNormal, () =>
                PanelMarkup.TryParse("<w=900>a</w>", out Run[] r, out _) && r.Length == 1
                && Drawn(FontRegistry.UiFace()) == "hakdv_b.ttf#0@700"
                && Drawn(PanelFonts.RunFace(r[0], out _)) == "hakdv_b.ttf#0@700"
                && Drawn(FontRegistry.UiRunFace(0, FontRegistry.StyleItalic, false, out bool slant)) == "hakdv_b.ttf#0@700" && !slant)));

        // E56 Given a .ttf loose in override/, and the base game's own .ttf files When the module has loaded Then neither is
        // offered in any Typeface list; only a hak's, the module's and NWSync's fonts are
        Example(check, "E56 only server content is listed", () =>
        {
            FontCatalog loose = FontCatalog.FromContent(new[] { ("ovrdv", FontSource.Loose, Fixture("DejaVuSans.ttf")), ("basedv", FontSource.Game, Fixture("DejaVuSans-Bold.ttf")) });
            FontCatalog mixed = FontCatalog.FromContent(new[] { ("ovrdv", FontSource.Loose, Fixture("DejaVuSans.ttf")), ("hakdv_b", FontSource.Server, Fixture("DejaVuSans-Bold.ttf")) });
            return loose.Families.Length == 0
                && WithFonts(noto, loose, () => !UiKnobs.Typeface.Choices.Contains(DejaVu))
                && WithFonts(noto, mixed, () => Drawn(FontRegistry.FaceKey(DejaVu, 400, FontRegistry.StyleNormal)) == "hakdv_b.ttf#0@700");
        });

        // E57 Given the player has Old Fell installed, and the server's hak supplies its own Old Fell When the player picks Old
        // Fell while the module is loaded Then the list offers Old Fell once, and the UI draws the hak's file
        Example(check, "E57 a hak's family wins over an installed one of the same name", () =>
            WithFonts(all, Hak("hakdv_r"), () =>
                UiKnobs.Typeface.Choices.Count(n => n == DejaVu) == 1
                && WithUi(DejaVu, 700, FontRegistry.StyleNormal, () => Drawn(FontRegistry.UiFace()) == "hakdv_r.ttf#0@400"))
            && WithFonts(all, FontCatalog.Empty, () => WithUi(DejaVu, 700, FontRegistry.StyleNormal, () => Drawn(FontRegistry.UiFace()) == "DejaVuSans-Bold.ttf#0@700")));

        // E58 Given cx.cfg holds ui.typeface=Old Fell, a font only the server's hak supplies When the client starts, and later
        // the module loads Then at the main menu the UI draws the built-in face and cx.cfg still holds Old Fell, and once the
        // module loads the UI draws Old Fell without the player picking it again
        Example(check, "E58 a saved hak font waits for its content", () => WithFonts(noto, FontCatalog.Empty, () =>
        {
            Settings.Read("cx-fontcheck-e58.typeface=DejaVu Sans\n");
            try
            {
                TextStyle w = Window("cx-fontcheck-e58");
                bool menu = w.Typeface.V == DejaVu && w.Typeface.FormatStored() == DejaVu && FontRegistry.Effective(w.FaceName) == FontRegistry.DefaultName;
                FontCatalog was = FontRegistry.UseContent(Hak(hakFour));
                try { return menu && w.Typeface.V == DejaVu && Drawn(w.FaceName) == "hakdv_r.ttf#0@400"; }
                finally { FontRegistry.UseContent(was); }
            }
            finally { foreach (string k in new[] { "typeface", "weight", "style" }) Settings.Forget("cx-fontcheck-e58." + k); }
        }));

        // E59 Given the player picked Old Fell from a server's hak When they leave for a server or a module without that hak
        // Then Old Fell leaves every Typeface list, the UI draws the built-in face, and cx.cfg still holds Old Fell
        Example(check, "E59 a hak's font leaves when its content does", () => WithFonts(noto, Hak(hakFour), () =>
            WithUi(DejaVu, 400, FontRegistry.StyleNormal, () =>
            {
                TextStyle w = Window("cx-fontcheck-e59");
                bool listed = UiKnobs.Typeface.Choices.Contains(DejaVu) && w.Typeface.Choices.Contains(DejaVu);
                FontCatalog was = FontRegistry.UseContent(FontCatalog.Empty);
                try
                {
                    return listed && !UiKnobs.Typeface.Choices.Contains(DejaVu) && !w.Typeface.Choices.Contains(DejaVu)
                        && FontRegistry.Effective(FontRegistry.UiFace()) == FontRegistry.DefaultName && UiKnobs.Typeface.FormatStored() == DejaVu;
                }
                finally { FontRegistry.UseContent(was); }
            })));

        // E60 Given the server's hak supplies Old Fell, and the player's own UI typeface is Noto Sans When the server sends the
        // policy lines ui.typeface=Old Fell, ui.weight=700 and ui.style=Italic Then the UI and every element that follows it draw
        // Old Fell at 700 italic, the three rows show as held by the server, cx.cfg keeps Noto Sans, and the release line
        // ui.typeface= brings Noto Sans back
        Example(check, "E60 a server holds the UI's font", () => WithFonts(noto, Hak(hakFour), () =>
            WithUi(Noto, 400, FontRegistry.StyleNormal, () =>
            {
                TextStyle follows = Window("cx-fontcheck-e60");
                try
                {
                    bool applied = ServerPolicy.Apply("ui.typeface=DejaVu Sans\nui.weight=700\nui.style=Italic") == 3;
                    bool held = UiKnobs.Typeface.Enforced && UiKnobs.FontWeight.Enforced && UiKnobs.FontStyle.Enforced
                        && UiKnobs.Typeface.FormatStored() == Noto
                        && Drawn(FontRegistry.UiFace()) == "hakdv_bi.ttf#0@700i" && Drawn(follows.FaceName) == "hakdv_bi.ttf#0@700i"
                        && Drawn(ChatService.FaceName(Chat(""))) == "hakdv_bi.ttf#0@700i";
                    ServerPolicy.Apply("ui.typeface=");
                    return applied && held && !UiKnobs.Typeface.Enforced && UiKnobs.Typeface.V == Noto;
                }
                finally { ReleaseFontHolds(); }
            })));

        // E61 Given the player set the dialog window's Typeface to Noto Sans When the server holds dialog.typeface=Old Fell,
        // dialog.weight=700 and dialog.style=Italic Then the dialog window draws Old Fell at 700 italic, and the same holds for
        // every element's typeface, weight and style rows
        Example(check, "E61 a server holds an element's own font", () => WithFonts(noto, Hak(hakFour), () =>
        {
            TextStyle d = DialogKnobs.Text;
            string was = d.Typeface.V;
            d.Typeface.V = Noto;
            try
            {
                bool applied = ServerPolicy.Apply("dialog.typeface=DejaVu Sans\ndialog.weight=700\ndialog.style=Italic") == 3;
                Knob[] rows = KnobGroup.All.SelectMany(g => g.Knobs).Where(k => k.Label is "Typeface" or "Default weight" or "Default style").ToArray();
                return applied && Drawn(d.FaceName) == "hakdv_bi.ttf#0@700i" && d.Typeface.FormatStored() == Noto
                    && rows.Length >= 6 && rows.All(k => ServerPolicy.Holdable(k.Key));
            }
            finally { ReleaseFontHolds(); d.Typeface.V = was; }
        }));

        // E62 Given no hak, NWSync file or installed font has the family Old Fell When the server sends ui.typeface=Old Fell
        // Then the hold is refused, /policy lists it under refused with the reason, and the UI keeps the player's own typeface
        Example(check, "E62 a hold on a font nobody supplies is refused", () => WithFonts(noto, FontCatalog.Empty, () =>
            WithUi(Noto, 400, FontRegistry.StyleNormal, () =>
            {
                try
                {
                    bool refused = ServerPolicy.Apply("ui.typeface=DejaVu Sans") == 0 && !UiKnobs.Typeface.Enforced && UiKnobs.Typeface.V == Noto
                        && PolicyRefusals.All().Any(r => r.Key == "ui.typeface" && r.Reason.Length > 0);
                    // the same row takes a font that is there, so the refusal is the missing font's and not the row's
                    return refused && ServerPolicy.Apply("ui.typeface=Noto Sans") == 1 && UiKnobs.Typeface.Enforced;
                }
                finally { ReleaseFontHolds(); PolicyRefusals.Forget("ui.typeface"); }
            })));

        // E63 Given one chat window that follows the UI, and another whose gear sets its own typeface to Noto Sans When the
        // server holds ui.typeface=Old Fell Then the first window draws Old Fell and the second keeps Noto Sans, since a chat
        // window's gear values are not knobs a server can hold
        Example(check, "E63 a chat window's own gear font is not held", () => WithFonts(noto, Hak(hakFour), () =>
            WithUi(FontRegistry.DefaultName, 400, FontRegistry.StyleNormal, () =>
            {
                try
                {
                    string own = Drawn(ChatService.FaceName(Chat(Noto)));
                    return ServerPolicy.Apply("ui.typeface=DejaVu Sans") == 1
                        && Drawn(ChatService.FaceName(Chat(""))) == "hakdv_r.ttf#0@400" && Drawn(ChatService.FaceName(Chat(Noto))) == own && own.StartsWith("NotoSans");
                }
                finally { ReleaseFontHolds(); }
            })));
    }
}
