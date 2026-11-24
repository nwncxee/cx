using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>A crypt's stale air lies as dust on its floor. Oracle, by hand: heights chosen off a standing character (the
/// knee at half a metre), the room's walls and pool laid tile by tile, and the fog terms the volume builds.</summary>
internal static unsafe class AirDustApprovedExamples
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirDustApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        OnTheFloor(check);
        ByTheWalls(check);
        StillShifts(check);
        OverThePool(check);
        FogCostSame(check);
        AmountThicker(check);
        AmountZeroLies(check);
        AmountTornLess(check);
        AmountBackSooner(check);
        AmountZeroGust(check);
        FogOffLies(check);
        OldMistOff(check);
        UnderBalcony(check);
        log.Info(check.Ok ? "AirDustApprovedExamples.SelfCheck: passed" : "AirDustApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A crypt room at fog amount <paramref name="amount"/>, three still seconds after entry.</summary>
    private static void Settled(float amount)
    {
        Enter(Crypt, Room(), amount, false);
        Go(3f, Vector2.Zero);
    }

    /// <summary>The dust's density <paramref name="z"/> above the floor at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static float At(float x, float y, float z) => AirScene.Density(AirLayer.Dust, new Vector3(x, y, z));

    // Given a crypt (an indoor area on a crypt tileset)
    // When the player stands in a room
    // Then dust lies on the floor, thickest on the floor itself and gone by a standing character's knee.
    private static void OnTheFloor(Checker check)
    {
        Settled(0f);
        float floor = At(0f, 0f, 0.02f), shin = At(0f, 0f, 0.2f), knee = At(0f, 0f, Knee);
        check.Expect(floor > 0f && floor > shin && knee == 0f, $"E45 the dust reads {floor:F4} on the floor, {shin:F4} at the shin, {knee:F4} at the knee (want thickest low, gone by the knee)");
    }

    // Given the same room
    // When the player looks across it
    // Then the dust is thicker in the corners and along the walls than in the middle of the room.
    private static void ByTheWalls(Checker check)
    {
        Settled(0f);
        float middle = Near(AirLayer.Dust, 0f, 0f, 1f), wall = Near(AirLayer.Dust, 0f, RoomHalf - 1f, 0.9f), corner = Near(AirLayer.Dust, RoomHalf - 1f, RoomHalf - 1f, 0.9f);
        check.Expect(wall > middle && corner > middle, $"E46 the dust lies {corner:F4} m in the corner, {wall:F4} m by the wall, {middle:F4} m in the middle (want both thicker than the middle)");
    }

    /// <summary>Samples of the dust's depth over the room, 0.5 m apart.</summary>
    private static float[,] Snapshot()
    {
        var s = new float[24, 24];
        for (int i = 0; i < 24; i++)
            for (int j = 0; j < 24; j++) s[i, j] = Column(AirLayer.Dust, -6f + i * 0.5f, -6f + j * 0.5f);
        return s;
    }

    // Given a crypt with no wind and no one moving
    // When ten seconds pass
    // Then the dust still shifts slowly over the floor.
    private static void StillShifts(Checker check)
    {
        Settled(0f);
        float[,] before = Snapshot();
        Go(10f, Vector2.Zero);
        float[,] after = Snapshot();
        float moved = 0f, mass = 0f;
        for (int i = 0; i < 24; i++)
            for (int j = 0; j < 24; j++) { moved += MathF.Abs(after[i, j] - before[i, j]); mass += before[i, j]; }
        check.Expect(mass > 0f && moved > 0.02f * mass && moved < mass, $"E50 ten still seconds move {moved:F3} of {mass:F3} m of dust (want some, slowly)");
    }

    // Given a crypt room with a pool of standing water
    // When the dust settles
    // Then it lies over the water as it lies over the floor.
    private static void OverThePool(Checker check)
    {
        var room = Ground(20f, 1f, (_, _) => 0f, (x, y) => MathF.Abs(x) >= RoomHalf || MathF.Abs(y) >= RoomHalf ? Nonwalk : MathF.Abs(x - 4f) < 2f && MathF.Abs(y) < 2f ? MistFixture.Water : Stone);
        Enter(Crypt, room, 0f, false);
        Go(3f, Vector2.Zero);
        float pool = Near(AirLayer.Dust, 4f, 0f, 1.5f), floor = Near(AirLayer.Dust, -4f, 0f, 1.5f);
        check.Expect(floor > 0f && MathF.Abs(pool - floor) <= 0.1f * floor, $"E51 the dust lies {pool:F4} m over the pool, {floor:F4} m over the floor as far from the walls (want the same)");
    }

    // Given Volumetric fog on in the docks
    // When the player switches the air off and on
    // Then the fog volume costs the same either way.
    private static void FogCostSame(Checker check)
    {
        Enter(City, Quay(), 3f, true);
        Go(2f, Vector2.Zero);
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, -12f, 8f), Vector3.Zero, Vector3.UnitZ);
        Matrix4x4 vp = view * Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.5f, 200f);
        int* viewport = stackalloc int[] { 0, 0, 1280, 720 };
        Air.Enabled.V = true;
        bool builtOn = FogVolume.Prepare(view, vp, viewport, 200f, true, true, 0f, out FogVolume.Terms with);
        Air.Enabled.V = false;
        bool builtOff = FogVolume.Prepare(view, vp, viewport, 200f, true, true, 0f, out FogVolume.Terms without);
        bool drawnOff = Drawn();
        Air.Enabled.V = true;
        check.Expect(builtOn && builtOff, "E85 Given: the fog volume builds for the camera");
        check.Expect(!drawnOff, "E85 with the air off the fog stage draws no air");
        check.Expect(with.M == without.M && with.Height == without.Height && with.Noise == without.Noise && with.Phase == without.Phase,
            "E85 the fog volume's grid, steps and terms are the same with the air off");
    }

    // Given two crypts alike but for their fog amount, 0 and 10
    // When the player stands in each
    // Then more dust lies in the one with fog amount 10.
    private static void AmountThicker(Checker check)
    {
        Settled(0f);
        float none = Near(AirLayer.Dust, 0f, 0f, 4f);
        Settled(10f);
        float ten = Near(AirLayer.Dust, 0f, 0f, 4f);
        check.Expect(ten > none, $"E87 the crypt at fog amount 10 lays {ten:F4} m against {none:F4} m at 0 (want more)");
    }

    // Given a crypt whose fog amount is 0, the toolset default
    // When the player enters it
    // Then dust lies on its floor as in E45.
    private static void AmountZeroLies(Checker check)
    {
        Settled(0f);
        check.Expect(At(0f, 0f, 0.02f) > 0f && At(0f, 0f, Knee) == 0f, $"E88 at fog amount 0 the dust reads {At(0f, 0f, 0.02f):F4} on the floor, {At(0f, 0f, Knee):F4} at the knee (want some, none)");
    }

    /// <summary>A crypt at fog amount <paramref name="amount"/> through a two-second strong gust: the share of its dust
    /// that stays, and the seconds of calm it takes to fill back to 90% of what lay.</summary>
    private static (float Kept, float Back) Gust(float amount)
    {
        Settled(amount);
        float lay = Near(AirLayer.Dust, 0f, 0f, 4f);
        Go(2f, Strong);
        float kept = Near(AirLayer.Dust, 0f, 0f, 4f);
        float back = 0f;
        while (back < 120f && Near(AirLayer.Dust, 0f, 0f, 4f) < 0.9f * lay) { Go(0.25f, Vector2.Zero); back += 0.25f; }
        return (lay > 0f ? kept / lay : 0f, back);
    }

    // Given the same two crypts
    // When the same gust crosses each
    // Then less dust is torn away in the one with fog amount 10.
    private static void AmountTornLess(Checker check)
    {
        (float kept0, _) = Gust(0f);
        (float kept10, _) = Gust(10f);
        check.Expect(kept10 > kept0, $"E89 the gust leaves {kept10:P0} of the dust at fog amount 10, {kept0:P0} at 0 (want more kept at 10)");
    }

    // Given the same two crypts after the gust
    // When the air is calm again
    // Then the dust fills back sooner in the one with fog amount 10.
    private static void AmountBackSooner(Checker check)
    {
        (_, float back0) = Gust(0f);
        (_, float back10) = Gust(10f);
        check.Expect(back10 < back0, $"E90 the dust fills back in {back10:F1} s at fog amount 10, {back0:F1} s at 0 (want sooner at 10)");
    }

    // Given a crypt whose fog amount is 0
    // When a gust crosses it and the air turns calm
    // Then the dust is torn and refills as E31 to E39 say.
    private static void AmountZeroGust(Checker check)
    {
        (float kept, float back) = Gust(0f);
        check.Expect(kept < 0.8f && back < 60f, $"E91 at fog amount 0 the gust keeps {kept:P0} and the dust is back in {back:F1} s (want torn, back within a minute)");
    }

    // Given Volumetric fog switched off in the player's settings
    // When the player enters a crypt
    // Then dust lies on its floor.
    private static void FogOffLies(Checker check)
    {
        VolumetricFog.Enabled.V = false;
        Settled(0f);
        float lies = Near(AirLayer.Dust, 0f, 0f, 4f);
        VolumetricFog.Enabled.V = true;
        check.Expect(lies > 0f, $"E92 with Volumetric fog off {lies:F4} m of dust lies (want some)");
    }

    // Given a cx.cfg holding vfog.mist=0 from an older version
    // When the client starts
    // Then the Air switch is off.
    private static void OldMistOff(Checker check)
    {
        string? mist = Settings.Raw(RetiredMist), air = Settings.Raw(Air.Enabled.Key);
        Settings.Forget(Air.Enabled.Key);
        Settings.Read($"{RetiredMist}=0\n");
        string? started = Settings.Raw(Air.Enabled.Key);
        Settings.Forget(RetiredMist);
        Settings.Forget(Air.Enabled.Key);
        if (mist != null) Settings.Read($"{RetiredMist}={mist}\n");
        if (air != null) Settings.Read($"{Air.Enabled.Key}={air}\n");
        check.Expect(started != null && Air.Enabled.TryParse(started, out bool on) && !on, $"E120 the Air switch starts from {started ?? "nothing"} (want off)");
    }

    // Given a crypt room under a balcony
    // When the player stands below
    // Then dust lies on the player's floor and not on the balcony.
    private static void UnderBalcony(Checker check)
    {
        var room = Room();
        Deck(room, -4f, -4f, 4f, 4f, 3f, Stone);
        Enter(Crypt, room, 0f, false);
        Go(3f, Vector2.Zero);
        float floor = At(0f, 0f, 0.02f), balcony = At(0f, 0f, 3.02f);
        check.Expect(floor > 0f && balcony == 0f, $"E122 the dust reads {floor:F4} on the player's floor and {balcony:F4} on the balcony (want some, none)");
    }
}
