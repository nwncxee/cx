using System;
using System.Text;
using CX.Framework;
using CX.Services.Nui;
using CX.Ui;
using CX.Services.Panels;

namespace CX.Services.Cards;

/// <summary>
/// Parsing, layout, placement, the registry and the show and hide rules of the hover card. The oracle is arithmetic done by
/// hand on the numbers written in each check (a test face is 20 px tall with 10 px letters, so "aaaa" is 40 px, a 200 px card
/// has 180 px of text and 10 px of padding, a 20 px line plus its 2 px gap and the padding make a 42 px card), the declarations
/// and keys typed into the checks, and Tooltip's own dwell and fade times set to 0.5 s and 0.2 s on a fake clock.
/// </summary>
internal static class CardSelfCheck
{
    private const string Everything = """
        [{"width":240,"bg":{"from":"#102030","to":"#304050","dir":"v"},"border":"#aabbcc","tab":"Equipped","tabcolor":"#112233","item":"1f",
          "images":[{"src":"gui_x","w":40,"h":40,"at":"tr","dx":20,"dy":-20}],
          "blocks":[
            {"type":"text","text":"Sword","color":"#3399ff","size":1.3},
            {"type":"text","text":"Rare","size":0.85,"dim":true},
            {"type":"status","icon":"gui_s","text":"Equipped"},
            {"type":"header","text":"2~12 Damage"},
            {"type":"divider"},
            {"type":"section","tint":"#ffffff10","blocks":[{"type":"text","text":"in <c=#ff0000>red</c>"}]},
            {"type":"icons","icons":["a","b","c"],"frame":true},
            {"type":"text","text":"old <i>words</i>","italic":true},
            {"type":"footer","left":"tags","right":"2.7 <icon=w> 211"},
            {"type":"nonsense","text":"degrades"},
            {"type":"nonsense2"}
          ]}]
        """;

    private static GlyphAtlas TestAtlas()
    {
        var a = new GlyphAtlas { LineHeight = 20, Ascent = 15, SpaceAdvance = 4 };
        a.Store(' ', new Glyph { Advance = 4 });
        for (char c = 'a'; c <= 'z'; c++) a.Store(c, new Glyph { Advance = 10, W = 8, H = 12, XOff = 1, YOff = -12, U1 = 1, V1 = 1 });
        return a;
    }

    private static bool Near(float v, float want) => Math.Abs(v - want) < 0.01f;

    private static PanelView Lay(GlyphAtlas atlas, string cardJson, PanelSubject subject = default, float scale = 1f)
    {
        PanelParser.TryParse(cardJson, out PanelDecl[] cards, out _);
        return PanelLayout.Build(cards[0], scale, m => (atlas, m), subject);
    }

    private static PanelStyle StyleOf(string json)
    {
        PanelParser.TryParseStyle(json, out PanelStyle st, out _);
        return st;
    }

    private static PanelView LayS(GlyphAtlas atlas, string styleJson, string cardJson)
    {
        PanelParser.TryParse(cardJson, out PanelDecl[] cards, out _, StyleOf(styleJson));
        return PanelLayout.Build(cards[0], 1f, m => (atlas, m), default);
    }

