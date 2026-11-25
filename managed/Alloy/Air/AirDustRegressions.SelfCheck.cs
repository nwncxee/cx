using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The crypt dust's plain regressions: a passer's gap closes, Low and a teleport part nothing, a new area lies
/// whole and sends no front, the air off shows none, the upload carries each column's dust and a new area's anew. Oracle, by hand: a still crypt room's dust.</summary>
internal static class AirDustRegressions
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirDustRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        GapCloses(check);
        LowParts(check);
        OffParts(check);
        TeleportParts(check);
        LowStirs(check);
        EntersWhole(check);
        OffShows(check);
        Uploads(check);
        EntersCalm(check);
        EntersAgain(check);
        FogOffLays(check);
        FileKeeps(check);
        FramesOnce(check);
        NewAreaDrops(check);
        LiftsLess(check);
        RefillsFaster(check);
        SettleHolds(check);
        GapEdge(check);
        log.Info(check.Ok ? "AirDustRegressions.SelfCheck: passed" : "AirDustRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A still crypt room, two seconds calm.</summary>
    private static void Still()
    {
        Enter(Crypt, Room(), 0f, false);
        Go(2f, Vector2.Zero);
    }

    /// <summary>A creature, not the player, walking from x = -3 along y = 0 for <paramref name="metres"/> in
    /// <paramref name="frames"/> frames: the dust beside its path less the dust on it, then and ten seconds on.</summary>
    private static (float Gap, float Left) Pass(float metres, int frames)
    {
        DbId world = MistPartingApprovedExamples.World(), who = MistPartingApprovedExamples.Seat(world, new Vector3(-3f, 0f, 0f), true);
        AirFixture.Still(world, Frames(0.1f));
        Walk(world, who, new Vector3(-3f, 0f, 0f), new Vector3(-3f + metres, 0f, 0f), false, frames);
        float gap = Beside(AirLayer.Dust, -3f, 0f) - AirFixture.Path(AirLayer.Dust, -3f, 0f);
        AirFixture.Still(world, Frames(10f));
        float left = Beside(AirLayer.Dust, -3f, 0f) - AirFixture.Path(AirLayer.Dust, -3f, 0f);
        MistPartingApprovedExamples.Drop(world, who);
        return (gap, left);
    }

    // Given crypt dust
    // When a creature walks 5 m through it
    // Then it leaves a gap that closes within ten seconds.
    private static void GapCloses(Checker check)
    {
        Still();
        (float gap, float left) = Pass(5f, Pace(5f));
        check.Expect(gap > 0.02f && left < 0.25f * gap, $"a passer leaves a {gap:F4} m gap, {left:F4} m ten seconds on (want a gap that closes)");
    }

    // Given crypt dust at Low
    // When a creature walks 5 m through it
    // Then the dust on its path lies as it did.
    private static void LowParts(Checker check)
    {
        Air.Quality.V = "low";
        Still();
        float before = AirFixture.Path(AirLayer.Dust, -3f, 0f);
        Pass(5f, Pace(5f));
        float after = AirFixture.Path(AirLayer.Dust, -3f, 0f);
        Air.Quality.V = "medium";
        check.Expect(before > 0f && after == before, $"at Low the path keeps {after:F4} m of {before:F4} m (want all of it)");
    }

    // Given crypt dust and the air off
    // When a creature walks 5 m through it and the air comes back
    // Then the dust on its path lies as it did.
    private static void OffParts(Checker check)
    {
        Still();
        float before = AirFixture.Path(AirLayer.Dust, -3f, 0f);
        Air.Enabled.V = false;
        Pass(5f, Pace(5f));
        Air.Enabled.V = true;
        float after = AirFixture.Path(AirLayer.Dust, -3f, 0f);
        check.Expect(before > 0f && after == before, $"with the air off the path keeps {after:F4} m of {before:F4} m (want all of it)");
    }

    // Given crypt dust
    // When a creature jumps 5 m in one frame
    // Then the dust where it lands lies as it did.
    private static void TeleportParts(Checker check)
    {
        Still();
        float before = Near(AirLayer.Dust, 2f, 0f, 1f);
        DbId world = MistPartingApprovedExamples.World(), who = MistPartingApprovedExamples.Seat(world, new Vector3(-3f, 0f, 0f), true);
        AirFixture.Still(world, 1);
        MistPartingApprovedExamples.Place(who, new Vector3(2f, 0f, 0f));
        DbAnim.Animate(world, Frame);
        float after = Near(AirLayer.Dust, 2f, 0f, 1f);
        MistPartingApprovedExamples.Drop(world, who);
        check.Expect(before > 0f && MathF.Abs(after - before) <= 0.01f * before, $"a 5 m jump leaves {after:F4} m of {before:F4} m where it lands (want all of it)");
    }

    // Given crypt dust at Low
    // When ten still seconds pass
    // Then not a column of it moves.
    private static void LowStirs(Checker check)
    {
        Air.Quality.V = "low";
        Still();
        float a = Column(AirLayer.Dust, 0.5f, 0.5f) + Column(AirLayer.Dust, -2.5f, 3.5f);
        Go(10f, Vector2.Zero);
        float b = Column(AirLayer.Dust, 0.5f, 0.5f) + Column(AirLayer.Dust, -2.5f, 3.5f);
        Air.Quality.V = "medium";
        check.Expect(a > 0f && a == b, $"at Low ten seconds leave {b:F5} m where {a:F5} m lay (want the same)");
    }

    // Given a crypt whose wind is already strong
    // When the player enters it and the air steps once
    // Then its dust lies whole, as in a calm crypt.
    private static void EntersWhole(Checker check)
    {
        Still();
        float calm = Near(AirLayer.Dust, 0f, 0f, 4f);
        Enter(Crypt, Room(), 0f, false);
        AirScene.Step(Frame, Strong);
        float entered = Near(AirLayer.Dust, 0f, 0f, 4f);
        check.Expect(calm > 0f && entered >= 0.95f * calm, $"the first step under a strong wind leaves {entered:F4} m against {calm:F4} m calm (want whole)");
    }

    // Given crypt dust
    // When the player turns the air off
    // Then no dust reads anywhere and the air says none lies.
    private static void OffShows(Checker check)
    {
        Still();
        bool lay = AirScene.Lies;
        Air.Enabled.V = false;
        float off = Near(AirLayer.Dust, 0f, 0f, 6f);
        bool lies = AirScene.Lies;
        Air.Enabled.V = true;
        check.Expect(lay && off == 0f && !lies, $"with the air off {off:F4} m reads and it lies {lies} (want none, false; it lay {lay})");
    }

    // Given crypt dust
    // When the fog stage takes its upload
    // Then the player's column carries its dust and the height it stands to, and a wall's carries none.
    private static void Uploads(Checker check)
    {
        Still();
        var into = new byte[GroundMist.PackedBytes];
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, into);
        Vector4 head = MemoryMarshal.Read<Vector4>(into);
        Span<GroundMist.Cell> cells = MemoryMarshal.Cast<byte, GroundMist.Cell>(into.AsSpan(GroundMist.Head));
        int ox = (int)head.X, oy = (int)head.Y;
        GroundMist.Cell feet = cells[UploadAt(0, 0, ox, oy)], wall = cells[UploadAt((int)RoomHalf + 1, 0, ox, oy)];
        (float depth, float top) = AirScene.Shown(0.5f, 0.5f);
        int again = GroundMist.Pack(ref gen, into);
        Go(Frame, Vector2.Zero);
        int next = GroundMist.Pack(ref gen, into);
        check.Expect(head.W == 1f && feet.Dust == depth && depth > 0f && feet.Top == top && top > 0f && wall.Dust == 0f,
            $"the upload carries {feet.Dust:F4} m to {feet.Top:F2} m at the feet against {depth:F4} m to {top:F2} m, {wall.Dust:F4} m on the wall (want the air's, none)");
        check.Expect(again == 0 && next == GroundMist.PackedBytes, $"an unchanged air sends {again} bytes, a stepped one {next} (want 0, the whole field)");
    }

    /// <summary>The upload's column holding world metre (<paramref name="x"/>, <paramref name="y"/>) under corner (<paramref name="ox"/>, <paramref name="oy"/>).</summary>
    private static int UploadAt(int x, int y, int ox, int oy) => (y - oy) * GroundMist.Side + (x - ox);

    // Given a crypt whose wind is already strong
    // When the player enters it and the air steps once
    // Then no front starts for a change nobody saw.
    private static void EntersCalm(Checker check)
    {
        Enter(Crypt, Room(), 0f, false);
        AirScene.Step(Frame, Strong);
        check.Expect(!AirScene.Front.Live, "entering under a strong wind starts a front (want none)");
    }

    // Given a crypt whose dust the fog stage has taken, then the air reset
    // When the player enters a second area over the same field
    // Then the fog stage takes that area's dust anew.
    private static void EntersAgain(Checker check)
    {
        AirScene.Reset();
        Enter(Crypt, Room(), 0f, false);
        var into = new byte[GroundMist.PackedBytes];
        int gen = int.MinValue;
        int first = GroundMist.Pack(ref gen, into);
        AirScene.Reset();
        AirScene.Enter(Crypt, false);
        int again = GroundMist.Pack(ref gen, into);
        check.Expect(first == GroundMist.PackedBytes && again == GroundMist.PackedBytes,
            $"the first area sends {first} bytes, the second {again} (want the whole field both times)");
    }

    // Given a crypt whose area sets fog amount 10 with its fog off
    // When the player enters it
    // Then its dust lies as at fog amount 0.
    private static void FogOffLays(Checker check)
    {
        Enter(Crypt, Room(), 0f, false);
        (float plain, _) = AirScene.Shown(0.5f, 0.5f);
        Lay(Room(), Vector3.Zero, false, 10f);
        AirScene.Enter(Crypt, false);
        (float off, _) = AirScene.Shown(0.5f, 0.5f);
        check.Expect(plain > 0f && off == plain, $"fog amount 10 with the fog off lays {off:F4} m against {plain:F4} m at 0 (want the same)");
    }

    // Given a cx.cfg holding vfog.mist=0 and air.enabled=1
    // When CX reads it
    // Then the Air switch keeps the file's own 1.
    private static void FileKeeps(Checker check)
    {
        string? mist = Settings.Raw(RetiredMist), air = Settings.Raw(Air.Enabled.Key);
        Settings.Forget(Air.Enabled.Key);
        Settings.Read($"{RetiredMist}=0\n{Air.Enabled.Key}=1\n");
        string? kept = Settings.Raw(Air.Enabled.Key);
        Settings.Forget(RetiredMist);
        Settings.Forget(Air.Enabled.Key);
        if (mist != null) Settings.Read($"{RetiredMist}={mist}\n");
        if (air != null) Settings.Read($"{Air.Enabled.Key}={air}\n");
        check.Expect(kept == "1", $"a file setting both keeps the Air switch at {kept ?? "nothing"} (want 1)");
    }

    // Given a laid room the live air has framed
    // When the renderer frames it twice for one frame, then once for the next
    // Then the air steps once per frame.
    private static void FramesOnce(Checker check)
    {
        var scene = new DbId(0x7a1u);
        Lay(Room(), Vector3.Zero, true, 0f);
        AirScene.Frame(scene, 1, true, false, Frame, Vector2.Zero);
        int one = AirScene.Gen;
        AirScene.Frame(scene, 1, true, false, Frame, Vector2.Zero);
        int same = AirScene.Gen;
        AirScene.Frame(scene, 2, true, false, Frame, Vector2.Zero);
        int next = AirScene.Gen;
        check.Expect(same == one && next == one + 1, $"the air is at {one}, {same} after the same frame again, {next} after the next (want {one}, {one}, {one + 1})");
    }

    // Given crypt dust lying in one area
    // When the renderer frames a different scene
    // Then that dust is gone.
    private static void NewAreaDrops(Checker check)
    {
        Still();
        bool was = AirScene.Lies;
        AirScene.Frame(new DbId(0x7a2u), 1, false, false, Frame, Vector2.Zero);
        AirScene.Frame(new DbId(0x7a2u), 2, false, false, Frame, Vector2.Zero);
        check.Expect(was && !AirScene.Lies && AirScene.Shown(0.5f, 0.5f).Depth == 0f,
            $"the last area's dust lay {was}; framing a new scene it lies {AirScene.Lies} (want true, then none)");
    }

    /// <summary>A crypt at fog amount <paramref name="amount"/> through a three-second gust: the highest the feet's dust
    /// stands, and what is left of the dust the gust took ten seconds after the calm after it.</summary>
    private static (float Top, float Left) Gust(float amount)
    {
        Enter(Crypt, Room(), amount, false);
        Go(2f, Vector2.Zero);
        float rest = Near(AirLayer.Dust, 0f, 0f, 4f), top = 0f;
        for (int n = (int)MathF.Round(3f / Frame); n > 0; n--) { AirScene.Step(Frame, Strong); top = MathF.Max(top, AirScene.Shown(0.5f, 0.5f).Top); }
        Go(3f, Vector2.Zero);
        float taken = rest - Near(AirLayer.Dust, 0f, 0f, 4f);
        Go(10f, Vector2.Zero);
        return (top, taken > 0f ? (rest - Near(AirLayer.Dust, 0f, 0f, 4f)) / taken : 1f);
    }

    // Given crypt dust at fog amount 0 and at fog amount 10
    // When the same gust crosses each
    // Then the dust at the feet rises less high at 10.
    private static void LiftsLess(Checker check)
    {
        float none = Gust(0f).Top, ten = Gust(10f).Top;
        check.Expect(ten < none && ten > 0f, $"a gust lifts the dust to {ten:F3} m at fog amount 10 against {none:F3} m at 0 (want lower)");
    }

    // Given crypt dust at fog amount 0 and at fog amount 10, each torn by the same gust
    // When ten calm seconds pass
    // Then less of what the gust took is still missing at 10.
    private static void RefillsFaster(Checker check)
    {
        float none = Gust(0f).Left, ten = Gust(10f).Left;
        check.Expect(ten < 0.8f * none, $"ten calm seconds leave {ten:P0} of the torn dust missing at fog amount 10 against {none:P0} at 0 (want less)");
    }

    // Given a crypt at fog amount 10 whose air comes back on under a steady wind
    // When a minute passes in that wind
    // Then its dust lies where it lay when the air came back.
    private static void SettleHolds(Checker check)
    {
        Enter(Crypt, Room(), 10f, false);
        Go(1f, Strong);
        Air.Enabled.V = false;
        AirScene.Step(Frame, Strong);
        Air.Enabled.V = true;
        AirScene.Step(Frame, Strong);
        float back = Near(AirLayer.Dust, 0f, 0f, 4f);
        Go(60f, Strong);
        float later = Near(AirLayer.Dust, 0f, 0f, 4f);
        check.Expect(back > 0f && MathF.Abs(later - back) <= 0.05f * back, $"a minute of the same wind takes the dust from {back:F4} m to {later:F4} m (want where it lay)");
    }

    // Given crypt dust
    // When a creature walks along a line 0.3 m inside one row of columns
    // Then the row it walks in parts more than the row beside it.
    private static void GapEdge(Checker check)
    {
        Still();
        float inBefore = AirScene.Shown(0.5f, 0.5f).Depth, byBefore = AirScene.Shown(0.5f, -0.5f).Depth;
        DbId world = MistPartingApprovedExamples.World(), who = MistPartingApprovedExamples.Seat(world, new Vector3(-3f, 0.3f, 0f), true);
        AirFixture.Still(world, 1);
        Walk(world, who, new Vector3(-3f, 0.3f, 0f), new Vector3(2f, 0.3f, 0f), false, Pace(5f));
        float inside = AirScene.Shown(0.5f, 0.5f).Depth / inBefore, beside = AirScene.Shown(0.5f, -0.5f).Depth / byBefore;
        MistPartingApprovedExamples.Drop(world, who);
        check.Expect(inside < 0.5f && beside > inside + 0.1f, $"the walker's row keeps {inside:P0} of its dust, the row 0.3 m off {beside:P0} (want less where it walks)");
    }
}
