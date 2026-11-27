using System;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>The cursor key's edges no approved example names: both Alts at once, a modifier the game still sees, a held
/// key's repeats under the toggle, a left click bound as the key, and what a rebind or the camera's switch-off forgets.
/// Oracle: the keys and setting names written here.</summary>
public sealed unsafe partial class MouseLookService
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool CursorKeyEdges(ExtenderLog log)
    {
        var check = new Checker(log, "CursorKeyEdges.SelfCheck");
        Knob[] touched = { CameraModes.Chase.CursorKey, CameraModes.Chase.AltToggle };
        string[] stored = Array.ConvertAll(touched, k => k.FormatStored());
        string?[] raw = Array.ConvertAll(touched, k => Settings.Raw(k.Key));

        bool Key(uint type, uint sym, bool repeat = false)
        {
            SdlEvent e = default;
            e.Type = type; e.SymU = sym; e.Repeat = repeat ? (byte)1 : (byte)0;
            return _cursor.Feed(&e, true, 0);
        }
        bool Button(uint type, byte button)
        {
            SdlEvent e = default;
            e.Type = type; e.Button = button;
            return _cursor.Feed(&e, true, 0);
        }
        void Fresh(string key)
        {
            foreach (Knob k in touched) k.Reset();
            CameraModes.Chase.CursorKey.Set(key);
            _cursor.Reset();
        }
        try
        {
            {
                Fresh("Alt");
                Key(SdlKeyDown, SdlkLAlt); Key(SdlKeyDown, SdlkRAlt);
                Key(SdlKeyUp, SdlkLAlt);
                bool rightHeld = _cursor.Freed(0);
                Key(SdlKeyUp, SdlkRAlt);
                check.Expect(rightHeld && !_cursor.Freed(0),
                    $"both Alts held, letting go of the left keeps the cursor free until the right comes up too: after left {rightHeld}, after both {_cursor.Freed(0)}");
            }
            {
                Fresh("Alt");
                bool down = Key(SdlKeyDown, SdlkLAlt);
                bool up = Key(SdlKeyUp, SdlkLAlt);
                check.Expect(!down && !up, $"Alt as the cursor key still reaches the game, which reads its own Alt chords: down taken {down}, up taken {up}");
            }
            {
                Fresh("Space");
                CameraModes.Chase.AltToggle.Set(true);
                Key(SdlKeyDown, ' '); Key(SdlKeyDown, ' ', repeat: true);
                check.Expect(_cursor.Freed(0), "under the toggle a held key's repeats are not taps: the cursor stays free");
            }
            {
                Fresh("Mouse1");
                bool down = Button(SdlMouseDown, 1);
                bool freed = _cursor.Freed(0);
                bool up = Button(SdlMouseUp, 1);
                check.Expect(!down && !up && !freed, $"only the thumb buttons free the cursor: a left click bound as the key stays a click: down {down}, freed {freed}, up {up}");
            }
            {
                Fresh("Space");
                Key(SdlKeyDown, ' ');
                CameraModes.Chase.CursorKey.Set("Q");
                bool freed = _cursor.Freed(0);
                bool up = Key(SdlKeyUp, ' ');
                check.Expect(!freed && !up, $"rebinding the row while the old key is held hides the cursor and leaves the old key's release to the game: freed {freed}, up taken {up}");
            }
            {
                Fresh("Space");
                CameraModes.Chase.AltToggle.Set(true);
                Key(SdlKeyDown, ' '); Key(SdlKeyUp, ' ');
                bool tapped = _cursor.Freed(0);
                ActionChanged();
                check.Expect(tapped && !_cursor.Freed(0), $"switching the action camera off forgets a tap's free cursor: tapped {tapped}, after {_cursor.Freed(0)}");
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
        log.Info(check.Ok ? "CursorKeyEdges.SelfCheck: passed" : "CursorKeyEdges.SelfCheck: FAILED");
        return check.Ok;
    }
}
