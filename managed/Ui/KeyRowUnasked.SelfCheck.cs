using System;
using CX.Framework;
using CX.Services.Camera;
using CX.Services.Interact;

namespace CX.Ui;

/// <summary>The key row's edges no approved example names: a modifier press, the warning's own keys and lines, and
/// the camera toggle yielding a thumb button to Interact. Oracle: the key names and codes written here.</summary>
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
                p.Key(0x4000003A);  // not-an-offset: SDL key/event code
                check.Expect(p.Waiting(r) && InteractKnobs.Key.V == "F", $"U1 a key with no name (F1 on a host without SDL) keeps the row waiting and F bound ({InteractKnobs.Key.V})");
                p.Key(0x400000E1);
                check.Expect(p.Waiting(r) && InteractKnobs.Key.V == "F", $"U1 a Shift press alone keeps the row waiting and binds nothing ({InteractKnobs.Key.V})");
                p.Key('g');
                check.Expect(!p.Waiting(r) && InteractKnobs.Key.V == "G", $"U1 G after it binds G, no modifier ({InteractKnobs.Key.V})");
            }
            {
                CameraModes.Chase.ActionKey.Set("]");
                KnobPanel p = Fresh();
                Wait(p, InteractKnobs.Key);
                p.Key(']');
                Row? warned = Find(p, InteractKnobs.Key);
                check.Expect(warned != null && warned.Warned && string.Join(" ", warned.Help).Contains("action camera") && p.KeyBoxText(warned) == "]",
                    $"U2 the warning shows in the row's lines and its box holds ]: '{(warned == null ? "" : string.Join(" ", warned.Help))}'");
                Row? lootRow = Find(p, InteractKnobs.LootKey);
                check.Expect(lootRow != null && !lootRow.Warned, "U2 the warning shows on the waiting row alone, not on the Loot row");
                p.Key(0x0D);
                Row? after = Find(p, InteractKnobs.Key);
                check.Expect(InteractKnobs.Key.V == "]" && p.KeyWarning == null && after != null && !after.Warned && !string.Join(" ", after.Help).Contains("action camera"),
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
        }
        finally
        {
            KnobPanelService.Close();
            CameraModes.Chase.ActionKey.Reset();
            MouseLook.Action.Reset();
            InteractWorldDouble.Defaults();
            InteractInput.Use(null);
        }
        log.Info(check.Ok ? "KeyRowUnasked.SelfCheck: passed" : "KeyRowUnasked.SelfCheck: FAILED");
        return check.Ok;
    }
}
