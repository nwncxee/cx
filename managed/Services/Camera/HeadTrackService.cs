using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using CX.Services.Anim;

namespace CX.Services.Camera;

/// <summary>
/// Action-camera head tracking: the local player's creature turns its HEAD toward the camera axis, body left to
/// the engine, by retargeting NWN's own clamped <c>LookAtAnimation</c>. <c>UpdateLookAt</c> is replaced for the
/// player with a <c>BeginLookAtAnimate</c> on itself and <c>LookAtAnimation::Control</c> chained, so the four
/// floats at <c>+0x58</c> become the camera aim. Allocates nothing; the quaternion maths is ported (sret), not called.
/// </summary>
public sealed unsafe class HeadTrackService : IExtenderService
{
    private static ExtenderLog? _log;
    private static UpdateLookAtFn _updateOrig;
    private static LookAtControlFn* _controlNext;
    /// <summary>Resolved once: both run inside the per-creature animation step.</summary>
    private static BeginLookAtAnimateFn _begin;
    /// <inheritdoc cref="_begin"/>
    private static PartGetWorldFn _getWorld;

    /// <summary>The gob we drove this frame, and the key <see cref="OnControl"/> matches an animation by
    /// (<c>LookAtAnimation+0x30</c> is the looker's gob). Zero = not driving.</summary>
    private static IntPtr _gob;
    /// <summary>The YAW is beyond what a head can do (behind the shoulder): it is let go, and zeroed, until it
    /// comes back inside the limit. Degrees past <c>headyaw</c> that release it, and the hysteresis band below
    /// that which re-engages it. Pitch is never released.</summary>
    private static bool _released;
    private const float ReleaseOver = 20f, ReleaseHysteresis = 10f;

    private static bool _armed, _probed, _rot;
    private static int _drives, _writes;

    private const float Deg = MathF.PI / 180f;
    /// <summary>The engine's own literals at the <c>BeginLookAtAnimate</c> call site: the 0.25 s ease every
    /// look-at blends over, and a reach the self-target can never exceed (the distance check is gob-to-gob).</summary>
    private const float Blend = 0.25f, Reach = 1e9f;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        CameraKnobs.Declare();
        SelfCheck(log);
        RigMap.Init(log);
        if (!EngineLayout.Require(log, "HeadTrack", "CNWCCreature", "LookAtAnimation", "Part")) return;
        if (!SymbolMap.Require(log, "HeadTrack", SymbolMap.UpdateLookAt, SymbolMap.LookAtControl,
                               SymbolMap.BeginLookAtAnimate, SymbolMap.PartGetWorld)) return;
        _begin = EngineFn.BeginLookAtAnimate;
        _getWorld = EngineFn.PartGetWorld;

