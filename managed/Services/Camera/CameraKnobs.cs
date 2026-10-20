using CX.Framework;

namespace CX.Services.Camera;

/// <summary>The six camera groups, in panel order. <see cref="Declare"/> touches each class so
/// the groups declare on the main thread at Initialize, in this order, whether or not a camera service arms
/// (a group that stands down still declares).</summary>
internal static class CameraKnobs
{
    internal static void Declare()
    {
        _ = CameraModes.ByMode; _ = Walls.G; _ = FreeCam.G; _ = TabTarget.G;
    }

    internal const float FovMin = 10f, FovMax = 120f;

    /// <summary>Off-device, read-only (a set would arm the writer against the user's real file): the six groups
    /// declare in panel order, the per-mode defaults differ where the spec says, the FOV magic zero is gone from
    /// the modes and kept on <c>free.fov</c>, and <c>/chase fov 60</c> parses to the chase row alone.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Declare();
        string[] order = { "chase", "topdown", "driving", "walls", "free", "tabtarget" };
        int at = -1;
        foreach (string g in order)
        {
            int i = -1;
            var all = KnobGroup.All;
            for (int k = 0; k < all.Count; k++) if (all[k].Name == g) i = k;
            check.Expect(i > at, $"group {g} declares after the one before it");
            at = i;
        }
        check.Expect(CameraModes.ByMode[0] == CameraModes.Chase && CameraModes.ByMode[1] == CameraModes.TopDown && CameraModes.ByMode[2] == CameraModes.Driving,
          "the mode byte indexes chase, top-down, driving");
        var fov = KnobGroup.Find("chase.fov") as Knob<float>;
        check.Expect(fov != null && fov == CameraModes.Chase.Fov && fov.Default == 45f && fov.Min == FovMin && fov.Max == FovMax, "chase.fov is the 10-120 slider defaulting to 45");
        check.Expect(fov!.TryParse("60", out float v) && v == 60f && CameraModes.TopDown.Fov != fov, "/chase fov 60 parses and names the chase row only");
        check.Expect(CameraModes.Chase.Drive.Default && !CameraModes.TopDown.Drive.Default && CameraModes.Driving.Drive.Default
          && CameraModes.Chase.Recenter.Default && !CameraModes.TopDown.Recenter.Default, "drive and recenter default off for top-down only");
        check.Expect(CameraModes.Chase.Face.Default && !CameraModes.TopDown.Face.Default && !CameraModes.Driving.Face.Default
          && !CameraModes.Chase.SideRun.Default && CameraModes.Chase.Rate.Default == 360f && CameraModes.Chase.DeadZone.Default == 0f && CameraModes.Chase.Idle.Default == 0f, "chase faces the camera by default, the recenter is rigid and never drifts idle");
        check.Expect(CameraModes.Chase.Enabled.Key == "chase.enabled" && CameraModes.Chase.Look.Key == "chase.mouselook" && KnobGroup.Find("mouselook.enabled") == null,
          "the mode master is declared; mouse-look is a per-mode toggle, not a group");
        check.Expect(CameraModes.Chase.Look.Changed == MouseLookService.MasterChanged && CameraModes.Chase.Enabled.Changed == null, "mouse-look's toggle releases on its edge; a mode's master is read per Control");
        check.Expect(MouseLook.Enabled == CameraModes.Chase.Look && MouseLook.Sens == CameraModes.Chase.Sens, "off-device (no mode) the facade answers the chase rows");
        check.Expect(KnobGroup.Find("free.enter") is KnobButton && FreeCam.G.Knobs[0] == FreeCam.Enter, "free.enter is the first row and a button");
        check.Expect(FreeCam.Fov.Default == 0f && FreeCam.Fov.Min == 0f && FreeCam.Leash.Default == FreeCam.LeashMax && FreeCam.Leash.Max == FreeCam.LeashMax,
          "free.fov keeps 0 = the mode's; the leash never exceeds the engine's 25");
        check.Expect(Walls.Fade.Default && Walls.Floor.Default == 0.5f && Walls.ArmOut.Default == 4f, "walls defaults are the #537/#538 boot values");
        check.Expect(!MouseLook.Right.Default && MouseLook.Sens.Default == 1f && MouseLook.Threshold.Default == 4 && !MouseLook.Invert.Default, "mouselook defaults are the old boot keys'");
        check.Expect(MouseLook.HeadTrack.Default && MouseLook.HeadYaw.Default == 70f && MouseLook.HeadPitch.Default == 30f
          && MouseLook.HeadYaw.Max == 89f && MouseLook.HeadPitch.Max == 89f,
          "head tracking is on by default and neither arc can ask for more than the engine's 89");
        check.Expect(CameraModes.Driving.Hz.Default == 10f && CameraModes.Driving.Turn.Default == 540f && CameraModes.TopDown.KeyTurn.Default == 180f, "drive defaults are the #540 values, in every mode");
        check.Expect(KnobGroup.Find("topdown.rate") is Knob<float> && KnobGroup.Find("move.rate") == null, "movement tuning is per mode; the move group is gone");
        check.Expect(!TabTarget.Enabled.Default && TabTarget.Cast.Default == (int)TabTargetCore.CastMode.Quick && TabTarget.CycleKey.Default == "Tab"
          && TabTarget.Filter.Default == 0 && !TabTarget.Objects.Default && TabTarget.Camera.Default == 1 && TabTarget.LockKey.Default == "F" && TabTarget.Tilt.Default == 12f,
          "tab-target is off by default, casts Quick, cycles on Tab, hostiles only, turns the camera, locks on F");
        check.Expect(KnobGroup.Find("camera.occlude") == null && KnobGroup.Find("mouselook_sens") == null, "the retired flat keys are not declared");
        check.Expect(CameraModes.Chase.Smooth == CameraModes.Chase.LagPos && CameraModes.Chase.Smooth.Default == 0f && CameraModes.Chase.Smooth.Tier == Tier.User
          && CameraModes.Chase.LagRot.Tier == Tier.ChatOnly
          && CameraModes.Chase.LagZoom.Tier == Tier.ChatOnly && CameraModes.Chase.LagZoom.Default == 0.02f,
          "Smoothing is lagpos, the one row; rotation and zoom lag are cfg-only and the zoom keeps its stock step");
        check.Expect(CameraModes.Chase.G.PanelStatus != null, "the live-mode line is the panel's");
        check.Expect(KnobGroup.Find("walk.enabled") == null, "the step guard has no off switch: it is not a preference");
        log.Info(check.Ok ? "CameraKnobs.SelfCheck: passed" : "CameraKnobs.SelfCheck: FAILED");
    }
}

