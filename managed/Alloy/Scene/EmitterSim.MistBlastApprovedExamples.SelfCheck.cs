using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary>A blast blows the mist out, and it rolls back: an Explosion emitter seated and burst through the walk's
    /// own detonate event and <c>ExplosionBirth</c>, its radius the emitter's blast radius. Oracle, by hand: area fog 10's floor read before the
    /// blast, against the mist inside the radius and a metre past it.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool MistBlastSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "EmitterSim.MistBlastApprovedExamples.SelfCheck");
        bool on = VolumetricFog.Enabled.V;
        string quality = VolumetricFog.Quality.V;
        try
        {
            VolumetricFog.Quality.V = "medium";
            VolumetricFog.Enabled.V = true;
            Reset();
            Fireball(check);
            Reset();
            OtherBlast(check);
            Reset();
            WindyBlast(check);
        }
        finally
        {
            Reset();
            VolumetricFog.Enabled.V = on;
            VolumetricFog.Quality.V = quality;
            GroundMist.Reset();
        }
        log.Info(check.Ok ? "EmitterSim.MistBlastApprovedExamples.SelfCheck: passed" : "EmitterSim.MistBlastApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Seat <paramref name="model"/> at the origin, its own Explosion emitter's blast radius
    /// <paramref name="radius"/>, and play one frame of an animation whose "detonate" event bursts it. Free it after.</summary>
    private static void Detonate(string model, float radius)
    {
        ModelDef m = BlastModel(model, false);
        m.Nodes[1].Emitter!.BlastRadius = radius;
        DbId obj = SeatBlast(m, out int first);
        if (first < 0) return;
        int blast = first + 1, s = BlastSlot(blast);
        BlastFrame(obj, blast, s, BlastAnim("detonate", "detonate"), 0f, 0.3f);
        UnseatBlast(obj, first, m.Nodes.Length);
    }

    /// <summary>Seat <paramref name="model"/> with a reference node whose own Explosion child, blast radius
    /// <paramref name="radius"/>, is the emitter its "detonate" event bursts. Free it after.</summary>
    private static void DetonateChild(string model, float radius)
    {
        ModelDef m = BlastModel(model, false), sub = BlastModel(model + "_sub", false);
        m.Nodes[1].Emitter!.BlastRadius = 0f;
        DbId obj = SeatReach(m, sub, out int first);
        if (first < 0) return;
        m.Nodes[RefKid].Emitter!.BlastRadius = radius;
        int kid = first + RefKid, s = BlastSlot(kid);
        BlastFrame(obj, kid, s, BlastAnim("detonate", "detonate"), 0f, 0.3f);
        UnseatBlast(obj, first, ReachParts);
    }

    /// <summary>The mean depth on a ring of <paramref name="r"/> around the origin, eight points.</summary>
    private static float Ring(float r)
    {
        float sum = 0f;
        for (int k = 0; k < 8; k++) sum += GroundMist.At(r * MathF.Cos(k * MathF.PI / 4f), r * MathF.Sin(k * MathF.PI / 4f));
        return sum / 8f;
    }

    /// <summary>Flat ground under area fog 10, settled a second; returns the floor.</summary>
    private static float Floor()
    {
        Fogged(Flat(), Vector3.Zero);
        Run(1f, Vector2.Zero);
        return GroundMist.At(0.5f, 0.5f);
    }

    private static void Fireball(Checker check)
    {
        float floor = Floor();

        // Given ground mist around a point
        // When a fireball detonates there
        // Then the mist within the blast's radius thins at once and thickens just outside it
        Detonate("cx_fireball", 5f);
        GroundMist.Step(Frame, Vector2.Zero);
        float inside = MistFixture.Near(0f, 0f, 2f), outside = Ring(6f);
        check.Expect(floor > 0f && inside < floor * 0.5f, $"E1 a frame after the fireball the mist inside lies {inside:F4} m, the floor {floor:F4} (want under half, of some)");
        check.Expect(outside > floor, $"E1 a metre past the radius it lies {outside:F4} m, the floor {floor:F4} (want thicker)");

        // Given that cleared ring
        // When ten seconds pass
        // Then the mist has flowed back over the blast point
        Run(10f, Vector2.Zero);
        float back = GroundMist.At(0.5f, 0.5f);
        check.Expect(inside < floor * 0.5f && back >= floor * 0.9f,
            $"E3 10 s after a cleared ring ({inside:F4} m) the blast point holds {back:F4} m (want at least 90% of the {floor:F4} m floor)");
    }

    private static void OtherBlast(Checker check)
    {
        float floor = Floor();

        // Given ground mist around a point
        // When a detonation other than a fireball goes off there
        // Then the mist within its radius thins the same way
        DetonateChild("cx_holyblast", 3f);
        GroundMist.Step(Frame, Vector2.Zero);
        float inside = MistFixture.Near(0f, 0f, 1f);
        check.Expect(floor > 0f && inside < floor * 0.5f, $"E2 a frame after the other blast the mist inside lies {inside:F4} m, the floor {floor:F4} (want under half, of some)");
    }

    private static void WindyBlast(Checker check)
    {
        float floor = Floor();
        GroundMist.Place(new Vector3(-15f, 0f, 0f), 3f, 1f);
        Vector2 was = Centroid(60f, floor, out _);

        // Given high wind and a cleared ring
        // When ten seconds pass
        // Then the mist has closed over the blast point and drifts downwind again
        Detonate("cx_fireball", 5f);
        GroundMist.Step(Frame, HighWindX);
        float cleared = MistFixture.Near(0f, 0f, 2f);
        Run(10f, HighWindX);
        float back = GroundMist.At(0.5f, 0.5f);
        Vector2 now = Centroid(60f, floor, out float m);
        check.Expect(cleared < floor * 0.5f && back >= floor * 0.9f,
            $"E4 in high wind a ring cleared to {cleared:F4} m holds {back:F4} m after 10 s (want cleared, then at least 90% of the {floor:F4} m floor)");
        check.Expect(m > 0f && now.X - was.X > 1f, $"E4 the mist upwind moved {now.X - was.X:F3} m downwind over those 10 s (want over 1 m)");
    }
}
