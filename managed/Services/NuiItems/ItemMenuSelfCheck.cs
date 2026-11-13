using System;
using CX.Framework;

namespace CX.Services.NuiItems;

/// <summary>
/// The server's item menu as policy lines: where it opens, which rows it carries and in what order, what it refuses, and
/// what a close leaves behind. Oracle: the lines, labels, ids and points typed in by hand.
/// </summary>
internal static class ItemMenuSelfCheck
{
    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "ItemMenuSelfCheck");
        Rows(check);
        Order(check);
        Refusals(check);
        Closes(check);
        AtThePointer(check);
        AboveNui(check);
        NuiLook(check);
        ServerLook(check);
        ServerLayout(check);
        log.Info(check.Ok ? "ItemMenuSelfCheck: passed" : "ItemMenuSelfCheck: FAILED");
    }

    /// <summary>Oracle: the three lines typed here and the fields they spell.</summary>
    private static void Rows(Checker check)
    {
        var m = new ItemMenuModel();
        check.Expect(!m.Open && m.RowCount == 0 && m.Rows().Length == 0, "a menu nobody opened offers nothing");
        check.Expect(m.Set("open", "120;64") == null && m.Open && m.X == 120 && m.Y == 64, "open names the point in pixels");
        check.Expect(m.Set("row.1", "use;Use;iit_potion;1") == null && m.RowCount == 1, "a row is taken");
        ItemMenuRow r = m.Rows()[0];
        check.Expect(r.Id == "use" && r.Label == "Use" && r.Icon == "iit_potion" && r.Enabled, "the row keeps the id, label, icon and enabled flag the server sent");
        check.Expect(m.Set("row.2", "bandolier;Add to bandolier;;0") == null && !m.Rows()[1].Enabled && m.Rows()[1].Icon.Length == 0,
            "a row with no icon and a 0 flag is a greyed row with no icon");
    }

    /// <summary>Oracle: rows declared out of order here, read back in the order of their numbers.</summary>
    private static void Order(Checker check)
    {
        var m = new ItemMenuModel();
        m.Set("open", "0;0");
        m.Set("row.3", "c;Third;;1");
        m.Set("row.1", "a;First;;1");
        m.Set("row.2", "b;Second;;1");
        ItemMenuRow[] rows = m.Rows();
        check.Expect(rows.Length == 3 && rows[0].Id == "a" && rows[1].Id == "b" && rows[2].Id == "c", "the rows draw in the order the server numbered them");
        int gen = m.Gen;
        m.Set("row.2", "");
        check.Expect(m.RowCount == 2 && m.Rows()[1].Id == "c" && m.Gen != gen, "a released row leaves the others in order, and the draw is told to rebuild");
    }

    /// <summary>Oracle: the malformed lines typed here, each with the reason it earns.</summary>
    private static void Refusals(Checker check)
    {
        var m = new ItemMenuModel();
        check.Expect(m.Set("open", "nope") != null && !m.Open, "an open that names no point is refused and opens nothing");
        check.Expect(m.Set("row.x", "a;A;;1") != null, "a row key that is not a number is refused");
        check.Expect(m.Set("rows.1", "a;A;;1") != null, "an unknown key is refused");
        m.Set("open", "10;10");
        check.Expect(m.Set("row.1", "a") != null && m.RowCount == 0, "a row without a label is refused");
        check.Expect(m.Set("row.1", ";A;;1") != null && m.RowCount == 0, "a row without an id is refused: a pick could name nothing");
        for (int i = 0; i < ItemMenuModel.MaxRows; i++) m.Set("row." + i, $"r{i};Row {i};;1");
        check.Expect(m.RowCount == ItemMenuModel.MaxRows && m.Set("row.999", "x;X;;1") != null, "the row cap holds, and the row past it is refused");
        check.Expect(m.Set("row.0", "r0;Changed;;1") == null, "a row already held can still change under the cap");
    }

    /// <summary>Oracle: the state after a close, written out: no rows, not open, and a report that says so.</summary>
    private static void Closes(Checker check)
    {
        var m = new ItemMenuModel();
        m.Set("open", "5;5");
        m.Set("row.1", "use;Use;;1");
        int gen = m.Gen;
        m.Close();
        check.Expect(!m.Open && m.RowCount == 0 && m.Gen != gen, "a close puts the menu away and holds no row");
        check.Expect(m.Report().Contains("menu"), "the /policy line names the menu");
        check.Expect(m.Report().Contains("closed"), "a closed menu says so");
        m.Set("open", "5;5");
        check.Expect(m.Report().Contains("open at 5,5"), "an open one says where it is");
    }

    /// <summary>The menu opens over the NUI window it was asked from, so a press anywhere reaches it before NUI does: off the menu
    /// it closes with <c>menu;</c>. Oracle: the points, the row and the wire text typed here, and a stand-in font of hand-picked metrics.</summary>
    private static void AboveNui(Checker check)
    {
        var atlas = new CX.Ui.GlyphAtlas { LineHeight = 20, Ascent = 15 };
        var m = new ItemMenuModel();
        m.Set("row.1", "use;Use;;1");
        m.Set("open", "200;200");
        ItemMenuService.Show(m, atlas, 1920, 1080);
        check.Expect(ItemMenuService.IsOpen, "the declared menu opens");
        check.Expect(CX.Ui.UiHost.HitTest(205, 205)?.Layer == CX.Ui.UiLayer.AboveNui, "the menu draws above every NUI window, so the inventory cannot cover it");

        var sent = new System.Collections.Generic.List<string>();
        var was = ServerPolicy.Send;
        ServerPolicy.Send = (minor, ascii) => sent.Add(ascii);
        bool taken = CX.Ui.UiHost.DispatchMouseButton(true, 1, 1500, 900, captureOnly: true);
        ServerPolicy.Send = was;
        check.Expect(taken, "a press off the menu is taken ahead of the NUI window under it");
        check.Expect(!ItemMenuService.IsOpen && sent.Count == 1 && sent[0] == "menu;", $"...and closes the menu with menu; ({(sent.Count == 0 ? "nothing sent" : sent[0])})");
        ItemMenuService.Show(new ItemMenuModel(), atlas, 1920, 1080);
    }

    /// <summary>The menu stands in for the server's NUI Item menu, so it wears the stock NUI skin whatever look the extender's
    /// own windows take: no frame art, no fade. Oracle: the colours of the stock <c>nui_skin.tml</c>, typed here by hand.</summary>
    private static void NuiLook(Checker check)
    {
        string was = CX.Ui.UiKnobs.Look.V;
        CX.Ui.UiKnobs.Look.Set(CX.Ui.Theme.LookFlat);
        CX.Ui.Theme.Apply();
        check.Expect(!CX.Ui.Theme.IsNative, "precondition: the extender's own windows take the flat look");
        CX.Ui.CascadeStyle look = ItemMenuService.Look(0);
        bool art = ItemMenuService.DrawsArt;
        CX.Ui.UiKnobs.Look.Set(was);
        CX.Ui.Theme.Apply();
        check.Expect(look.Bg == (0f, 0f, 0f, 1f), $"the menu's ground is NUI's black ({look.Bg})");
        check.Expect(look.TextRgb == 0xf8f8f8, $"its text is NUI's #F8F8F8 ({look.TextRgb:x6})");
        check.Expect(look.Hover == (0x1f / 255f, 0x42 / 255f, 0x61 / 255f, 1f), $"a hovered row is NUI's selection blue ({look.Hover})");
        check.Expect(look.Frame == (0xc8 / 255f, 0xc8 / 255f, 0xc8 / 255f, 1f), $"its edge is NUI's #C8C8C8 ({look.Frame})");
        check.Expect(look.AnimMs == 0 && look.Alpha == 1f, "it opens at once and fully opaque, as a NUI window does");
        check.Expect(!art, "it draws no frame art");
        CX.Ui.UiKnobs.Look.Set(CX.Ui.Theme.LookNative);
        CX.Ui.Theme.Apply();
        check.Expect(!ItemMenuService.DrawsArt, "...not even when the extender's own windows take the game's art");
        CX.Ui.UiKnobs.Look.Set(was);
        CX.Ui.Theme.Apply();
    }

    /// <summary>The server's own NUI menu is the look to match, so the server sends it: its row colours, its row height,
    /// and which rows start a group. Oracle: the lines and colours typed here by hand.</summary>
    private static void ServerLook(Checker check)
    {
        var m = new ItemMenuModel();
        check.Expect(m.Look.Fill == null && m.Look.RowHeight == 0, "a menu nobody styled draws in the NUI skin's colours, its rows as tall as its text");
        check.Expect(m.Set("look", "#261C14;#3B2D20;#615032;#C7B9A0;#33261A;#5A4630;#615032") == null, "a look of seven colours is taken");
        check.Expect(m.Look.Fill == 0x261C14FFu && m.Look.Edge == 0x3B2D20FFu && m.Look.Stripe == 0x615032FFu && m.Look.Text == 0xC7B9A0FFu
                     && m.Look.HoverFill == 0x33261AFFu && m.Look.HoverEdge == 0x5A4630FFu && m.Look.Rule == 0x615032FFu,
            "each colour lands where the line names it, opaque when no alpha is given");
        check.Expect(m.Set("look", "#11223380;;;;;;") == null && m.Look.Fill == 0x11223380u && m.Look.Edge == null,
            "an alpha is kept, and an empty field leaves that part to the NUI skin");
        check.Expect(m.Set("look", "red;;;;;;") != null && m.Look.Fill == 0x11223380u, "a colour that is no #RRGGBB is refused, and the look stays as it was");
        check.Expect(m.Set("rowheight", "28") == null && m.Look.RowHeight == 28f, "the row height is taken in NUI units");
        check.Expect(m.Set("rowheight", "0") != null && m.Set("rowheight", "x") != null && m.Look.RowHeight == 28f, "a row height that is no positive number is refused");
        m.Set("row.1", "use;Use;;1");
        m.Set("row.2", "drop;Drop;;1;1");
        ItemMenuRow[] rows = m.Rows();
        check.Expect(!rows[0].RuleAbove && rows[1].RuleAbove, "a row whose fifth field is 1 starts a group, with a rule above it; one without it does not");
        m.Set("look", "#261C14;#3B2D20;#615032;#C7B9A0;#33261A;#5A4630;#615032");
        CX.Ui.CascadeStyle drawn = ItemMenuService.Look(0, m.Look);
        check.Expect(drawn.Rows && drawn.Bg.a == 0f && drawn.Frame.a == 0f, "under the server's look each row is a list row of its own, on no body or frame");
        check.Expect(drawn.RowFill == CX.Ui.CascadeStyle.Rgba(0x261C14FFu) && drawn.RowStripe == CX.Ui.CascadeStyle.Rgba(0x615032FFu)
                     && drawn.Hover == CX.Ui.CascadeStyle.Rgba(0x33261AFFu) && drawn.TextRgb == 0xC7B9A0u,
            "the rows draw in the server's fill, stripe, hover and text colours");
        check.Expect(!ItemMenuService.Look(0, new ItemMenuLook()).Rows, "with no look from the server the menu keeps the NUI skin's plain list");
        m.Set("look", "#11223380;;;;;;");
        m.Close();
        check.Expect(m.Look.Fill == 0x11223380u && m.Look.RowHeight == 28f, "a closed menu keeps its look for the next one");
        check.Expect(m.Set("look", "") == null && m.Set("rowheight", "") == null && m.Look.Fill == null && m.Look.RowHeight == 0,
            "empty values give the look back to the NUI skin");
    }

    /// <summary>The server's row layout: where a list row puts its icon and label, and how much room it leaves after the
    /// label, so the menu is as wide as the server's own. Oracle: the line typed here, and a stand-in font of 6 px glyphs.</summary>
    private static void ServerLayout(Checker check)
    {
        var m = new ItemMenuModel();
        check.Expect(m.Set("layout", "3;15;4;8;28") == null && m.Look.Layout is { } l
                     && l.Stripe == 3f && l.Lead == 15f && l.IconInset == 4f && l.Gap == 8f && l.Trail == 28f,
            "a layout of five NUI-unit numbers is taken: stripe, lead, icon inset, label gap, trail");
        check.Expect(m.Set("layout", "3;15;4;8") != null && m.Set("layout", "3;15;-4;8;28") != null && m.Look.Layout?.Lead == 15f,
            "a layout short of a number, or with a negative one, is refused and the last one stands");
        check.Expect(m.Set("layout", "") == null && m.Look.Layout == null, "an empty layout gives the row back to the font's own");

        var atlas = new CX.Ui.GlyphAtlas { LineHeight = 20, Ascent = 15 };
        for (char ch = 'a'; ch <= 'z'; ch++) atlas.SetTestGlyph(ch, 6f);
        var menu = new CX.Ui.CascadeMenu { FixedItemH = 28f, Row = new CX.Ui.CascadeRowLayout(3f, 15f, 4f, 8f, 28f) };
        menu.OpenAt(new[] { new CX.Ui.CascadeItem("use", "ir_x") }, 100f, 100f, atlas, 1920f, 1080f);
        var look = new ItemMenuLook { Fill = 0x261C14FFu };
        CX.Ui.CascadeStyle st = ItemMenuService.Look(0, look);
        menu.Draw(new CX.Ui.VertexBatch(0f, 0f), atlas, atlas.Ascent, in st);
        var icons = menu.Icons;
        check.Expect(icons.Length == 1 && icons[0].X == 115f && icons[0].Y == 104f && icons[0].Size == 20f,
            $"the icon sits past the stripe and inset, inset from the row's top and bottom ({(icons.Length == 0 ? "none" : $"{icons[0].X},{icons[0].Y} {icons[0].Size}")})");
        check.Expect(menu.Level(0).W == 15f + 20f + 8f + 18f + 28f, $"the menu is as wide as lead, icon, gap, label and trail ({menu.Level(0).W})");

        // A 21 px line in a 28 px row puts the baseline half a pixel off, and 6.4 px advances put every glyph between pixels.
        var odd = new CX.Ui.GlyphAtlas { LineHeight = 21, Ascent = 16 };
        foreach (char ch in "use") odd.SetTestGlyph(ch, 6.4f, 5, 9);
        var snapped = new CX.Ui.CascadeMenu
        {
            FixedItemH = 28f, Row = new CX.Ui.CascadeRowLayout(3f, 15f, 4f, 8f, 28f), SnapText = true, TextSpacing = 0.5f,
        };
        snapped.OpenAt(new[] { new CX.Ui.CascadeItem("use") }, 100f, 100f, odd, 1920f, 1080f);
        var batch = new CX.Ui.VertexBatch(0f, 0f);
        snapped.Draw(batch, odd, odd.Ascent, in st);
        ReadOnlySpan<float> v = batch.AsSpan();
        bool whole = v.Length > 0;
        for (int i = 0; i < v.Length; i += CX.Ui.VertexBatch.Floats)
            whole &= v[i] == MathF.Round(v[i]) && v[i + 1] == MathF.Round(v[i + 1]);
        check.Expect(whole, "with the skin's pixel snap every glyph is drawn on whole pixels, as NUI draws it, so the label is sharp");
        check.Expect(MathF.Abs(snapped.Level(0).W - (15f + 3f * (6.4f + 0.5f) + 28f)) < 0.01f,
            $"the skin's spacing goes between the glyphs, and the menu is as wide as that ({snapped.Level(0).W})");
    }

    /// <summary>Oracle: the word pointer typed here, which a server sends because it knows the element pressed and not the
    /// pixel; and the points typed beside it.</summary>
    private static void AtThePointer(Checker check)
    {
        var m = new ItemMenuModel();
        check.Expect(m.Set("open", "pointer") == null && m.Open && m.AtPointer, "open=pointer opens the menu where the pointer is");
        check.Expect(m.Report().Contains("at the pointer"), "the /policy line says it opened at the pointer");
        check.Expect(m.At(300, 200) == (300, 200), "open at the pointer, the menu takes the pointer's pixel");
        int gen = m.Gen;
        check.Expect(m.Set("open", "40;50") == null && !m.AtPointer && m.X == 40 && m.Y == 50 && m.Gen != gen,
            "a point sent after it opens the menu at that point instead");
        check.Expect(m.At(300, 200) == (40, 50), "open at a point, the menu ignores the pointer");
        m.Set("open", "Pointer");
        check.Expect(m.AtPointer, "the word is read whatever its case");
        m.Close();
        check.Expect(!m.AtPointer && !m.Open, "a close forgets the pointer too");
    }
}
