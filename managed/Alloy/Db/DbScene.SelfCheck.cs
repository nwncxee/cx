using System;
using System.Numerics;
using System.Threading;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Off-device checks: the camera arithmetic against hand-computed values, the
/// snapshot ping-pong protocol against a second thread, and the scene's draw-order list. No engine, no GL --
/// every input is a column this file writes.</summary>
internal static unsafe class DbSceneSelfCheck
{
    internal static bool Run(ExtenderLog log)
    {
        var check = new Checker(log, "DbScene.SelfCheck");
        // Everything below is a FIXTURE. Take the counters now and put them back at the end: a fixture is not
        // work the session did, and a period line that reports five scenes created and five destroyed beside
        // a live figure that never saw either is a line nobody can act on.
        var scenes = DbScene.TakeCounters();
        var cams = DbCamera.TakeCounters();
        int liveScenes = SceneDb.Scenes.Live, liveCams = SceneDb.Cameras.Live;
        ObjectBridge.MuteForSelfCheck();
        Camera(check);
        Names(check);
        Order(check);
        PingPong(check, log);
        // The suite must give every slot back. Asserted rather than assumed: a leaked fixture record shows up
        // live as a scene that exists and draws nothing, and the period line's live figure is then a number
        // nobody can reconcile with the created/destroyed pair beside it.
        check.Expect(SceneDb.Scenes.Live == liveScenes,
                     $"the suite leaves the scene table as it found it ({SceneDb.Scenes.Live} live, was {liveScenes})");
        check.Expect(SceneDb.Cameras.Live == liveCams,
                     $"the suite leaves the camera table as it found it ({SceneDb.Cameras.Live} live, was {liveCams})");
        DbScene.PutCounters(scenes);
        DbCamera.PutCounters(cams);
        // The suite destroys scenes with the object side deliberately unwired; the refusal line was muted for
        // it and is given back here, so a genuinely unwired DB session still says so.
        ObjectBridge.ResetForSelfCheck();
        log.Info(check.Ok ? "DbScene.SelfCheck: passed"
                          : "DbScene.SelfCheck: FAILED -- the CX scene database's camera math, draw order or "
                            + "snapshot handshake is wrong; in DB mode that is every view placed or sequenced wrong");
        return check.Ok;
    }

    /// <summary>The engine's naming rule and the camera member lookup. Oracle: hand-written names -- the
    /// suffixes <c>Scene::Scene</c>'s loop gives a second and third holder, and the camera a by-name find returns.</summary>
    private static void Names(Checker check)
    {
        DbId a = DbScene.Create("sceneGUI_cx_selfcheck"), b = DbScene.Create("SCENEGUI_CX_SELFCHECK"), c = DbScene.Create("sceneGUI_cx_selfcheck");
        check.Expect(!a.IsNull && !b.IsNull && !c.IsNull, "three scene records allocate");
        if (a.IsNull || b.IsNull || c.IsNull) return;
        check.Expect(SceneDb.SceneName[a.Index] == "sceneGUI_cx_selfcheck", "the first holder keeps the name");
        check.Expect(SceneDb.SceneName[b.Index] == "SCENEGUI_CX_SELFCHECK1", "a case-blind second holder gets 1");
        check.Expect(SceneDb.SceneName[c.Index] == "sceneGUI_cx_selfcheck2", "a third holder gets 2");
        check.Expect(DbScene.UniqueName(new string('n', 70)).Length == 63, "a name is clipped to the engine's 63 bytes");

        DbId camObj = SceneDb.Objects.Alloc();
        DbId cam = DbCamera.Create(camObj);
        check.Expect(!cam.IsNull, "a camera record allocates");
        if (!cam.IsNull)
        {
            SceneDb.CamName[cam.Index] = "cameraGUI_cx_selfcheck1";
            DbCamera.AttachToScene(cam, b);
            check.Expect(DbScene.Find(b, "cameraGUI_cx_selfcheck1").Value == camObj.Value,
                "a camera attached to a scene is found there by name, as its object half");
            check.Expect(DbScene.Find(a, "cameraGUI_cx_selfcheck1").IsNull, "...and only in its own scene");
            check.Expect(DbScene.UniqueName("cameraGUI_cx_selfcheck1") == "cameraGUI_cx_selfcheck11",
                "a live camera's name is taken for the next holder");
            DbCamera.Destroy(cam);
        }
        DbScene.Destroy(a); DbScene.Destroy(b); DbScene.Destroy(c);
    }

