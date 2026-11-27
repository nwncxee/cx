using System;
using CX.Framework;
using CX.Services.Chat.Core;

namespace CX.Services.Chat;

/// <summary>Tells kept for the whole session, and a 2,048-line backlog (approved-spec, E1, E3 to E7). Oracle:
/// the line counts and the order each example states, written by hand; a tab reads the backlog as the chat window
/// does, through its channel mask, player and pinned name.</summary>
internal static class TellBacklogApprovedExamples
{
    private const uint Talk = 0x001, Tell = 0x002, Combat = 0x010;  // not-an-offset: chat channel flags
    private const uint Alice = 42, You = 1;

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "TellBacklogApprovedExamples.SelfCheck");
        var aliceTab = new Tab("T:Alice", Tell, Alice) { PinnedName = "Alice" };
        var everything = new Tab("All", uint.MaxValue);

        // Given a tell from Alice
        // When 3,000 lines of combat feedback arrive after it
        // Then Alice's tell still shows in her tell tab
        {
            var store = new ChatStore();
            store.Append(Line(Tell, Alice, "hello from alice"));
            Fill(store, Combat, 3000);
            ChatLine[] shown = Shown(store, aliceTab);
            check.Expect(shown.Length == 1 && shown[0].RawText == "hello from alice",
                $"E1 Alice's tell still shows after 3,000 combat lines ({shown.Length} lines in her tab)");
        }

        // Given 2,049 lines of talk in a row
        // When the player scrolls to the top of a tab that shows every channel
        // Then 2,048 lines show, and the first line of talk is the one gone
        {
            var store = new ChatStore();
            for (int i = 0; i < 2049; i++) store.Append(Line(Talk, 7, "talk" + i));
            ChatLine[] shown = Shown(store, everything);
            check.Expect(shown.Length == 2048, $"E3 2,048 lines show, not {shown.Length}");
            check.Expect(shown.Length > 0 && shown[^1].RawText == "talk1" && shown[0].RawText == "talk2048",
                "E3 the oldest line shown is the second line of talk, so the first is the one gone");
        }

        // Given 100 tells, then 2,048 lines of talk
        // When the player scrolls to the top of a tab that shows every channel
        // Then all 100 tells and all 2,048 lines of talk show
        {
            var store = new ChatStore();
            for (int i = 0; i < 100; i++) store.Append(Line(Tell, Alice, "tell" + i));
            for (int i = 0; i < 2048; i++) store.Append(Line(Talk, 7, "talk" + i));
            ChatLine[] shown = Shown(store, everything);
            check.Expect(shown.Length == 2148, $"E4 all 100 tells and 2,048 lines of talk show, not {shown.Length} lines");
            check.Expect(shown.Length == 2148 && shown[^1].RawText == "tell0" && shown[2047].RawText == "talk0",
                "E4 the oldest tell and the oldest line of talk are both still there");
        }

        // Given a tell the player sent to Alice
        // When 3,000 lines of combat feedback arrive after it
        // Then the sent tell still shows in Alice's tell tab
        {
            var store = new ChatStore();
            store.Append(Line(Tell, You, "hi alice", recipient: "Alice"));
            Fill(store, Combat, 3000);
            ChatLine[] shown = Shown(store, aliceTab);
            check.Expect(shown.Length == 1 && shown[0].RawText == "hi alice",
                $"E5 the sent tell still shows in Alice's tab ({shown.Length} lines)");
        }

        // Given 2,100 tells from Alice
        // When the player scrolls to the top of her tell tab
        // Then all 2,100 tells show
        {
            var store = new ChatStore();
            for (int i = 0; i < 2100; i++) store.Append(Line(Tell, Alice, "tell" + i));
            ChatLine[] shown = Shown(store, aliceTab);
            check.Expect(shown.Length == 2100 && shown[^1].RawText == "tell0",
                $"E6 all 2,100 tells show, the first one at the top ({shown.Length} shown)");
        }

        // Given a line of talk, then a tell from Alice, then another line of talk
        // When 3,000 lines of combat feedback arrive after them
        // Then a tab that shows Talk and Tell lists the tell between the two lines of talk, as long as both are still kept
        {
            var talkAndTell = new Tab("Talk+Tell", Talk | Tell);
            foreach (int combat in new[] { 3000, 1000 })
            {
                var store = new ChatStore();
                store.Append(Line(Talk, 7, "before"));
                store.Append(Line(Tell, Alice, "the tell"));
                store.Append(Line(Talk, 7, "after"));
                Fill(store, Combat, combat);
                string order = string.Join(",", Array.ConvertAll(Shown(store, talkAndTell), l => l.RawText));
                string want = combat == 3000 ? "the tell" : "after,the tell,before";
                check.Expect(order == want, $"E7 after {combat} combat lines the tab lists {want}, newest first, not {order}");
            }
        }

        log.Info(check.Ok ? "TellBacklogApprovedExamples.SelfCheck: passed" : "TellBacklogApprovedExamples.SelfCheck: FAILED");
    }

    /// <summary>What <paramref name="tab"/> shows, newest first, from the top of its history to the bottom.</summary>
    internal static ChatLine[] Shown(ChatStore store, Tab tab)
    {
        var dest = new ChatLine[8192];
        int n = store.View(tab.ChannelMask, tab.PlayerId, dest, tab.PinnedName.Length > 0 ? tab.PinnedName : null);
        return dest.AsSpan(0, n).ToArray();
    }

    /// <summary><paramref name="count"/> lines on <paramref name="channel"/> from a third player.</summary>
    internal static void Fill(ChatStore store, uint channel, int count)
    {
        for (int i = 0; i < count; i++) store.Append(Line(channel, 9, "line" + i));
    }

    /// <summary>A plain chat line; <paramref name="recipient"/> marks the player's own sent tell.</summary>
    internal static ChatLine Line(uint channel, uint playerId, string raw, string recipient = "") =>
        new(channel, "", playerId, "", [new ColorRun(raw, 1, 1, 1)], DateTime.UtcNow, raw, default, recipient);
}
