using CX.Framework;
using CX.Services.Chat.Core;
using static CX.Services.Chat.TellBacklogApprovedExamples;

namespace CX.Services.Chat;

/// <summary>Regression tests for the kept-tells examples E2 and E8, which hold on main before the change, so they carry no approved-spec
/// mark. Oracle: the line counts each example states, written by hand.</summary>
internal static class TellBacklogRegressions
{
    private const uint Tell = 0x002, Combat = 0x010, Party = 0x040;  // not-an-offset: chat channel flags

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "TellBacklogRegressions.SelfCheck");

        // Given a line of party chat
        // When 3,000 lines of combat feedback arrive after it
        // Then the party line no longer shows in the Party tab
        {
            var store = new ChatStore();
            store.Append(Line(Party, 7, "party line"));
            Fill(store, Combat, 3000);
            check.Expect(Shown(store, new Tab("Party", Party)).Length == 0, "E2 the party line no longer shows in the Party tab");
        }

        // Given a tell from Alice in her tell tab
        // When the player leaves the server and joins again
        // Then the tell tab starts empty
        {
            var store = new ChatStore();
            var alice = new Tab("T:Alice", Tell, 42) { PinnedName = "Alice" };
            store.Append(Line(Tell, 42, "hello from alice"));
            check.Expect(Shown(store, alice).Length == 1, "E8 the tell shows before leaving");
            store.Clear();
            check.Expect(Shown(store, alice).Length == 0, "E8 the tell tab starts empty after joining again");
        }

        log.Info(check.Ok ? "TellBacklogRegressions.SelfCheck: passed" : "TellBacklogRegressions.SelfCheck: FAILED");
    }
}
