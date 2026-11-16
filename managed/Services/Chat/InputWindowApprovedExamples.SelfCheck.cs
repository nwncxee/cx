using CX.Framework;
using CX.Services.Chat.Core;
using static CX.Services.Chat.ChatExamplesKit;

namespace CX.Services.Chat;

/// <summary>Where Enter opens the typing bar: approved examples E11 and E13. Oracle: Main is window 0 by the
/// layout's own contract; the windows and their channel masks are written by hand. A sent line leaves the focus where
/// the bar was, so E13's state after the send is the second window focused.</summary>
internal static class InputWindowApprovedExamples
{
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "InputWindowApprovedExamples.SelfCheck");

        // Given a second chat window the player clicked last, whose tab shows the DM channel
        // When the player presses Enter
        // Then the typing bar opens in the Main window
        {
            var layout = new ChatLayout(Window(0, 0));
            layout.AddWindow(new ChatWindow(500, 0, 400, 300, new[] { new Tab("System", 0x400) }));
            layout.FocusedIndex = 1;
            check.Expect(layout.TypingWindow() == 0, $"E11 Enter opens in Main, not the DM window clicked last (got {layout.TypingWindow()})");
        }

        // Given the player sent a line from the typing bar in a second window
        // When they press Enter again
        // Then the typing bar opens in the Main window
        {
            var layout = new ChatLayout(Window(0, 0));
            layout.AddWindow(new ChatWindow(500, 0, 400, 300, new[] { new Tab("Party", 0x040) }));
            layout.FocusedIndex = 1;
            check.Expect(layout.TypingWindow() == 0, $"E13 the next Enter after sending from a second window opens in Main (got {layout.TypingWindow()})");
        }

        if (check.Ok) log.Info("InputWindowApprovedExamples.SelfCheck: passed");
    }
}
