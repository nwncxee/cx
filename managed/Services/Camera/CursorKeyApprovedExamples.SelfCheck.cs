using System;
using System.Collections.Generic;
using CX.Engine;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Camera;

/// <summary>The cursor key's approved examples: each mode's real <c>cursorkey</c> row set as the player sets it, then
/// key and thumb-button events fed to the action camera's cursor key as WinMessageHandler hands them on, the action
/// camera's gate open, or the row pressed in a settings panel. Oracle: the keys, modes and setting names written here.</summary>
public sealed unsafe partial class MouseLookService
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool CursorKeyApprovedExamples(ExtenderLog log)
    {
        var check = new Checker(log, "CursorKeyApprovedExamples.SelfCheck");
        Knob<string>? chase = KnobGroup.Find("chase.cursorkey") as Knob<string>;
        Knob<string>? topDown = KnobGroup.Find("topdown.cursorkey") as Knob<string>;
        var rows = new List<Knob> { CameraModes.Chase.AltToggle, CameraModes.TopDown.AltToggle, CameraModes.Chase.ActionKey, CameraModes.Chase.Enabled, CameraModes.Chase.Action };
        if (chase != null) rows.Add(chase);
        if (topDown != null) rows.Add(topDown);
        Knob[] touched = rows.ToArray();
        string[] stored = Array.ConvertAll(touched, k => k.FormatStored());
        string?[] raw = Array.ConvertAll(touched, k => Settings.Raw(k.Key));

        bool Bind(Knob<string>? row, string name) { if (row == null) return false; row.Set(name); return true; }
        bool Key(uint type, uint sym, int mode, bool repeat = false)
        {
            SdlEvent e = default;
            e.Type = type; e.SymU = sym; e.Repeat = repeat ? (byte)1 : (byte)0;
            return _cursor.Feed(&e, true, mode);
        }
        bool Thumb(uint type, byte button, int mode)
        {
            SdlEvent e = default;
            e.Type = type; e.Button = button;
            return _cursor.Feed(&e, true, mode);
        }
        void Fresh()
        {
            foreach (Knob k in touched) k.Reset();
            CameraModes.Chase.Enabled.Set(true);
            _cursor.Reset();
        }
        try
        {
            // Given the action camera is on and the cursor key is set to Space
            // When the player holds Space
            // Then the cursor appears and a click lands on the panel under it
            {
                Fresh();
                check.Expect(Bind(chase, "Space"), "E133 the chase camera has a cursor key row (chase.cursorkey)");
                Key(SdlKeyDown, ' ', 0);
                Key(SdlKeyDown, ' ', 0, repeat: true);
                check.Expect(_cursor.Freed(0), "E133 holding Space frees the cursor, so a click reaches the panel under it");
            }

            // Given the action camera is on, the cursor key is Space and the player is holding it
            // When the player lets go of Space
            // Then the cursor is hidden and the crosshair is back in the centre
            {
                Fresh();
                Bind(chase, "Space");
                Key(SdlKeyDown, ' ', 0);
                bool held = _cursor.Freed(0);
                Key(SdlKeyUp, ' ', 0);
                check.Expect(held && !_cursor.Freed(0), $"E134 letting go of Space hides the cursor again: freed while held {held}, after {_cursor.Freed(0)}");
            }

            // Given the action camera is on and the cursor key is set to Space
            // When the player holds Alt
            // Then the cursor stays hidden and the crosshair stays
            {
                Fresh();
                bool bound = Bind(chase, "Space");
                Key(SdlKeyDown, SdlkLAlt, 0);
                bool left = _cursor.Freed(0);
                Key(SdlKeyDown, SdlkRAlt, 0);
                check.Expect(bound && !left && !_cursor.Freed(0), $"E135 with Space bound, neither Alt frees the cursor: bound {bound}, left Alt {left}, right Alt {_cursor.Freed(0)}");
            }

            // Given a player who has never changed the cursor key
            // When they hold either Alt key under the action camera
            // Then the cursor appears, as it does today
            {
                Fresh();
                check.Expect(chase is { Default: "Alt" } && chase.V == "Alt", $"E136 the cursor key starts as Alt: '{chase?.V}'");
                Key(SdlKeyDown, SdlkLAlt, 0);
                bool left = _cursor.Freed(0);
                Key(SdlKeyUp, SdlkLAlt, 0);
                bool leftUp = _cursor.Freed(0);
                Key(SdlKeyDown, SdlkRAlt, 0);
                bool right = _cursor.Freed(0);
                Key(SdlKeyUp, SdlkRAlt, 0);
                check.Expect(left && right && !leftUp && !_cursor.Freed(0),
                    $"E136 holding either Alt frees the cursor and letting go hides it: left {left} then {leftUp}, right {right} then {_cursor.Freed(0)}");
            }

            // Given the action camera is on, the cursor key is Space and the toggle option is on
            // When the player taps Space
            // Then the cursor stays free until they tap Space again
            {
                Fresh();
                Bind(chase, "Space");
                CameraModes.Chase.AltToggle.Set(true);
                Key(SdlKeyDown, ' ', 0); Key(SdlKeyUp, ' ', 0);
                bool tapped = _cursor.Freed(0);
                Key(SdlKeyDown, ' ', 0); Key(SdlKeyUp, ' ', 0);
                check.Expect(tapped && !_cursor.Freed(0), $"E137 a tap of Space frees the cursor until the next tap: after one {tapped}, after two {_cursor.Freed(0)}");
            }

            // Given the action camera is on and the cursor key is Space
            // When the player presses Space
            // Then only the cursor changes: whatever the game itself does on Space does not happen
            {
                Fresh();
                Bind(chase, "Space");
                bool down = Key(SdlKeyDown, ' ', 0);
                bool repeat = Key(SdlKeyDown, ' ', 0, repeat: true);
                bool freed = _cursor.Freed(0);
                bool up = Key(SdlKeyUp, ' ', 0);
                check.Expect(down && repeat && up && freed,
                    $"E138 Space frees the cursor and the game sees none of it: down taken {down}, repeat {repeat}, up {up}, freed {freed}");
            }

            // Given the action camera toggle key is ]
            // When the player presses ] on the cursor key row
            // Then the row warns that ] is already bound to the action camera toggle key, and waits for Enter before taking it
            {
                Fresh();
                CameraModes.Chase.ActionKey.Set("]");
                CameraModes.Chase.Action.Set(true);
                KnobPanel p = Panel();
                p.Show(CameraModes.Chase.G);
                p.LayoutRows();
                KnobPanel.Row? r = null;
                foreach (KnobPanel.Row row in p.Rows) if (row.Kind == KnobPanel.RowKind.Knob && row.K != null && ReferenceEquals(row.K, chase)) r = row;
                check.Expect(r != null, "E141 the chase camera's panel shows a cursor key row");
                if (r != null)
                {
                    p.List.Offset = r.Top - 0.4f * p.Em; p.List.Clamp();
                    p.BoxRect(r, p.List.ToScreen(r.Top), out float x0, out float x1, out float y0, out float y1);
                    p.MouseDown((x0 + x1) * 0.5f, (y0 + y1) * 0.5f); p.MouseUp();
                    p.Key(']');
                    string? warn = p.KeyWarning;
                    check.Expect(warn != null && warn.StartsWith("] is already bound to Action camera toggle key", StringComparison.Ordinal),
                        $"E141 the row warns that ] is the action camera toggle key: '{warn}'");
                    check.Expect(chase!.V == "Alt", $"E141 nothing is bound before Enter ({chase.V})");
                    p.Key(0x0D);
                    check.Expect(chase.V == "]" && p.KeyWarning == null, $"E141 Enter binds ] ({chase.V})");
                }
            }

            // Given the action camera is on and the cursor key is set to Mouse4
            // When the player holds the thumb button
            // Then the cursor appears, and comes back hidden when they let go
            {
                Fresh();
                Bind(chase, "Mouse4");
                bool down = Thumb(SdlMouseDown, 4, 0);
                bool held = _cursor.Freed(0);
                bool up = Thumb(SdlMouseUp, 4, 0);
                check.Expect(down && up && held && !_cursor.Freed(0),
                    $"E142 the held thumb button frees the cursor and letting go hides it: down {down}, held {held}, up {up}, after {_cursor.Freed(0)}");
            }

            // Given the action camera is on and the cursor key row is cleared
            // When the player holds Alt or Space
            // Then the cursor stays hidden; only the action camera toggle key gives it back
            {
                Fresh();
                bool cleared = Bind(chase, "");
                Key(SdlKeyDown, SdlkLAlt, 0);
                bool alt = _cursor.Freed(0);
                Key(SdlKeyDown, ' ', 0);
                check.Expect(cleared && !alt && !_cursor.Freed(0), $"E143 a cleared row frees nothing: cleared {cleared}, Alt {alt}, Space {_cursor.Freed(0)}");
            }

            // Given the cursor key is set to Space for Chase Cam only
            // When the player switches to Top Down with the action camera on and holds Alt
            // Then the cursor appears, since Top Down still has Alt
            {
                Fresh();
                bool bound = Bind(chase, "Space");
                Key(SdlKeyDown, SdlkLAlt, 1);
                check.Expect(bound && topDown is { V: "Alt" } && _cursor.Freed(1),
                    $"E144 Top Down keeps Alt while Chase Cam has Space: chase '{chase?.V}', top-down '{topDown?.V}', freed {_cursor.Freed(1)}");
            }
        }
        finally
        {
            for (int i = 0; i < touched.Length; i++)
            {
                Knob k = touched[i];
                if (k.FormatStored() != stored[i]) k.Parse(stored[i]);
                if (raw[i] is { } v) Settings.Read($"{k.Key}={v}\n");
                else Settings.Forget(k.Key);
            }
            _cursor.Reset();
        }
        log.Info(check.Ok ? "CursorKeyApprovedExamples.SelfCheck: passed" : "CursorKeyApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The settings panel open on Controls at 1280x720, its faces stand-ins with fixed advances.</summary>
    private static KnobPanel Panel()
    {
        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x180; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }
        foreach (int cp in new[] { 0x2014, 0x21BB, 0x25B8, 0x25BE }) { body.SetTestGlyph(cp, 11f); small.SetTestGlyph(cp, 8f); }
        var p = new KnobPanel { Category = Array.IndexOf(KnobPanel.KnobCategories, "Controls") };
        p.OpenAt(1280f, 720f, KnobPanel.FacePx, body, small);
        return p;
    }
}