/// <summary>One engine camera mode's group: <c>chase</c> / <c>topdown</c> / <c>driving</c>, identical rows with
/// per-mode values, each with its own master. Off is the stock camera for that mode -- the ninth
/// <c>CameraService.Gate</c> value, read per Control (feel, recenter and the drive stand aside, the view angle goes
/// back to the first one read), so it needs no <c>changed:</c>. Read hot through <see cref="CameraModes.ByMode"/>.</summary>
public sealed class CameraModeKnobs
{
    public readonly KnobGroup G;
    public readonly Knob<bool> Enabled, Drive, Face, SideRun, Recenter;
    /// <summary><see cref="Smooth"/> is <see cref="LagPos"/>, the one panel row for the feel; a set carries the rotation
    /// and zoom lag along. The mode's User rows are exactly <see cref="KnobGroup.MaxUserRows"/>: a new one must replace one.</summary>
    public readonly Knob<float> Fov, Smooth, LagPos, LagRot, LagZoom, PitchCurve, PitchRef, OffsetX, OffsetY;
    /// <summary>The Movement tuning for the drive and the recenter, per mode: every mode has its own toggles.</summary>
    public readonly Knob<float> Rate, DeadZone, Idle, Grace, IdleRate, Hz, Turn, KeyTurn;
    /// <summary>Mouse-look and the action camera, per mode; read through the <see cref="MouseLook"/> facade.</summary>
    public readonly Knob<bool> Look, Right, Invert, Action, AltToggle, HeadTrack, Assist, AutoTarget, Feedback;
    public readonly Knob<float> Sens, HeadYaw, HeadPitch;
    public readonly Knob<int> Threshold, AssistPx;
    public readonly Knob<string> ActionKey;

