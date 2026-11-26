using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The lamp motes' plain regressions: lamps that hold none or one lamp's, the floor gap, a blast's reach and falloff,
/// a walker's reach overhead, what shows with the light emptied, the air off or at Low, and what the stage gets.
/// Oracle, by hand: the fixture's still crypt room, a lamp's 40 motes and the lamps and blasts placed below.</summary>
internal static class AirMotesRegressions
{
    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirMotesRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        NoReach(check);
        SeatedTwice(check);
        PastTheMost(check);
        OverTheFloor(check);
        BlastMoves(check);
        PacksShown(check);
        DarkHidden(check);
        OffShows(check);
        LowHolds(check);
        BlastReach(check);
        BlastFalls(check);
        StirsLow(check);
        log.Info(check.Ok ? "AirMotesRegressions.SelfCheck: passed" : "AirMotesRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A lamp's motes, as the fixture's lamp seats them.</summary>
    private const int OneLamp = 40;

    /// <summary>A crypt room with no lamp, a frame stepped.</summary>
    private static void Dark()
    {
        EnterWith(Crypt, Room(), 0f, false, false, false);
        Go(Frame, Vector2.Zero);
    }

    private static void NoReach(Checker check)
    {
        Dark();
        AirScene.Lamp(Lamp, 0f);
        int n = AirScene.Particles(AirLayer.Motes).Length;
        check.Expect(n == 0, $"a lamp reaching 0 m holds {n} motes as it is seated (want none)");
    }

    private static void SeatedTwice(Checker check)
    {
        Dark();
        AirScene.Lamp(Lamp, LampReach);
        AirScene.Lamp(Lamp + new Vector3(0.1f, 0f, 0f), LampReach);
        Go(Frame, Vector2.Zero);
        int n = AirScene.Particles(AirLayer.Motes).Length;
        check.Expect(n == OneLamp, $"a lamp seated twice, 0.1 m apart, holds {n} motes (want {OneLamp}, one lamp's)");
    }

    private static void PastTheMost(Checker check)
    {
        Dark();
        int seated = 0;
        for (float x = -6f; x <= 6f; x += 3f)
            for (float y = -6f; y <= 3f; y += 3f)
                if (seated++ < 17) AirScene.Lamp(new Vector3(x, y, 2f), LampReach);
        Go(Frame, Vector2.Zero);
        int n = AirScene.Particles(AirLayer.Motes).Length;
        check.Expect(n == 16 * OneLamp, $"17 lamps hold {n} motes (want {16 * OneLamp}, the first 16 lamps')");
    }

    private static void OverTheFloor(Checker check)
    {
        Dark();
        AirScene.Lamp(new Vector3(0f, 0f, 0.5f), LampReach);
        Go(Frame, Vector2.Zero);
        float lowest = float.MaxValue;
        foreach (AirParticle p in AirScene.Particles(AirLayer.Motes)) lowest = MathF.Min(lowest, p.Pos.Z);
        check.Expect(lowest >= 0.29f, $"a lamp 0.5 m over the floor floats its lowest mote at {lowest:F3} m (want 0.3 m up or more)");
    }

    private static void BlastMoves(Checker check)
    {
        Dark();
        AirScene.Lamp(Lamp, LampReach);
        Go(1f, Vector2.Zero);
        AirParticle[] before = AirScene.Particles(AirLayer.Motes).ToArray();
        AirScene.Blast(Lamp, 2f);
        Go(0.5f, Vector2.Zero);
        ReadOnlySpan<AirParticle> after = AirScene.Particles(AirLayer.Motes);
        check.Expect(after.Length == before.Length, $"Given: {before.Length} motes before the blast, {after.Length} after");
        if (after.Length != before.Length) return;
        float gone = 0f;
        int near = 0;
        for (int i = 0; i < before.Length; i++)
        {
            float was = Vector3.Distance(before[i].Pos, Lamp);
            if (was >= 2f) continue;
            gone += Vector3.Distance(after[i].Pos, Lamp) - was;
            near++;
        }
        float mean = near > 0 ? gone / near : 0f;
        check.Expect(near > 0 && mean > 0.3f, $"half a second after a blast at the lamp its {near} motes within 2 m moved {mean:F3} m out (want over 0.3 m)");
    }

    private static void PacksShown(Checker check)
    {
        var into = new Vector4[AirScene.MotesPacked];
        Dark();
        int none = AirScene.PackMotes(into);
        AirScene.Lamp(Lamp, LampReach);
        Go(Frame, Vector2.Zero);
        int fresh = AirScene.PackMotes(into);
        float freshest = 0f;
        for (int i = 1; i < fresh; i++) freshest = MathF.Max(freshest, into[i].W);
        Go(3f, Vector2.Zero);
        int shown = AirScene.Particles(AirLayer.Motes).Length, packed = AirScene.PackMotes(into);
        float least = 1f, most = 0f;
        for (int i = 1; i < packed; i++) { least = MathF.Min(least, into[i].W); most = MathF.Max(most, into[i].W); }
        check.Expect(none == 0, $"a room with no lamp packs {none} records (want none)");
        check.Expect(packed == shown + 1 && into[0].X == shown, $"the stage gets {packed} records headed {into[0].X} for {shown} motes shown (want a head and one a mote)");
        check.Expect(freshest < 0.1f && least >= 0f && most == 1f, $"motes just set afloat glow {freshest:F3} at most, and three seconds on from {least:F3} to {most:F3} (want faint, then 0 to 1 with some full)");
    }

    private static void DarkHidden(Checker check)
    {
        Dark();
        AirScene.Lamp(Lamp, LampReach);
        Go(3f, Strong);
        int shown = AirScene.Particles(AirLayer.Motes).Length, packed = AirScene.PackMotes(new Vector4[AirScene.MotesPacked]);
        check.Expect(shown == 0 && packed <= 1, $"a gust that emptied the light leaves {shown} motes shown and {packed} records packed (want none, a head at most)");
    }

    private static void OffShows(Checker check)
    {
        Dark();
        AirScene.Lamp(Lamp, LampReach);
        Go(Frame, Vector2.Zero);
        int on = AirScene.Particles(AirLayer.Motes).Length;
        Air.Enabled.V = false;
        int off = AirScene.Particles(AirLayer.Motes).Length;
        bool floats = AirScene.Floats;
        Air.Enabled.V = true;
        check.Expect(on > 0 && off == 0 && !floats, $"with the air off {off} motes show and they float {floats} (want none, false; {on} showed)");
    }

    private static void LowHolds(Checker check)
    {
        Air.Quality.V = "low";
        Dark();
        AirScene.Lamp(Lamp, LampReach);
        Go(1f, Vector2.Zero);
        AirScene.Blast(Lamp, 4f);
        Go(Frame, Vector2.Zero);
        float fastest = Fastest(AirLayer.Motes, Lamp, LampReach);
        Air.Quality.V = "medium";
        check.Expect(fastest < Slow, $"at Low a blast at the lamp leaves its fastest mote at {fastest:F3} m/s (want none thrown)");
    }

    private static void BlastReach(Checker check)
    {
        Dark();
        AirScene.Lamp(Lamp, LampReach);
        Go(1f, Vector2.Zero);
        Vector3 at = Lamp + new Vector3(-3f, 0f, 0f);
        AirScene.Blast(at, 2f);
        Go(Frame, Vector2.Zero);
        float most = 0f;
        int far = 0;
        foreach (AirParticle p in AirScene.Particles(AirLayer.Motes))
            if (Vector3.Distance(p.Pos, at) > 2.1f) { most = MathF.Max(most, p.Vel.Length()); far++; }
        check.Expect(far > 0 && most < Slow, $"a 2 m blast leaves the fastest of the {far} motes past its reach at {most:F3} m/s (want none thrown)");
    }

    private static void BlastFalls(Checker check)
    {
        Dark();
        AirScene.Lamp(Lamp, LampReach);
        Go(1f, Vector2.Zero);
        AirScene.Blast(Lamp, 4f);
        Go(Frame, Vector2.Zero);
        float near = 0f, rim = 0f;
        int n = 0, r = 0;
        foreach (AirParticle p in AirScene.Particles(AirLayer.Motes))
        {
            float d = Vector3.Distance(p.Pos, Lamp);
            if (d < 1.5f) { near += p.Vel.Length(); n++; }
            else if (d > 2.5f && d < 4f) { rim += p.Vel.Length(); r++; }
        }
        near = n > 0 ? near / n : 0f;
        rim = r > 0 ? rim / r : float.MaxValue;
        check.Expect(n > 0 && r > 0 && near > 2f * rim, $"a 4 m blast throws its {n} near motes at {near:F3} m/s and its {r} far ones at {rim:F3} m/s (want the near over twice as fast)");
    }

    /// <summary>Four lamps about the end of the walker's path, so its last second passes motes both low and overhead.</summary>
    private static readonly Vector3[] StirLamps = [new(4.5f, -0.6f, 2.4f), new(4.5f, 0.6f, 2.4f), new(4f, 0f, 2.6f), new(5f, 0f, 2.6f)];

    private static void StirsLow(Checker check)
    {
        Dark();
        foreach (Vector3 at in StirLamps) AirScene.Lamp(at, LampReach);
        Go(1f, Vector2.Zero);
        AirParticle[] before = AirScene.Particles(AirLayer.Motes).ToArray();
        DbId world = MistPartingApprovedExamples.World(), who = MistPartingApprovedExamples.Seat(world, new Vector3(0f, 0f, 0f), true);
        Walk(world, who, Vector3.Zero, new Vector3(5f, 0f, 0f), true, Pace(5f));
        ReadOnlySpan<AirParticle> after = AirScene.Particles(AirLayer.Motes);
        float low = 0f, high = 0f;
        int highs = 0;
        for (int i = 0; i < before.Length && after.Length == before.Length; i++)
        {
            Vector3 was = before[i].Pos;
            if (MathF.Abs(was.Y) > 1.4f || was.X < 3.5f || was.X > 5.5f) continue;
            if (was.Z < 1.8f) low = MathF.Max(low, after[i].Vel.Length());
            else if (was.Z > 2.6f) { high = MathF.Max(high, after[i].Vel.Length()); highs++; }
        }
        MistPartingApprovedExamples.Drop(world, who);
        check.Expect(after.Length == before.Length && highs > 0, $"Given: {before.Length} motes before the walk, {after.Length} after, {highs} overhead");
        check.Expect(low > Slow && high < Slow, $"a walker's last second swirls the motes beside it at up to {low:F3} m/s and those overhead at {high:F3} m/s (want swirled, then left)");
    }
}
