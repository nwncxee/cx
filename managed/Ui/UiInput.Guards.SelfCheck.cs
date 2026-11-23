using System;
using CX.Framework;
using CX.Services.Nui;

namespace CX.Ui;

/// <summary>What keeps a key or a wheel notch from the CX window it is not for: the topmost Esc target, a typing owner,
/// a NUI edit box, a window that does not capture the pointer. Oracle: counted offers and hand-built nuklear flags.</summary>
internal static class UiInputGuardsSelfCheck
{
    /// <summary>The SDL keycodes the checks press.</summary>
    private const uint Esc = 0x1B, KeyA = 0x61;   // not-an-offset: SDL key codes

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UiInput.Guards.SelfCheck");

        // Given two CX windows Esc would close, a closable NUI window drawn over only the upper one
        // When the player presses Esc
        // Then that NUI window closes
        UiHost.Alone(() =>
        {
            using var nk = new NuiScratch();
            _ = new Window(0, 0, 100, 100) { EscArea = () => (0f, 0f, 100f, 100f) };
            _ = new Window(500, 500, 100, 100) { EscArea = () => (500f, 500f, 100f, 100f) };
            IntPtr ctx = EscOrderApprovedExamples.NuiContext(nk);
            IntPtr nui = EscOrderApprovedExamples.NuiWindow(nk, ctx, EscOrderApprovedExamples.Json + "quest",
                EscOrderApprovedExamples.Closable, 480f, 480f, 200f, 200f);
            bool taken = EscOrderApprovedExamples.Press(ctx);
            check.Expect(taken && EscOrderApprovedExamples.Closed(nui),
                $"Esc closes the NUI window over the topmost CX window: taken={taken} flags 0x{EscOrderApprovedExamples.Flags(nui):x}");
        });

        // Given a CX window typing, and a closable NUI window drawn over a CX window Esc would close
        // When the player presses Esc
        // Then the typing window gets the key and the NUI window stays open
        UiHost.Alone(() =>
        {
            using var nk = new NuiScratch();
            int typed = 0;
            _ = new Window(0, 0, 100, 100) { EscArea = () => (0f, 0f, 100f, 100f) };
            _ = new Window(300, 300, 50, 50) { WantsKeyboard = true, OnKey = (k, m) => { typed++; return true; } };
            IntPtr ctx = EscOrderApprovedExamples.NuiContext(nk);
            IntPtr nui = EscOrderApprovedExamples.NuiWindow(nk, ctx, EscOrderApprovedExamples.Json + "quest",
                EscOrderApprovedExamples.Closable, 0f, 0f, 200f, 200f);
            bool taken = EscOrderApprovedExamples.Press(ctx);
            check.Expect(taken && typed == 1 && EscOrderApprovedExamples.Flags(nui) == EscOrderApprovedExamples.Closable,
                $"a typing window keeps Esc from NUI: taken={taken} typed={typed} flags 0x{EscOrderApprovedExamples.Flags(nui):x}");
        });

        // Given a NUI edit box holding the keyboard, and a CX window that takes every key
        // When the player presses a key
        // Then the key goes to the engine and the CX window never sees it
        UiHost.Alone(() =>
        {
            int seen = 0;
            _ = new Window(0, 0, 100, 100) { OnKey = (k, m) => { seen++; return true; } };
            bool taken = UiInputService.RouteKeyDown(KeyA, 0, true, new UiInputService.EscUnder(false, false, IntPtr.Zero));
            check.Expect(!taken && seen == 0, $"a NUI edit box keeps its key from CX: taken={taken} seen={seen}");
        });

        // Given a CX window over the game's panels that does not capture the pointer, and one that does
        // When the wheel turns over each
        // Then only the capturing one is offered the notch
        UiHost.Alone(() =>
        {
            int plain = 0, capturing = 0;
            _ = new Window(0, 0, 100, 100) { OnMouseWheel = dy => { plain++; return true; } };
            _ = new Window(200, 0, 100, 100) { CapturesPointer = true, OnMouseWheel = dy => { capturing++; return true; } };
            bool overPlain = UiHost.OfferWheelOverGui(1, 10f, 10f);
            bool overCapturing = UiHost.OfferWheelOverGui(1, 210f, 10f);
            check.Expect(!overPlain && plain == 0 && overCapturing && capturing == 1,
                $"the wheel over the game's panels goes only to a capturing CX window: plain {overPlain}/{plain}, capturing {overCapturing}/{capturing}");
        });

        if (check.Ok) log.Info("UiInput.Guards.SelfCheck: passed");
    }
}
