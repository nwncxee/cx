using System;
using CX.Framework;

namespace CX.Ui;

public sealed partial class CascadeMenu
{
    /// <summary>Off-device proof of the widget's contract: root placement + clamp, hit-test, hover, wrapping key
    /// navigation, lazy children at the parent's right edge (flipped and clamped at the viewport edge), a 30-row
    /// child scrolling to its tail, sibling replacement, Left/Esc unwind, outside press, disabled rows, the
    /// right-button context, the hover dwell, icon rects, a zero-alloc draw, the row drag and the open animation.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        for (char ch = '0'; ch <= '9'; ch++) atlas.SetTestGlyph(ch, 6f);
        for (char ch = 'a'; ch <= 'z'; ch++) atlas.SetTestGlyph(ch, 6f);
        atlas.SetTestGlyph('>', 6f);
        const float vpW = 1920f, vpH = 1080f;

        var root = new CascadeItem[8];
        for (int i = 0; i < 8; i++) root[i] = new CascadeItem("row" + i);
        root[1].Icon = "ir_x";
        root[2].HasChildren = true;
        root[3].Count = "3";
        root[5].Disabled = true;
        root[6].Disabled = true; root[6].HasChildren = true;
        root[7].HasChildren = true;
        var child = new CascadeItem[30];
        for (int i = 0; i < 30; i++) child[i] = new CascadeItem("c" + i);
        child[0].HasChildren = true;
        var grand = new[] { new CascadeItem("g0", "ir_g"), new CascadeItem("g1") { IconHandle = (IntPtr)0x7f01 } };

        var m = new CascadeMenu();
        check.Expect(!m.Open && m.MouseDown(0f, 0f, 1).Kind == CascadeActionKind.None && m.Key(KeyDown).Kind == CascadeActionKind.None
          && m.Tick(0).Kind == CascadeActionKind.None && !m.MouseMove(0f, 0f) && !m.MouseWheel(1), "closed: nothing consumed");

        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        float ih = m.ItemH;
        var l0 = m.Level(0);
        check.Expect(m.Open && m.Depth == 1 && l0.X == 100f && l0.Y == 100f && l0.H == 8 * ih && !l0.List.Overflows, "8 rows open at the point, no scroll");
        check.Expect(ih == atlas.LineHeight + 2f * Pad && l0.HasIcons && l0.Mark == '>' && l0.W > ih + 30f && l0.W <= MaxW, "row height from the face, icon column, '>' fallback mark, measured width");
        check.Expect(m.Contains(110f, 101f) && !m.Contains(99f, 101f) && m.LevelAt(110f, 101f) == 0, "contains + level hit-test");
        check.Expect(m.ItemAt(0, 110f, 100f + ih * 2f + 1f) == 2 && m.RowTop(0, 2) == 100f + 2f * ih, "row hit-test and row top");
        check.Expect(m.MouseMove(110f, 100f + ih * 2f + 1f) && l0.Hover == 2 && !m.MouseMove(111f, 100f + ih * 2f + 1f), "hover tracks the row; same row reports no change");

        // Keys: Down/Up move the hover and wrap; Enter/Right on a leaf activate.
        check.Expect(m.Key(KeyDown).Kind == CascadeActionKind.None && l0.Hover == 3, "Down moves the hover");
        m.Key(KeyUp); m.Key(KeyUp); m.Key(KeyUp);
        check.Expect(l0.Hover == 0 && m.Key(KeyUp).Kind == CascadeActionKind.None && l0.Hover == 7, "Up wraps from the first row to the last");
        check.Expect(m.Key(KeyDown).Kind == CascadeActionKind.None && l0.Hover == 0, "Down wraps from the last row to the first");
        m.Key(KeyDown); m.Key(KeyDown); m.Key(KeyDown); m.Key(KeyDown);
        var act = m.Key(KeyReturn);
        check.Expect(l0.Hover == 4 && act.Kind == CascadeActionKind.Activate && act.Level == 0 && act.Row == 4 && m.Open, "Enter on a leaf activates it and leaves the menu open");
        check.Expect(m.Key(KeyRight).Kind == CascadeActionKind.Activate && m.Key(KeyKpEnter).Kind == CascadeActionKind.Activate, "Right / keypad Enter activate a leaf too");
        check.Expect(m.Key(KeyLeft).Kind == CascadeActionKind.None && m.Open && m.Depth == 1, "Left at the root does nothing");

