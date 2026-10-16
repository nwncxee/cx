using System.Collections.Generic;
using CX.Framework;
using CX.Ui;
using Row = CX.Ui.KnobPanel.Row;

namespace CX.Services.Chat.Core;

public sealed partial class SettingsWindow
{
    /// <summary>Off-device proof of the widget state machine: the category list, open/close/ownership, each
    /// control type's edit, the background shape rows, the tab editor and the font rows.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        string[] realFaces = Typefaces;
        Typefaces = new[] { FollowUi, "Arial", "DejaVu Serif" };

        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x180; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }
        body.SetTestGlyph(0x25BE, 11f); small.SetTestGlyph(0x25BE, 8f);
        void Open(SettingsWindow sw, ChatWindow w, int winIndex = 0, int winCount = 1)
        { sw.OpenAt(w, 1280, 800, 20f, body, small, winIndex, winCount); sw.MouseUp(); }
        Row Find(SettingsWindow sw, SetAct act, string key = "") => sw.Rows.Find(r => r.Act == act && (key.Length == 0 || r.Key == key))!;
        // The window rows overflow the column: scroll a row into the box before pressing it (offset-correct hits).
        void Reveal(SettingsWindow sw, Row r) { sw.Panel.List.Offset = r.Top - 0.4f * sw.Panel.Em; sw.Panel.List.Clamp(); }
        void PressRow(SettingsWindow sw, ChatWindow w, Row r) { Reveal(sw, r); sw.MouseDown(sw.Panel.List.X + 5, sw.Panel.RowTop(r) + 2, w); sw.MouseUp(); }
        void PressBox(SettingsWindow sw, ChatWindow w, Row r)
        { Reveal(sw, r); sw.Panel.BoxRect(r, sw.Panel.RowTop(r), out float x0, out float x1, out float y0, out float y1); sw.MouseDown((x0 + x1) / 2f, (y0 + y1) / 2f, w); sw.MouseUp(); }
        void WheelToEnd(SettingsWindow sw) { for (int i = 0; i < 64; i++) sw.MouseWheel(-1); }
        void PressCat(SettingsWindow sw, ChatWindow w, int c)
        { sw.MouseDown(sw.Panel.Cats.X + 5, sw.Panel.Cats.Y + 0.4f * sw.Panel.Em + c * (body.LineHeight + 1.2f * sw.Panel.Em) + 2, w); sw.MouseUp(); }

        var win = new ChatWindow(100, 100, 400, 300, new[] { new Tab("All", 0xffffffff) });
        var s = new SettingsWindow();

        Open(s, win);
        check.Expect(s.Open && s.Rows.Count > 0 && s.Panel.Category == 0, "OpenAt opens on the window category with rows");
        check.Expect(s.Panel.Categories.Length == 3 && s.Panel.Categories[0] == WindowCategory && s.Panel.Categories[1] == "All" && s.Panel.Categories[2] == AddTabCategory,
          "categories: the window, one per tab, add");
        check.Expect(Find(s, SetAct.TabLabel) == null && Find(s, SetAct.ToggleBit) != null, "the window category holds the window rows, no tab editor");
        check.Expect(System.Math.Abs(s.X - (1280 - s.W) / 2f) < 0.5f && System.Math.Abs(s.Y - (800 - s.H) / 2f) < 0.5f, "panel opens centred");

        // Toggle row flips its bit and stays open.
        Row tgl = Find(s, SetAct.ToggleBit);
        uint before = win.Toggles;
        Reveal(s, tgl);
        check.Expect(s.MouseDown(s.Panel.List.X + 5, s.Panel.RowTop(tgl) + 2, win), "toggle click consumed");
        check.Expect(s.Open && win.Toggles == (before ^ tgl.Bit), "toggle flips its bit, stays open");
        s.MouseUp();

        // Animations off hides the duration sliders; on shows them.
        PressRow(s, win, Find(s, SetAct.AnimOn));
        check.Expect(!win.AnimEnabled && Find(s, SetAct.SliderHold) == null, "anim off hides sliders");
        PressRow(s, win, Find(s, SetAct.AnimOn));
        check.Expect(win.AnimEnabled && Find(s, SetAct.SliderHold) != null, "anim on shows sliders");

        // Slider drag sets the window value from the pointer x (full-right -> max), on the drawn row.
        Row hold = Find(s, SetAct.SliderHold);
        Reveal(s, hold);
        s.Panel.TrackRect(hold, s.Panel.RowTop(hold), out float hx0, out float hx1, out float hcy);
        s.MouseDown(hx1 - 0.1f, hcy, win);
        check.Expect(System.Math.Abs(win.HoldSec - 300f) < 0.5f, "hold slider full-right -> max");
        s.MouseMove((hx0 + hx1) / 2f, hcy, win);
        check.Expect(System.Math.Abs(win.HoldSec - 150f) < 2f, "hold slider drag to mid -> ~half");
        check.Expect(System.Math.Abs(Find(s, SetAct.SliderHold).Norm - 0.5f) < 0.02f, "drag updates the drawn row's Norm");
        check.Expect(s.MouseUp(), "slider release consumed");
        float fadeBefore = win.FadeSec;
        PressRow(s, win, Find(s, SetAct.SliderFade));
        check.Expect(win.FadeSec == fadeBefore, "click on a slider label leaves the value untouched");

        // Inline numeric entry: value box -> commit / clamp / cancel / keep-on-empty / click-away.
        void ClickValueBox()
        {
            Row hr = Find(s, SetAct.SliderHold);
            Reveal(s, hr);
            s.Panel.ValueRect(hr, s.Panel.RowTop(hr), out float vx0, out float vx1, out float vy0, out float vy1);
            s.MouseDown((vx0 + vx1) / 2f, (vy0 + vy1) / 2f, win);
        }
        ClickValueBox();
        check.Expect(s.Editing, "clicking a value box starts a field edit");
        s.EditInsert("42"); s.CommitEdit(win);
        check.Expect(!s.Editing && System.Math.Abs(win.HoldSec - 42f) < 0.01f && Find(s, SetAct.SliderHold).Value == "42", "typed value commits and the value text follows");
        ClickValueBox(); s.EditInsert("999"); s.CommitEdit(win);
        check.Expect(System.Math.Abs(win.HoldSec - 300f) < 0.01f, "typed value clamps to max");
        ClickValueBox(); s.EditInsert("10"); s.CancelEdit();
        check.Expect(!s.Editing && System.Math.Abs(win.HoldSec - 300f) < 0.01f, "cancel leaves the value untouched");
        ClickValueBox(); s.CommitEdit(win);
        check.Expect(System.Math.Abs(win.HoldSec - 300f) < 0.01f, "empty commit keeps the old value");
        ClickValueBox(); s.EditInsert("77");
        PressRow(s, win, Find(s, SetAct.AnimOn));
        check.Expect(!s.Editing && System.Math.Abs(win.HoldSec - 77f) < 0.01f, "click-away commits the edit");
        PressRow(s, win, Find(s, SetAct.AnimOn));

        // Dropdown rows: the box opens the list; picking an item sets the enum by index.
        Row alR = Find(s, SetAct.CycleAlign);
        check.Expect(alR.Ctl == KnobKind.Dropdown && alR.Choices!.Length == 4 && alR.Selected == (int)win.Align, "alignment is a 4-choice dropdown seeded from the window");
        PressBox(s, win, alR);
        check.Expect(s.Panel.Drop.Open && win.Align == TextAlign.Left, "pressing the box opens the list without changing the value");
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)TextAlign.Center) + 2, win); s.MouseUp();
        check.Expect(!s.Panel.Drop.Open && win.Align == TextAlign.Center, "picking Center sets the alignment and closes the list");
        check.Expect(Find(s, SetAct.CycleAlign).Selected == (int)TextAlign.Center, "the relaid row shows the pick");
        PressBox(s, win, Find(s, SetAct.CycleFmt));
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)TsFormat.Hour12) + 2, win); s.MouseUp();
        check.Expect(win.TimeFormat == TsFormat.Hour12, "time-format pick sets the enum");

        // Font rows: the per-window size ladder (UI default + every point) and the typeface list; a pick raises
        // FontChanged for the shell and writes the window, never a knob.
        Row fs = Find(s, SetAct.SliderFont);
        check.Expect(fs.Ctl == KnobKind.Dropdown && fs.Choices!.Length == TextRig.MaxFontPx - TextRig.MinFontPx + 2 && fs.Selected == 0 && win.FontPx == 0,
          "text size is a dropdown over UI default + the whole point ladder, at UI default");
        PressBox(s, win, fs);
        check.Expect(s.Panel.Drop.Open && s.Panel.Drop.List.Overflows && s.Panel.Drop.Searching, "the ladder scrolls and searches");
        s.Panel.Drop.List.Offset = 0f;
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop(3) + 2, win); s.MouseUp();
        check.Expect(!s.Panel.Drop.Open && win.FontPx == TextRig.MinFontPx + 2 && s.FontChanged, "picking the third size sets MinFontPx + 2 and flags the shell");
        check.Expect(Find(s, SetAct.SliderFont).Selected == 3, "the relaid row shows the size");
        s.FontChanged = false;
        PressBox(s, win, Find(s, SetAct.SliderFont));
        s.Panel.Drop.List.Offset = 0f;
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop(0) + 2, win); s.MouseUp();
        check.Expect(win.FontPx == 0 && s.FontChanged, "UI default sets 0");
        s.FontChanged = false;
        Row tf = Find(s, SetAct.Typeface);
        check.Expect(tf.Choices == Typefaces && tf.Selected == 0 && win.Typeface == "", "typeface row: UI default at an unset window");
        PressBox(s, win, tf);
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop(2) + 2, win); s.MouseUp();
        check.Expect(win.Typeface == "DejaVu Serif" && s.FontChanged && Find(s, SetAct.Typeface).Selected == 2, "a typeface pick writes the name and flags the shell");
        s.FontChanged = false;
        win.Typeface = "nosuch";
        s.Layout(win);
        check.Expect(Find(s, SetAct.Typeface).Selected == 0, "an unknown typeface shows as UI default");
        win.Typeface = "";

        // Tell auto-open: the pick is handed to the shell AND shows on the relaid row at once (the knob is set later).
        Row tellOpen = Find(s, SetAct.CycleTellOpen);
        check.Expect(tellOpen.Selected == ChatKnobs.TellOpen.V, "tell auto-open seeds from the knob");
        PressBox(s, win, tellOpen);
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop(2) + 2, win); s.MouseUp();
        check.Expect(s.TellOpenPicked == 2 && Find(s, SetAct.CycleTellOpen).Selected == 2, "a pick is handed to the shell and the row shows it before the knob moves");
        s.TellOpenPicked = -1;

        // An outside press closes the list and reaches nothing beneath.
        PressBox(s, win, Find(s, SetAct.CycleTellOpen));
        check.Expect(s.Panel.Drop.Open, "tell auto-open list opened");
        Row outl = Find(s, SetAct.OutlineOn);
        int outlBefore = win.Outline;
        PressRow(s, win, outl);
        check.Expect(!s.Panel.Drop.Open && win.Outline == outlBefore && s.TellOpenPicked == -1, "outside press closes the list, toggles nothing");
        check.Expect(outl.Help.Length > 0 && outl.H > tgl.H, "help rows wrap their help under the label; a row without help is compact");

        // No colour rows: the look is shared (ui.look / ui.palette), this window only shapes the fill.
        check.Expect(s.Rows.TrueForAll(r => r.Ctl != KnobKind.Swatch && r.Kind != KnobPanel.RowKind.Channel),
          "the chat gear carries no colour swatch or channel row");

        // Close button, click-off.
        Open(s, win);
        s.MouseDown(s.X + s.W - 1.2f * s.Panel.Em - 2, s.Y + 2, win);
        check.Expect(!s.Open, "close-X closes");
        Open(s, win);
        check.Expect(s.MouseDown(s.X + 5, s.Y + s.H + 40, win) && !s.Open && s.MouseUp(), "click-off closes and owns the press (the shell saves on the release)");
        s.Reset();

        // ---- Tab editing as categories ----
        Open(s, win);
        check.Expect(win.Tabs.Count == 1, "pre-tab: one default tab");
        PressCat(s, win, 2);   // Add new tab...
        check.Expect(win.Tabs.Count == 2 && win.Tabs[1].Label == "All" && win.Tabs[1].ChannelMask == 0xffffffff, "add creates an All-channel tab");
        check.Expect(s.Panel.Category == 2 && s.CategoryTab == 1 && s.Panel.Categories.Length == 4 && s.Panel.Categories[3] == AddTabCategory,
          "the new tab's category is selected; add moved down");
        check.Expect(s.Rows.FindAll(r => r.Act == SetAct.TabChannel && r.Key == "1").Count == FilterChannel.Editable().Length, "the tab category shows the channel toggles");
        check.Expect(Find(s, SetAct.ToggleBit) == null, "...and none of the window rows");

        // Channel toggle: flip the Talk bit (0x001) and verify XOR preserves other bits.
        uint maskBefore = win.Tabs[1].ChannelMask;
        PressRow(s, win, s.Rows.Find(r => r.Act == SetAct.TabChannel && r.Key == "1" && r.Bit == 0x001)!);
        check.Expect(win.Tabs[1].ChannelMask == (maskBefore ^ 0x001), "channel toggle XORs exactly its bit");
        check.Expect((win.Tabs[1].ChannelMask & ~0x001u) == (maskBefore & ~0x001u), "other bits unchanged");

        // Label edit: press the label's box (seeded with the name), retype, commit; the category entry follows.
        PressRow(s, win, Find(s, SetAct.TabLabel, "1"));
        check.Expect(!s.Editing, "a press beside a tab label's box edits nothing");
        PressBox(s, win, Find(s, SetAct.TabLabel, "1"));
        check.Expect(s.Editing && s.EditText == "All", "pressing a tab label's box starts the edit seeded with the name");
        for (int i = 0; i < 3; i++) s.EditBackspace();
        s.EditInsert("Custom"); s.CommitEdit(win);
        check.Expect(win.Tabs[1].Label == "Custom" && Find(s, SetAct.TabLabel, "1").Value == "Custom" && s.Panel.Categories[2] == "Custom",
          "label edit commits the typed text and renames the category");
        PressBox(s, win, Find(s, SetAct.TabLabel, "1"));
        for (int i = 0; i < 6; i++) s.EditBackspace();
        s.CommitEdit(win);
        check.Expect(win.Tabs[1].Label == "Custom" && !s.Editing, "empty label commit keeps old label");

        check.Expect(s.Rows.Find(r => r.Ctl == KnobKind.Text && r.Act != SetAct.TabLabel) == null, "the tell pin is not a setting (no pin row)");

        // The header's Copy this window hands the shell a request.
        s.Panel.HeaderButtonRect(out float cbx0, out float cbx1, out float cby0, out float cby1);
        s.MouseDown((cbx0 + cbx1) / 2f, (cby0 + cby1) / 2f, win); s.MouseUp();
        check.Expect(s.CopyWindowRequested && s.Open, "Copy this window requests a copy and stays open");
        s.CopyWindowRequested = false;

        // Close this window: Main has no row; a closeable window needs two presses, any other press disarms.
        PressCat(s, win, 0);
        check.Expect(Find(s, SetAct.CloseWindow) == null, "Main has no close row");
        win.Closeable = true; s.Layout(win);
        Row? closeRow = Find(s, SetAct.CloseWindow);
        check.Expect(closeRow != null && closeRow.Text == "Close this window", "closeable window gets the close row");
        PressBox(s, win, closeRow!);
        check.Expect(s.CloseArmed && !s.CloseWindowRequested && Find(s, SetAct.CloseWindow).Text == "Click again to confirm", "first press arms");
        PressRow(s, win, Find(s, SetAct.ToggleBit));
        check.Expect(!s.CloseArmed && Find(s, SetAct.CloseWindow).Text == "Close this window", "another press disarms");
        PressBox(s, win, Find(s, SetAct.CloseWindow)); PressBox(s, win, Find(s, SetAct.CloseWindow));
        check.Expect(s.CloseWindowRequested, "second press requests the close");
        s.Reset(); win.Closeable = false; Open(s, win);

        // Switching categories: the window entry, back to the tab.
        PressCat(s, win, 0);
        check.Expect(s.Panel.Category == 0 && Find(s, SetAct.ToggleBit) != null, "the window entry shows the window rows");
        PressCat(s, win, 1);
        check.Expect(s.CategoryTab == 0 && Find(s, SetAct.TabLabel, "0") != null, "the first tab's entry edits tab 0");

        // Remove tab: present when >1 tab, removes it and returns to the window category.
        PressCat(s, win, 2);
        PressBox(s, win, Find(s, SetAct.TabRemove, "1"));
        check.Expect(win.Tabs.Count == 1 && s.Panel.Category == 0 && s.Panel.Categories.Length == 3, "remove tab removes it; back to the window category");
        PressCat(s, win, 1);
        check.Expect(Find(s, SetAct.TabRemove) == null, "remove button absent at 1 tab");

        // Ephemeral tabs are entirely hidden from settings.
        win.AddTab(new Tab("Tell:Bob", 0x002));
        check.Expect(win.Tabs[^1].Ephemeral, "pre: Tell-only tab is ephemeral");
        Open(s, win);
        check.Expect(s.Panel.Categories.Length == 3, "ephemeral tab: no category");
        win.RemoveTab(1);
        s.Reset();

        // ---- Copy from ----
        Open(s, win, winIndex: 0, winCount: 2);
        Row? copyRow = s.Rows.Find(r => r.Act == SetAct.CopyFrom);
        check.Expect(copyRow != null && copyRow.Text == "Copy from Window 2", "copy row shown for the other window");
        s.MouseMove(s.Panel.List.X + 5, s.Panel.List.Y + 5, win);
        WheelToEnd(s);
        PressBox(s, win, copyRow!);
        check.Expect(s.CopyRequested == 1, "CopyRequested set to the source window index");
        s.CopyRequested = -1;
        Open(s, win, winIndex: 0, winCount: 1);
        check.Expect(s.Rows.Find(r => r.Act == SetAct.CopyFrom) == null, "no copy row when only one window");
        s.Reset();

        // ---- BgMode dropdown + conditional bg2 swatch ----
        check.Expect(win.BgMode == BgMode.Solid, "pre: BgMode starts Solid");
        Open(s, win);
        Row bgmRow = Find(s, SetAct.CycleBgMode);
        check.Expect(bgmRow.Ctl == KnobKind.Dropdown && bgmRow.Choices!.Length == 4, "BgMode is a 4-choice dropdown");
        check.Expect(Find(s, SetAct.SliderBgAngle) == null && Find(s, SetAct.SliderBgStart) == null, "gradient rows hidden when Solid");
        PressBox(s, win, bgmRow);
        check.Expect(s.Panel.Drop.Open && !s.Panel.Drop.List.Overflows, "4 shapes: the list fits");
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)BgMode.Linear) + 2, win); s.MouseUp();
        check.Expect(win.BgMode == BgMode.Linear, "picking Linear sets Linear");
        check.Expect(Find(s, SetAct.SliderBgAngle) != null && Find(s, SetAct.SliderBgEnd) != null, "Linear shows direction + stops");
        Row endRow = Find(s, SetAct.SliderBgEnd);
        check.Expect(System.Math.Abs(endRow.Norm - 1f) < 1e-4f, "End seeds from BgEnd (100%)");
        s.Panel.ApplyValue!(endRow, 40f);
        check.Expect(System.Math.Abs(win.BgEnd - 0.4f) < 1e-4f, "End slider writes BgEnd as a fraction");
        PressBox(s, win, Find(s, SetAct.CycleBgMode));
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)BgMode.Radial) + 2, win); s.MouseUp();
        check.Expect(win.BgMode == BgMode.Radial && Find(s, SetAct.SliderBgAngle) == null && Find(s, SetAct.SliderBgStart) != null, "Radial hides direction, keeps stops");
        PressBox(s, win, Find(s, SetAct.CycleBgMode));
        s.Panel.Drop.List.Offset = 0f;
        s.MouseDown(s.Panel.Drop.X + 5, s.Panel.Drop.ItemTop((int)BgMode.Solid) + 2, win); s.MouseUp();
        check.Expect(win.BgMode == BgMode.Solid, "picking Solid returns to Solid");
        s.Reset();

        // ---- the rows overflow the column and scroll; a hit past the fold is offset-correct ----
        Open(s, win);
        check.Expect(s.Panel.List.Overflows, "the window rows overflow the column");
        Row last = s.Rows[^1];
        check.Expect(s.Panel.RowTop(last) >= s.Panel.List.Y + s.Panel.List.H, "pre: the last row lies past the box");
        s.MouseMove(s.Panel.List.X + 5, s.Panel.List.Y + 5, win);
        WheelToEnd(s);
        float lastTop = s.Panel.RowTop(s.Rows[^1]);
        check.Expect(lastTop >= s.Panel.List.Y && lastTop + s.Rows[^1].H <= s.Panel.List.Y + s.Panel.List.H + 0.01f, "wheeling to the end brings the last row into the box");
        Row scrolledRow = s.Rows[^1];
        check.Expect(s.MouseDown(s.Panel.List.X + 5, s.Panel.RowTop(scrolledRow) + 2, win) && s.Open,
          "a press on a row scrolled into the box is offset-correct");
        s.MouseUp();
        {
            var vb = new VertexBatch(body.SolidU, body.SolidV);
            s.Panel.Emit(vb);
            long alloc0 = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); s.Panel.Emit(vb); }
            long delta = System.GC.GetAllocatedBytesForCurrentThread() - alloc0;
            check.Expect(delta == 0, $"100 draws (scrolled) allocate nothing ({delta} bytes)");
        }
        s.Reset();

        // ---- 12 tabs: the category column overflows and scrolls; a scrolled-to entry picks ----
        var tabbed = new ChatWindow(100, 100, 400, 300, new[] { new Tab("All", 0xffffffff) }) { AnimEnabled = false };
        for (int i = 1; i < 12; i++) tabbed.AddTab(new Tab(new string('W', 32), 0xffffffff));
        s.OpenAt(tabbed, 1280, 400, 20f, body, small); s.MouseUp();
        check.Expect(s.Panel.Categories.Length == 14 && s.Panel.Cats.Overflows, "12 tabs in a 400 px viewport: the categories overflow");
        s.MouseMove(s.Panel.Cats.X + 5, s.Panel.Cats.Y + 5, tabbed);
        WheelToEnd(s);
        float navTop = s.Panel.Cats.ToScreen(0.4f * s.Panel.Em + 12 * (body.LineHeight + 1.2f * s.Panel.Em));
        check.Expect(navTop >= s.Panel.Cats.Y && navTop < s.Panel.Cats.Y + s.Panel.Cats.H, "wheeling the column brings the last tab's entry into view");
        s.MouseDown(s.Panel.Cats.X + 5, navTop + 2, tabbed); s.MouseUp();
        check.Expect(s.CategoryTab == 11 && Find(s, SetAct.TabLabel, "11").Value.Length == 32, "a scrolled-to entry picks its tab (offset-correct hit)");
        s.Reset();

        Typefaces = realFaces;
        log.Info(check.Ok ? "SettingsWindow.SelfCheck: passed" : "SettingsWindow.SelfCheck: FAILED");
    }
}
