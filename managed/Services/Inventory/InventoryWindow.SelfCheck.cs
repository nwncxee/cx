using System;
using System.Collections.Generic;
using System.Diagnostics;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Inventory;

internal sealed partial class InventoryWindow
{
    /// <summary>Off-device: the model's fit rules, the layout at two scales and under a server policy, every drop rule,
    /// the page paging and the scrolled view, drops onto a tab, ctrl-click quick moves, the page stream (what is asked for
    /// when, loaders, blank pages refusing drops), the loot pane (placement, take / put / merge, its scroll and emit), the
    /// skin's part order, the search dim (Ctrl+F), stack counts, a zero-alloc emit, and the window's TLK words (hand-written stand-ins).</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Func<long> clock = Clock;
        long now = 1_000_000;
        Clock = () => now;
        try { Run(check); }
        finally { Clock = clock; ServerPolicy.Clear(); }
        log.Info(check.Ok ? "InventoryWindow.SelfCheck: passed" : "InventoryWindow.SelfCheck: FAILED");

        // Quads of a batch wholly inside a rect that sample the atlas (a fill samples the solid texel, (0, 0) here).
        static int Glyphs(VertexBatch b, float x0, float y0, float x1, float y1)
        {
            ReadOnlySpan<float> v = b.AsSpan();
            int n = 0;
            for (int q = 0; q + 6 * VertexBatch.Floats <= v.Length; q += 6 * VertexBatch.Floats)
            {
                bool inside = true, textured = false;
                for (int k = 0; k < 6; k++)
                {
                    int o = q + k * VertexBatch.Floats;
                    inside &= v[o] >= x0 && v[o] <= x1 && v[o + 1] >= y0 && v[o + 1] <= y1;
                    textured |= v[o + 2] != 0f || v[o + 3] != 0f;
                }
                if (inside && textured) n++;
            }
            return n;
        }

        void Run(Checker check)
        {
            ServerPolicy.Clear();
            var w = new InventoryWindow();
            var acts = new List<InvAction>();
            w.Act = acts.Add;
            w.NameOf = oid => oid == 101 ? "Longsword" : oid is 102 or 103 or 202 ? "Arrows" : "Ring of Protection";
            w.Word = strref => strref switch { 123 => "Head", 3731 => "Gepäck", 7260 => "Suchen", 6685 => "Behälter", _ => "?" };
            InventoryModel m = w.Model;
            m.Items[0] = new InvItem { Oid = 101, X = 0, Y = 0, W = 1, H = 3, SlotMask = 0x10 | 0x20, BaseId = 1 };         // not-an-offset: slot masks
            m.Items[1] = new InvItem { Oid = 102, X = 2, Y = 0, W = 1, H = 2, SlotMask = 0x800, BaseId = 20, Stackable = true, Stack = 20 };   // not-an-offset: arrows
            m.Items[2] = new InvItem { Oid = 103, X = 4, Y = 6, W = 1, H = 2, SlotMask = 0x800, BaseId = 20, Stackable = true, Stack = 5 };
            m.Count = 3;
            Array.Fill(m.Fresh, true);

            var ci = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            check.Expect(WeightText(1234, "150", false, "lb") == "123 / 150 lb", "pounds: the stock rounding over the stock max");
            check.Expect(WeightText(1234, "150", true, "lb") == "56.0 / 68.0 kg", "kilograms: 123.4 lb = 56.0 kg, 150 lb = 68.0 kg");
            check.Expect(WeightText(5, "", true, "lb") == "0.2 kg", "kilograms without a max label");
            System.Globalization.CultureInfo.CurrentCulture = ci;
            check.Expect(m.Fits(1, 0, 1, 3, InventoryModel.Invalid) && !m.Fits(0, 0, 1, 1, InventoryModel.Invalid), "fit: free vs occupied");
            check.Expect(!m.Fits(0, 5, 1, 2, InventoryModel.Invalid), "fit: a footprint may not cross a page");
            check.Expect(Radial.RadialIcons.ContentFrac(3) == 0.75f && Radial.RadialIcons.ContentFrac(5) == 0.625f && Radial.RadialIcons.ContentFrac(2) == 1f,
                         "icon crop: a 1x3 sheet is 96 of 128 texels, 1x5 160 of 256, 1x2 unpadded");
            check.Expect(m.Fits(0, 0, 1, 3, 101), "fit: an item ignores itself");
            check.Expect(m.FirstFree(2, 2, 1, 6, out int fx, out int fy) && fx == 0 && fy == 10, "first free starts on the asked page, bottom-left first");
            check.Expect(InventoryModel.Flip(0, 1) == 5 && InventoryModel.Flip(7, 3) == 8 && InventoryModel.Flip(InventoryModel.Flip(13, 2), 2) == 13,
                         "row flip: wire row 0 is the page bottom; a 1x3 at wire 7 (page 1, local 1) tops display 8; involution");
            check.Expect(m.FirstFree(2, 2, 1, 1, out fx, out fy) && fy < 6, "first free stays under the page ceiling");

            foreach (float scale in new[] { 1f, 2f })
            {
                w.Layout(scale, false, true, 4000, 4000);
                w.X = 100f; w.Y = 50f;
                w.Show();
                check.Expect(w.S == scale && MathF.Abs(w.W - 372f * scale) < 0.01f, $"x{scale}: width is the vanilla 372 design px × scale");
                var h = w.HitTest(w.X + (6f + 33f * 2 + 5f) * scale, w.Y + (24f + 6f + 198f + 6f + 5f) * scale);
                check.Expect(h.Kind == InvHitKind.Cell && h.X == 2 && h.Y == 0, $"x{scale}: cell (2,0) hit");
                var sl = w.HitTest(w.X + (6f + 234f + 5f) * scale, w.Y + (30f + 5f) * scale);
                check.Expect(sl.Kind == InvHitKind.Slot && InventoryModel.Slots[sl.Index].Id == "head", $"x{scale}: the helmet box sits top right");
                acts.Clear();
                w.MouseButton(true, ButtonLeft, w.X + w.W - (6f + 5f) * scale, w.Y + w.H - (6f + 5f) * scale, 0);
                check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Gold, $"x{scale}: the gold button sits at the bar's right end");
                w.MouseButton(false, ButtonLeft, w.X + w.W - (6f + 5f) * scale, w.Y + w.H - (6f + 5f) * scale, 0);
            }
            w.Layout(3f, false, true, 800, 600);
            check.Expect(w.H <= 600f && w.W <= 800f, "the window shrinks to fit a small screen");