        // Submenu row: a press asks for children; OpenChild places them at the right edge, top on the row.
        var req = m.MouseDown(110f, m.RowTop(0, 2) + 1f, 1);
        check.Expect(req.Kind == CascadeActionKind.RequestChildren && req.Level == 0 && req.Row == 2 && m.Open, "press on a submenu row requests its children");
        check.Expect(m.MouseUp().Kind == CascadeActionKind.None, "its release is nothing");
        check.Expect(m.OpenChild(0, 2, child) && m.Depth == 2, "OpenChild opens level 1");
        var l1 = m.Level(1);
        check.Expect(l1.X == l0.X + l0.W && l1.Y == m.RowTop(0, 2) && l1.ParentRow == 2 && l0.Hover == 2, "the child hangs off the parent's right edge, top on its row");
        check.Expect(l1.H == MaxVisible * ih && l1.List.Overflows && l1.List.MaxOffset == (30 - MaxVisible) * ih, "30 rows cap at MaxVisible and scroll");
        check.Expect(m.MouseDown(110f, m.RowTop(0, 2) + 1f, 1).Kind == CascadeActionKind.None && m.Depth == 2 && m.MouseUp().Kind == CascadeActionKind.None, "pressing the owning row again keeps its child");
        check.Expect(!m.OpenChild(0, 9, child) && !m.OpenChild(3, 0, child), "bad row / level refused");

        // Wheel in the child reaches the tail; keys scroll the hover into view.
        check.Expect(m.MouseMove(l1.X + 10f, l1.Y + 1f) && l1.Hover == 0 && l0.Hover == 2, "hover in the child leaves the parent's owning row lit");
        int notches = 0;
        while (m.MouseWheel(-1)) notches++;
        check.Expect(notches == 30 - MaxVisible && l1.List.Offset == l1.List.MaxOffset && l1.Hover == -1, $"wheel scrolls one row per notch to the tail ({notches})");
        check.Expect(m.ItemAt(1, l1.X + 10f, l1.Y + l1.H - 1f) == 29, "the last row sits at the child's bottom");
        check.Expect(m.Key(KeyDown).Kind == CascadeActionKind.None && l1.Hover == 0 && l1.List.Offset == 0f, "Down from no hover picks row 0 and scrolls it into view");
        check.Expect(m.Key(KeyUp).Kind == CascadeActionKind.None && l1.Hover == 29 && l1.List.Offset == l1.List.MaxOffset, "Up wraps to the tail and scrolls to it");
        check.Expect(m.Key(KeyReturn).Kind == CascadeActionKind.Activate && m.Key(KeyReturn).Level == 1, "Enter in the child activates at level 1");
        check.Expect(m.ItemAt(1, l1.X + l1.W - 2f, l1.Y + 5f) == -1, "the gutter is not a row");
        check.Expect(l1.List.ThumbRect(out float tt, out _) && m.MouseDown(l1.X + l1.W - 2f, tt + 1f, 1).Kind == CascadeActionKind.None && l1.List.Dragging, "thumb press grabs, picks nothing");
        m.MouseMove(l1.X + l1.W - 2f, tt + 1f - l1.H);
        check.Expect(m.MouseUp().Kind == CascadeActionKind.None && !l1.List.Dragging && l1.List.Offset == 0f, "thumb dragged to the top shows the head");