    /// <summary>The camera block: view/projection against the engine's own formulae recomputed by hand,
    /// <c>MapToScreen</c> against a point whose screen position is known by construction, <c>MouseDir</c>
    /// against <c>MapToScreen</c>'s inverse, and <c>GetViewVolume</c> against the six half-space tests.</summary>
    private static void Camera(Checker check)
    {
        DbId scene = DbScene.Create("sceneWorldTest");
        check.Expect(!scene.IsNull, "a scene record allocates");
        SceneDb.SceneIsWorld[scene.Index] = true;

        // With the sub-object the shell carries at +8, because every camera read takes ITS word for the
        // camera's place in the world and a camera without one would not exercise that at all.
        DbId camObj = SceneDb.Objects.Alloc();
        SceneDb.ObjKindOf[camObj.Index] = ObjKind.Empty;
        SceneDb.ObjPos[camObj.Index] = Vector3.Zero;
        SceneDb.ObjRot[camObj.Index] = Quaternion.Identity;
        DbId cam = DbCamera.Create(camObj);
        check.Expect(!cam.IsNull, "a camera record allocates");
        check.Expect(SceneDb.Objects.Valid(SceneDb.CamObj[cam.Index]),
                     "a camera record carries the CAurObject sub-object its shell has at +8");
        DbCamera.AttachToScene(cam, scene);

        // A camera at the origin looking down -Z with an unrotated frame, 90 degrees vertical, 2:1 viewport.
        const float Fov = 90f, Near = 1f, Far = 101f;
        const int Vw = 800, Vh = 400;
        DbCamera.SetPosition(cam, Vector3.Zero);
        DbCamera.SetOrientation(cam, Quaternion.Identity);
        DbCamera.SetViewAngle(cam, Fov);
        DbCamera.SetClipDist(cam, Near, Far);
        bool built = DbCamera.BuildView(cam, 0f, 0f, Vw, Vh, Vw, Vh, default, out AlloyView v);
        check.Expect(built && v.Valid, "the world camera (no viewport of its own) builds a view off the draw rect");
        check.Expect(v.W == Vw && v.H == Vh && v.X == 0 && v.Y == 0,
                     "a camera with no viewport of its own covers the whole draw rect, as Camera::RenderScene's "
                     + "camW==0 && camH==0 branch does");
        check.Expect(Near1(v.Aspect, 2f), "the aspect is the rect's w/h");
        check.Expect(v.Perspective, "a positive view angle is the perspective branch");

        // aurMatrixStack::Perspective @0x4db1d0 with fovy 90: tan(45deg) = 1, so m11 = 1/aspect, m22 = 1.
        check.Expect(Near1(v.Proj.M11, 0.5f) && Near1(v.Proj.M22, 1f),
                     "the projection is the engine's glFrustum form: m11 = 1/(aspect*tan(fov/2)), m22 = 1/tan(fov/2)");
        check.Expect(Near1(v.Proj.M33, -(Far + Near) / (Far - Near)) && Near1(v.Proj.M34, -1f)
                     && Near1(v.Proj.M43, -(2f * Far * Near) / (Far - Near)),
                     "the depth row is GL's [-1,1] mapping, not D3D's [0,1]");
        check.Expect(Near1(v.DepthNear, 0f) && Near1(v.DepthFar, 1f),
                     "a non-sceneGUI scene with a positive view angle owns the whole depth range");

        // The view matrix of a camera at the origin with no rotation is the identity.
        check.Expect(Close(v.View, Matrix4x4.Identity),
                     "the view matrix of an unrotated camera at the origin is the identity");

        // ...and of a translated one it is a pure -pos translation.
        var at = new Vector3(3f, -4f, 5f);
        DbCamera.SetPosition(cam, at);
        DbCamera.BuildView(cam, 0f, 0f, Vw, Vh, Vw, Vh, default, out v);
        check.Expect(Close(v.View, Matrix4x4.CreateTranslation(-at)),
                     "the view matrix of an unrotated camera is a translation by -position");

        // MapToScreen: a point one unit dead ahead lands at the rect's centre. The engine's tail is
        // vp + half + P' * (h/2)/tan(fov/2), and P' of a centred point is (0,0).
        Vector3 s = DbCamera.MapToScreen(cam, at + new Vector3(0f, 0f, -10f), false, 0f, 0f, Vw, Vh, 1f);
        check.Expect(Near1(s.X, Vw * 0.5f) && Near1(s.Y, Vh * 0.5f),
                     "MapToScreen puts a point dead ahead at the centre of the camera's rect");
        check.Expect(Near1(s.Z, -10f), "MapToScreen's z is the eye-space depth, negative in front");

        // One unit right at 10 ahead: tan(45) = 1, so (h/2)/tan = 200 pixels per unit of x/(-z).
        Vector3 r = DbCamera.MapToScreen(cam, at + new Vector3(1f, 0f, -10f), false, 0f, 0f, Vw, Vh, 1f);
        check.Expect(Near1(r.X, Vw * 0.5f + 0.1f * 200f),
                     "MapToScreen scales BOTH axes by the VERTICAL half-extent over tan(fov/2), as the engine does");
        check.Expect(Near1(r.Y, Vh * 0.5f), "a point with no vertical offset keeps the centre's y");

        // The normalised form is the engine's own tail: x/w and 1 - y/h (top-down [0,1]).
        Vector3 n = DbCamera.MapToScreen(cam, at + new Vector3(0f, 0f, -10f), true, 0f, 0f, Vw, Vh, 1f);
        check.Expect(Near1(n.X, 0.5f) && Near1(n.Y, 0.5f),
                     "the normalised form of a centred point is (0.5, 0.5) -- x/w and 1 - y/h");

        // MouseDir through the rect's centre is the camera's forward axis.
        Vector3 d = DbCamera.MouseDir(cam, Vw / 2, Vh / 2, 0f, 0f, Vw, Vh);
        check.Expect(Near1(d.X, 0f) && Near1(d.Y, 0f) && Near1(d.Z, -1f),
                     "MouseDir through the rect's centre is -Z, the camera's forward axis");
        check.Expect(Near1(d.Length(), 1f), "MouseDir is normalised");

        // ...and MouseDir round-trips MapToScreen: project a point, ask for the ray through that pixel, and
        // the ray must point at the point. This is the one check that would catch a sign or axis error in
        // either direction, because a matching error in both would not round-trip through the rect's corner.
        Vector3 target = at + new Vector3(2f, -1.5f, -7f);
        Vector3 proj = DbCamera.MapToScreen(cam, target, false, 0f, 0f, Vw, Vh, 1f);
        Vector3 ray = DbCamera.MouseDir(cam, (int)MathF.Round(proj.X), (int)MathF.Round(proj.Y), 0f, 0f, Vw, Vh);
        Vector3 want = Vector3.Normalize(target - at);
        check.Expect(Vector3.Dot(ray, want) > 0.9999f,
                     "MouseDir(MapToScreen(p)) points at p -- the two share one screen-space convention");

        // The rotated case: a 90-degree yaw about Z must put a point on +X dead ahead.
        Quaternion yaw = Quaternion.CreateFromAxisAngle(new Vector3(0f, 1f, 0f), MathF.PI * 0.5f);
        DbCamera.SetOrientation(cam, yaw);
        Vector3 fwd = DbCamera.Forward(cam);
        check.Expect(Near1(fwd.X, -1f) && Near1(fwd.Z, 0f),
                     "a +90 degree yaw about Y turns the forward axis from -Z to -X");

        // The live case the two columns hide: the game turns the world camera through its CAurObject
        // sub-object, so ObjRot moves while CamRot stays where NewCAurCamera left it. Every camera read has
        // to take the sub-object's word, as the engine does -- Camera::GetOrientation @0x6aa610 returns
        // `this+0xbc`, which IS Gob+0xb4 on the gob half at +8. MouseDir read the column instead, so the pick
        // ray ignored the camera's rotation entirely: click-to-move walked nowhere near the cursor and a
        // hover never found a creature to highlight.
        DbCamera.SetOrientation(cam, Quaternion.Identity);
        SceneDb.ObjRot[camObj.Index] = yaw;
        Vector3 subFwd = DbCamera.Forward(cam);
        check.Expect(Near1(subFwd.X, -1f) && Near1(subFwd.Z, 0f),
                     "turning the camera through its sub-object alone turns the forward axis");
        Vector3 subRay = DbCamera.MouseDir(cam, Vw / 2, Vh / 2, 0f, 0f, Vw, Vh);
        check.Expect(Near1(subRay.X, -1f) && Near1(subRay.Z, 0f),
                     "...and the PICK RAY with it: MouseDir rotates by the sub-object, not by the camera column");
        SceneDb.ObjRot[camObj.Index] = Quaternion.Identity;

        DbCamera.SetOrientation(cam, Quaternion.Identity);
        DbCamera.SetPosition(cam, Vector3.Zero);

        // The view volume: every plane must report a point at the frustum's centre as inside, and a point
        // well outside each plane as outside.
        DbCamera.BuildView(cam, 0f, 0f, Vw, Vh, Vw, Vh, default, out v);
        DbCamera.SetViewVolume(cam, v);
        check.Expect(SceneDb.CamVolumeSet[cam.Index], "a built view latches ViewVolumeSet, as Camera+0x4e4 does");
        // The ENGINE's convention, measured in Camera::HitCheckViewVolumeNearest @0x5a4060, which rejects a
        // candidate on n.P + d > 0: INSIDE is <= 0. That is the opposite of CullPass.Planes', and having it
        // backwards would answer every frustum question the GAME asks inside-out.
        var inside = new Vector3(0f, 0f, -(Near + Far) * 0.5f);
        bool allIn = true;
        for (int k = 0; k < SceneDb.ViewPlanes; k++)
        {
            Plane p = DbCamera.ViewVolume(cam, k);
            if (Vector3.Dot(p.Normal, inside) + p.D > 0f) allIn = false;
        }
        check.Expect(allIn, "a point in the middle of the frustum is INSIDE all six view-volume planes -- "
                          + "the engine's dot(n, p) + d <= 0, not the renderer's >= 0");
        check.Expect(Vector3.Dot(DbCamera.ViewVolume(cam, 0).Normal, new Vector3(0f, 0f, -1f)) < -0.99f,
                     "plane 0 is the NEAR plane, and in the engine's sign its normal points AWAY from the "
                   + "view direction");
        var behind = new Vector3(0f, 0f, 10f);
        check.Expect(Vector3.Dot(DbCamera.ViewVolume(cam, 0).Normal, behind) + DbCamera.ViewVolume(cam, 0).D > 0f,
                     "a point behind the camera is outside the near plane");
        var tooFar = new Vector3(0f, 0f, -(Far + 10f));
        check.Expect(Vector3.Dot(DbCamera.ViewVolume(cam, 1).Normal, tooFar) + DbCamera.ViewVolume(cam, 1).D > 0f,
                     "a point past the far clip is outside the far plane");

        // A GUI camera: a view angle of 0 takes the ortho branch and the sceneGUI depth slice.
        DbId gui = DbScene.Create("sceneGUI_PNL_INV");
        DbId guiCam = DbCamera.Create(default);
        DbCamera.AttachToScene(guiCam, gui);
        DbCamera.SetViewPort(guiCam, 10f, 20f, 300f, 200f);
        DbCamera.SetViewAngle(guiCam, 0f);
        DbCamera.SetClipDist(guiCam, -1f, 1f);
        DbCamera.BuildView(guiCam, 0f, 0f, Vw, Vh, Vw, Vh, default, out AlloyView gv);
        check.Expect(gv.Valid && !gv.Perspective, "a view angle of 0 is the ORTHO branch");
        check.Expect(gv.X == 10 && gv.Y == 20 && gv.W == 300 && gv.H == 200,
                     "a camera with a viewport of its own renders into it, not into the draw rect");
        // The engine's ortho box is (0, aspect*h*0.01, 0, h*0.01) with h the viewport height.
        float unit = 200f * 0.01f;
        check.Expect(Near1(gv.Proj.M22, 2f / unit),
                     "the ortho box is (0, aspect*h/100, 0, h/100), the engine's cam+0x4c8 extent");
        check.Expect(Near1(gv.DepthNear, DbCamera.GuiBackNear) && Near1(gv.DepthFar, DbCamera.GuiBackFar),
                     "a sceneGUI view that is not one of the five overlays gets the BACK depth slice");
        // The character sheet's resize viewport (0, y, 8192, h): cut to the screen, still one pixel per 0.01.
        DbCamera.SetViewPort(guiCam, 0f, 100f, 8192f, 8192f);
        DbCamera.SetPosition(guiCam, new Vector3(0f, 1f, 0f));
        DbCamera.BuildView(guiCam, 0f, 0f, Vw, Vh, Vw, Vh, default, out AlloyView cv);
        Vector4 c = Vector4.Transform(new Vector4(2f, 2f, 0f, 1f), cv.View * cv.Proj);
        float px = cv.X + (c.X / c.W * 0.5f + 0.5f) * cv.W, py = cv.Y + (c.Y / c.W * 0.5f + 0.5f) * cv.H;
        check.Expect(cv.W == Vw && cv.H == Vh - 100, "a viewport past the screen is cut to it (SetViewPortLight)");
        check.Expect(Near1(px, 200f) && Near1(py, 200f), "a cut viewport still maps 0.01 world units to one pixel");
        DbCamera.SetViewPort(guiCam, 10f, 20f, 300f, 200f);
        DbCamera.SetPosition(guiCam, Vector3.Zero);
        DbId front = DbScene.Create("sceneGUI_MouseOverlay");
        DbCamera.DepthRange(front, 0f, out float fn, out float ff);
        check.Expect(Near1(fn, DbCamera.GuiFrontNear) && Near1(ff, DbCamera.GuiFrontFar),
                     "sceneGUI_MouseOverlay is one of the five FRONT overlays");

        // A GUI camera at a UI scale other than 1: the rect shrinks and recentres, which is the arithmetic
        // Camera::MapToScreen spells out and UpscaleViewport applies to the view.
        DbCamera.Rect(guiCam, 0f, 0f, Vw, Vh, 2f, out float sx, out float sy, out float sw, out float sh);
        check.Expect(Near1(sw, 150f) && Near1(sh, 100f), "a UI scale of 2 halves a panel camera's rect");
        check.Expect(Near1(sx, 10f + 75f) && Near1(sy, 20f + 50f),
                     "the shrunk rect stays centred on the original: x += (w - w/s)/2");
        DbCamera.Rect(guiCam, 0f, 0f, Vw, Vh, 1.05f, out _, out _, out float ew, out _);
        check.Expect(Near1(ew, 300f),
                     "a UI scale within 0.09 of 1 is no scale at all -- Camera::UpscaleViewport's own first test");
        DbCamera.Rect(cam, 0f, 0f, Vw, Vh, 2f, out _, out _, out float worldW, out _);
        check.Expect(Near1(worldW, Vw * 0.5f),
                     "a camera with no rect of its own takes the DRAW rect and is still UI-scaled -- the "
                   + "engine applies the shrink past the GetViewPort fallback, not before it");

        Upscaled(check, gui, guiCam, Vw, Vh);

        DbCamera.Destroy(guiCam);
        DbCamera.Destroy(cam);
        DbScene.Destroy(front);
        DbScene.Destroy(gui);
        DbScene.Destroy(scene);
    }