    internal CameraModeKnobs(string name, string submenu, int mode, int order, bool moveDefault, bool faceDefault = false)
    {
        G = KnobGroup.Declare(name, "Controls", submenu, master: "Enable the CX camera for this mode",
            masterHelp: "CX shapes this camera mode: smoothing, field of view, offset, camera-relative movement, auto-recenter. Off, the stock camera runs untouched in this mode.",
            header: "The game has three camera modes, picked under its own Options » Controls » Camera Mode (Chase Cam, Driving, Top Down; Top Down is the game default). The camera-mode key (* by default) flips between Driving and the other one you picked. Each mode keeps its own settings here.",
            order: order);
        G.PanelStatus = () => ModeLine(mode, submenu);
        G.Status = () => CameraService.Mode == mode ? ModeLine(mode, submenu) + "\n" + MouseLookService.Report()
                                                   : ModeLine(mode, submenu);
        Enabled = G.Master!;
        G.Section("View");
        Fov = G.Slider("fov", "Field of view", CameraKnobs.FovMin, CameraKnobs.FovMax, 1f, 45f, "deg",
                       help: "Vertical field of view. The game's own is 45.");
        PitchCurve = G.Slider("pitchcurve", "Tilt down when zoomed out", -10f, 10f, 0.5f, 0f, "deg/m", Tier.Advanced,
                              help: "Degrees of extra tilt per metre of zoom past the reference distance. Negative lowers the angle instead. 0 leaves the tilt where you put it.");
        PitchRef = G.Slider("pitchref", "Tilt reference distance", 1f, 60f, 1f, 10f, "m", Tier.ChatOnly);
        G.Section("Feel");
        LagPos = Smooth = G.Slider("lagpos", "Smoothing", 0f, 2f, 0.05f, 0f, "s", changed: SmoothChanged,
                          help: "How long the camera takes to catch up with your character and to settle after a turn. 0 is welded, as in the stock game.");
        LagRot = G.Slider("lagrot", "Rotation smoothing", 0f, 2f, 0.05f, 0f, "s", Tier.ChatOnly,
                          help: "How long a turn takes to settle. 0 is welded.");
        LagZoom = G.Slider("lagzoom", "Zoom smoothing", 0f, 2f, 0.01f, ZoomSmooth, "s", Tier.ChatOnly,
                           help: "Smooths the mouse wheel's stepped zoom. 0 is the stock game's steps.");
        G.Section("Offset");
        OffsetX = G.Slider("offsetx", "Sideways offset", -10f, 10f, 0.25f, 0f, "m",
                           help: "Moves your character off centre; positive puts them on the left. 0 is the stock orbit.");
        OffsetY = G.Slider("offsety", "Height offset", -10f, 10f, 0.25f, 0f, "m",
                           help: "Raises the camera's focus above your character.");
        G.Section("Movement");
        Drive = G.Toggle("drive", "Move relative to the camera", moveDefault,
                         help: "Forward is where the camera looks, not where your character faces.");
        Face = G.Toggle("face", "Face where the camera looks", faceDefault,
                        help: "Your character always faces the camera's direction. Forward runs that way, back walks backwards, the strafe keys sidestep, and the turn keys turn you and the camera together. After you look around with the mouse, your character turns to the new direction on the next step. Off: your character turns to face whichever way it moves, and the camera swings behind it.");
        SideRun = G.Toggle("siderun", "Turn keys run sideways", false, Tier.Advanced,
                           help: "With Face where the camera looks on: the turn keys run you sideways instead of turning you and the camera (your character faces where it runs, the camera stays). The mouse is the way to look around; back turns you to the camera at once. Locked on a target this is always so, for the strafe keys too.");
        Recenter = G.Toggle("recenter", "Swing behind you as you move", moveDefault,
                            help: "The camera turns to follow your movement direction instead of the stock chase.");
        Rate = G.Slider("rate", "Recenter speed", 1f, 720f, 5f, 360f, "deg/s", Tier.Advanced,
                        help: "Fastest swing behind your movement; it eases in near the target.");
        DeadZone = G.Slider("deadzone", "Recenter dead zone", 0f, 180f, 5f, 0f, "deg", Tier.ChatOnly,
                            help: "How far off your movement direction the camera may sit before it starts to swing.");
        Idle = G.Slider("idle", "Recenter after standing still", 0f, 60f, 0.5f, 0f, "s", Tier.ChatOnly,
                        help: "Seconds of standing still before the camera drifts behind your character's facing. 0 never does.");
        Grace = G.Slider("grace", "Orbit grace", 0f, 30f, 0.1f, 1.5f, "s", Tier.ChatOnly,
                         help: "No recenter this long after you orbit by hand.");
        IdleRate = G.Slider("idlerate", "Idle recenter speed", 1f, 720f, 5f, 30f, "deg/s", Tier.ChatOnly);
        KeyTurn = G.Slider("keyturn", "Turn-key speed", 10f, 720f, 10f, 180f, "deg/s", Tier.ChatOnly,
                           help: "How fast the turn keys swing you and the camera when facing the camera.");
        Hz = G.Slider("hz", "Heading updates per second", 1f, 60f, 1f, 10f, "", Tier.ChatOnly,
                      help: "Most heading packets sent per second; the stock game sends 2.");
        Turn = G.Slider("turn", "Heading turn rate", 0f, 3600f, 30f, 540f, "deg/s", Tier.ChatOnly, help: "0 = snap.");
        G.Section("Mouse-look");
        Look = G.Toggle("mouselook", "Enable CX mouse-look", true, changed: MouseLookService.MasterChanged,
                        help: "CX handles the mouse-drag camera: the cursor stays put and the view never stalls at the screen edge. Off, the stock middle-drag orbit runs.");
        Right = G.Toggle("right", "Right-drag looks around", false,
                         help: "Hold the right button and drag to orbit; a still right-click still opens the radial menu.");
        Sens = G.Slider("sens", "Sensitivity", 0.05f, 20f, 0.05f, 1f, "x", help: "1 matches the stock middle-drag.");
        Invert = G.Toggle("invert", "Invert vertical", false, help: "Dragging up tilts the view down.");
        Threshold = G.Slider("threshold", "Click-to-drag threshold", 0, 100, 1, 4, "px", Tier.ChatOnly,
                             help: "Pixels of movement before a right press becomes a look instead of a click.");
        G.Section("Action camera");
        Action = G.Toggle("action", "Action camera", false, changed: MouseLookService.ActionChanged,
                          help: "Hides the cursor and pins it to a crosshair in the centre of the screen. Moving the mouse turns the camera; whatever the crosshair points at is what you hover, tooltip and click, so aiming replaces pointing. Left-click acts on it, a right-click opens its menu. Clicking the ground does not walk you there (use the movement keys) and the crosshair never picks your own character. Alt frees the cursor for the game's panels (held, or a tap with the toggle option below); the toggle key switches the camera on and off.");
        ActionKey = G.Text("actionkey", "Action camera toggle key", "]", changed: MouseLookService.ActionKeyChanged,
                           help: "An SDL key name (F8, Tab, Backspace, Z...) or Mouse4 / Mouse5 for the thumb buttons. Blank binds nothing.");
        AltToggle = G.Toggle("alttoggle", "Alt toggles the cursor", false, Tier.ChatOnly,
                             help: "Under the action camera, tapping Alt frees the cursor until you tap it again, instead of holding it.");
        Assist = G.Toggle("assist", "Aim assist", true, Tier.Advanced,
                          help: "Under the action camera, the crosshair snaps its aim to the nearest creature, placeable or door within a few pixels of the centre, so you hover and click it without pixel-perfect aiming. The camera never moves; only what you point at changes. Brackets appear around the crosshair while a target is picked.");
        AssistPx = G.Slider("assistpx", "Aim assist radius", 0, 200, 5, 80, "px", Tier.ChatOnly,
                            help: "How far from the centre of the screen aim assist looks for a target. 0 turns it into the plain crosshair.");
        AutoTarget = G.Toggle("autotarget", "Cast at the crosshair", true, Tier.Advanced,
                              help: "Under the action camera, a spell, feat or item that asks you to pick a target fires straight away on whatever the crosshair is pointing at, with no second click. A ground-targeted spell still asks you to aim it.");
        Feedback = G.Toggle("feedback", "Action feedback on the crosshair", true, Tier.ChatOnly,
                            help: "Under the action camera, the crosshair answers what you did: a pulse when an order goes out, a red ring while you are attacking, and a dot underneath while an action is still waiting to happen.");
        HeadTrack = G.Toggle("headtrack", "Head follows the crosshair", true, Tier.Advanced,
                             help: "Under the action camera, your character turns their head toward whatever you are aiming at. The body still faces where it is walking. Only you see it -- other players see your character the way the game has always shown it.");
        HeadYaw = G.Slider("headyaw", "Head turn limit", 0f, 89f, 1f, 70f, "deg", Tier.ChatOnly,
                           help: "How far the head turns to the side before it stops following the crosshair. 0 stops sideways turning.");
        HeadPitch = G.Slider("headpitch", "Head tilt limit", 0f, 89f, 1f, 30f, "deg", Tier.ChatOnly,
                             help: "How far the head tilts up or down before it stops following the crosshair. 0 keeps it level.");
    }