        // Grandchild, then a sibling at the root replaces the whole subtree.
        l1.Hover = 0;
        check.Expect(m.Key(KeyRight).Kind == CascadeActionKind.RequestChildren && m.OpenChild(1, 0, grand) && m.Depth == 3, "Right on a submenu row + OpenChild nests a third level");
        check.Expect(m.Level(2).X == l1.X + l1.W && m.Level(2).Y == m.RowTop(1, 0), "the grandchild hangs off the child");
        check.Expect(m.OpenChild(0, 7, child) && m.Depth == 2 && l1.ParentRow == 7 && l1.Y == m.RowTop(0, 7) && l0.Hover == 7, "opening a sibling closes the first child and its subtree");
        check.Expect(m.OpenChild(1, 0, grand) && m.Depth == 3, "re-nest");
        check.Expect(m.Key(KeyLeft).Kind == CascadeActionKind.None && m.Depth == 2, "Left closes the deepest level");
        check.Expect(m.Key(KeyEscape).Kind == CascadeActionKind.None && m.Depth == 1, "Esc closes one level");
        check.Expect(m.Key(KeyEscape).Kind == CascadeActionKind.CloseAll && !m.Open && m.Depth == 0, "Esc at the root closes all");

        // Press on a different parent row closes the child at once; press outside closes all.
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        m.OpenChild(0, 2, child);
        check.Expect(m.MouseDown(110f, m.RowTop(0, 3) + 1f, 1).Kind == CascadeActionKind.None && m.Depth == 1, "a press on another parent row closes the child, activates nothing yet");
        var rel = m.MouseUp();
        check.Expect(rel.Kind == CascadeActionKind.Activate && rel.Level == 0 && rel.Row == 3 && m.Open, "the release on the same leaf activates it");
        check.Expect(m.MouseUp().Kind == CascadeActionKind.None, "a second release is nothing");
        m.MouseDown(110f, m.RowTop(0, 3) + 1f, 1);
        m.MouseMove(110f + DragPx - 1f, m.RowTop(0, 3) + DragPx);
        check.Expect(!m.RowDrag && m.MouseUp().Kind == CascadeActionKind.Activate, "jitter under DragPx is still a click");
        m.OpenChild(0, 2, child);
        check.Expect(m.MouseDown(5f, 5f, 1).Kind == CascadeActionKind.CloseAll && !m.Open, "press outside every level closes all");

