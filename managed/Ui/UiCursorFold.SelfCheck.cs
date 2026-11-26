using CX.Framework;

namespace CX.Ui;

/// <summary>Which cursor the fold leaves on screen over a CX window. Oracle: the engine's own cursor ids, as literals.</summary>
internal static class UiCursorFoldSelfCheck
{
    /// <summary>A cursor id a target mode sets for an allowed object; any id but the walk pointer's.</summary>
    private const int TargetCursor = 41;   // not-an-offset: a cursor id

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UiCursorFold.SelfCheck");

        // Given no target mode is up
        // When the pointer is over a CX window
        // Then the walk pointer shows, not the world's cursor behind the window
        int plain = UiInputService.CursorFor(TargetCursor, overUi: true, targetMode: false, UiInputService.NoCursor);
        check.Expect(plain == 3, $"over a CX window the walk pointer shows: {plain}");

        // Given a target mode is up
        // When the pointer is over a CX window
        // Then the target mode's own cursor shows, as over the stock panel
        int aimed = UiInputService.CursorFor(TargetCursor, overUi: true, targetMode: true, UiInputService.NoCursor);
        check.Expect(aimed == TargetCursor, $"a target mode keeps its cursor over a CX window: {aimed}");

        // Given a held image holds the cursor
        // When the pointer is over a CX window, a target mode up or not
        // Then the held image's cursor shows
        int held = UiInputService.CursorFor(TargetCursor, overUi: true, targetMode: true, 7);
        check.Expect(held == 7, $"a held image keeps its cursor: {held}");

        // Given the pointer is over the world
        // When the engine sets a cursor
        // Then that cursor shows
        int world = UiInputService.CursorFor(TargetCursor, overUi: false, targetMode: false, UiInputService.NoCursor);
        check.Expect(world == TargetCursor, $"over the world the engine's cursor shows: {world}");
        if (check.Ok) log.Info("UiCursorFold.SelfCheck: passed");
    }
}
