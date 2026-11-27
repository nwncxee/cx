using System.Collections.Generic;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Services.Quickbar;

/// <summary>The two key settings that take no mouse button, a quickbar key row and a tab-target key, refuse a Mouse name
/// with the notice they gave before KeyNames read one, and take a key. Off the client SDL names no key, so only the
/// one-character names it reads stand in. Oracle: the notices written here.</summary>
internal static class MouseKeyRefusals
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MouseKeyRefusals.SelfCheck");
        string[] rows = { QuickbarKnobs.Keys[0].V, QuickbarKnobs.Keys[1].V, QuickbarKnobs.Keys[2].V };
        string lockKey = TabTarget.LockKey.V;
        var heard = new List<string>();
        System.Action<string>? tap = log.Tap;
        try
        {
            QuickbarKnobs.Keys[1].Set("G");
            QuickbarKnobs.Keys[2].Set("H");
            log.Tap = heard.Add;
            QuickbarKnobs.Keys[0].Set("Mouse4");
            QuickbarService.KeysChanged();
            bool rowRefused = heard.Exists(l => l.Contains("quickbar: 1 key(s) in the key rows are not SDL key names"));
            heard.Clear();
            QuickbarKnobs.Keys[0].Set("F");
            QuickbarService.KeysChanged();
            bool rowTook = !heard.Exists(l => l.Contains("quickbar:"));
            heard.Clear();
            TabTarget.LockKey.Set("Mouse4");
            TabTargetService.KeysChanged();
            bool lockRefused = heard.Exists(l => l.Contains("TabTarget: 'Mouse4' is not an SDL key name; nothing bound"));
            heard.Clear();
            TabTarget.LockKey.Set("F");
            TabTargetService.KeysChanged();
            bool lockTook = !heard.Exists(l => l.Contains("TabTarget: 'F'"));
            log.Tap = tap;
            check.Expect(rowRefused, $"a quickbar key row naming Mouse4 is refused with the notice: {rowRefused}");
            check.Expect(rowTook, $"a quickbar key row naming F binds with no notice: {rowTook}");
            check.Expect(lockRefused, $"tab-target's Lock-on key naming Mouse4 binds nothing, with the notice: {lockRefused}");
            check.Expect(lockTook, $"tab-target's Lock-on key naming F binds with no notice: {lockTook}");
        }
        finally
        {
            log.Tap = tap;
            for (int set = 0; set < rows.Length; set++) QuickbarKnobs.Keys[set].Set(rows[set]);
            TabTarget.LockKey.Set(lockKey);
            QuickbarService.KeysChanged();
            TabTargetService.KeysChanged();
        }
        log.Info(check.Ok ? "MouseKeyRefusals.SelfCheck: passed" : "MouseKeyRefusals.SelfCheck: FAILED");
        return check.Ok;
    }
}