        // Row drag: armed by a left press, started past DragPx, the gap under the pointer is the target,
        // hover/dwell/closes hold, the release drops; Esc or a release off every level cancels.
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        m.OpenChild(0, 2, child);
        float r4 = m.RowTop(0, 4) + 1f;
        check.Expect(m.MouseDown(110f, r4, 1).Kind == CascadeActionKind.None && !m.RowDrag && m.Depth == 1, "a left press arms, does not drag; the child closed");
        check.Expect(m.MouseMove(110f + DragPx, r4) && !m.RowDrag && m.MouseMove(110f + DragPx + 1f, r4) && m.RowDrag, "the drag starts one past DragPx");
        check.Expect(m.DropLevel == 0 && m.DropIndex == 4 && m.MouseMove(110f, m.RowTop(0, 1) + ih / 2f + 1f) && m.DropIndex == 2, "the gap rounds to the nearest row edge");
        check.Expect(l0.Hover == 4 && m.Tick(100).Kind == CascadeActionKind.None && m.Tick(100 + DefaultDwellMs).Kind == CascadeActionKind.None, "hover and the dwell hold while dragging");
        m.MouseMove(110f, m.RowTop(0, 2) + 1f);
        check.Expect(m.Key(KeyDown).Kind == CascadeActionKind.None && l0.Hover == 4, "keys are inert while dragging");
        check.Expect(m.MouseMove(110f, m.RowTop(0, 0) - 5f) && m.DropLevel == -1 && m.MouseMove(110f, m.RowTop(0, 7) + ih - 1f) && m.DropIndex == 8, "off the level = no target; below the last row = gap n");
        var drop = m.MouseUp();
        check.Expect(drop.Kind == CascadeActionKind.Drop && drop.Level == 0 && drop.Row == 4 && drop.ToLevel == 0 && drop.ToIndex == 8 && !m.RowDrag && m.Open, "the release drops into the gap");
        m.MouseDown(110f, r4, 1); m.MouseMove(110f, r4 + 2f * DragPx);
        check.Expect(m.RowDrag && m.MouseMove(5f, 5f) && m.DropLevel == -1 && m.MouseUp().Kind == CascadeActionKind.None && !m.RowDrag, "a release off every level cancels");
        m.MouseDown(110f, r4, 1); m.MouseMove(110f, r4 + 2f * DragPx);
        check.Expect(m.RowDrag && m.Key(KeyEscape).Kind == CascadeActionKind.None && !m.RowDrag && m.Open && m.MouseUp().Kind == CascadeActionKind.None, "Esc cancels the drag, the menu stays");
        m.OpenChild(0, 2, child);
        m.MouseDown(l1.X + 10f, m.RowTop(1, 3) + 1f, 1); m.MouseMove(l1.X + 10f, m.RowTop(1, 3) + 2f * DragPx);
        check.Expect(m.RowDrag && m.Depth == 2 && m.MouseMove(110f, m.RowTop(0, 1) + 1f) && m.Depth == 2 && m.DropLevel == 0 && m.DropIndex == 1, "dragging from the child over the root keeps the child open, targets the root gap");
        check.Expect(m.OpenChild(1, 0, grand) && m.MouseMove(m.Level(2).X + 5f, m.Level(2).Y + 1f) && m.DropLevel == -1 && m.MouseMove(l1.X + 5f, l1.Y + 1f) && m.DropLevel == 1, "a level that is neither the source nor the root is no target");
        m.Level(2).DropTarget = true;
        check.Expect(m.MouseMove(m.Level(2).X + 5f, m.Level(2).Y + 1f) && m.DropLevel == 2 && m.DropIndex == 0, "a level flagged as a drop target takes a drop from another level");
        check.Expect(m.OpenChild(1, 0, grand) && !m.Level(2).DropTarget, "re-opening a level clears the flag");
        m.MouseMove(110f, m.RowTop(0, 1) + 1f);
        drop = m.MouseUp();
        check.Expect(drop.Kind == CascadeActionKind.Drop && drop.Level == 1 && drop.Row == 3 && drop.ToLevel == 0 && drop.ToIndex == 1, "a cross-level drop reports both ends");
        m.CloseTo(0);
        m.OpenChild(0, 2, child);
        m.Level(1).DropTarget = true;
        m.MouseDown(110f, r4, 1);
        check.Expect(m.Depth == 2 && m.RowDrag == false && m.MouseMove(110f, r4 + 2f * DragPx) && m.RowDrag && m.MouseMove(l1.X + 5f, l1.Y + 1f) && m.DropLevel == 1,
            "a press on another row keeps a drop-target child open, so the drag can land in it");
        drop = m.MouseUp();
        check.Expect(drop.Kind == CascadeActionKind.Drop && drop.Level == 0 && drop.Row == 4 && drop.ToLevel == 1, "and the drop reports the child as its end");
        m.CloseTo(0); m.OpenChild(0, 2, child);
        m.MouseDown(110f, r4, 1);
        check.Expect(m.Depth == 1, "without the flag the press still closes a sibling's child");
        m.MouseUp();
        m.OpenChild(0, 2, child);
        m.Level(1).DropTarget = true;
        m.DwellMs = 0;
        check.Expect(m.MouseMove(110f, r4) && m.Tick(1).Kind == CascadeActionKind.None && m.Depth == 2, "resting on a leaf beside a drop-target child keeps it open");
        check.Expect(m.MouseMove(110f, m.RowTop(0, 7) + 1f) && m.Tick(2).Kind == CascadeActionKind.RequestChildren && m.Depth == 1, "resting on another submenu row still replaces it");
        m.DwellMs = DefaultDwellMs;
        m.MouseDown(110f, r4, 1); m.MouseMove(110f, r4 + 2f * DragPx);
        m.Close();
        check.Expect(!m.RowDrag && m.DropLevel == -1, "Close drops the drag");
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        m.DragEnabled = false;
        m.MouseDown(110f, r4, 1); m.MouseMove(110f, r4 + 2f * DragPx);
        check.Expect(!m.RowDrag && m.MouseUp().Kind == CascadeActionKind.Activate, "drag disabled: a moved press never drags, the release on the row still activates");
        m.DragEnabled = true;
        m.Close();