    /// <summary>A view under an <c>UpscaleViewport</c> answer at UI scale 2. Oracle: screen pixels worked by
    /// hand from <c>Camera::RenderScene</c>'s two branches and <c>ModifyGUIMatrix</c>'s Scale-then-Translate.</summary>
    private static void Upscaled(Checker check, DbId gui, DbId panelCam, int vw, int vh)
    {
        DbId full = DbCamera.Create(default);
        DbCamera.AttachToScene(full, gui);
        DbCamera.SetViewAngle(full, 0f);
        DbCamera.SetClipDist(full, -1f, 1f);
        // The centred full-screen answer for an 800x400 draw rect: rect (200,100,400,200), mode 1, offset = origin.
        var centred = new DbCamera.ViewUpscale { Valid = true, X = 200f, Y = 100f, W = 400f, H = 200f,
                                                 Mode = 1f, OffX = 200f, OffY = 100f, PickX = 200f, PickY = 100f, Scale = 2f };
        DbCamera.BuildView(full, 0f, 0f, vw, vh, vw, vh, in centred, out AlloyView fv);
        check.Expect(fv.Valid && fv.X == 0 && fv.Y == 0 && fv.W == vw && fv.H == vh,
                     "a GUI camera with no rect keeps the whole draw rect under an upscale");
        Pixel(fv, centred, 4f, 2f, out float cx, out float cy);
        check.Expect(Near1(cx, 400f) && Near1(cy, 200f), "the screen centre stays put at UI scale 2");
        Pixel(fv, centred, 2f, 1f, out float ox, out float oy);
        check.Expect(Near1(ox, 0f) && Near1(oy, 0f), "the GUI point at the upscaled rect's corner lands on the screen's");
        Pixel(fv, centred, 3f, 1.5f, out float qx, out float qy);
        check.Expect(Near1(qx, 200f) && Near1(qy, 100f), "a GUI point half-way to the centre is drawn twice as far out");

        // A panel with its own rect: the answer's rect is the viewport, mode 3 scales from its origin.
        var anchored = new DbCamera.ViewUpscale { Valid = true, X = 5f, Y = 0f, W = 600f, H = 400f, Mode = 3f, Scale = 2f };
        DbCamera.BuildView(panelCam, 0f, 0f, vw, vh, vw, vh, in anchored, out AlloyView pv);
        check.Expect(Near1(pv.View.M11, 1f), "the view stays rigid: the GUI model is applied after the view volume");
        check.Expect(pv.X == 5 && pv.Y == 0 && pv.W == 600 && pv.H == 400, "a panel camera renders into the upscaled rect");
        Pixel(pv, anchored, 1f, 1f, out float px, out float py);
        check.Expect(Near1(px, 205f) && Near1(py, 200f), "mode 3 doubles a panel's content from the rect's origin");
        DbCamera.PickSegment(full, 0, 0, 0f, 0f, vw, vh, in centred, out Vector3 at, out _);
        check.Expect(Near1(at.X, 2.005f) && Near1(at.Y, 1.005f),
                     "a pick at the screen's corner finds the GUI point drawn there: px/s plus the pick offset");
        // A mode 3 answer for the panel's own (10,20,300,200) rect: drawn at (20,40,600,400), GUI (1,1) at (220,240).
        var chat = new DbCamera.ViewUpscale { Valid = true, X = 20f, Y = 40f, W = 600f, H = 400f, Mode = 3f, Scale = 2f };
        DbCamera.PickSegment(panelCam, 220, 240, 0f, 0f, vw, vh, in chat, out at, out _);
        check.Expect(Near1(at.X, 1.005f) && Near1(at.Y, 1.005f),
                     "a pick on a scaled panel maps back against the camera's OWN rect, as Camera::HitCheck does");
        check.Expect(DbCamera.PickSegment(panelCam, 700, 380, 0f, 0f, vw, vh, in chat, out _, out _),
                     "the pick bounds grow by the scale with the panel");
        anchored.Mode = 0f;
        DbCamera.BuildView(panelCam, 0f, 0f, vw, vh, vw, vh, in anchored, out pv);
        Pixel(pv, anchored, 1f, 1f, out px, out py);
        check.Expect(Near1(px, 105f) && Near1(py, 100f), "a placement mode outside 1..7 is outside ModifyGUIMatrix's gate: no GUI scale");
        DbCamera.Destroy(full);
    }

