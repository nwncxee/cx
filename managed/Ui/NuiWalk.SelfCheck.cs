using System;
using System.Threading;
using CX.Engine;
using CX.Framework;
using CX.Services.Nui;

namespace CX.Ui;

/// <summary>The walks of nuklear's window list end on a list that rings and reach a window however deep it sits.
/// Oracle: window lists built by hand in scratch memory, a ring linked by hand.</summary>
internal static unsafe class NuiWalkSelfCheck
{
    /// <summary>How long a walk over a short list may take before the check calls it endless.</summary>
    private const int WalkMs = 3000;

    /// <summary>Runs <paramref name="f"/> on a background thread and says whether it returned within <see cref="WalkMs"/>.</summary>
    private static bool Returns(Func<bool> f)
    {
        var t = new Thread(() => f()) { IsBackground = true };
        t.Start();
        return t.Join(WalkMs);
    }

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "NuiWalk.SelfCheck");

        // Given nuklear's window list rings, its last window linking back to the first
        // When Esc asks for the NUI window over a CX window, and a press asks whether NUI covers the pointer
        // Then each walk returns rather than holding the input thread
        {
            var ring = new NuiScratch();
            IntPtr ctx = EscOrderApprovedExamples.NuiContext(ring);
            IntPtr a = EscOrderApprovedExamples.NuiWindow(ring, ctx, "a", 0, 3000f, 3000f, 10f, 10f);
            IntPtr b = EscOrderApprovedExamples.NuiWindow(ring, ctx, "b", 0, 3100f, 3100f, 10f, 10f);
            new NkWindow(b).Next = a;
            bool esc = Returns(() => EscNui.CloseOver(ctx, 0f, 0f, 100f, 100f));
            bool covers = Returns(() => UiInputService.NuiCoversIn(ctx, 10f, 10f));
            bool over = Returns(() => UiInputService.NuiOver(ctx, null, 10, 10));
            check.Expect(esc && covers && over, $"a ringed window list ends every walk: Esc {esc}, covers {covers}, over {over}");
        }

        // Given 300 NUI windows, the topmost a closable server window over the inventory
        // When the player presses Esc over the inventory
        // Then that topmost window closes
        {
            using var big = new NuiScratch();
            IntPtr ctx = EscOrderApprovedExamples.NuiContext(big);
            for (int i = 0; i < 299; i++) EscOrderApprovedExamples.NuiWindow(big, ctx, "panel" + i, 0, 5000f, 5000f, 10f, 10f);
            IntPtr top = EscOrderApprovedExamples.NuiWindow(big, ctx, EscOrderApprovedExamples.Json + "quest",
                EscOrderApprovedExamples.Closable, 0f, 0f, 400f, 400f);
            bool closed = EscNui.CloseOver(ctx, 50f, 50f, 100f, 100f);
            check.Expect(closed && EscOrderApprovedExamples.Closed(top),
                $"Esc reaches the 300th NUI window: closed {closed}, flags 0x{EscOrderApprovedExamples.Flags(top):x}");
        }

        if (check.Ok) log.Info("NuiWalk.SelfCheck: passed");
    }
}
