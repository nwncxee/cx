using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>The <c>CAurCamera</c> interface, implemented by CX: 33 interface slots (<c>_ZTV10CAurCamera</c>
/// @0x186d628, Windows rva 0xd82d00) plus the 8 <c>Camera</c>-only slots the primary sub-vtable carries. A
/// <c>Camera</c> is <c>: CAurCamera, Gob</c>, the <c>Gob</c> vptr at +8; slot 3 <c>RenderScene</c> RECORDS the
/// view, and the renderer draws the frame's whole list at the present boundary (ADR 0028 section 5).</summary>
internal static unsafe class CameraSlots
{
    /// <summary>Name each camera slot the first time the game calls it: which calls actually arrive is a fact
    /// no amount of reading the slot table can establish. Once per slot.</summary>
    private static readonly System.Collections.Generic.HashSet<string> _traced = new();
    private static void Trace(string slot)
    {
        if (!Renderer.On || _traced.Count > 64 || !_traced.Add(slot)) return;
        KnobGroup.Log?.Info($"CameraSlots: [db] first call -> {slot}");
    }

    /// <summary>Slots in the <c>CAurCamera</c> interface, and in <c>Camera</c>'s primary table on Linux.</summary>
    internal const int Count = 33, PrimaryCount = 41;

    private static ExtenderLog? _log;
    private static long _calls, _nullCam, _views;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>The <c>CAurCamera</c> interface table (33 slots), Linux slot index. The seven by-value slots
    /// (7-12, 32) are declared PER OS: SysV returns <c>Vector</c>/<c>Quaternion</c>/<c>Plane</c> in xmm0:xmm1
    /// with no hidden pointer, Win64 takes <c>(this=rcx, sret=rdx, ...)</c>. The boundary plane is
    /// <see cref="AurPlane"/>, four plain floats, never the <c>[Intrinsic]</c>-backed <c>Plane</c>.</summary>
    internal static readonly IntPtr[] Table = Build();

    /// <summary>The 8 extra slots <c>Camera</c>'s PRIMARY sub-vtable carries on Linux (indices 33..40), as
    /// Linux-indexed bodies. On Windows only <c>SetFadePlanes</c> (Linux 37) is a new primary virtual (Windows
    /// primary slot 32); the other seven live in Camera's <c>Gob</c> vftable as <c>adjustor{8}</c> thunks,
    /// because GCC duplicates a secondary-base override into the primary table and MSVC does not.</summary>
    internal static readonly IntPtr[] PrimaryExtra = BuildExtra();

    private static IntPtr[] Build()
    {
        var t = new IntPtr[Count];
        t[0] = (IntPtr)(delegate* unmanaged<nint, void>)&Dtor;
        t[1] = (IntPtr)(delegate* unmanaged<nint, void>)&DtorDeleting;
        t[2] = (IntPtr)(delegate* unmanaged<nint, nint>)&AsCAurObject;
        t[3] = (IntPtr)(delegate* unmanaged<nint, void>)&RenderScene;
        t[4] = (IntPtr)(delegate* unmanaged<nint, float, void>)&Animate;
        t[5] = (IntPtr)(delegate* unmanaged<nint, byte*, float, int, float, int>)&PlayAnimation;
        t[6] = (IntPtr)(delegate* unmanaged<nint, nint, int>)&AttachToScene;
#if WINDOWS
        t[7] = (IntPtr)(delegate* unmanaged<nint, Vec3*, Vec3*, nint>)&SetPositionWin;
        t[8] = (IntPtr)(delegate* unmanaged<nint, Quat*, Quat*, nint>)&SetOrientationWin;
        t[9] = (IntPtr)(delegate* unmanaged<nint, Vec3*, nint>)&GetPositionWin;
        t[10] = (IntPtr)(delegate* unmanaged<nint, Quat*, nint>)&GetOrientationWin;
        t[11] = (IntPtr)(delegate* unmanaged<nint, Vec3*, Vec3*, byte, nint>)&MapToScreenWin;
        t[12] = (IntPtr)(delegate* unmanaged<nint, Vec3*, int, int, nint>)&MouseDirWin;
#else
        t[7] = (IntPtr)(delegate* unmanaged<nint, Vec3, Vec3>)&SetPositionSysV;
        t[8] = (IntPtr)(delegate* unmanaged<nint, Quat, Quat>)&SetOrientationSysV;
        t[9] = (IntPtr)(delegate* unmanaged<nint, Vec3>)&GetPositionSysV;
        t[10] = (IntPtr)(delegate* unmanaged<nint, Quat>)&GetOrientationSysV;
        t[11] = (IntPtr)(delegate* unmanaged<nint, Vec3, byte, Vec3>)&MapToScreenSysV;
        t[12] = (IntPtr)(delegate* unmanaged<nint, int, int, Vec3>)&MouseDirSysV;
#endif
        t[13] = (IntPtr)(delegate* unmanaged<nint, int, int, Vec3*, nint, int>)&HitCheckScreen;
        t[14] = (IntPtr)(delegate* unmanaged<nint, float, float, float, float, void>)&SetViewPort;
        t[15] = (IntPtr)(delegate* unmanaged<nint, float>)&GetViewPortHeight;
        t[16] = (IntPtr)(delegate* unmanaged<nint, float*, float*, void>)&GetClipDist;
        t[17] = (IntPtr)(delegate* unmanaged<nint, float, float, void>)&SetClipDist;
        t[18] = (IntPtr)(delegate* unmanaged<nint, float, void>)&SetViewAngle;
        t[19] = (IntPtr)(delegate* unmanaged<nint, float>)&GetViewAngle;
        t[20] = (IntPtr)(delegate* unmanaged<nint, Vec3*, Vec3*, Vec3*, int*, float>)&HitCheckViewVolumeNearest;
        t[21] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&SetFollow;
        t[22] = (IntPtr)(delegate* unmanaged<nint, byte*, void>)&BehaviorMessage;
        t[23] = (IntPtr)(delegate* unmanaged<nint, float>)&GetYaw;
        t[24] = (IntPtr)(delegate* unmanaged<nint, float>)&GetPitch;
        t[25] = (IntPtr)(delegate* unmanaged<nint, float>)&GetDist;
        t[26] = (IntPtr)(delegate* unmanaged<nint, nint>)&AsAurCamera;
        t[27] = (IntPtr)(delegate* unmanaged<nint, nint, byte*, int, nint>)&AttachToObject;
        t[28] = (IntPtr)(delegate* unmanaged<nint, nint, nint, int, nint>)&AttachToPart;
        t[29] = (IntPtr)(delegate* unmanaged<nint, nint, void>)&SetBehavior;
        t[30] = (IntPtr)(delegate* unmanaged<nint, int, nint>)&GetBehavior;
        t[31] = (IntPtr)(delegate* unmanaged<nint, int>)&ViewVolumeSet;
#if WINDOWS
        t[32] = (IntPtr)(delegate* unmanaged<nint, AurPlane*, int, nint>)&GetViewVolumeWin;
#else
        t[32] = (IntPtr)(delegate* unmanaged<nint, int, AurPlane>)&GetViewVolumeSysV;
#endif
        return t;
    }

    private static IntPtr[] BuildExtra()
    {
        var t = new IntPtr[PrimaryCount - Count];
        t[0] = (IntPtr)(delegate* unmanaged<nint, nint>)&AsCAurCamera;                        // Linux 33
        t[1] = (IntPtr)(delegate* unmanaged<nint, byte, void>)&Render;                        // Linux 34
#if WINDOWS
        t[2] = (IntPtr)(delegate* unmanaged<nint, Vec3*, nint>)&GetPreviousPositionWin;       // Linux 35
#else
        t[2] = (IntPtr)(delegate* unmanaged<nint, Vec3>)&GetPreviousPositionSysV;
#endif
        t[3] = (IntPtr)(delegate* unmanaged<nint, VectorArg, VectorArg, Vec3*, nint, nint>)&HitCheckRay;   // Linux 36
        t[4] = (IntPtr)(delegate* unmanaged<nint, AurPlane*, AurPlane*, void>)&SetFadePlanes;       // Linux 37
        t[5] = (IntPtr)(delegate* unmanaged<nint, nint>)&AsCamera;                            // Linux 38
        t[6] = (IntPtr)(delegate* unmanaged<nint, Vec3*, Vec3*, void>)&GetMinimumBoundingBox; // Linux 39
        t[7] = (IntPtr)(delegate* unmanaged<nint, Vec3*, float*, void>)&GetMinimumBoundingSphere; // Linux 40
        return t;
    }

    private static DbId Id(nint self)
    {
        _calls++;
        DbId id = Facade.CameraId(self);
        if (!SceneDb.Cameras.Valid(id)) { _nullCam++; return default; }
        return id;
    }

    // ---- lifetime and the downcasts ------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static void Dtor(nint self) => Die(self);

    [UnmanagedCallersOnly]
    private static void DtorDeleting(nint self) => Die(self);

    /// <summary>The camera destroy edge. Shared body, because an <c>[UnmanagedCallersOnly]</c> static cannot
    /// be called from managed code and so neither destructor slot can forward to the other.</summary>
    private static void Die(nint self)
    {
        DbId id = Facade.CameraId(self);
        DbCamera.Destroy(id);
        Facade.Retire(self);
    }

    /// <summary>Slot 2, <c>Camera::AsCAurObject</c>: the <c>Gob</c> half of the camera, which the game also
    /// reaches as the literal <c>camera + 8</c>. Returning the same address is what keeps the two routes
    /// consistent.</summary>
    [UnmanagedCallersOnly]
    private static nint AsCAurObject(nint self) => self == 0 ? 0 : new Camera(self).GobSub;

    /// <summary>Slot 26, <c>CAurCamera::AsAurCamera</c> -- a real <c>CAurCamera</c> body the concrete class does
    /// not override, so it is the identity.</summary>
    [UnmanagedCallersOnly]
    private static nint AsAurCamera(nint self) => self;

    /// <summary>Linux primary slot 33, <c>Camera::AsCAurCamera</c> (the duplicate GCC puts in the primary
    /// table): also the identity.</summary>
    [UnmanagedCallersOnly]
    private static nint AsCAurCamera(nint self) => Base(self);

    /// <summary>Linux primary slot 38, <c>Camera::AsCamera</c>: the concrete downcast. The game uses the
    /// <c>AsXxx</c> slots in place of <c>dynamic_cast</c> (there is no <c>dynamic_cast</c> on any Aurora type),
    /// and a CX camera IS the concrete camera, so this is the identity too.</summary>
    [UnmanagedCallersOnly]
    private static nint AsCamera(nint self) => Base(self);

    /// <summary>The camera block these two downcasts must answer with. Both slots also sit in the GOB-side
    /// table (<c>SeamTable.CameraGob</c>), where the engine calls them with the <c>+8</c> sub-object as
    /// <c>this</c> and <c>CAurBehaviorSubwindow::Control</c> @0x68b720 then calls vtable <c>+0x70</c> on the
    /// result with NO null check, so answering <c>self</c> there would land on a <c>Gob</c> slot instead.</summary>
    private static nint Base(nint self)
    {
        nint shell = Facade.Shell(self);
        return shell != 0 ? shell : self;
    }

    // ---- the frame ------------------------------------------------------------------------------------

    /// <summary>Slot 3, <c>Camera::RenderScene</c> -- THE frame slot. Build this camera's view from its own
    /// columns and hand it to the feed IN THE ORDER the game issued it: that order is the panel z-order, which
    /// is why the frame's view list is append-only and never sorted. Nothing draws here; the renderer draws
    /// the whole list at the present boundary, and a view with a degenerate rect is recorded invalid.</summary>
    [UnmanagedCallersOnly]
    private static void RenderScene(nint self)
    {
        Trace("RenderScene");
        DbId cam = Id(self);
        if (cam.IsNull) return;
        DbId scene = SceneDb.CamScene[cam.Index];
        if (!SceneDb.Scenes.Valid(scene)) return;   // the engine's own first test: no scene, no view
        _views++;
        // The engine's body calls Scene::Render, which is the ONLY caller of the frame's shader-runtime
        // refresh -- the world clock every owned stage animates on. This slot replaces that body, so the
        // refresh happens here or nowhere (see SceneSlots.UniformsForFrame).
        SceneSlots.UniformsForFrame(SceneDb.SceneFacade[scene.Index]);
        // The keyhole state is written by the body this slot replaces, and it must come AFTER RequestView,
        // which fills the camera's view volume (RenderSnapshot -> DbCamera.SetViewVolume): the rule needs
        // plane 0, and SetPosition/SetRotation clear the volume every time the chase camera moves.
        RenderSnapshot.RequestView(scene, cam);
        DbKeyhole.WriteCameraState(self, cam, scene);
        DbFeed.RequestView(scene, cam);
    }

    /// <summary>Linux primary slot 34, <c>Camera::Render(bool)</c> -- the <c>Gob</c> override. A camera has no
    /// geometry of its own, and the engine's body is empty.</summary>
    [UnmanagedCallersOnly]
    private static void Render(nint self, byte _) { }

    /// <summary>Slot 4, <c>Camera::Animate(float)</c>: the camera's behaviour solve. The behaviour object is
    /// the GAME's (it constructs and subclasses the engine's classes in place), so CX calls its
    /// <c>Control</c> through its own vtable -- which is what moves a chase camera.</summary>
    [UnmanagedCallersOnly]
    private static void Animate(nint self, float dt)
    {
        Trace("Animate");
        DbId cam = Id(self);
        if (cam.IsNull) return;
        DbAnimCamera.Animate(cam, dt);
    }

    /// <summary>Slot 5, <c>Camera::PlayAnimation</c>: the engine's own body is <c>return 0</c> @0x5a3960 -- a
    /// camera has no animations. Kept as the same refusal rather than a poison slot, because the engine
    /// answers it.</summary>
    [UnmanagedCallersOnly]
    private static int PlayAnimation(nint self, byte* name, float speed, int flags, float blend) => 0;

    // ---- attachment -----------------------------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static int AttachToScene(nint self, nint scene)
    {
        Trace("AttachToScene");
        DbId cam = Id(self);
        if (cam.IsNull) return 0;
        DbCamera.AttachToScene(cam, Facade.SceneId(scene));
        return 1;
    }

    /// <summary>Slot 27, <c>Camera::AttachToObject(CAurObject*, char const*, int)</c>. Returns the engine's
    /// opaque attachment handle; CX answers with the camera's own shell, which is a non-null token the game
    /// only tests against zero.</summary>
    [UnmanagedCallersOnly]
    private static nint AttachToObject(nint self, nint obj, byte* partName, int flags)
    {
        Trace("AttachToObject");
        DbId cam = Id(self);
        if (cam.IsNull) return 0;
        DbId target = Facade.ObjectId(obj);
        SceneDb.CamAttachTo[cam.Index] = target;
        SceneDb.CamAttachPart[cam.Index] = FacadeParts.PartIndexByName(target, partName);
        SceneDb.TouchCam(cam, Dirty.Xform);
        return self;
    }

    [UnmanagedCallersOnly]
    private static nint AttachToPart(nint self, nint obj, nint part, int flags)
    {
        Trace("AttachToPart");
        DbId cam = Id(self);
        if (cam.IsNull) return 0;
        SceneDb.CamAttachTo[cam.Index] = Facade.ObjectId(obj);
        SceneDb.CamAttachPart[cam.Index] = FacadeParts.PartIndex(part);
        SceneDb.TouchCam(cam, Dirty.Xform);
        return self;
    }

    /// <summary>Slot 21, <c>Camera::SetFollow(CAurObject*)</c>, which does far more than record a target:
    /// @0x5aa500 CONSTRUCTS the chase camera -- <c>operator_new(0x90)</c>, then the engine's own
    /// <c>CAurBehaviorCameraFollow(camera + 8, target)</c>, the owner being the camera's OWN <c>CAurObject</c>
    /// sub-object -- and installs it through <c>SetBehavior</c>. That behaviour places the camera; null clears.</summary>
    [UnmanagedCallersOnly]
    private static void SetFollow(nint self, nint obj)
    {
        Trace("SetFollow");
        DbId cam = Id(self);
        if (cam.IsNull) return;
        SceneDb.CamFollow[cam.Index] = Facade.ObjectId(obj);
        SceneDb.TouchCam(cam, Dirty.Xform);
        nint behavior = obj == 0 ? 0 : BehaviorSeam.NewCameraFollow(self, obj);
        SceneDb.CamBehavior[cam.Index] = behavior;
        // Three places have to agree, because three different readers ask: the camera record (what
        // GetBehavior answers), the shell's own +0x450 word (what the game reads raw), and DbAnim's table
        // (what the per-frame solver dispatches on). Setting only the first two is a behaviour that exists
        // and never runs.
        DbId camObj = SceneDb.CamObj[cam.Index];
        BehaviorSeam.Attached(behavior, camObj);
        DbAnim.SetBehavior(camObj, behavior);
    }

    // ---- behaviour: the game's own objects -----------------------------------------------------------

    /// <summary>Slot 29, <c>Camera::SetBehavior</c>. The pointer is a GAME-constructed engine-layout
    /// <c>CAurBehavior</c> subclass (0x88/0x90 bytes, the engine's constructor, the game's vptr), held raw and
    /// called through its vtable. All three readers <see cref="SetFollow"/> names must agree here too,
    /// <c>DbAnim</c>'s solver table included, or <c>GuiAttachViewport</c>'s subwindow never sets a panel rect.</summary>
    [UnmanagedCallersOnly]
    private static void SetBehavior(nint self, nint behavior)
    {
        Trace("SetBehavior");
        DbId cam = Id(self);
        if (cam.IsNull) return;
        SceneDb.CamBehavior[cam.Index] = behavior;
        DbId camObj = SceneDb.CamObj[cam.Index];
        BehaviorSeam.Attached(behavior, camObj);
        DbAnim.SetBehavior(camObj, behavior);
    }

    /// <summary>Slot 30, <c>Camera::GetBehavior(int)</c>. The engine keeps a behaviour LIST and -1 means "the
    /// last one"; a CX camera keeps one, so -1 and 0 both answer it and any other index is null -- which is
    /// what every caller tests (<c>CNWCModule::TiltCamera</c> asks for -1 and then
    /// <c>AsBehaviorCameraFollow()</c>).</summary>
    [UnmanagedCallersOnly]
    private static nint GetBehavior(nint self, int index)
    {
        Trace("GetBehavior");
        DbId cam = Id(self);
        if (cam.IsNull || (index != -1 && index != 0)) return 0;
        return SceneDb.CamBehavior[cam.Index];
    }

    /// <summary>Slot 22, <c>Camera::BehaviorMessage(char const*)</c>: forwarded to the behaviour's
    /// <c>Message</c> slot, which is how <c>StopSceneChangeHack</c> and <c>RemoveAuroraObject</c>'s
    /// "Dislodge" reach it.</summary>
    [UnmanagedCallersOnly]
    private static void BehaviorMessage(nint self, byte* msg)
    {
        Trace("BehaviorMessage");
        DbId cam = Id(self);
        if (cam.IsNull) return;
        BehaviorSeam.Message(SceneDb.CamBehavior[cam.Index], msg);
    }

    /// <summary>Slots 23/24/25, <c>GetYaw</c>/<c>GetPitch</c>/<c>GetDist</c>. The engine reads these off the
    /// camera's BEHAVIOUR (after <c>AsBehaviorCameraFollow()</c>) -- the very fields
    /// <c>CNWCModule::InterpolateYaw</c>/<c>Pitch</c>/<c>Dist</c> write directly. So does CX.</summary>
    [UnmanagedCallersOnly]
    private static float GetYaw(nint self)
    { var f = DbCamera.BehaviorFollow(Id(self)); return f.IsNull ? 0f : f.Yaw; }

    [UnmanagedCallersOnly]
    private static float GetPitch(nint self)
    { var f = DbCamera.BehaviorFollow(Id(self)); return f.IsNull ? 0f : f.Pitch; }

    [UnmanagedCallersOnly]
    private static float GetDist(nint self)
    { var f = DbCamera.BehaviorFollow(Id(self)); return f.IsNull ? 0f : f.Dist; }

    // ---- the viewport and clip block -----------------------------------------------------------------

    /// <summary><c>alloy.hitcheck=1</c> also says every CHANGE to a GUI camera's rect (budgeted): a resize bar
    /// drag and a page's first sizing are this slot, so the line tells a drag that never arrived from one
    /// that arrived with the wrong numbers.</summary>
    [UnmanagedCallersOnly]
    private static void SetViewPort(nint self, float x, float y, float w, float h)
    {
        DbId cam = Id(self);
        if (Renderer.HitCheck.Boot && _rectsSaid < RectSayMax && SceneDb.Cameras.Valid(cam)
            && (SceneDb.CamViewX[cam.Index] != x || SceneDb.CamViewY[cam.Index] != y
                || SceneDb.CamViewW[cam.Index] != w || SceneDb.CamViewH[cam.Index] != h))
        {
            _rectsSaid++;
            DbId sc = SceneDb.CamScene[cam.Index];
            _log?.Info($"[dbrect] cam={cam.Index} \"{(SceneDb.Scenes.Valid(sc) ? SceneDb.SceneName[sc.Index] : "-")}\" "
                       + $"({SceneDb.CamViewX[cam.Index]},{SceneDb.CamViewY[cam.Index]},{SceneDb.CamViewW[cam.Index]},{SceneDb.CamViewH[cam.Index]})"
                       + $" -> ({x},{y},{w},{h})");
        }
        DbCamera.SetViewPort(cam, x, y, w, h);
    }

    private const int RectSayMax = 200;
    private static int _rectsSaid;

    [UnmanagedCallersOnly]
    private static float GetViewPortHeight(nint self)
    {
        Trace("GetViewPortHeight");
        DbId cam = Id(self);
        return cam.IsNull ? 0f : SceneDb.CamViewH[cam.Index];
    }

    [UnmanagedCallersOnly]
    private static void GetClipDist(nint self, float* near, float* far)
    {
        Trace("GetClipDist");
        DbId cam = Id(self);
        if (near != null) *near = cam.IsNull ? 0f : SceneDb.CamNear[cam.Index];
        if (far != null) *far = cam.IsNull ? 0f : SceneDb.CamFar[cam.Index];
    }

    [UnmanagedCallersOnly]
    private static void SetClipDist(nint self, float near, float far) => DbCamera.SetClipDist(Id(self), near, far);

    [UnmanagedCallersOnly]
    private static void SetViewAngle(nint self, float degrees) => DbCamera.SetViewAngle(Id(self), degrees);

    [UnmanagedCallersOnly]
    private static float GetViewAngle(nint self)
    {
        Trace("GetViewAngle");
        DbId cam = Id(self);
        return cam.IsNull ? 0f : SceneDb.CamViewAngle[cam.Index];
    }

    [UnmanagedCallersOnly]
    private static int ViewVolumeSet(nint self)
    {
        Trace("ViewVolumeSet");
        DbId cam = Id(self);
        return cam.IsNull || !SceneDb.CamVolumeSet[cam.Index] ? 0 : 1;
    }

    /// <summary>Linux primary slot 37, <c>Camera::SetFadePlanes(Plane&amp;, Plane&amp;)</c> -- Windows primary
    /// slot 32, the ONE genuinely new primary virtual of the eight. By reference on both OSes, so one
    /// signature serves.</summary>
    [UnmanagedCallersOnly]
    private static void SetFadePlanes(nint self, AurPlane* a, AurPlane* b)
    {
        Trace("SetFadePlanes");
        DbId cam = Id(self);
        if (cam.IsNull) return;
        if (a != null) SceneDb.CamFadeA[cam.Index] = *a;
        if (b != null) SceneDb.CamFadeB[cam.Index] = *b;
    }

    /// <summary>Linux primary slots 39/40: a camera's bounds. It has none, so both answer a degenerate
    /// box/sphere at its position -- which is what the engine's <c>Gob</c> body produces for a gob with no
    /// geometry.</summary>
    [UnmanagedCallersOnly]
    private static void GetMinimumBoundingBox(nint self, Vec3* min, Vec3* max)
    {
        Trace("GetMinimumBoundingBox");
        DbId cam = Id(self);
        Vector3 p = cam.IsNull ? Vector3.Zero : DbCamera.PosOf(cam);
        if (min != null) { min->X = p.X; min->Y = p.Y; min->Z = p.Z; }
        if (max != null) { max->X = p.X; max->Y = p.Y; max->Z = p.Z; }
    }

    [UnmanagedCallersOnly]
    private static void GetMinimumBoundingSphere(nint self, Vec3* centre, float* radius)
    {
        Trace("GetMinimumBoundingSphere");
        DbId cam = Id(self);
        Vector3 p = cam.IsNull ? Vector3.Zero : DbCamera.PosOf(cam);
        if (centre != null) { centre->X = p.X; centre->Y = p.Y; centre->Z = p.Z; }
        if (radius != null) *radius = 0f;
    }

    // ---- hit tests: the hit stream's HitTest ---------------------------------------------------------

    /// <summary>Slot 13, <c>Camera::HitCheck(int, int, Vector*, CHitInfo*)</c>: the pick from a screen pixel,
    /// the one slot every mouse hover, click, tooltip and drop goes through, world and GUI panel alike. The
    /// segment is <see cref="DbCamera.PickSegment"/>, which keeps the engine's BOTH arms -- perspective casts
    /// <c>MouseDir</c> near to far, ORTHO (every GUI panel) a world -Z segment through the mapped pixel.</summary>
    [UnmanagedCallersOnly]
    private static int HitCheckScreen(nint self, int px, int py, Vec3* at, nint info)
    {
        Trace("HitCheckScreen");
        DbId cam = Id(self);
        if (cam.IsNull) return 0;
        DbId scene = SceneDb.CamScene[cam.Index];
        if (!SceneDb.Scenes.Valid(scene)) return 0;
        SeamFrame.Viewport(out float vx, out float vy, out float vw, out float vh);
        GuiUpscale.Ask(cam, vx, vy, vw, vh, SeamFrame.GuiScale, out DbCamera.ViewUpscale up);
        if (!DbCamera.PickSegment(cam, px, py, vx, vy, vw, vh, in up, out Vector3 from, out Vector3 to))
        {
            Pick(cam, scene, px, py, from, to, HitKind.None, null, default);
            return 0;
        }
        HitKind kind = SeamHit.Cast(scene, from, to, at, info, out DbId hit);
        Pick(cam, scene, px, py, from, to, kind, at, hit);
        return (int)kind;
    }

    /// <summary><c>alloy.hitcheck=1</c>: the pixel this pick came in on, the rect it was mapped against and
    /// the segment and answer that came out. Every world click, hover and drop is this slot, so a pick that
    /// lands in the wrong place is a wrong segment or a wrong rect and the line says which -- the one thing
    /// no screenshot can tell apart. Budgeted, because a hover fires it every frame.</summary>
    private static void Pick(DbId cam, DbId scene, int px, int py,
                            Vector3 from, Vector3 to, HitKind kind, Vec3* at, DbId hit)
    {
        if (!Renderer.HitCheck.Boot) return;
        // Two budgets, because one would be spent before the game ever reaches an area: every GUI panel is an
        // ORTHO camera on this same slot and the main menu alone fires hundreds.
        bool world = DbCamera.ViewAngleOf(cam) > 0f;
        int b = world ? 0 : 1;
        if (world && _picksSaid[0] >= WorldPickMax) return;
        // GUI: a budget per camera, so the main menu cannot spend the one a later panel needs.
        if (!world && (uint)cam.Index < (uint)SceneDb.MaxCameras && _guiSaid[cam.Index] >= GuiPickMax) return;
        // One line per pixel PER CAMERA. The game re-picks the hover every frame, so a plain budget is spent
        // in the first second on whatever pixel the cursor was parked at and never reaches a click; and
        // CGuiMan::GuiObjectHitCheck @0x67da40 walks EVERY active panel's camera on the same pixel, so a
        // single last-key slot never saw two calls in a row from the same camera and suppressed nothing.
        int ci = cam.Index;
        if ((uint)ci >= (uint)SceneDb.MaxCameras) return;
        // GUI: one line per change of WHAT is under the cursor, so a hover across a panel is a few lines.
        long at2 = world ? ((long)px << 32) | (uint)py : ((long)hit.Value << 8) | (uint)kind;
        if (at2 == _lastPx[ci]) return;
        _lastPx[ci] = at2;
        _picksSaid[b]++;
        if (!world) _guiSaid[ci]++;
        // The CAMERA's rect, not the frame's: a panel on a sub-viewport is mapped against its own, and a
        // pick that lands in the wrong place is nearly always the wrong one of the two.
        SeamFrame.Viewport(out float vx, out float vy, out float vw, out float vh);
        DbCamera.Rect(cam, vx, vy, vw, vh, SeamFrame.GuiScale, out float rx, out float ry, out float rw, out float rh);
        string who = "-";
        if (SceneDb.Objects.Valid(hit))
        {
            int o = hit.Index;
            Vector3 w = new(SceneDb.ObjWorld[o].M41, SceneDb.ObjWorld[o].M42, SceneDb.ObjWorld[o].M43);
            who = $"o={o} \"{SceneDb.ObjModel[o]?.Name ?? "-"}\" at=({w.X:F2},{w.Y:F2},{w.Z:F2}) rides:";
            // The rider chain up to the root, with each link's scene: a control left in a scene its page has
            // left shows here which link did not carry the move.
            DbId up = SceneDb.RiderParent(o);
            for (int hop = 0; hop < 8 && SceneDb.Objects.Valid(up); hop++, up = SceneDb.RiderParent(up.Index))
                who += $" o{up.Index}\"{SceneDb.ObjModel[up.Index]?.Name ?? "-"}\"@s{SceneDb.ObjScene[up.Index].Index}";
            who += $" beh={(DbAnim.Behavior(hit) != 0 ? "y" : "n")} attachTo=o{SceneDb.ObjAttachTo[o].Index}";
        }
        _log?.Info($"[dbpick] {(world ? "world" : "gui")} px=({px},{py}) scene={scene.Index} "
                   + $"\"{SceneDb.SceneName[scene.Index]}\" rect=({rx},{ry},{rw},{rh}) "
                   + $"from=({from.X:F2},{from.Y:F2},{from.Z:F2}) to=({to.X:F2},{to.Y:F2},{to.Z:F2}) "
                   + $"kind={(int)kind} hit={(at != null ? $"({at->X:F2},{at->Y:F2},{at->Z:F2})" : "-")} {who}");
    }

    private const int WorldPickMax = 60, GuiPickMax = 60;
    private static readonly int[] _guiSaid = new int[SceneDb.MaxCameras];
    private static readonly int[] _picksSaid = new int[2];
    private static readonly long[] _lastPx = Minus1(new long[SceneDb.MaxCameras]);

    private static long[] Minus1(long[] a) { Array.Fill(a, -1L); return a; }

    /// <summary>Linux primary slot 36, <c>Camera::HitCheck(Vector, Vector, Vector*, CHitInfo*)</c>. The
    /// engine's whole body @0x5aa420 is <c>Gob::HitCheck(this + 8)</c> -- the camera's own <c>Gob</c> half,
    /// which has no model and no parts, so it can only ever return null (Windows: slot 37 of Camera's Gob
    /// vftable, rva 0xd82f18). So this answers null: a scene cast would report hits vanilla never reports.</summary>
    [UnmanagedCallersOnly]
    private static nint HitCheckRay(nint self, VectorArg from, VectorArg to, Vec3* at, nint info)
    {
        Trace("HitCheckRay");
        Id(self);
        return 0;
    }

    /// <summary>Slot 20, <c>Camera::HitCheckViewVolumeNearest</c> @0x5a4060, term for term: only the four SIDE
    /// planes are candidates (<c>i = 2..5</c>, near and far only reject); a candidate outside ANY other plane
    /// (<c>n.P + d &gt; 0</c>, inside-negative) is dropped; the out-<c>Vector</c> is CAMERA-RELATIVE; argument
    /// four is a FOUND FLAG; the return is view-axis depth while <c>0 &lt; depth &lt; best</c>, miss = FAR clip.</summary>
    [UnmanagedCallersOnly]
    private static float HitCheckViewVolumeNearest(nint self, Vec3* from, Vec3* to, Vec3* at, int* found)
    {
        Trace("HitCheckViewVolumeNearest");
        DbId cam = Id(self);
        if (found != null) *found = 0;
        if (cam.IsNull || from == null || to == null) return 0f;
        float best = SceneDb.CamFar[cam.Index];
        var a = new Vector3(from->X, from->Y, from->Z);
        var b = new Vector3(to->X, to->Y, to->Z);
        Vector3 eye = DbCamera.PosOf(cam);
        Plane near = DbCamera.ViewVolume(cam, 0);
        for (int i = 2; i < SceneDb.ViewPlanes; i++)
        {
            Plane p = DbCamera.ViewVolume(cam, i);
            Vector3 d = b - a;
            float denom = Vector3.Dot(p.Normal, d);
            if (denom == 0f) continue;              // the engine's own test: equality with zero, no sign
            float t = -(Vector3.Dot(p.Normal, a) + p.D) / denom;
            Vector3 hit = a + d * t;
            bool outside = false;
            for (int j = 0; j < SceneDb.ViewPlanes && !outside; j++)
            {
                if (j == i) continue;
                Plane q = DbCamera.ViewVolume(cam, j);
                if (Vector3.Dot(q.Normal, hit) + q.D > 0f) outside = true;
            }
            if (outside) continue;
            Vector3 rel = hit - eye;
            float depth = Vector3.Dot(rel, near.Normal);
            if (depth >= best || depth <= 0f) continue;
            best = depth;
            if (at != null) { at->X = rel.X; at->Y = rel.Y; at->Z = rel.Z; }
            if (found != null) *found = 1;
        }
        return best;
    }

    // ---- the seven by-value returns, per OS ----------------------------------------------------------

#if WINDOWS
    [UnmanagedCallersOnly]
    private static nint SetPositionWin(nint self, Vec3* ret, Vec3* p)
    {
        Trace("SetPositionWin");
        DbId cam = Id(self);
        if (p != null) DbCamera.SetPosition(cam, new Vector3(p->X, p->Y, p->Z));
        return Out(ret, cam.IsNull ? Vector3.Zero : DbCamera.PosOf(cam));
    }

    [UnmanagedCallersOnly]
    private static nint SetOrientationWin(nint self, Quat* ret, Quat* q)
    {
        Trace("SetOrientationWin");
        DbId cam = Id(self);
        if (q != null) DbCamera.SetOrientation(cam, new Quaternion(q->X, q->Y, q->Z, q->W));
        return OutQ(ret, cam.IsNull ? Quaternion.Identity : DbCamera.RotOf(cam));
    }

    [UnmanagedCallersOnly]
    private static nint GetPositionWin(nint self, Vec3* ret)
    {
        Trace("GetPositionWin");
        DbId cam = Id(self);
        return Out(ret, cam.IsNull ? Vector3.Zero : DbCamera.PosOf(cam));
    }

    [UnmanagedCallersOnly]
    private static nint GetOrientationWin(nint self, Quat* ret)
    {
        Trace("GetOrientationWin");
        DbId cam = Id(self);
        return OutQ(ret, cam.IsNull ? Quaternion.Identity : DbCamera.RotOf(cam));
    }

    [UnmanagedCallersOnly]
    private static nint GetPreviousPositionWin(nint self, Vec3* ret)
    {
        Trace("GetPreviousPositionWin");
        DbId cam = Id(self);
        return Out(ret, cam.IsNull ? Vector3.Zero : SceneDb.CamPrevPos[cam.Index]);
    }

    [UnmanagedCallersOnly]
    private static nint MapToScreenWin(nint self, Vec3* ret, Vec3* world, byte normalized)
    {
        Trace("MapToScreenWin");
        Vector3 w = world != null ? new Vector3(world->X, world->Y, world->Z) : Vector3.Zero;
        return Out(ret, Project(Id(self), w, normalized != 0));
    }

    [UnmanagedCallersOnly]
    private static nint MouseDirWin(nint self, Vec3* ret, int px, int py)
        => Out(ret, Ray(Id(self), px, py));

    [UnmanagedCallersOnly]
    private static nint GetViewVolumeWin(nint self, AurPlane* ret, int index)
    {
        Trace("GetViewVolumeWin");
        if (ret != null) *ret = DbCamera.ViewVolume(Id(self), index);
        return (nint)ret;
    }

    private static nint Out(Vec3* ret, Vector3 v)
    {
        if (ret != null) { ret->X = v.X; ret->Y = v.Y; ret->Z = v.Z; }
        return (nint)ret;
    }

    private static nint OutQ(Quat* ret, Quaternion q)
    {
        if (ret != null) { ret->W = q.W; ret->X = q.X; ret->Y = q.Y; ret->Z = q.Z; }
        return (nint)ret;
    }
#else
    [UnmanagedCallersOnly]
    private static Vec3 SetPositionSysV(nint self, Vec3 p)
    {
        Trace("SetPositionSysV");
        DbId cam = Id(self);
        DbCamera.SetPosition(cam, new Vector3(p.X, p.Y, p.Z));
        return V(cam.IsNull ? Vector3.Zero : DbCamera.PosOf(cam));
    }

    [UnmanagedCallersOnly]
    private static Quat SetOrientationSysV(nint self, Quat q)
    {
        Trace("SetOrientationSysV");
        DbId cam = Id(self);
        DbCamera.SetOrientation(cam, new Quaternion(q.X, q.Y, q.Z, q.W));
        return cam.IsNull ? (Quat)Quaternion.Identity : (Quat)DbCamera.RotOf(cam);
    }

    [UnmanagedCallersOnly]
    private static Vec3 GetPositionSysV(nint self)
    {
        Trace("GetPositionSysV");
        DbId cam = Id(self);
        return V(cam.IsNull ? Vector3.Zero : DbCamera.PosOf(cam));
    }

    [UnmanagedCallersOnly]
    private static Quat GetOrientationSysV(nint self)
    {
        Trace("GetOrientationSysV");
        DbId cam = Id(self);
        return cam.IsNull ? (Quat)Quaternion.Identity : (Quat)DbCamera.RotOf(cam);
    }

    [UnmanagedCallersOnly]
    private static Vec3 GetPreviousPositionSysV(nint self)
    {
        Trace("GetPreviousPositionSysV");
        DbId cam = Id(self);
        return V(cam.IsNull ? Vector3.Zero : SceneDb.CamPrevPos[cam.Index]);
    }

    [UnmanagedCallersOnly]
    private static Vec3 MapToScreenSysV(nint self, Vec3 world, byte normalized)
        => V(Project(Id(self), new Vector3(world.X, world.Y, world.Z), normalized != 0));

    [UnmanagedCallersOnly]
    private static Vec3 MouseDirSysV(nint self, int px, int py) => V(Ray(Id(self), px, py));

    [UnmanagedCallersOnly]
    private static AurPlane GetViewVolumeSysV(nint self, int index) => DbCamera.ViewVolume(Id(self), index);

    private static Vec3 V(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
#endif

    private static Vector3 Project(DbId cam, Vector3 world, bool normalized)
    {
        SeamFrame.Viewport(out float vx, out float vy, out float vw, out float vh);
        return DbCamera.MapToScreen(cam, world, normalized, vx, vy, vw, vh, SeamFrame.GuiScale);
    }

    private static Vector3 Ray(DbId cam, int px, int py)
    {
        SeamFrame.Viewport(out float vx, out float vy, out float vw, out float vh);
        return DbCamera.MouseDir(cam, px, py, vx, vy, vw, vh);
    }

    internal static string Stats(int _)
        => $"camera slots: {_calls} calls, {_views} views requested"
         + (_nullCam > 0 ? $", {_nullCam} on a DEAD camera handle" : "");
}