    private static void Pixel(in AlloyView v, in DbCamera.ViewUpscale up, float ux, float uy, out float x, out float y)
    {
        Matrix4x4 view = DbCamera.HasGuiModel(in up) ? DbCamera.GuiModel(in up) * v.View : v.View;
        Vector4 c = Vector4.Transform(new Vector4(ux, uy, 0f, 1f), view * v.Proj);
        x = v.X + (c.X / c.W * 0.5f + 0.5f) * v.W;
        y = v.Y + (c.Y / c.W * 0.5f + 0.5f) * v.H;
    }

    /// <summary>The scene's member list, the four <c>RenderGob*</c> reorderings and the highlight list's
    /// leave and death edges. Oracle: the handles and order written in by hand.</summary>
    private static void Order(Checker check)
    {
        DbId scene = DbScene.Create("orderTest");
        DbId a = SceneDb.Objects.Alloc(), b = SceneDb.Objects.Alloc(), c = SceneDb.Objects.Alloc();
        DbScene.Join(scene, a); DbScene.Join(scene, b); DbScene.Join(scene, c);
        check.Expect(DbScene.MemberCount(scene) == 3, "three joins give three members");
        DbList m = DbScene.Members(scene);
        check.Expect(m[0] == a && m[1] == b && m[2] == c, "a join APPENDS, so the list is the join order");
        DbScene.Join(scene, a);
        check.Expect(DbScene.MemberCount(scene) == 3, "re-joining a member does not duplicate it");

        DbScene.RenderLast(scene, a);
        check.Expect(m[0] == b && m[1] == c && m[2] == a, "RenderGobLast moves the object to the back");
        DbScene.RenderFirst(scene, a);
        check.Expect(m[0] == a && m[1] == b && m[2] == c, "RenderGobFirst moves it to the front");
        DbScene.RenderAfter(scene, a, b);
        check.Expect(m[0] == b && m[1] == a && m[2] == c, "RenderGobAfter puts it immediately after the anchor");
        DbScene.RenderBefore(scene, c, b);
        check.Expect(m[0] == c && m[1] == b && m[2] == a, "RenderGobBefore puts it immediately before the anchor");
        DbScene.RenderAfter(scene, a, default);
        check.Expect(m[0] == c && m[1] == b && m[2] == a, "a RenderGobAfter with a non-member anchor changes nothing");

        DbScene.Leave(scene, b);
        check.Expect(DbScene.MemberCount(scene) == 2 && m[0] == c && m[1] == a,
                     "a leave removes the member and keeps the rest in order");
        DbScene.AddHighlight(scene, a, new Vector3(1f, 0.5f, 0.25f));
        DbScene.AddHighlight(scene, a, new Vector3(0f, 1f, 0f));
        check.Expect(SceneFeed.HighlightCount(scene) == 1,
                     "re-highlighting an object replaces its colour instead of listing it twice");
        SceneFeed.HighlightAt(scene, 0, out DbId hobj, out Vector3 hcol);
        check.Expect(hobj == a && Near1(hcol.Y, 1f), "the highlight carries the LAST colour set");
        DbScene.Leave(scene, a);
        check.Expect(SceneFeed.HighlightCount(scene) == 0, "leaving the scene drops the object's highlight too");
        // Highlighted by a scene it is not a member of: the leave of its own scene never reaches this list.
        DbScene.AddHighlight(scene, b, Vector3.One);
        DbScene.ObjectDied(b);
        check.Expect(SceneFeed.HighlightCount(scene) == 0,
                     "an object that dies leaves every scene's highlight list, not only its own scene's");

        // The camera is TWO records; destroying it must free both. This is the shape of leak that only shows
        // up as "the object table is full" an hour into a session, so it is asserted rather than trusted.
        DbId leakScene = DbScene.Create("camObjLifetime");
        DbId camObj = SceneDb.Objects.Alloc();
        DbId leakCam = DbCamera.Create(camObj);
        int objLiveBefore = SceneDb.Objects.Live;
        DbCamera.Destroy(leakCam);
        check.Expect(!SceneDb.Objects.Valid(camObj),
                     "destroying a camera frees its CAurObject row too -- a camera is two records, and the "
                   + "GUI builds one per panel view");
        check.Expect(SceneDb.Objects.Live == objLiveBefore - 1, "...exactly one object slot, not more");
        DbScene.Destroy(leakScene);

        // JoinNew is Join without the membership test; it must agree with Join on everything else, because
        // the tile path uses it ~1150 times an area load and nothing else would notice a divergence.
        DbId jScene = DbScene.Create("joinNew");
        DbId jObj = SceneDb.Objects.Alloc();
        DbScene.JoinNew(jScene, jObj);
        check.Expect(DbScene.MemberCount(jScene) == 1, "JoinNew adds the member");
        check.Expect(SceneDb.ObjScene[jObj.Index] == jScene, "...and sets its scene, exactly as Join does");
        DbScene.Leave(jScene, jObj);
        check.Expect(DbScene.MemberCount(jScene) == 0, "...and it leaves like any other member");
        check.Expect(SceneDb.ObjScene[jObj.Index].IsNull,
                     "...and its scene column goes with it (ChildRemove's UpdateScenes propagates that column)");
        // Gob::SetScene's ChildAdd onto a new panel with no matching ChildRemove (its +0x88 word was stale):
        // the old panel must stop listing the object, or it keeps drawing there after the move.
        DbId kScene = DbScene.Create("strandFrom");
        DbScene.Join(kScene, jObj);
        DbScene.Join(jScene, jObj);
        check.Expect(DbScene.MemberCount(kScene) == 0, "a join elsewhere takes the object out of the scene it was in");
        check.Expect(DbScene.MemberCount(jScene) == 1 && SceneDb.ObjScene[jObj.Index] == jScene,
                     "...and it is a member of the new one alone");
        DbScene.Leave(jScene, jObj);
        DbScene.Destroy(kScene);
        SceneDb.Objects.Free(jObj);
        DbScene.Destroy(jScene);

        check.Expect(DbScene.IsAnimating(scene),
                     "a fresh scene IS animating -- Scene::Scene @0x4724f0 leaves this[0xb9] = 1, and the "
                   + "flag is a question the game asks, never a gate Scene::Animate reads");
        DbScene.SetAnimating(scene, false);
        check.Expect(!DbScene.IsAnimating(scene),
                     "...and DisableAnimations is what turns it off: ONE column, because the engine has one "
                   + "byte -- Enable/DisableAnimations write it and IsAnimating (`return this[0xb9]`) reads it");
        DbScene.SetAnimating(scene, true);

        SceneDb.Objects.Free(a); SceneDb.Objects.Free(b); SceneDb.Objects.Free(c);
        DbScene.Destroy(scene);
    }

