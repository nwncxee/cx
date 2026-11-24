using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>A creature walking through mist parts it, and it rolls back. Each walker is a creature object in a world scene,
/// moved a frame at a time and stepped by <see cref="DbAnim.Animate"/>. Oracle, by hand: the floor area fog 10 lays, read before
/// the walk, against the mist along the path and a metre and a half beside it.</summary>
internal static class MistPartingApprovedExamples
{
    /// <summary>A walk's pace, in metres a second.</summary>
    private const float Pace = 1.4f;

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MistPartingApprovedExamples.SelfCheck");
        bool on = VolumetricFog.Enabled.V;
        string quality = VolumetricFog.Quality.V;
        try
        {
            VolumetricFog.Quality.V = "medium";
            VolumetricFog.Enabled.V = true;
            PlayerPath(check);
            Creature(check, 10f, true, "E3");
            Creature(check, -10f, false, "E4");
            FarCreature(check);
        }
        finally
        {
            VolumetricFog.Enabled.V = on;
            VolumetricFog.Quality.V = quality;
            GroundMist.Reset();
        }
        log.Info(check.Ok ? "MistPartingApprovedExamples.SelfCheck: passed" : "MistPartingApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The world scene the walkers stand in, stepped by the scene animate the frame runs.</summary>
    private static DbId World()
    {
        DbId scene = SceneDb.Scenes.Alloc();
        SceneDb.SceneIsWorld[scene.Index] = true;
        SceneDb.SceneObjects[scene.Index]?.Clear();
        return scene;
    }

    /// <summary>A creature object at <paramref name="at"/>, a member of <paramref name="scene"/>, wind-enabled when
    /// <paramref name="wind"/> is set.</summary>
    private static DbId Seat(DbId scene, Vector3 at, bool wind)
    {
        DbId obj = SceneDb.Objects.Alloc();
        int o = obj.Index;
        DbFeed.InitObject(o);
        SceneDb.ObjFlags[o] = ObjFlag.Creature;
        SceneDb.ObjSeamFlags[o] = wind ? (uint)SeamFlag.Wind : 0u;
        Place(obj, at);
        DbScene.Join(scene, obj);
        return obj;
    }

    /// <summary>Stand <paramref name="obj"/> at <paramref name="p"/>, as the game's position write leaves it.</summary>
    private static void Place(DbId obj, Vector3 p)
    {
        SceneDb.ObjPos[obj.Index] = p;
        SceneDb.ObjWorld[obj.Index] = Matrix4x4.CreateTranslation(p);
    }

    /// <summary>One frame: the scene's animate step, then the field's.</summary>
    private static void Tick(DbId scene)
    {
        DbAnim.Animate(scene, Frame);
        GroundMist.Step(Frame, Vector2.Zero);
    }

    /// <summary>Walk <paramref name="obj"/> from <paramref name="from"/> to <paramref name="to"/> at <see cref="Pace"/>,
    /// the field following it when it is the player.</summary>
    private static void Walk(DbId scene, DbId obj, Vector3 from, Vector3 to, bool player)
    {
        int frames = (int)MathF.Ceiling(Vector3.Distance(from, to) / Pace / Frame);
        for (int n = 0; n <= frames; n++)
        {
            Vector3 p = Vector3.Lerp(from, to, (float)n / frames);
            Place(obj, p);
            if (player) GroundMist.Follow(p);
            Tick(scene);
        }
    }

    /// <summary>Hold the scene still for <paramref name="seconds"/>, its animate step still running.</summary>
    private static void Hold(DbId scene, float seconds)
    {
        for (int n = (int)(seconds / Frame); n > 0; n--) Tick(scene);
    }

    /// <summary>Take <paramref name="obj"/> out of <paramref name="scene"/> and free both.</summary>
    private static void Drop(DbId scene, DbId obj)
    {
        DbScene.Leave(scene, obj);
        SceneDb.Objects.Free(obj);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>The mean depth along a 5 m path at <paramref name="y"/> from <paramref name="x"/>, its ends left out.</summary>
    private static float Path(float x, float y) => Mean(x + 1f, y - 0.5f, x + 4f, y + 0.5f);

    /// <summary>The mean depth a metre and a half beside that path.</summary>
    private static float Beside(float x, float y) => Mean(x + 1f, y + 1.5f, x + 4f, y + 2.5f);

    private static void PlayerPath(Checker check)
    {
        Fogged(Flat(), Vector3.Zero);
        Run(1f, Vector2.Zero);
        DbId world = World(), me = Seat(world, Vector3.Zero, true);
        Hold(world, 0.1f);
        float before = Beside(0f, 0f);

        // Given the player stands in ground mist
        // When the player walks 5 m through it
        // Then the mist is thinner along the path just walked than beside it, and thicker beside it than before the walk
        Walk(world, me, Vector3.Zero, new Vector3(5f, 0f, 0f), true);
        float path = Path(0f, 0f), beside = Beside(0f, 0f);
        check.Expect(path < beside, $"E1 after a 5 m walk the path holds {path:F4} m, beside it {beside:F4} (want the path thinner)");
        check.Expect(beside > before, $"E1 beside the path {beside:F4} m after the walk, {before:F4} before (want thicker after)");

        // Given that clear path
        // When ten seconds pass with nothing moving
        // Then the mist along the path is back to the density beside it
        Hold(world, 10f);
        float healed = Path(0f, 0f), side = Beside(0f, 0f);
        check.Expect(path < beside && MathF.Abs(healed - side) <= side * 0.05f,
            $"E2 10 s after a clear path ({path:F4} against {beside:F4} m) it holds {healed:F4} m, beside it {side:F4} (want within 5%)");
        Drop(world, me);
    }

    private static void Creature(Checker check, float y, bool wind, string e)
    {
        // Given a creature walks through mist 10 m from the player
        // When it moves
        // Then it clears a path the same way the player does

        // Given a creature whose model is not wind-enabled walks through mist near the player
        // When it moves
        // Then it clears a path the same way
        Fogged(Flat(), Vector3.Zero);
        Run(1f, Vector2.Zero);
        DbId world = World(), it = Seat(world, new Vector3(0f, y, 0f), wind);
        Hold(world, 0.1f);
        float before = Beside(0f, y);
        Walk(world, it, new Vector3(0f, y, 0f), new Vector3(5f, y, 0f), false);
        float path = Path(0f, y), beside = Beside(0f, y);
        check.Expect(path < beside && beside > before,
            $"{e} a creature (wind-enabled {wind}) walking 5 m at y {y} m leaves {path:F4} m on its path, {beside:F4} beside, {before:F4} beside before (want the path thinner, beside thicker)");
        Drop(world, it);
    }

    private static void FarCreature(Checker check)
    {
        // Given a creature walks through mist 40 m from the player, outside the air box
        // When it moves
        // Then the mist there is unchanged
        Fogged(Flat(), Vector3.Zero);
        Run(1f, Vector2.Zero);
        DbId world = World(), it = Seat(world, new Vector3(40f, 0f, 0f), true);
        Hold(world, 0.1f);
        float before = Path(40f, 0f);
        Walk(world, it, new Vector3(40f, 0f, 0f), new Vector3(45f, 0f, 0f), false);
        float after = Path(40f, 0f);
        check.Expect(before > 0f && MathF.Abs(after - before) <= before * 1e-4f,
            $"E5 a creature 40 m off leaves {after:F5} m on its path, {before:F5} before (want mist there, unchanged)");
        Drop(world, it);
    }
}