        _updateOrig = hooks.InstallUpdateLookAt(&OnUpdateLookAt);
        if (_updateOrig == null) { log.Info("HeadTrack: standing down (UpdateLookAt install refused)"); return; }
        _controlNext = hooks.ChainLookAtControl(&OnControl, 50);
        // Both or neither: the chooser hook alone would aim the head at the creature's own head.
        if (_controlNext == null) { log.Info("HeadTrack: standing down (Control chain refused)"); return; }
        _armed = true;
        log.Info("HeadTrack: hooked CNWCCreature::UpdateLookAt + LookAtAnimation::Control "
                 + $"(arcs {MouseLook.HeadYaw.V:0}/{MouseLook.HeadPitch.V:0} deg, {(MouseLook.HeadTrack.V ? "on" : "off")})");
    }

    /// <summary>Driving this frame: the knob, the action camera actually held, and nothing rotten.</summary>
    private static bool Wanted => _armed && !_rot && MouseLook.HeadTrack.V && MouseLookService.ActionHeld;

    /// <summary>The engine's own target chooser. For the local player under the action camera it is replaced
    /// by one <c>BeginLookAtAnimate</c> aimed at ourselves, which <see cref="OnControl"/> then re-aims; every
    /// other creature, and every frame we are not driving, runs vanilla.</summary>
    [UnmanagedCallersOnly]
    private static void OnUpdateLookAt(IntPtr creature)
    {
        if (creature == IntPtr.Zero) { _gob = IntPtr.Zero; return; }   // a null `this` is not the engine's to take
        if (!Wanted) { _gob = IntPtr.Zero; _updateOrig(creature); return; }
        if (creature != CameraService.PlayerCreature()) { _updateOrig(creature); return; }
        if (!_probed)
        {
            _probed = true;
            _rot = !EngineLayout.Probe("CNWCCreature", creature, _log);
            if (_rot) { _gob = IntPtr.Zero; _updateOrig(creature); return; }
        }
        IntPtr gob = Model(creature);
        if (gob == IntPtr.Zero) { _gob = IntPtr.Zero; _updateOrig(creature); return; }
        var head = RigMap.Head(RigMap.Appearance(creature));
        _gob = gob; _drives++;
        if (Renderer.On) { DriveDb(gob, head.Text); return; }
        fixed (byte* node = head.Name) _begin(gob, gob, node, node, MouseLook.HeadYaw.V, MouseLook.HeadPitch.V, Blend, Reach);
    }

    /// <summary>The look-at slot's own body, and <c>Gob::BeginLookAtAnimate</c> called directly.</summary>
    private const int LookAtSlot = 74;

    /// <summary>The model is a scene-database facade, with no engine part tree for <c>Gob::BeginLookAtAnimate</c>
    /// to walk and no <c>LookAtAnimation</c> for <see cref="OnControl"/> to re-aim: the database's own look-at
    /// is begun on <paramref name="head"/> and then aimed, with the same clamp and release.</summary>
    private static void DriveDb(IntPtr gob, string head)
    {
        DbId id = Facade.Object(gob, LookAtSlot);
        if (id.Value == 0) return;
        DbAnim.BeginLookAt(id, id, head, head, MouseLook.HeadYaw.V, MouseLook.HeadPitch.V, Blend, Reach);
        CameraService.Rendered(out _, out float camYaw, out float camPitch, out _);
        Vector3 dir = -CameraService.Forward(CameraService.Pose(camYaw, camPitch));
        if (!DbAnim.LookAtMeasure(id, dir, out float want, out float pitch)) return;
        Clamp(want, pitch, out float yaw, out pitch);
        DbAnim.LookAtAim(id, yaw, pitch);
        _writes++;
    }

    /// <summary>The creature's Gob, through the engine's own accessor: <c>GetModel(0xff, 1)</c> forwards to
    /// the CNWCAnimBase at +0x78, which answers <c>*(animBase + 0xc8)</c>.</summary>
    private static IntPtr Model(IntPtr creature)
    {
        IntPtr vtbl = *(IntPtr*)creature;
        if (vtbl == IntPtr.Zero) return IntPtr.Zero;
        return new CNWCCreature(creature).GetModel(0xff, 1);
    }

    /// <summary>After the engine's own aim step: ours is the animation whose looker is the gob we drove, and
    /// only while it is still live (the finished byte is the release latch's, not ours to fight).</summary>
    [UnmanagedCallersOnly]
    private static void OnControl(IntPtr self)
    {
        (*_controlNext)(self);
        IntPtr gob = _gob;
        // Wanted as well as the gob: the camera dropping is what stops us driving, and a gob freed after it
        // dropped could otherwise be matched by an address the engine has handed to something else.
        var anim = new LookAtAnimation(self);
        if (gob == IntPtr.Zero || self == IntPtr.Zero || !Wanted || anim.Gob.Ptr != gob) return;
        if (anim.Finished != 0) return;
        float* key = (float*)anim.TargetKey;
        Part head = anim.HeadPart;
        if (key == null || head.IsNull) return;
        Aim(head, key);
        _writes++;
    }

    /// <summary>Control's own maths with one substitution: the direction is the camera axis rather than the vector
    /// to a target object. Yaw is the arc from the head's world forward (local +Y) onto it, composed onto the head's
    /// local rotation, so the clamp is measured from the body's facing; pitch is the aim's elevation. The camera
    /// looks back at its focus, so the axis aimed at is -Forward. Past the yaw limit the head lets go (yaw only).</summary>
    private static void Aim(Part head, float* key)
    {
        CameraService.Rendered(out _, out float camYaw, out float camPitch, out _);
        Vector3 dir = -CameraService.Forward(CameraService.Pose(camYaw, camPitch));
        Vector3 pos;
        float* world = stackalloc float[4];
        _getWorld(head.Ptr, &pos, world);
        // Engine quaternions are (w, x, y, z); System.Numerics is (x, y, z, w).
        Quaternion wq = new(world[1], world[2], world[3], world[0]);
        Quaternion lq = head.Rot;
        Vector3 fwd = YAxis(wq);
        if (fwd.LengthSquared() < 1e-12f) return;
        Clamp(YawOf(RotationArc(fwd, dir) * lq), MathF.Asin(Math.Clamp(dir.Z, -1f, 1f)) / Deg, out float yaw, out float pitch);
        Quaternion q = CameraService.Pose(yaw, pitch);
        key[0] = q.X; key[1] = q.Y; key[2] = q.Z; key[3] = q.W;
    }

    /// <summary>The mode's arcs on a wanted yaw and pitch (degrees), with the yaw's release latch.</summary>
    private static void Clamp(float want, float wantPitch, out float yaw, out float pitch)
    {
        float arcH = MouseLook.HeadYaw.V, arcV = MouseLook.HeadPitch.V;
        float over = arcH > 0f ? MathF.Abs(want) - arcH : 0f;
        if (over > ReleaseOver) _released = true; else if (over < ReleaseOver - ReleaseHysteresis) _released = false;
        yaw = _released ? 0f : Math.Clamp(want, -arcH, arcH);
        pitch = Math.Clamp(wantPitch, -arcV, arcV);
    }

    /// <summary>A quaternion's local <b>+Y</b> axis -- the head's forward, and what the engine's
    /// <c>Yaw</c>/<c>Pitch</c> overloads read a quaternion through.</summary>
    private static Vector3 YAxis(in Quaternion q) =>
        new(2f * (q.X * q.Y - q.W * q.Z), 1f - 2f * (q.X * q.X + q.Z * q.Z), 2f * (q.Y * q.Z + q.W * q.X));

    /// <summary><c>Yaw(Quaternion const&amp;)</c> @0x5b68b0 ported: <c>atan2(-y.x, y.y)</c> in degrees over the
    /// quaternion's +Y axis, i.e. the same convention as <c>Yaw(Vector const&amp;)</c> @0x5b6cc0. Zero when the
    /// axis has no horizontal part, as the engine's own guard does.</summary>
    private static float YawOf(in Quaternion q)
    {
        float x = 2f * (q.X * q.Y - q.W * q.Z), y = 1f - 2f * (q.X * q.X + q.Z * q.Z);
        return x == 0f && y == 0f ? 0f : MathF.Atan2(-x, y) / Deg;
    }

    /// <summary><c>RotationArc(Vector, Vector)</c> @0x5b7070 ported: the shortest rotation taking
    /// <paramref name="a"/> onto <paramref name="b"/>, and the engine's own antiparallel answer (a half turn
    /// about +X) when the two oppose.</summary>
    private static Quaternion RotationArc(Vector3 a, Vector3 b)
    {
        a = Vector3.Normalize(a); b = Vector3.Normalize(b);
        float d = Vector3.Dot(a, b);
        if (d <= -1f) return new Quaternion(1f, 0f, 0f, 0f);
        Vector3 c = Vector3.Cross(a, b);
        float s = MathF.Sqrt((d + 1f) * 2f);
        return new Quaternion(c.X / s, c.Y / s, c.Z / s, s * 0.5f);
    }

    /// <summary>The mode status tail: whether the head is being driven and whether the aim is landing.
    /// Drives without writes means <c>FindPart</c> never found the head node.</summary>
    internal static string Fragment =>
        !_armed ? "head tracking unhooked"
        : _rot ? "head tracking stood down (layout probe)"
        : FormattableString.Invariant($"head tracking {(MouseLook.HeadTrack.V ? "on" : "off")}, {_drives} drive(s), {_writes} aim write(s)");

    /// <summary>The ported maths against the engine's conventions: a yaw of +Y is 0 and east is -90
    /// (<c>atan2(-x, y)</c>), and a pose written by <see cref="CameraService.Pose"/> reads back as the yaw it
    /// was built from -- the one property the overwrite depends on.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "HeadTrack.SelfCheck");
        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.01f;
        check.Expect(Near(YawOf(Quaternion.Identity), 0f), "facing +Y is yaw 0");
        check.Expect(Near(YawOf(RotationArc(Vector3.UnitY, Vector3.UnitX)), -90f), "an aim due east is yaw -90");
        check.Expect(Near(YawOf(RotationArc(Vector3.UnitY, -Vector3.UnitX)), 90f), "an aim due west is yaw +90");
        check.Expect(Near(YawOf(CameraService.Pose(35f, 0f)), 35f), "the written pose reads back as its own yaw");
        check.Expect(Near(YAxis(Quaternion.Identity).Y, 1f) && Near(YAxis(CameraService.Pose(0f, 30f)).Z, 0.5f),
          "+Y is the unpitched forward, and a written pitch is the elevation the aim was measured as");
        log.Info(check.Ok ? "HeadTrack.SelfCheck: passed" : "HeadTrack.SelfCheck: FAILED");
    }
}
