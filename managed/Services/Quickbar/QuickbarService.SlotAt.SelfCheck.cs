using CX.Framework;
using CX.Ui;

namespace CX.Services.Quickbar;

public sealed unsafe partial class QuickbarService
{
    /// <summary>The engine's hit-test view of the CX bar under a NUI window: the bar's slot when it draws above NUI, none
    /// under it. Oracle: a bar laid out by hand at (100, 100) and the slot its last cell is.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SlotAtSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "QuickbarService.SlotAt.SelfCheck");
        QuickbarBarState was = _cx;
        bool inGame = GameGate.InGame;
        string?[] keys = { "quickbar.enabled", "quickbar.replace", "quickbar.hidden", "quickbar.abovenui" };
        string?[] stored = new string?[keys.Length], raw = new string?[keys.Length];
        for (int i = 0; i < keys.Length; i++) { stored[i] = KnobGroup.Find(keys[i]!)?.FormatStored(); raw[i] = Settings.Raw(keys[i]!); }
        try
        {
            _cx = new QuickbarBarState { Armed = true };
            _cx.Layout.Layout(1f, 1, false);
            _cx.Layout.X = 100f; _cx.Layout.Y = 100f;
            GameGate.InGame = true;
            QuickbarKnobs.Enabled.Parse("1"); QuickbarKnobs.Replace.Parse("1"); QuickbarKnobs.Hidden.Parse("0");
            int x = (int)(_cx.Layout.X + _cx.Layout.W - 4f), y = (int)(_cx.Layout.Y + _cx.Layout.H / 2f);
            int slot = _cx.Layout.SlotAt(x, y, 0);
            UiHost.Alone(() =>
            {
                _cx.Window = NewWindow();

                // Given the CX quickbar under NUI, and a NUI window over a slot
                // When the engine asks which button is at that point
                // Then the bar answers none, and NUI keeps the press
                QuickbarKnobs.AboveNui.Parse("0");
                check.Expect(SlotAt(x, y, nuiCovers: true) == -1, $"a slot under a NUI window is not the bar's: {SlotAt(x, y, nuiCovers: true)}");

                // Given Draw above NUI on for the CX quickbar, and a NUI window over a slot
                // When the engine asks which button is at that point
                // Then the bar answers its slot, so the press fires it
                QuickbarKnobs.AboveNui.Parse("1");
                check.Expect(slot >= 0 && SlotAt(x, y, nuiCovers: true) == slot,
                    $"above NUI, a slot over a NUI window is the bar's: want {slot}, got {SlotAt(x, y, nuiCovers: true)}");
            });
        }
        finally
        {
            _cx = was;
            GameGate.InGame = inGame;
            for (int i = 0; i < keys.Length; i++)
            {
                if (stored[i] != null) KnobGroup.Find(keys[i]!)?.Parse(stored[i]!);
                if (raw[i] != null) Settings.Read(keys[i] + "=" + raw[i] + "\n"); else Settings.Forget(keys[i]!);
            }
        }
        if (check.Ok) log.Info("QuickbarService.SlotAt.SelfCheck: passed");
    }
}
