using System;
using CX.Framework;
using CX.Services.Chat.Core;
using static CX.Services.Chat.TellBacklogApprovedExamples;

namespace CX.Services.Chat;

/// <summary>Tells the kept-tells change still lost, found while proving it: a tell from before its sender relogged,
/// and a tell a DM sends. Oracle: the tell counts each case states, by hand, and the engine's DM flag on chat minor
/// 0x14, read from HandleServerToPlayerChat on Linux and Windows.</summary>
internal static class TellBacklogFound
{
    private const uint Tell = 0x002, Combat = 0x010, Dm = 0x400;  // not-an-offset: chat channel flags
    private const int TellFromDm = 0x14;  // not-an-offset: chat minor

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "TellBacklogFound.SelfCheck");

        // Given a tell from Alice
        // When Alice leaves the server, joins again and sends another tell
        // Then both tells show in her tell tab
        {
            var store = new ChatStore();
            var alice = new Tab("T:Alice", Tell, 42) { PinnedName = "Alice" };
            var before = From("Alice", 42, "before she relogged");
            store.Append(before);
            store.Append(From("Alice", 77, "after she relogged"));
            alice.PlayerId = 77;
            check.Expect(Shown(store, alice).Length == 2, $"both of Alice's tells show in her tab after she relogs ({Shown(store, alice).Length} shown)");
            check.Expect(alice.Matches(before), "her tab still counts the tell from before she relogged");
            int had = Shown(store, alice).Length;
            store.Append(From("Bob", 91, "from bob"));
            check.Expect(Shown(store, alice).Length == had, "a tell from Bob does not show in Alice's tab");
        }

        // Given a tell a DM sends
        // When 3,000 lines of combat feedback arrive after it
        // Then the tell still shows in a tab of tells
        {
            var store = new ChatStore();
            store.Append(Line(InboundChannel.Filed(Dm, TellFromDm), 5, "a tell from the DM"));
            Fill(store, Combat, 3000);
            check.Expect(Shown(store, new Tab("Tells", Tell)).Length == 1, "the DM's tell is kept and shows with the tells");
        }

        log.Info(check.Ok ? "TellBacklogFound.SelfCheck: passed" : "TellBacklogFound.SelfCheck: FAILED");
    }

    /// <summary>A tell from <paramref name="sender"/>, named as the roster held it when it arrived.</summary>
    private static ChatLine From(string sender, uint playerId, string raw) =>
        new(Tell, sender, playerId, "", [new ColorRun(raw, 1, 1, 1)], DateTime.UtcNow, raw);
}
