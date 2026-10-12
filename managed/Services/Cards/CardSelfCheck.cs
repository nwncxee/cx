using System;
using System.Text;
using CX.Framework;
using CX.Services.Nui;
using CX.Ui;

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
            {"type":"header","text":"Sword","color":"#3399ff"},
            {"type":"subtitle","text":"Rare"},
            {"type":"status","icon":"gui_s","text":"Equipped"},
            {"type":"headline","text":"2~12 Damage"},
            {"type":"divider"},
            {"type":"section","tint":"#ffffff10","blocks":[{"type":"text","text":"in <c=#ff0000>red</c>"}]},
            {"type":"icons","icons":["a","b","c"]},
            {"type":"flavour","text":"old <i>words</i>"},
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

    private static CardView Lay(GlyphAtlas atlas, string cardJson, CardSubject subject = default, float scale = 1f)
    {
        CardParser.TryParse(cardJson, out CardDecl[] cards, out _);
        return CardLayout.Build(cards[0], scale, m => (atlas, m), subject);
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
        Registry(check, log);
        Presenting(check, atlas, log);
        log.Info(check.Ok ? "CardSelfCheck: passed" : "CardSelfCheck: FAILED");
    }

    private static void Markup(Checker check)
    {
        check.Expect(CardMarkup.TryParse("a<c=#ff0000>b</c> <i>c</i><br>d <<e <z>", out Run[] r, out _) && r.Length == 6
            && r[0].Text == "a" && r[0].Rgba == 0 && r[1].Text == "b" && r[1].Rgba == 0xFF0000FF && r[2].Text == " " && r[2].Rgba == 0
            && r[3].Text == "c" && r[3].Italic && r[4].Break && r[5].Text == "d <e <z>",
            "colour and italic open and close, <br> breaks, << is a literal <, an unknown tag stays as typed");
        check.Expect(CardMarkup.TryParse("<s=1.5>x</s><s=9>y</s>", out r, out _) && r[0].Size == 1.6f && r[1].Size == 2f, "a size snaps to the nearest ladder step");
        check.Expect(CardMarkup.TryParse("<c=red>x", out r, out _) && r.Length == 1 && r[0].Text == "<c=red>x", "a bad colour value stays as typed");
        check.Expect(CardMarkup.TryParse("a\nb<icon=gui_x>", out r, out _) && r.Length == 4 && r[1].Break && r[3].Icon == "gui_x", "a newline breaks the line and <icon=> is an inline icon");
        check.Expect(!CardMarkup.TryParse(new string('a', 401), out _, out _), "401 characters is over the text limit");
        check.Expect(!CardMarkup.TryParse(string.Concat(System.Linq.Enumerable.Repeat("a<i>", 49)), out _, out _), "49 runs is over the run limit");
    }

    private static void Parse(Checker check)
    {
        check.Expect(CardParser.TryParse(Everything, out CardDecl[] cards, out string why) && cards.Length == 1, "a card using every block parses: " + why);
        if (cards.Length != 1) return;
        CardDecl c = cards[0];
        BlockKind[] kinds = [BlockKind.Header, BlockKind.Subtitle, BlockKind.Status, BlockKind.Headline, BlockKind.Divider, BlockKind.Section,
            BlockKind.Icons, BlockKind.Flavour, BlockKind.Footer, BlockKind.Text, BlockKind.Text];
        bool order = c.Blocks.Length == kinds.Length;
        for (int i = 0; order && i < kinds.Length; i++) order = c.Blocks[i].Kind == kinds[i];
        check.Expect(order, "the blocks come back in order, an unknown type as text");
        check.Expect(c.BgFrom == 0x102030FF && c.BgTo == 0x304050FF && !c.Horizontal && c.Border == 0xAABBCCFF && c.Tab == "Equipped" && c.Width == 240f && c.Item == "1f",
            "card fields: gradient, border, tab, width, item");
        check.Expect(c.Blocks[0].Color == 0x3399FFFF && c.Blocks[0].Size == 1.3f && c.Blocks[1].Size == 0.85f && c.Blocks[1].Color == 0xFFFFFF99, "header colour and size, subtitle dim");
        check.Expect(c.Blocks[7].Runs[0].Italic && c.Blocks[7].Runs[1].Italic, "flavour is italic");
        check.Expect(c.Blocks[5].Kids.Length == 1 && c.Blocks[5].Tint == 0xFFFFFF10 && c.Blocks[6].Icons.Length == 3 && c.Blocks[6].Frame, "section tint and kids, a framed icon row");
        check.Expect(c.Blocks[8].Runs.Length == 1 && c.Blocks[8].Right.Length == 3 && c.Blocks[8].Right[1].Icon == "w", "footer left and right, an inline icon on the right");
        check.Expect(c.Images.Length == 1 && c.Images[0].At == Anchor.TopRight && c.Images[0].Dx == 20f && c.Images[0].Dy == -20f, "an image anchored top right, pushed out");
        check.Expect(CardParser.TryParse("{\"blocks\":[],\"future\":{\"a\":1}}", out cards, out _) && cards.Length == 1, "an unknown field is ignored");
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
        foreach (string b in bad) if (CardParser.TryParse(b, out _, out string e) || e.Length == 0) { allRefused = false; check.Expect(false, "refused: " + b[..Math.Min(b.Length, 40)]); }
        check.Expect(allRefused, "each malformed declaration is refused with a reason");
    }

    private static void Layout(Checker check, GlyphAtlas atlas)
    {
        CardView v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"aaaa bbbb cccc\"}]}");
        check.Expect(Near(v.W, 200f) && Near(v.H, 42f), $"one 20 px line: 10 pad + 20 + 2 gap + 10 pad = 42 ({v.H})");
        v = Lay(atlas, "{\"width\":160,\"blocks\":[{\"text\":\"aaaa bbbb cccc dddd\"}]}");
        check.Expect(Near(v.H, 62f), $"140 px of text holds three words (128) and wraps the fourth: two lines, 10 + 40 + 2 + 10 = 62 ({v.H})");
        v = Lay(atlas, "{\"width\":160,\"blocks\":[{\"text\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}");
        check.Expect(Near(v.H, 42f), "one word wider than the card stays on one line and runs past the edge");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"headline\",\"text\":\"ab\"}]}");
        check.Expect(Near(v.H, 10f + 32f + 2f + 10f), $"a headline at 1.6 is 20 * 1.6 = 32 px tall ({v.H})");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"header\",\"text\":\"ab\"}]}");
        check.Expect(Near(v.H, 10f + 26f + 10f), $"a header at 1.3 is 26 px tall with no gap below ({v.H})");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"ab<br>cd\"}]}");
        check.Expect(Near(v.H, 10f + 40f + 2f + 10f), "<br> starts a second line");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"status\",\"icon\":\"gui_s\",\"text\":\"ab\"}]}");
        check.Expect(Near(v.H, 10f + 22f + 4f + 10f) && v.Icons.Count == 1 && v.Icons[0].Src == "gui_s" && Near(v.Icons[0].X, 10f) && Near(v.Icons[0].W, 22f),
            "a status row is as tall as its 22 px icon, the icon sits at the left padding");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"icons\",\"icons\":[\"a\",\"b\",\"c\"]}]}");
        check.Expect(Near(v.H, 10f + 28f + 4f + 10f) && v.Icons.Count == 3 && Near(v.Icons[1].X, 10f + 2f + 32f), "three framed 28 px icons share a row, 32 px apart");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"icons\",\"icons\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\"]}]}");
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
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"{item.name}\"}]}", new CardSubject("torch", "3"));
        check.Expect(v.Text.VertexCount == 30, "{item.name} is the item's name: five letters, five quads");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"{item.name}\"}]}");
        check.Expect(v.Text.VertexCount == 0, "with no item it is empty");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"type\":\"icons\",\"icons\":[\"@item:1f\",\"@item:zz\",\"@look:0001\",\"gui_x\",\"@item:2.7\"]}]}");
        check.Expect(v.Icons.Count == 3 && v.Icons[0].Item == 0x1F && v.Icons[1].Src == "gui_x" && v.Icons[2].Item == 0x2,
            "@item: takes its hex digits, a bad one, a @look: and a @model: draw nothing, a resref draws as itself");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"ab\"}]}", scale: 2f);
        check.Expect(Near(v.W, 400f) && Near(v.H, 20f + 20f + 4f + 20f), "at UI scale 2 the padding and gaps double; the test face's own height does not");
    }

    private static void Placement(Checker check)
    {
        var a = TestAtlas();
        CardView one = Lay(a, "{\"width\":160,\"blocks\":[{\"text\":\"ab\"}]}"), two = Lay(a, "{\"width\":160,\"tab\":\"t\",\"blocks\":[{\"text\":\"ab<br>cd<br>ef\"}]}");
        CardView[] views = [one, two];
        float[] xs = new float[2], ys = new float[2];
        CardPlacer.Place(views, 50, 40, 30, 800, 600, 1f, xs, ys);
        check.Expect(Near(xs[0], 88f) && Near(xs[1], 88f + 160f + 8f) && Near(ys[0], 40f) && Near(ys[1], 40f), "cards sit right of the element, 8 px off it and from each other, tops level");
        CardPlacer.Place(views, 600, 40, 30, 800, 600, 1f, xs, ys);
        check.Expect(Near(xs[0], 600f - 8f - 328f) && Near(xs[1], xs[0] + 168f), "no room on the right: the whole group goes to its left, order kept");
        CardPlacer.Place(views, 50, 560, 30, 800, 600, 1f, xs, ys);
        check.Expect(Near(ys[0], 600f - two.H) && Near(ys[0], ys[1]), "a group past the bottom edge is lifted until its tallest card fits");
        CardPlacer.Place(views, 50, 0, 30, 800, 600, 1f, xs, ys);
        check.Expect(Near(ys[0], 24f), "a card with a tab is kept 24 px from the top so the tab shows");
        CardPlacer.Place(views, 10, 40, 700, 800, 600, 1f, xs, ys);
        check.Expect(Near(xs[0], 0f), "no room on either side: pinned to the left edge");
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
        check.Expect(CardParser.TryParse("[]", out CardDecl[] zero, out _) && zero.Length == 0 && !CardParser.TryParse("{}x", out _, out _), "[] parses to zero cards");
        ServerPolicy.Apply("card.cellx#4=\ncard.celly=");
        check.Expect(Pick(Hit("celly")) is { Key: "cell*" }, "an empty value still releases the key, so the stand-in answers again");

        check.Expect(Near(CardPulse.Alpha(0), 0.675f) && Near(CardPulse.Alpha(300), 1f) && Near(CardPulse.Alpha(900), 0.35f) && Near(CardPulse.Alpha(1200), 0.675f), "the pulse fades between 0.35 and 1 once every 1200 ms");
        CardView v = Lay(atlas, standIn);
        check.Expect(v.Pulse != null && v.Pulse.VertexCount > 0 && v.Text.VertexCount == 0 && Near(v.H, 42f), "a loading block's text is the pulsing batch, laid out like a text block");
        v = Lay(atlas, "{\"width\":200,\"blocks\":[{\"text\":\"abab\"}]}");
        check.Expect(v.Pulse == null && v.Text.VertexCount > 0, "a text block does not pulse");

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
        }
        finally
        {
            Tooltip.Clock = clock;
            Tooltip.Timing = timing;
            CardRegistry.Reset();
            ServerPolicy.Clear();
        }
    }
}
