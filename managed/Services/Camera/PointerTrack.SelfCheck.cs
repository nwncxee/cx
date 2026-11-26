using CX.Framework;

namespace CX.Services.Camera;

/// <summary>The pointer track against the warp echoes Xwayland can lose, merge or deliver late, and the cursor hold
/// against the engine's mid-look cursor calls. Oracle: the deltas a hand moving the cursor would produce, worked by
/// hand for a 1920x1080 window (edge band 240 x 135, a warp's target within 270).</summary>
internal static class PointerTrackSelfCheck
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "PointerTrack.SelfCheck");
        var t = new PointerTrack();
        int dx, dy, wx, wy;
        bool moved, warp;

        // Given a look begun at (900, 500)
        // When the cursor moves to (910, 497)
        // Then the turn is (10, -3) and no warp is asked for
        t.Begin(900, 500, 1920, 1080, false);
        moved = t.Feed(910, 497, out dx, out dy, out warp, out _, out _);
        check.Expect(moved && dx == 10 && dy == -3 && !warp, $"a small move turns by itself ({dx},{dy} warp {warp})");

        // Given the cursor near the right edge
        // When it reaches the band's first pixel, and before that the pixel inside it
        // Then only the band's pixel asks for a warp to the centre, and both turn by their own size
        t.Begin(1660, 500, 1920, 1080, false);
        moved = t.Feed(1679, 500, out dx, out _, out warp, out _, out _);
        check.Expect(moved && dx == 19 && !warp, $"one pixel short of the band warps nothing ({dx} warp {warp})");
        moved = t.Feed(1680, 500, out dx, out dy, out warp, out wx, out wy);
        check.Expect(moved && dx == 1 && dy == 0 && warp && wx == 960 && wy == 540, $"the edge band warps to the centre ({dx},{dy} warp {warp} to {wx},{wy})");

        // Given that warp's echo is lost
        // When the next real move lands 100 px right of the centre
        // Then the turn is (100, -2), not the distance from the edge and not cut short
        moved = t.Feed(1060, 538, out dx, out dy, out _, out _, out _);
        check.Expect(moved && dx == 100 && dy == -2, $"a lost echo costs nothing ({dx},{dy})");

        // Given a warp from the edge band
        // When one event fewer than the stale limit, queued from before the warp, arrives after it
        // Then each is dropped, the echo turns nothing, and the move after it turns by itself
        t = new PointerTrack();
        t.Begin(1660, 500, 1920, 1080, false);
        t.Feed(1700, 500, out _, out _, out _, out _, out _);
        bool any = false;
        for (int i = 1; i < PointerTrack.StaleLimit; i++) any |= t.Feed(1700 + i, 502, out _, out _, out _, out _, out _);
        moved = t.Feed(960, 540, out dx, out dy, out _, out _, out _);
        bool next = t.Feed(950, 541, out int ndx, out int ndy, out _, out _, out _);
        check.Expect(!any && t.Dropped == PointerTrack.StaleLimit - 1 && t.Lost == 0, $"stale edge events are dropped (dropped {t.Dropped}, lost {t.Lost})");
        check.Expect(!moved && next && ndx == -10 && ndy == 1, $"the echo turns nothing and the next move turns by itself ({dx},{dy} then {ndx},{ndy})");

        // Given a warp with more queued events behind it than the stale limit
        // When its echo lands after the warp was judged lost
        // Then the echo turns nothing and the 3 px move after it turns 3
        t.Begin(1660, 500, 1920, 1080, false);
        t.Feed(1700, 500, out _, out _, out _, out _, out _);
        for (int i = 1; i <= PointerTrack.StaleLimit + 4; i++) t.Feed(1500, 540 + i, out _, out _, out _, out _, out _);
        moved = t.Feed(960, 540, out dx, out dy, out _, out _, out _);
        next = t.Feed(963, 540, out ndx, out _, out _, out _, out _);
        check.Expect(!moved && next && ndx == 3, $"a late echo turns nothing ({dx},{dy} then {ndx})");

        // Given the same, with the queued events at the edge
        // When the echo lands
        // Then it turns nothing and the move after it turns by itself
        t.Begin(1660, 500, 1920, 1080, false);
        t.Feed(1700, 500, out _, out _, out _, out _, out _);
        for (int i = 1; i <= PointerTrack.StaleLimit + 4; i++) t.Feed(1700 + i, 500, out _, out _, out _, out _, out _);
        moved = t.Feed(960, 540, out dx, out _, out _, out _, out _);
        next = t.Feed(963, 540, out ndx, out _, out _, out _, out _);
        check.Expect(!moved && next && ndx == 3, $"a late echo behind edge events turns nothing ({dx} then {ndx})");

        // Given two warps in one look, each with stale events and an echo
        // When the second warp's stale events would reach the limit only counted with the first's
        // Then neither warp is lost
        t = new PointerTrack();
        t.Begin(1660, 500, 1920, 1080, false);
        for (int round = 0; round < 2; round++)
        {
            t.Feed(1700, 500, out _, out _, out _, out _, out _);
            for (int i = 0; i < PointerTrack.StaleLimit / 2 + 1; i++) t.Feed(1701, 500, out _, out _, out _, out _, out _);
            t.Feed(960, 540, out _, out _, out _, out _, out _);
            t.Feed(1660, 500, out _, out _, out _, out _, out _);
        }
        check.Expect(t.Lost == 0 && t.Warps == 2, $"two warps in one look count their stale events apart (lost {t.Lost}, warps {t.Warps})");

        // Given a window system that loses one warp
        // When the look reaches the edge again
        // Then it still warps; a second lost warp in a row stops warping, and later moves turn by their own size
        t = new PointerTrack();
        t.Begin(1660, 500, 1920, 1080, false);
        t.Feed(1700, 500, out _, out _, out _, out _, out _);
        for (int i = 0; i < PointerTrack.StaleLimit; i++) t.Feed(1500, 500, out _, out _, out _, out _, out _);
        t.Feed(1700, 500, out _, out _, out warp, out _, out _);
        check.Expect(!t.WarpsIgnored && warp, $"one lost warp keeps warping (ignored {t.WarpsIgnored}, warp {warp})");
        for (int i = 0; i < PointerTrack.StaleLimit; i++) t.Feed(1500, 500, out _, out _, out _, out _, out _);
        moved = t.Feed(1705, 500, out dx, out _, out warp, out _, out _);
        check.Expect(t.WarpsIgnored && !warp && moved && dx == 205, $"two lost warps stop warping ({dx} warp {warp} ignored {t.WarpsIgnored})");

        // Given a compositor that ignores every warp
        // When the cursor stays at the edge for more events than two warps can leave queued
        // Then no event ever turns by the distance to the centre
        t = new PointerTrack();
        t.Begin(1660, 500, 1920, 1080, false);
        t.Feed(1700, 500, out _, out _, out _, out _, out _);
        int worst = 0;
        for (int i = 1; i <= 2 * PointerTrack.StaleLimit + 4; i++)
        {
            t.Feed(1700 + i * 5, 500, out dx, out _, out warp, out _, out _);
            if (System.Math.Abs(dx) > worst) worst = System.Math.Abs(dx);
        }
        check.Expect(t.WarpsIgnored && worst <= 5 && !warp, $"ignored warps stop warping and never jump (worst {worst}, ignored {t.WarpsIgnored})");

        // Given the action camera begins by warping to the centre
        // When a move from before that warp arrives from where the cursor was
        // Then it is dropped, and the moves after turn by their own size
        t.Begin(960, 540, 1920, 1080, true);
        moved = t.Feed(200, 900, out _, out _, out _, out _, out _);
        next = t.Feed(966, 540, out dx, out dy, out _, out _, out _);
        check.Expect(!moved && next && dx == 6 && dy == 0, $"the action camera's first warp drops the old position ({dx},{dy})");

        // Given a capture begun in a 1920x1080 window
        // When the window shrinks to 1280x720 mid-capture and the cursor reaches the new edge band
        // Then the warp goes to the new centre
        t.Begin(900, 360, 1920, 1080, false);
        t.Size(1280, 720);
        t.Feed(1130, 360, out _, out _, out warp, out wx, out wy);
        check.Expect(warp && wx == 640 && wy == 360, $"a resize moves the edge band and the centre (warp {warp} to {wx},{wy})");

        // Given a capture began with the cursor shown
        // When the engine hides it, shows it and asks its state mid-look
        // Then each call answers hidden, and the release restores the last state asked for
        var h = new CursorHold();
        h.Begin(1);
        int a1 = h.Ask(0), a2 = h.Ask(1), a3 = h.Ask(-1);
        check.Expect(h.Held && a1 == 0 && a2 == 0 && a3 == 0 && h.End() == 1 && !h.Held, $"a mid-look show is held for the release ({a1},{a2},{a3})");
        h.Begin(1);
        h.Ask(0);
        check.Expect(h.End() == 0, "a mid-look hide stays hidden after the release");
        h.Begin(0);
        h.Ask(-1);
        check.Expect(h.End() == 0, "a cursor hidden before the capture stays hidden after it");

        log.Info(check.Ok ? "PointerTrack.SelfCheck: passed" : "PointerTrack.SelfCheck: FAILED");
    }
}
