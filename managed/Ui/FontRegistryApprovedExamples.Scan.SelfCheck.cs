using CX.Framework;

namespace CX.Ui;

internal static partial class FontRegistryApprovedExamples
{
    /// <summary>Fonts fontconfig lists, the folder walk without it, and fonts inside a collection.</summary>
    private static void Scan(Checker check, string dir)
    {
        string elsewhere = Path.Combine(dir, "nowhere");
        Directory.CreateDirectory(elsewhere);
        string pcf = Path.Combine(dir, "fc", "helvR12.pcf.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(pcf)!);
        File.WriteAllBytes(pcf, new byte[] { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 3 });

        // E1 Given a Linux machine whose fonts are installed only where fontconfig's configuration points, as NixOS's
        // fonts.packages and Home Manager install them When the player opens a Typeface list in CX settings Then those
        // fonts are offered, one line per family, such as Noto Sans and JetBrainsMono Nerd Font
        Example(check, "E1 the fonts fontconfig lists are offered", () =>
        {
            string fc = $"{Path.Combine(dir, "DejaVuSans.ttf")}\n{Path.Combine(dir, "DejaVuSans-Bold.ttf")}\n{Path.Combine(dir, "NotoSans-VF.ttf")}\n";
            return FontCatalog.Scan(fc, new[] { elsewhere }).Families.SequenceEqual(new[] { DejaVu, Noto });
        });

        // E2 Given a Linux machine with no fontconfig, and fonts in /usr/share/fonts, /usr/local/share/fonts,
        // ~/.local/share/fonts or ~/.fonts When the player opens a Typeface list Then those fonts are offered, as they are today
        Example(check, "E2 with no fontconfig the font folders are walked", () =>
            FontCatalog.Scan(null, new[] { Path.Combine(dir, "win"), elsewhere }).Families.SequenceEqual(new[] { DejaVu }));

        // E3 Given fontconfig lists a gzipped bitmap font (.pcf.gz) When the player opens a Typeface list Then that font is
        // not offered
        Example(check, "E3 a gzipped bitmap font is not offered", () =>
            FontCatalog.Scan($"{pcf}\n{Path.Combine(dir, "DejaVuSans.ttf")}\n", new[] { elsewhere }).Families.SequenceEqual(new[] { DejaVu }));

        // E11 Given a font collection file (.ttc) holding TrueType fonts, as Windows ships Cambria and Cambria Math in one
        // file When the player opens a Typeface list Then each family inside it is offered, and picking one draws with that
        // font, not the first one in the file
        // E44 Given Windows, or the game under Proton, with cambria.ttc in the Fonts folder When the player opens a
        // Typeface list and picks Cambria Math Then Cambria and Cambria Math are both offered, and the text draws in Cambria Math
        Example(check, "E11 E44 each font in a collection is offered and drawn", () =>
        {
            FontCatalog pair = FontCatalog.Scan(null, new[] { Path.Combine(dir, "pair") });
            FontCatalog was = FontRegistry.UseCatalog(pair);
            try
            {
                return pair.Families.SequenceEqual(new[] { DejaVu, DejaVuCondensed })
                    && Drawn(FontRegistry.FaceKey(DejaVuCondensed, 400, Normal)) == "cambria.ttc#1@400"
                    && Drawn(FontRegistry.FaceKey(DejaVu, 400, Normal)) == "cambria.ttc#0@400";
            }
            finally { FontRegistry.UseCatalog(was); }
        });

        // E43 Given fontconfig lists a collection whose fonts are CFF2, as nixpkgs ships Noto Sans CJK When the player
        // opens a Typeface list Then none of its families is offered, and no text draws blank
        Example(check, "E43 a collection of CFF2 fonts is not offered", () =>
            FontCatalog.Scan($"{Path.Combine(dir, "Cantarell-CFF2.ttc")}\n{Path.Combine(dir, "DejaVuSans.ttf")}\n", new[] { elsewhere })
                .Families.SequenceEqual(new[] { DejaVu }));

        // E45 Given fontconfig's monospace match is a font inside a collection, at index 1 or later When the console draws
        // Then it draws with that font, not the first in the file and not the UI face
        Example(check, "E45 the monospace match keeps its index in a collection", () =>
        {
            string ttc = Path.Combine(dir, "pair", "cambria.ttc");
            return FontRegistry.ParseFcMatch($"{ttc}:1") == (ttc, 1)
                && FontRegistry.ParseFcMatch($"{Path.Combine(dir, "DejaVuSans.ttf")}:0") == (Path.Combine(dir, "DejaVuSans.ttf"), 0)
                && FontRegistry.ParseFcMatch($"{Path.Combine(dir, "Cantarell-CFF2.ttc")}:0") == null
                && FontRegistry.ParseFcMatch($"{pcf}:0") == null && FontRegistry.ParseFcMatch("") == null;
        });
    }
}
