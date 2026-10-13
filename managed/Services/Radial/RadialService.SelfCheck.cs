using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Radial;

public sealed unsafe partial class RadialService
{
    /// <summary>The pure parts, off-device: the cascade opened on fake rows with no engine root held, the per-level
    /// handle bookkeeping, the close-reason paths, the three tag shapes through <see cref="RowOf"/> (slot / hoist /
    /// header), an unvalidated hoist refused on Enter, and the row-action popup's open, routing and close. Engine
    /// calls are skipped because no root is held (<see cref="Release"/> is a no-op at zero) and there is no window.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        for (char ch = 'a'; ch <= 'z'; ch++) atlas.SetTestGlyph(ch, 6f);
        var stale = new RadialPin { Root = RootKind.Creature, Path = new[] { 1, 4, 3, 0 }, Fn = "x", Label = "cast > fire" };
        var rows = new[]
        {
            new CascadeItem("attack", "ir_attack", tag: 2), new CascadeItem("cast", "ir_cast", hasChildren: true, tag: 1),
            new CascadeItem("fire", disabled: false, tag: new RadialRowTag { Pin = stale, Valid = false }),
        };
        var headed = new[] { new CascadeItem("item"), rows[0], rows[1] };
        void OpenFake(CascadeItem[]? items = null)
        {
            Array.Clear(_levelMenu); Array.Clear(_levelPath); Array.Clear(_levelFolder);   // level 0 stays Zero: every engine call gates on it, so the harness never reaches one
            _ctx.Close(); _ctxLevel = _ctxRow = -1; _naming = null;
            _pressOwned = false; _deferLevel = _deferRow = -1; _deferClose = null;
            _atlas = atlas; _vpW = 1920f; _vpH = 1080f; _kind = RootKind.Creature;
            _menu.DragEnabled = !RadialKnobs.Lock.V;
            _menu.OpenAt(items ?? rows, 100f, 100f, atlas, 1920f, 1080f);
        }
        bool wasEnabled = RadialKnobs.Enabled.V, wasLocked = RadialKnobs.Lock.V;
        var savedLog = _log;
        _log = null;   // Close's reason line is the live instrument; the harness only checks state
        RadialKnobs.Enabled.V = true;   // the field, not Set(): no Changed, no writer armed
        try
        {
            check.Expect(!OnMouseButton(true, 1, 5, 5) && !OnKey(0x1B, 0) && !OnMouseMotion(5, 5) && !OnMouseWheel(1), "closed: nothing consumed");
            check.Expect(KnobGroup.Find("radial.font") is Knob<int> { Default: 0 }
              && KnobGroup.Find("radial.typeface") is Knob<string> && KnobGroup.Find("radial.anim") is Knob<bool> { Default: true }
              && KnobGroup.Find("radial.animms") is Knob<int> { Default: 120 } && RadialKnobs.Font.Size!.Default == 0
              && RadialKnobs.Font.Typeface.Default == FontRegistry.DefaultName && RadialKnobs.Font.Outline.Default == 0, "the look rows bind; the text rows default to the UI face, unoutlined");

            OpenFake();
            check.Expect(_menu.Open && SlotOf(0, 0) == 2 && SlotOf(0, 1) == 1, "rows map back to their vanilla slot through the tag");
            check.Expect(RowOf(0, 1, out _, out _, out int[] p1, out RadialRowTag? h1) && p1.Length == 1 && p1[0] == 1 && h1 == null, "a slot row's path is the level path + its slot");
            check.Expect(RowOf(0, 2, out _, out int s2, out int[] p2, out RadialRowTag? h2) && h2 != null && h2.Pin == stale && p2 == stale.Path && s2 == 0 && SlotOf(0, 2) == 0,
                "a hoisted row reads its pin's path through the tag");
            _menu.Level(0).Hover = 2;
            check.Expect(OnKey(0x0D, 0) && _menu.Open, "Enter on an unvalidated hoist is refused: the menu stays, nothing invoked");
            check.Expect(OnMouseMotion(110, 101) && OnMouseWheel(1) && OnKey(0x40000051, 0) && _menu.Open, "motion, wheel and keys are consumed while open");
            check.Expect(OnMouseButton(false, 3, 110, 101) && _menu.Open && !_pressOwned, "the opening right-up is swallowed, menu stays");

            // Click-away: the press closes AND is consumed; its release is consumed by the ledger, the next is not.
            check.Expect(OnMouseButton(true, 1, 5, 5) && !_menu.Open && _levelMenu[0] == IntPtr.Zero, "a press outside closes and clears the level handles");
            check.Expect(OnMouseButton(false, 1, 5, 5) && !OnMouseButton(false, 1, 5, 5), "the click-away's release is ours, the next one is not");

            OpenFake();
            check.Expect(OnKey(0x1B, 0) && !_menu.Open, "Esc at the root closes");

            OpenFake();
            _menu.Level(0).Hover = 0;
            check.Expect(OnKey(0x0D, 0) && !_menu.Open && _levelMenu[0] == IntPtr.Zero, "Enter on a leaf with no held root just closes (no engine call at zero)");

            // A click is press + release on the leaf: the press arms a drag, the release activates.
            OpenFake();
            int r0 = (int)_menu.RowTop(0, 0) + 1;
            check.Expect(OnMouseButton(true, 1, 110, r0) && _menu.Open && _pressOwned, "the press on a leaf activates nothing yet");
            check.Expect(OnMouseButton(false, 1, 110, r0) && !_menu.Open && !_pressOwned, "its release activates: the leaf closes the menu");
            OpenFake();
            OnMouseButton(true, 1, 110, r0); OnMouseMotion(110, r0 + 40);
            check.Expect(_menu.RowDrag && OnMouseMotion(5, 5) && OnMouseButton(false, 1, 5, 5) && _menu.Open && !_menu.RowDrag, "a drag released off the menu is nothing, the menu stays");
            DropRow(0, 0, 1, 0); DropRow(1, 0, 0, 0);
            check.Expect(_menu.Open && _menu.Depth == 1, "a drop between unrelated levels is nothing");

            // Draw-time closes are queued, not executed, and served at PostPresent.
            OpenFake();
            _deferClose = CloseFont;
            check.Expect(_menu.Open, "a queued close leaves the menu open until Service");
            Service();
            check.Expect(!_menu.Open && _deferClose == null, "Service closes on the queued reason");

            OpenFake();
            _deferLevel = 0; _deferRow = 1;
            Service();   // the dwell request on the submenu row: no engine (parent is fake, no resolver) -> Materialize yields 0
            check.Expect(_menu.Open && _menu.Depth == 1 && _deferLevel == -1, "a served dwell with a declined creator leaves the row as it is");

            // An empty child cascades the No actions row: inert to Enter, the popup and a click.
            OpenFake();
            check.Expect(_menu.OpenChild(0, 1, OrEmpty(Array.Empty<CascadeItem>())) && _menu.Depth == 2 && _menu.Level(1).Items.Length == 1
              && _menu.Level(1).Items[0].Disabled && _menu.Level(1).Items[0].Tag == null && SlotOf(1, 0) == -1, "an empty child opens as one disabled No actions row");
            _menu.Level(1).Hover = 0;
            int naY = (int)_menu.RowTop(1, 0) + 1, naX = (int)_menu.Level(1).X + 5;
            check.Expect(OnKey(0x0D, 0) && _menu.Depth == 2, "Enter on No actions does nothing");
            check.Expect(OnMouseButton(true, 3, naX, naY) && !_ctx.Open && OnMouseButton(false, 3, naX, naY), "a right press on No actions opens no popup");
            check.Expect(OnMouseButton(true, 1, naX, naY) && _menu.Depth == 2 && OnMouseButton(false, 1, naX, naY), "a click on No actions is swallowed");

            // The item header: no slot, no popup, Enter does nothing; the rows behind it still map.
            OpenFake(headed);
            check.Expect(SlotOf(0, 0) == -1 && SlotOf(0, 1) == 2 && Lead(headed) == 1, "a null-tag header is no row; the slot rows behind it map");
            _menu.Level(0).Hover = 0;
            check.Expect(OnKey(0x0D, 0) && _menu.Open, "Enter on the header does nothing");
            check.Expect(OnMouseButton(true, 3, 110, 101) && !_ctx.Open, "a right press on the header opens no popup");
            OnMouseButton(false, 3, 110, 101);

            // The row-action popup: right press opens it beside the row, it takes motion and keys, Esc closes only it,
            // a press outside it closes only it, a press outside both is the popup's alone.
            OpenFake();
            int rowY = (int)_menu.RowTop(0, 1) + 1;
            OnMouseMotion(110, rowY);
            _menu.OpenChild(0, 1, new[] { new CascadeItem("child") });
            check.Expect(OnMouseButton(true, 3, 110, rowY) && _ctx.Open && _ctxLevel == 0 && _ctxRow == 1 && _menu.Depth == 1, "a right press on a row opens the popup; the row's open child closes");
            check.Expect(_ctx.Level(0).Items.Length == 6 && (RowAction)_ctx.Level(0).Items[0].Tag! == RowAction.PinToTop && _ctx.Level(0).Items[0].Label == "Move to top"
              && !_ctx.Level(0).Items[1].Disabled && !_ctx.Level(0).Items[2].Disabled && (RowAction)_ctx.Level(0).Items[3].Tag! == RowAction.Hide
              && !_ctx.Level(0).Items[3].Disabled && (RowAction)_ctx.Level(0).Items[4].Tag! == RowAction.MoveTo && (RowAction)_ctx.Level(0).Items[5].Tag! == RowAction.Reset,
                "top-level row: Move to top, Move up, Move down, Hide, Move to, Reset -- no Also show on (a one-step path)");
            check.Expect(_ctx.Level(0).X >= _menu.Level(0).X + _menu.Level(0).W - 1f, "the popup hangs off the row's right edge");
            OnMouseButton(false, 3, 110, rowY);
            check.Expect(OnMouseMotion((int)_ctx.Level(0).X + 5, (int)_ctx.Level(0).Y + 1) && _ctx.Level(0).Hover == 0 && _menu.Level(0).Hover == 1, "motion goes to the popup, the menu's hover holds");
            _deferLevel = 0; _deferRow = 1;
            Service();
            check.Expect(_ctx.Open && _deferLevel == -1 && _menu.Depth == 1, "a dwell queued under the popup is dropped, not served");
            check.Expect(OnKey(0x1B, 0) && !_ctx.Open && _menu.Open, "Esc closes the popup only");
            OnMouseButton(true, 3, 110, rowY); OnMouseButton(false, 3, 110, rowY);
            check.Expect(_ctx.Open && OnMouseButton(true, 1, 5, 5) && !_ctx.Open && _menu.Open, "a press outside the popup closes the popup only");
            OnMouseButton(false, 1, 5, 5);
            OnMouseButton(true, 3, 110, (int)_menu.RowTop(0, 0) + 1); OnMouseButton(false, 3, 110, (int)_menu.RowTop(0, 0) + 1);
            check.Expect(_ctx.Open && _ctx.Level(0).Items[0].Disabled && _ctx.Level(0).Items[1].Disabled && !_ctx.Level(0).Items[2].Disabled, "the first row: Move to top and Move up disabled");
            OnKey(0x1B, 0);
            OnMouseButton(true, 3, 110, (int)_menu.RowTop(0, 2) + 1); OnMouseButton(false, 3, 110, (int)_menu.RowTop(0, 2) + 1);
            check.Expect(_ctx.Open && (RowAction)_ctx.Level(0).Items[0].Tag! == RowAction.Unpin && _ctx.Level(0).Items[2].Disabled && _ctx.Level(0).Items.Length == 6
              && (RowAction)_ctx.Level(0).Items[3].Tag! == RowAction.MoveTo && _ctx.Level(0).Items[3].HasChildren
              && (RowAction)_ctx.Level(0).Items[4].Tag! == RowAction.ShowOn && _ctx.Level(0).Items[4].HasChildren
              && (RowAction)_ctx.Level(0).Items[5].Tag! == RowAction.Reset, "a hoisted row: Unpin, Move down disabled at the end, no Hide, Move to, Also show on");
            // Also show on cascades the other world roots; Move to cascades New folder alone when the root has none.
            int ctxRow4 = (int)_ctx.RowTop(0, 4) + 1, ctxX = (int)_ctx.Level(0).X + 5;
            check.Expect(OnMouseButton(true, 1, ctxX, ctxRow4) && _ctx.Depth == 2 && _ctx.Level(1).Items.Length == 3 && (RootKind)_ctx.Level(1).Items[0].Tag! == RootKind.Placeable
              && (RootKind)_ctx.Level(1).Items[2].Tag! == RootKind.Plain && OnMouseButton(false, 1, ctxX, ctxRow4), "Also show on lists Placeables, Doors, Ground for a Creature root");
            int ctxRow3 = (int)_ctx.RowTop(0, 3) + 1;
            check.Expect(OnMouseButton(true, 1, ctxX, ctxRow3) && _ctx.Depth == 2 && _ctx.Level(1).Items.Length == 1 && _ctx.Level(1).Items[0].Tag is FolderPick { New: true }
              && OnMouseButton(false, 1, ctxX, ctxRow3), "Move to lists New folder alone with no folders");
            check.Expect(PinOf(0, 2, out bool ex) == stale && ex && PinOf(0, 0, out _) == null, "the popup row's pin: the hoist's own; a top-level vanilla row has none");
            RadialPin? f0 = FiledOf(0, 0, out bool fx);
            check.Expect(f0 != null && !fx && !f0.IsHoist && f0.Path.Length == 1 && f0.Path[0] == 2 && f0.Label == "attack" && f0.In == "", "a top-level vanilla row files as a new reorder of its slot, labelled");
            RadialPin filed = RadialPins.AddLike(f0!, RootKind.Creature, 0, "Buffs");
            check.Expect(FiledOf(0, 0, out fx) == filed && fx && filed.In == "Buffs", "a slot already filed answers with that reorder");
            RadialPins.Remove(filed);
            check.Expect(SelfKeyOf(0, 2) == null && SelfKeyOf(0, 0) == null, "no self key off an unvalidated hoist or a row with no engine menu");
            // A self pin row (fake, the unit is unresolved off-device): Enter fires nothing and closes; its key is its own.
            var selfPin = new RadialPin { Root = RootKind.Creature, Path = new[] { 0 }, Fn = SelfActions.Prefix + "rest", Label = "Rest", Icon = "ir_rest" };
            OpenFake(new[] { rows[0], new CascadeItem("Rest", "ir_rest", tag: new RadialRowTag { Slot = -1, Pin = selfPin, Valid = true }) });
            check.Expect(SelfKeyOf(0, 1) == SelfActions.Prefix + "rest" && PinOf(0, 1, out _) == selfPin, "a self pin row answers with its own token and pin");
            _menu.Level(0).Hover = 1;
            check.Expect(OnKey(KeyReturn, 0) && !_menu.Open, "Enter on a self pin with no unit fires nothing and closes");
            OpenFake();
            // The name editor: typed keys build the row, Backspace/Shift work, Esc cancels, Enter commits the trimmed name.
            string? named = null;
            BeginName("", n => named = n);
            check.Expect(_naming != null && _ctx.Open && _ctx.Depth == 1 && _ctx.Level(0).Items[0].Label == "Name: _", "New folder opens the editor in the popup's place");
            check.Expect(OnKey('b', ModShift) && OnKey('u', 0) && OnKey('f', 0) && OnKey('x', 0) && OnKey(KeyBackspace, 0) && OnKey('f', ModCaps) && OnKey('s', 0)
              && OnKey(0x40000051, 0) && _ctx.Level(0).Items[0].Label == "Name: BufFs_", $"keys edit the name (\"{_ctx.Level(0).Items[0].Label}\")");
            check.Expect(OnKey(KeyEscape, 0) && _naming == null && !_ctx.Open && named == null && _menu.Open, "Esc cancels the editor and the popup; nothing committed");
            BeginName("old", n => named = n);
            OnKey(KeyBackspace, 0); OnKey(KeyBackspace, 0); OnKey(KeyBackspace, 0); OnKey(' ', 0); OnKey('a', 0); OnKey(' ', 0);
            check.Expect(OnKey(KeyReturn, 0) && named == "a" && _naming == null && !_ctx.Open, "Enter commits the trimmed name");
            BeginName("", n => named = n);
            named = null;
            check.Expect(OnKey(KeyReturn, 0) && named == null && !_ctx.Open, "an empty name commits nothing");
            Close(CloseEsc);
            // A folder row: its popup, its level (drop target, hoists resolved from the root -- greyed here, no root), Enter descends.
            var fold = new RadialPin { Root = RootKind.Creature, Folder = "Buffs" };
            OpenFake(new[] { rows[0], new CascadeItem("Buffs", hasChildren: true, count: "1", tag: new RadialRowTag { Pin = fold, Valid = true }) });
            int fY = (int)_menu.RowTop(0, 1) + 1;
            OnMouseButton(true, 3, 110, fY); OnMouseButton(false, 3, 110, fY);
            check.Expect(_ctx.Open && _ctx.Level(0).Items.Length == 5 && (RowAction)_ctx.Level(0).Items[0].Tag! == RowAction.Rename
              && (RowAction)_ctx.Level(0).Items[3].Tag! == RowAction.RemoveFolder && (RowAction)_ctx.Level(0).Items[4].Tag! == RowAction.Reset, "a folder row: Rename, Move up/down, Remove folder, Reset");
            OnKey(KeyEscape, 0);
            _menu.Level(0).Hover = 1;
            check.Expect(OnKey(KeyReturn, 0) && _menu.Open && _menu.Depth == 2 && _levelFolder[1] == "Buffs" && _menu.Level(1).DropTarget
              && _menu.Level(1).Items.Length == 1 && _menu.Level(1).Items[0].Disabled, "Enter on a folder opens its level as a drop target; empty = No actions");
            check.Expect(PinOf(0, 1, out _) == null && FiledOf(0, 1, out _) == null, "a folder row is no pin");
            Close(CloseEsc);
            // A filed slot inside its folder level: the root's own row (int tag, one-step path); its popup says Move to top,
            // offers no Hide, no Also show on; Move to lists Top level; it never mints a hoist.
            OpenFake(new[] { rows[0], new CascadeItem("Buffs", hasChildren: true, count: "1", tag: new RadialRowTag { Pin = fold, Valid = true }) });
            RadialPin filedRow = RadialPins.AddLike(FiledOf(0, 0, out _)!, RootKind.Creature, 0, "Buffs");
            _menu.OpenChild(0, 1, new[] { rows[0] }); _levelFolder[1] = "Buffs"; _levelPath[1] = Array.Empty<int>();
            int fr = (int)_menu.RowTop(1, 0) + 1, fx1 = (int)_menu.Level(1).X + 5;
            OnMouseButton(true, 3, fx1, fr); OnMouseButton(false, 3, fx1, fr);
            check.Expect(_ctx.Open && _ctxLevel == 1 && _ctx.Level(0).Items.Length == 5 && _ctx.Level(0).Items[0].Label == "Move to top"
              && (RowAction)_ctx.Level(0).Items[3].Tag! == RowAction.MoveTo && (RowAction)_ctx.Level(0).Items[4].Tag! == RowAction.Reset,
                "a filed slot's popup: Move to top, Move up/down, Move to, Reset");
            int mv = (int)_ctx.RowTop(0, 3) + 1, cx1 = (int)_ctx.Level(0).X + 5;
            check.Expect(OnMouseButton(true, 1, cx1, mv) && _ctx.Depth == 2 && _ctx.Level(1).Items[0].Label == "Top level" && OnMouseButton(false, 1, cx1, mv), "Move to lists Top level from inside the folder");
            check.Expect(FiledOf(1, 0, out bool fex) == filedRow && fex && PinOf(1, 0, out _) == null, "the filed slot's pin is its reorder, never a one-step hoist");
            RadialPins.Remove(filedRow);
            Close(CloseEsc);
            check.Expect(_levelFolder[1] == null, "Close clears the folder markers");
            OpenFake(new[] { rows[0] });
            OnMouseButton(true, 3, 110, r0); OnMouseButton(false, 3, 110, r0);
            check.Expect(_ctx.Open && (RowAction)_ctx.Level(0).Items[3].Tag! == RowAction.Hide && _ctx.Level(0).Items[3].Disabled, "the last slot row of a list cannot be hidden");
            Close(CloseEsc);
            OpenFake(new[] { rows[2], rows[0] });   // a hoist does not count as a slot row
            OnMouseButton(true, 3, 110, (int)_menu.RowTop(0, 1) + 1); OnMouseButton(false, 3, 110, (int)_menu.RowTop(0, 1) + 1);
            check.Expect(_ctx.Open && (RowAction)_ctx.Level(0).Items[3].Tag! == RowAction.Hide && _ctx.Level(0).Items[3].Disabled, "the last slot row beside a hoist cannot be hidden either");
            Close(CloseEsc);
            check.Expect(SlotRows(Without(rows, 2)) == 1 && SlotRows(Without(Without(rows, 2), 1)) == 0 && Without(rows, 9).Length == 3, "Without drops the slot's row; SlotRows counts int tags only");
            RadialKnobs.Lock.V = true;
            OpenFake();
            check.Expect(OnMouseButton(true, 3, 110, rowY) && !_ctx.Open && OnMouseButton(false, 3, 110, rowY) && !_menu.DragEnabled, "locked: a right press on a row opens no popup, rows do not drag");
            RadialKnobs.Lock.V = false;
            Close(CloseEsc);
            check.Expect(!_ctx.Open && _ctxLevel == -1, "Close drops the popup");

            OpenFake();
            Close(CloseArea);
            check.Expect(!_menu.Open && _levelMenu[0] == IntPtr.Zero && _deferLevel == -1, "the area-edge close clears the widget and the handles");
            Close(CloseArea);
            check.Expect(!_menu.Open, "Close is idempotent");
        }
        finally
        {
            _menu.Close(); _ctx.Close(); _ctxLevel = _ctxRow = -1; _naming = null; Array.Clear(_levelMenu); Array.Clear(_levelPath); Array.Clear(_levelFolder);
            _pressOwned = false; _deferLevel = _deferRow = -1; _deferClose = null; _kind = RootKind.Unknown; _atlas = null;
            _nullChildWarned = false;
            _log = savedLog;
            RadialKnobs.Enabled.V = wasEnabled; RadialKnobs.Lock.V = wasLocked; _menu.DragEnabled = true;
        }
        log.Info(check.Ok ? "RadialService.SelfCheck: passed" : "RadialService.SelfCheck: FAILED");
    }
}
