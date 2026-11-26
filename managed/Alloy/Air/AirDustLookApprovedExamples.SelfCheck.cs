using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirDustMarch;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>How the dust and the night mist look once drawn, through the stage's march over the field the air uploads.
/// Oracle, by hand: a standing character's eye 1.7 m up, stock's point light at gamma 2.2, 1/e as "still seen".</summary>
internal static class AirDustLookApprovedExamples
{
    /// <summary>A standing character's eye over the floor, metres.</summary>
    private const float Eye = 1.7f;

    /// <summary>The distance between two samples a smoothness reading takes, metres.</summary>
    private const float Pace = 0.05f;

    /// <summary>The crypt's light: a dim cold ambient and no sky.</summary>
    private static readonly Shine CryptLight = new(new Vector3(0.03f, 0.03f, 0.04f), Vector3.Zero, Vector3.Zero);

    /// <summary>The night's light by the water: a dark fog under a pale moon.</summary>
    private static readonly Shine NightLight = new(new Vector3(0.02f, 0.02f, 0.03f), new Vector3(0.02f, 0.02f, 0.03f), new Vector3(0.05f, 0.06f, 0.1f));

    /// <summary>A torch's colour as the engine hands it over, sRGB.</summary>
    private static readonly Vector3 Torch = new(1f, 0.55f, 0.2f);

    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirDustLookApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        Field declared = Upload();
        check.Expect(declared.Declared, "the dust stage declares every number its march takes");
        if (declared.Declared)
        {
            SeenThrough(check);
            Smooth(check);
            NoBands(check);
            StairSmooth(check);
            NoneOffTheLedge(check);
            NoneOverTheDrop(check);
            SoftWake(check);
            LampWhereDustIs(check);
            MistSeenThrough(check);
            MistSmooth(check);
            MistSoftWake(check);
            MistLampWhereItIs(check);
        }
        log.Info(check.Ok ? "AirDustLookApprovedExamples.SelfCheck: passed" : "AirDustLookApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The field the stage takes now, packed fresh.</summary>
    private static Field Upload()
    {
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        return new Field(Sent);
    }

    /// <summary>A crypt hall 60 m across, stone inside and walls round it.</summary>
    private static List<Tile> Hall() => Ground(40f, 2f, (_, _) => 0f, (x, y) => MathF.Abs(x) >= 30f || MathF.Abs(y) >= 30f ? Nonwalk : Stone);

    /// <summary>Settle <paramref name="tiles"/> as a crypt three still seconds after entry.</summary>
    private static Field Crypted(List<Tile> tiles)
    {
        Enter(AirFixture.Crypt, tiles, 0f, false);
        Go(3f, Vector2.Zero);
        return Upload();
    }

    /// <summary>The quay by night, its mist gathered.</summary>
    private static Field Misted()
    {
        Enter(City, Quay(), 0f, true);
        Go(Gather, Vector2.Zero);
        return Upload();
    }

    /// <summary>What the eye at <paramref name="eye"/> keeps of the floor at <paramref name="floor"/> through the air.</summary>
    private static float Keep(Field f, Vector3 eye, Vector3 floor, Shine room) => f.March(eye, floor, room, ReadOnlySpan<Point>.Empty).Keep;

    // Given dust on a crypt's floor
    // When the eye looks along the floor across 10 m of it
    // Then the floor beyond is still visible through the dust: dust greys a view out over tens of metres, never within one.
    private static void SeenThrough(Checker check)
    {
        Field f = Crypted(Hall());
        Vector3 eye = new(-25f, 0.5f, Eye);
        float ten = Keep(f, eye, new Vector3(-15f, 0.5f, 0f), CryptLight), forty = Keep(f, eye, new Vector3(15f, 0.5f, 0f), CryptLight);
        check.Expect(ten >= 1f / MathF.E && forty < ten && forty < 0.9f,
            $"F1 the eye keeps {ten:P0} of the floor 10 m off and {forty:P0} of it 40 m off (want at least 37% at 10 m, greyer at 40 m)");
    }

    /// <summary>The metres of dust or mist over (<paramref name="x"/>, <paramref name="y"/>) and the mean height they hang
    /// at over <paramref name="floor"/>, on a ray that ends on that floor.</summary>
    private static (float Depth, float Height) Column(Field f, float x, float y, float floor, bool mist)
    {
        const float dz = 0.01f;
        float depth = 0f, moment = 0f;
        for (float z = floor - 0.5f + dz / 2f; z < floor + 2.5f; z += dz)
        {
            (float d, float m) = f.At(new Vector3(x, y, z), floor);
            float rho = mist ? m : d;
            depth += rho * dz;
            moment += rho * z * dz;
        }
        return (depth, depth > 0f ? moment / depth : 0f);
    }

    /// <summary>Along y = <paramref name="y"/> from <paramref name="x0"/> to <paramref name="x1"/>: the largest change in
    /// depth one <see cref="Pace"/> makes as a share of the depth's whole range, and the largest change in height.</summary>
    private static (float Jump, float Rise, float Range) Walk(Field f, float x0, float x1, float y, Func<float, float> floor, bool mist)
    {
        float lo = float.MaxValue, hi = float.MinValue, jump = 0f, rise = 0f;
        (float d, float h) was = Column(f, x0, y, floor(x0), mist);
        for (float x = x0; x <= x1; x += Pace)
        {
            (float d, float h) now = Column(f, x + Pace, y, floor(x + Pace), mist);
            jump = MathF.Max(jump, MathF.Abs(now.d - was.d));
            if (now.d > 0f && was.d > 0f) rise = MathF.Max(rise, MathF.Abs(now.h - was.h));
            lo = MathF.Min(lo, now.d);
            hi = MathF.Max(hi, now.d);
            was = now;
        }
        float range = hi - lo;
        return (range > 0f ? jump / range : 1f, rise, range);
    }

    // Given a crypt floor whose columns differ in dust depth
    // When the dust is drawn
    // Then its depth and its floor change smoothly between neighbouring columns: no 1 m square edge shows.
    private static void Smooth(Checker check)
    {
        Field f = Crypted(Room());
        (float jump, _, float range) = Walk(f, -7.5f, 7.5f, 0.3f, _ => 0f, false);
        check.Expect(range > 0f && jump <= 0.15f, $"F2 one {Pace} m pace across the room changes the dust's depth by {jump:P0} of its {range:F3} m range (want 15% at most)");
    }

    // Given dust on a crypt's floor, and an eye looking down along it to the floor 12 m off
    // When neighbouring pixels march it, each from its own offset along the ray
    // Then they see the floor through it alike: no band shows where the steps fall.
    private static void NoBands(Checker check)
    {
        Field f = Crypted(Room());
        var eye = new Vector3(-6f, 0.3f, Eye);
        var surf = new Vector3(6f, 0.3f, 0f);
        float lo = 1f, hi = 0f;
        for (float j = 0f; j < 1f; j += 0.125f)
        {
            float keep = f.March(eye, surf, CryptLight, ReadOnlySpan<Point>.Empty, j).Keep;
            lo = MathF.Min(lo, keep);
            hi = MathF.Max(hi, keep);
        }
        check.Expect(hi < 1f && hi - lo <= 0.03f, $"F2 from one offset to another the eye keeps {lo:P1} to {hi:P1} of the floor (want under 3 points apart, some dust)");
    }

    // Given a crypt floor with a dais half a metre up
    // When the dust is drawn
    // Then the height the dust lies at climbs onto the dais smoothly, and its depth leaves no bare seam along the step.
    private static void StairSmooth(Checker check)
    {
        List<Tile> room = Room();
        Deck(room, 0.3f, -RoomHalf, RoomHalf, RoomHalf, 0.5f, Stone);
        Field f = Crypted(room);
        Func<float, float> floor = x => x >= 0.3f ? 0.5f : 0f;
        (_, float rise, float range) = Walk(f, -3f, 3f, 0.3f, floor, false);
        float below = Column(f, -1.5f, 0.3f, 0f, false).Depth, above = Column(f, 2f, 0.3f, 0.5f, false).Depth, seam = float.MaxValue;
        for (float x = -1.5f; x <= 2f; x += Pace) seam = MathF.Min(seam, Column(f, x, 0.3f, floor(x), false).Depth);
        check.Expect(range > 0f && rise <= 0.1f && seam >= 0.5f * MathF.Min(below, above),
            $"F2 one {Pace} m pace onto the dais lifts the dust's height by {rise:F3} m at most, and its depth falls to {seam:F3} m on the step between {below:F3} m and {above:F3} m (want 0.1 m at most, half of either at least)");
    }

    // Given a crypt floor with a ledge 1.5 m high, its edge partway across a column
    // When the dust is drawn on the floor below the ledge
    // Then no dust from the ledge hangs over the floor half a column past its edge.
    private static void NoneOffTheLedge(Checker check)
    {
        List<Tile> room = Room();
        Deck(room, -RoomHalf, -RoomHalf, 0.7f, RoomHalf, 1.5f, Stone);
        Field f = Crypted(room);
        float hung = 0f;
        for (float z = 1.2f; z < 2.5f; z += 0.02f) hung += f.At(new Vector3(1.2f, 0.5f, z), 0f).Dust;
        float ledge = f.At(new Vector3(0.3f, 0.5f, 1.55f), 1.5f).Dust;
        check.Expect(hung == 0f && ledge > 0f, $"F2 the ledge's dust over the floor 0.5 m past its edge reads {hung:F4}, on the ledge 0.4 m short of it {ledge:F4} (want none, some)");
    }

    // Given a crypt floor whose edge drops 5 m into a pit, the edge partway across a column
    // When the dust is drawn
    // Then no dust hangs in the air over the drop, and dust still lies on the floor by the edge.
    private static void NoneOverTheDrop(Checker check)
    {
        List<Tile> hall = Ground(20f, 2f, (_, _) => -5f, (x, y) => MathF.Abs(x) >= 18f || MathF.Abs(y) >= 18f ? Nonwalk : Stone);
        Deck(hall, -18f, -18f, 0.7f, 18f, 0f, Stone);
        Field f = Crypted(hall);
        float over = 0f;
        for (float z = -4f; z < 1.5f; z += 0.02f) { (float d, float m) = f.At(new Vector3(0.85f, 0.5f, z), -5f); over += d + m; }
        float edge = f.At(new Vector3(0.3f, 0.5f, 0.05f), 0f).Dust;
        check.Expect(over == 0f && edge > 0f, $"F2 the dust over the pit 0.15 m past its edge reads {over:F4}, on the floor 0.4 m short of it {edge:F4} (want none, some)");
    }

    /// <summary>The spread of depth round (<paramref name="x"/>, <paramref name="y"/>) at the part's radius ahead of a
    /// walker heading +x, as a share of how deep the wake is; and the largest pace's change across the wake behind.</summary>
    private static (float Spread, float Jump) Wake(Field f, float x, float y, bool mist)
    {
        const float r = 0.6f;
        float centre = Column(f, x, y, 0f, mist).Depth, far = Column(f, x, y + 3f, 0f, mist).Depth;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int a = -90; a <= 90; a += 15)
        {
            float th = a * MathF.PI / 180f;
            float d = Column(f, x + r * MathF.Cos(th), y + r * MathF.Sin(th), 0f, mist).Depth;
            lo = MathF.Min(lo, d);
            hi = MathF.Max(hi, d);
        }
        float deep = far - centre, jump = 0f;
        float was = Column(f, x - 1f, y - 2f, 0f, mist).Depth;
        for (float s = y - 2f + Pace; s <= y + 2f; s += Pace)
        {
            float now = Column(f, x - 1f, s, 0f, mist).Depth;
            jump = MathF.Max(jump, MathF.Abs(now - was));
            was = now;
        }
        return deep > 0f ? ((hi - lo) / deep, jump / deep) : (1f, 1f);
    }