    /// <summary><see cref="LagZoom"/>'s floor under the merged row, and its declared default: Smoothing at 0 is
    /// "welded", but the wheel's stepped zoom keeps the slight smoothing it ships with.</summary>
    private const float ZoomSmooth = 0.02f;

    /// <summary>The Smoothing row is <c>lagpos</c> itself; a set carries rotation and zoom along (chat and cfg can still
    /// tune those two apart).</summary>
    private void SmoothChanged()
    {
        float s = LagPos.V;
        LagRot.Set(s); LagZoom.Set(MathF.Max(s, ZoomSmooth));
    }

    /// <summary>Which camera mode the player is in, and whether these rows are the live ones: the panel's status
    /// line, and the first line of the diagnostic one.</summary>
    private static string ModeLine(int mode, string submenu)
        => CameraService.Mode < 0 ? ""
         : CameraService.Mode == mode ? "You are in this camera mode now."
         : $"You are in {CameraModes.ByMode[CameraService.Mode].G.Submenu} now; these rows apply once you switch to {submenu}.";
}

/// <summary>The three mode groups, indexed by the engine's camera mode byte (0 chase, 1 top-down, 2 driving; nwscript
/// calls the third STIFF_CHASE_CAMERA, the game's own UI and feedback call it Driving, so the player-facing name wins).</summary>
internal static class CameraModes
{
    internal static readonly CameraModeKnobs Chase, TopDown, Driving;
    internal static readonly CameraModeKnobs[] ByMode;

