using CX.Framework;

namespace CX.Services.NuiItems;

/// <summary>
/// The NUI skin's default font, read from its <c>nui_skin.tml</c> the way NUI reads it: the first <c>[[fonts]]</c> entry's
/// <c>resref</c> and <c>font_size</c>. Oracle: the skin text typed here by hand.
/// </summary>
internal static class NuiSkinFontSelfCheck
{
    private const string Skin = """
        [text]
        color = "#FFFFFFFF"

        # the first one is the UI/default font
        [[fonts]]
          name = "(default)"
          resref = "fnt_maintext"
          pixel_snap = true
          font_size = 17.0
          spacing_h = 0.5

        [[fonts]]
          name = "title"
          resref = "fnt_title"
          font_size = 36.0
        """;

    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "NuiSkinFontSelfCheck");
        check.Expect(NuiSkinFont.Parse(Skin, out string resref, out float px) && resref == "fnt_maintext" && px == 17f,
            "the first [[fonts]] entry is the default: fnt_maintext at 17 px");
        check.Expect(NuiSkinFont.Parse(Skin.Replace("fnt_maintext", "MyFont"), out resref, out _) && resref == "myfont",
            "a resref is read lowercase, as the resource tables hold it");
        check.Expect(!NuiSkinFont.Parse("[text]\ncolor = \"#FFFFFFFF\"\n", out _, out _), "a skin with no font names none");
        check.Expect(!NuiSkinFont.Parse(Skin.Replace("font_size = 17.0", "font_size = big"), out _, out _),
            "a default font with no readable size is refused, so the menu keeps its own face");
        check.Expect(NuiSkinFont.Text(Skin, out float spacing, out bool snap) && spacing == 0.5f && snap,
            "the default font's spacing_h and pixel_snap are read too: half a pixel between glyphs, each on a whole pixel");
        check.Expect(NuiSkinFont.Text(Skin.Replace("spacing_h = 0.5", "spacing_h = 0.0").Replace("pixel_snap = true", "pixel_snap = false"), out spacing, out snap)
                     && spacing == 0f && !snap, "a skin that turns them off is read as off");
        log.Info(check.Ok ? "NuiSkinFontSelfCheck: passed" : "NuiSkinFontSelfCheck: FAILED");
    }
}