            w.Layout(1f, false, true, 4000, 4000);
            check.Expect(w._title.Label == "Gepäck" && w._search.Label == "Suchen" && w._containerWord == "Behälter",
                         "the title, search box and nameless container read the TLK's Inventory, Search and Container");
            w.X = 0f; w.Y = 0f;
            float Cx(int cx) => (6f + 33f * cx + 10f) * w.S;
            float Cy(int ly) => (24f + 6f + 198f + 6f + 33f * ly + 10f) * w.S;

            // drag the sword from (0,0) to (5,0): a Move to the grabbed cell's origin
            acts.Clear();
            w.MouseButton(true, 1, Cx(0), Cy(1), 0);
            w.MouseMove(Cx(3), Cy(1));
            check.Expect(w.Dragging, "travel past the slop starts a drag");
            w.MouseMove(Cx(5), Cy(1));
            w.MouseButton(false, 1, Cx(5), Cy(1), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Move, 101, X: 5, Y: 0), "drop in a free cell moves, keeping the grab offset");

            acts.Clear();
            w.MouseButton(true, 1, Cx(0), Cy(0), 0);
            w.MouseMove(Cx(2), Cy(0)); w.MouseButton(false, 1, Cx(2), Cy(0), 0);
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused, "a drop onto another item is refused");

            acts.Clear();
            w.SetPage(1);
            check.Expect(w.Page == 1, "page 2 shown");
            w.MouseButton(true, 1, Cx(4), Cy(0), 0);
            w.SetPage(0);
            w.MouseMove(Cx(2), Cy(0)); w.MouseButton(false, 1, Cx(2), Cy(0), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Merge, 102, Other: 103), "arrows onto arrows merge");

            // drop onto slots
            float SlotX(string id) { foreach (SlotDef d in InventoryModel.Slots) if (d.Id == id) return (6f + d.X + 5f) * w.S; return 0f; }
            float SlotY(string id) { foreach (SlotDef d in InventoryModel.Slots) if (d.Id == id) return (30f + d.Y + 5f) * w.S; return 0f; }
            acts.Clear();
            w.MouseButton(true, 1, Cx(0), Cy(0), 0);
            w.MouseMove(SlotX("righthand"), SlotY("righthand")); w.MouseButton(false, 1, SlotX("righthand"), SlotY("righthand"), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Equip, 101, 0x10), "the sword equips to the right hand");   // not-an-offset: slot mask
            acts.Clear();
            w.MouseButton(true, 1, Cx(0), Cy(0), 0);
            w.MouseMove(SlotX("head"), SlotY("head")); w.MouseButton(false, 1, SlotX("head"), SlotY("head"), 0);
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused && acts[0].Text.Contains("head"), "a sword is refused by the head slot, naming it");

            acts.Clear();
            w.MouseButton(true, 1, Cx(0), Cy(2), 0);
            w.MouseMove(Cx(9), Cy(0)); w.MouseButton(false, 1, Cx(9), Cy(0), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Move, 101, X: 9, Y: 0), "a drop past the page's top edge is clamped into it");
            now += Stopwatch.Frequency;
            acts.Clear();
            w.MouseButton(true, 1, Cx(2), Cy(0), 0x1); w.MouseButton(false, 1, Cx(2), Cy(0), 0);   // not-an-offset: KMOD_LSHIFT
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Split, 102, X: 10), "shift-click splits a stack in half");
            now += Stopwatch.Frequency;

            w.MarkPending(new InvAction(InvActKind.Move, 101, X: 5, Y: 0));
            w.ApplyPending();
            check.Expect(m.Items[0].X == 5 && m.Items[0].Ghost, "an in-flight move draws ghosted at its destination");
            acts.Clear();
            w.MouseButton(true, 1, Cx(5), Cy(0), 0); w.MouseMove(Cx(7), Cy(0)); w.MouseButton(false, 1, Cx(7), Cy(0), 0);
            check.Expect(acts.Count == 0, "a ghosted item cannot be dragged again");
            m.Items[0].X = 0; m.Items[0].Ghost = false;
            now += Stopwatch.Frequency * 4;
            w.ApplyPending();
            check.Expect(m.Items[0].X == 0 && !m.Items[0].Ghost, "an unanswered move is forgotten after its timeout");

            // unequip by drag, and drop outside the window
            m.Equipped[6] = new InvItem { Oid = 104, X = -1, Y = -1, W = 2, H = 2, SlotMask = 0x1 };   // not-an-offset: head mask
            acts.Clear();
            w.MouseButton(true, 1, SlotX("head"), SlotY("head"), 0);
            w.MouseMove(Cx(7), Cy(3)); w.MouseButton(false, 1, Cx(7), Cy(3), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Unequip, 104, X: 7, Y: 3), "a worn helmet unequips to the cell (grab at its centre)");
            acts.Clear();
            w.MouseButton(true, 1, Cx(0), Cy(0), 0);
            check.Expect(!w.MouseMove(5000f, 5000f), "a drag outside the window leaves motion to the engine's hover");
            w.MouseButton(false, 1, 5000f, 5000f, 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Drop, 101, 0, 5000, 5000), "released outside the window: a world drop at the release point, from the backpack");
            acts.Clear();
            w.MouseButton(true, 1, SlotX("head"), SlotY("head"), 0);
            w.MouseMove(5000f, 4000f); w.MouseButton(false, 1, 5000f, 4000f, 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Drop, 104, 0x1, 5000, 4000), "a worn item dropped outside names its slot (no barter from a slot)");

            // clicks
            acts.Clear();
            w.MouseButton(true, 1, Cx(0), Cy(0), 0); w.MouseButton(false, 1, Cx(0), Cy(0), 0);
            now += Stopwatch.Frequency / 10;
            w.MouseButton(true, 1, Cx(0), Cy(0), 0); w.MouseButton(false, 1, Cx(0), Cy(0), 0);
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Use, "a double-click uses");
            now += Stopwatch.Frequency;
            acts.Clear();
            w.MouseButton(true, 3, Cx(0), Cy(0), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Radial, 101, X: (int)Cx(0), Y: (int)Cy(0)), "a right-click opens the item radial at the click");
            acts.Clear();
            w.MouseButton(true, 1, Cx(0), Cy(0), 0x40); w.MouseButton(false, 1, Cx(0), Cy(0), 0);   // not-an-offset: KMOD_LCTRL
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Equip, 101, 0x10), "ctrl-click equips to the first empty slot that takes it");   // not-an-offset: slot mask
            now += Stopwatch.Frequency;
            acts.Clear();
            w.MouseButton(true, 1, SlotX("head"), SlotY("head"), 0x40); w.MouseButton(false, 1, SlotX("head"), SlotY("head"), 0);   // not-an-offset: KMOD_LCTRL
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Unequip && acts[0].Oid == 104, "ctrl-click on a worn item unequips it");
            now += Stopwatch.Frequency;

            // a drag over a tab turns to that page; a drop on a tab takes the first free cell from that page on
            float TabX = (6f + 330f + 4f + 13f) * w.S;
            float TabY(int t) => (234f + 33f * t + 16f) * w.S;
            acts.Clear();
            w.MouseButton(true, 1, Cx(0), Cy(0), 0); w.MouseMove(Cx(3), Cy(0));
            w.MouseMove(TabX, TabY(2));
            check.Expect(w.Page == 2 && acts.Count == 0, "a drag over tab III turns to page 3 (the server hears only what the stream asks)");
            acts.Clear();
            w.MouseButton(false, 1, TabX, TabY(2), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Move, 101, X: 0, Y: 15), "a drop on tab III moves to that page's first free cell (bottom-left, as stock)");
            w.SetPage(0);
            now += Stopwatch.Frequency;

            // the scrolled view: one page tall, gliding through all six; the scrollbar replaces the tabs
            w.Layout(1f, true, true, 4000, 4000);
            void Frames(int k) { for (int i = 0; i < k; i++) { now += Stopwatch.Frequency / 60; w.Tick(); } }
            w.Tick();
            check.Expect(w.Scrolled && !w._tabs.On && w._scroll.On && w.Top == 0f && MathF.Abs(w.W - 372f) < 0.01f, "scrolled: a scrollbar where the tabs were, the vanilla width");
            w.MouseMove(Cx(1), Cy(1));
            w.MouseWheel(-1);
            now += Stopwatch.Frequency / 60; w.Tick();
            check.Expect(w.Top > 0f && w.Top < 2f, "a wheel notch glides, not jumps");
            Frames(60);
            check.Expect(w.Top == 2f && w.HitTest(Cx(1), Cy(0)) is { Kind: InvHitKind.Cell, Y: 2 }, "the glide lands two rows down; the hit follows");
            acts.Clear(); w.MouseWheel(-1); Frames(60);
            check.Expect(w.Top == 4f && w.Page == 1 && acts.Count == 0, "past half a page tab II is the page in view; nothing is sent");
            float BarX = (6f + 330f + 4f + 9f) * w.S;
            w.MouseButton(true, 1, BarX, (234f + 197f) * w.S, 0);
            check.Expect(w.Top == 30f, "the thumb dragged to the bottom shows page 6");
            w.MouseButton(false, 1, BarX, (234f + 197f) * w.S, 0);
            w.Key(0x4000004B); Frames(60);   // not-an-offset: SDLK_PAGEUP
            check.Expect(w.Top == 24f, "PageUp glides a page up");
            w.SetPage(1); Frames(60);
            w.MouseButton(true, 1, Cx(4), Cy(0), 0); w.MouseMove(Cx(4), Cy(2));
            w.MouseMove(Cx(4), (234f + 1f) * w.S);
            Frames(15);
            check.Expect(w.Dragging && w.Top > 3.5f && w.Top < 4.5f, "a drag at the top edge runs the view up, 8 rows a second");
            w.Key(0x1B);   // not-an-offset: SDLK_ESCAPE
            w.Layout(1f, false, true, 4000, 4000);
            w.SetPage(0);
            check.Expect(!w.Scrolled && w.Top == 0f && w._tabs.On, "tabbed again: tabs, whole pages");

            // the page stream: nothing asked while a request is out; a scrolled view asks only once it rests; a touched page
            // goes first; a blank page loads, refuses drops and is skipped by first-free
            long dwell = Stopwatch.Frequency / 4;
            m.Restart(0);
            check.Expect(!m.Blank(0) && !m.Blank(1) && m.Blank(2), "restarted: a page with a remembered item is not blank, an empty unsent one is");
            w.Layout(1f, true, true, 4000, 4000);
            w.Top = w._target = 13f;
            w.BagView(out int bf, out int bl);
            check.Expect(bf == 2 && bl == 3, "a view from row 13 shows pages 3 and 4");
            check.Expect(m.Want(bf, bl, now, dwell) == -1, "a view that just arrived asks for nothing");
            now += dwell / 2;
            w.Top = w._target = 12f; w.BagView(out bf, out bl);
            check.Expect(m.Want(bf, bl, now, dwell) == -1 && bf == 2 && bl == 2, "still moving: nothing");
            now += dwell;
            check.Expect(m.Want(bf, bl, now, dwell) == 2, "rested on page 3: it is asked for");
            m.Ask(2, now);
            check.Expect(m.Want(bf, bl, now, dwell) == -1, "one request in flight at a time");
            m.Arrived(2);
            check.Expect(m.Fresh[2] && m.Awaiting < 0 && m.Want(bf, bl, now, dwell) == -1, "arrived: the page is fresh, the view wants nothing more");
            w.Touch(new InvAction(InvActKind.Move, 101, X: 0, Y: 30));
            check.Expect(m.Want(bf, bl, now, dwell) == 5, "a move onto page 6 asks for it next, whatever the view");
            m.Ask(5, now); m.Arrived(5);
            w.Touch(new InvAction(InvActKind.Move, 101, X: 0, Y: 31));
            check.Expect(m.Focus < 0, "a move onto the streamed page asks for nothing");
            w.Touch(new InvAction(InvActKind.Take, 201, X: AutoCell, Y: AutoCell, Other: 0x500));   // not-an-offset: the container
            check.Expect(m.Fresh[5] && !m.Fresh[2], "a take the server places makes every other page stale");
            w.Layout(1f, false, true, 4000, 4000);
            w.SetPage(3);
            w.BagView(out bf, out bl);
            check.Expect(m.Want(bf, bl, now, 0) == 3, "tabbed: a tab is asked for when shown");
            acts.Clear();
            w.SetPage(0); w.MouseButton(true, 1, Cx(0), Cy(0), 0); w.SetPage(3); w.MouseMove(Cx(2), Cy(3)); w.MouseButton(false, 1, Cx(2), Cy(3), 0);
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused && acts[0].Text.Contains("loading"), "a blank page refuses a drop");
            check.Expect(m.FirstFree(1, 1, 3, 6, out fx, out fy) && fy / InventoryModel.Rows == 5, "first free skips blank pages");
            var sa = new GlyphAtlas { LineHeight = 18, Ascent = 14 };
            for (int cp = 32; cp < 127; cp++) sa.SetTestGlyph(cp, 8f);
            var su = new VertexBatch(0f, 0f); var so = new VertexBatch(0f, 0f); var sgu = new VertexBatch(0f, 0f); var sgo = new VertexBatch(0f, 0f);
            var slu = new VertexBatch(0f, 0f); var slo = new VertexBatch(0f, 0f);
            var sicons = new InvIcon[64];
            w.Emit(su, so, sgu, sgo, slu, slo, null, sa, sa, sicons);
            int blankVerts = sgo.VertexCount;
            Array.Fill(m.Fresh, true);
            sgo.Clear(); w.Emit(su, so, sgu, sgo, slu, slo, null, sa, sa, sicons);
            check.Expect(blankVerts - sgo.VertexCount == LoaderDots * 6, "a blank page in view draws a loader of ten dots");
            w.SetPage(0);
            m.Restart(0); Array.Fill(m.Fresh, true);

            acts.Clear();
            check.Expect(w.Key(0x1B) && acts.Count == 1 && acts[0].Kind == InvActKind.Close, "Esc closes");   // not-an-offset: SDLK_ESCAPE

            // server policy: hide gold, limit pages, read-only, move a component
            ServerPolicy.Apply("inventory.gold.visible=0\ninventory.backpack.pages=2\ninventory.readonly=1\ninventory.weight.dx=100\ninventory.title.label=Pack");
            check.Expect(w.PolicyStale, "a policy change marks the layout stale");
            w.Layout(1f, true, true, 4000, 4000);
            check.Expect(!ServerPolicy.Set("ui.font", "12"), "policy: a knob outside the opted-in groups is refused");
            check.Expect(!w._gold.On && !w._goldButton.On && w.PageLimit == 2 && w.ReadOnly && w._title.Label == "Pack", "policy: gold (and its button) hidden, 2 pages, read-only, relabelled");
            check.Expect(w._weight.X0 == 106f && w.Scrolled, "policy: the weight moved; two pages still scroll");
            acts.Clear();
            w.MouseButton(true, 1, Cx(0), Cy(0), 0); w.MouseMove(Cx(5), Cy(0)); w.MouseButton(false, 1, Cx(5), Cy(0), 0);
            check.Expect(acts.Count == 0 && !w.Dragging, "read-only: no drag");
            ServerPolicy.Clear();
            w.Layout(1f, false, true, 4000, 4000);

            // the loot pane: a 3-page container, pages 1-2 read, a gem on page 1 and arrows on page 2
            LootModel l = w.Loot;
            l.Reset(0x500, now); l.Pages = 3; l.Fresh[0] = l.Fresh[1] = true;   // not-an-offset: an object id
            l.Items[0] = new InvItem { Oid = 201, X = 0, Y = 0, W = 1, H = 1, BaseId = 30 };
            l.Items[1] = new InvItem { Oid = 202, X = 3, Y = 5, W = 1, H = 2, SlotMask = 0x800, BaseId = 20, Stackable = true, Stack = 10 };   // not-an-offset: arrows
            l.Count = 2;
            now += Stopwatch.Frequency;
            float Lx(int cx) => w.X + w.W + (6f + 33f * cx + 10f) * w.S;
            float Ly(int r) => w.Y + (30f + 33f * r + 10f) * w.S;
            check.Expect(ItemGrid.Flip(0, 1, LootModel.Rows) == 4 && ItemGrid.Flip(7, 2, LootModel.Rows) == 6, "loot rows: wire row 0 is each 5-row page's bottom");
            check.Expect(w.LootShown && w.HitTest(Lx(0), Ly(0)) is { Kind: InvHitKind.LootCell, X: 0, Y: 0 } && w.Contains(Lx(6), Ly(12)),
                         "the loot pane is attached right of the window, 13 rows tall");
            w.Layout(1f, false, true, 700, 4000);
            w.X = 450f; w.ClampOnScreen(700, 4000);
            check.Expect(w.HitTest(w.X - (265f - 16f) * w.S, Ly(0)) is { Kind: InvHitKind.LootCell, X: 0 }, "no room on the right: the pane attaches left");
            w.Layout(1f, false, true, 4000, 4000);
            w.X = 0f;

            acts.Clear();
            w.MouseButton(true, 1, Lx(0), Ly(0), 0); w.MouseMove(Lx(2), Ly(0)); w.MouseMove(Cx(5), Cy(3)); w.MouseButton(false, 1, Cx(5), Cy(3), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Take, 201, X: 5, Y: 3, Other: 0x500), "loot dragged to a backpack cell: a Take there");   // not-an-offset: the container
            acts.Clear();
            w.MouseButton(true, 1, Cx(2), Cy(0), 0); w.MouseMove(Cx(4), Cy(0)); w.MouseMove(Lx(1), Ly(1)); w.MouseButton(false, 1, Lx(1), Ly(1), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Put, 102, X: 1, Y: 1, Other: 0x500), "a backpack item dropped on a loot cell: a Put there");   // not-an-offset: the container
            acts.Clear();
            w.MouseButton(true, 1, Cx(2), Cy(0), 0); w.MouseMove(Cx(4), Cy(0)); w.MouseMove(Lx(3), Ly(5)); w.MouseButton(false, 1, Lx(3), Ly(5), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Merge, 202, Other: 102), "arrows onto the loot's arrows merge");
            acts.Clear();
            w.MouseButton(true, 1, Cx(2), Cy(0), 0); w.MouseMove(Cx(4), Cy(0)); w.MouseMove(Lx(1), Ly(10)); w.MouseButton(false, 1, Lx(1), Ly(10), 0);
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused && acts[0].Text.Contains("loading"), "a loot page not sent yet refuses a drop");
            acts.Clear();
            w.MouseButton(true, 1, SlotX("head"), SlotY("head"), 0); w.MouseMove(Lx(2), Ly(2)); w.MouseButton(false, 1, Lx(2), Ly(2), 0);
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused && acts[0].Text.Contains("Unequip"), "a worn item is refused by the loot, as by the stock grid");
            acts.Clear();
            w.MouseButton(true, 1, Lx(0), Ly(0), 0); w.MouseMove(5000f, 5000f); w.MouseButton(false, 1, 5000f, 5000f, 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Drop, 201, 0, 5000, 5000, Other: 0x500), "loot dragged out: a world drop marked with its container");   // not-an-offset: the container
            now += Stopwatch.Frequency;
            acts.Clear();
            w.MouseButton(true, 1, Lx(0), Ly(0), 0); w.MouseButton(false, 1, Lx(0), Ly(0), 0);
            now += Stopwatch.Frequency / 10;
            w.MouseButton(true, 1, Lx(0), Ly(0), 0); w.MouseButton(false, 1, Lx(0), Ly(0), 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Take, 201, X: AutoCell, Y: AutoCell, Other: 0x500), "a double-click takes, the server picking the cell");   // not-an-offset: the container
            now += Stopwatch.Frequency;
            acts.Clear();
            w.MouseButton(true, 1, Lx(0), Ly(0), 0x40); w.MouseButton(false, 1, Lx(0), Ly(0), 0);   // not-an-offset: KMOD_LCTRL
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Take && acts[0].X == AutoCell, "ctrl-click takes too");
            now += Stopwatch.Frequency;
            acts.Clear();
            w.MouseButton(true, 1, w.X + w.W + 249f * w.S, w.Y + 12f * w.S, 0); w.MouseButton(false, 1, w.X + w.W + 249f * w.S, w.Y + 12f * w.S, 0);
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.CloseLoot, Other: 0x500), "the pane's X closes the container");   // not-an-offset: the container

            w.LootView(out int lf, out int ll);
            check.Expect(lf == 0 && ll == 2 && l.Want(lf, ll, now, dwell) == -1, "the loot view shows all three pages; just shown, nothing asked");
            now += dwell;
            check.Expect(l.Want(lf, ll, now, dwell) == 2, "rested: the page not sent yet is asked for");
            l.Ask(2, now); l.Arrived(2);
            check.Expect(l.Want(lf, ll, now, dwell) == -1 && l.Selected == 2, "all sent: nothing more, however long the view rests (no follow)");
            w.Touch(new InvAction(InvActKind.Put, 102, X: 0, Y: 1, Other: 0x500));   // not-an-offset: the container
            check.Expect(l.Want(lf, ll, now, dwell) == 0, "a Put onto another page asks for it next");
            l.Ask(0, now); l.Arrived(0);
            w.Touch(new InvAction(InvActKind.Take, 202, X: 5, Y: 3, Other: 0x500));   // not-an-offset: the container
            check.Expect(l.Want(lf, ll, now, dwell) == 1 && m.Focus < 0, "a Take off page 2 asks for that page (its item is gone); the backpack's page is streamed");
            l.Ask(1, now); l.Arrived(1);
            w.MarkPending(new InvAction(InvActKind.Put, 201, X: 2, Y: 1, Other: 0x500));   // not-an-offset: the container
            w.ApplyPending();
            check.Expect(l.Items[0].X == 2 && l.Items[0].Y == 1 && l.Items[0].Ghost, "a loot move draws ghosted at its destination");
            l.Items[0].X = 0; l.Items[0].Y = 0; l.Items[0].Ghost = false; m.Items[1].Ghost = false;
            now += Stopwatch.Frequency * 4;
            w.ApplyPending();

            w.MouseMove(Lx(1), Ly(1));
            w.MouseWheel(-1); Frames(60);
            check.Expect(w._lootTop == 2f && w.HitTest(Lx(1), Ly(0)) is { Kind: InvHitKind.LootCell, Y: 2 }, "the wheel over the loot glides it two rows");
            w.MouseWheel(-1); Frames(60);
            check.Expect(w._lootTop == 2f, "the loot view stops at its last row");
            w._lootTop = w._lootTarget = 0f;

            var latlas = new GlyphAtlas { LineHeight = 18, Ascent = 14 };
            for (int cp = 32; cp < 127; cp++) latlas.SetTestGlyph(cp, 8f);
            for (int cp = '0'; cp <= '9'; cp++) latlas.Store(cp, new Glyph { Advance = 8f, W = 6, H = 10, XOff = 1, YOff = -10, U0 = 0.1f, V0 = 0.1f, U1 = 0.2f, V1 = 0.2f });
            var bu = new VertexBatch(0f, 0f); var bo = new VertexBatch(0f, 0f); var bgu = new VertexBatch(0f, 0f); var bgo = new VertexBatch(0f, 0f);
            var blu = new VertexBatch(0f, 0f); var blo = new VertexBatch(0f, 0f);
            var licons = new InvIcon[64];
            Skin lskin = NewSkin();
            // Oracle: the part names, hand-written -- the common chrome first, the inventory's own from NativeChrome.Count.
            check.Expect(lskin.IndexOf("frame") == NativeChrome.Frame && lskin.IndexOf("buttonhot") == NativeChrome.ButtonHot
                         && lskin.IndexOf("thumb") == NativeChrome.Thumb, "the native chrome's parts keep their own indices");
            check.Expect(lskin.IndexOf("cell") == NativeChrome.Count && lskin.IndexOf("slider") == NativeChrome.Slider
                         && lskin.IndexOf("sil." + InventoryModel.Slots[0].Id) == KSil, "the inventory's extras land after them");
            check.Expect(lskin.IndexOf("scrollbar") < 0, "an undeclared part has no index");
            void ClearAll() { bu.Clear(); bo.Clear(); bgu.Clear(); bgo.Clear(); blu.Clear(); blo.Clear(); }
            w.Emit(bu, bo, bgu, bgo, blu, blo, lskin, latlas, latlas, licons);
            ClearAll();
            int ln = w.Emit(bu, bo, bgu, bgo, blu, blo, null, latlas, latlas, licons);
            check.Expect(ln == 5 && Array.FindAll(licons[..ln], q => q.Clip == ClipLoot).Length == 2, "the loot's two items draw, clipped to the loot grid");
            InvIcon arrows = Array.Find(licons[..ln], q => q.Oid == 102);
            check.Expect(Glyphs(bgo, arrows.X + arrows.W * 0.25f, arrows.Y + arrows.H * 0.6f, arrows.X + arrows.W, arrows.Y + arrows.H) == 4,
                         "a stack of 20 shows its count in the icon's bottom-right corner (two glyphs over their shadow)");
            InvIcon gem = Array.Find(licons[..ln], q => q.Oid == 201), loose = Array.Find(licons[..ln], q => q.Oid == 202);
            check.Expect(Glyphs(blo, gem.X + gem.W / 2f, gem.Y + gem.H / 2f, gem.X + gem.W, gem.Y + gem.H) == 0
                         && Glyphs(blo, loose.X, loose.Y + loose.H / 2f, loose.X + loose.W, loose.Y + loose.H) == 4,
                         "in the loot too: a single item shows no count, a stack of 10 does");
            int lootVerts = blo.VertexCount;
            l.Fresh[2] = false;
            ClearAll();
            w.Emit(bu, bo, bgu, bgo, blu, blo, null, latlas, latlas, licons);
            check.Expect(blo.VertexCount - lootVerts == LoaderDots * 6, "a loot page not sent yet draws a loader");
            ClearAll(); w.Emit(bu, bo, bgu, bgo, blu, blo, lskin, latlas, latlas, licons); w.Emit(bu, bo, bgu, bgo, blu, blo, null, latlas, latlas, licons);
            long lb = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) { bu.Clear(); bo.Clear(); bgu.Clear(); bgo.Clear(); blu.Clear(); blo.Clear(); w.Emit(bu, bo, bgu, bgo, blu, blo, lskin, latlas, latlas, licons); w.Emit(bu, bo, bgu, bgo, blu, blo, null, latlas, latlas, licons); }
            check.Expect(GC.GetAllocatedBytesForCurrentThread() - lb == 0, "40 emits with the loot pane allocate nothing");
            w.Search = "arr";
            w.RefreshSearch();
            check.Expect(w._dim.Contains(201) && !w._dim.Contains(202), "search dims the loot too");
            w.Search = "";
            l.Reset(InventoryModel.Invalid, now);
            check.Expect(!w.LootShown && w.HitTest(Lx(0), Ly(0)).Kind == InvHitKind.None, "a closed container hides the pane");

            // search
            check.Expect(w.Key(0x66, 0x40) && w.SearchFocused, "Ctrl+F focuses the search");   // not-an-offset: SDLK_f, KMOD_LCTRL
            w.Key(0x1B);   // not-an-offset: SDLK_ESCAPE
            w.MouseButton(true, 1, w.X + 200f, w.Y + 10f, 0);
            check.Expect(w.SearchFocused, "a click in the box focuses the search");
            w.Type("arr");
            w.RefreshSearch();
            check.Expect(w._dim.Contains(101) && !w._dim.Contains(102), "search dims what doesn't match");
            w.Key(0x0D);   // not-an-offset: SDLK_RETURN
            check.Expect(!w.SearchFocused, "Enter leaves the box");

            // emit: zero alloc once warm, icons for every visible item
            var atlas = new GlyphAtlas { LineHeight = 18, Ascent = 14 };
            for (int cp = 32; cp < 127; cp++) atlas.SetTestGlyph(cp, 8f);
            var under = new VertexBatch(0f, 0f); var over = new VertexBatch(0f, 0f); var gu = new VertexBatch(0f, 0f); var go = new VertexBatch(0f, 0f);
            var lu = new VertexBatch(0f, 0f); var lo = new VertexBatch(0f, 0f);
            var icons = new InvIcon[64];
            Skin skin = NewSkin();
            w.Emit(under, over, gu, go, lu, lo, null, atlas, atlas, icons);
            int nIcons = w.Emit(under, over, gu, go, lu, lo, skin, atlas, atlas, icons);
            check.Expect(nIcons == 3, "an icon per visible item (two on page 1 plus the worn helmet)");
            w.Layout(1f, true, true, 4000, 4000);
            w.Top = w._target = 2f;
            nIcons = w.Emit(under, over, gu, go, lu, lo, null, atlas, atlas, icons);
            check.Expect(Array.Exists(icons, q => q.Oid == 101 && q.Y < w.Y + 234f * w.S) && nIcons == 3, "an item whose top row scrolled out still draws (clipped)");
            w.Layout(1f, false, true, 4000, 4000);
            w.Top = w._target = 0f;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) { under.Clear(); over.Clear(); gu.Clear(); go.Clear(); lu.Clear(); lo.Clear(); w.Emit(under, over, gu, go, lu, lo, null, atlas, atlas, icons); w.Emit(under, over, gu, go, lu, lo, skin, atlas, atlas, icons); }
            check.Expect(GC.GetAllocatedBytesForCurrentThread() - before == 0, "100 emits allocate nothing");
            w.Hide();
            check.Expect(w.Emit(under, over, gu, go, lu, lo, null, atlas, atlas, icons) == 0 && !w.Contains(w.X + 1f, w.Y + 1f), "a hidden window emits nothing and hits nothing");
        }
    }
}