        // Disabled rows hover but never activate; the right button gives Context on any row.
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        check.Expect(m.MouseMove(110f, m.RowTop(0, 5) + 1f) && l0.Hover == 5, "a disabled row hovers");
        check.Expect(m.Key(KeyReturn).Kind == CascadeActionKind.None && m.Key(KeyRight).Kind == CascadeActionKind.None
          && m.MouseDown(110f, m.RowTop(0, 5) + 1f, 1).Kind == CascadeActionKind.None && m.MouseUp().Kind == CascadeActionKind.None && m.Open, "a disabled leaf never activates");
        m.MouseMove(110f, m.RowTop(0, 6) + 1f);
        check.Expect(m.Key(KeyReturn).Kind == CascadeActionKind.None && m.MouseDown(110f, m.RowTop(0, 6) + 1f, 1).Kind == CascadeActionKind.None, "a disabled submenu row never requests children");
        m.MouseUp();
        var ctx = m.MouseDown(110f, m.RowTop(0, 3) + 1f, 3);
        check.Expect(ctx.Kind == CascadeActionKind.Context && ctx.Level == 0 && ctx.Row == 3 && m.Open, "right button on a row is Context, menu stays");
        check.Expect(m.MouseDown(110f, m.RowTop(0, 5) + 1f, 3).Kind == CascadeActionKind.Context, "Context on a disabled row too");

        // Dwell: timed from the first Tick after the hover changed, fires once, re-arms on a new row.
        m.MouseMove(110f, m.RowTop(0, 2) + 1f);
        check.Expect(m.Tick(1000).Kind == CascadeActionKind.None && m.Tick(1000 + DefaultDwellMs - 1).Kind == CascadeActionKind.None, "under the dwell: nothing");
        m.MouseMove(111f, m.RowTop(0, 2) + 1f);
        var dw = m.Tick(1000 + DefaultDwellMs);
        check.Expect(dw.Kind == CascadeActionKind.RequestChildren && dw.Level == 0 && dw.Row == 2, "at the dwell the hovered submenu row asks for its children");
        check.Expect(m.Tick(1000 + DefaultDwellMs + 1).Kind == CascadeActionKind.None && m.Tick(9999).Kind == CascadeActionKind.None, "the request fires once");
        m.MouseMove(110f, m.RowTop(0, 7) + 1f);
        m.Tick(1500);
        m.MouseMove(5f, 5f);
        check.Expect(m.Tick(1500 + DefaultDwellMs).Kind == CascadeActionKind.None && m.Depth == 1, "leaving every level disarms the dwell");
        m.MouseMove(110f, m.RowTop(0, 2) + 1f);
        m.OpenChild(0, 2, child);
        m.MouseMove(110f, m.RowTop(0, 7) + 1f);
        check.Expect(m.Tick(2000).Kind == CascadeActionKind.None && m.Depth == 2, "hovering another submenu row keeps the child until the dwell");
        dw = m.Tick(2000 + DefaultDwellMs);
        check.Expect(dw.Kind == CascadeActionKind.RequestChildren && dw.Row == 7 && m.Depth == 1, "at the dwell the sibling closes and the new row asks");
        m.OpenChild(0, 7, child);
        m.MouseMove(110f, m.RowTop(0, 4) + 1f);
        m.Tick(3000);
        check.Expect(m.Tick(3000 + DefaultDwellMs).Kind == CascadeActionKind.None && m.Depth == 1, "dwelling on a leaf closes the sibling child, asks nothing");
        m.OpenChild(0, 7, child);
        m.MouseMove(l1.X + 10f, l1.Y + ih + 1f);
        m.Tick(4000);
        check.Expect(m.Tick(4000 + DefaultDwellMs).Kind == CascadeActionKind.None && m.Depth == 2, "dwelling on a leaf inside the child keeps it");
        m.MouseMove(l1.X + 10f, l1.Y + 1f);
        m.Tick(4500);
        dw = m.Tick(4500 + DefaultDwellMs);
        check.Expect(dw.Kind == CascadeActionKind.RequestChildren && dw.Level == 1 && dw.Row == 0 && m.Depth == 2, "dwelling on a submenu row inside the child asks at level 1");
        m.MouseMove(110f, m.RowTop(0, 6) + 1f);
        m.Tick(5000);
        check.Expect(m.Tick(5000 + DefaultDwellMs).Kind == CascadeActionKind.None && m.Depth == 1, "a disabled submenu row closes the sibling but asks nothing");
        m.OpenChild(0, 7, child);
        m.MouseMove(110f, m.RowTop(0, 7) + 1f);
        m.Tick(6000);
        check.Expect(m.Tick(6000 + DefaultDwellMs).Kind == CascadeActionKind.None && m.Depth == 2, "dwelling on the owning row asks nothing");
        m.CloseTo(0);
        m.DwellMs = 0;
        m.MouseMove(110f, m.RowTop(0, 2) + 1f);
        dw = m.Tick(7000);
        check.Expect(dw.Kind == CascadeActionKind.RequestChildren && dw.Row == 2 && m.Tick(7000).Kind == CascadeActionKind.None, "dwell 0: the first Tick after the hover asks, once");
        m.DwellMs = DefaultDwellMs;
        m.Close();

