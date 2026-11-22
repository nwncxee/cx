using System;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;

namespace CX.Services.Chat;

/// <summary>The chat's limits at their edges: the store's ring, the caret's selection and the counter's cap.</summary>
internal static class ChatLimitsSelfCheck
{
    /// <summary>Oracle: the store's 512 lines, the 4,000-character cap at 90% (3,600) and the caret positions,
    /// written by hand.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "ChatLimits.SelfCheck");
        var store = new ChatStore();
        for (int i = 0; i <= 512; i++)
            store.Append(new ChatLine(1u, "", 0, "", [new ColorRun("x", 1, 1, 1)], DateTime.UtcNow, i.ToString()));
        var dest = new ChatLine[600];
        int n = 0;
        bool wrapped = true;
        try { n = store.View(uint.MaxValue, null, dest); }
        catch (IndexOutOfRangeException) { wrapped = false; }
        check.Expect(!wrapped || n == 512, $"Capacity: 513 lines leave 512 in view, not {n}");
        check.Expect(wrapped && n > 0 && dest[0].RawText == "512" && dest[n - 1].RawText == "1",
            "the ring wraps to the oldest line kept, the second one written");

        var edit = new EditBuffer();
        edit.Insert("abc");
        edit.SetCaret(3, true);
        check.Expect(!edit.HasSelection, "a selection collapsed to its caret is no selection");

        check.Expect(InputStripLayout.ShowsCap(3600) && !InputStripLayout.ShowsCap(3599), "CounterWarn: the cap shows from 3,600 characters on");
        log.Info(check.Ok ? "ChatLimits.SelfCheck: passed" : "ChatLimits.SelfCheck: FAILED");
    }
}
