using CX.Framework;
using CX.Services.Chat;
using CX.Services.Panels;

namespace CX.Ui;

internal static partial class FontRegistryApprovedExamples
{
    /// <summary>The full name (name id 4) of the font a registry name draws, read from its own bytes; null for none.</summary>
    private static string? FullNameOf(string name) => FontRegistry.Bytes(name) is byte[] b ? Sfnt.Read(b, 0)?.FullName : null;

    /// <summary>The built-in DejaVu Sans's own weights and slanted letters, from the files CX ships.</summary>
    private static void BuiltIn(Checker check, string dir)
    {
        // E34 Given the UI typeface left at default, the built-in DejaVu Sans When the player sets the UI Default weight to
        // 700 Then the UI draws the built-in DejaVu Sans Bold, never thickened regular letters
        Example(check, "E34 the built-in face draws its own bold", () =>
            WithUi(FontRegistry.DefaultName, 700, FontRegistry.StyleNormal, () => FullNameOf(ChatService.FaceName(Chat(""))) == "DejaVu Sans Bold")
            && FullNameOf(FontRegistry.FaceKey(FontRegistry.DefaultName, 700, FontRegistry.StyleNormal)) == "DejaVu Sans Bold");

        // E46 Given the UI typeface left at default When the player opens Interface, Windows and text Then the Default
        // weight and Default style rows sit beside it
        Example(check, "E46 the rows show for the built-in face", () =>
            WithUi(FontRegistry.DefaultName, 400, FontRegistry.StyleNormal, () => UiKnobs.FontWeight.ShowIf!() && UiKnobs.FontStyle.ShowIf!()));

        // E47 Given the UI typeface left at default, which CX ships at weights 200, 400 and 700 When the player sets Default
        // weight 300, 700, 100 or 900 Then the UI draws DejaVu Sans ExtraLight, Bold, ExtraLight and Bold in turn, by E10's rule
        Example(check, "E47 the built-in face's weights", () =>
            new[] { 300, 700, 100, 900 }.Select(w => FullNameOf(FontRegistry.FaceKey(FontRegistry.DefaultName, w, FontRegistry.StyleNormal)))
                .SequenceEqual(new[] { "DejaVu Sans ExtraLight", "DejaVu Sans Bold", "DejaVu Sans ExtraLight", "DejaVu Sans Bold" }));

        // E48 Given the UI typeface left at default When the player sets Default style Italic at weight 400, 700 and 200 Then
        // the UI draws DejaVu Sans Oblique, DejaVu Sans Bold Oblique, and DejaVu Sans Oblique, the lightest slanted file CX ships
        Example(check, "E48 the built-in face's slanted letters", () =>
            new[] { 400, 700, 200 }.Select(w => FullNameOf(FontRegistry.FaceKey(FontRegistry.DefaultName, w, FontRegistry.StyleItalic)))
                .SequenceEqual(new[] { "DejaVu Sans Oblique", "DejaVu Sans Bold Oblique", "DejaVu Sans Oblique" }));

        // E49 Given the UI typeface left at default, and a player who touches no Default weight or Default style row When the
        // client starts after the update Then the UI draws the built-in DejaVu Sans exactly as before
        Example(check, "E49 an untouched built-in face is the same face", () =>
            FontRegistry.FaceKey(FontRegistry.DefaultName, FontRegistry.Regular, FontRegistry.StyleNormal) == FontRegistry.DefaultName
            && WithUi(FontRegistry.DefaultName, FontRegistry.Regular, FontRegistry.StyleNormal, () => FontRegistry.UiFace() == FontRegistry.DefaultName));

        // E50 Given a machine with no DejaVu installed, as most Windows machines are When the player sets the UI Default weight
        // to 700 at the default typeface Then the UI draws DejaVu Sans Bold from CX's own files
        Example(check, "E50 the built-in weights need no installed font", () =>
        {
            FontCatalog was = FontRegistry.UseCatalog(FontCatalog.Empty);
            try { return FullNameOf(FontRegistry.FaceKey(FontRegistry.DefaultName, 700, FontRegistry.StyleNormal)) == "DejaVu Sans Bold"; }
            finally { FontRegistry.UseCatalog(was); }
        });

        // E51 Given the UI typeface left at default When a card asks for bold, or for weight 300 Then that run draws in DejaVu
        // Sans Bold, or DejaVu Sans ExtraLight
        Example(check, "E51 card markup on the built-in face", () =>
            PanelMarkup.TryParse("<b>bold</b><w=300>light</w>", out Run[] r, out _) && r.Length == 2
            && WithUi(FontRegistry.DefaultName, 400, FontRegistry.StyleNormal, () =>
                FullNameOf(PanelFonts.RunFace(r[0], out _)) == "DejaVu Sans Bold" && FullNameOf(PanelFonts.RunFace(r[1], out _)) == "DejaVu Sans ExtraLight"));

        // E52 Given cx.cfg holds font.default naming another file When the player sets the UI Default weight to 700 or
        // Default style Italic Then that file draws as it is, as E33 keeps any font a cx.cfg line names
        Example(check, "E52 a font.default line stays one face", () =>
        {
            bool own = Settings.Raw("font.default") != null;
            if (!own) Settings.Read($"font.default={Path.Combine(dir, "DejaVuSans-ExtraLight.ttf")}\n");
            FontRegistry.UseCatalog(FontRegistry.UseCatalog(FontCatalog.Empty));
            try
            {
                return FontRegistry.FaceKey(FontRegistry.DefaultName, 700, FontRegistry.StyleNormal) == FontRegistry.DefaultName
                    && FontRegistry.FaceKey(FontRegistry.DefaultName, 400, FontRegistry.StyleItalic) == FontRegistry.DefaultName;
            }
            finally
            {
                if (!own) Settings.Forget("font.default");
                FontRegistry.UseCatalog(FontRegistry.UseCatalog(FontCatalog.Empty));
            }
        });
    }
}
