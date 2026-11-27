using System;
using CX.Framework;
using CX.Services.Camera;
using CX.Services.Interact;

namespace CX.Ui;

/// <summary>The key row's edges no approved example names: a key with no name, a modifier bound to Interact, the
/// warning's own keys and lines, the cursor keys' clashes and the camera toggle yielding a thumb button to Interact. Oracle: the key names and codes written here.</summary>
public sealed partial class KnobPanel
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool KeyRowUnasked(ExtenderLog log)
    {
        var check = new Checker(log, "KeyRowUnasked.SelfCheck");
        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x180; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }

        KnobPanel Fresh()
        {
            InteractWorldDouble.Install();
            InteractKnobs.Key.Set("F");
            TabTarget.LockKey.Set("");
            var p = new KnobPanel { Category = Array.IndexOf(KnobCategories, "Controls") };
            p.OpenAt(1280f, 720f, FacePx, body, small);
            p._subs = new[] { new SubMenu(InteractKnobs.G) }; p.Sub = 0; p.LayoutRows();
            return p;
        }
        try
        {
            {
                KnobPanel p = Fresh();
                Row r = Wait(p, InteractKnobs.Key);
                check.Expect(p.KeyBoxText(r) == WaitText, $"U1 the waiting row's box reads '{WaitText}': '{p.KeyBoxText(r)}'");
                Row? loot = Find(p, InteractKnobs.LootKey);
                check.Expect(loot != null && !p.Waiting(loot) && p.KeyBoxText(loot) == loot.Value, $"U1 the Loot row beside it does not wait and shows its own key ('{(loot == null ? "" : p.KeyBoxText(loot))}')");
                p.Key(0x40000000);  // not-an-offset: SDL key/event code
                check.Expect(p.Waiting(r) && InteractKnobs.Key.V == "F", $"U1 a key with no name (SDL's unknown scancode) keeps the row waiting and F bound ({InteractKnobs.Key.V})");
                p.Key('g');
                check.Expect(!p.Waiting(r) && InteractKnobs.Key.V == "G", $"U1 G after it binds G ({InteractKnobs.Key.V})");
            }
            {
                CameraModes.Chase.ActionKey.Set("]");
                KnobPanel p = Fresh();
                Wait(p, InteractKnobs.Key);
                p.Key(']');
                Row? warned = Find(p, InteractKnobs.Key);
                check.Expect(warned != null && warned.Warned && string.Join(" ", warned.Help).Contains("action camera", StringComparison.OrdinalIgnoreCase) && p.KeyBoxText(warned) == "]",
                    $"U2 the warning shows in the row's lines and its box holds ]: '{(warned == null ? "" : string.Join(" ", warned.Help))}'");
                Row? lootRow = Find(p, InteractKnobs.LootKey);
                check.Expect(lootRow != null && !lootRow.Warned, "U2 the warning shows on the waiting row alone, not on the Loot row");
                p.Key(0x0D);
                Row? after = Find(p, InteractKnobs.Key);
                check.Expect(InteractKnobs.Key.V == "]" && p.KeyWarning == null && after != null && !after.Warned && !string.Join(" ", after.Help).Contains("action camera", StringComparison.OrdinalIgnoreCase),
                    $"U2 Enter binds ] and the row's help is back ({InteractKnobs.Key.V}, '{(after == null ? "" : string.Join(" ", after.Help))}')");
            }
            {
                CameraModes.Chase.ActionKey.Set("]");
                KnobPanel p = Fresh();
                Row r = Wait(p, InteractKnobs.Key);
                p.Key(']');
                p.Key(0x1B);
                check.Expect(p.Open && !p.Waiting(r) && p.KeyWarning == null && InteractKnobs.Key.V == "F", $"U3 Escape on the warning keeps F and the panel open ({InteractKnobs.Key.V}, open {p.Open})");
                Row again = Wait(p, InteractKnobs.Key);
                check.Expect(p.KeyBoxText(again) == WaitText, $"U3 the next wait reads '{WaitText}', not the declined ] ('{p.KeyBoxText(again)}')");
            }
            {
                CameraModes.Chase.ActionKey.Set("]");
                KnobPanel p = Fresh();
                Row r = Wait(p, InteractKnobs.Key);
                p.Key(']');
                Row? warned = Find(p, InteractKnobs.Key);
                bool consumed = false;
                if (warned != null)
                {
                    p.BoxRect(warned, p.List.ToScreen(warned.Top), out float x0, out float x1, out float y0, out float y1);
                    consumed = p.MouseDown((x0 + x1) * 0.5f, (y0 + y1) * 0.5f); p.MouseUp();
                }
                check.Expect(consumed && !p.Waiting(r) && p.KeyWarning == null && InteractKnobs.Key.V == "F",
                    $"U4 a click on the box while the warning shows keeps F and starts no new wait ({InteractKnobs.Key.V}, waiting {p.Waiting(r)})");
                check.Expect(!p.KeyButton(-4), "U4 a thumb button with no row waiting is not the panel's");
            }
            {
                InteractWorldDouble.Install();
                InteractKnobs.Key.Set("F");
                bool thumb = MouseLookService.Flips(-4, -4), key = MouseLookService.Flips(']', ']');
                InteractKnobs.Key.Set("Mouse4");
                bool thumbShared = MouseLookService.Flips(-4, -4);
                InteractKnobs.Key.Set("]");
                bool keyShared = MouseLookService.Flips(']', ']');
                check.Expect(thumb && key, $"U5 control: with Interact on F the toggle's own key and thumb button flip the action camera (Mouse4 {thumb}, ] {key})");
                check.Expect(!thumbShared && !keyShared, $"U5 bound to Interact too, they run Interact and leave the action camera (Mouse4 {thumbShared}, ] {keyShared})");
            }
            {
                KnobPanel p = Fresh();
                Row r = Wait(p, InteractKnobs.Key);
                bool right = p.KeyButton(-3);
                check.Expect(!right && p.Waiting(r), $"U6 the right button is not a key a row can take: the row still waits (took {right})");
                p.ConfirmKey();
                check.Expect(InteractKnobs.Key.V == "F", $"U6 a confirm with no warning showing binds nothing ({InteractKnobs.Key.V})");
                p.Key('f');
                Row? row = Find(p, InteractKnobs.Key);
                bool inLabel = false;
                if (row != null)
                {
                    float top = p.List.ToScreen(row.Top);
                    p.BoxRect(row, top, out float x0, out _, out _, out _);
                    p.MouseDown(x0 - 4f * p.Em, top + row.H * 0.5f); p.MouseUp();
                    inLabel = p.Waiting(row);
                }
                check.Expect(row != null && !inLabel, "U6 a press on the row's label, left of its box, does not start a wait");
            }
            {
                InteractWorldDouble.Install();
                CameraModes.Chase.ActionKey.Set("");
                string? clash = KeyClash(InteractKnobs.Key, "Nonesuch");
                check.Expect(clash == null, $"U7 a name with no key code clashes with nothing, not with an unbound camera toggle ('{clash}')");
            }
            {
                KnobPanel p = Fresh();
                TabTarget.Enabled.Set(true);
                TabTarget.LockKey.Set("F");
                InteractKnobs.Key.Set("");
                Wait(p, InteractKnobs.Key);
                p.Key('f');
                string? warn = p.KeyWarning;
                check.Expect(warn != null && warn.Contains(TabTarget.LockKey.Label) && InteractKnobs.Key.V == "",
                    $"U9 binding Interact to F, tab-target's lock-on key, warns naming that setting and binds nothing yet: '{warn}', bound '{InteractKnobs.Key.V}'");
            }
            {
                KnobPanel p = Fresh();
                Wait(p, InteractKnobs.LootKey);
                p.Key('f');
                string? warn = p.KeyWarning;
                check.Expect(warn != null && warn.Contains(InteractKnobs.Key.Label) && InteractKnobs.LootKey.V == "",
                    $"U10 binding Loot to F, the Interact key, warns naming the Interact row and binds nothing yet: '{warn}', bound '{InteractKnobs.LootKey.V}'");
            }
            {
                InteractWorldDouble w = InteractWorldDouble.Install();
                InteractKnobs.Key.Set("F");
                w.KeysFree = false;
                bool typing = InteractInput.KeyDown('f', false);
                check.Expect(!typing, "U8 with a text box taking keys, F is the text box's, not Interact's");
                InteractKnobs.Key.Set("");
                check.Expect(!InteractInput.Chord.WantsKey(0), "U8 an unbound Interact key wants no press, not code 0");
                InteractKnobs.Key.Set("]");
                InteractInput.Use(null);
                check.Expect(MouseLookService.Flips(']', ']'), "U8 with no world in, Interact takes nothing, so ] still toggles the action camera");
            }
            {
                InteractWorldDouble w = InteractWorldDouble.Install();
                InteractKnobs.Key.Set("Left Shift");
                uint bag = w.Bag(3f);
                bool took = w.PressCode(LeftShift);
                bool repeat = w.PressCode(LeftShift, repeat: true);
                check.Expect(!took && !repeat && w.Did(bag), $"U11 Interact on Left Shift acts and hands the press and its repeat on to the game: took {took}, repeat {repeat}, {w.Seen}");
                InteractKnobs.Key.Set("F");
                check.Expect(w.Press('F'), "U11 a key that is no modifier is still Interact's alone");
            }
            {
                InteractWorldDouble.Install();
                Knob<string> chase = CameraModes.Chase.CursorKey, topDown = CameraModes.TopDown.CursorKey;
                string[] was = { chase.FormatStored(), topDown.FormatStored() };
                string?[] raw = { Settings.Raw(chase.Key), Settings.Raw(topDown.Key) };
                try
                {
                    chase.Set("Space");
                    TabTarget.LockKey.Set("");
                    string? peer = KeyClash(topDown, "Space");
                    string? interact = KeyClash(topDown, "F");
                    string? back = KeyClash(InteractKnobs.Key, "Space");
                    check.Expect(peer == null && interact == "F is already bound to Interact in Interact." && back != null && back.Contains("Cursor key"),
                        $"U12 a cursor key clashes with no other mode's but with every other key, both ways: peer '{peer}', Interact '{interact}', from Interact '{back}'");
                }
                finally
                {
                    Knob<string>[] rows = { chase, topDown };
                    for (int i = 0; i < rows.Length; i++)
                    {
                        if (rows[i].FormatStored() != was[i]) rows[i].Parse(was[i]);
                        if (raw[i] is { } v) Settings.Read($"{rows[i].Key}={v}\n");
                        else Settings.Forget(rows[i].Key);
                    }
                }
            }
            {
                InteractWorldDouble.Install();
                var g = new KnobGroup("cx-keyrow-parent", "Test", "Test", "on", "x", true, Effect.Live, null, "interact.enabled", "", register: false);
                bool on = g.SwitchedOff;
                InteractKnobs.Enabled.Set(false);
                bool parentOff = g.SwitchedOff;
                InteractKnobs.Enabled.Set(true);
                g.Master!.Set(false);
                check.Expect(!on && parentOff && g.SwitchedOff, $"U14 a group reads switched off when its parent's master is off, or its own: on {on}, parent off {parentOff}, own off {g.SwitchedOff}");
            }
            {
                string[] names = { "Left Ctrl", "Left Shift", "Left Alt", "Left GUI", "Right Ctrl", "Right Shift", "Right Alt", "Right GUI" };
                int round = 0;
                for (int i = 0; i < names.Length; i++)
                    if (KeyNames.Name(FirstModifier + i) == names[i] && KeyNames.Code(names[i]) == FirstModifier + i && KeyNames.Code(names[i].ToLowerInvariant()) == FirstModifier + i && KeyNames.IsModifier(FirstModifier + i)) round++;
                check.Expect(round == 8 && !KeyNames.IsModifier(FirstModifier - 1) && !KeyNames.IsModifier(FirstModifier + 8) && KeyNames.Name(FirstModifier + 8) != "Right GUI",
                    $"U13 KeyNames names the eight modifiers and reads each back, and no code beside them is one: {round} of 8");
            }
        }
        finally
        {
            KnobPanelService.Close();
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "KeyRowUnasked.SelfCheck: passed" : "KeyRowUnasked.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>SDL's codes for Left Ctrl, the first modifier key, and Left Shift.</summary>
    private const int FirstModifier = 0x400000E0, LeftShift = 0x400000E1;  // not-an-offset: SDL key code
}
