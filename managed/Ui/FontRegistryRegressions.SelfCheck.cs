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
    private const string DejaVu = "DejaVu Sans", Noto = "Noto Sans";
    /// <summary>The approved suite's <c>font.&lt;name&gt;=</c> line naming one static file.</summary>
    private const string MineStatic = "cx-fontcheck-mine-static";

    /// <summary>Runs over the approved suite's fixture folder and catalog, which <paramref name="dir"/> holds.</summary>
    internal static void SelfCheck(Checker check, string dir)
    {
        Example(check, "R1 <w=900> asks for 900, and <w=950> is no tag", () =>
            PanelMarkup.TryParse("<w=900>a</w><w=950>b", out Run[] r, out _) && r.Length == 2 && r[0].Weight == 900 && r[1].Weight == 0 && r[1].Text == "<w=950>b");

        Example(check, "R2 text after a closed <b> is back at no weight", () =>
            PanelMarkup.TryParse("<b>a</b>b", out Run[] r, out _) && r.Length == 2 && r[0].Weight == 700 && r[1].Weight == 0);

        Example(check, "R3 a run's own weight wins over its block's", () =>
            PanelParser.TryParse("[{\"key\":\"k\",\"blocks\":[{\"text\":\"<b>x</b>y\",\"weight\":300}]}]", out PanelDecl[] c, out _)
            && c.Length == 1 && c[0].Blocks[0].Runs is [{ Weight: 700 }, { Weight: 300 }]
            && PanelParser.TryParse("[{\"key\":\"k\",\"blocks\":[{\"text\":\"<b>x</b>y\",\"weight\":300,\"italic\":true}]}]", out PanelDecl[] i, out _)
            && i.Length == 1 && i[0].Blocks[0].Runs is [{ Weight: 700, Italic: true }, { Weight: 300, Italic: true }]);

        Example(check, "R4 a chat window's weight and style are saved and read back", () =>
        {
            ChatConfig a = ChatConfig.From(new[] { new KeyValuePair<string, string>("w0.weight", "700"), new KeyValuePair<string, string>("w0.style", "2") });
            List<KeyValuePair<string, string>> saved = a.Pairs();
            ChatWindow w = Chat("");
            a.Windows[0].ApplyTo(w);
            return w.Weight == 700 && w.Style == FontRegistry.StyleItalic && WindowConfig.Capture(w) is { Weight: 700, Style: 2 }
                && saved.Contains(new KeyValuePair<string, string>("w0.weight", "700")) && saved.Contains(new KeyValuePair<string, string>("w0.style", "2"));
        });

        Example(check, "R5 the UI's weight and style rows hide for a font a cx.cfg line names", () =>
            WithUi(MineStatic, 400, FontRegistry.StyleNormal, () => !UiKnobs.FontWeight.ShowIf!() && !UiKnobs.FontStyle.ShowIf!())
            && WithUi(DejaVu, 400, FontRegistry.StyleNormal, () => UiKnobs.FontWeight.ShowIf!() && UiKnobs.FontStyle.ShowIf!()));

        Example(check, "R6 an older chat window typeface that is a family keeps its weight as UI", () =>
            ChatConfig.From(new[] { new KeyValuePair<string, string>("w0.typeface", DejaVu) }).Windows[0] is { Typeface: DejaVu, Weight: null, Style: null });

        Example(check, "R7 a migrated typeface keeps a weight the file sets", () =>
        {
            Settings.Read("cx-fontcheck-r7.typeface=DejaVu Sans Bold\ncx-fontcheck-r7.weight=400\n");
            try { return Window("cx-fontcheck-r7") is { Typeface.V: DejaVu, Weight.V: 400 }; }
            finally { foreach (string k in new[] { "typeface", "weight", "style" }) Settings.Forget("cx-fontcheck-r7." + k); }
        });

        Example(check, "R8 a weight or style row of its own no longer follows the UI", () =>
        {
            TextStyle w = Window("cx-fontcheck-r8");
            bool before = w.FollowsUi;
            w.Weight.V = 700;
            bool weighted = !w.FollowsUi;
            w.Weight.V = 0;
            w.Style.V = FontRegistry.StyleItalic;
            bool styled = !w.FollowsUi;
            return before && weighted && styled && ChatService.FollowsUi(Chat("")) && !ChatService.FollowsUi(Chat("", 700)) && !ChatService.FollowsUi(Chat("", 0, 2));
        });

        Example(check, "R10 an untouched point between two touched ones at its own place moves only when both move alike", () =>
        {
            double[] pos = { 0, 5, 0, 10, 20, 10 }, apart = { 1, 0, 3, 2, 0, 2 };
            bool[] t = { true, false, true, true, false, true };
            VarFont.Interpolate(pos, apart, t, 0, 2);
            VarFont.Interpolate(pos, apart, t, 3, 5);
            return apart[1] == 0 && apart[4] == 2;
        });

        Example(check, "R11 point numbers past 255 and runs of zero deltas decode", () =>
        {
            var r = new VarFont.Reader(new byte[] { 2, 0x81, 1, 0, 0, 5, 0x81, 0x00, 5 });
            int p = 0;
            int[]? pts = r.Points(ref p);
            double[] d = r.Deltas(ref p, 3);
            return pts is [256, 261] && d is [0, 0, 5] && p == 9;
        });

        Example(check, "R12 the bake is handed the font's index inside its collection", () =>
        {
            FontCatalog was = FontRegistry.UseCatalog(FontCatalog.Scan(null, new[] { Path.Combine(dir, "pair") }));
            try { return FontRegistry.Index(FontRegistry.FaceKey("DejaVu Sans Condensed", 400, FontRegistry.StyleNormal)) == 1 && FontRegistry.Index(FontRegistry.FaceKey(DejaVu, 400, FontRegistry.StyleNormal)) == 0; }
            finally { FontRegistry.UseCatalog(was); }
        });

        Example(check, "R13 an element's weight and style rows hide for a font a cx.cfg line names", () =>
            WithUi(MineStatic, 400, FontRegistry.StyleNormal, () =>
            {
                TextStyle w = Window("cx-fontcheck-r13");
                bool hidden = !w.Weight.ShowIf!() && !w.Style.ShowIf!();
                w.Typeface.V = DejaVu;
                return hidden && w.Weight.ShowIf!() && w.Style.ShowIf!();
            }));

        Example(check, "R14 a run's own style wins over its block's", () =>
            PanelParser.TryParse("[{\"key\":\"k\",\"blocks\":[{\"text\":\"<n>x</n>y\",\"style\":\"italic\",\"italic\":true}]}]", out PanelDecl[] c, out _)
            && c.Length == 1 && c[0].Blocks[0].Runs is [{ Style: FontRegistry.StyleNormal }, { Style: FontRegistry.StyleItalic }]);

        Example(check, "R15 a style picked on a chat window's gear is the window's", () =>
        {
            ChatWindow w = Chat(DejaVu);
            var gear = new SettingsWindow();
            gear.OpenAt(w, 1280, 800);
            if (gear.Rows.Find(r => r.Text == "Default style") is not { } row) return false;
            row.Selected = Array.IndexOf(row.Choices!, "Italic");
            gear.Panel.RowPressed!(row);
            return w.Style == FontRegistry.StyleItalic && gear.FontChanged && Drawn(ChatService.FaceName(w)) == "DejaVuSans-Oblique.ttf#0@400i";
        });

        Example(check, "R16 a card's <i> run is slanted on an upright face and drawn as it is on an italic one", () =>
        {
            var atlas = new GlyphAtlas { LineHeight = 18, Ascent = 14, SpaceAdvance = 5 };
            atlas.Store('x', new Glyph { W = 4, H = 8, U1 = 1, V1 = 1, Advance = 5, YOff = -8 });
            float[] Verts(string text)
            {
                PanelParser.TryParse($"[{{\"key\":\"k\",\"blocks\":[{{\"text\":\"{text}\"}}]}}]", out PanelDecl[] c, out _);
                return PanelLayout.Build(c[0], 1f, m => (atlas, m), default).Text.AsSpan().ToArray();
            }
            bool asItIs = WithUi(Noto, 400, FontRegistry.StyleItalic, () => Verts("<i>x</i>").SequenceEqual(Verts("x")));
            bool slanted = WithUi(Noto, 400, FontRegistry.StyleNormal, () => !Verts("<i>x</i>").SequenceEqual(Verts("x")));
            return asItIs && slanted;
        });

        Example(check, "R17 a hak's face draws the resource's own bytes, which no file on disk holds", () =>
        {
            FontCatalog was = FontRegistry.UseCatalog(FontCatalog.Empty);
            FontCatalog wasContent = FontRegistry.UseContent(FontCatalog.FromContent(new[] { ("hakdv_b", FontSource.Server, FontRegistryApprovedExamples.Fixture("DejaVuSans-Bold.ttf")) }));
            try
            {
                string key = FontRegistry.FaceKey(DejaVu, 700, FontRegistry.StyleNormal);
                return FontRegistry.Bytes(key) is byte[] b && Sfnt.Read(b, 0)?.FullName == "DejaVu Sans Bold" && FontRegistry.Effective(key) == key;
            }
            finally { FontRegistry.UseContent(wasContent); FontRegistry.UseCatalog(was); }
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
