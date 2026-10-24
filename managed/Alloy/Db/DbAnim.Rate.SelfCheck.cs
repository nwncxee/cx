using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The rate band's own property: the ladder and its hysteresis. A band chosen wrongly reads in a
/// session as something else entirely -- a creature at arm's length animating at a quarter rate, or a cloak
/// going rigid in the middle of the screen -- and neither leaves a log line.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Oracle: the ladder in <see cref="ChooseRateBand"/>'s remarks, transcribed by hand -- band 0
    /// inside <c>near</c>, 1 between it and <c>far</c>, 2 beyond, one band per call, and an edge crossed only
    /// once the distance clears it by the 15% hysteresis this layer declares.</summary>
    private static void RateBandChoice(Checker check)
    {
        const float near = 30f, far = 60f, h = 0.15f;
        check.Expect(ChooseRateBand(1f, near, far, 0) == 0, "inside the detail distance an object is band 0: full rate");
        check.Expect(ChooseRateBand(45f, near, far, 0) == 1, "past the detail distance, band 0 rises one");
        check.Expect(ChooseRateBand(45f, near, far, 1) == 1, "...and holds there");
        check.Expect(ChooseRateBand(200f, near, far, 1) == 2, "past twice it, band 1 rises");
        check.Expect(ChooseRateBand(200f, near, far, 0) == 1, "...one band at a time, never two");
        check.Expect(ChooseRateBand(45f, near, far, 2) == 1, "back inside the middle band, band 2 falls one");
        check.Expect(ChooseRateBand(1f, near, far, 1) == 0, "back inside the detail distance, band 1 falls");
        check.Expect(ChooseRateBand(near * 1.02f, near, far, 0) == 0,
                     "a distance just past the near edge does not change band: that is the pop");
        check.Expect(ChooseRateBand(near * (1f + h) * 1.01f, near, far, 0) == 1, "...and one past the widened edge does");
        check.Expect(ChooseRateBand(near * 0.98f, near, far, 1) == 1, "coming back, band 1 holds just inside the edge");
        check.Expect(ChooseRateBand(near / (1f + h) * 0.99f, near, far, 1) == 0, "...and falls past it");
        check.Expect(ChooseRateBand(200f, 0f, 0f, 2) == 0,
                     "a zero detail distance is the knob off: every object reads band 0 whatever it held");
        check.Expect(ChooseRateBand(-1f, near, far, 2) == 0, "a negative distance (no sphere measured) reads band 0");
        check.Expect(ChooseRateBand(200f, near, near, 0) == 0, "a far edge that is not past the near one stands the ladder down");
    }
}
