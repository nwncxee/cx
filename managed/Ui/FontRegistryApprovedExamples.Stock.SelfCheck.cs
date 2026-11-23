using CX.Framework;

namespace CX.Ui;

internal static partial class FontRegistryApprovedExamples
{
    /// <summary>Stand-ins for the base game's own fonts, which cannot ship with the check: the DejaVu Sans fixtures play
    /// a family of four files (as JetBrains Mono), and DejaVu Sans Condensed a family of one file.</summary>
    private static readonly (string ResRef, string Fixture)[] GameDejaVu =
    {
        ("fnt_dvr", "DejaVuSans.ttf"), ("fnt_dvb", "DejaVuSans-Bold.ttf"), ("fnt_dvi", "DejaVuSans-Oblique.ttf"),
        ("fnt_dvbi", "DejaVuSans-BoldOblique.ttf"), ("fnt_dvc", "DejaVuSansCondensed.ttf"),
    };

    /// <summary>The stand-in game fonts as the content scan reads them, with <paramref name="more"/> after them.</summary>
    private static FontCatalog Game(params (string, FontSource, byte[])[] more)
        => FontCatalog.FromContent(GameDejaVu.Select(g => (g.ResRef, FontSource.Game, Fixture(g.Fixture))).Concat(more).ToArray());

    /// <summary>The base game's own fonts: offered in every Typeface list, at the top, drawn from the game's files.</summary>
    private static void Stock(Checker check, string dir)
    {
        FontCatalog noto = FontCatalog.Scan(null, new[] { Path.Combine(dir, "narrow") });
        FontCatalog all = FontCatalog.Scan(null, new[] { dir });

        // E64 Given the stock game, with no hak and no module loaded
        // When the player opens any Typeface list, at the main menu or in play
        // Then it offers Neverwinter Nights EE Default, Neverwinter Nights EE Default High Resolution, NWN_Main_Text and JetBrains Mono
        Example(check, "E64 the game's own fonts are offered", () => WithFonts(noto, Game(), () =>
        {
            bool offers(string[] c) => c.Contains(DejaVu) && c.Contains(DejaVuCondensed);
            return offers(UiKnobs.Typeface.Choices) && offers(Window("cx-fontcheck-e64").Typeface.Choices);
        }));

        // E65 Given the game's own fonts and fonts installed on the machine
        // When the player opens a Typeface list
        // Then it reads default first, then the game's fonts in alphabetical order, then every other font in alphabetical order
        Example(check, "E65 the game's fonts sit at the top of the list", () => WithFonts(noto, Game(), () =>
        {
            string[] c = UiKnobs.Typeface.Choices;
            string[] rest = c.Skip(3).ToArray();
            return c.Length > 4 && c[0] == FontRegistry.DefaultName && c[1] == DejaVu && c[2] == DejaVuCondensed
                && rest.Contains(Noto) && rest.Any(n => n.StartsWith("cx-fontcheck-mine-", StringComparison.Ordinal))
                && rest.SequenceEqual(rest.Order(StringComparer.OrdinalIgnoreCase));
        }));

        // E66 Given the player picked JetBrains Mono
        // When they set Default weight to 300 and Default style to italic
        // Then the UI draws JetBrains Mono Light Italic, the game's own face
        Example(check, "E66 a game family takes the weight and style rows", () => WithFonts(noto, Game(), () =>
            WithUi(DejaVu, 700, FontRegistry.StyleItalic, () => UiKnobs.FontWeight!.ShowIf!() && UiKnobs.FontStyle!.ShowIf!()
                && Drawn(FontRegistry.UiFace()) == "fnt_dvbi.ttf#0@700i")));

        // E67 Given the player has JetBrains Mono installed on the machine as well
        // When they pick JetBrains Mono
        // Then the list offers it once, among the game's fonts at the top, and the UI draws the game's copy
        Example(check, "E67 the game's copy wins over an installed font of the same name", () => WithFonts(all, Game(), () =>
        {
            string[] c = UiKnobs.Typeface.Choices;
            return c.Count(n => n == DejaVu) == 1 && Array.IndexOf(c, DejaVu) == 1
                && Drawn(FontRegistry.FaceKey(DejaVu, 400, FontRegistry.StyleNormal)) == "fnt_dvr.ttf#0@400";
        }));

        // E68 Given a server's hak supplies its own NWN_Main_Text
        // When the module has loaded and the player picks NWN_Main_Text
        // Then the list offers it once, among the game's fonts at the top, and the UI draws the hak's file
        Example(check, "E68 a hak's font wins over the game's copy and keeps its place", () =>
            WithFonts(noto, Game(("hakdv_b", FontSource.Server, Fixture("DejaVuSans-Bold.ttf"))), () =>
            {
                string[] c = UiKnobs.Typeface.Choices;
                return c.Count(n => n == DejaVu) == 1 && Array.IndexOf(c, DejaVu) == 1
                    && Drawn(FontRegistry.FaceKey(DejaVu, 400, FontRegistry.StyleNormal)) == "hakdv_b.ttf#0@700";
            }));

        // E69 Given the player picked Neverwinter Nights EE Default for the UI
        // When they quit and start the client again
        // Then the main menu draws in Neverwinter Nights EE Default, and the Typeface row still shows it
        Example(check, "E69 a picked game font survives a restart", () =>
        {
            // at boot the cfg binds the row before the content scan has found the game's fonts
            bool bound = WithFonts(noto, FontCatalog.Empty, () => UiKnobs.Typeface.TryParse(DejaVuCondensed, out string v) && v == DejaVuCondensed);
            return bound && WithFonts(noto, Game(), () => WithUi(DejaVuCondensed, FontRegistry.Regular, FontRegistry.StyleNormal, () =>
                UiKnobs.Typeface.Choices.Contains(DejaVuCondensed) && Drawn(FontRegistry.UiFace()) == "fnt_dvc.ttf#0@400"));
        });
    }
}
