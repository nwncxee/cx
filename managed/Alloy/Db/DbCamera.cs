using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>What a <c>CAurCamera</c> IS: a row in <see cref="SceneDb.Cameras"/>, and the view/projection
/// arithmetic over its columns, whose terms are not re-derived here -- <see cref="AlloyCamera.Perspective"/>,
/// <see cref="AlloyCamera.Ortho"/>, <see cref="AlloyCamera.ViewOf"/>, <see cref="AlloyCamera.ViewportScales"/>
/// and <c>DepthRange</c> are the one copy. GAME thread: the game's reads are synchronous (ADR 0028 s5).</summary>
public static unsafe class DbCamera
{
    /// <summary>The ortho extent <c>Camera::RenderScene</c> derives: <c>cam+0x4c8 = viewportHeight * 0.01</c>,
    /// and the box is then <c>(0, aspect*h, 0, h)</c>.</summary>
    private const float OrthoUnitsPerPixel = 0.01f;
    /// <summary>Degrees to radians, halved -- the engine's own constant, <c>fovDeg * 0.017453292f * 0.5f</c>.</summary>
    private const float HalfFovToRad = MathF.PI / 360f;
    /// <summary><c>Camera::UpscaleViewport</c>'s first test: a game UI scale this close to 1 is no scale at all
    /// (@0x5a44b0), and the same epsilon gates <c>MapToScreen</c>'s and <c>MouseDir</c>'s rect division.</summary>
    private const float UpscaleEpsilon = 0.09f;

    private static ExtenderLog? _log;
    private static long _created, _destroyed, _refused, _views;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Create a camera record with the engine's own defaults: no viewport of its own (which is what
    /// makes it the world camera -- <c>Camera::SetViewPort</c> clamps a non-positive extent to 1, so a camera
    /// that ever set one cannot read as the world camera again), identity orientation, a null view volume.</summary>
    public static DbId Create(DbId obj)
    {
        DbId id = SceneDb.Cameras.Alloc();
        if (id.IsNull)
        {
            if (_refused++ == 0)
                _log?.Always($"DbCamera: the camera table is full ({SceneDb.MaxCameras} cameras) -- "
                           + "NewCAurCamera REFUSED; the game layer will get a null camera");
            return default;
        }
        int i = id.Index;
        SceneDb.CamScene[i] = default;
        SceneDb.CamPos[i] = Vector3.Zero;
        SceneDb.CamPrevPos[i] = Vector3.Zero;
        SceneDb.CamRot[i] = Quaternion.Identity;
        SceneDb.CamViewX[i] = 0f; SceneDb.CamViewY[i] = 0f; SceneDb.CamViewW[i] = 0f; SceneDb.CamViewH[i] = 0f;
        SceneDb.CamNear[i] = DefaultNear; SceneDb.CamFar[i] = DefaultFar;
        SceneDb.CamViewAngle[i] = DefaultViewAngle;
        SceneDb.CamFollow[i] = default; SceneDb.CamAttachTo[i] = default; SceneDb.CamAttachPart[i] = -1;
        SceneDb.CamBehavior[i] = 0;
        SceneDb.CamVolumeSet[i] = false;
        for (int k = 0; k < SceneDb.ViewPlanes; k++) SceneDb.CamVolume[i * SceneDb.ViewPlanes + k] = default;
        SceneDb.CamFadeA[i] = default; SceneDb.CamFadeB[i] = default;
        SceneDb.CamObj[i] = obj;
        SceneDb.CamFacade[i] = 0;
        SceneDb.CamName[i] = null;
        _created++;
        return id;
    }

    /// <summary>The clip distances a camera has before anything sets them -- the ENGINE's own constructor
    /// defaults, 0.1 near and 100.0 far across <c>Camera+0x4cc</c>/<c>+0x4d0</c> (<c>Camera::Camera</c>
    /// @0x5a42c0). Nothing in the game calls <c>SetClipDist</c> on the world camera, so these are what it
    /// renders with; a far plane of 45 -- the view ANGLE, one field over -- reaches the camera's own feet.</summary>
    public const float DefaultNear = 0.1f, DefaultFar = 100f;

