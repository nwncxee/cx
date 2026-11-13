using CX.Framework;

namespace CX.Ui;

/// <summary>The window events a bug report needs dated in <c>cx.log</c>: focus, minimise and restore change how the
/// window presents, and a log that never says when they happened cannot tie a fault to them.</summary>
internal static class WindowNotes
{
    /// <summary>The log's words for SDL window event <paramref name="kind"/> (<c>SDL_WindowEventID</c>), or null
    /// for the kinds that are not worth a line: exposes, moves and pointer crossings.</summary>
    internal static string? Of(byte kind) => kind switch
    {
        1 => "shown",
        2 => "hidden",
        6 => "size changed",
        7 => "minimised",
        8 => "maximised",
        9 => "restored",
        12 => "focus gained",
        13 => "focus lost",
        _ => null,
    };

    /// <summary>Oracle: SDL 2's <c>SDL_WindowEventID</c> values, written out by hand from <c>SDL_video.h</c>.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "WindowNotes.SelfCheck");
        check.Expect(Of(13) == "focus lost" && Of(12) == "focus gained", "focus edges are named (SDL ids 13 and 12)");
        check.Expect(Of(7) == "minimised" && Of(9) == "restored", "minimise and restore are named (SDL ids 7 and 9)");
        check.Expect(Of(3) == null && Of(4) == null && Of(10) == null && Of(11) == null,
                     "exposes, moves and pointer crossings say nothing");
        log.Info(check.Ok ? "WindowNotes.SelfCheck: passed" : "WindowNotes.SelfCheck: FAILED");
    }
}
