using System;
using CX.Framework;
using CX.Services.Camera;
using CX.Services.Interact;

namespace CX.Ui;

/// <summary>The press-to-bind key rows' approved examples: the Interact group's real rows in a panel the test drives
/// as the player would, or in the settings service's own panel pressed through the UI host, then a press in play on a
/// world double. Oracle: the key names, codes and Key Mapping names written here.</summary>
public sealed partial class KnobPanel
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool KeyRowApprovedExamples(ExtenderLog log)
    {
        var check = new Checker(log, "KeyRowApprovedExamples.SelfCheck");
        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x180; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }
        foreach (int cp in new[] { 0x2014, 0x21BB, 0x25B8, 0x25BE }) { body.SetTestGlyph(cp, 11f); small.SetTestGlyph(cp, 8f); }

        bool inGame = GameGate.InGame;

        (KnobPanel, InteractWorldDouble) Fresh()
        {
            var w = InteractWorldDouble.Install();
            InteractWorldDouble.Defaults();
            var p = new KnobPanel { Category = Array.IndexOf(KnobCategories, "Controls") };
            p.OpenAt(1280f, 720f, FacePx, body, small);
            p._subs = new[] { new SubMenu(InteractKnobs.G) }; p.Sub = 0; p.LayoutRows();
            return (p, w);
        }

        KnobPanel Live()
        {
            GameGate.InGame = true;
            KnobPanelService.Open(InteractKnobs.G);
            KnobPanel p = KnobPanelService.Panel;
            p.Rebase(1280f, 720f, FacePx, body, small);
            p.Show(InteractKnobs.G);
            return p;
        }
        try
        {
            // Given the settings panel
            // When the player opens Controls
            // Then Interact sits beside Hotkeys, with its own master toggle and its key rows
            {
                KnobGroup hotkeys = CX.Services.Hotkeys.Hotkeys.G;
                var p = new KnobPanel { Category = Array.IndexOf(KnobCategories, "Controls") };
                p.OpenAt(1280f, 720f, FacePx, body, small);
                int hk = Array.FindIndex(p._subs, m => Array.IndexOf(m.Groups, hotkeys) >= 0);
                int ix = Array.FindIndex(p._subs, m => Array.IndexOf(m.Groups, InteractKnobs.G) >= 0);
                check.Expect(hk >= 0 && ix == hk + 1 && p._subs[ix].Name == "Interact",
                    $"E19 Interact is its own submenu in Controls, right after Hotkeys: Hotkeys at {hk}, Interact at {ix}");
                if (ix >= 0) { p.SelectSub(ix); p.LayoutRows(); }
                Row? master = InteractKnobs.G.Master is Knob m0 ? Find(p, m0) : null;
                int keys = 0, waits = 0;
                foreach (Knob k in InteractKnobs.G.Knobs)
                {
                    if (k.Kind != KnobKind.Key) continue;
                    keys++;
                    if (p.Waiting(Wait(p, k))) waits++;
                    p.Key(KeyEscape);
                }
                check.Expect(master != null && keys == 6 && waits == 6,
                    $"E19 the submenu shows its own master toggle and six key rows, each waiting for a key when pressed: master {master != null}, {keys} key rows, {waits} waited");
            }

            // Given the Interact key row in the settings panel
            // When the player clicks the row and presses F
            // Then the row reads F, and pressing F in play runs Interact
            {
                var (p, w) = Fresh();
                uint bag = w.Bag(3f);
                Row r = Wait(p, InteractKnobs.Key);
                check.Expect(p.Waiting(r), "E1 a press on the row's box sets it waiting for a key");
                p.Key('f');
                check.Expect(InteractKnobs.Key.V == "F" && Value(p, InteractKnobs.Key) == "F", $"E1 the row reads F ({InteractKnobs.Key.V} / {Value(p, InteractKnobs.Key)})");
                check.Expect(w.Press('F') && w.Did(bag), $"E1 F in play runs Interact: {w.Seen}");
            }

            // Given a key row waiting for a key
            // When the player presses Escape
            // Then the row keeps the key it had
            {
                var (p, _) = Fresh();
                InteractKnobs.Key.Set("F");
                Row r = Wait(p, InteractKnobs.Key);
                check.Expect(p.Waiting(r), "E2 the row waits for a key");
                p.Key(0x1B);
                check.Expect(!p.Waiting(r) && InteractKnobs.Key.V == "F" && Value(p, InteractKnobs.Key) == "F", $"E2 Escape keeps the key it had ({InteractKnobs.Key.V})");
            }

            // Given a key row waiting for a key
            // When the player presses Backspace
            // Then the row reads Unbound, and no key runs that action
            {
                var (p, w) = Fresh();
                w.Bag(3f);
                InteractKnobs.Key.Set("F");
                Row r = Wait(p, InteractKnobs.Key);
                check.Expect(p.Waiting(r), "E3 the row waits for a key");
                p.Key(0x08);
                check.Expect(InteractKnobs.Key.V == "" && Value(p, InteractKnobs.Key) == "Unbound", $"E3 Backspace unbinds and the row reads Unbound ({Value(p, InteractKnobs.Key)})");
                check.Expect(!w.Press('F') && w.Acts.Count == 0, $"E3 F no longer runs Interact: {w.Seen}");
            }

            // Given a key row waiting for a key
            // When the player presses the mouse thumb button
            // Then the row reads Mouse4 and the thumb button runs the action
            {
                var w = InteractWorldDouble.Install();
                uint bag = w.Bag(3f);
                KnobPanel p = Live();
                Row r = Press(p, InteractKnobs.Key, out int x, out int y);
                bool waited = p.Waiting(r);
                bool took = UiHost.DispatchMouseButton(true, ThumbButton, x, y);
                UiHost.DispatchMouseButton(false, ThumbButton, x, y);
                check.Expect(p.Open && waited && took, $"E4 the waiting row takes the thumb button from the UI host: open {p.Open}, waited {waited}, took {took}");
                check.Expect(InteractKnobs.Key.V == "Mouse4" && Value(p, InteractKnobs.Key) == "Mouse4", $"E4 the row reads Mouse4 ({Value(p, InteractKnobs.Key)})");
                KnobPanelService.Close();
                check.Expect(w.PressCode(-4) && w.Did(bag), $"E4 the thumb button runs Interact: {w.Seen}");
            }

            // Given ] already toggles the action camera
            // When the player binds ] to Interact
            // Then the row warns that ] already toggles the action camera, and binds it only when the player confirms
            {
                CameraModes.Chase.ActionKey.Set("]");
                var (p, _) = Fresh();
                Row r = Wait(p, InteractKnobs.Key);
                p.Key(']');
                string? warn = p.KeyWarning;
                check.Expect(warn != null && warn.Contains(']') && warn.Contains("action camera", StringComparison.OrdinalIgnoreCase), $"E5 the row warns that ] toggles the action camera: '{warn}'");
                check.Expect(InteractKnobs.Key.V == "", $"E5 nothing is bound before the confirm ({InteractKnobs.Key.V})");
                p.ConfirmKey();
                check.Expect(InteractKnobs.Key.V == "]", $"E5 the confirm binds ] ({InteractKnobs.Key.V})");
            }

            // Given F is bound to Interact and the game binds F to something too
            // When the player presses F in play
            // Then only Interact runs
            {
                var (_, w) = Fresh();
                InteractKnobs.Key.Set("F");
                w.GameKeys['f'] = "Rest";
                uint bag = w.Bag(3f);
                bool took = w.Press('F');
                check.Expect(took && w.Did(bag), $"E6 Interact takes F and the game's binding never sees it: took {took}, {w.Seen}");
            }

            // Given a player who never opened the Interact settings
            // When they open them
            // Then they see the Interact row and one row each for Transition, Loot, Pick up, Door and Talk, and every row reads Unbound
            {
                var (p, _) = Fresh();
                var keys = new[] { InteractKnobs.Key, InteractKnobs.TransitionKey, InteractKnobs.LootKey, InteractKnobs.PickUpKey, InteractKnobs.DoorKey, InteractKnobs.TalkKey };
                var labels = new[] { "Interact", "Transition", "Loot", "Pick up", "Door", "Talk" };
                for (int i = 0; i < keys.Length; i++)
                    check.Expect(Find(p, keys[i]) is { } row && row.Text == labels[i] && row.Value == "Unbound", $"E7 the {labels[i]} row is shown and reads Unbound ({Find(p, keys[i])?.Value})");
            }

            // Given the game's Key Mapping binds Q to one of its actions
            // When the player binds Q to Interact
            // Then the row warns that Q already does that action, named as the Key Mapping names it, and binds it only when the player confirms
            {
                var (p, w) = Fresh();
                w.GameKeys['q'] = "Quick Chat";
                Wait(p, InteractKnobs.Key);
                p.Key('q');
                string? warn = p.KeyWarning;
                check.Expect(warn != null && warn.Contains('Q') && warn.Contains("Quick Chat"), $"E68 the row warns with the Key Mapping name: '{warn}'");
                check.Expect(InteractKnobs.Key.V == "", $"E68 nothing is bound before the confirm ({InteractKnobs.Key.V})");
                p.ConfirmKey();
                check.Expect(InteractKnobs.Key.V == "Q", $"E68 the confirm binds Q ({InteractKnobs.Key.V})");
            }

            // Given the ] warning is showing
            // When the player declines
            // Then the Interact row keeps the key it had, and ] still toggles the action camera
            {
                CameraModes.Chase.ActionKey.Set("]");
                var (p, _) = Fresh();
                InteractKnobs.Key.Set("F");
                Wait(p, InteractKnobs.Key);
                p.Key(']');
                check.Expect(p.KeyWarning != null, "E69 the ] warning shows");
                p.DeclineKey();
                check.Expect(p.KeyWarning == null && InteractKnobs.Key.V == "F" && CameraModes.Chase.ActionKey.V == "]", $"E69 the row keeps F and ] still toggles the action camera ({InteractKnobs.Key.V}, {CameraModes.Chase.ActionKey.V})");
            }

            // Given the ] warning is showing
            // When the player confirms
            // Then the Interact row reads ], and pressing ] in play runs Interact and does not toggle the action camera
            {
                CameraModes.Chase.ActionKey.Set("]");
                var (p, w) = Fresh();
                uint bag = w.Bag(3f);
                bool action = MouseLook.Action.V;
                Wait(p, InteractKnobs.Key);
                p.Key(']');
                check.Expect(p.KeyWarning != null, "E70 the ] warning shows");
                p.ConfirmKey();
                check.Expect(InteractKnobs.Key.V == "]" && Value(p, InteractKnobs.Key) == "]", $"E70 the row reads ] ({Value(p, InteractKnobs.Key)})");
                bool took = w.PressUi(']');
                IChordConsumer? owner = UiHost.ResolveChord(']');
                check.Expect(!took && w.Did(bag) && ReferenceEquals(owner, InteractInput.Chord) && MouseLook.Action.V == action,
                    $"E70 ] in play runs Interact, which the chord layer ranks over the camera toggle, and the action camera stays {action}: owner {owner?.GetType().Name ?? "none"}, action {MouseLook.Action.V}, {w.Seen}");
            }

            // Given a key row waiting for a key
            // When the player presses ]
            // Then only the row takes it, and the action camera does not toggle
            {
                CameraModes.Chase.ActionKey.Set("]");
                var w = InteractWorldDouble.Install();
                bool action = MouseLook.Action.V;
                KnobPanel p = Live();
                Row r = Press(p, InteractKnobs.Key, out _, out _);
                check.Expect(p.Waiting(r), "E71 the row waits for a key");
                bool took = w.PressUi(']');
                check.Expect(took && p.KeyWarning != null && w.Acts.Count == 0 && MouseLook.Action.V == action,
                    $"E71 the settings window takes ] (its clash warning shows), nothing runs in play and the action camera stays {action}: took {took}, action {MouseLook.Action.V}, {w.Seen}");
                KnobPanelService.Close();
            }

            // Given F is bound to Interact
            // When the player types F in the chat box
            // Then F appears in the chat line, and Interact does not run
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Key.Set("F");
                uint bag = w.Bag(3f);
                bool tookFree = w.PressUi('f');
                check.Expect(!tookFree && w.Did(bag), $"E72 control: with no text box focused F runs Interact: {w.Seen}");
                w.Acts.Clear();
                var typed = new System.Text.StringBuilder();
                var chat = new Window(0, 0, 10, 10) { WantsKeyboard = true, OnKey = (key, _) => { typed.Append((char)key); return true; } };
                try
                {
                    bool took = w.PressUi('f');
                    check.Expect(took && typed.ToString() == "f" && w.Acts.Count == 0, $"E72 typing in the chat box: F reaches the chat line and Interact does not run: took {took}, typed '{typed}', {w.Seen}");
                }
                finally { UiHost.Unregister(chat); }
            }

            // Given a key row waiting for a key
            // When the player clicks outside the row
            // Then the row keeps the key it had
            {
                var (p, _) = Fresh();
                InteractKnobs.Key.Set("F");
                Row r = Wait(p, InteractKnobs.Key);
                check.Expect(p.Waiting(r), "E73 the row waits for a key");
                p.List.Offset = float.MaxValue; p.List.Clamp();
                Row last = p.Rows[^1];
                float ex = p.List.X + p.Pad + 2f, ey = p.List.ToScreen(last.Top + last.H) + 0.5f * p.Em;
                bool empty = p.Contains(ex, ey) && p.RowAt(ex, ey, out _) == null;
                p.MouseDown(ex, ey); p.MouseUp();
                check.Expect(empty && !p.Waiting(r) && InteractKnobs.Key.V == "F", $"E73 a click on empty panel space stops the wait and keeps F (empty {empty}, {InteractKnobs.Key.V})");
            }

            // Given F is bound to Interact
            // When the player quits and starts the game again
            // Then the row reads F, and F runs Interact
            {
                var (p, w) = Fresh();
                uint bag = w.Bag(3f);
                InteractKnobs.Key.Set("F");
                string cfg = KnobFile.Compose();
                bool written = cfg.Contains("interact.key=F"), had = Settings.Raw(InteractKnobs.Key.Key) != null;
                try
                {
                    InteractKnobs.Key.Reset();
                    Settings.Read(cfg);
                    InteractKnobs.Key.Parse(Settings.Raw(InteractKnobs.Key.Key) ?? "");
                    check.Expect(written && InteractKnobs.Key.V == "F" && Value(p, InteractKnobs.Key) == "F", $"E74 the binding is written to cx.cfg and reads F after a reload (written {written}, {InteractKnobs.Key.V})");
                    check.Expect(w.Press('F') && w.Did(bag), $"E74 F runs Interact after the reload: {w.Seen}");
                }
                finally { if (!had) Settings.Forget(InteractKnobs.Key.Key); }
            }

            // Given the settings panel
            // When the player opens the action camera toggle key row
            // Then it is the text field it is today
            {
                var (ip, _) = Fresh();
                bool keyRowWaits = ip.Waiting(Wait(ip, InteractKnobs.Key));
                Knob<string> action = CameraModes.Chase.ActionKey;
                var p = new KnobPanel { Category = Array.IndexOf(KnobCategories, action.Group.Category) };
                p.OpenAt(1280f, 720f, FacePx, body, small);
                p.Show(action.Group);
                Row r = Wait(p, action);
                check.Expect(keyRowWaits && p.Editing && !p.Waiting(r),
                    $"E75 beside a press-to-bind key row (it waits: {keyRowWaits}), a press on the action camera toggle key row opens it as a text field: editing {p.Editing}, waiting {p.Waiting(r)}");
            }
        }
        finally
        {
            KnobPanelService.Close();
            GameGate.InGame = inGame;
            CameraModes.Chase.ActionKey.Reset();
            MouseLook.Action.Reset();
            InteractWorldDouble.Defaults();
            InteractInput.Use(null);
        }
        return check.Ok;
    }

    private static Row? Find(KnobPanel p, Knob k)
    {
        foreach (Row r in p.Rows) if (r.Kind == RowKind.Knob && ReferenceEquals(r.K, k)) return r;
        return null;
    }

    private static string? Value(KnobPanel p, Knob k) { p.LayoutRows(); return Find(p, k)?.Value; }

    private static void Show(KnobPanel p, Row r) { p.List.Offset = r.Top - 0.4f * p.Em; p.List.Clamp(); }

    /// <summary>SDL's number for the first thumb button, X1, which the row names Mouse4.</summary>
    private const int ThumbButton = 4;

    /// <summary>A left press on <paramref name="k"/>'s box through the UI host, as the pump routes the player's click;
    /// <paramref name="x"/>, <paramref name="y"/> are where it landed.</summary>
    private static Row Press(KnobPanel p, Knob k, out int x, out int y)
    {
        Row r = Find(p, k) ?? throw new InvalidOperationException(k.Key + " has no row");
        Show(p, r);
        p.BoxRect(r, p.List.ToScreen(r.Top), out float x0, out float x1, out float y0, out float y1);
        x = (int)((x0 + x1) * 0.5f); y = (int)((y0 + y1) * 0.5f);
        UiHost.DispatchMouseButton(true, 1, x, y);
        UiHost.DispatchMouseButton(false, 1, x, y);
        return r;
    }

    /// <summary>A press on <paramref name="k"/>'s box, as the player clicks it to bind a key.</summary>
    private static Row Wait(KnobPanel p, Knob k)
    {
        Row r = Find(p, k) ?? throw new InvalidOperationException(k.Key + " has no row");
        Show(p, r);
        p.BoxRect(r, p.List.ToScreen(r.Top), out float x0, out float x1, out float y0, out float y1);
        p.MouseDown((x0 + x1) * 0.5f, (y0 + y1) * 0.5f); p.MouseUp();
        return r;
    }
}