    /// <summary>Destroy the camera record. Its <c>CAurObject</c> sub-object record is seam-obj's to free, from
    /// the same destructor slot.</summary>
    public static void Destroy(DbId cam)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        // A camera is TWO records -- the camera row and the CAurObject row its +8 sub-object resolves to
        // (NewCAurCamera mints both) -- and this freed only one, so every camera the game destroyed leaked an
        // object slot for the session. The GUI builds and tears down a camera per panel view, so that is a
        // steady drip, not a one-off.
        // Through the object destroy path, not a bare Free: the row can be in a scene's member list (a
        // subwindow behaviour attaches it) and carries a facade the game may still hold.
        DbId obj = SceneDb.CamObj[cam.Index];
        if (SceneDb.Objects.Valid(obj))
        {
            if (ObjectBridge.ObjectDestroy is { } destroy) destroy(obj);
            else { DbScene.ObjectDied(obj); SceneDb.Objects.Free(obj); }
        }
        SceneDb.CamObj[cam.Index] = default;
        SceneDb.CamFacade[cam.Index] = 0;
        SceneDb.CamName[cam.Index] = null;
        SceneDb.CamScene[cam.Index] = default;
        SceneDb.CamBehavior[cam.Index] = 0;
        SceneDb.Cameras.Free(cam);
        _destroyed++;
    }

    /// <summary>Attach the camera to a scene (<c>Camera::AttachToScene</c>, which the engine routes through
    /// <c>Gob::SetScene</c>). Zero detaches. The shell's scene word follows: <c>Camera::UpscaleViewport</c>
    /// reads the scene's name through it.</summary>
    public static void AttachToScene(DbId cam, DbId scene)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        SceneDb.CamScene[cam.Index] = scene;
        nint shell = SceneDb.CamFacade[cam.Index];
        if (shell != 0)
            new CX.Engine.Camera(shell).Scene = new CX.Engine.Scene(SceneDb.Scenes.Valid(scene) ? SceneDb.SceneFacade[scene.Index] : 0);
        SceneDb.TouchCam(cam, Dirty.Tree);
    }

    /// <summary>Set the camera's position, keeping the previous one (<c>GetPreviousPosition</c>, which the
    /// engine answers off its own saved field and the motion-vector pass reads).</summary>
    public static void SetPosition(DbId cam, Vector3 p)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        int i = cam.Index;
        SceneDb.CamPos[i] = p;
        // ...and the sub-object record with it, because that is what every reader takes as the authority.
        DbId pobj = SceneDb.CamObj[i];
        if (SceneDb.Objects.Valid(pobj))
        {
            SceneDb.ObjPos[pobj.Index] = p;
            // ...and the facade's own Camera+0xb0 words: a game-side behaviour's Control (the server list's
            // CAurBehaviorSubwindow) is followed by a pose read BACK from the shell (DbAnim.SyncFromShell), and
            // stale words there put the list camera at the origin, every row behind its near plane.
            nint sh = SceneDb.ObjFacade[pobj.Index];
            if (sh != 0) { float* w = (float*)(sh + Seam.SeamLayout.GobPos); w[0] = p.X; w[1] = p.Y; w[2] = p.Z; }
            SceneDb.Touch(pobj, Dirty.Xform);
        }
        SceneDb.CamVolumeSet[i] = false;
        SceneDb.TouchCam(cam, Dirty.Xform);
    }

    /// <summary>Latch every camera's previous position, once per frame. The engine keeps it at
    /// <c>Camera+0xcc</c> and writes it per FRAME, not per <c>SetPosition</c> -- so a camera the game moves
    /// three times in one frame still reports where it was last frame, which is what a motion vector means.
    /// Saving it inside the setter (as this first did) made "previous" mean "one setter ago".</summary>
    internal static void NewFrame()
    {
        for (int i = 1; i < SceneDb.Cameras.High; i++)
            if (SceneDb.Cameras.LiveAt(i)) { SceneDb.CamPrevPos[i] = PosAt(i); PublishShellAt(i); }
    }

    /// <summary>Republish one camera's place NOW, for a writer whose answer the engine-view readers
    /// (nameplates, the aim assist's projector, every <c>Camera::GetPosition</c>) must see in the frame it was
    /// written -- the camera-feel layer's pose rewrite. The per-frame walk alone leaves them a frame behind,
    /// which they must never be.</summary>
    internal static void Publish(DbId cam)
    {
        if (SceneDb.Cameras.Valid(cam)) PublishShellAt(cam.Index);
    }

    /// <summary>Copy the camera's place into its facade SHELL, at the Camera-relative offsets the engine keeps
    /// it in: <c>+0xb0</c>/<c>+0xbc</c>, which ARE the gob half's <c>+0xa8</c>/<c>+0xb4</c> at <c>+8</c>. The
    /// game, and every CX reader that goes through an engine view, takes the shell's word. Per FRAME and not in
    /// the setters: the game moves the world camera through its <c>CAurObject</c>, never by that setter.</summary>
    private static unsafe void PublishShellAt(int i)
    {
        nint shell = SceneDb.CamFacade[i];
        if (shell == 0) return;
        var cam = new Camera(shell);
        cam.Pos = PosAt(i);
        DbId obj = SceneDb.CamObj[i];
        Quaternion q = SceneDb.Objects.Valid(obj) ? SceneDb.ObjRot[obj.Index] : SceneDb.CamRot[i];
        // W-FIRST in memory, which is how Gob keeps a quaternion and how Facade.SetXform writes one: Quat's
        // own field order, so the conversion is the store.
        cam.Rot = q;
    }

    public static void SetOrientation(DbId cam, Quaternion q)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        SceneDb.CamRot[cam.Index] = q;
        DbId robj = SceneDb.CamObj[cam.Index];
        if (SceneDb.Objects.Valid(robj))
        {
            SceneDb.ObjRot[robj.Index] = q;
            nint sh = SceneDb.ObjFacade[robj.Index];
            if (sh != 0) { float* w = (float*)(sh + Seam.SeamLayout.GobRot); w[0] = q.W; w[1] = q.X; w[2] = q.Y; w[3] = q.Z; }
            SceneDb.Touch(robj, Dirty.Xform);
        }
        SceneDb.CamVolumeSet[cam.Index] = false;
        SceneDb.TouchCam(cam, Dirty.Xform);
    }

    /// <summary><c>Camera::SetViewPort</c> @0x5a38a0 exactly: a non-positive extent is clamped to 1, which is
    /// the reason the world camera's 0x0 rect is a reliable discriminator.</summary>
    public static void SetViewPort(DbId cam, float x, float y, float w, float h)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        int i = cam.Index;
        SceneDb.CamViewX[i] = x;
        SceneDb.CamViewY[i] = y;
        SceneDb.CamViewW[i] = w <= 0f ? 1f : w;
        SceneDb.CamViewH[i] = h <= 0f ? 1f : h;
        SceneDb.CamVolumeSet[i] = false;
        SceneDb.TouchCam(cam, Dirty.Xform);
    }

    public static void SetClipDist(DbId cam, float near, float far)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        SceneDb.CamNear[cam.Index] = near;
        SceneDb.CamFar[cam.Index] = far;
        SceneDb.CamVolumeSet[cam.Index] = false;
        SceneDb.TouchCam(cam, Dirty.Xform);
    }

    /// <summary>The view angle a camera has before anything sets one -- the ENGINE's own constructor default of
    /// 45.0 at <c>Camera+0x4c4</c> (<c>Camera::Camera</c> @0x5a42c0), not zero, and nothing in the game calls
    /// <c>SetViewAngle</c> on the world camera. At 0 <c>BuildView</c> takes the ORTHO branch, an ortho
    /// projection fails <c>AlloyLights.TryPerspective</c>, no light rig builds and every lit part is skipped.</summary>
    public const float DefaultViewAngle = 45f;

    public static void SetViewAngle(DbId cam, float degrees)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        SceneDb.CamViewAngle[cam.Index] = degrees;
        WriteShellViewAngle(cam, degrees);
        SceneDb.CamVolumeSet[cam.Index] = false;
        SceneDb.TouchCam(cam, Dirty.Xform);
    }

    // ---- the view ------------------------------------------------------------------------------------

    /// <summary>The rect <c>Camera::MapToScreen</c> @0x5a3d60 and the pick segment project through: the camera's
    /// own when it has one, else the whole draw viewport, divided by the game's UI scale when that is not 1.
    /// A view's DRAW rect is <see cref="BuildView"/>'s, from <c>UpscaleViewport</c>'s answer.</summary>
    public static void Rect(DbId cam, float vpX, float vpY, float vpW, float vpH, float guiScale,
                            out float x, out float y, out float w, out float h)
    {
        x = vpX; y = vpY; w = vpW; h = vpH;
        if (!SceneDb.Cameras.Valid(cam)) return;
        int i = cam.Index;
        float cw = SceneDb.CamViewW[i], ch = SceneDb.CamViewH[i];
        // The engine's own test is "EITHER extent is zero" (Camera::MapToScreen @0x5a3d60), not "both".
        if (cw != 0f || ch != 0f) { x = SceneDb.CamViewX[i]; y = SceneDb.CamViewY[i]; w = cw; h = ch; }
        // ...and the UI-scale shrink is applied AFTER that choice, to whichever rect was taken -- the engine
        // puts it past the GetViewPort fallback's label, so a camera with no rect of its own is scaled too.
        if (MathF.Abs(guiScale - 1f) < UpscaleEpsilon) return;
        x += (w - w / guiScale) * 0.5f;
        y += (h - h / guiScale) * 0.5f;
        w /= guiScale;
        h /= guiScale;
    }

    /// <summary>Where this camera actually is. In the engine a <c>Camera</c> IS-A <c>Gob</c> and there is ONE
    /// position; the database splits the camera record from the <c>CAurObject</c> sub-object record the shell
    /// carries at +8, and the game moves the camera through that sub-object. So the sub-object record is the
    /// authority whenever the camera has one, and <see cref="SetPosition"/> writes it.</summary>
    internal static Vector3 PosOf(DbId cam) => PosAt(cam.Index);

    /// <inheritdoc cref="PosOf"/>
    /// <param name="i">The camera's record index, for the per-frame walk that has no handle.</param>
    internal static Vector3 PosAt(int i)
    {
        DbId obj = SceneDb.CamObj[i];
        return SceneDb.Objects.Valid(obj) ? SceneDb.ObjPos[obj.Index] : SceneDb.CamPos[i];
    }

    /// <inheritdoc cref="PosOf"/>
    internal static Quaternion RotOf(DbId cam)
    {
        DbId obj = SceneDb.CamObj[cam.Index];
        return SceneDb.Objects.Valid(obj) ? SceneDb.ObjRot[obj.Index] : SceneDb.CamRot[cam.Index];
    }

    /// <summary>This camera's view angle, taking the SHELL's own word as the authority: the game layer reaches
    /// <c>Camera+0x4c4</c> by literal offset as well as through <c>SetViewAngle</c>, and such a write lands in
    /// the layout-compatible shell, never in the column. An angle of 0 is <c>BuildView</c>'s ORTHO branch,
    /// which <c>TryPerspective</c> refuses, so nothing lights. The slot setter writes both, so the two agree.</summary>
    internal static unsafe float ViewAngleOf(DbId cam)
    {
        int i = cam.Index;
        nint shell = SceneDb.CamFacade[i];
        if (shell != 0)
        {
            float shellAngle = new Camera(shell).ViewAngle;
            // A sane view angle only: the shell is game-written memory, and a NaN or an out-of-range value
            // must not become a projection. Anything else falls back to the column.
            if (shellAngle > 0f && shellAngle < 180f) return shellAngle;
        }
        return SceneDb.CamViewAngle[i];
    }

    private static unsafe void WriteShellViewAngle(DbId cam, float degrees)
    {
        nint shell = SceneDb.CamFacade[cam.Index];
        if (shell != 0) new Camera(shell).ViewAngle = degrees;
    }

    /// <summary><c>Camera::UpscaleViewport</c> @0x5a44b0's answer for one view at the game's UI scale: the
    /// rewritten rect (floats 0..3), the placement mode (4), the draw offset (5/6), the pick offset (7/8) and
    /// the scale (9). <see cref="Valid"/> is the function's own return; default is "not upscaled".</summary>
    internal struct ViewUpscale
    {
        internal bool Valid;
        internal float X, Y, W, H, Mode, OffX, OffY, PickX, PickY, Scale;
    }

    /// <summary><c>Camera::ModifyGUIMatrix</c> @0x5a61e0's gate: modes 1 to 7 get the GUI model matrix.</summary>
    internal static bool HasGuiModel(in ViewUpscale up) => up.Valid && up.Mode is 1f or 2f or 3f or 4f or 5f or 6f or 7f;

    /// <summary><c>Camera::ModifyGUIMatrix</c>'s MODEL matrix, <c>Scale(s, s, 1)</c> then
    /// <c>Translate(-off / 100)</c>: what makes a GUI view's content grow with the UI scale.</summary>
    internal static Matrix4x4 GuiModel(in ViewUpscale up)
        => Matrix4x4.CreateTranslation(-up.OffX / 100f, -up.OffY / 100f, 0f)
         * Matrix4x4.CreateScale(up.Scale, up.Scale, 1f);

    /// <summary>This camera's whole view state -- what <c>Camera::RenderScene</c> derives before it renders:
    /// viewport, aspect, projection (perspective or the ortho branch a view angle of 0 takes), view matrix and
    /// depth range, with <paramref name="up"/>'s rect and ortho box applied. The GUI model term is the
    /// caller's, after the view volume, as <c>ModifyGUIMatrix</c> runs after the engine builds it.</summary>
    internal static bool BuildView(DbId cam, float vpX, float vpY, float vpW, float vpH,
                                 int screenW, int screenH, in ViewUpscale up, out AlloyView v)
    {
        v = default;
        if (!SceneDb.Cameras.Valid(cam)) return false;
        int i = cam.Index;
        float rx, ry, rw, rh, aspect, extentH;
        if (SceneDb.CamViewW[i] == 0f && SceneDb.CamViewH[i] == 0f)
        {
            // No rect of its own: the view keeps the draw rect, and an upscale changes only the ortho box.
            rx = vpX; ry = vpY; rw = vpW; rh = vpH;
            if (rw <= 0f || rh <= 0f) return false;
            if (up.Valid && up.W > 0f && up.H > 0f) { aspect = up.W / up.H; extentH = up.H * up.Scale; }
            else { aspect = rw / rh; extentH = rh; }
        }
        else
        {
            if (up.Valid) { rx = up.X; ry = up.Y; rw = up.W; rh = up.H; }
            else { rx = SceneDb.CamViewX[i]; ry = SceneDb.CamViewY[i]; rw = SceneDb.CamViewW[i]; rh = SceneDb.CamViewH[i]; }
            if (rw <= 0f || rh <= 0f) return false;
            aspect = rw / rh; extentH = rh;
        }
        // The engine skips a view that lies wholly off the screen (Camera.c:3031-3037: it ends the scene,
        // bumps the external-animate counter and returns without rendering). A panel dragged off the edge is
        // the case, and drawing it would cost a full view for nothing.
        if (screenW > 0 && screenH > 0
            && (rx >= screenW || ry >= screenH || rx + rw <= 0f || ry + rh <= 0f)) return false;
        v.X = (int)rx; v.Y = (int)ry; v.W = (int)rw; v.H = (int)rh;
        v.Aspect = aspect;
        v.Near = SceneDb.CamNear[i];
        v.Far = SceneDb.CamFar[i];
        v.Fovy = ViewAngleOf(cam);
        v.Perspective = v.Fovy > 0f;
        float unit = extentH * OrthoUnitsPerPixel;
        v.Proj = v.Perspective ? AlloyCamera.Perspective(v.Fovy, v.Aspect, v.Near, v.Far)
                               : AlloyCamera.Ortho(0f, v.Aspect * unit, 0f, unit, v.Near, v.Far);
        AlloyCamera.ViewportScales(v.X, v.Y, v.W, v.H, screenW, screenH, out float xs, out float ys);
        v.View = AlloyCamera.ViewOf(PosOf(cam), RotOf(cam), xs, ys);
        ClipToScreen(ref v, screenW, screenH);
        DepthRange(SceneDb.CamScene[i], v.Fovy, out v.DepthNear, out v.DepthFar);
        v.Valid = true;
        _views++;
        return true;
    }

    /// <summary><c>GLRender::SetViewPortLight</c> @0x5cb4b0: a rect past the screen's right or top edge is cut
    /// back to it, and the view's x/y scale (already in <c>v.View</c>) squeezes the projection into the cut
    /// rect. The character sheet's resize viewport is (0, y, 8192, h) -- drawn uncut, it was scaled twice.</summary>
    internal static void ClipToScreen(ref AlloyView v, int screenW, int screenH)
    {
        if (screenW <= 0 || screenH <= 0) return;
        if (v.Y + v.H > screenH && screenH - v.Y > 0) v.H = screenH - v.Y;
        if (v.X + v.W > screenW && screenW - v.X > 0) v.W = screenW - v.X;
    }

    /// <summary><c>Camera::RenderScene</c>'s depth decision over a DB scene's NAME, the same test
    /// <see cref="AlloyCamera.DepthRange"/> makes over the engine's: a <c>sceneGUI</c> scene -- or any camera
    /// whose view angle is exactly 0 -- shares one depth buffer split by range, with five named overlays in
    /// front of the rest.</summary>
    public static void DepthRange(DbId scene, float fovy, out float near, out float far)
    {
        string name = SceneDb.Scenes.Valid(scene) ? SceneDb.SceneName[scene.Index] : "";
        bool gui = fovy == 0f || name.StartsWith("sceneGUI", StringComparison.Ordinal);
        if (!gui) { near = 0f; far = 1f; return; }
        bool front = name.StartsWith("sceneGUI_MouseOverlay", StringComparison.Ordinal)
                  || name.StartsWith("sceneGUI_Overlay", StringComparison.Ordinal)
                  || name.StartsWith("sceneGUI_pnl_vidopts", StringComparison.Ordinal)
                  || name.StartsWith("sceneGUI_PNL_CONNECTSTAT", StringComparison.Ordinal)
                  || name.StartsWith("sceneGUI_PNL_UTIL_OK", StringComparison.Ordinal);
        near = front ? GuiFrontNear : GuiBackNear;
        far = front ? GuiFrontFar : GuiBackFar;
    }

    /// <summary>The depth slices <c>Camera::RenderScene</c> @0x4a82e9 hands a view.</summary>
    public const float GuiFrontNear = 0f, GuiFrontFar = 0.02f, GuiBackNear = 0.021f, GuiBackFar = 0.1f;

    /// <summary>Fill the camera's world-space view volume from the view just built and latch
    /// <c>ViewVolumeSet</c>: the planes in the engine's own order -- near, far, then the four sides -- out of
    /// <c>view * proj</c>. <b>The engine's convention is <c>dot(n, p) + d &gt; 0</c> is OUTSIDE</b> (measured at
    /// <c>Camera::HitCheckViewVolumeNearest</c> @0x5a4060), so every <see cref="CullPass.Planes"/> plane is negated.</summary>
    internal static void SetViewVolume(DbId cam, in AlloyView v)
    {
        if (!SceneDb.Cameras.Valid(cam) || !v.Valid) return;
        Matrix4x4 m = v.View * v.Proj;
        float* p = stackalloc float[24];
        CullPass.Planes((float*)&m, p, PosOf(cam));
        int b = cam.Index * SceneDb.ViewPlanes;
        // CullPass emits left, right, bottom, top, near, far and inside-POSITIVE; the ENGINE's list -- which
        // GetViewVolume(i) must index, because the game's callers index it the engine's way -- is near, far,
        // then the four sides, inside-NEGATIVE. So: permute, then negate.
        for (int k = 0; k < SceneDb.ViewPlanes; k++)
        {
            int src = Order[k] * 4;
            SceneDb.CamVolume[b + k] = new Plane(-p[src], -p[src + 1], -p[src + 2], -p[src + 3]);
        }
        SceneDb.CamVolumeSet[cam.Index] = true;
    }

    /// <summary>The engine's view-volume plane order, as indices into <see cref="CullPass.Planes"/>' output.</summary>
    private static readonly int[] Order = { 4, 5, 0, 1, 2, 3 };

    /// <summary>One plane of the camera's view volume (<c>Camera::GetViewVolume(int)</c>). Out of range or a
    /// volume this frame's view never filled answers the zero plane, which is what the engine's own
    /// uninitialised list holds.</summary>
    public static Plane ViewVolume(DbId cam, int index)
    {
        if (!SceneDb.Cameras.Valid(cam) || (uint)index >= SceneDb.ViewPlanes) return default;
        return SceneDb.CamVolume[cam.Index * SceneDb.ViewPlanes + index];
    }

    // ---- the three projections the game asks for -----------------------------------------------------

    /// <summary><c>Camera::MapToScreen(Vector, bool)</c> @0x5a3d60, term for term: eye space is -Z-forward so
    /// <c>t = -1/P.z</c>, and the VERTICAL half-extent scales BOTH axes (square-pixel). Unnormalised it is
    /// <b>pixels, bottom-left origin, y up</b> in the camera's rect; <paramref name="normalized"/> is
    /// <c>x/w</c>, <c>1 - y/h</c>; a view angle &lt;= 0 returns zero. <c>ponytail:</c> z is nobody's term yet.</summary>
    public static Vector3 MapToScreen(DbId cam, Vector3 world, bool normalized,
                                      float vpX, float vpY, float vpW, float vpH, float guiScale)
    {
        if (!SceneDb.Cameras.Valid(cam)) return default;
        int i = cam.Index;
        float fov = ViewAngleOf(cam);
        if (fov <= 0f) return default;
        Rect(cam, vpX, vpY, vpW, vpH, guiScale, out float rx, out float ry, out float rw, out float rh);
        Vector3 p = Vector3.Transform(world - PosOf(cam), Quaternion.Conjugate(RotOf(cam)));
        if (p.Z == 0f) return default;   // the engine's own denominator test: equality with zero, no sign test
        float t = -1f / p.Z;
        float half = rh * 0.5f;
        float scale = half / MathF.Tan(fov * HalfFovToRad);
        float sx = p.X * t * scale + rw * 0.5f + rx;
        float sy = p.Y * t * scale + half + ry;
        if (normalized) { sx /= rw; sy = 1f - sy / rh; }
        return new Vector3(sx, sy, p.Z);
    }

    /// <summary>The segment a screen pick casts through this camera, in world space (<c>Camera::HitCheck</c>
    /// @0x5a5e10's two arms); false means the pixel is outside the rect. The ORTHO arm is every GUI panel's:
    /// the pixel goes back through <paramref name="up"/> first, then one pixel is 0.01 GUI units along -Z.</summary>
    internal static bool PickSegment(DbId cam, int px, int py, float vpX, float vpY, float vpW, float vpH,
                                   in ViewUpscale up, out Vector3 from, out Vector3 to)
    {
        from = default; to = default;
        if (!SceneDb.Cameras.Valid(cam)) return false;
        int i = cam.Index;
        Vector3 pos = PosOf(cam);
        float near = SceneDb.CamNear[i], far = SceneDb.CamFar[i];
        if (ViewAngleOf(cam) > 0f)
        {
            Vector3 dir = MouseDir(cam, px, py, vpX, vpY, vpW, vpH);
            if (dir == Vector3.Zero) return false;
            from = pos + dir * near;
            to = pos + dir * far;
            return true;
        }
        float rx, ry, rw, rh, grow = 1f;
        float cw = SceneDb.CamViewW[i], ch = SceneDb.CamViewH[i];
        if (cw == 0f || ch == 0f) { rx = vpX; ry = vpY; rw = vpW; rh = vpH; }
        else { rx = SceneDb.CamViewX[i]; ry = SceneDb.CamViewY[i]; rw = cw; rh = ch; if (up.Valid) grow = up.Scale; }
        if (rw <= 0f || rh <= 0f) return false;
        float sx = px, sy = py;
        // Camera::ScreenToUpscaledScene: the pixel back through ModifyGUIMatrix, by the PICK offset.
        if (HasGuiModel(in up)) { sx = sx / up.Scale + up.PickX; sy = sy / up.Scale + up.PickY; }
        float cx = sx - rx, cy = sy - ry;
        if (cx < 0f || cy < 0f || cx >= rw * grow || cy >= rh * grow) return false;
        float wx = (cx + 0.5f) * OrthoUnitsPerPixel + pos.X;
        float wy = (cy + 0.5f) * OrthoUnitsPerPixel + pos.Y;
        from = new Vector3(wx, wy, pos.Z - near);
        to = new Vector3(wx, wy, pos.Z - near - far);
        return true;
    }

    /// <summary><c>Camera::MouseDir(int, int)</c> @0x5a3bc0, term for term: the normalised WORLD-space ray
    /// through a pixel of this camera's rect (the same screen space <see cref="MapToScreen"/> returns
    /// unnormalised), <c>((px - x - w/2) * t/(h/2), (py - y - h/2) * t/(h/2), -1)</c> before the camera
    /// rotation -- both axes divide by the VERTICAL half-extent. No UI-scale division here, as in the engine.</summary>
    public static Vector3 MouseDir(DbId cam, int px, int py, float vpX, float vpY, float vpW, float vpH)
    {
        if (!SceneDb.Cameras.Valid(cam)) return default;
        int i = cam.Index;
        float rx, ry, rw, rh;
        float cw = SceneDb.CamViewW[i], ch = SceneDb.CamViewH[i];
        if (cw == 0f && ch == 0f) { rx = vpX; ry = vpY; rw = vpW; rh = vpH; }
        else { rx = SceneDb.CamViewX[i]; ry = SceneDb.CamViewY[i]; rw = cw; rh = ch; }
        if (rh <= 0f) return default;
        float t = MathF.Tan(ViewAngleOf(cam) * HalfFovToRad);
        float half = rh * 0.5f;
        var d = new Vector3((px - rx - rw * 0.5f) * t / half, (py - ry - half) * t / half, -1f);
        // RotOf, not the camera's own column: the engine rotates by `this + 0xbc`, which is Gob+0xb4 on the
        // camera's CAurObject half, and the game turns the world camera through that gob rather than through
        // Camera::SetOrientation -- so the column stays identity all session. An unrotated ray cast straight
        // down world -Z from the eye, which is why click-to-move walked nowhere near the cursor and a hover
        // never found a creature to highlight.
        d = Vector3.Transform(d, RotOf(cam));
        float len = d.Length();
        return len > 0f ? d / len : d;
    }

    /// <summary>The camera's forward axis in world space -- the rotation applied to (0, 0, -1). The one term
    /// every caller that wants "is this in front of me" needs, and the discriminator
    /// <see cref="MapToScreen"/> deliberately does NOT apply (the engine's own projection mirrors a point
    /// behind the camera onto the screen, and CX reproduces that so a plate does not move).</summary>
    public static Vector3 Forward(DbId cam)
        => SceneDb.Cameras.Valid(cam)
         ? Vector3.Transform(new Vector3(0f, 0f, -1f), RotOf(cam))
         : default;

    /// <summary>This camera's follow behaviour, or null: yaw, pitch and distance live on it, as the engine's
    /// <c>Camera::GetYaw</c>/<c>GetPitch</c>/<c>GetDist</c> read them. The type test is not optional: a
    /// <c>CAurBehaviorSubwindow</c> is far smaller, so follow fields read off it over-read another object.</summary>
    public static CAurBehaviorCameraFollow BehaviorFollow(DbId cam)
    {
        if (!SceneDb.Cameras.Valid(cam)) return default;
        nint b = SceneDb.CamBehavior[cam.Index];
        if (b == 0) return default;
        var beh = new CAurBehavior(b);
        if (beh.AsBehaviorCameraFollowFn == IntPtr.Zero) return default;
        return beh.AsBehaviorCameraFollow();
    }

    /// <inheritdoc cref="DbScene.TakeCounters"/>
    internal static (long, long, long, long) TakeCounters() => (_created, _destroyed, _refused, _views);

    /// <inheritdoc cref="DbScene.TakeCounters"/>
    internal static void PutCounters((long, long, long, long) c)
        => (_created, _destroyed, _refused, _views) = c;

    internal static string Stats(int _)
        => $"cameras: {SceneDb.Cameras.Live} live ({_created} created, {_destroyed} destroyed), {_views} views built"
         + (_refused > 0 ? $", {_refused} REFUSED (table full)" : "");
}