    /// <summary>The publish/consume handshake, against a real second thread: the renderer must see every
    /// frame exactly once and in order, the publisher must never overtake it by more than one frame, and
    /// neither side may allocate per frame.</summary>
    private static void PingPong(Checker check, ExtenderLog log)
    {
        const int Frames = 200;
        RenderSnapshot.Resume();
        int consumed = 0;
        long lastIndex = -1;
        bool orderOk = true, aliasOk = true;
        var started = new ManualResetEventSlim(false);
        RenderSnapshot.Frame? holding = null;

        var consumer = new Thread(() =>
        {
            started.Set();
            while (consumed < Frames)
            {
                RenderSnapshot.Frame? f = RenderSnapshot.Take();
                if (f == null) break;
                // The frame the renderer holds must never be the one the game thread is filling: that is the
                // whole point of the ping-pong, and an alias here would be a torn frame in live play.
                if (ReferenceEquals(f, RenderSnapshot.Building)) aliasOk = false;
                if (f.Index <= lastIndex) orderOk = false;
                lastIndex = f.Index;
                holding = f;
                consumed++;
                RenderSnapshot.Release();
            }
        }) { IsBackground = true, Name = "DbScene.SelfCheck.consumer" };
        consumer.Start();
        started.Wait(1000);

        DbId scene = DbScene.Create("pingpong");
        DbId cam = DbCamera.Create(default);
        DbCamera.AttachToScene(cam, scene);
        // Two measured blocks, not one. The first publish of a session legitimately allocates once -- the
        // lock's wait handle is lazy, and .NET makes it on the thread that first waits -- so a run measured
        // from cold reports that one-off as a per-frame allocator. What has to be zero is the MARGINAL cost:
        // the second block of identical publishes.
        // The lock also inflates lazily the first time the consumer and the publisher actually CONTEND, which
        // is timing: it can land in either measured block (live, 120 bytes once). A per-frame allocator
        // allocates in BOTH blocks; a one-off in at most one -- so the cleaner block is the marginal cost.
        int half = Frames / 2, threeQ = Frames * 3 / 4;
        for (int i = 0; i < half; i++) RenderSnapshot.Publish();
        long gcWarm = GC.GetAllocatedBytesForCurrentThread();
        for (int i = half; i < threeQ; i++) RenderSnapshot.Publish();
        long gcMid = GC.GetAllocatedBytesForCurrentThread();
        for (int i = threeQ; i < Frames; i++) RenderSnapshot.Publish();
        long gcEnd = GC.GetAllocatedBytesForCurrentThread();
        long perBlock = Math.Min(gcMid - gcWarm, gcEnd - gcMid);

        bool joined = consumer.Join(5000);
        check.Expect(joined, "the consumer thread drains every published frame and finishes");
        check.Expect(consumed == Frames, $"the renderer saw all {Frames} frames (saw {consumed})");
        check.Expect(orderOk, "frames arrive in publish order, each index strictly greater than the last");
        check.Expect(aliasOk, "the frame the renderer holds is never the one the game thread is filling");
        check.Expect(holding != null, "the consumer held a real frame");
        check.Expect(perBlock == 0,
                     $"the publish allocates NOTHING per frame on the game thread once warm "
                     + $"({gcMid - gcWarm} and {gcEnd - gcMid} bytes over two blocks of {Frames / 4} publishes) -- "
                     + "a NativeAOT GC on the present boundary is a stutter");

        DbCamera.Destroy(cam);
        DbScene.Destroy(scene);
        RenderSnapshot.Resume();
    }

    private static bool Near1(float a, float b) => MathF.Abs(a - b) < 1e-3f * MathF.Max(1f, MathF.Abs(b));

    private static bool Close(in Matrix4x4 a, in Matrix4x4 b)
    {
        fixed (Matrix4x4* pa = &a, pb = &b)
        {
            float* x = (float*)pa, y = (float*)pb;
            for (int i = 0; i < 16; i++) if (MathF.Abs(x[i] - y[i]) > 1e-4f) return false;
        }
        return true;
    }
}