    static CameraModes()
    {
        Chase = new CameraModeKnobs("chase", "Chase", 0, 500, moveDefault: true, faceDefault: true);
        TopDown = new CameraModeKnobs("topdown", "Top-down", 1, 501, moveDefault: false);
        Driving = new CameraModeKnobs("driving", "Driving", 2, 502, moveDefault: true);
        ByMode = new[] { Chase, TopDown, Driving };
    }
}

/// <summary>The <c>walls</c> group: the occlusion fade (read by <c>AlloyRenderer.Occlude</c>) and the offset
/// camera's spring arm (read by <see cref="CameraService"/>). The fade rate stays a const; the spring arm's
/// own rates are nobody's dial but the one who tuned them, so they are ChatOnly (ADR 0030).</summary>
internal static class Walls
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Fade;
    internal static readonly Knob<float> Floor, Radius, Margin, ArmIn, ArmOut, ArmRadius;

    static Walls()
    {
        G = KnobGroup.Declare("walls", "Controls", "Camera", order: 510, title: "Walls");
        Fade = G.Toggle("fade", "Fade objects that hide you", true,
                        help: "Placeables between the camera and your character turn see-through.");
        Floor = G.Slider("floor", "Faded opacity", 0f, 1f, 0.05f, 0.5f,
                         help: "How solid a faded object stays. 0 hides it entirely.");
        Radius = G.Slider("radius", "Fade ray width", 0f, 5f, 0.1f, 0.5f, "m", Tier.ChatOnly,
                          help: "Half-width of the ray fan that finds occluders.");
        G.Section("Spring arm");
        Margin = G.Slider("margin", "Arm margin", 0f, 5f, 0.1f, 0.3f, "m", Tier.ChatOnly,
                          help: "How far short of a wall the offset camera stops.");
        ArmIn = G.Slider("armin", "Arm pull-in rate", 0f, 60f, 1f, 0f, "/s", Tier.ChatOnly, help: "0 = snap.");
        ArmOut = G.Slider("armout", "Arm ease-out rate", 0f, 60f, 1f, 4f, "/s", Tier.ChatOnly);
        ArmRadius = G.Slider("armradius", "Arm ray width", 0f, 2f, 0.1f, 0.3f, "m", Tier.ChatOnly, help: "0 = one ray.");
    }
}