        // Edges: the root clamps into the viewport; a child past the right edge flips left; a tall child clamps up.
        m.OpenAt(root, 1900f, 1070f, atlas, vpW, vpH);
        check.Expect(l0.X == vpW - l0.W && l0.Y == vpH - l0.H, "the root clamps into the viewport");
        check.Expect(m.OpenChild(0, 2, child) && l1.X == l0.X - l1.W && l1.Y == vpH - l1.H, "a child past the right edge flips to the parent's left; a tall child clamps up");
        m.OpenAt(root, 100f, 1000f, atlas, vpW, vpH);
        check.Expect(l0.Y == vpH - 8 * ih && m.OpenChild(0, 7, child) && l1.X == l0.X + l0.W
          && l1.Y == vpH - l1.H && l1.Y < m.RowTop(0, 7),
          "a child off a row near the bottom is pulled up until its last row is on screen");
        m.Close();

        // Drill mode: a child replaces the parent in place under a Back band; only the deepest level is live.
        VertexBatch? b2 = null;
        atlas.SetTestGlyph('<', 6f);
        m.Drill = true;
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        check.Expect(!l0.HasBack && m.MouseDown(110f, m.RowTop(0, 2) + 1f, 1).Kind == CascadeActionKind.None, "drill: a press on a submenu row asks nothing yet");
        var dr = m.MouseUp();
        check.Expect(dr.Kind == CascadeActionKind.RequestChildren && dr.Row == 2, "drill: the release asks for its children");
        check.Expect(m.OpenChild(0, 2, child) && m.Depth == 2 && l1.HasBack && l1.BackLabel == "row2" && l1.X == l0.X && l1.Y == l0.Y + ih && l1.Top(ih) == l0.Y,
            "drill: the child sits where the parent was, its Back band on the parent's top row");
        check.Expect(m.LevelAt(110f, l0.Y + 1f) == 1 && m.ItemAt(1, 110f, l0.Y + 1f) == -1 && m.Contains(110f, l0.Y + 1f), "drill: the band belongs to the child, not to a row; the hidden parent is under nothing");
        check.Expect(m.MouseMove(110f, l0.Y + 1f) && l1.BackHover && l1.Hover == -1 && m.MouseMove(110f, l1.Y + 1f) && !l1.BackHover && l1.Hover == 0, "drill: the band hovers as one, rows hover as rows");
        m.MouseMove(110f, l1.Y + 1f);
        check.Expect(m.Tick(100).Kind == CascadeActionKind.None && m.Tick(100 + DefaultDwellMs).Kind == CascadeActionKind.None, "drill: no dwell descent");
        check.Expect(m.MouseDown(110f, l0.Y + 1f, 1).Kind == CascadeActionKind.None && m.Depth == 2, "drill: a press on Back waits for the release");
        check.Expect(m.MouseMove(110f + 30f, l0.Y + 1f) && !m.RowDrag, "drill: Back never drags");
        check.Expect(m.MouseUp().Kind == CascadeActionKind.None && m.Depth == 1 && l0.Hover == 2, "drill: releasing on Back returns to the parent, its owning row still lit");
        m.OpenChild(0, 2, child);
        m.MouseDown(110f, l1.Y + 1f, 1);
        m.MouseMove(110f, l1.Y + ih * 3f + 1f);
        check.Expect(m.RowDrag && m.DropLevel == 1, "drill: a row drags within its own list");
        m.MouseMove(110f, 5f);
        check.Expect(m.DropLevel == -1, "drill: nowhere else is a target");
        m.MouseUp();
        var dst = CascadeStyle.Current;
        m.Draw(b2 ??= new VertexBatch(atlas.SolidU, atlas.SolidV), atlas, 15f, in dst);
        check.Expect(m.Icons.Length == 0, "drill: the hidden root's icon is not reported");
        check.Expect(m.Key(KeyLeft).Kind == CascadeActionKind.None && m.Depth == 1 && m.Key(KeyEscape).Kind == CascadeActionKind.CloseAll, "drill: Left goes up, Esc at the root closes");
        m.Drill = false;

