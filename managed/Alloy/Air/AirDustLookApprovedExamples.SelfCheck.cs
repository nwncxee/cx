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
    private static readonly Shine CryptLight = new(new Vector3(0.03f, 0.03f, 0.04f), Vector3.Zero);

    /// <summary>The night's light by the water: a dark ambient under a pale moon.</summary>
    private static readonly Shine NightLight = new(new Vector3(0.02f, 0.02f, 0.03f), new Vector3(0.05f, 0.06f, 0.1f));

    /// <summary>An interior's light: the dim ambient its lamps work over, under the bright sun the area still carries
    /// though no sky shows in it.</summary>
    private static readonly Shine HallLight = new(new Vector3(0.03f, 0.03f, 0.04f), new Vector3(0.6f, 0.6f, 0.5f));

    /// <summary>The stock fog placeable a module stands in a crypt, its emitter, and the frames it runs before a reading.</summary>
    private const string StockFog = "silm_fog_50_lo", StockNode = "AuroraEmitter01";
    private const int Settled = 1200;

    /// <summary>A torch's colour as the engine hands it over, sRGB.</summary>
    private static readonly Vector3 Torch = new(1f, 0.55f, 0.2f);

    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
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
            PlaceableSeenThrough(check);
            PlaceableLitByTheRoom(check);
            MistEdgeSoft(check);
            DenseFogSeenThrough(check);
            PlaceablePoolBounded(check);
            DarkCryptStaysDark(check);
            MistyCryptStaysDark(check);
            SoftTop(check);
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

    /// <summary><paramref name="tiles"/> as an interior crypt holding the stock fog placeable at <paramref name="at"/>, its
    /// mist settled.</summary>
    private static Field Placed(List<Tile> tiles, Vector3 at, int frames = Settled, float amount = 0f)
    {
        Lay(tiles, Vector3.Zero, true, amount);
        AirScene.Enter(AirFixture.Crypt, false, true);
        EmitterSim.MistEmit(StockFog, new[] { StockNode }, new[] { at }, frames);
        Go(3f, Vector2.Zero);
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

    // Given dust on a crypt's floor, and an eye looking down along it to the floor 12 m off; and dust on a stair of
    // 0.5 m treads and 0.25 m risers, seen from above
    // When neighbouring pixels march it, each from its own offset along the ray, and the eye looks along the stair
    // Then they see the floor through it alike, and each tread as its own floor gives it: no band shows where the steps
    // or the columns fall.
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
        List<Tile> stair = Room();
        for (int k = 0; k < 8; k++) Deck(stair, Tread * k, -RoomHalf, RoomHalf, RoomHalf, Riser * (k + 1), Stone);
        Field s = Crypted(stair);
        var above = new Vector3(-6f, 0.3f, 9f);
        float off = 0f, most = 1f;
        for (float x = 0.0625f; x < 3.5f; x += 0.125f)
        {
            var tread = new Vector3(x, 0.3f, StairFloor(x, 0f));
            float keep = s.March(above, tread, CryptLight, ReadOnlySpan<Point>.Empty).Keep;
            off = MathF.Max(off, MathF.Abs(keep - s.ExactKeep(above, tread, StairFloor, ExactSteps)));
            most = MathF.Min(most, keep);
        }
        check.Expect(most < 1f && off <= 0.005f, $"F2 along the stair the eye's keep of the treads strays {off:P2} from the stair's own floor marched exactly (want 0.5 points at most, some dust)");
    }

    /// <summary>A stair's tread, and its riser, metres.</summary>
    private const float Tread = 0.5f, Riser = 0.25f;

    /// <summary>The samples the exact march of the stair takes along one ray.</summary>
    private const int ExactSteps = 2048;

    /// <summary>The stair's floor at (<paramref name="x"/>, <paramref name="y"/>): eight treads up from x = 0, the
    /// top one running on to the wall.</summary>
    private static float StairFloor(float x, float y) => x < 0f ? 0f : Riser * (MathF.Min(MathF.Floor(x / Tread), 7f) + 1f);

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
    /// each colour to the power 2.2, times (1 - f2) / (x + y f2) for f2 the squared share of the reach and (x, y) the pair.</summary>
    private static Vector3 Stock(Vector3 ambient, ReadOnlySpan<Point> lamps, Vector3 at, Vector2 pair)
    {
        Vector3 sum = ambient;
        foreach (Point l in lamps)
        {
            float f2 = Vector3.DistanceSquared(l.At, at) / (l.Reach * l.Reach);
            if (f2 >= 1f) continue;
            Vector3 c = l.Colour;
            sum += new Vector3(MathF.Pow(c.X, 2.2f), MathF.Pow(c.Y, 2.2f), MathF.Pow(c.Z, 2.2f)) * ((1f - f2) / (pair.X + pair.Y * f2));
        }
        return sum;
    }

    /// <summary>The air's light on the ray from <paramref name="eye"/> to <paramref name="surf"/> and how far it is over
    /// the brightest the room and the lamps lay where the air is, per channel (at most 0 when bounded).</summary>
    private static (Vector3 Added, float Keep, float Over) Lit(Field f, Vector3 eye, Vector3 surf, Shine room, Point[] lamps, bool mist, bool sky)
    {
        (float keep, Vector3 added) = f.March(eye, surf, room, lamps, pair: ShippedPair);
        Vector3 most = Vector3.Zero;
        Vector3 bed = sky ? room.Ambient + room.Diffuse : room.Ambient;
        for (float t = 0f; t <= 1f; t += 0.001f)
        {
            Vector3 p = Vector3.Lerp(eye, surf, t);
            (float d, float m) = f.At(p, surf.Z);
            if ((mist ? m : d) > 0f) most = Vector3.Max(most, Stock(bed, lamps, p, ShippedPair));
        }
        Vector3 over = added - (1f - keep) * most;
        return (added, keep, MathF.Max(over.X, MathF.Max(over.Y, over.Z)));
    }

    // Given a crypt with lamps of a given colour, one low by the floor
    // When the player looks through the dust beside it
    // Then no point of the dust is brighter than the ambient plus the light the lamps actually lay at that point, and the
    // lamp adds at least 0.5% of the cover the air lays, in the torch's hue.
    private static void LampWhereDustIs(Checker check) => LampLights(check, Crypted(Room()), CryptLight, false, false, "F4", 0f, BesideLamp);

    /// <summary>The least share of the air's cover a torch low by the floor adds beside it under the engine's falloff.</summary>
    private const float BesideLamp = 0.005f;

    /// <summary>A grazing ray 12 m along the floor centred on <paramref name="x"/>, a torch low by the floor near its far
    /// end and past the reach of where most of the air on it lies: is the air bounded by the light, by the sky's when
    /// <paramref name="sky"/> lights it, and lit by the torch, by <paramref name="share"/> of its cover at least?</summary>
    private static void LampLights(Checker check, Field f, Shine room, bool mist, bool sky, string label, float x, float share = 0.02f)
    {
        var eye = new Vector3(x - 6f, 0.3f, Eye);
        var surf = new Vector3(x + 6f, 0.3f, 0f);
        Point[] lamp = { new(new Vector3(x + 3.4f, 0.3f, 0.2f), 1.5f, Torch) };
        (Vector3 lit, float keep, float over) = Lit(f, eye, surf, room, lamp, mist, sky);
        (Vector3 dark, _, _) = Lit(f, eye, surf, room, Array.Empty<Point>(), mist, sky);
        Vector3 torch = lit - dark;
        check.Expect(over <= 1e-5f, $"{label} the air adds {lit.X:F4},{lit.Y:F4},{lit.Z:F4}, {over:F5} over what the room and the lamp lay where it is (want none over)");
        check.Expect(torch.X >= share * (1f - keep) && torch.X > torch.Y && torch.Y > torch.Z,
            $"{label} the lamp low by the floor adds {torch.X:F4},{torch.Y:F4},{torch.Z:F4} to the {1f - keep:P0} the air covers (want {share:P1} of it in red at least, in the torch's hue)");
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
    private static void MistLampWhereItIs(Checker check) => LampLights(check, Misted(), NightLight, true, true, "F5", 10f);

    // Given a crypt holding the module's stock fog placeable
    // When the eye looks along the floor across 10 m of the mist it lays
    // Then F1 holds through it: the floor beyond is still seen, the placeable's mist no denser to the eye than the night's.
    private static void PlaceableSeenThrough(Checker check)
    {
        Field f = Placed(Hall(), new Vector3(-20f, 0.5f, 0.5f));
        Vector3 eye = new(-25f, 0.5f, Eye);
        float ten = Keep(f, eye, new Vector3(-15f, 0.5f, 0f), CryptLight);
        float held = Column(f, -20f, 0.5f, 0f, true).Depth;
        check.Expect(held > 0f && ten >= 1f / MathF.E,
            $"G1 through {held:F3} m of the placeable's mist the eye keeps {ten:P0} of the floor 10 m off (want some mist, at least 37% kept)");
    }

    // Given an interior area whose fog colour and sun are bright, and mist a placeable lays in it
    // When the mist is drawn
    // Then no point of it is brighter than the room's ambient plus the lamps at that point: no sky lights a room, and a
    // lamp low by the floor adds at least 0.5% of the cover the mist lays, in the torch's hue.
    private static void PlaceableLitByTheRoom(Checker check)
        => LampLights(check, Placed(Room(), new Vector3(1f, 0.5f, 0.5f)), HallLight, true, false, "G2", 0f, BesideLamp);

    /// <summary>Along y = <paramref name="y"/> from <paramref name="x0"/> on, the night mist's depth falling from what it
    /// holds at <paramref name="x0"/>: the metres it takes from 90% of it to 10%, and its top where half of it is left
    /// as a share of its top at <paramref name="x0"/>, each top the height under which 95% of the column lies.</summary>
    private static (float Fade, float Top) Edge(Field f, float x0, float y)
    {
        float full = Column(f, x0, y, 0f, true).Depth, at90 = float.NaN, at10 = float.NaN, half = float.NaN;
        for (float x = x0; x <= x0 + 4f && float.IsNaN(at10); x += Pace / 5f)
        {
            float d = Column(f, x, y, 0f, true).Depth;
            if (float.IsNaN(at90) && d <= 0.9f * full) at90 = x;
            if (float.IsNaN(half) && d <= 0.5f * full) half = x;
            if (d <= 0.1f * full) at10 = x;
        }
        if (full <= 0f || float.IsNaN(at10)) return (0f, 1f);
        return (at10 - at90, Top(f, half, y) / Top(f, x0, y));
    }

    /// <summary>The height under which 95% of the night mist over (<paramref name="x"/>, <paramref name="y"/>) lies.</summary>
    private static float Top(Field f, float x, float y)
    {
        const float dz = 0.005f;
        float all = Column(f, x, y, 0f, true).Depth, sum = 0f;
        for (float z = dz / 2f; z < 2.5f; z += dz)
        {
            sum += f.At(new Vector3(x, y, z), 0f).Mist * dz;
            if (sum >= 0.95f * all) return z;
        }
        return 2.5f;
    }

    // Given the night mist over a channel whose far bank and a stone in it have no floor
    // When the eye looks across the mist
    // Then its edge and its top thin out over about a column: no wall stands at the bank or round the stone, no plateau
    // keeps its full height to a hard rim.
    private static void MistEdgeSoft(Checker check)
    {
        List<Tile> tiles = Ground(40f, 1f, (_, _) => 0f, (x, y) => x >= 6f || x >= 3f && x < 4f && y >= 3f && y < 4f ? Nonwalk : x < 0f ? Stone : MistFixture.Water);
        Enter(City, tiles, 0f, true);
        Go(Gather, Vector2.Zero);
        Field f = Upload();
        (float bank, float bankTop) = Edge(f, 3.5f, 0.5f);
        (float stone, float stoneTop) = Edge(f, 1.5f, 3.5f);
        check.Expect(bank >= 0.75f && stone >= 0.75f,
            $"G3 the mist thins from 90% to 10% over {bank:F2} m to the bank and {stone:F2} m to the stone (want 0.75 m at least)");
        check.Expect(bankTop <= 0.75f && stoneTop <= 0.75f,
            $"G3 where half the mist is left its top stands at {bankTop:P0} of its height off the bank and {stoneTop:P0} by the stone (want 75% at most)");
    }

    /// <summary>The highest fog amount the toolset sets an area.</summary>
    private const float MostFog = 15f;

    /// <summary>The frames a long settle runs: long enough for a pool to fill as far as it ever will.</summary>
    private const int LongSettled = 6000;

    // Given a crypt's 4 m passage whose area fog is set as high as the toolset sets it
    // When the eye looks along the floor across 10 m of its dust, gathered by the walls either side
    // Then F1 still holds: the fog's amount thickens the dust, never into a slab the floor 10 m off is lost behind.
    private static void DenseFogSeenThrough(Checker check)
    {
        Enter(AirFixture.Crypt, Ground(40f, 1f, (_, _) => 0f, (x, y) => MathF.Abs(x) >= 30f || MathF.Abs(y - 0.5f) >= 2f ? Nonwalk : Stone), MostFog, false);
        Go(3f, Vector2.Zero);
        Field f = Upload();
        float ten = Keep(f, new Vector3(-25f, 0.5f, Eye), new Vector3(-15f, 0.5f, 0f), CryptLight);
        check.Expect(ten >= 1f / MathF.E, $"F1 under fog {MostFog} the eye keeps {ten:P0} of the floor 10 m off (want at least 37%)");
    }

    // Given the stock fog placeable on the rim of a hollow, feeding it for over a minute and a half
    // When the eye looks down into the hollow 10 m off
    // Then the mist pooled there is no deeper to the eye than the night's lay, and the hollow's floor is still seen.
    private static void PlaceablePoolBounded(Checker check)
    {
        static float Hollow(float x, float y) => -0.5f * MathF.Max(0f, 1f - Vector2.Distance(new Vector2(x, y), new Vector2(-15f, 0.5f)) / 6f);
        List<Tile> tiles = Ground(40f, 1f, Hollow, (x, y) => MathF.Abs(x) >= 30f || MathF.Abs(y) >= 30f ? Nonwalk : Stone);
        Field f = Placed(tiles, new Vector3(-20f, 0.5f, Hollow(-20f, 0.5f) + 0.5f), LongSettled);
        float pool = Column(f, -15f, 0.5f, Hollow(-15f, 0.5f), true).Depth;
        float ten = Keep(f, new Vector3(-25f, 0.5f, Eye), new Vector3(-15f, 0.5f, Hollow(-15f, 0.5f)), CryptLight);
        check.Expect(pool > 0f && pool <= 0.31f && ten >= 1f / MathF.E,
            $"G1 the hollow holds {pool:F3} m of the placeable's mist and the eye keeps {ten:P0} of its floor 10 m off (want some mist, 0.31 m at most, at least 37% kept)");
    }

    /// <summary>A dark crypt floor's lit albedo, linear: worn stone.</summary>
    private const float FloorAlbedo = 0.2f;

    /// <summary>A crypt's green brazier as the engine hands its colour over, sRGB.</summary>
    private static readonly Vector3 Brazier = new(0.35f, 0.9f, 0.35f);

    /// <summary>Rec. 709 luma of a linear colour.</summary>
    private static float Luma(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    // Given a crypt floor that reads dark under its green braziers without the air, at fog amount 15
    // When the dust lies on it and the eye looks across it grazing
    // Then the floor seen through the dust reads no brighter than 1.6 times its own brightness, and no darker than half of it.
    private static void DarkCryptStaysDark(Checker check)
    {
        Enter(AirFixture.Crypt, Room(), MostFog, false);
        Go(3f, Vector2.Zero);
        (float best, float worst) = Grazed(Upload());
        check.Expect(worst <= 1.6f && best >= 0.5f,
            $"H3 through the dust the crypt floor reads {best:F2} to {worst:F2} times its brightness without the air (want 0.5 to 1.6)");
    }

    // Given the dark crypt the live client stands in: a dais 3 m up over a sunken ring, its fog amount 15, its rig of lamps
    // under the client's lighting settings,
    // the stock fog placeable in the ring and the dust off, the ring's floor showing a hundredth of the light that reaches it
    // When the eye at the dais edge looks down across the ring
    // Then the ring's floor in view reads to the eye on average no brighter than 1.5 times its brightness without the air.
    private static void MistyCryptStaysDark(Checker check)
    {
        float was = Air.Dust.V;
        Air.Dust.V = 0f;
        try
        {
            Field f = Placed(Ground(20f, 1f, Dais, (x, y) => Ring(x, y) >= 14f ? Nonwalk : Stone), new Vector3(9.5f, 3f, 0.5f), LongSettled, MostFog);
            float mist = Column(f, 9.5f, 0.5f, 0f, true).Depth, worst = 0f, sum = 0f;
            int n = 0;
            for (float x = 7.5f; x <= 10.5f; x += 0.5f)
                for (float y = -2f; y <= 3f; y += 1f)
                {
                    var surf = new Vector3(x, y, Dais(x, y));
                    (float keep, Vector3 added) = f.March(LiveEye, surf, LiveLight, LiveRig, pair: ShippedPair);
                    float bare = LiveFloorShare * Luma(Stock(LiveLight.Ambient, LiveRig, surf, Vector2.One));
                    float shown = Shown((bare * keep + Luma(added)) / bare);
                    worst = MathF.Max(worst, shown);
                    sum += shown;
                    n++;
                }
            check.Expect(mist > 0f && sum / n <= 1.5f,
                $"J2 through the placeable's mist ({mist:F3} m) the live crypt's ring floor reads on average {sum / n:F2} times its brightness without the air to the eye, at most {worst:F2} (want some mist, 1.5 at most on average)");
        }
        finally
        {
            Air.Dust.V = was;
        }
    }

    /// <summary>How far from the live crypt's centre (<paramref name="x"/>, <paramref name="y"/>) is, by its square plan, metres.</summary>
    private static float Ring(float x, float y) => MathF.Max(MathF.Abs(x), MathF.Abs(y));

    /// <summary>The live crypt's floor: a dais 3 m up within 3.5 m of the centre, a ramp down to the ring at 7.5 m, the ring
    /// at 0 out to 11 m and a 0.2 m step beyond it.</summary>
    private static float Dais(float x, float y)
    {
        float r = Ring(x, y);
        return r >= 11f ? 0.2f : 3f * Math.Clamp((7.5f - r) / 4f, 0f, 1f);
    }

    /// <summary>The live crypt's eye, at the dais edge, and its area ambient, as the client logged them.</summary>
    private static readonly Vector3 LiveEye = new(4.1f, 0f, 3f);
    private static readonly Shine LiveLight = new(new Vector3(0.058f, 0.024f, 0.024f), Vector3.Zero);

    /// <summary>The share the live crypt's ring floor shows, read off the client, of the light <see cref="Stock"/> lays on it
    /// over the fallback pair (1, 1), which the reading was taken against.</summary>
    private const float LiveFloorShare = 0.01f;

    /// <summary>The first twelve lamps of the live crypt's rig, about its centre, as the client logged them.</summary>
    private static readonly Point[] LiveRig =
    {
        new(new Vector3(-15f, -5f, 5f), 20f, new Vector3(0.72f, 0.87f, 0.56f)), new(new Vector3(-12.6f, -6.4f, 4f), 19f, new Vector3(1f, 0.895f, 0.032f)),
        new(new Vector3(-12.8f, 6.4f, 4f), 19f, new Vector3(1f, 0.895f, 0.032f)), new(new Vector3(-15f, 5f, 5f), 20f, new Vector3(0.72f, 0.87f, 0.56f)),
        new(new Vector3(-5f, 15f, 5f), 20f, new Vector3(0.72f, 0.87f, 0.56f)), new(new Vector3(1.8f, 14.1f, 4f), 18.5f, new Vector3(0.065f, 1f, 0.227f)),
        new(new Vector3(7.3f, 2.3f, 2.1f), 14f, new Vector3(0.72f, 0.87f, 0.56f)), new(new Vector3(-7.3f, 2.3f, 2.1f), 14f, new Vector3(0.72f, 0.87f, 0.56f)),
        new(new Vector3(-2.3f, 7.3f, 2.1f), 14f, new Vector3(0.72f, 0.87f, 0.56f)), new(new Vector3(-13.9f, 2f, 2.2f), 14f, new Vector3(0.72f, 0.87f, 0.56f)),
        new(new Vector3(7.3f, -2.3f, 2.1f), 14f, new Vector3(0.72f, 0.87f, 0.56f)), new(new Vector3(2.3f, -7.3f, 2.1f), 14f, new Vector3(0.72f, 0.87f, 0.56f)),
    };

    /// <summary>A linear brightness ratio as the eye sees it on screen: to the power 1 / 2.2.</summary>
    private static float Shown(float ratio) => MathF.Pow(ratio, 1f / 2.2f);

    /// <summary>The least and the most the floor of <paramref name="f"/> reads, seen grazing from 8 m back under two green
    /// braziers, as a share of its brightness without the air.</summary>
    private static (float Best, float Worst) Grazed(Field f)
    {
        Point[] lamps = { new(new Vector3(-2f, 2.5f, 1.5f), 6f, Brazier), new(new Vector3(4f, -2.5f, 1.5f), 6f, Brazier) };
        float worst = 1f, best = 1f;
        for (float x = -2f; x <= 6f; x += 1f)
        {
            var surf = new Vector3(x, 0.3f, 0f);
            (float keep, Vector3 added) = f.March(new Vector3(x - 8f, 0.3f, Eye), surf, CryptLight, lamps, pair: ShippedPair);
            float bare = Luma(FloorAlbedo * Stock(CryptLight.Ambient, lamps, surf, ShippedPair));
            float seen = bare * keep + Luma(added);
            worst = MathF.Max(worst, seen / bare);
            best = MathF.Min(best, seen / bare);
        }
        return (best, worst);
    }

    /// <summary>Seen side-on across 10 m of <paramref name="f"/> along y = 0.3 ending at x = <paramref name="x"/>: the
    /// height band over which the air goes from nine tenths of how much it hides at the floor to a tenth of it, metres.</summary>
    private static (float Band, float Floor) SideOn(Field f, float x, bool mist)
    {
        float Hides(float h)
        {
            (float d, float m) = (0f, 0f);
            for (float t = 0.025f; t < 10f; t += 0.05f) { (float a, float b) = f.At(new Vector3(x - t, 0.3f, h), h); d += a; m += b; }
            return 1f - MathF.Exp(-f.Sigma * 0.05f * (mist ? m : d));
        }
        float floor = Hides(0.01f), h90 = 0f, h10 = 0f;
        for (float h = 0.01f; h < 2.5f; h += 0.01f)
        {
            float o = Hides(h);
            if (o >= 0.9f * floor) h90 = h;
            if (o > 0.1f * floor) h10 = h + 0.01f;
        }
        return (floor > 0f ? h10 - h90 : 0f, floor);
    }

    // Given the stock fog placeable's mist in a crypt
    // When the player looks across it side-on
    // Then it fades out over at least 0.5 m below its top, rather than stopping at a level line.
    private static void SoftTop(Checker check)
    {
        (float band, float floor) = SideOn(Placed(Room(), new Vector3(0f, 0.3f, 0.5f), LongSettled), 5f, true);
        check.Expect(floor > 0f && band >= 0.5f,
            $"J5 side-on the placeable's mist fades out over {band:F2} m (hiding {floor:P0} at the floor) (want some, over 0.5 m at least)");
    }
}
