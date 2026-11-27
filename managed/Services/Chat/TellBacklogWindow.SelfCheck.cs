using System;
using System.Runtime.CompilerServices;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Core.Model;
using CX.Services.Chat.Render;
using CX.Ui;
using static CX.Services.Chat.ChatExamplesKit;
using static CX.Services.Chat.TellBacklogApprovedExamples;

namespace CX.Services.Chat;

/// <summary>The chat window scrolls through every line the backlog keeps, tells on top of the 2,048 others, grows its
/// line buffer once for many tells, and lets a cleared session's tells go: found while building the kept tells, as the
/// window's own line buffer was sized to the ring. Oracle: one line of talk fewer leaves a shorter scroll range, by
/// hand.</summary>
internal static class TellBacklogWindow
{
    private const uint Talk = 0x001, Tell = 0x002;  // not-an-offset: chat channel flags

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "TellBacklogWindow.SelfCheck");
        UiCategory.Declare();   // a window frame reads the UI rows, and the UI groups declare in their own order first

        // Given a window opened on an empty chat, then 100 tells and 2,048 lines of talk
        // When the window shows every channel
        // Then its scroll range is longer than for the 2,048 lines of talk alone
        ContentHeight(-1);
        float talkOnly = ContentHeight(0);
        float withTells = ContentHeight(100);
        check.Expect(withTells > talkOnly, $"the window scrolls over the tells too ({withTells} against {talkOnly} for talk alone)");

        // Given a window's line buffer that one more tell outgrows
        // When the next tell comes after it
        // Then the window keeps the buffer it grew, with room for the next
        ChatLine[] held = WindowRenderer.RingScratch(0);
        ChatLine[] grown = WindowRenderer.RingScratch(held.Length + 1);
        check.Expect(ReferenceEquals(grown, WindowRenderer.RingScratch(held.Length + 2)), "the grown buffer has room for the next tell");

        // Given 3,000 tells in a window
        // When the player leaves the server and the window draws the next session's first line
        // Then nothing holds the last session's tells
        WeakReference first = LastSession();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        check.Expect(!first.IsAlive, "the window lets the last session's tells go");

        log.Info(check.Ok ? "TellBacklogWindow.SelfCheck: passed" : "TellBacklogWindow.SelfCheck: FAILED");
    }

    /// <summary>Draws a window over 3,000 tells, leaves the server and draws the next session's first line; returns
    /// a handle on the first tell, which nothing should then hold.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LastSession()
    {
        var store = new ChatStore();
        var win = new ChatWindow(0f, 0f, 400f, 300f, new[] { new Tab("All", uint.MaxValue) });
        var first = Line(Tell, 42, "the first tell");
        store.Append(first);
        for (int i = 0; i < 3000; i++) store.Append(Line(Tell, 42, "tell"));
        Build(win, Atlas(), false, store: store);
        store.Clear();
        store.Append(Line(Talk, 7, "next session"));
        Build(win, Atlas(), false, store: store);
        return new WeakReference(first);
    }

    /// <summary>The scroll range over <paramref name="tells"/> tells and 2,048 lines of talk; a negative count draws
    /// the window on an empty chat, as it opens.</summary>
    private static float ContentHeight(int tells)
    {
        var store = new ChatStore();
        var win = new ChatWindow(0f, 0f, 400f, 300f, new[] { new Tab("All", uint.MaxValue) });
        if (tells < 0) { Build(win, Atlas(), false, store: store); return win.ContentH; }
        for (int i = 0; i < tells; i++) store.Append(Line(Tell, 42, "tell"));
        for (int i = 0; i < 2048; i++) store.Append(Line(Talk, 7, "talk"));
        Build(win, Atlas(), false, store: store);
        return win.ContentH;
    }
}
