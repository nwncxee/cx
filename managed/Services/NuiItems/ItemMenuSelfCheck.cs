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