/// <summary>The <c>free</c> group: the enter button first, then the feel. <see cref="Fov"/> keeps the spec's
/// one magic zero -- 0 = the field of view of the mode the camera left -- so its floor is 0 and a value under
/// <see cref="CameraKnobs.FovMin"/> snaps up to it.</summary>
internal static class FreeCam
{
    internal static readonly KnobGroup G;
    internal static readonly KnobButton Enter;
    internal static readonly Knob<float> Speed, Leash, Fov, Sens, Turn, Roll;

    /// <summary>The engine's own 25 m max-dist default; a server raises it through <c>SetCameraLimits</c>, never the client.</summary>
    internal const float LeashMax = 25f;

    static FreeCam()
    {
        G = KnobGroup.Declare("free", "Controls", "Camera", order: 512, title: "Free camera",
            header: "No master: the free camera is entered from the button and left with Escape.");
        Enter = G.Button("enter", "Enter the free camera", FreeCameraService.Toggle,
                         help: "Fly the camera freely: move keys fly, Space/C up and down, Z/X roll, Shift is fast, the wheel sets speed, right-drag looks. Escape returns. Closes this panel.");
        G.Section("Feel");
        Speed = G.Slider("speed", "Fly speed", 0.1f, 200f, 0.5f, 8f, "m/s", help: "How fast the move keys fly the camera; Shift is four times this.");
        Leash = G.Slider("leash", "Range from your character", 1f, LeashMax, 1f, LeashMax, "m",
                         help: "The camera stops this far from your character. A server that sets its own camera limit overrides it.");
        Fov = G.Slider("fov", "Field of view", 0f, CameraKnobs.FovMax, 1f, 0f, "deg", Tier.Advanced,
                       help: "0 keeps the field of view of the mode you came from.", changed: FovChanged);
        Sens = G.Slider("sens", "Look sensitivity", 0.05f, 20f, 0.05f, 1f, "x", Tier.Advanced, help: "On top of Mouse-look's sensitivity.");
        Turn = G.Slider("turn", "Keyboard turn speed", 10f, 720f, 10f, 90f, "deg/s", Tier.Advanced);
        Roll = G.Slider("roll", "Roll on entry", -180f, 180f, 5f, 0f, "deg", Tier.Advanced, changed: FreeCameraService.RollChanged);
    }

    private static void FovChanged()
    {
        if (Fov.V > 0f && Fov.V < CameraKnobs.FovMin) Fov.V = CameraKnobs.FovMin;
        FreeCameraService.FovChanged();
    }
}

/// <summary>The mouse-look rows of the LIVE camera mode, so every reader keeps its <c>MouseLook.X.V</c> shape.
/// Off-device -- no module, no mode -- the chase rows answer; every
/// action-camera gate also requires a mode, so that fallback only ever shapes the menu's plain drag capture.</summary>
internal static class MouseLook
{
    private static CameraModeKnobs M => CameraService.Mode is >= 0 and < 3 ? CameraModes.ByMode[CameraService.Mode] : CameraModes.Chase;
    internal static Knob<bool> Enabled => M.Look;
    internal static Knob<bool> Right => M.Right;
    internal static Knob<bool> Invert => M.Invert;
    internal static Knob<bool> Action => M.Action;
    internal static Knob<bool> AltToggle => M.AltToggle;
    internal static Knob<bool> HeadTrack => M.HeadTrack;
    internal static Knob<bool> Assist => M.Assist;
    internal static Knob<bool> AutoTarget => M.AutoTarget;
    internal static Knob<bool> Feedback => M.Feedback;
    internal static Knob<float> Sens => M.Sens;
    internal static Knob<float> HeadYaw => M.HeadYaw;
    internal static Knob<float> HeadPitch => M.HeadPitch;
    internal static Knob<int> Threshold => M.Threshold;
    internal static Knob<int> AssistPx => M.AssistPx;
}