        // Icons: reported for rows with icons only, inside the row's square; draw allocates nothing.
        var b = b2 ?? new VertexBatch(atlas.SolidU, atlas.SolidV);
        var st = CascadeStyle.Current;
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        m.Draw(b, atlas, 15f, in st);
        check.Expect(m.Icons.Length == 1 && m.Icons[0].Resref == "ir_x" && m.Icons[0].X == l0.X + IconInset && m.Icons[0].Y == m.RowTop(0, 1) + IconInset
          && m.Icons[0].Size == ih - 2f * IconInset && m.Icons[0].Alpha == 1f, "one icon rect, on row 1, inset in its square");
        // Frame: the bg quad first, then four 1 px strips outside it -- nothing opaque paints under a translucent bg.
        float[] fv = b.ToArray();
        const int QuadFloats = 6 * VertexBatch.Floats;
        bool strips = fv[0] == l0.X && fv[1] == l0.Y && fv[7] == st.Bg.a;
        for (int q = 1; q <= 4 && strips; q++)
        {
            float qx0 = fv[q * QuadFloats], qy0 = fv[q * QuadFloats + 1], qx1 = fv[q * QuadFloats + 2 * VertexBatch.Floats], qy1 = fv[q * QuadFloats + 2 * VertexBatch.Floats + 1];
            strips = (qx1 - qx0 == 1f || qy1 - qy0 == 1f) && (qx1 <= l0.X || qx0 >= l0.X + l0.W || qy1 <= l0.Y || qy0 >= l0.Y + l0.H);
        }
        check.Expect(strips, "the frame is four strips outside the bg, drawn after it");
        m.OpenChild(0, 2, child); m.OpenChild(1, 0, grand);
        m.MouseMove(m.Level(2).X + 10f, m.Level(2).Y + 1f);
        b.Clear(); m.Draw(b, atlas, 15f, in st);
        check.Expect(m.Icons.Length == 3 && m.Icons[1].Resref == "ir_g" && m.Icons[1].Handle == IntPtr.Zero && !m.Level(1).HasIcons, "icon rects across levels; a level without icons has no column");
        check.Expect(m.Icons[2].Handle == (IntPtr)0x7f01 && m.Icons[2].Resref == "" && m.Icons[2].Y == m.RowTop(2, 1) + IconInset, "a handle-only row is an icon row and its quad carries the handle");
        check.Expect(b.VertexCount > 0, "the draw emitted geometry");
        var wide = new[] { new CascadeItem("leaf", count: new string('x', 200)) };
        m.OpenAt(wide, 100f, 100f, atlas, vpW, vpH);
        b.Reset(atlas.SolidU, atlas.SolidV); m.Draw(b, atlas, 15f, in st);
        int withCount = b.VertexCount;
        m.OpenAt(new[] { new CascadeItem("leaf") }, 100f, 100f, atlas, vpW, vpH);
        b.Reset(atlas.SolidU, atlas.SolidV); m.Draw(b, atlas, 15f, in st);
        check.Expect(withCount == b.VertexCount, $"a count wider than the room beside the label is dropped, the label still draws ({withCount} vs {b.VertexCount}, w={m.Level(0).W})");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { b.Clear(); m.Draw(b, atlas, 15f, in st); m.Tick(7000 + i); }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        check.Expect(delta == 0, $"100 three-level draws + ticks allocate nothing ({delta} bytes)");
        m.Close();
        check.Expect(!m.Open && m.Icons.Length == 0 && !m.Contains(110f, 110f), "closed: nothing reported, nothing hit");