    /// <summary>A walker crossing from three metres west to (<paramref name="x"/>, <paramref name="y"/>) a pace at a time.</summary>
    private static void Cross(float x, float y)
    {
        for (float s = x - 3f; s < x - 1e-3f; s += 0.1f) AirScene.Part(new Vector3(s, y, 0f), new Vector3(s + 0.1f, y, 0f));
    }

    // Given a walker crossing the dust
    // When the wake is drawn
    // Then the wake has a rounded, soft edge, not square tiles.
    private static void SoftWake(Checker check)
    {
        Crypted(Room());
        Cross(0.5f, 0.5f);
        (float spread, float jump) = Wake(Upload(), 0.5f, 0.5f, false);
        check.Expect(spread <= 0.35f && jump <= 0.15f, $"F3 the wake's edge varies {spread:P0} of its depth round its head and one pace across it changes {jump:P0} (want 35% and 15% at most)");
    }

    /// <summary>Stock's point light at <paramref name="at"/> from <paramref name="lamps"/> over <paramref name="ambient"/>:
    /// each colour to the power 2.2, times (1 - f2) / (1 + f2) for f2 the squared share of the reach.</summary>
    private static Vector3 Stock(Vector3 ambient, ReadOnlySpan<Point> lamps, Vector3 at)
    {
        Vector3 sum = ambient;
        foreach (Point l in lamps)
        {
            float f2 = Vector3.DistanceSquared(l.At, at) / (l.Reach * l.Reach);
            if (f2 >= 1f) continue;
            Vector3 c = l.Colour;
            sum += new Vector3(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f)) * ((1f - f2) / (1f + f2));
        }
        return sum;
    }

    /// <summary>The air's light on the ray from <paramref name="eye"/> to <paramref name="surf"/> and how far it is over
    /// the brightest the room and the lamps lay where the air is, per channel (at most 0 when bounded).</summary>
    private static (Vector3 Added, float Keep, float Over) Lit(Field f, Vector3 eye, Vector3 surf, Shine room, Point[] lamps, bool mist)
    {
        (float keep, Vector3 added) = f.March(eye, surf, room, lamps);
        Vector3 most = Vector3.Zero;
        Vector3 bed = mist ? room.Fog + room.Diffuse : room.Ambient;
        for (float t = 0f; t <= 1f; t += 0.001f)
        {
            Vector3 p = Vector3.Lerp(eye, surf, t);
            (float d, float m) = f.At(p, surf.Z);
            if ((mist ? m : d) > 0f) most = Vector3.Max(most, Stock(bed, lamps, p));
        }
        Vector3 over = added - (1f - keep) * most;
        return (added, keep, MathF.Max(over.X, MathF.Max(over.Y, over.Z)));
    }

    // Given a crypt with lamps of a given colour
    // When the dust is drawn
    // Then no point of the dust is brighter than the ambient plus the light the lamps actually lay at that point, and a
    // lamp near the floor lights the dust near it.
    private static void LampWhereDustIs(Checker check) => LampLights(check, Crypted(Room()), CryptLight, false, "F4", 0f);

    /// <summary>A grazing ray 12 m along the floor centred on <paramref name="x"/>, a torch low by the floor near its far
    /// end and past the reach of where most of the air on it lies: is the air bounded by the light, and lit by the torch?</summary>
    private static void LampLights(Checker check, Field f, Shine room, bool mist, string label, float x)
    {
        var eye = new Vector3(x - 6f, 0.3f, Eye);
        var surf = new Vector3(x + 6f, 0.3f, 0f);
        Point[] lamp = { new(new Vector3(x + 3.4f, 0.3f, 0.2f), 1.5f, Torch) };
        (Vector3 lit, float keep, float over) = Lit(f, eye, surf, room, lamp, mist);
        (Vector3 dark, _, _) = Lit(f, eye, surf, room, Array.Empty<Point>(), mist);
        Vector3 torch = lit - dark;
        check.Expect(over <= 1e-5f, $"{label} the air adds {lit.X:F4},{lit.Y:F4},{lit.Z:F4}, {over:F5} over what the room and the lamp lay where it is (want none over)");
        check.Expect(torch.X >= 0.02f * (1f - keep) && torch.X > torch.Y && torch.Y > torch.Z,
            $"{label} the lamp low by the floor adds {torch.X:F4},{torch.Y:F4},{torch.Z:F4} to the {1f - keep:P0} the air covers (want 2% of it in red at least, in the torch's hue)");
    }

    // Given the night mist over water
    // When it is drawn
    // Then F1 holds for it too: the water 10 m off is still seen through it, and farther water is greyer.
    private static void MistSeenThrough(Checker check)
    {
        Field f = Misted();
        Vector3 eye = new(-5f, 0.5f, Eye);
        float ten = Keep(f, eye, new Vector3(5f, 0.5f, 0f), NightLight), forty = Keep(f, eye, new Vector3(35f, 0.5f, 0f), NightLight);
        check.Expect(ten >= 1f / MathF.E && forty < ten && forty < 0.9f,
            $"F5 the eye keeps {ten:P0} of the water 10 m off and {forty:P0} of it 40 m off through the mist (want at least 37% at 10 m, greyer at 40 m)");
    }

    // Given the night mist over water, thinning onto the bank
    // When it is drawn
    // Then F2 holds for it too: its depth changes smoothly from column to column.
    private static void MistSmooth(Checker check)
    {
        Field f = Misted();
        (float jump, _, float range) = Walk(f, -8f, 4f, 0.3f, _ => 0f, true);
        check.Expect(range > 0f && jump <= 0.15f, $"F5 one {Pace} m pace onto the bank changes the mist's depth by {jump:P0} of its {range:F3} m range (want 15% at most)");
    }

    // Given a walker crossing the night mist over water
    // When the wake is drawn
    // Then F3 holds for it too: the wake has a rounded, soft edge.
    private static void MistSoftWake(Checker check)
    {
        Misted();
        Cross(10.5f, 0.5f);
        (float spread, float jump) = Wake(Upload(), 10.5f, 0.5f, true);
        check.Expect(spread <= 0.35f && jump <= 0.15f, $"F5 the mist's wake varies {spread:P0} of its depth round its head and one pace across it changes {jump:P0} (want 35% and 15% at most)");
    }

    // Given the night mist over water and a lamp by the water
    // When it is drawn
    // Then F4 holds for it too: no point is brighter than the sky and the lamp lay there, and the lamp lights the mist by it.
    private static void MistLampWhereItIs(Checker check) => LampLights(check, Misted(), NightLight, true, "F5", 10f);
}
