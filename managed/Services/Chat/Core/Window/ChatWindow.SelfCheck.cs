using System.Collections.Generic;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Chat.Core;

public sealed partial class ChatWindow
{
    /// <summary>Off-device proof of the drag/resize/tab/scroll state machine.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var tabs = new[] { new Tab("All", 0xffffffff), new Tab("Talk", 0x001) };
        var win = new ChatWindow(100, 100, 400, 300, tabs);

        // move: grab the empty tab-bar area (past both tabs), drag +50/+30
        check.Expect(win.MouseDown(100 + 2 * win.TabW + 5, 105), "down on move handle");
        check.Expect(win.Dragging, "dragging after move-grab");
        win.MouseMove(100 + 2 * win.TabW + 55, 135);
        check.Expect(win.X == 150 && win.Y == 130, "moved by drag delta");
        check.Expect(win.MouseUp(0, 0) && !win.Dragging, "release ends move");

        // resize: grab bottom-right grip, drag out +100/+80
        float rx = win.X + win.W, ry = win.Y + win.H;
        check.Expect(win.MouseDown(rx - 2, ry - 2), "down on resize grip");
        win.MouseMove(rx + 98, ry + 78);
        check.Expect(win.W == 500 && win.H == 380, "resized by drag delta");
        win.MouseUp(0, 0);

        // resize honours the minimum size
        var small = new ChatWindow(0, 0, MinW, MinH, tabs);
        small.MouseDown(MinW - 2, MinH - 2);
        small.MouseMove(-500, -500);
        check.Expect(small.W == MinW && small.H == MinH, "resize clamped to minimum");

        // tab switch: click the second tab region -> switches, drag only after threshold
        check.Expect(win.MouseDown(win.X + win.TabW + 5, win.Y + 5), "down on tab 1");
        check.Expect(win.ActiveTab == 1 && !win.TabDragActive, "tab press switches but does not start drag yet");
        win.MouseMove(win.X + win.TabW + 5 + TabDragThreshold + 1, win.Y + 5);
        check.Expect(win.TabDragActive, "drag activates after moving past threshold");
        win.MouseUp(0, 0);

        // scroll: pixel offset up, then over-scroll down clamps at the bottom (low end clamped here; the
        // renderer clamps the high end once it knows the content height)
        win.ActiveTab = 0; win.Active.ScrollPx = 0f;
        win.ScrollByPx(50f); check.Expect(win.Active.ScrollPx == 50f, "scroll up by pixels");
        win.ScrollByPx(-80f); check.Expect(win.Active.ScrollPx == 0f, "scroll clamps at bottom");

        // scrollbar thumb: geometry + absolute-anchor drag (against renderer-written ContentH/ViewH). ViewH
        // is the real body height (frame minus tab bar) so the thumb/resize-grip separation is exercised.
        win.ViewH = win.H - TabBarH; win.ContentH = win.ViewH * 3f;   // 3x overflow, MaxScroll = 2*ViewH
        win.Active.ScrollPx = 0f;
        check.Expect(win.ThumbRect(out float tt0, out float th0), "thumb shown when content overflows");
        // Bug fix: at ScrollPx 0 (bottom-pinned) the thumb must clear the resize grip -- they used to share
        // the bottom-right corner and steal each other's clicks.
        check.Expect(tt0 + th0 <= win.Y + win.H - ResizeGrip + 0.5f, "bottom-pinned thumb clears the resize grip");
        check.Expect(win.MouseDown(win.X + win.W - 3, win.Y + win.H - 3), "grip-corner press consumed");
        check.Expect(!win.ScrollDragging, "grip-corner press is a resize, not a scroll");
        win.MouseUp(0, 0);
        // absolute-anchor drag: grab the thumb, drag up the full track travel -> oldest.
        win.Active.ScrollPx = 0f;
        check.Expect(win.ThumbRect(out float tt1, out float th1), "thumb still shown");
        float travel = win.SbTrackH - th1;
        check.Expect(win.MouseDown(win.X + win.W - 3, tt1 + 2), "down on thumb grabs the scrollbar");
        check.Expect(win.ScrollDragging, "scroll-dragging after thumb grab");
        win.MouseMove(win.X + win.W - 3, tt1 + 2 - travel);
        check.Expect(System.Math.Abs(win.Active.ScrollPx - win.MaxScroll) < 0.5f, "drag up maps to MaxScroll (oldest)");
        win.MouseUp(0, 0);
        win.ContentH = 100f; win.ViewH = 300f;   // content fits -> no thumb, no divide blow-up
        check.Expect(!win.ThumbRect(out _, out _), "no thumb when content fits");
        win.ContentH = 0f; win.ViewH = 0f; win.Active.ScrollPx = 0f;   // leave state clean