        // Open animation: a level stamps its first draw, fades/slides over AnimMs (the icon quads carry the
        // eased alpha and offset, the hit rect does not move), a re-layout of an open level keeps its stamp.
        st.AnimMs = 100; st.NowMs = 5000;
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        check.Expect(l0.OpenedMs == -1, "unstamped before the first draw");
        b.Clear(); m.Draw(b, atlas, 15f, in st);
        check.Expect(l0.OpenedMs == 5000 && m.Icons.Length == 1 && m.Icons[0].Alpha == 0f && m.Icons[0].Y == m.RowTop(0, 1) + IconInset - SlidePx, "the first draw stamps the level: alpha 0, slid up by SlidePx");
        check.Expect(m.ItemAt(0, 110f, m.RowTop(0, 1) + 1f) == 1, "the hit rect does not slide");
        st.NowMs = 5050; b.Clear(); m.Draw(b, atlas, 15f, in st);
        check.Expect(m.Icons[0].Alpha > 0.7f && m.Icons[0].Alpha < 0.8f && m.Icons[0].Y > m.RowTop(0, 1) + IconInset - SlidePx && m.Icons[0].Y < m.RowTop(0, 1) + IconInset, "half-way: eased-out alpha, part-way slid");
        st.NowMs = 5100; b.Clear(); m.Draw(b, atlas, 15f, in st);
        check.Expect(m.Icons[0].Alpha == 1f && m.Icons[0].Y == m.RowTop(0, 1) + IconInset, "at AnimMs: settled");
        m.OpenChild(0, 2, child);
        check.Expect(l1.OpenedMs == -1, "a new child is unstamped");
        st.NowMs = 5200; b.Clear(); m.Draw(b, atlas, 15f, in st);
        check.Expect(l1.OpenedMs == 5200 && l0.OpenedMs == 5000, "the child stamps at its own first draw, the root keeps its stamp");
        m.OpenChild(0, 2, child);
        check.Expect(l1.OpenedMs == 5200, "re-opening the same child in place keeps its stamp");
        m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        check.Expect(l0.OpenedMs == 5000, "re-laying the open root keeps its stamp");
        m.OpenChild(0, 7, child);
        check.Expect(l1.OpenedMs == -1, "a sibling child is a fresh open");
        st.AnimMs = 0; m.Close(); m.OpenAt(root, 100f, 100f, atlas, vpW, vpH);
        b.Clear(); m.Draw(b, atlas, 15f, in st);
        check.Expect(m.Icons[0].Alpha == 1f && m.Icons[0].Y == m.RowTop(0, 1) + IconInset, "AnimMs 0 draws settled at once");
        m.Close();

        log.Info(check.Ok ? "CascadeMenu.SelfCheck: passed" : "CascadeMenu.SelfCheck: FAILED");
    }
}