/// <summary>The <c>tabtarget</c> group: one master, the cast mode, the two keys and the candidate
/// filter. Off by default -- installing CX changes nothing until the player opts in. Every knob is read live by
/// <see cref="TabTargetService"/>; the master's off edge drops the lock (<see cref="TabTargetService.MasterChanged"/>).</summary>
internal static class TabTarget
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Objects;
    /// <summary><see cref="Cast"/> is the <see cref="TabTargetCore.CastMode"/> ordinal (0 Normal, 1 Quick, 2 Quick
    /// with preview); <see cref="Camera"/> is 0 nothing, 1 turn to the target once, 2 lock on until the lock key.</summary>
    internal static readonly Knob<int> Cast, Filter, Camera;
    internal static readonly Knob<float> Range, Tilt;
    internal static readonly Knob<string> CycleKey, ClearKey, LockKey;

    static TabTarget()
    {
        G = KnobGroup.Declare("tabtarget", "Controls", "Targeting", order: 520, master: "Enable tab-target mode",
            masterHelp: "MMO-style targeting: pick one target with Tab or a left-click, then quickbar spells, feats, items and the attack button fire at it with no second click. Off, Tab and clicks are the stock ones.",
            masterDefault: false, masterChanged: TabTargetService.MasterChanged,
            header: "Clicking a creature only picks it as your target; the walk, attack or talk a click used to start is not sent. Empty ground keeps your target. The target loses its lock when it dies, leaves the area or the screen, or you change area. With the action camera held, the crosshair wins and the lock waits.");
        Enabled = G.Master!;
        Cast = G.Dropdown("cast", "Cast mode", new[] { 0, 1, 2 }, (int)TabTargetCore.CastMode.Quick, new[] { "Normal", "Quick", "Quick with preview" },
                          help: "Normal: press the button, then click a target, as the game always did. Quick: the button fires at once -- at your target, or with none at what the cursor points at; a ground spell lands at the cursor. Quick with preview: hold the button to see the game's own targeting preview, release to fire; right-click or Escape before release cancels.");
        CycleKey = G.Text("cyclekey", "Cycle key", "Tab", changed: TabTargetService.KeysChanged,
                          help: "Locks the candidate nearest the centre of the screen, then steps through the others. Shift with it steps back. An SDL key name (Tab, F8, Z...).");
        ClearKey = G.Text("clearkey", "Clear key", "Backspace", changed: TabTargetService.KeysChanged,
                          help: "Drops the lock. Blank binds nothing.");
        Camera = G.Dropdown("camera", "After picking a target", new[] { 0, 1, 2 }, 1, new[] { "Nothing", "Turn the camera to it", "Lock the camera on it" },
                            help: "Turn: the camera swings once to look at the new target (in tilt too, when the target sits off the middle of the view). Lock on: it keeps looking at the target as you both move, until you press the lock key (or pick nothing); the mouse cannot pull it off, and the turn and strafe keys run you sideways around the target instead of orbiting or strafing.");
        LockKey = G.Text("lockkey", "Lock-on key", "F", changed: TabTargetService.KeysChanged,
                         help: "Locks the camera on the creature nearest the middle of the screen (it becomes your target); pressed while locked on, lets go. An SDL key name.");
        Tilt = G.Slider("tilt", "Target within", 0f, 25f, 1f, 12f, "° of the centre", Tier.Advanced,
                        help: "The camera keeps your own tilt while the target sits within this many degrees of the middle of the view; past it the camera tilts just enough to bring it back -- flatter for a far or raised target, steeper for one below. 0 always puts the target dead centre.");
        Filter = G.Dropdown("filter", "Tab cycles", new[] { 0, 1 }, 0, new[] { "Hostile creatures", "All creatures" },
                            help: "Hostile only keeps allies and townsfolk out of a fight; all creatures lets you lock an ally for a heal.");
        Objects = G.Toggle("objects", "Include placeables and doors", false,
                           help: "Tab can also lock a lever, chest or door.");
        Range = G.Slider("range", "Maximum range", 5f, 80f, 1f, 30f, "m", help: "Tab ignores anything farther from you than this.");
    }
}