        // channel dropdown: strip lives below the frame, only while typing.
        win.FooterVisible = true;
        float sy = win.StripTop;                                  // top of the pop-out strip
        check.Expect(win.MouseDown(win.X + 5, sy + 5), "click dropdown button consumes");
        check.Expect(win.ChannelListOpen, "dropdown opens");
        // list stacks upward: item 2 (Whisper) sits at StripTop - 3*FooterH .. -2*FooterH
        float item2Y = sy - FooterH * 3 + 2;
        check.Expect(win.MouseDown(win.X + 5, item2Y), "click list item consumes");
        check.Expect(!win.ChannelListOpen && win.SendChannel == Channel.Whisper, "item selected + list closed");
        // opening then clicking off the list dismisses without changing the channel
        win.MouseDown(win.X + 5, sy + 5); check.Expect(win.ChannelListOpen, "reopen");
        win.MouseDown(win.X + win.W - 5, win.Y + win.H - 5); check.Expect(!win.ChannelListOpen, "click-off dismisses");
        check.Expect(win.SendChannel == Channel.Whisper, "channel unchanged on dismiss");

        // Input-on-top: the strip sits just above the frame; the button is at StripTop, the list stacks
        // DOWNWARD from the frame top over the body (the opposite of below-mode).
        win.SendChannel = Channel.Talk; win.InputOnTop = true; win.FooterRows = 1;
        float syT = win.StripTop;
        check.Expect(System.Math.Abs(syT - (win.Y - FooterH)) < 0.001f, "above-mode strip sits one row above the frame");
        check.Expect(win.MouseDown(win.X + 5, syT + 5), "above: dropdown button consumes");
        check.Expect(win.ChannelListOpen, "above: dropdown opens");
        float item2YT = win.ChannelItemTop(2) + 2;             // Whisper, stacked downward
        check.Expect(win.MouseDown(win.X + 5, item2YT), "above: list item consumes");
        check.Expect(!win.ChannelListOpen && win.SendChannel == Channel.Whisper, "above: item selected + list closed");
        win.InputOnTop = false;
        win.FooterVisible = false;
        win.ActiveTab = 0;

        // Gear/options moved to SettingsWindow (its own SelfCheck); the gear press is claimed by the shell.

