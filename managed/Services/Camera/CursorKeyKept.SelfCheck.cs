using System;
using CX.Engine;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Camera;

/// <summary>What the cursor key leaves alone: with the action camera's gate shut, and while a window owns the keyboard,
/// the key is the game's and the cursor is untouched. Oracle: the keys and setting names written here.</summary>
public sealed unsafe partial class MouseLookService
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool CursorKeyKept(ExtenderLog log)
    {
        var check = new Checker(log, "CursorKeyKept.SelfCheck");
        Knob<string> chase = CameraModes.Chase.CursorKey;
        string stored = chase.FormatStored();
        string? raw = Settings.Raw(chase.Key);

        bool Space(uint type, bool gateOpen)
        {
            SdlEvent e = default;
            e.Type = type; e.SymU = ' ';
            return _cursor.Feed(&e, gateOpen, 0);
        }
        Window? chat = null;
        try
        {
            // Given the action camera is off and the cursor key is Space
            // When the player presses Space
            // Then the game does what it always does on Space, and the cursor is untouched
            {
                chase.Set("Space"); _cursor.Reset();
                bool down = Space(SdlKeyDown, false);
                bool freed = _cursor.Freed(0);
                bool up = Space(SdlKeyUp, false);
                check.Expect(!down && !up && !freed, $"E139 with the gate shut Space is the game's: down taken {down}, up taken {up}, freed {freed}");
            }

            // Given the action camera is on, the cursor key is Space and the chat box is open for typing
            // When the player types a space
            // Then the space goes into the chat line and the cursor stays as it is
            {
                chase.Set("Space"); _cursor.Reset();
                bool before = KeyboardFree();
                chat = new Window(0, 0, 100, 100) { WantsKeyboard = true };
                bool typing = KeyboardFree();
                bool down = Space(SdlKeyDown, typing);
                bool freed = _cursor.Freed(0);
                bool up = Space(SdlKeyUp, typing);
                check.Expect(before && !typing, $"E140 a window owning the keyboard shuts the cursor key's gate: free before {before}, while typing {typing}");
                check.Expect(!down && !up && !freed, $"E140 a typed space reaches the chat line: down taken {down}, up taken {up}, freed {freed}");
            }
        }
        finally
        {
            if (chat != null) UiHost.Unregister(chat);
            if (chase.FormatStored() != stored) chase.Parse(stored);
            if (raw is { } v) Settings.Read($"{chase.Key}={v}\n");
            else Settings.Forget(chase.Key);
            _cursor.Reset();
        }
        log.Info(check.Ok ? "CursorKeyKept.SelfCheck: passed" : "CursorKeyKept.SelfCheck: FAILED");
        return check.Ok;
    }
}
