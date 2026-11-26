using CX.Framework;

namespace CX.Services.Camera;

/// <summary>The pointer track against the warp echoes Xwayland can lose, merge or deliver late. Oracle: the
/// deltas a hand moving the cursor would produce, worked by hand for a 1920x1080 window.</summary>
internal static class PointerTrackSelfCheck
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "PointerTrack.SelfCheck");
        var t = new PointerTrack();

        // Given a look begun at (900, 500)
        // When the cursor moves to (910, 497)
        // Then the turn is (10, -3) and no warp is asked for
        t.Begin(900, 500, 1920, 1080);
        bool moved = t.Feed(910, 497, out int dx, out int dy, out bool warp, out _, out _);
        check.Expect(moved && dx == 10 && dy == -3 && !warp, $"a small move turns by itself ({dx},{dy} warp {warp})");

        // Given the cursor near the right edge
        // When it moves into the edge band
        // Then that move turns by its own size and a warp to the centre is asked for
        t.Begin(1660, 500, 1920, 1080);
        moved = t.Feed(1690, 500, out dx, out dy, out warp, out int wx, out int wy);
        check.Expect(moved && dx == 30 && dy == 0 && warp && wx == 960 && wy == 540, $"the edge band warps to the centre ({dx},{dy} warp {warp} to {wx},{wy})");

        // Given that warp's echo is lost
        // When the next real move lands at (975, 538)
        // Then the turn is (15, -2), not the distance from the edge
        moved = t.Feed(975, 538, out dx, out dy, out _, out _, out _);
        check.Expect(moved && dx == 15 && dy == -2, $"a lost echo costs nothing ({dx},{dy})");

        // Given another warp from the edge band
        // When motion queued from before the warp arrives after it, still at the edge
        // Then it is dropped, and the move after the echo turns by its own size
        t.Begin(1660, 500, 1920, 1080);
        t.Feed(1700, 500, out _, out _, out _, out _, out _);
        moved = t.Feed(1720, 502, out dx, out dy, out _, out _, out _);
        check.Expect(!moved, $"a stale edge event is dropped ({dx},{dy})");
        moved = t.Feed(960, 540, out dx, out dy, out _, out _, out _);
        check.Expect(!moved, "the echo itself turns nothing");
        moved = t.Feed(950, 541, out dx, out dy, out _, out _, out _);
        check.Expect(moved && dx == -10 && dy == 1, $"the move after a late echo turns by itself ({dx},{dy})");

        // Given a compositor that ignores warps
        // When the cursor stays at the edge for more events than a warp can leave queued
        // Then warping stops, the moves turn by their own size, and no event ever turns by the distance to the centre
        t.Begin(1660, 500, 1920, 1080);
        t.Feed(1700, 500, out _, out _, out _, out _, out _);
        int worst = 0;
        for (int i = 1; i <= PointerTrack.StaleLimit + 4; i++)
        {
            t.Feed(1700 + i * 5, 500, out dx, out _, out warp, out _, out _);
            if (System.Math.Abs(dx) > worst) worst = System.Math.Abs(dx);
        }
        check.Expect(t.WarpsIgnored && worst <= 5 && !warp, $"ignored warps stop warping and never jump (worst {worst}, ignored {t.WarpsIgnored})");

        // Given the action camera begins by warping to the centre
        // When a move from before that warp arrives from where the cursor was
        // Then it is dropped, and the moves after turn by their own size
        t.BeginWarped(960, 540, 1920, 1080);
        moved = t.Feed(200, 900, out _, out _, out _, out _, out _);
        bool next = t.Feed(966, 540, out dx, out dy, out _, out _, out _);
        check.Expect(!moved && next && dx == 6 && dy == 0, $"the action camera's first warp drops the old position ({dx},{dy})");

        log.Info(check.Ok ? "PointerTrack.SelfCheck: passed" : "PointerTrack.SelfCheck: FAILED");
    }
}
