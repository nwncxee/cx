using CX.Framework;
using CX.Services.Chat;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;
using CX.Services.Panels;

namespace CX.Ui;

internal static partial class FontRegistryApprovedExamples
{
    private const int Normal = FontRegistry.StyleNormal, Italic = FontRegistry.StyleItalic;

    /// <summary>The Default style rows, italic and oblique files, and text that asks for its own style.</summary>
    private static void Styles(Checker check, string dir)
    {
        // E12 Given DejaVu Sans installed with its Oblique and Bold Oblique files, and Noto Sans with its Italic file When
        // the player opens a Typeface list Then DejaVu Sans and Noto Sans are each offered once, with no style in the name
        Example(check, "E12 a family is offered once, with no style in its name", () =>
        {
            string[] n = FontRegistry.Names;
            return n.Count(x => x == DejaVu) == 1 && n.Count(x => x == Noto) == 1 && !n.Any(x => x.Contains("Oblique") || x.Contains("Italic"));
        });

        // E19 Given DejaVu Sans installed with its Condensed files When the player opens a Typeface list Then DejaVu Sans
        // Condensed is offered on its own line, beside DejaVu Sans, since condensed letters are a different width rather
        // than a style
        Example(check, "E19 condensed letters are a family of their own", () =>
            FontRegistry.Names.Count(x => x == DejaVuCondensed) == 1 && FontRegistry.Names.Contains(DejaVu));

        // E20 Given any Typeface row in CX settings When the player opens the settings Then a Style dropdown sits beside
        // it, offering Normal and Italic; the UI's own row is set to Normal, and an element's row offers as UI first and is
        // set to it
        Example(check, "E20 a Default style row beside every Typeface row", () =>
        {
            TextStyle w = Window("cx-fontcheck-e20");
            KnobPanel.Row? gear = Gear(Chat("")).Find(r => r.Text == "Default style");
            return gear?.Choices is string[] c && c.SequenceEqual(new[] { "as UI", "Normal", "Italic" }) && gear.Selected == 0
                && UiKnobs.Style!.Choices.SequenceEqual(new[] { "Normal", "Italic" }) && UiKnobs.Style.Default == Normal
                && UiKnobs.Style.Label == "Default style" && w.Style!.Key == "cx-fontcheck-e20.style" && w.Style.Label == "Default style"
                && w.Style.Choices.SequenceEqual(new[] { "as UI", "Normal", "Italic" }) && w.Style.ChoiceIndex == 0;
        });

        // E21 Given a chat window whose typeface is Noto Sans at weight 700, or DejaVu Sans at weight 700 When the player
        // sets its Style to Italic Then it draws with Noto Sans Bold Italic, or with DejaVu Sans Bold Oblique, the slanted
        // file DejaVu ships in place of an italic
        Example(check, "E21 Italic draws the family's italic or oblique file", () =>
        {
            return Drawn(ChatService.FaceName(Chat(Noto, 700, Italic))) == "NotoSans-Italic-VF.ttf#0@700i"
                && Drawn(ChatService.FaceName(Chat(DejaVu, 700, Italic))) == "DejaVuSans-BoldOblique.ttf#0@700i";
        });

        // E22 Given a chat window whose typeface is a family that ships no italic or oblique file When the player sets its
        // Style to Italic Then it draws in that family's upright letters, at the window's weight
        Example(check, "E22 a family with no italic draws upright", () =>
        {
            return Drawn(ChatService.FaceName(Chat(DejaVuCondensed, 700, Italic))) == "DejaVuSansCondensed-Bold.ttf#0@700";
        });

        // E23 Given the UI typeface is Noto Sans, its weight 700 and its style Italic When the client restarts Then every
        // CX window that follows the UI draws in Noto Sans Bold Italic, and the settings still show Noto Sans, 700 and Italic
        Example(check, "E23 the UI's style survives a restart", () =>
        {
            Settings.Read("cx-fontcheck-e23.typeface=Noto Sans\ncx-fontcheck-e23.weight=700\ncx-fontcheck-e23.style=Italic\n");
            try
            {
                TextStyle w = Window("cx-fontcheck-e23");
                return w.Typeface.V == Noto && w.Weight!.V == 700 && w.Style!.V == Italic && Drawn(w.FaceName) == "NotoSans-Italic-VF.ttf#0@700i"
                    && WithUi(Noto, 700, Italic, () => Drawn(Window("cx-fontcheck-e23b").FaceName) == "NotoSans-Italic-VF.ttf#0@700i");
            }
            finally { foreach (string k in new[] { "typeface", "weight", "style" }) Settings.Forget("cx-fontcheck-e23." + k); }
        });

        // E24 Given a cx.cfg written by an older CX whose ui.typeface is DejaVu Sans Bold Oblique When the client starts
        // Then the UI draws in DejaVu Sans Bold Oblique, and the settings show Typeface DejaVu Sans, Weight 700 and Style Italic
        Example(check, "E24 an older full name migrates to family, weight and style", () =>
        {
            Settings.Read("cx-fontcheck-e24.typeface=DejaVu Sans Bold Oblique\n");
            try
            {
                TextStyle w = Window("cx-fontcheck-e24");
                return FontRegistry.Migrate("DejaVu Sans Bold Oblique") == (DejaVu, 700, Italic)
                    && w.Typeface.V == DejaVu && w.Weight!.V == 700 && w.Style!.V == Italic && Drawn(w.FaceName) == "DejaVuSans-BoldOblique.ttf#0@700i";
            }
            finally { foreach (string k in new[] { "typeface", "weight", "style" }) Settings.Forget("cx-fontcheck-e24." + k); }
        });

        // E28 Given a window whose Default style is Italic, drawing plain text and text that asks to be upright When the
        // window draws Then the plain text is italic and the upright text is upright; E20's Style row is named Default style
        Example(check, "E28 text that asks to be upright stays upright", () =>
            UiKnobs.Style!.Label == "Default style"
            && Drawn(FontRegistry.RunFace(Noto, 400, Italic, 0, 0, false, out _)) == "NotoSans-Italic-VF.ttf#0@400i"
            && Drawn(FontRegistry.RunFace(Noto, 400, Italic, 0, Normal, false, out _)) == "NotoSans-VF.ttf#0@400");

        // E31 Given the UI typeface is Noto Sans with Default style Italic, and a card whose text holds <n>upright</n>, a
        // text block with "style": "normal", and <i>slanted</i> When the card draws Then the <n> run and that block are
        // upright, the rest is Noto Sans Italic, and the <i> run is Noto Sans Italic with no extra slant; at Default style
        // Normal the <i> run is slanted upright letters, as today
        Example(check, "E31 card markup asks for a style", () =>
        {
            bool inline = PanelMarkup.TryParse("rest <n>upright</n> <i>slanted</i>", out Run[] runs, out _);
            bool blocks = PanelParser.TryParse("[{\"key\":\"k\",\"blocks\":[{\"text\":\"plain\",\"style\":\"normal\"}]}]", out PanelDecl[] cards, out _);
            if (!inline || !blocks || runs.Length != 5 || cards.Length != 1) return false;
            Run block = cards[0].Blocks[0].Runs[0], n = runs[1], i = runs[3];
            bool restSlant = true, itSlant = true, todaySlant = false;
            bool italicUi = WithUi(Noto, 400, Italic, () =>
                Drawn(PanelFonts.RunFace(runs[0], out restSlant)) == "NotoSans-Italic-VF.ttf#0@400i"
                && Drawn(PanelFonts.RunFace(n, out _)) == "NotoSans-VF.ttf#0@400" && Drawn(PanelFonts.RunFace(block, out _)) == "NotoSans-VF.ttf#0@400"
                && Drawn(PanelFonts.RunFace(i, out itSlant)) == "NotoSans-Italic-VF.ttf#0@400i");
            bool normalUi = WithUi(Noto, 400, Normal, () => Drawn(PanelFonts.RunFace(i, out todaySlant)) == "NotoSans-VF.ttf#0@400");
            return n.Style == Normal && block.Style == Normal && i.Italic && italicUi && !restSlant && !itSlant && normalUi && todaySlant;
        });

        // E40 Given a chat window in Noto Sans with Default style Italic When its attribution line draws Then the gold run
        // is Noto Sans Italic with no extra slant; at Default style Normal it is slanted upright letters, as today
        Example(check, "E40 the credit line's slant follows the face", () =>
            WithUi(Noto, 400, Italic, () => Drawn(ChatService.FaceName(Chat(""))) == "NotoSans-Italic-VF.ttf#0@400i" && !ChatRenderer.AttributionSlant())
            && WithUi(Noto, 400, Normal, () => Drawn(ChatService.FaceName(Chat(""))) == "NotoSans-VF.ttf#0@400" && ChatRenderer.AttributionSlant()));

        // E41 Given the UI in Noto Sans with Default style Italic, and a card text block that asks to be italic When the
        // card draws Then the block is Noto Sans Italic with no extra slant; at Default style Normal it is slanted upright
        // letters, as today
        Example(check, "E41 a card block that asks to be italic", () =>
        {
            if (!PanelParser.TryParse("[{\"key\":\"k\",\"blocks\":[{\"text\":\"aside\",\"italic\":true}]}]", out PanelDecl[] cards, out _) || cards.Length != 1)
                return false;
            Run r = cards[0].Blocks[0].Runs[0];
            bool onItalic = true, onNormal = false;
            return r.Italic
                && WithUi(Noto, 400, Italic, () => Drawn(PanelFonts.RunFace(r, out onItalic)) == "NotoSans-Italic-VF.ttf#0@400i") && !onItalic
                && WithUi(Noto, 400, Normal, () => Drawn(PanelFonts.RunFace(r, out onNormal)) == "NotoSans-VF.ttf#0@400") && onNormal;
        });

        // E42 Given an older cx.cfg whose chat window 2 typeface is DejaVu Sans Oblique When the client starts Then window
        // 2 draws in DejaVu Sans Oblique and shows Typeface DejaVu Sans, Default weight 400 and Default style Italic
        Example(check, "E42 an older chat window typeface migrates", () =>
        {
            ChatConfig c = ChatConfig.From(new[] { new KeyValuePair<string, string>("w2.typeface", "DejaVu Sans Oblique") });
            if (c.Windows.Count < 3) return false;
            WindowConfig wc = c.Windows[2];
            ChatWindow w = Chat("");
            wc.ApplyTo(w);
            List<KnobPanel.Row> gear = Gear(w);
            return wc.Typeface == DejaVu && wc.Weight == 400 && wc.Style == Italic && Drawn(ChatService.FaceName(w)) == "DejaVuSans-Oblique.ttf#0@400i"
                && gear.Find(r => r.Text == "Typeface") is { } t && t.Choices![t.Selected] == DejaVu
                && gear.Find(r => r.Text == "Default weight") is { } dw && dw.Choices![dw.Selected] == "400"
                && gear.Find(r => r.Text == "Default style") is { } ds && ds.Choices![ds.Selected] == "Italic";
        });
    }
}
