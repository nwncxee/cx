using System.Collections.Generic;
using CX.Framework;

namespace CX.Services.Hotkeys;

/// <summary>A server hotkey on a mouse button wants the press the chord layer asks as key -button, asked of the
/// consumer itself, as the stub host resolves no symbol to register it. Oracle: the armed code written here.</summary>
internal static class HotkeyChord
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "HotkeyChord.SelfCheck");
        HashSet<int> armed = HotkeyStore.Armed;
        bool on = Hotkeys.Enabled.V;
        try
        {
            Hotkeys.Enabled.Set(true);
            HotkeyStore.Armed = new HashSet<int>();
            bool before = !HotkeyInputService.Chord.WantsKey(-4);
            HotkeyStore.Armed = new HashSet<int> { HotkeyProtocol.MouseBase + 4 };
            bool thumb = HotkeyInputService.Chord.WantsKey(-4), key = !HotkeyInputService.Chord.WantsKey(4);
            check.Expect(before && thumb && key, $"an armed Mouse4 hotkey wants the press asked as -4 and not key 4: none before {before}, -4 {thumb}, 4 free {key}");
        }
        finally
        {
            HotkeyStore.Armed = armed;
            Hotkeys.Enabled.Set(on);
        }
        log.Info(check.Ok ? "HotkeyChord.SelfCheck: passed" : "HotkeyChord.SelfCheck: FAILED");
        return check.Ok;
    }
}
