using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The lamp motes' plain regressions: a lamp reaching nothing holds none, one lamp seated twice holds one lamp's,
/// the lamps past the most hold none, no mote floats under the floor, a blast moves the motes and the stage gets what shows.
/// Oracle, by hand: the fixture's still crypt room, a lamp's 40 motes and the lamps placed below.</summary>
internal static class AirMotesRegressions
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
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
}