        // Tab list mutation: add/remove, the always->=1-tab guard, and active-tab identity.
        var tw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 1u), new Tab("C", 1u) });
        tw.AddTab(new Tab("D", 1u)); check.Expect(tw.Tabs.Count == 4, "AddTab appends");
        tw.ActiveTab = 2; Tab wasActive = tw.Active;               // C active
        check.Expect(tw.RemoveTab(0) && tw.Active == wasActive, "removing a tab before the active one keeps it active");
        check.Expect(tw.Tabs.Count == 3 && tw.ActiveTab == 1, "index tracked the removed tab");
        check.Expect(!tw.RemoveTab(9), "out-of-range remove is a no-op");
        while (tw.RemoveTab(0)) { }                                // drain toward the floor
        check.Expect(tw.Tabs.Count == 1 && !tw.RemoveTab(0), "the final tab is undeletable");
        // SetTabs: replace the whole list, reject empty
        tw.SetTabs(new[] { new Tab("X", 1u), new Tab("Y", 2u) });
        check.Expect(tw.Tabs.Count == 2 && tw.Tabs[0].Label == "X" && tw.ActiveTab == 0, "SetTabs replaces all tabs");
        tw.SetTabs(new Tab[0]);
        check.Expect(tw.Tabs.Count == 2, "SetTabs rejects empty (keeps old tabs)");
        bool threw = false;
        try { _ = new ChatWindow(0, 0, 400, 300, new Tab[0]); } catch (System.ArgumentException) { threw = true; }
        check.Expect(threw, "constructing a window with no tabs is rejected");

        // Tab drag reorder: down on tab, exceed threshold, drag across boundaries.
        var dw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 2u), new Tab("C", 4u) });
        float dtw = dw.TabWFor(3);
        check.Expect(dw.MouseDown(dtw * 0.5f, 5), "drag: down on tab A");
        check.Expect(!dw.TabDragActive && dw.ActiveTab == 0, "drag: hold on A, not yet dragging");
        dw.MouseMove(dtw * 1.5f, 5);
        check.Expect(dw.Tabs[0].Label == "B" && dw.Tabs[1].Label == "A" && dw.Tabs[2].Label == "C", "drag: A past B reorders");
        check.Expect(dw.ActiveTab == 1, "drag: ActiveTab follows A");
        dw.MouseMove(dtw * 2.5f, 5);
        check.Expect(dw.Tabs[0].Label == "B" && dw.Tabs[1].Label == "C" && dw.Tabs[2].Label == "A", "drag: A to end");
        check.Expect(dw.ActiveTab == 2, "drag: ActiveTab tracks to end");
        dw.MouseUp(0, 0);
        check.Expect(!dw.TabDragActive, "drag: release ends it");

        // MoveTab with a non-active tab: ActiveTab adjusts to stay on the same tab.
        var mt = new ChatWindow(0, 0, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 2u), new Tab("C", 4u) });
        mt.ActiveTab = 1;
        mt.MoveTab(0, 2);
        check.Expect(mt.Tabs[0].Label == "B" && mt.Tabs[2].Label == "A", "MoveTab forward shifts");
        check.Expect(mt.ActiveTab == 0, "MoveTab: active follows shift-down");
        mt.MoveTab(1, 1);
        check.Expect(mt.Tabs[1].Label == "C", "MoveTab same-pos is a no-op");

        // InsertTab at a position.
        var it = new ChatWindow(0, 0, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 2u) });
        it.InsertTab(new Tab("Z", 8u), 1);
        check.Expect(it.Tabs.Count == 3 && it.Tabs[1].Label == "Z" && it.Tabs[2].Label == "B", "InsertTab at 1");
        it.InsertTab(new Tab("W", 16u), 99);
        check.Expect(it.Tabs[it.Tabs.Count - 1].Label == "W", "InsertTab past end appends");

        // TabBarDropIndex.
        var di = new ChatWindow(100, 100, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 2u) });
        check.Expect(di.TabBarDropIndex(100 + di.TabW * 0.5f, 105) == 0, "drop over first tab -> 0");
        check.Expect(di.TabBarDropIndex(100 + di.TabW * 1.5f, 105) == 1, "drop over second tab -> 1");
        check.Expect(di.TabBarDropIndex(50, 105) == -1, "drop outside -> -1");
        check.Expect(di.TabBarDropIndex(100 + 400 - GearW + 1, 105) == -1, "drop on button area -> -1");
        check.Expect(di.TabBarDropIndex(100, 50) == -1, "drop above tab bar -> -1");

        // a press outside the window is not consumed
        check.Expect(!win.MouseDown(-10, -10), "outside press ignored");

        // a release whose press missed the window is NOT consumed (the engine owns that button pair)
        win.MouseDown(win.X - 50, win.Y - 50);
        check.Expect(!win.MouseUp(win.X + 5, win.Y + 5), "release after outside press not consumed");

        // Send-channel constraint (ADR 0003): Bit mapping, CanSend, HasSendable, clamp, hit-test.
        check.Expect(Bit(Channel.Talk) == 0x001 && Bit(Channel.Tell) == 0x002 && Bit(Channel.Shout) == 0x004
          && Bit(Channel.Whisper) == 0x008 && Bit(Channel.Party) == 0x040 && Bit(Channel.DM) == 0x400,
          "Bit maps each Channel to its FilterChannel flag");

        // CanSend + HasSendable against a Talk|DM mask (no Shout/Whisper/Tell/Party).
        var cw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("Narrow", 0x001 | 0x400) });
        check.Expect(cw.CanSend(Channel.Talk) && cw.CanSend(Channel.DM), "CanSend true for bits in mask");
        check.Expect(!cw.CanSend(Channel.Shout) && !cw.CanSend(Channel.Whisper), "CanSend false for bits not in mask");
        check.Expect(cw.HasSendable, "HasSendable true when mask covers at least one sendable");

        // Receive-only mask -> no sendable.
        var ro = new ChatWindow(0, 0, 400, 300, new[] { new Tab("Log", 0x010 | 0x020 | 0x080) });
        check.Expect(!ro.HasSendable, "HasSendable false for a receive-only mask");

        // Clamp: current selection outside the mask -> first visible sendable.
        cw.SendChannel = Channel.Shout; cw.ClampSendChannel();
        check.Expect(cw.SendChannel == Channel.Talk, "clamp moves invalid selection to first visible sendable");
        cw.SendChannel = Channel.DM; cw.ClampSendChannel();
        check.Expect(cw.SendChannel == Channel.DM, "clamp keeps a valid selection");

        // Tab switch clamp: switching to a narrow tab invalidates the selection.
        var sw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("DM", 0x400) });
        sw.SendChannel = Channel.Talk;
        sw.MouseDown(sw.X + sw.TabW + 5, sw.Y + 5);   // click tab 1 (DM-only)
        check.Expect(sw.ActiveTab == 1 && sw.SendChannel == Channel.DM, "tab switch clamps SendChannel to tab's mask");
        sw.MouseUp(0, 0);

        // each tab keeps its own channel. The reporter's layout -- All, and a Server/Combat/DM tab whose only
        // sendable is DM -- must not leave All on DM, and a channel picked on All survives a trip to a narrow tab.
        void Click(ChatWindow w, int tab) { w.MouseDown(w.X + tab * w.TabW + 5, w.Y + 5); w.MouseUp(0, 0); }
        var rt = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("Srv/Cbt/DM", 0x020 | 0x010 | 0x400), new Tab("Party", 0x040), new Tab("Fresh", 0xffffffff) });
        rt.SendChannel = Channel.Talk;
        Click(rt, 1);
        check.Expect(rt.ActiveTab == 1 && rt.SendChannel == Channel.DM, "CX-59: the DM-only tab sends on DM");
        Click(rt, 0);
        check.Expect(rt.ActiveTab == 0 && rt.SendChannel == Channel.Talk, $"CX-59: back on All, Talk again ({rt.SendChannel})");
        rt.SendChannel = Channel.Party;
        Click(rt, 2); Click(rt, 1); Click(rt, 0);
        check.Expect(rt.SendChannel == Channel.Party, $"CX-59: a channel picked on All survives trips through narrow tabs ({rt.SendChannel})");
        Click(rt, 1); Click(rt, 3);
        check.Expect(rt.SendChannel == Channel.Talk, $"CX-59: a tab not yet used starts on Talk, not the DM tab's channel ({rt.SendChannel})");
        rt.SendChannel = Channel.Shout; Click(rt, 0); Click(rt, 3);
        check.Expect(rt.SendChannel == Channel.Shout, "CX-59: two broad tabs keep separate channels");
        Click(rt, 1);
        check.Expect(rt.RemoveTab(1) && rt.Active.Label == "Party" && rt.SendChannel == Channel.Party, $"CX-59: removing the active tab restores the channel of the tab that slides in ({rt.SendChannel})");
        rt.SelectTab(0);
        check.Expect(rt.SendChannel == Channel.Party && rt.Active.SendChannel == Channel.Party, "CX-59: SelectTab restores, and the window's channel is the active tab's");
        var pin = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("T:Alice", 0x002) { PinnedName = "Alice" } });
        pin.SendChannel = Channel.Whisper;
        Click(pin, 1);
        check.Expect(pin.SendChannel == Channel.Tell, "CX-59: a pinned tell tab still sends Tell");
        Click(pin, 0);
        check.Expect(pin.SendChannel == Channel.Whisper, $"CX-59: back from a tell tab, the channel picked before it ({pin.SendChannel})");
        var st = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        st.SendChannel = Channel.Shout;
        st.SetTabs(new[] { new Tab("A", 0xffffffff), new Tab("DM", 0x400) });
        check.Expect(st.SendChannel == Channel.Shout && st.Active.SendChannel == Channel.Shout, "CX-59: a restored tab list's first tab takes over the window's channel");

        // CanSelect: Tell hidden from dropdown when other channels exist.
        check.Expect(!cw.CanSelect(Channel.Tell), "CanSelect hides Tell when Talk|DM present");
        var allTab = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        check.Expect(!allTab.CanSelect(Channel.Tell), "CanSelect hides Tell on All-channel tab");
        var tellOnly = new ChatWindow(0, 0, 400, 300, new[] { new Tab("Tell", 0x002) });
        check.Expect(tellOnly.CanSelect(Channel.Tell), "CanSelect shows Tell when it's the only channel");

        // Tell-pinned tab auto-sets Tell on clamp.
        var pinWin = new ChatWindow(0, 0, 400, 300,
            new[] { new Tab("All", 0xffffffff), new Tab("Tell:Alice", 0x002) { PinnedName = "Alice" } });
        pinWin.ActiveTab = 1; pinWin.SendChannel = Channel.Talk;
        pinWin.ClampSendChannel();
        check.Expect(pinWin.SendChannel == Channel.Tell, "clamp auto-sets Tell on pinned tab");

        // Hit-test with constrained dropdown: Talk|DM mask -> only 2 rows (Tell hidden).
        cw.FooterVisible = true; cw.SendChannel = Channel.Talk;
        cw.MouseDown(cw.X + 5, cw.StripTop + 5); check.Expect(cw.ChannelListOpen, "constrained dropdown opens");
        // Row 0 = Talk, row 1 = DM (Shout/Whisper/Tell/Party skipped).
        float r1y = cw.ChannelItemTop(1) + 2;
        cw.MouseDown(cw.X + 5, r1y);
        check.Expect(cw.SendChannel == Channel.DM, "row 1 of constrained list selects DM, not Shout");
        cw.FooterVisible = false;

        // Scale invariance (#font-clip): chrome must track Metrics.Scale so text never outgrows its box. If a
        // metric is ever reverted to a bare const this catches it. Save/restore -- self-checks run at Scale 1.
        float s0 = Metrics.Scale;
        float h1 = TabBarH, f1 = FooterH; Metrics.Scale = 2f;
        check.Expect(System.Math.Abs(TabBarH - 2f * h1) < 1e-3f && System.Math.Abs(FooterH - 2f * f1) < 1e-3f
          && System.Math.Abs(GearW - TabBarH) < 1e-3f, "tab/footer/gear scale with Metrics.Scale");
        Metrics.Scale = s0;

        // CopySettingsFrom: deep-copies all look settings, NOT tabs/rect/sendChannel.
        var src = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        src.Toggles = ChatToggle.Timestamp; src.InputOnTop = true; src.AnimEnabled = false;
        src.HoldSec = 99f; src.FadeSec = 11f; src.TauMs = 200f; src.Outline = 2;
        src.TimeFormat = TsFormat.HourMinSec; src.TimeSource = TsSource.Game;
        src.Align = TextAlign.Right;
        src.BgMode = BgMode.Radial; src.BgEnd = 0.7f;
        src.AutoHideHeader = false;
        src.FontPx = 14; src.Typeface = "Arial";
        var dst = new ChatWindow(50, 50, 300, 200, new[] { new Tab("Combat", 0x010) });
        dst.CopySettingsFrom(src);
        check.Expect(dst.Toggles == ChatToggle.Timestamp && dst.InputOnTop && !dst.AnimEnabled, "copy: toggles+inputOnTop+anim");
        check.Expect(dst.HoldSec == 99f && dst.FadeSec == 11f && dst.TauMs == 200f, "copy: hold/fade/tau");
        check.Expect(dst.Outline == 2 && dst.TimeFormat == TsFormat.HourMinSec && dst.TimeSource == TsSource.Game, "copy: outline+ts");
        check.Expect(dst.Align == TextAlign.Right, "copy: align");
        check.Expect(dst.BgMode == BgMode.Radial && dst.BgEnd == 0.7f, "copy: bgmode + stops");
        check.Expect(!dst.AutoHideHeader, "copy: autoHideHeader");
        check.Expect(dst.FontPx == 14 && dst.Typeface == "Arial", "copy: font");
        check.Expect(dst.X == 50 && dst.W == 300, "copy: rect untouched");
        check.Expect(dst.Tabs.Count == 1 && dst.Tabs[0].Label == "Combat", "copy: tabs untouched");

        // Lock: prevents move, resize, and tab drag; scroll + tab switch still work.
        var lw = new ChatWindow(100, 100, 400, 300, new[] { new Tab("A", 1u), new Tab("B", 2u) });
        lw.Locked = true;
        check.Expect(lw.MouseDown(100 + lw.TabW * 2 + 5, 105), "locked: move-handle press consumed");
        check.Expect(!lw.Dragging, "locked: no move grab");
        lw.MouseUp(0, 0);
        check.Expect(lw.MouseDown(lw.X + lw.W - 2, lw.Y + lw.H - 2), "locked: resize-grip press consumed");
        check.Expect(!lw.Dragging, "locked: no resize grab");
        lw.MouseUp(0, 0);
        check.Expect(lw.MouseDown(lw.X + lw.TabW * 1.5f, lw.Y + 5), "locked: tab press consumed");
        check.Expect(lw.ActiveTab == 1, "locked: tab switch works");
        check.Expect(!lw.TabDragActive, "locked: no tab drag");
        lw.MouseUp(0, 0);
        // Scroll still works when locked.
        lw.ViewH = lw.H - TabBarH; lw.ContentH = lw.ViewH * 3f;
        check.Expect(lw.ThumbRect(out float lt, out float lh), "locked: thumb shown");
        check.Expect(lw.MouseDown(lw.X + lw.W - 3, lt + 2), "locked: scrollbar grab consumed");
        check.Expect(lw.ScrollDragging, "locked: scroll still works");
        lw.MouseUp(0, 0);
        lw.ContentH = 0f; lw.ViewH = 0f;

        // Grid snap: move snaps X/Y, resize snaps the edge, MinW beats snap, off = free.
        float spOld = SnapPx; float cxOld = SnapCX, cyOld = SnapCY;
        SnapPx = 50; SnapCX = 0; SnapCY = 0;
        var gs = new ChatWindow(0, 0, 400, 300, tabs);
        gs.MouseDown(250, 5);                    // move handle (past 2 tabs at 120px each)
        gs.MouseMove(297, 28);                   // raw X=47 → snap 50; raw Y=23 → snap 0
        check.Expect(gs.X == 50 && gs.Y == 0, "grid snap: move snaps X/Y");
        gs.MouseUp(0, 0);
        var gs2 = new ChatWindow(50, 0, 400, 300, tabs);
        gs2.MouseDown(gs2.X + gs2.W - 2, gs2.Y + gs2.H - 2);   // resize grip
        gs2.MouseMove(gs2.X + gs2.W - 2 + 27, gs2.Y + gs2.H - 2 + 13); // right=477→500 W=450; bottom=313→300 H=300
        check.Expect(gs2.W == 450 && gs2.H == 300, "grid snap: resize snaps the edge");
        gs2.MouseUp(0, 0);
        var gsm = new ChatWindow(0, 0, MinW, MinH, tabs);
        gsm.MouseDown(MinW - 2, MinH - 2);
        gsm.MouseMove(-500, -500);
        check.Expect(gsm.W == MinW && gsm.H == MinH, "grid snap: MinW/MinH wins over snap");
        gsm.MouseUp(0, 0);
        SnapPx = 0;
        var gf = new ChatWindow(0, 0, 400, 300, tabs);
        gf.MouseDown(250, 5);
        gf.MouseMove(297, 28);
        check.Expect(gf.X == 47 && gf.Y == 23, "grid off: free drag");
        gf.MouseUp(0, 0);
        SnapPx = spOld; SnapCX = cxOld; SnapCY = cyOld;

        // Edge snap: move near edge snaps to edge, resize right/bottom snaps to viewport.
        float etOld = EdgeThresh, evwOld = EdgeVpW, evhOld = EdgeVpH;
        EdgeThresh = EdgeMagnet; EdgeVpW = 1920; EdgeVpH = 1080;
        SnapPx = 0;
        var em = new ChatWindow(0, 0, 400, 300, tabs);
        em.MouseDown(250, 5);
        em.MouseMove(255, 13);   // raw X=5, Y=8 → both < 12, snap to 0,0
        check.Expect(em.X == 0 && em.Y == 0, "edge snap: move near top-left snaps to 0,0");
        em.MouseUp(0, 0);
        var em2 = new ChatWindow(0, 0, 400, 300, tabs);
        em2.MouseDown(250, 5);
        em2.MouseMove(1770, 788);   // raw X=1520, right=1920 exact; raw Y=783, bot=1083 ~1080
        check.Expect(em2.X == 1920 - 400, "edge snap: move near right snaps to vpW-W");
        check.Expect(em2.Y == 1080 - 300, "edge snap: move near bottom snaps to vpH-H");
        em2.MouseUp(0, 0);
        var er = new ChatWindow(100, 100, 400, 300, tabs);
        er.MouseDown(er.X + er.W - 2, er.Y + er.H - 2);
        er.MouseMove(er.X + er.W - 2 + (1920 - 100 - 400 - 5), er.Y + er.H - 2 + (1080 - 100 - 300 - 8));
        check.Expect(er.W == 1920 - 100, "edge snap: resize right edge snaps to vpW");
        check.Expect(er.H == 1080 - 100, "edge snap: resize bottom edge snaps to vpH");
        er.MouseUp(0, 0);
        EdgeThresh = 0;
        var ef = new ChatWindow(0, 0, 400, 300, tabs);
        ef.MouseDown(250, 5);
        ef.MouseMove(255, 13);
        check.Expect(ef.X == 5 && ef.Y == 8, "edge off: free drag");
        ef.MouseUp(0, 0);
        EdgeThresh = etOld; EdgeVpW = evwOld; EdgeVpH = evhOld;

        // Window-to-window snap: flush against a neighbour within the magnet; a distant/non-overlapping one = no pull.
        var obst = new (float, float, float, float)[] { (500f, 100f, 200f, 150f) };
        float wx = 500f - 400f + 3f, wy = 120f;   // our right edge 3px shy of their left; Y ranges overlap
        SnapToRects(ref wx, ref wy, 400f, 300f, obst);
        check.Expect(System.Math.Abs(wx - 100f) < 0.01f && System.Math.Abs(wy - 120f) < 0.01f, "winsnap: right edge snaps flush");
        float fx = 50f, fy = 600f;                 // far off + no overlap: untouched
        SnapToRects(ref fx, ref fy, 400f, 300f, obst);
        check.Expect(fx == 50f && fy == 600f, "winsnap: distant window does not pull");

        // Ephemeral: Tell-only tab is structurally ephemeral.
        var ew = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("Tell:X", 0x002) });
        check.Expect(ew.Tabs[1].Ephemeral, "Tell-only tab is ephemeral");
        check.Expect(!ew.Tabs[0].Ephemeral, "non-Tell tab is not ephemeral");
        ew.MouseDown(ew.TabW + 5, 5);
        check.Expect(ew.ActiveTab == 1 && ew.Tabs[1].Ephemeral, "tab click keeps ephemeral (X stays)");
        ew.MouseUp(0, 0);
        // Pin button: clicking the pin zone sets Kept, clears ephemeral.
        float pinX = ew.TabW * 2 - 2 * TabBtnW + 2;
        ew.MouseDown(pinX, 5);
        check.Expect(ew.Tabs[1].Kept && !ew.Tabs[1].Ephemeral, "pin click sets Kept, clears ephemeral");
        ew.MouseUp(0, 0);
        ew.Tabs[1].Kept = false;

        // Ephemeral close-X: clicking the right edge removes the tab.
        var xw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("Tell:Y", 0x002) });
        float xTabRight = xw.TabW * 2;
        check.Expect(xw.MouseDown(xTabRight - 2, 5), "close-X press consumed");
        check.Expect(xw.Tabs.Count == 1, "close-X removes the ephemeral tab");
        xw.MouseUp(0, 0);
        // Kept tab: same click region is a normal tab switch, not a close.
        var nw = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff), new Tab("Tell:Z", 0x002) { Kept = true } });
        nw.MouseDown(nw.TabW * 2 - 2, 5);
        check.Expect(nw.Tabs.Count == 2 && nw.ActiveTab == 1, "kept: right-edge click switches, not closes");
        nw.MouseUp(0, 0);
        // Solo ephemeral tab: close-X is dead (can't remove the last tab; window [×] is the dismiss).
        var sw2 = new ChatWindow(0, 0, 400, 300, new[] { new Tab("Tell:Solo", 0x002) });
        sw2.MouseDown(sw2.TabW - 2, 5);
        check.Expect(sw2.Tabs.Count == 1, "solo ephemeral: close-X does not remove the last tab");
        sw2.MouseUp(0, 0);

        log.Info(check.Ok ? "ChatWindow.SelfCheck: passed" : "ChatWindow.SelfCheck: FAILED");
    }
}
