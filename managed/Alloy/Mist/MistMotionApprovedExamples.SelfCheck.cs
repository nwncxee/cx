using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.MistFixture;
using static CX.Alloy.MistPartingApprovedExamples;

namespace CX.Alloy;

/// <summary>The mist moves like air: a walker's wake curls and runs on, a fireball throws a ragged ring that rolls outward and
/// lifts the mist, and a wall stops it. Oracle, by hand: area fog 10's floor, a fireball of 5 m at the origin, and a wall at 2 m.</summary>
internal static class MistMotionApprovedExamples
{
    /// <summary>A fireball's blast radius, the one the emitter examples burst.</summary>
    internal const float Fireball = 5f;

    /// <summary>A curl of the mist's motion over this, per second, is an eddy a player sees turn.</summary>
    internal const float Eddy = 0.3f;

    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MistMotionApprovedExamples.SelfCheck");
        using var switches = MistFixture.Switches.Force();
        Wake(check);
        RunsOn(check);
        RingRolls(check);
        StandsTall(check);
        Ragged(check);
        Wall(check);
        log.Info(check.Ok ? "MistMotionApprovedExamples.SelfCheck: passed" : "MistMotionApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The curl of the mist's motion at the world column (<paramref name="x"/>, <paramref name="y"/>), per second.</summary>
    internal static float Curl(float x, float y)
        => (GroundMist.Motion(x + 1f, y).Y - GroundMist.Motion(x - 1f, y).Y - GroundMist.Motion(x, y + 1f).X + GroundMist.Motion(x, y - 1f).X) * 0.5f;

    /// <summary>The strongest curl over the rectangle, column by column.</summary>
    internal static float MaxCurl(float x0, float y0, float x1, float y1)
    {
        float most = 0f;
        for (float x = x0 + 0.5f; x < x1; x += 1f)
            for (float y = y0 + 0.5f; y < y1; y += 1f) most = MathF.Max(most, MathF.Abs(Curl(x, y)));
        return most;
    }

    /// <summary>The fastest the mist moves over the rectangle, in metres a second.</summary>
    internal static float MaxMotion(float x0, float y0, float x1, float y1)
    {
        float most = 0f;
        for (float x = x0 + 0.5f; x < x1; x += 1f)
            for (float y = y0 + 0.5f; y < y1; y += 1f) most = MathF.Max(most, GroundMist.Motion(x, y).Length());
        return most;
    }

    /// <summary>The mean height the mist stands over the columns from <paramref name="r0"/> to <paramref name="r1"/> metres from the origin.</summary>
    internal static float Annulus(float r0, float r1)
    {
        float sum = 0f;
        int n = 0;
        for (float x = -r1 + 0.5f; x < r1; x += 1f)
            for (float y = -r1 + 0.5f; y < r1; y += 1f)
            {
                float r = MathF.Sqrt(x * x + y * y);
                if (r < r0 || r >= r1) continue;
                sum += GroundMist.Height(x, y);
                n++;
            }
        return n > 0 ? sum / n : 0f;
    }

    /// <summary>The radius, in metres, of the thickest ring of mist around the origin out to 20 m, and how thick it lies.</summary>
    internal static float Peak(out float thickest)
    {
        float at = 0f;
        thickest = 0f;
        for (float r = 0f; r < 20f; r += 0.5f)
        {
            float d = Annulus(r, r + 1f);
            if (d > thickest) { thickest = d; at = r + 0.5f; }
        }
        return at;
    }

    /// <summary>How fast, on the mean, the mist 1 to 3 m ahead of and beside <paramref name="stop"/> moves away from it, a walker having come along x.</summary>
    internal static float Outward(Vector2 stop)
    {
        float sum = 0f;
        int n = 0;
        for (float x = stop.X - 2.5f; x < stop.X + 3f; x += 1f)
            for (float y = stop.Y - 2.5f; y < stop.Y + 3f; y += 1f)
            {
                var d = new Vector2(x, y) - stop;
                float r = d.Length();
                if (r < 1f || r > 3f || d.X < -0.5f) continue;
                sum += Vector2.Dot(GroundMist.Motion(x, y), d / r);
                n++;
            }
        return n > 0 ? sum / n : 0f;
    }

    /// <summary>A walker crosses 5 m of settled mist from the origin along x and stops; returns the scene and the walker.</summary>
    private static (DbId world, DbId me) Crossed()
    {
        Fogged(Flat(), Vector3.Zero);
        Run(1f, Vector2.Zero);
        DbId world = World(), me = Seat(world, Vector3.Zero, true);
        Hold(world, 0.1f);
        Walk(world, me, Vector3.Zero, new Vector3(5f, 0f, 0f), true);
        return (world, me);
    }

    private static void Wake(Checker check)
    {
        (DbId world, DbId me) = Crossed();
        Hold(world, 1f);
        float early = MaxCurl(0f, -3f, 5f, 3f);
        Vector2 was = GroundMist.Motion(2.5f, 1.5f);
        Hold(world, 1f);
        float later = MaxCurl(0f, -3f, 5f, 3f);
        Vector2 now = GroundMist.Motion(2.5f, 1.5f);
        Hold(world, 8f);
        float settled = MaxMotion(-3f, -5f, 8f, 5f);

        // Given the docks with the player walking through the mist on the street
        // When they stop
        // Then the mist along the path behind them curls into eddies for a few seconds before it settles.
        check.Expect(early > Eddy && later > Eddy, $"E31 the path's strongest curl 1 s after the stop is {early:F3}/s and 2 s after {later:F3}/s (want both over {Eddy}/s)");
        check.Expect(Vector2.Distance(was, now) > 0.05f, $"E31 beside the path the mist moved {was} at 1 s and {now} at 2 s (want it turning, not set)");
        check.Expect(settled < 0.05f, $"E31 10 s after the stop the mist around the path moves at most {settled:F3} m/s (want settled, under 0.05)");
        Drop(world, me);
    }

    private static void RunsOn(Checker check)
    {
        (DbId world, DbId me) = Crossed();
        var stop = new Vector2(5f, 0f);
        Hold(world, 0.1f);
        float soon = Outward(stop);
        Hold(world, 0.5f);
        float later = Outward(stop);

        // Given the player walking through the mist
        // When they stop
        // Then the mist that was just ahead of and beside them goes on moving away from where they stopped for a moment, rather than freezing.
        check.Expect(soon > 0.2f && later > 0.2f, $"E39 1 to 3 m ahead of and beside the stop the mist moves away at {soon:F3} m/s 0.1 s after it and {later:F3} at 0.6 s (want both over 0.2)");
        Drop(world, me);
    }

    /// <summary>Settled mist around the origin, then a fireball there.</summary>
    private static void Detonated()
    {
        Fogged(Flat(), Vector3.Zero);
        Run(1f, Vector2.Zero);
        GroundMist.Blast(Vector3.Zero, Fireball);
    }

    private static void RingRolls(Checker check)
    {
        Detonated();
        Run(0.05f, Vector2.Zero);
        float landed = Peak(out float first);
        Run(0.95f, Vector2.Zero);
        float rolled = Peak(out float then);

        // Given ground mist around a point
        // When a fireball detonates there
        // Then over the next second the thick ring of mist travels outward past where it first landed, thinning as it goes.
        check.Expect(rolled >= landed + 1.5f, $"E33 the thickest ring lies {landed:F1} m out as it lands and {rolled:F1} m a second later (want 1.5 m further)");
        check.Expect(then < first, $"E33 the ring stands {first:F3} m as it lands and {then:F3} a second later (want thinner)");
    }

    private static void StandsTall(Checker check)
    {
        Detonated();
        float blast = 0f, far = 0f;
        for (int n = 0; n < 3; n++)
        {
            Run(1f, Vector2.Zero);
            blast = MathF.Min(n == 0 ? float.MaxValue : blast, Annulus(0f, Fireball + 5f));
            far = Annulus(25f, 35f);
        }
        Run(7f, Vector2.Zero);
        float back = Annulus(0f, Fireball + 5f), there = Annulus(25f, 35f);

        // Given ground mist around a point
        // When a fireball detonates there
        // Then for the next few seconds the mist around the blast stands taller than the mist elsewhere, and it is back to its usual height within ten seconds.
        check.Expect(blast > far * 1.1f, $"E35 for 3 s the mist within {Fireball + 5f} m stands at least {blast:F3} m, elsewhere {far:F3} (want over a tenth taller)");
        check.Expect(MathF.Abs(back - there) <= there * 0.05f, $"E35 10 s on the blast's mist stands {back:F3} m, elsewhere {there:F3} (want within 5%)");
    }

    private static void Ragged(Checker check)
    {
        Detonated();
        Run(0.3f, Vector2.Zero);
        Span<float> sector = stackalloc float[8];
        for (float x = -Fireball - 6f + 0.5f; x < Fireball + 6f; x += 1f)
            for (float y = -Fireball - 6f + 0.5f; y < Fireball + 6f; y += 1f)
            {
                float r = MathF.Sqrt(x * x + y * y);
                if (r < Fireball - 2f || r >= Fireball + 6f) continue;
                int k = (int)MathF.Floor((MathF.Atan2(y, x) + MathF.PI) / (MathF.Tau / 8f)) & 7;
                sector[k] += GroundMist.Height(x, y);
            }
        float most = 0f, least = float.MaxValue, sum = 0f;
        foreach (float v in sector) { most = MathF.Max(most, v); least = MathF.Min(least, v); sum += v; }
        float spread = (most - least) / MathF.Max(sum / 8f, 1e-6f);

        // Given ground mist around a point on open ground
        // When a fireball detonates there
        // Then the ring it throws is thicker in some directions than in others, not an even circle.
        check.Expect(spread > 0.25f, $"E36 the ring's eight sectors hold {least:F2} to {most:F2} m^3, a spread of {spread:F3} of their mean (want over 0.25)");
    }

    private static void Wall(Checker check)
    {
        Lay(Ground(160f, 1f, (_, _) => 0f, (x, _) => x >= 2f && x < 3f ? Nonwalk : Stone), Vector3.Zero, true, AreaAmount);
        Run(1f, Vector2.Zero);
        float before = Mass(3f, -10f, 15f, 10f);
        GroundMist.Blast(Vector3.Zero, Fireball);
        float most = 0f;
        for (int n = 0; n < 10; n++)
        {
            Run(0.1f, Vector2.Zero);
            most = MathF.Max(most, MathF.Abs(Mass(3f, -10f, 15f, 10f) - before));
        }

        // Given a fireball detonating 2 m from a wall, with mist on both sides
        // When the mist rolls outward
        // Then the mist on the far side of the wall is unchanged.
        check.Expect(most <= before * 0.01f, $"E38 past the wall {before:F2} m^3 lay before the blast and it moved by up to {most:F2} over the next second (want within 1%)");
    }
}