    private static NuiHit Hit(string token, int row = -1, string image = "", float x = 50f, float y = 40f, float w = 30f, float h = 30f)
    {
        var hit = new NuiHit { Element = (IntPtr)1, Row = row, X = x, Y = y, W = w, H = h };
        hit.TokenLen = Encoding.UTF8.GetBytes(token, hit.TokenBuffer());
        hit.ImageLen = Encoding.UTF8.GetBytes(image, hit.ImageBuffer());
        return hit;
    }

    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "CardSelfCheck");
        Markup(check);
        Parse(check);
        GlyphAtlas atlas = TestAtlas();
        Layout(check, atlas);
        Placement(check);
        Styled(check, atlas);
        StyleRegistry(check, log);
        Roles(check, log);
        Registry(check, log);
        Presenting(check, atlas, log);
        log.Info(check.Ok ? "CardSelfCheck: passed" : "CardSelfCheck: FAILED");
    }

    private static void Markup(Checker check)
    {
        check.Expect(PanelMarkup.TryParse("a<c=#ff0000>b</c> <i>c</i><br>d <<e <z>", out Run[] r, out _) && r.Length == 6
            && r[0].Text == "a" && r[0].Rgba == 0 && r[1].Text == "b" && r[1].Rgba == 0xFF0000FF && r[2].Text == " " && r[2].Rgba == 0
            && r[3].Text == "c" && r[3].Italic && r[4].Break && r[5].Text == "d <e <z>",
            "colour and italic open and close, <br> breaks, << is a literal <, an unknown tag stays as typed");
        check.Expect(PanelMarkup.TryParse("<s=1.5>x</s><s=9>y</s>", out r, out _) && r[0].Size == 1.6f && r[1].Size == 2f, "a size snaps to the nearest ladder step");
        check.Expect(PanelMarkup.TryParse("<c=red>x", out r, out _) && r.Length == 1 && r[0].Text == "<c=red>x", "a bad colour value stays as typed");
        check.Expect(PanelMarkup.TryParse("a\nb<icon=gui_x>", out r, out _) && r.Length == 4 && r[1].Break && r[3].Icon == "gui_x", "a newline breaks the line and <icon=> is an inline icon");
        check.Expect(!PanelMarkup.TryParse(new string('a', 401), out _, out _), "401 characters is over the text limit");
        check.Expect(!PanelMarkup.TryParse(string.Concat(System.Linq.Enumerable.Repeat("a<i>", 49)), out _, out _), "49 runs is over the run limit");
    }

    private static void Parse(Checker check)
    {
        check.Expect(PanelParser.TryParse(Everything, out PanelDecl[] cards, out string why) && cards.Length == 1, "a card using every block parses: " + why);
        if (cards.Length != 1) return;
        PanelDecl c = cards[0];
        BlockKind[] kinds = [BlockKind.Text, BlockKind.Text, BlockKind.Status, BlockKind.Text, BlockKind.Divider, BlockKind.Section,
            BlockKind.Icons, BlockKind.Text, BlockKind.Footer, BlockKind.Text, BlockKind.Text];
        bool order = c.Blocks.Length == kinds.Length;
        for (int i = 0; order && i < kinds.Length; i++) order = c.Blocks[i].Kind == kinds[i];
        check.Expect(order, "the blocks come back in order, an unknown type as text");
        check.Expect(c.St.BgFrom == 0x102030FF && c.St.BgTo is { } far && far == 0x304050FF && c.St.Dir == BgDir.Vertical && c.St.Border == 0xAABBCCFF && c.Tab == "Equipped" && c.St.Width == 240f && c.Item == "1f",
            "card fields: gradient, border, tab, width, item");
        check.Expect(c.Blocks[0].Color == 0x3399FFFF && c.Blocks[0].Size == 1.3f && c.Blocks[1].Size == 0.85f && c.Blocks[1].Color == 0xFFFFFF99, "a declared colour and size, a dim line");
        check.Expect(c.Blocks[7].Runs[0].Italic && c.Blocks[7].Runs[1].Italic, "italic on the block makes every run italic");
        check.Expect(c.Blocks[3].Size == 1f && c.Blocks[3].Color == 0 && !c.Blocks[3].Runs[0].Italic && !c.Blocks[3].Pulse && !c.Blocks[2].Frame && c.Blocks[8].Size == 1f,
            "an old preset name and every undeclared field draw plain: size 1, the default colour, upright, no frame, no pulse");
        check.Expect(c.Blocks[5].Kids.Length == 1 && c.Blocks[5].Tint == 0xFFFFFF10 && c.Blocks[6].Icons.Length == 3 && c.Blocks[6].Frame, "section tint and kids, a framed icon row");
        check.Expect(c.Blocks[8].Runs.Length == 1 && c.Blocks[8].Right.Length == 3 && c.Blocks[8].Right[1].Icon == "w", "footer left and right, an inline icon on the right");
        check.Expect(c.Images.Length == 1 && c.Images[0].At == Anchor.TopRight && c.Images[0].Dx == 20f && c.Images[0].Dy == -20f, "an image anchored top right, pushed out");
        check.Expect(PanelParser.TryParse("{\"blocks\":[],\"future\":{\"a\":1}}", out cards, out _) && cards.Length == 1, "an unknown field is ignored");
        check.Expect(PanelParser.TryParse("{\"border\":\"#aabbccdd\",\"blocks\":[]}", out _, out _) && PanelParser.TryParse("{\"border\":\"#aabbcc\",\"blocks\":[]}", out _, out _), "a card colour is #RRGGBB or #RRGGBBAA");
        foreach (string wrong in new[] { "#12345", "#GGGGGG", "", "#1234567890", "123456" })
            check.Expect(!PanelParser.TryParse("{\"border\":\"" + wrong + "\",\"blocks\":[]}", out _, out _), $"a card colour of '{wrong}' refuses the card");
        check.Expect(!PanelParser.TryParseStyle("{\"glow\":\"#12345\"}", out _, out _) && PanelParser.TryParseStyle("{\"glow\":\"#12345678\"}", out _, out _), "a style colour is checked the same way");
        string[] bad =
        [
            "not json", "5", "{\"width\":\"wide\"}", "{\"border\":\"red\"}", "{\"blocks\":{}}", "{\"blocks\":[{\"type\":\"icons\"}]}",
            "[{},{},{},{},{}]", "{\"images\":[{\"w\":4}]}",
            "{\"blocks\":[{\"type\":\"section\",\"blocks\":[{\"type\":\"section\",\"blocks\":[{\"type\":\"section\"}]}]}]}",
            "{\"blocks\":[{\"text\":\"" + new string('x', 401) + "\"}]}",
            "{\"blocks\":[" + string.Join(",", System.Linq.Enumerable.Repeat("{\"type\":\"divider\"}", 49)) + "]}",
            "{\"tab\":\"" + new string('x', 13_000) + "\"}",
        ];
        bool allRefused = true;
        foreach (string b in bad) if (PanelParser.TryParse(b, out _, out string e) || e.Length == 0) { allRefused = false; check.Expect(false, "refused: " + b[..Math.Min(b.Length, 40)]); }
        check.Expect(allRefused, "each malformed declaration is refused with a reason");
    }

    private static void Layout(Checker check, GlyphAtlas atlas)
    {
        PanelView v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"aaaa bbbb cccc\"}]}");
        check.Expect(Near(v.W, 200f) && Near(v.H, 42f), $"one 20 px line: 10 pad + 20 + 2 gap + 10 pad = 42 ({v.H})");
        v = Lay(atlas, "{\"width\":160,\"blocks\":[{\"text\":\"aaaa bbbb cccc dddd\"}]}");
        check.Expect(Near(v.H, 62f), $"140 px of text holds three words (128) and wraps the fourth: two lines, 10 + 40 + 2 + 10 = 62 ({v.H})");
        v = Lay(atlas, "{\"width\":160,\"blocks\":[{\"text\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}");
        check.Expect(Near(v.H, 42f), "one word wider than the card stays on one line and runs past the edge");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"ab\",\"size\":1.6}]}");
        check.Expect(Near(v.H, 10f + 32f + 2f + 10f), $"a line at size 1.6 is 20 * 1.6 = 32 px tall ({v.H})");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"header\",\"text\":\"ab\"}]}");
        check.Expect(Near(v.H, 10f + 20f + 2f + 10f), $"an old preset name lays out as plain text, 20 px and its gap ({v.H})");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"ab<br>cd\"}]}");
        check.Expect(Near(v.H, 10f + 40f + 2f + 10f), "<br> starts a second line");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"status\",\"icon\":\"gui_s\",\"text\":\"ab\"}]}");
        check.Expect(Near(v.H, 10f + 22f + 4f + 10f) && v.Icons.Count == 1 && v.Icons[0].Src == "gui_s" && Near(v.Icons[0].X, 10f) && Near(v.Icons[0].W, 22f),
            "a status row is as tall as its 22 px icon, the icon sits at the left padding");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"icons\",\"frame\":true,\"icons\":[\"a\",\"b\",\"c\"]}]}");
        check.Expect(Near(v.H, 10f + 28f + 4f + 10f) && v.Icons.Count == 3 && Near(v.Icons[1].X, 10f + 2f + 32f), "three framed 28 px icons share a row, 32 px apart");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"icons\",\"frame\":true,\"icons\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\"]}]}");
        check.Expect(Near(v.H, 10f + 60f + 4f + 10f) && Near(v.Icons[5].Y, 10f + 32f + 2f), "seven icons wrap after the fifth: 180 px holds 5 of 32");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"divider\"}]}");
        check.Expect(Near(v.H, 27f), "a divider is 7 px");
        int plain = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"ab\"}]}").Back.VertexCount;
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"section\",\"tint\":\"#ffffff10\",\"blocks\":[{\"text\":\"ab\"}]}]}");
        check.Expect(Near(v.H, 54f) && v.Back.VertexCount == plain + 6, $"a section pads its blocks 6 px and adds one tint rectangle (54, {v.H}; {plain} + 6, {v.Back.VertexCount})");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"footer\",\"size\":1,\"left\":\"ab\",\"right\":\"cd\"}]}");
        check.Expect(Near(v.H, 50f), "a footer is its tallest side plus 5 px above and below");
        float[] t = v.Text.ToArray();
        check.Expect(v.Text.VertexCount == 24 && Near(t[0], 11f) && Near(t[2 * 6 * VertexBatch.Floats], 171f), "the left side starts at the padding, the right side ends flush with the text edge");
        v = Lay(atlas, "{\"width\":200,\"bg\":{\"from\":\"#102030\",\"to\":\"#304050\"},\"blocks\":[]}");
        float[] b = v.Back.ToArray();
        check.Expect(Near(b[4], 0x10 / 255f) && Near(b[5], 0x20 / 255f) && Near(b[6], 0x30 / 255f) && Near(b[2 * VertexBatch.Floats + 4], 0x30 / 255f) && Near(b[5 * VertexBatch.Floats + 4], 0x30 / 255f),
            "a vertical gradient is the from colour on top and the to colour at the bottom");
        v = Lay(atlas, "{\"width\":200,\"bg\":{\"from\":\"#102030\",\"to\":\"#304050\",\"dir\":\"h\"},\"blocks\":[]}");
        b = v.Back.ToArray();
        check.Expect(Near(b[1 * VertexBatch.Floats + 4], 0x30 / 255f) && Near(b[5 * VertexBatch.Floats + 4], 0x10 / 255f), "a horizontal one runs from the left edge to the right");
        v = Lay(atlas, "{\"width\":200,\"tab\":\"ab\",\"blocks\":[{\"text\":\"ab\"}]}");
        float[] tt = v.Text.ToArray();
        check.Expect(Near(v.TabH, 24f) && Near(tt[1], -24f + 2f + 15f - 12f), "a tab is 24 px above the card and its text sits inside it");
        v = Lay(atlas, "{\"width\":200,\"images\":[{\"src\":\"gui_x\",\"w\":40,\"h\":40,\"at\":\"tr\",\"dx\":20,\"dy\":-20}],\"blocks\":[]}");
        check.Expect(v.Icons.Count == 1 && Near(v.Icons[0].X, 180f) && Near(v.Icons[0].Y, -20f), "an image at the top right pushed 20 right and 20 up overflows the corner");
        v = Lay(atlas, "{\"width\":200,\"images\":[{\"src\":\"gui_x\",\"w\":40,\"h\":40,\"at\":\"bl\"}],\"blocks\":[{\"text\":\"ab\"}]}");
        check.Expect(Near(v.Icons[0].X, 0f) && Near(v.Icons[0].Y, v.H - 40f), "bottom left sits flush in the corner");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"a <icon=gui_w> b\"}]}");
        check.Expect(v.Icons.Count == 1 && Near(v.Icons[0].W, 20f) && Near(v.Icons[0].H, 20f), "an inline icon is as tall as its line");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"{item.name}\"}]}", new PanelSubject("torch", "3"));
        check.Expect(v.Text.VertexCount == 30, "{item.name} is the item's name: five letters, five quads");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"{item.name}\"}]}");
        check.Expect(v.Text.VertexCount == 0, "with no item it is empty");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"icons\",\"icons\":[\"@item:1f\",\"@item:zz\",\"@look:0001\",\"gui_x\",\"@item:2.7\"]}]}");
        check.Expect(v.Icons.Count == 4 && v.Icons[0].Item == 0x1F && v.Icons[1].Look == "@look:0001" && v.Icons[1].Src == "" && v.Icons[2].Src == "gui_x" && v.Icons[3].Item == 0x2,
            "@item: takes its hex digits, a bad one draws nothing, a @look: keeps its name for the minted item, a resref draws as itself");
        v = Lay(atlas, "{\"width\":200,\"images\":[{\"src\":\"@look:0002\",\"w\":40,\"h\":40}],\"blocks\":[]}");
        check.Expect(v.Icons.Count == 1 && v.Icons[0].Look == "@look:0002" && Near(v.Icons[0].W, 40f), "an @look: image is placed like any other");
        v = Lay(atlas, "{\"width\":200,\"images\":[{\"src\":\"@model:hound\",\"w\":64,\"h\":96},{\"src\":\"@model:\",\"w\":8,\"h\":8}],\"blocks\":[{\"type\":\"icons\",\"icons\":[\"@model:ogre\"]}]}");
        check.Expect(v.Icons.Count == 2 && v.Icons[0].Model == "hound" && v.Icons[0].Src == "" && Near(v.Icons[0].H, 96f) && v.Icons[1].Model == "ogre",
            "@model: names a model view by its key in an image and in an icons row, and a bare prefix draws nothing");
        Looks(check);
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"ab\"}]}", scale: 2f);
        check.Expect(Near(v.W, 400f) && Near(v.H, 20f + 20f + 4f + 20f), "at UI scale 2 the padding and gaps double; the test face's own height does not");
    }

    /// <summary>
    /// Oracle: the pre-change numbers written as literals (pad 10, gap 4, text gap 2, divider 7, footer 62/38 and 5, tab 8/6/4/2, width 300 in 160-480),
    /// and heights, positions and vertex counts counted by hand on the test face (20 px line, 10 px letter, 4 px space).
    /// </summary>
    private static void Styled(Checker check, GlyphAtlas atlas)
    {
        PanelStyle d = StyleOf("{}");
        check.Expect(d.Pad == 10f && d.Gap == 4f && d.TextGap == 2f && d.StatusGap == 6f && d.IconGap == 4f && d.FrameInset == 2f && d.DividerMargin == 3f && d.SectionPad == 6f
            && d.FooterPad == 5f && d.FooterSplit == 0.62f && d.TabX == 8f && d.TabPad == 6f && d.TabExtra == 4f && d.TabDrop == 2f && d.StatusPx == 22f && d.IconsPx == 28f && d.ImageSize == 64f,
            "an empty style is every spacing the cards had before");
        check.Expect(d.Text.Role == Role.Tx && d.Text.Alpha == 0xFF && d.BgFrom.Role == Role.PanelBg && d.BgFrom.Alpha == 0xF2 && d.Border.Role == Role.Border
            && d.FrameLine.Role == Role.Grip && d.FrameLine.Alpha == 0xC0 && d.TabBg.Role == Role.MasterBg && d.TabBg.Alpha == 0xB0,
            "a colour nobody set follows the player's look: text, background, border, icon frame and tab by role");
        check.Expect(!d.Divider.IsRole && d.Divider == 0xFFFFFF30 && d.FooterBand == 0x00000066 && d.FrameFill == 0x00000066 && !d.TabText.Set && !d.Shadow.Set && !d.Glow.Set
            && d.DimAlpha == 0x99 && d.PulseMin == 0.35f && d.PulsePeriodMs == 1200f && d.Width == 300f && d.MinWidth == 160f && d.MaxWidth == 480f,
            "the washes stay as written, the optional colours unset, and the pulse and width range hold");
        check.Expect(d.Side == Side.Right && d.Flip && d.Offset == 8f && d.CardGap == 8f && d.Delay < 0f && d.FadeIn < 0f && d.Sizes.Length == 7 && d.Sizes[0] == 0.7f && d.Sizes[6] == 2f, "the placement, the player's own delay and the seven sizes");

        check.Expect(Near(LayS(atlas, "{\"pad\":4,\"textgap\":6}", "{\"width\":200,\"blocks\":[{\"text\":\"ab\"}]}").H, 4f + 20f + 6f + 4f), "pad 4 and text gap 6: 4 + 20 + 6 + 4 = 34");
        check.Expect(Near(LayS(atlas, "{}", "{\"pad\":3,\"blocks\":[{\"text\":\"ab\",\"after\":9}]}").H, 3f + 20f + 9f + 3f), "a card's own pad and a block's own gap beat the style");
        check.Expect(Near(LayS(atlas, "{\"pad\":4}", "{\"pad\":6,\"blocks\":[{\"text\":\"ab\"}]}").H, 6f + 20f + 2f + 6f) && Near(LayS(atlas, "{\"pad\":4}", "{\"blocks\":[{\"text\":\"ab\"}]}").H, 4f + 20f + 2f + 4f),
            "a card field beats the style, and a card without it takes the style's");
        check.Expect(Near(LayS(atlas, "{}", "{\"blocks\":[{\"type\":\"divider\",\"margin\":5,\"thick\":3}]}").H, 10f + 5f + 3f + 5f + 10f), "a divider's margin and thickness: 10 + 13 + 10 = 33");
        check.Expect(Near(LayS(atlas, "{}", "{\"width\":200,\"blocks\":[{\"type\":\"section\",\"pad\":2,\"blocks\":[{\"text\":\"ab\"}]}]}").H, 10f + 2f + 22f + 2f + 10f), "a section's own pad: 10 + 26 + 10 = 46");
        const string foot = "{\"width\":200,\"blocks\":[{\"type\":\"footer\",\"left\":\"a\",\"right\":\"aaaa bbbb\"}]}";
        check.Expect(Near(LayS(atlas, "{}", foot).H, 10f + 40f + 10f + 10f) && Near(LayS(atlas, "{\"footersplit\":0.3}", foot).H, 10f + 20f + 10f + 10f),
            "the footer's right text wraps in 38% of 180 px (two lines) and fits in 70% (one); each pads 5 px above and below");
        check.Expect(Near(LayS(atlas, "{\"leading\":1.5}", "{\"width\":200,\"blocks\":[{\"text\":\"ab\"}]}").H, 10f + 30f + 2f + 10f), "a line spacing of 1.5 makes the 20 px line 30");
        check.Expect(Near(LayS(atlas, "{\"tabextra\":10}", "{\"tab\":\"t\",\"blocks\":[]}").TabH, 30f), "the tab's extra height: 20 + 10");

        PanelView v = LayS(atlas, "{}", "{\"width\":200,\"blocks\":[]}");
        check.Expect(v.Back.VertexCount == 6 + 24, "the default frame is four rectangles");
        v = LayS(atlas, "{\"borderw\":3}", "{\"width\":200,\"blocks\":[]}");
        float[] b = v.Back.ToArray();
        float topEdge = 0f;
        for (int i = 6; i < 12; i++) topEdge = Math.Max(topEdge, b[i * VertexBatch.Floats + 1]);
        check.Expect(Near(topEdge, 3f), $"a 3 px border: the top edge is 3 px deep ({topEdge})");
        check.Expect(LayS(atlas, "{\"sides\":\"l\"}", "{\"width\":200,\"blocks\":[]}").Back.VertexCount == 6 + 6, "sides l draws one edge");
        check.Expect(LayS(atlas, "{\"border2\":\"#ffffff\"}", "{\"width\":200,\"blocks\":[]}").Back.VertexCount == 6 + 24 + 24, "an inner border adds four rectangles");
        check.Expect(LayS(atlas, "{\"corners\":\"#ffffff\"}", "{\"width\":200,\"blocks\":[]}").Back.VertexCount == 6 + 24 + 48, "corner brackets add eight");
        check.Expect(LayS(atlas, "{\"glow\":\"#00ffff80\",\"glowspread\":2}", "{\"width\":200,\"blocks\":[]}").Back.VertexCount == 6 + 24 + 48, "a glow two steps wide adds two frames of four rectangles");
        float GlowEdge(string extra) => LayS(atlas, "{\"glow\":\"#00ffff80\",\"glowspread\":2" + extra + "}", "{\"width\":200,\"blocks\":[]}").Back.ToArray()[VertexBatch.Floats - 1];
        check.Expect(Near(GlowEdge(""), 64f / 255f) && Near(GlowEdge(",\"glowalpha\":1"), 128f / 255f) && Near(GlowEdge(",\"glowalpha\":0"), 0f) && Near(GlowEdge(",\"glowalpha\":7"), 128f / 255f),
            "the nearest glow ring is half the colour's opacity by default, all of it at glowalpha 1, none at 0, and the field is capped at 1");

        v = LayS(atlas, "{\"bg\":{\"from\":\"#102030\",\"to\":\"#304050\",\"dir\":\"d\"}}", "{\"width\":200,\"blocks\":[]}");
        b = v.Back.ToArray();
        check.Expect(Near(b[4], 0x10 / 255f) && Near(b[1 * VertexBatch.Floats + 4], 0x20 / 255f) && Near(b[2 * VertexBatch.Floats + 4], 0x30 / 255f) && Near(b[5 * VertexBatch.Floats + 4], 0x20 / 255f),
            "a diagonal gradient runs from the top left to the bottom right, the other corners half way");
        v = LayS(atlas, "{\"bg\":\"#ff0000\"}", "{\"width\":200,\"bg\":{\"from\":\"#00ff00\",\"to\":\"#00000000\"},\"blocks\":[]}");
        b = v.Back.ToArray();
        check.Expect(Near(b[5], 1f) && Near(b[2 * VertexBatch.Floats + 7], 0f), "a gradient may fade to a clear colour");

        v = LayS(atlas, "{\"color\":\"#ff0000\",\"shadow\":\"#000000\"}", "{\"width\":200,\"blocks\":[{\"text\":\"ab\"}]}");
        check.Expect(v.Text.VertexCount == 24, "a shadow draws every glyph twice");
        float[] t = v.Text.ToArray();
        check.Expect(Near(t[4], 0f) && Near(t[12 * VertexBatch.Floats + 4], 1f) && Near(t[12 * VertexBatch.Floats + 5], 0f), "the shadow first, then the text in the style's colour");
        v = LayS(atlas, "{}", "{\"width\":200,\"blocks\":[{\"text\":\"ab\",\"align\":\"center\"}]}");
        check.Expect(Near(v.Text.ToArray()[0], 10f + (180f - 20f) / 2f + 1f), "centred text starts half the spare width in");

        check.Expect(LayS(atlas, "{\"pulsemin\":0.5,\"pulseperiod\":400}", "{\"blocks\":[]}") is { PulseMin: 0.5f, PulsePeriodMs: 400f }, "the pulse's floor and period reach the view");
        check.Expect(Near(PanelPulse.Alpha(0, 0.5f, 400f), 0.75f) && Near(PanelPulse.Alpha(100, 0.5f, 400f), 1f) && Near(PanelPulse.Alpha(300, 0.5f, 400f), 0.5f) && Near(PanelPulse.Alpha(400, 0.5f, 400f), 0.75f),
            "a 0.5 floor on a 400 ms period: half way up at 0, full at a quarter, the floor at three quarters");
        check.Expect(StyleOf("{\"pulsemin\":3,\"pulseperiod\":5}") is { PulseMin: 1f, PulsePeriodMs: 100f }, "the floor clamps to 0-1 and the period to 100-60000 ms");

        check.Expect(StyleOf("{\"minwidth\":100,\"maxwidth\":900,\"width\":700}").Width == 700f && StyleOf("{\"width\":9999}").Width == 480f && StyleOf("{\"minwidth\":1}").MinWidth == 32f,
            "the width range is the style's, inside 32-2000");
        PanelParser.TryParse("{\"width\":2000}", out PanelDecl[] wide, out _, StyleOf("{\"maxwidth\":900}"));
        PanelParser.TryParse("{\"width\":50}", out PanelDecl[] narrow, out _);
        check.Expect(wide[0].St.Width == 900f && narrow[0].St.Width == 160f, "a card's width clamps to its style's range");
        PanelStyle ladder = StyleOf("{\"sizes\":[2.5,1,0.5]}");
        check.Expect(ladder.Sizes.Length == 3 && ladder.Sizes[0] == 0.5f && ladder.Sizes[2] == 2.5f, "a size ladder is sorted");
        PanelParser.TryParse("{\"blocks\":[{\"text\":\"a<s=0.6>b</s>\",\"size\":2}]}", out wide, out _, ladder);
        check.Expect(wide[0].Blocks[0].Size == 2.5f && wide[0].Blocks[0].Runs[1].Size == 0.5f, "block sizes and <s=> snap to the style's ladder");
        check.Expect(!PanelParser.TryParseStyle("{\"sizes\":[]}", out _, out _) && !PanelParser.TryParseStyle("{\"sizes\":[1,1,1,1,1,1,1,1,1,1,1,1,1]}", out _, out _) && !PanelParser.TryParseStyle("[1]", out _, out _)
            && !PanelParser.TryParseStyle("{\"pad\":\"big\"}", out _, out _) && !PanelParser.TryParseStyle("{\"side\":\"up\"}", out _, out _) && !PanelParser.TryParseStyle("{\"sides\":\"x\"}", out _, out _),
            "an empty or long ladder, a non-object, a wrong type and a bad word each refuse the style");

        PanelParser.TryParse("{\"blocks\":[{\"text\":\"a\",\"dim\":true}]}", out wide, out _, StyleOf("{\"dimcolor\":\"#112233\",\"dimalpha\":0.2}"));
        check.Expect(wide[0].Blocks[0].Color == 0x11223333, "a dim line takes the style's colour and opacity");
        v = LayS(atlas, "{\"imagesize\":48}", "{\"width\":200,\"images\":[{\"src\":\"gui_x\"},{\"src\":\"gui_y\",\"at\":\"c\",\"w\":40,\"h\":20,\"alpha\":0.5}],\"blocks\":[]}");
        check.Expect(Near(v.Icons[0].W, 48f) && v.Icons[0].Alpha == 1f && Near(v.Icons[1].X, 80f) && Near(v.Icons[1].Y, 0f) && v.Icons[1].Alpha == 0.5f, "an image takes the style's size, a centre anchor and its own opacity");
        v = LayS(atlas, "{}", "{\"width\":200,\"images\":[{\"src\":\"gui_x\",\"at\":\"t\",\"w\":40,\"h\":10},{\"src\":\"gui_y\",\"at\":\"r\",\"w\":10,\"h\":4},{\"src\":\"gui_z\",\"at\":\"l\",\"w\":10,\"h\":4}],\"blocks\":[]}");
        check.Expect(Near(v.Icons[0].X, 80f) && Near(v.Icons[0].Y, 0f) && Near(v.Icons[1].X, 190f) && Near(v.Icons[1].Y, 8f) && Near(v.Icons[2].X, 0f) && Near(v.Icons[2].Y, 8f), "edge anchors centre along the other axis");
        check.Expect(!PanelParser.TryParse("{\"images\":[{\"src\":\"a\",\"at\":\"x\"}]}", out _, out _), "an unknown anchor is refused");
        check.Expect(!PanelParser.TryParse("{\"images\":[1]}", out _, out string why) && why == "an image is a JSON object"
                     && !PanelParser.TryParse("{\"images\":[null]}", out _, out _), "an image that is a number or null is refused, not thrown");
        check.Expect(!PanelParser.TryParse("{\"blocks\":[{\"text\":\"\\ud800\"}]}", out _, out why) && why.Length > 0, "a lone surrogate in a text is refused, not thrown");
    }

    /// <summary>
    /// Oracle: the role names, alpha bytes and hex colours written into each check by hand, and the palette <see cref="Theme"/>
    /// holds for a role, which no part of the parser computes.
    /// </summary>
    private static void Roles(Checker check, ExtenderLog log)
    {
        Paint border = StyleOf("{\"border\":\"@accent\"}").Border;
        check.Expect(border.IsRole && border.Role == Role.Accent && border.Alpha == 0xFF, "@accent is kept as the accent role, opaque");
        Paint half = StyleOf("{\"border\":\"@accent/80\"}").Border;
        check.Expect(half.IsRole && half.Role == Role.Accent && half.Alpha == 0x80, "@accent/80 is the same role at half opacity");
        check.Expect((uint)half == (Theme.Rgb(Role.Accent) << 8 | 0x80u) && (uint)border == Theme.Rgba(Role.Accent), "a role reads as the look's colour at the written opacity");
        Paint flat = StyleOf("{\"border\":\"#112233\"}").Border;
        check.Expect(!flat.IsRole && flat == 0x112233FFu && StyleOf("{\"border\":\"#11223344\"}").Border == 0x11223344u, "a colour written as hex is still exactly that colour");
        check.Expect(PanelParser.TryParse("{\"blocks\":[{\"text\":\"a\",\"color\":\"@tx2\"}]}", out PanelDecl[] cards, out _) && cards[0].Blocks[0].Color.Role == Role.Tx2,
            "a block colour takes a role too");
        check.Expect(PanelParser.TryParse("{\"blocks\":[{\"text\":\"a\",\"color\":\"@tx2\",\"dim\":true}]}", out cards, out _) && cards[0].Blocks[0].Color.Role == Role.Tx2 && cards[0].Blocks[0].Color.Alpha == 0x99,
            "a dim block keeps its role and takes the dim opacity");
        check.Expect(PanelMarkup.TryParse("<c=@accent/80>x", out Run[] r, out _) && r.Length == 1 && r[0].Rgba.Role == Role.Accent && r[0].Rgba.Alpha == 0x80, "<c=@accent/80> colours a run by role");
        check.Expect(PanelMarkup.TryParse("<c=@bogus>x", out r, out _) && r.Length == 1 && r[0].Text == "<c=@bogus>x", "an unknown role in markup stays as typed");
        foreach (string wrong in new[] { "@bogus", "@", "@accent/", "@accent/8", "@accent/8g", "@accent/800", "accent", "@7" })
            check.Expect(!PanelParser.TryParseStyle("{\"border\":\"" + wrong + "\"}", out _, out string why) && why.Contains("@role", StringComparison.Ordinal), $"'{wrong}' refuses the style like any bad colour");

        CardRegistry.Reset();
        CardRegistry.Init(log);
        PolicyRefusals.ForgetPrefix("");
        ServerPolicy.Apply("cardstyle={\"border\":\"@bogus\"}");
        PolicyRefusal[] refused = PolicyRefusals.All();
        check.Expect(refused.Length == 1 && refused[0].Kind == RefusalKind.Style && refused[0].Key == "cardstyle" && refused[0].Reason.Contains("@role", StringComparison.Ordinal),
            "an unknown role is logged as a style refusal, with the colour forms in its reason");
        CardRegistry.Reset();
        ServerPolicy.Clear();
        PolicyRefusals.ForgetPrefix("");
    }

    /// <summary>Oracle: lines typed into the checks; a card sent before the style, a style sent first, and a bad style, with the paddings read back.</summary>
    private static void StyleRegistry(Checker check, ExtenderLog log)
    {
        CardRegistry.Reset();
        CardRegistry.Init(log);
        ServerPolicy.Apply("card.x={\"blocks\":[]}");
        check.Expect(CardRegistry.Find("x")!.Cards[0].St.Pad == 10f, "a card sent with no style has the defaults");
        ServerPolicy.Apply("cardstyle={\"pad\":0,\"sizes\":[1,3]}");
        check.Expect(CardRegistry.Find("x")!.Cards[0].St.Pad == 0f, "a style sent after the card lays it out again");
        ServerPolicy.Apply("card.y={\"blocks\":[]}");
        check.Expect(CardRegistry.Find("y")!.Cards[0].St.Pad == 0f && CardRegistry.Find("y")!.Cards[0].St.Sizes.Length == 2, "a card sent after the style starts from it");
        ServerPolicy.Apply("cardstyle={\"pad\":\"big\"}");
        check.Expect(CardRegistry.Find("x")!.Cards[0].St.Pad == 10f, "a style CX refuses leaves the defaults");
        ServerPolicy.Apply("cardstyle={\"pad\":2}\ncard.*=");
        check.Expect(CardRegistry.Count == 0, "card.*= drops the cards");
        ServerPolicy.Apply("card.z={\"blocks\":[]}");
        check.Expect(CardRegistry.Find("z")!.Cards[0].St.Pad == 2f, "and keeps the style");
        ServerPolicy.Apply("cardstyle=");
        check.Expect(CardRegistry.Find("z")!.Cards[0].St.Pad == 10f, "an empty cardstyle restores the defaults");
        ServerPolicy.Apply("cardstyle={\"pad\":2}");
        ServerPolicy.Apply("*=");
        ServerPolicy.Apply("card.z={\"blocks\":[]}");
        check.Expect(CardRegistry.Find("z")!.Cards[0].St.Pad == 10f, "*= releases the style too");
        check.Expect(ServerPolicy.Report().Contains("card style: default"), "/policy reports the style");
        CardRegistry.Reset();
        ServerPolicy.Clear();
    }

    private static void Placement(Checker check)
    {
        var a = TestAtlas();
        PanelView one = Lay(a, "{\"width\":160,\"blocks\":[{\"text\":\"ab\"}]}"), two = Lay(a, "{\"width\":160,\"tab\":\"t\",\"blocks\":[{\"text\":\"ab<br>cd<br>ef\"}]}");
        PanelView[] views = [one, two];
        float[] xs = new float[2], ys = new float[2];
        var plain = new PanelStyle();
        CardPlacer.Place(views, 50, 40, 30, 0f, 800, 600, 1f, plain, xs, ys);
        check.Expect(Near(xs[0], 88f) && Near(xs[1], 88f + 160f + 8f) && Near(ys[0], 40f) && Near(ys[1], 40f), "cards sit right of the element, 8 px off it and from each other, tops level");
        CardPlacer.Place(views, 600, 40, 30, 0f, 800, 600, 1f, plain, xs, ys);
        check.Expect(Near(xs[0], 600f - 8f - 328f) && Near(xs[1], xs[0] + 168f), "no room on the right: the whole group goes to its left, order kept");
        CardPlacer.Place(views, 50, 560, 30, 0f, 800, 600, 1f, plain, xs, ys);
        check.Expect(Near(ys[0], 600f - two.H) && Near(ys[0], ys[1]), "a group past the bottom edge is lifted until its tallest card fits");
        CardPlacer.Place(views, 50, 0, 30, 0f, 800, 600, 1f, plain, xs, ys);
        check.Expect(Near(ys[0], 24f), "a card with a tab is kept 24 px from the top so the tab shows");
        CardPlacer.Place(views, 10, 40, 700, 0f, 800, 600, 1f, plain, xs, ys);
        check.Expect(Near(xs[0], 0f), "no room on either side: pinned to the left edge");
        PanelView[] solo = [Lay(a, "{\"width\":160,\"blocks\":[{\"text\":\"ab\"}]}")];
        void At(string style, float ex, float ey, string what, float wantX, float wantY)
        {
            CardPlacer.Place(solo, ex, ey, 50, 40, 800, 600, 1f, StyleOf(style), xs, ys);
            check.Expect(Near(xs[0], wantX) && Near(ys[0], wantY), $"{what}: ({wantX}, {wantY}) got ({xs[0]}, {ys[0]})");
        }
        At("{}", 100, 100, "the default is right of the element, tops level", 158f, 100f);
        At("{\"offset\":20,\"dx\":5,\"dy\":-3}", 100, 100, "offset 20 and a nudge of 5 right and 3 up", 175f, 97f);
        At("{\"side\":\"left\"}", 300, 100, "left of the element", 132f, 100f);
        At("{\"side\":\"left\"}", 100, 100, "left with no room flips right", 158f, 100f);
        At("{\"side\":\"left\",\"flip\":false}", 100, 100, "left with no room and no flip pins to the edge", 0f, 100f);
        At("{\"side\":\"right\",\"flip\":false}", 760, 100, "right with no flip is clamped, never flipped", 640f, 100f);
        At("{\"align\":\"center\"}", 100, 100, "centred on the element's side: 100 + 20 - 21", 158f, 99f);
        At("{\"align\":\"end\"}", 100, 100, "bottoms with the element's: 140 - 42", 158f, 98f);
        At("{\"side\":\"above\"}", 100, 100, "above, lefts level: 100 - 8 - 42", 100f, 50f);
        At("{\"side\":\"above\",\"align\":\"center\"}", 100, 100, "above and centred: 100 + 25 - 80", 45f, 50f);
        At("{\"side\":\"above\",\"align\":\"end\"}", 100, 100, "above with the right edges level is pinned at the screen's edge", 0f, 50f);
        At("{\"side\":\"below\"}", 100, 100, "below: 100 + 40 + 8", 100f, 148f);
        At("{\"side\":\"above\"}", 100, 10, "above with no room flips below: 10 + 40 + 8", 100f, 58f);
        PanelView[] pair = [solo[0], solo[0]];
        CardPlacer.Place(pair, 100, 100, 50, 40, 800, 600, 1f, StyleOf("{\"cardgap\":20}"), xs, ys);
        check.Expect(Near(xs[1] - xs[0], 180f), "cardgap 20: the second card starts 160 + 20 on");
    }

    private static void Registry(Checker check, ExtenderLog log)
    {
        CardRegistry.Reset();
        CardRegistry.Init(log);
        const string one = "{\"blocks\":[{\"text\":\"one\"}]}", two = "[{\"blocks\":[]},{\"blocks\":[]}]";
        check.Expect(ServerPolicy.Apply("card.Slot_1=" + one) == 1 && CardRegistry.Find("slot_1") is { Cards.Length: 1 }, "a card.<key> line is parsed and found without regard to case");
        int gen = CardRegistry.Gen;
        ServerPolicy.Apply("card.slot_1=" + two);
        check.Expect(CardRegistry.Find("slot_1") is { Cards.Length: 2 } && CardRegistry.Gen > gen, "a new send for the key replaces its cards");
        ServerPolicy.Apply("card.slot_1={\"width\":\"x\"}");
        check.Expect(CardRegistry.Find("slot_1") == null, "a malformed resend refused: the key keeps no card");
        ServerPolicy.Apply("card.a=" + one + "\ncard.b=" + one + "\ncard.@item:1f=" + one + "\ncard.a#2=" + one);
        check.Expect(CardRegistry.Count == 4 && CardRegistry.Find("@item:1F") != null && CardRegistry.Find("a#2") != null, "item and row keys are keys like any other");
        ServerPolicy.Apply("card.a=");
        check.Expect(CardRegistry.Find("a") == null && CardRegistry.Find("b") != null, "an empty value releases the key");
        ServerPolicy.Apply("card.*=");
        check.Expect(CardRegistry.Count == 0, "card.*= releases every card");
        ServerPolicy.Apply("card.x=" + one + "\ncard.y=" + one);
        ServerPolicy.Clear();
        check.Expect(CardRegistry.Count == 0, "a new session clears the cards");
        check.Expect(!CardRegistry.Set(new string('k', 97), one) && CardRegistry.Count == 0, "a key over 96 bytes is refused");
        check.Expect(ServerPolicy.Report().Contains("cards: 0 key(s)"), "/policy reports the cards");
        for (int i = 0; i < CardLimits.MaxKeys; i++) CardRegistry.Set("k" + i, one);
        check.Expect(!CardRegistry.Set("onemore", one) && CardRegistry.Set("k0", two) && CardRegistry.Count == CardLimits.MaxKeys, "256 keys is the ceiling; an existing one can still be replaced");
        CardRegistry.Reset();
        ServerPolicy.Clear();
    }

    /// <summary>Oracle: Tooltip's timing fixed at a 0.5 s dwell and 0.2 s fade-in on a fake clock, and the delays typed into the checks.</summary>
    private static void StyleTiming(Checker check, GlyphAtlas atlas, Func<long> clock, Action<long> setClock, long f)
    {
        NuiHit h = Hit("timed");
        ServerPolicy.Apply("card.timed={\"blocks\":[]}");
        bool Shows(string style, double seconds)
        {
            ServerPolicy.Apply("cardstyle=" + style);
            var p = new CardPresenter { Face = m => (atlas, m) };
            setClock(clock() + 100 * f);
            NuiHit none = default;
            p.Update(false, in none);
            p.Update(true, in h);
            setClock(clock() + (long)(seconds * f));
            return p.Prepare(800, 600) && p.Alpha > 0f;
        }
        check.Expect(!Shows("", 0.4) && Shows("", 0.55), "no delay in the style: the player's 0.5 s dwell");
        check.Expect(Shows("{\"delay\":0,\"fadein\":0}", 0.0), "a delay of 0 shows at once");
        check.Expect(!Shows("{\"delay\":1.5}", 1.4) && Shows("{\"delay\":1.5,\"fadein\":0}", 1.6), "a delay longer than the player's replaces it");
        ServerPolicy.Apply("cardstyle=");
        ServerPolicy.Apply("card.timed=");
    }

    /// <summary>
    /// The latency stand-ins. Oracle: keys and widths typed into the checks; the lookup order from the contract (row, id, image, longest
    /// prefix); the pulse values worked by hand from its sine (0 ms: 0.35 + 0.65 * 0.5 = 0.675, 300 ms: the crest 1, 900 ms: the trough 0.35);
    /// and Tooltip's 0.5 s dwell and 0.2 s fade on the fake clock. A 200 px card beside an element at x 50, w 30 sits at 88; a 320 px one
    /// does not fit right of it in 400 px, goes left, and is clamped to 0.
    /// </summary>
    private static void StandIns(Checker check, GlyphAtlas atlas, Func<long> clock, Action<long> setClock, long f)
    {
        CardRegistry.Reset();
        ServerPolicy.Clear();
        const string standIn = "{\"width\":200,\"blocks\":[{\"type\":\"loading\",\"text\":\"abab\"}]}";
        ServerPolicy.Apply("card.cell*=" + standIn + "\ncard.ce*=" + standIn.Replace("200", "180") + "\ncard.@item:1f=" + standIn.Replace("200", "170"));
        CardSet? Pick(NuiHit h) => CardPresenter.Lookup(in h);
        check.Expect(Pick(Hit("cellar")) is { Key: "cell*" } && Pick(Hit("cell", 2)) is { Key: "cell*" }, "the longest declared prefix of the id answers, an id equal to the prefix included");
        check.Expect(Pick(Hit("cexx")) is { Key: "ce*" } && Pick(Hit("zzz")) == null && Pick(Hit("")) == null, "a shorter prefix answers when it is the only one, and no prefix means no card");
        check.Expect(Pick(Hit("cellx", -1, "@item:1f")) is { Key: "@item:1f" }, "an image key beats a prefix");
        ServerPolicy.Apply("card.cellx=" + standIn.Replace("200", "190"));
        check.Expect(Pick(Hit("cellx")) is { Key: "cellx" }, "an id key beats a prefix");
        ServerPolicy.Apply("card.cellx#4=" + standIn.Replace("200", "191"));
        check.Expect(Pick(Hit("cellx", 4)) is { Key: "cellx#4" } && Pick(Hit("cellx", 5)) is { Key: "cellx" }, "a row key beats the id key");
        ServerPolicy.Apply("card.cell*=");
        check.Expect(Pick(Hit("cellar")) is { Key: "ce*" }, "releasing a prefix falls back to the next shorter one");
        ServerPolicy.Apply("card.cell*=" + standIn + "\ncard.cellx#4=[]\ncard.celly=[]");
        check.Expect(Pick(Hit("cellx", 4)) is { Cards.Length: 0 } && Pick(Hit("cellx", 5)) is { Key: "cellx" }, "[] is a held key with no cards, and it stops the fallback to the id key");
        check.Expect(Pick(Hit("celly")) is { Cards.Length: 0 } && Pick(Hit("celly", 1, "@item:1f")) is { Cards.Length: 0 }, "[] on an id also stops the prefix and the image key");
        var none = new CardPresenter { Face = m => (atlas, m) };
        NuiHit nh = Hit("celly");
        none.Update(true, in nh);
        check.Expect(!none.Hovering && none.Set == null, "an element declared with no card shows none, not the stand-in");
        check.Expect(PanelParser.TryParse("[]", out PanelDecl[] zero, out _) && zero.Length == 0 && !PanelParser.TryParse("{}x", out _, out _), "[] parses to zero cards");
        ServerPolicy.Apply("card.cellx#4=\ncard.celly=");
        check.Expect(Pick(Hit("celly")) is { Key: "cell*" }, "an empty value still releases the key, so the stand-in answers again");

        check.Expect(Near(PanelPulse.Alpha(0, 0.35f, 1200f), 0.675f) && Near(PanelPulse.Alpha(300, 0.35f, 1200f), 1f) && Near(PanelPulse.Alpha(900, 0.35f, 1200f), 0.35f) && Near(PanelPulse.Alpha(1200, 0.35f, 1200f), 0.675f), "the pulse fades between 0.35 and 1 once every 1200 ms");
        PanelView v = Lay(atlas, standIn);
        check.Expect(v.Pulse != null && v.Pulse.VertexCount > 0 && v.Text.VertexCount == 0 && Near(v.H, 42f), "a loading block is a text block that pulses");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"abab\"}]}");
        check.Expect(v.Pulse == null && v.Text.VertexCount > 0, "a text block does not pulse");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"abab\",\"pulse\":true}]}");
        check.Expect(v.Pulse != null && v.Pulse.VertexCount > 0 && v.Text.VertexCount == 0, "pulse on a text block makes it pulse");

        var p = new CardPresenter { Face = m => (atlas, m) };
        long t0 = clock() + 100 * f;
        setClock(t0);
        NuiHit h = Hit("cell", 2);
        p.Update(true, in h);
        setClock(t0 + f / 2 + f / 5);
        check.Expect(p.Prepare(400, 600) && p.Alpha == 1f && Near(p.Views![0].W, 200f) && Near(p.X(0), 88f), "a stand-in shows for the element until its own card arrives");
        CardSet? shown = p.Set;
        ServerPolicy.Apply("card.cell#2={\"width\":320,\"blocks\":[{\"text\":\"abab\"}]}");
        p.Update(true, in h);
        check.Expect(p.Set != shown && p.Hovering && p.Alpha == 1f, "the real card replaces the stand-in with no fade out and in");
        check.Expect(p.Prepare(400, 600) && Near(p.Views![0].W, 320f) && Near(p.X(0), 0f), "and is laid out and placed again at its own size");

        var q = new CardPresenter { Face = m => (atlas, m) };
        ServerPolicy.Apply("card.cell#2=");
        long t1 = t0 + 10 * f;
        setClock(t1);
        q.Update(true, in h);
        setClock(t1 + 3 * f / 10);
        check.Expect(q.Alpha == 0f, "inside the dwell the stand-in is not yet drawn");
        ServerPolicy.Apply("card.cell#2={\"width\":320,\"blocks\":[{\"text\":\"abab\"}]}");
        q.Update(true, in h);
        setClock(t1 + 7 * f / 10);
        check.Expect(q.Alpha == 1f && q.Prepare(400, 600) && Near(q.Views![0].W, 320f), "a swap inside the dwell keeps the dwell running: the card is in at 0.7 s, not 1.0 s");

        long before = GC.GetAllocatedBytesForCurrentThread();
        NuiHit a = Hit("cellar"), b = Hit("cexx");
        for (int i = 0; i < 200; i++) { q.Update(true, in a); q.Update(true, in b); }
        check.Expect(GC.GetAllocatedBytesForCurrentThread() - before == 0, "a prefix lookup allocates nothing");
        CardRegistry.Reset();
        ServerPolicy.Clear();
    }

    private static void Presenting(Checker check, GlyphAtlas atlas, ExtenderLog log)
    {
        CardRegistry.Reset();
        CardRegistry.Init(log);
        const string two = "[{\"width\":160,\"blocks\":[{\"text\":\"ab\"}]},{\"width\":160,\"blocks\":[{\"text\":\"cd\"}]}]";
        ServerPolicy.Apply("card.img=" + two + "\ncard.cell={\"blocks\":[]}\ncard.cell#2=[{\"blocks\":[{\"text\":\"ab\"}]}]\ncard.@item:1f={\"blocks\":[]}");
        long now = 0, f = System.Diagnostics.Stopwatch.Frequency;
        Func<long> clock = Tooltip.Clock;
        var timing = Tooltip.Timing;
        Tooltip.Clock = () => now;
        Tooltip.Timing = () => (0.5f, 0.2f, 0.4f);
        try
        {
            var p = new CardPresenter { Face = m => (atlas, m) };
            var none = default(NuiHit);
            p.Update(false, in none);
            check.Expect(!p.Hovering && p.Alpha == 0f && !p.Prepare(800, 600), "nothing under the pointer: no card");
            NuiHit h = Hit("img");
            p.Update(true, in h);
            check.Expect(p.Hovering && p.Alpha == 0f && !p.Prepare(800, 600), "a declared token starts the dwell and draws nothing yet");
            now = f / 2 + f / 5;
            check.Expect(p.Prepare(800, 600) && p.Alpha == 1f && p.Views!.Length == 2 && Near(p.X(0), 88f) && Near(p.X(1), 88f + 160f + 8f), "after the dwell and the fade-in: two cards side by side beside the element");
            p.Update(false, in none);
            check.Expect(!p.Hovering && p.Alpha > 0f && p.Alpha < 1.01f, "the pointer left, a window closed or the list scrolled the element away: the card fades");
            now += f;
            check.Expect(p.Alpha == 0f && !p.Prepare(800, 600), "and is gone after the fade-out");

            h = Hit("cell", 2);
            p.Update(true, in h);
            CardSet? atRow = p.Set;
            h = Hit("cell", 3);
            p.Update(true, in h);
            CardSet? other = p.Set;
            h = Hit("cell", -1);
            p.Update(true, in h);
            check.Expect(atRow != null && atRow.Cards[0].Blocks.Length == 1 && other != null && other.Cards[0].Blocks.Length == 0 && p.Set == other, "a list row's own key beats the shared cell key, which the other rows fall back to");
            h = Hit("", -1, "@item:1F.7");
            p.Update(true, in h);
            check.Expect(p.Set != null && p.Set.Key == "@item:1f" && p.Hovering, "an @item: image is a key too, whatever case the hex is in and whatever follows it");
            h = Hit("nothing", -1, "gui_plain");
            p.Update(true, in h);
            check.Expect(!p.Hovering, "an element no key matches shows no card");

            now += 10 * f;
            ServerPolicy.Apply("card.pair=[{\"item\":\"2a\",\"blocks\":[]},{\"blocks\":[]}]");
            var asked = new System.Collections.Generic.List<uint>();
            p.SubjectOf = id => { asked.Add(id); return default; };
            h = Hit("pair", -1, "@item:1f");
            p.Update(true, in h);
            now += f;
            p.Prepare(800, 600);
            check.Expect(asked.Count == 2 && asked[0] == 0x2A && asked[1] == 0x1F, "each card is about its own item, else the item the element draws");
            h = Hit("pair", -1, "@item:20");
            p.Update(true, in h);
            asked.Clear();
            p.Prepare(800, 600);
            check.Expect(asked.Count == 2 && asked[0] == 0x2A && asked[1] == 0x20, "moving to another row's item on the same key lays the card out again for that item");
            p.SubjectOf = static _ => default;
            h = Hit("img");
            p.Update(true, in h);
            now += f;
            p.Prepare(800, 600);
            CardSet? before = p.Set;
            ServerPolicy.Apply("card.img=[{\"blocks\":[]}]");
            p.Update(true, in h);
            check.Expect(p.Set != before && p.Set!.Cards.Length == 1 && p.Hovering, "a new send for the key replaces the card the pointer rests on");
            ServerPolicy.Apply("card.img=");
            p.Update(true, in h);
            check.Expect(!p.Hovering, "releasing the key hides it");

            ServerPolicy.Apply("card.img=" + two);
            h = Hit("img");
            p.Update(true, in h);
            now += f;
            p.Prepare(800, 600);
            long before1 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) { p.Update(true, in h); p.Prepare(800, 600); }
            check.Expect(GC.GetAllocatedBytesForCurrentThread() - before1 == 0, "a card on show costs no allocation per frame");
            NuiHit a = Hit("cell", 2), b = Hit("cell", 3), c = Hit("", -1, "@item:1f.7");
            before1 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) { p.Update(true, in a); p.Update(true, in b); p.Update(true, in c); }
            check.Expect(GC.GetAllocatedBytesForCurrentThread() - before1 == 0, "moving between elements allocates nothing: every lookup is on spans");
            StandIns(check, atlas, () => now, t => now = t, f);
            StyleTiming(check, atlas, () => now, t => now = t, f);
        }
        finally
        {
            Tooltip.Clock = clock;
            Tooltip.Timing = timing;
            CardRegistry.Reset();
            ServerPolicy.Clear();
        }
    }

    /// <summary>Oracle: hand-written declarations and the looks counted off them by eye; the fake mint hands out 1, 2, 3 and zero for a name containing "bad".</summary>
    private static void Looks(Checker check)
    {
        var wanted = new System.Collections.Generic.HashSet<string>();
        ServerPolicy.Clear();
        CardRegistry.Reset();
        PanelParser.TryParse("{\"width\":200,\"images\":[{\"src\":\"@look:0001\",\"w\":20,\"h\":20}],\"blocks\":[{\"type\":\"icons\",\"icons\":[\"@look:0002\",\"gui_x\",\"@look:0001\"]},{\"text\":\"a <icon=@look:0003> b\"}]}", out PanelDecl[] cards, out _);
        CardLooks.Collect(cards[0], wanted);
        check.Expect(wanted.Count == 3 && wanted.Contains("@look:0001") && wanted.Contains("@look:0002") && wanted.Contains("@look:0003"),
            "the images, an icon row and an inline icon each name a look; a repeat counts once");
        var state = new CardLooks.LooksState();
        int made = 0;
        var ended = new System.Collections.Generic.List<IntPtr>();
        IntPtr Mint(string look) => look.Contains("bad") ? IntPtr.Zero : (IntPtr)(++made);
        CardLooks.Reconcile(state, wanted, Mint, ended.Add, null);
        check.Expect(made == 3 && state.Minted.Count == 3 && ended.Count == 0, "three distinct looks mint three items");
        CardLooks.Reconcile(state, wanted, Mint, ended.Add, null);
        check.Expect(made == 3 && ended.Count == 0, "nothing changed, nothing minted or ended");
        IntPtr two = state.Minted["@look:0002"];
        wanted.Remove("@look:0002");
        wanted.Add("@look:bad");
        CardLooks.Reconcile(state, wanted, Mint, ended.Add, null);
        check.Expect(ended.Count == 1 && ended[0] == two && !state.Minted.ContainsKey("@look:0002"), "a look no card names any more has its item ended");
        check.Expect(state.Minted["@look:bad"] == IntPtr.Zero && made == 3, "a look the engine made nothing for is remembered as none");
        CardLooks.Reconcile(state, wanted, Mint, ended.Add, null);
        check.Expect(made == 3, "and is not tried again");
        wanted.Clear();
        CardLooks.Reconcile(state, wanted, Mint, ended.Add, null);
        check.Expect(ended.Count == 3 && state.Minted.Count == 0, "with no card naming a look every item is ended, and the none needs no ending");
    }
}
