using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The view gate as a player meets it (approved-spec, E1 to E11). Oracle: the fixture's wave track moves its middle part 2 m per
/// second, so a part's position is the animation's clock in a hand-derived literal; the camera at the origin looks
/// along +Y, so which side of the frustum a sphere sits on is hand-derived too.</summary>
public static unsafe partial class DbAnim
{
    private static readonly Vector4 InView = new(0f, 50f, 0f, 1f), BehindCamera = new(0f, -100f, 0f, 1f);

    private static void ViewGate(Checker check, ExtenderLog log)
    {
        DbId world = SceneDb.Scenes.Alloc(), panel = SceneDb.Scenes.Alloc();
        SceneDb.SceneIsWorld[world.Index] = true;
        SceneDb.SceneIsWorld[panel.Index] = false;
        SceneDb.SceneAnimOn[world.Index] = true;
        SceneDb.SceneAnimOn[panel.Index] = true;
        try
        {
            ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, default, hasSun: false, 0f);
            ClockPausesOutOfView(check, world);
            ClockRunsInView(check, world);
            UnseenEventsAreSilent(check, log, world);
            PositionStaysTrue(check, world);
            EffectsKeepPlaying(check, world);
            WornItemsFollowTheBody(check, world);
            ShadowsKeepMoving(check, world);
            LampsKeepFlickering(check, world);
            PanelsAlwaysAnimate(check, panel);
            NoRestPoseOnFirstLook(check, world);
            ClicksStillLand(check, world);
            LongDawnShadow(check, world);
            LampShadowsKeepMoving(check, world);
            ExternalAnimateSteps(check, world);
            HoldForgetsTheRateBand(check, world);
            HighSunShadowStops(check, world);
            WorldViewDrivesTheGate(check, world);
        }
        finally
        {
            _view.On = false;
            for (int o = 1; o < SceneDb.Objects.High; o++)
                if (SceneDb.Objects.LiveAt(o) && (SceneDb.ObjScene[o] == world || SceneDb.ObjScene[o] == panel))
                    Drop(SceneDb.Objects.IdAt(o));
            SceneDb.Scenes.Free(world);
            SceneDb.Scenes.Free(panel);
        }
    }

    /// <summary>A subject playing the wave, posed once so it owes nothing, with its cull sphere where the test says.
    /// A compose rewrites the sphere, so a test that steps again and means it to hold seats the sphere again.</summary>
    private static DbId Walker(ModelDef m, DbId scene, Vector4 sphere, out int first)
    {
        DbId obj = Subject(m, scene, out first);
        ScanTree(obj.Index);
        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn, 0f);
        SceneDb.ObjSphere[obj.Index] = InView;
        Step(obj, 0.5f);
        SceneDb.ObjSphere[obj.Index] = sphere;
        return obj;
    }

    private static void Step(DbId obj, float dt)
    {
        _frame++;
        _danglyDt = dt;
        StepObject(obj, dt, 0);
    }

    // Given a creature playing an animation outside the camera's view
    // When the camera turns back to it a few seconds later
    // Then its animation carries on from where it stopped, with no time skipped
    private static void ClockPausesOutOfView(Checker check, DbId world)
    {
        DbId obj = Walker(Chain(), world, BehindCamera, out int first);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1f, 0f, 0f)), "half a second in, the wave has run 1 m");
        for (int k = 0; k < 3; k++) Step(obj, 1f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1f, 0f, 0f)), "three seconds out of view move it nowhere");
        SceneDb.ObjSphere[obj.Index] = InView;
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "back in view, it carries on from where it stopped");
    }

    // Given a creature playing an animation inside the camera's view
    // When the frames pass
    // Then its animation advances every frame as it does today
    private static void ClockRunsInView(Checker check, DbId world)
    {
        DbId obj = Walker(Chain(), world, InView, out int first);
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "in view, the next frame advances the wave");
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(2f, 0f, 0f)), "...and so does the one after it");
    }

    // Given a creature walking outside the camera's view
    // When its feet would hit the ground
    // Then no footstep sound plays, as in the stock client
    private static void UnseenEventsAreSilent(Checker check, ExtenderLog log, DbId world)
    {
        ModelDef m = Chain();
        DbId obj = Subject(m, world, out _);
        ScanTree(obj.Index);
        byte* any = stackalloc byte[2];
        any[0] = (byte)'*'; any[1] = 0;
        nint shell = EventShell(check, log, obj, out bool freshTables);
        nint cb = Listen(shell, TestEvent, any);
        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn, 0f);
        SceneDb.ObjSphere[obj.Index] = InView;
        Step(obj, 0.1f);
        SceneDb.ObjSphere[obj.Index] = BehindCamera;
        _evN = 0;
        Step(obj, 1f);
        check.Expect(_evN == 0, "a second out of view crosses the wave's events and raises none");
        SceneDb.ObjSphere[obj.Index] = InView;
        Step(obj, 0.2f);
        check.Expect(_evN == 1 && _ev[0] == "snd_one", "back in view, the event ahead of its clock is raised");
        _evN = 0;
        Seam.CallbackList.Remove(shell, cb);
        DropEventShell(obj.Index, shell, freshTables);
    }

    // Given a creature walking outside the camera's view
    // When the camera turns back to it
    // Then it stands where the server moved it
    private static void PositionStaysTrue(Checker check, DbId world)
    {
        DbId obj = Walker(Chain(), world, BehindCamera, out int first);
        SceneDb.ObjPos[obj.Index] = new Vector3(7f, 3f, 0f);
        Moved(obj.Index);
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.ObjWorld[obj.Index].Translation, new Vector3(7f, 3f, 0f)),
                     "a held object's move still reaches its world placement");
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1f, 0f, 0f)), "...and its clock still stays put");
    }

    // Given a spell effect outside the camera's view
    // When the camera turns back to it
    // Then the effect has kept playing
    private static void EffectsKeepPlaying(Checker check, DbId world)
    {
        ModelDef m = Chain();
        m.Classification = HitTest.ClassEffect;
        DbId obj = Walker(m, world, BehindCamera, out int first);
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "an effect model keeps playing out of view");
    }

    // Given a creature in view whose held weapon or cloak is outside the frame
    // When the creature swings or walks
    // Then the weapon and cloak stay in step with it
    private static void WornItemsFollowTheBody(Checker check, DbId world)
    {
        DbId body = Walker(Chain(), world, InView, out _);
        DbId weapon = Walker(Chain(), world, BehindCamera, out int wFirst);
        SceneDb.SetAttachTo(weapon.Index, body);
        Step(weapon, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[wFirst + 1], new Vector3(1.5f, 0f, 0f)), "a weapon outside the frame follows its body in view");
        SceneDb.ObjSphere[body.Index] = BehindCamera;
        SceneDb.ObjSphere[weapon.Index] = InView;
        Step(weapon, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[wFirst + 1], new Vector3(1.5f, 0f, 0f)), "...and pauses with a body out of view");
        SceneDb.SetAttachTo(weapon.Index, default);
    }

    // Given a creature just outside the camera's view whose shadow falls inside it
    // When the creature walks
    // Then its shadow walks too
    private static void ShadowsKeepMoving(Checker check, DbId world)
    {
        var offRight = new Vector4(60f, 50f, 0f, 5f);
        DbId obj = Walker(Chain(), world, offRight, out int first);
        ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, Vector3.Normalize(new Vector3(-1f, 0f, -0.3f)), hasSun: true, 60f);
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "a sun throwing its shadow into view keeps it walking");
        ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, Vector3.Normalize(new Vector3(1f, 0f, -0.3f)), hasSun: true, 60f);
        SceneDb.ObjSphere[obj.Index] = offRight;
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "a sun throwing it away from the view holds it");
        ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, default, hasSun: false, 0f);
    }

    // Given a flickering lamp outside the camera's view whose light reaches into it
    // When the frames pass
    // Then the light keeps flickering
    private static void LampsKeepFlickering(Checker check, DbId world)
    {
        ModelDef m = Chain();
        m.Nodes[2].Kind = NodeKind.Light;
        m.Nodes[2].Light = new LightDef();
        DbId obj = Walker(m, world, BehindCamera, out int first);
        SceneDb.PartLitAnim[first + 2] = 0;
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1f, 0f, 0f)), "a steady lamp out of view holds");
        SceneDb.PartLitAnim[first + 2] = LitKeyed;
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "a flickering one keeps stepping");
        SceneDb.PartLitAnim[first + 2] = 0;
    }

    // Given a character model in a GUI panel
    // When the frames pass
    // Then it animates whatever the world camera faces
    private static void PanelsAlwaysAnimate(Checker check, DbId panel)
    {
        DbId obj = Walker(Chain(), panel, BehindCamera, out int first);
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "a panel's model animates out of the world's view");
    }

    // Given a creature that has just joined the area outside the camera's view
    // When the camera first turns to it
    // Then it shows in a proper pose, never its rest pose
    private static void NoRestPoseOnFirstLook(Checker check, DbId world)
    {
        DbId obj = Subject(Chain(), world, out int first);
        ScanTree(obj.Index);
        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn, 0f);
        SceneDb.ObjSphere[obj.Index] = BehindCamera;
        Step(obj, 0.5f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1f, 0f, 0f)), "a newcomer out of view takes its first pose");
        SceneDb.ObjSphere[obj.Index] = BehindCamera;
        Step(obj, 0.5f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1f, 0f, 0f)), "...and only then holds");
    }

    // Given a creature that has stood outside the camera's view
    // When the camera turns to it and the player clicks it
    // Then the click selects it
    private static void ClicksStillLand(Checker check, DbId world)
    {
        DbId obj = Walker(Chain(), world, BehindCamera, out _);
        SceneDb.ObjPos[obj.Index] = new Vector3(20f, 0f, 0f);
        Moved(obj.Index);
        Step(obj, 0.25f);
        HitBounds.Refresh(obj.Index);
        float away = (SceneDb.HitObjSphereC[obj.Index] - new Vector3(20f, 0f, 0f)).Length();
        check.Expect(away < 6f, $"the hit test's bounds follow a held object to where it stands ({away:F1} m off)");
    }

    /// <summary>A low sun's shadow runs as far as the cascades bake it, not a few of the caster's own heights.
    /// Oracle: 65 m along the sun at 60 m of shadow lands 5.6 m right of centre, inside the 37 m half-width.</summary>
    private static void LongDawnShadow(Checker check, DbId world)
    {
        var upSun = new Vector4(65f, 50f, 0f, 1.5f);
        DbId obj = Walker(Chain(), world, upSun, out int first);
        ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, Vector3.Normalize(new Vector3(-1f, 0f, -0.1f)), hasSun: true, 60f);
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "a dawn shadow 40 heights long keeps its caster walking");
        ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, Vector3.Normalize(new Vector3(-1f, 0f, -0.1f)), hasSun: true, 10f);
        SceneDb.ObjSphere[obj.Index] = upSun;
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "...and past the shadow distance the cascades bake it holds");
        ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, default, hasSun: false, 0f);
    }

    /// <summary>A lamp's shadow is the cube pass's cone. Oracle: the view's right edge at 50 m is 37.3 m, so a
    /// sphere at 45 m is out, and a 15 m lamp 2 m past it throws the shadow back in to 32 m.</summary>
    private static void LampShadowsKeepMoving(Checker check, DbId world)
    {
        var offRight = new Vector4(45f, 50f, 0f, 1.5f);
        DbId obj = Walker(Chain(), world, offRight, out int first);
        FrameInputs.ResetLocalLights(4);
        FrameInputs.RecordLocalLight(1, new Vector3(47f, 50f, 0f), 15f, Vector3.One);
        FrameInputs.RecordLocalLight(2, new Vector3(120f, 50f, 0f), 5f, Vector3.One);
        ViewGatherLamps(Vector3.Zero, 0f);
        check.Expect(_view.LampCount == 1, "only a lamp whose lit sphere reaches the view can shade it");
        ViewGatherLamps(Vector3.Zero, 30f);
        check.Expect(_view.LampCount == 0, "a lamp past the point-light shadow distance casts no shadow");
        ViewGatherLamps(Vector3.Zero, 0f);
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "a lamp throwing its shadow into view keeps it walking");
        FrameInputs.ResetLocalLights(4);
        FrameInputs.RecordLocalLight(1, new Vector3(100f, 50f, 0f), 60f, Vector3.One);
        ViewGatherLamps(Vector3.Zero, 0f);
        SceneDb.ObjSphere[obj.Index] = new Vector4(300f, 50f, 0f, 1.5f);
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "a lamp that never reaches the object holds it");
        FrameInputs.ResetLocalLights(0);
        ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, default, hasSun: false, 0f);
    }

    /// <summary>The game asking one object to step again, from inside the walk, gets that step. Oracle: the wave's 2 m/s.</summary>
    private static void ExternalAnimateSteps(Checker check, DbId world)
    {
        DbId obj = Walker(Chain(), world, BehindCamera, out int first);
        AnimateOneExternal(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1.5f, 0f, 0f)), "an external animate steps an object out of view");
        check.Expect(_view.On, "...and leaves the gate on for the rest of the walk");
    }

    /// <summary>A hold leaves no reduced rate behind. Oracle: band 2 is the far band, and 0 is full rate, at a stride of 1.</summary>
    private static void HoldForgetsTheRateBand(Checker check, DbId world)
    {
        DbId obj = Walker(Chain(), world, BehindCamera, out _);
        _rate.Band[obj.Index] = 2;
        _rate.Held[obj.Index] = 0.05f;
        Step(obj, 0.25f);
        check.Expect(_rate.Band[obj.Index] == 0, "a held object comes back at full rate, so its cloak does not snap rigid");
        check.Expect(_rate.Held[obj.Index] == 0f, "...with no time banked");
    }

    /// <summary>A high sun's shadow stops at the ground under the object, not 120 m down. Oracle: the view's right edge
    /// at 50 m is 37.3 m; 8.4 m along this sun moves a sphere at 60 m to 57.5 m, still out, where 120 m lands it at 24 m.</summary>
    private static void HighSunShadowStops(Checker check, DbId world)
    {
        var offRight = new Vector4(60f, 50f, 0f, 1.5f);
        DbId obj = Walker(Chain(), world, offRight, out int first);
        ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, Vector3.Normalize(new Vector3(-0.3f, 0f, -0.95f)), hasSun: true, 120f);
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1f, 0f, 0f)), "a shadow that lands short of the view holds its caster");
        ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, default, hasSun: false, 0f);
    }

    /// <summary>The gate reads the world's own view, whichever view drew last. Oracle: a camera at the origin looking
    /// along +Y has a sphere at y = -100 behind it, and a wave held at half a second reads 1 m.</summary>
    private static void WorldViewDrivesTheGate(Checker check, DbId world)
    {
        DbId obj = Walker(Chain(), world, BehindCamera, out int first);
        var wv = new AlloyView
        {
            View = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitY, Vector3.UnitZ),
            Fovy = 45f, Aspect = 1.8f, Near = 0.1f, Far = 200f, Perspective = true, Valid = true,
        };
        bool perspective = FrameInputs.HasPerspective, sun = FrameInputs.HasSun;
        FrameInputs.HasPerspective = false;
        FrameInputs.HasSun = false;
        ViewBegin(in wv);
        Step(obj, 0.25f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1f, 0f, 0f)), "a flat GUI view drawn last does not stand the world's gate down");
        wv.Perspective = false;
        int down = _census.UngatedWalks;
        FrameInputs.HasPerspective = true;
        ViewBegin(in wv);
        check.Expect(!_view.On && _census.UngatedWalks == down + 1, "a world view with no perspective stands the gate down, and the period line says so");
        FrameInputs.HasPerspective = perspective;
        FrameInputs.HasSun = sun;
        ViewLatch(Vector3.Zero, Vector3.UnitY, 45f, 1.8f, 0.1f, 200f, default, hasSun: false, 0f);
    }
}
