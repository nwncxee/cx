using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Which concrete <c>CAurBehavior</c> a vptr belongs to, index-aligned with
/// <c>BehaviorSolvers.Symbols</c>.</summary>
internal enum DbBehaviorKind
{
    Unknown = -1,
    Base = 0, Attach, AttachTranslate, AttachRotate, AttachNoOrientation, AttachGuiScale,
    CrossSceneAttach, CrossSceneAttachOffset, Subwindow, Navigate, CameraFollow, CameraLooseFollow,
    CameraOnAStick,
}

/// <summary>The behaviour solve, at the DISPATCH POINT the engine had inside <c>Gob::Animate</c> @0x49d8c0:
/// <c>Control(dt)</c> through the behaviour's own vtable slot (<c>CAurBehavior</c> +0x20 Linux / +0x18
/// Windows), before any pose work, the object's death re-checked on return. A behaviour stays a real
/// engine-layout object (ADR 0028 section 2), read through <c>EngineLayout</c> views; only the answer moves.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>The behaviour the game gave each object (<c>Gob+0x450</c>, <c>Gob::SetBehavior</c>). A facade
    /// mirrors the word at that offset for the game's direct reads; this is the column the solve uses.</summary>
    private static readonly nint[] _behavior = new nint[SceneDb.MaxObjects];

    /// <summary>The object's behaviour (<c>Gob::GetBehavior</c>'s raw answer; the TYPE test is the seam slot's,
    /// because it runs through the behaviour's own <c>AsBehavior*</c> virtuals).</summary>
    public static nint Behavior(DbId obj) => SceneDb.Objects.Valid(obj) ? _behavior[obj.Index] : 0;

    /// <summary><c>Gob::SetBehavior</c> @0x43ef90 -- a plain store, as the engine's is. CX never frees a
    /// behaviour: the game allocated it and ADR 0028 leaves its lifetime there.</summary>
    public static void SetBehavior(DbId obj, nint behavior)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        // A different behaviour has not solved yet, so the attach snap places the object until it does.
        if (_behavior[i] != behavior) _behaveRan[i] = false;
        _behavior[i] = behavior;
        Moved(i);
        // An attach behaviour the game built itself (GuiAurAttachToObject) makes this a rider of its target, which
        // is how a scene change reaches it: the engine's UpdateScenesAttach rides the parent's event, uncapped.
        if (behavior != 0 && AttachFamily(Kind(behavior)))
        {
            DbId up = Facade.ObjectId(new CAurBehavior((IntPtr)behavior).Target.Ptr);
            if (SceneDb.Objects.Valid(up) && up.Value != obj.Value) SceneDb.LinkRider(i, up);
        }
        else if (behavior == 0 && SceneDb.RiderParent(i).Value != SceneDb.ObjAttachTo[i].Value)
            SceneDb.LinkRider(i, SceneDb.ObjAttachTo[i]);
    }

    /// <summary>Resolve a <c>CAurPart*</c> the game holds (a facade-interior pointer) to an absolute part
    /// index, or -1. Defaults to seam-obj's own resolver; settable so the off-device suite can stand one in.</summary>
    public static delegate*<nint, int> ResolvePart = &FacadeParts.PartIndex;

    /// <summary>Read an object's placement back out of its facade shell into the record columns. Needed after a
    /// GAME-side behaviour's <c>Control</c>, which writes the shell's <c>+0xa8</c>/<c>+0xb4</c> words by
    /// interior address through <c>Part::GetWorld</c> rather than through a setter -- so the columns would
    /// otherwise never learn about a GUI attachment at all.</summary>
    public static delegate*<DbId, void> ShellPoseSync = &SyncFromShell;

    /// <summary>Destroy an object the way the game's own path would (the facade's deleting destructor), for the
    /// attach solver's <c>DieWithoutParent</c>. Defaults to the scene seam's bridge.</summary>
    public static delegate*<DbId, void> DestroyObject = &DestroyViaBridge;

    /// <summary>The default <see cref="ShellPoseSync"/>: the shell's mirrored placement words are the engine's
    /// own <c>Gob+0xa8</c>/<c>+0xb4</c>, which is exactly where the game-side solvers write.</summary>
    private static void SyncFromShell(DbId obj)
    {
        nint shell = Facade.ObjectShell(obj);
        if (shell == 0) return;
        SceneDb.ObjPos[obj.Index] = Facade.ShellPos(shell);
        SceneDb.ObjRot[obj.Index] = Facade.ShellRot(shell);
    }

    /// <inheritdoc cref="DestroyObject"/>
    private static void DestroyViaBridge(DbId obj) => ObjectBridge.ObjectDestroy?.Invoke(obj);

    private static readonly nint[] _control = new nint[13];
    private static bool _controlResolved;
    private static int _solved, _dispatched, _noSync, _noDestroy;
    private static bool _noSyncSaid, _noDestroySaid;

    /// <summary>Resolve the thirteen engine <c>Control</c> addresses once, so a vptr can be classified by a
    /// pointer compare. An unresolved one makes its class read as game-side, which dispatches to it -- the
    /// engine's own code, at the engine's own call site, which is the safe direction.</summary>
    private static void ResolveControls()
    {
        if (_controlResolved) return;
        _controlResolved = true;
        for (int i = 0; i < _control.Length && i < BehaviorSolvers.Symbols.Length; i++)
            _control[i] = Native.Sym(BehaviorSolvers.Symbols[i]);
    }

    /// <summary>Is this the chase camera's behaviour -- the one <c>CAurBehaviorCameraFollow</c> whose fields
    /// the camera-feel layer reads and whose yaw velocity it writes? Nothing else may be read as one.</summary>
    public static bool IsCameraFollow(nint beh) => beh != 0 && Kind(beh) == DbBehaviorKind.CameraFollow;

    private static DbBehaviorKind Kind(nint beh)
    {
        ResolveControls();
        if (*(nint*)beh == 0) return DbBehaviorKind.Unknown;
        nint ctl = new CAurBehavior((IntPtr)beh).ControlFn;
        for (int i = 0; i < _control.Length; i++)
            if (_control[i] != 0 && _control[i] == ctl) return (DbBehaviorKind)i;
        return DbBehaviorKind.Unknown;
    }

    /// <summary>Has this object's behaviour ever actually solved? <c>DbXform.Attach.cs</c>'s snap stands down
    /// on a behaviour that is PLACING the object, and "installed" is not the same thing: a GUI panel scene
    /// with its animations disabled never steps, so its controls' behaviours never run. A latch and not a time
    /// window, because <see cref="_frame"/> counts ANIMATE PASSES; <see cref="SetBehavior"/> clears it.</summary>
    private static readonly bool[] _behaveRan = new bool[SceneDb.MaxObjects];

    /// <summary>Is a behaviour placing this object? One is installed and has solved at least once.</summary>
    internal static bool BehaviourPlacing(int i) => _behavior[i] != 0 && _behaveRan[i];

    /// <summary>The dispatch point. False = the object died under the solve and nothing more may touch it.</summary>
    private static bool BehaviorControl(DbId obj, float dt, int depth)
    {
        nint beh = _behavior[obj.Index];
        if (beh == 0) return true;
        if (depth >= MaxDepth) return true;
        _behaveRan[obj.Index] = true;

        DbBehaviorKind kind = Kind(beh);
        // The link the death edge sweeps on. A GUI control is attached through its BEHAVIOUR's target
        // (GuiAurAttachToObject never writes ObjAttachTo), so the column is the only place both kinds of
        // attachment can be seen from -- and it is read here, where every solve already passes.
        if (AttachFamily(kind))
        {
            DbId up = Facade.ObjectId(new CAurBehavior((IntPtr)beh).Target.Ptr);
            if (!up.IsNull) { _attachParent[obj.Index] = up; NoteAttached(up); }
        }
        switch (kind)
        {
            case DbBehaviorKind.Base: _solved++; return Base(beh, obj, dt, depth) >= 0;
            case DbBehaviorKind.Attach: _solved++; return Attach(beh, obj, dt, depth, 0);
            case DbBehaviorKind.AttachTranslate: _solved++; return Attach(beh, obj, dt, depth, 1);
            case DbBehaviorKind.AttachRotate: _solved++; return Attach(beh, obj, dt, depth, 2);
            case DbBehaviorKind.AttachNoOrientation: _solved++; return Attach(beh, obj, dt, depth, 3);
            case DbBehaviorKind.AttachGuiScale: _solved++; return Attach(beh, obj, dt, depth, 4);
            case DbBehaviorKind.Navigate: _solved++; return Navigate(beh, obj, dt, depth);
            case DbBehaviorKind.CameraFollow: _solved++; return CameraFollow(beh, obj, dt, depth);
            case DbBehaviorKind.CameraOnAStick: _solved++; return OnAStick(beh, obj, dt, depth);
            default: return Dispatch(beh, obj, dt);
        }
    }

    /// <summary>A behaviour whose <c>Control</c> is not one of the engine's: game-side code, called where the
    /// engine called it. It writes the object's placement into the facade shell by interior address, so the
    /// columns are re-read from the shell afterwards.</summary>
    private const int DispSayMax = 3;
    private static readonly byte[] _dispSaid = new byte[SceneDb.MaxObjects];

    private static bool Dispatch(nint beh, DbId obj, float dt)
    {
        _dispatched++;
        nint vtbl = *(nint*)beh;
        if (vtbl == 0) return true;
        var b = new CAurBehavior((IntPtr)beh);
        if (b.ControlFn == IntPtr.Zero) return true;
        b.Control(dt);
        if (!SceneDb.Objects.Valid(obj)) return false;
        if (CX.Alloy.Renderer.HitCheck.Boot && _dispSaid[obj.Index] < DispSayMax && ShellPoseSync != null)
        {
            _dispSaid[obj.Index]++;
            nint sh = SceneDb.ObjFacade[obj.Index];
            float* w = sh == 0 ? null : (float*)Unsafe.AsPointer(ref new Gob(sh).Position);
            // CAurBehaviorCrossSceneAttach::Control's inputs: the child's own behaviour word must be this
            // behaviour, the target live, and the part the one GetWorld is asked for.
            nint target = new CAurBehavior((IntPtr)beh).Target.Ptr;
            nint part = new CAurBehaviorAttach((IntPtr)beh).Part;
            string pw = "-";
            if (part != 0 && Seam.PartShell.Resolve(part, out DbId po, out int pi))
                pw = $"o={po.Index}/p={pi} \"{SceneDb.ObjModel[po.Index]?.Name ?? "-"}\" world={SceneDb.PartWorld[pi].Translation}";
            _log?.Info($"[dbbeh] Control o={obj.Index} \"{SceneDb.ObjModel[obj.Index]?.Name ?? "-"}\" "
                       + $"vtbl=0x{vtbl:x} column={SceneDb.ObjPos[obj.Index]} "
                       + (w == null ? "shell=-" : $"shell=<{w[0]}, {w[1]}, {w[2]}>")
                       + $" own={(sh == 0 ? 0 : new Gob(sh).Attach.Ptr) == beh} "
                       + $"target=o{Facade.ObjectId(target).Index} part={pw}");
        }
        if (ShellPoseSync == null)
        {
            _noSync++;
            if (!_noSyncSaid)
            {
                _noSyncSaid = true;
                _log?.Always("DbAnim: a game-side CAurBehavior solved but DbAnim.ShellPoseSync was cleared "
                           + "-- whatever it attached will NOT move.");
            }
            return true;
        }
        ShellPoseSync(obj);
        Moved(obj.Index);
        return true;
    }

    /// <summary>
    /// <c>CAurBehavior::Control</c> @0x4a0510: drag every dependency's animation forward -- which is what makes
    /// a behaviour's parent posed and composed before its part transform is read, whatever order the walk
    /// visited them in -- then answer "am I still the owner's behaviour". -1 means the object died.
    /// </summary>
    private static int Base(nint beh, DbId obj, float dt, int depth)
    {
        var b = new CAurBehavior((IntPtr)beh);
        for (int i = b.DepCount - 1; i >= 0; i--)
        {
            // Re-read the count: a dependency's animate can add or drop dependencies of ours.
            if (i >= b.DepCount) continue;
            nint dep = b.DepList[i] == 0 ? 0 : *(nint*)b.DepList[i];
            if (dep == 0) continue;
            DbId d = Facade.ObjectId(dep);
            if (d.IsNull) continue;
            StepObject(d, dt, depth + 1);
            if (!SceneDb.Objects.Valid(obj)) return -1;
        }
        return _behavior[obj.Index] == beh ? 1 : 0;
    }

    /// <summary><c>CAurBehaviorAttach::Control</c> @0x4a2810 and its four subclasses: snap the owner's
    /// placement to the parent part's WORLD transform, then apply the subclass's own offset.
    /// <paramref name="variant"/> is 0 attach, 1 translate, 2 rotate, 3 no-orientation, 4 GUI scale. The snap
    /// is a raw column write, as the engine's is -- a <c>SetPosition</c> override is not consulted.</summary>
    private static bool Attach(nint beh, DbId obj, float dt, int depth, int variant)
    {
        int o = obj.Index;
        Quaternion saved = SceneDb.ObjRot[o];
        int r = Base(beh, obj, dt, depth);
        if (r < 0) return false;
        if (r == 0) return true;
        if (_behavior[o] != beh) return true;

        var att = new CAurBehaviorAttach((IntPtr)beh);
        if (att.Part == IntPtr.Zero || new CAurBehavior((IntPtr)beh).Target.Ptr == IntPtr.Zero)
        {
            // NOT a death. The engine's own Control destroys the child here, and that is safe THERE only
            // because +0x20 is a SafePointer the engine nulls at exactly one moment: the parent's destructor.
            // In CX the same word reads null transiently -- CGuiPanel::Deactivate detaches a panel's controls
            // with AttachToScene(0), and those controls have DieWithoutParent set -- so destroying here killed
            // a whole panel's controls in one frame and the game re-attached the dead pointers when the panel
            // reopened. The death edge is Gob::~Gob's "Dying" (its one sender, @0x444140), which
            // DbAnim.OnRetire now raises; a null read in a frame is told to wait.
            _attachNoParent++;
            return true;
        }

        int part = ResolvePart == null ? -1 : ResolvePart(att.Part);
        if (part < 0) { _unresolvedPart++; return true; }
        PartGetWorld(part, out Vector3 pos, out Quaternion rot);
        SceneDb.ObjPos[o] = pos;
        SceneDb.ObjRot[o] = rot;

        switch (variant)
        {
            case 1:
                // The offset is in the parent part's frame, which is why it is rotated by the orientation the
                // snap just wrote.
                SceneDb.ObjPos[o] += Vector3.Transform(att.Offset, rot);
                break;
            case 2:
            {
                // The field is a Vector of DEGREES and the engine feeds it to YawPitchRoll as (z, x, y).
                Vector3 e = att.Offset;
                SceneDb.ObjRot[o] = N(BehaviorMath.Mul(Q(rot), BehaviorMath.YawPitchRoll(e.Z, e.X, e.Y)));
                break;
            }
            case 3:
                SceneDb.ObjRot[o] = saved;
                break;
            case 4:
            {
                float s = GuiScaleNow();
                if (s != 0f)
                {
                    Vector3 p = SceneDb.ObjPos[o];
                    // Z is deliberately not divided: the overlay keeps its depth.
                    SceneDb.ObjPos[o] = new Vector3(p.X / s, p.Y / s, p.Z);
                }
                break;
            }
        }
        Moved(o);
        return true;
    }

    private static int _unresolvedPart, _attachNoParent, _dyingKills, _dyingSkipped, _dyingByEvent;

    /// <summary>The live GUI scale (<c>CAurora::GetGUIScale</c>), resolved on first use. 1.0 when the symbol
    /// is absent, which only affects the GUI-scaled attach.</summary>
    private static float GuiScaleNow()
    {
        if (!_guiScaleTried)
        {
            _guiScaleTried = true;
            _guiScale = EngineFn.GetGuiScale;
        }
        return _guiScale == null ? 1f : _guiScale();
    }

    private static GetGuiScaleFn _guiScale;
    private static bool _guiScaleTried;

    /// <summary><c>CAurBehaviorNavigate::Control</c> @0x4a0660 -- the <c>navigate</c> console free-fly. Four
    /// inputs, each consumed and cleared by one solve.</summary>
    private static bool Navigate(nint beh, DbId obj, float dt, int depth)
    {
        int r = Base(beh, obj, dt, depth);
        if (r < 0) return false;
        if (r == 0) return true;
        int o = obj.Index;
        Quat q = Q(SceneDb.ObjRot[o]);
        float yaw = BehaviorMath.Yaw(q), pitch = BehaviorMath.Pitch(q), roll = BehaviorMath.Roll(q);
        var nav = new CAurBehaviorNavigate((IntPtr)beh);
        yaw += nav.YawRate * dt; nav.YawRate = 0f;
        pitch += nav.PitchRate * dt; nav.PitchRate = 0f;
        if (pitch <= 0f) pitch = 0f;
        (Vector3 fwd, Vector3 right) = BehaviorSolvers.NavBasis(yaw);
        Vector3 p = SceneDb.ObjPos[o];
        if (nav.Forward != 0f) p = new Vector3(fwd.X * nav.Forward * dt + p.X, fwd.Y * nav.Forward * dt + p.Y, p.Z);
        nav.Forward = 0f;
        if (nav.Strafe != 0f) p += right * (nav.Strafe * dt);
        nav.Strafe = 0f;
        nav.Spare = 0f;
        SceneDb.ObjPos[o] = p;
        SceneDb.ObjRot[o] = N(BehaviorMath.YawPitchRoll(yaw, pitch, roll));
        Moved(o);
        return true;
    }

    /// <summary><c>CAurBehaviorCameraFollow::Control</c> @0x4a2290 -- the chase camera: integrate yaw, pitch and
    /// distance from their velocity + impulse pairs (each consumed), clamp each against its own optional range,
    /// lag the focus toward the target, slerp the orientation with the SAME lag factor, and put the camera one
    /// distance along <c>q * +Z</c> from the focus.</summary>
    private static bool CameraFollow(nint beh, DbId obj, float dt, int depth)
    {
        int r = Base(beh, obj, dt, depth);
        if (r < 0) return false;
        int o = obj.Index;
        var cb = new CAurBehavior((IntPtr)beh);
        DbId target = Facade.ObjectId(cb.Target.Ptr);
        if (target.IsNull) { _behavior[o] = 0; return true; }
        var b = new CAurBehaviorCameraFollow((IntPtr)beh);

        float yaw = b.Yaw + b.YawVel * dt + b.YawImpulse;
        b.YawVel = 0f; b.YawImpulse = 0f;
        if (b.YawClamp != 0f) yaw = BehaviorMath.Clamp(yaw, b.YawMin[0], b.YawMin[1]);
        b.Yaw = yaw;

        float pitch = b.Pitch + b.PitchVel * dt + b.PitchImpulse;
        b.PitchVel = 0f; b.PitchImpulse = 0f;
        if (b.PitchClamp != 0f) pitch = BehaviorMath.Clamp(pitch, b.PitchMin[0], b.PitchMin[1]);
        b.Pitch = pitch;

        float dist = BehaviorSolvers.Zoom(b.Dist, b.ZoomVel, dt);
        b.ZoomVel = 0f;
        if (b.DistClamp != 0f) dist = BehaviorMath.Clamp(dist, b.DistMin[0], b.DistMin[1]);
        b.Dist = dist;

        float a = BehaviorSolvers.Lag(b.Lag, dt);
        ref Vector3 focus = ref b.Focus;
        Vector3 next = BehaviorSolvers.FocusStep(focus, SceneDb.ObjPos[target.Index], b.Offset, a);
        Quat want = BehaviorMath.YawPitchRoll(yaw, pitch, 0f);
        Quat now = BehaviorMath.Normalize(BehaviorMath.Slerp(Q(SceneDb.ObjRot[o]), want, a));
        SceneDb.ObjRot[o] = N(now);
        SceneDb.ObjPos[o] = BehaviorSolvers.Rig(next, dist, now);
        focus = next;
        Moved(o);
        return true;
    }

    /// <summary><c>CAurBehaviorCameraOnAStick::Control</c> @0x4a0c70 -- a 2-D spring in XY at a fixed height.
    /// ponytail: UNVERIFIABLE -- nothing in this client constructs the class,
    /// so neither version can be compared against the engine. The period line's <c>onastick</c> count is what
    /// would say it ever fired.</summary>
    private static bool OnAStick(nint beh, DbId obj, float dt, int depth)
    {
        int r = Base(beh, obj, dt, depth);
        if (r < 0) return false;
        int o = obj.Index;
        var cb = new CAurBehavior((IntPtr)beh);
        DbId target = Facade.ObjectId(cb.Target.Ptr);
        if (target.IsNull) { _behavior[o] = 0; return true; }
        var st = new CAurBehaviorCameraOnAStick((IntPtr)beh);
        Vector3 cam = SceneDb.ObjPos[o], tgt = SceneDb.ObjPos[target.Index];
        float height = st.Height, stick = st.Length;
        Vector3 d = new(tgt.X - cam.X, tgt.Y - cam.Y, 0f);
        float len = d.Length();
        if (len <= 0f || stick == 0f) return true;
        float push = (len - stick) / stick * dt * st.Gain;
        Vector3 want = new(cam.X + d.X / len * push, cam.Y + d.Y / len * push, height);
        Vector3 dir = st.Dir, aim = d;
        if (st.Mode != 0 && dir.Length() > 0f)
        {
            float back = new Vector3(tgt.X - want.X, tgt.Y - want.Y, 0f).Length();
            want = new Vector3(tgt.X - back * dir.X, tgt.Y - back * dir.Y, height);
            aim = dir;
        }
        SceneDb.ObjRot[o] = N(BehaviorMath.YawPitchRoll(BehaviorMath.YawOf(aim), st.Pitch, 0f));
        SceneDb.ObjPos[o] = want;
        st.Last = want;
        Moved(o);
        return true;
    }

    /// <summary>The parent's DEATH EDGE, raised from <see cref="DbAnim.ObjectRetired"/> while the record is
    /// still readable -- the edge <c>Gob::~Gob</c> @0x444140 raises with <c>DoEvent("Dying", nullptr)</c>. The
    /// event goes out FIRST: the game's <c>UpdateScenesAttach</c> @0x49fc70 listener deletes the owner when
    /// <c>DieWithoutParent</c> is set. CX then walks the attach links itself, lest a child outlive its parent.</summary>
    internal static void ObjectDying(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        // Measured across the event, because vanilla's OWN listener destroys through its own path and would
        // otherwise be invisible: UpdateScenesAttach @0x49fc70 is registered on this object by every attach
        // behaviour's constructor, and on "Dying" it calls the attached owner's deleting destructor. A
        // non-zero count here is that mechanism working, not CX destroying anything.
        int liveBefore = SceneDb.Objects.Live;
        EventFire(obj, (byte*)CX.Alloy.Seam.EventNames.Dying, 0);
        int byEvent = liveBefore - SceneDb.Objects.Live;
        if (byEvent > 0) _dyingByEvent += byEvent;
        if (!SceneDb.Objects.Valid(obj) || !_hasKids[i]) return;
        if (_dyingDepth >= MaxAttach) { _dyingSkipped++; return; }
        _dyingDepth++;
        try { KillChildren(obj, i); }
        finally { _dyingDepth--; }
    }

    private static int _dyingDepth;

    /// <summary>Destroy the objects attached to <paramref name="obj"/> whose behaviour says they die with it.
    /// Scanned, not indexed: only an object something has actually been attached to carries
    /// <see cref="_hasKids"/>, so the sweep is paid once per such parent and never on the thousands of
    /// childless records an area change retires.</summary>
    private static void KillChildren(DbId obj, int i)
    {
        int high = SceneDb.Objects.High;
        for (int c = 1; c < high; c++)
        {
            if (!SceneDb.Objects.LiveAt(c) || c == i) continue;
            if (SceneDb.ObjAttachTo[c].Value != obj.Value && _attachParent[c].Value != obj.Value) continue;
            nint beh = _behavior[c];
            if (beh == 0) continue;
            if (!AttachFamily(Kind(beh))) continue;
            // The engine's own test, on the engine's own field: UpdateScenesAttach reads +0x3c and deletes
            // only when it is set; everything else merely loses its parent.
            if (new CAurBehaviorAttach((IntPtr)beh).DieWithoutParent == 0) continue;
            DbId child = SceneDb.Objects.IdAt(c);
            if (DestroyObject == null || ObjectBridge.ObjectDestroy == null)
            {
                _noDestroy++;
                if (!_noDestroySaid)
                {
                    _noDestroySaid = true;
                    _log?.Always("DbAnim: an object should die with the parent it is attached to, but no "
                               + "object destroy is wired (ObjectBridge.ObjectDestroy) -- it LINGERS.");
                }
                continue;
            }
            _dyingKills++;
            DestroyObject(child);
        }
    }

    /// <summary>A <c>CAurBehaviorCrossSceneAttach</c>(WithOffset): the GUI layout's attach (<c>GuiAurAttachToObject</c> @0x68b050).</summary>
    internal static bool IsCrossSceneAttach(nint beh)
        => beh != 0 && Kind(beh) is DbBehaviorKind.CrossSceneAttach or DbBehaviorKind.CrossSceneAttachOffset;

    /// <summary>Is this one of the behaviours that place an object on a parent's part? Only those carry the
    /// <c>DieWithoutParent</c> field at <c>+0x3c</c>, so only those may be read for it.</summary>
    private static bool AttachFamily(DbBehaviorKind k)
        => k is DbBehaviorKind.Attach or DbBehaviorKind.AttachTranslate or DbBehaviorKind.AttachRotate
             or DbBehaviorKind.AttachNoOrientation or DbBehaviorKind.AttachGuiScale
             or DbBehaviorKind.CrossSceneAttach or DbBehaviorKind.CrossSceneAttachOffset;

    /// <summary>Per child: the parent its attach BEHAVIOUR named, last time it solved. The engine's own
    /// linkage -- <c>UpdateScenesAttach</c> is registered on the parent and carries the behaviour, whose
    /// <c>+0x20</c> is that parent -- and the only link a GUI control has, since
    /// <c>GuiAurAttachToObject</c> leaves <see cref="SceneDb.ObjAttachTo"/> empty.</summary>
    internal static readonly DbId[] _attachParent = new DbId[SceneDb.MaxObjects];

    /// <summary>Objects something has been attached to. The death edge's sweep is skipped entirely for a
    /// record this is false for, which is nearly all of them.</summary>
    private static readonly bool[] _hasKids = new bool[SceneDb.MaxObjects];

    /// <summary>Note that <paramref name="parent"/> has an attached child, from wherever the link is read.</summary>
    internal static void NoteAttached(DbId parent)
    {
        if (SceneDb.Objects.Valid(parent)) _hasKids[parent.Index] = true;
    }

    /// <summary>The behaviour layer's period numbers.</summary>
    internal static string BehaviorPeriod()
        => $"{_solved} solved, {_dispatched} dispatched to game code"
         + (_unresolvedPart == 0 ? "" : $", {_unresolvedPart} attach(es) with an UNRESOLVED parent part")
         + (_noSync == 0 ? "" : $", {_noSync} unsynced")
         + (_attachNoParent == 0 ? "" : $", {_attachNoParent} attach solve(s) with no parent this frame (waiting, not destroyed)")
         + (_dyingKills == 0 ? "" : $", {_dyingKills} child(ren) died with their parent")
         + (_dyingByEvent == 0 ? "" : $", {_dyingByEvent} taken by the \"Dying\" event's own listeners")
         + (_dyingSkipped == 0 ? "" : $", {_dyingSkipped} death edge(s) past the depth guard")
         + (_noDestroy == 0 ? "" : $", {_noDestroy} orphan(s) kept alive");

    private static Quat Q(Quaternion q) => new() { W = q.W, X = q.X, Y = q.Y, Z = q.Z };
    private static Quaternion N(Quat q) => new(q.X, q.Y, q.Z, q.W);
}

/// <summary>The camera half of the animation step. A camera has no animations of its own -- its
/// <c>PlayAnimation</c> slot is <c>return 0</c> in the engine too -- so its <c>Animate</c> is exactly its
/// BEHAVIOUR's solve, which is what moves a chase camera, plus the compose that makes the view matrix current
/// for the <c>RenderScene</c> that follows in the same frame.</summary>
public static unsafe class DbAnimCamera
{
    /// <summary>The camera-feel layer's two edges around the follow solve, installed by
    /// <c>CX.Services.Camera.CameraService</c>. They stand in for a funchook chain on
    /// <c>CAurBehaviorCameraFollow::Control</c>, which nothing calls. Both take the behaviour, read before the
    /// solve so a solve that detaches it cannot free the pointer under the second edge.</summary>
    public static delegate*<IntPtr, float, void> FeelBefore, FeelAfter;

    /// <summary>Advance camera <paramref name="cam"/> by <paramref name="dt"/>: solve the behaviour on the
    /// object record the camera's <c>Gob</c> half IS (<see cref="SceneDb.CamObj"/>), then recompose it.</summary>
    public static void Animate(DbId cam, float dt)
    {
        if (!SceneDb.Cameras.Valid(cam)) return;
        DbId obj = SceneDb.CamObj[cam.Index];
        nint beh = DbAnim.Behavior(obj);
        bool feel = beh != 0 && (FeelBefore != null || FeelAfter != null) && DbAnim.IsCameraFollow(beh);
        if (feel && FeelBefore != null) FeelBefore((IntPtr)beh, dt);
        DbAnim.AnimateObject(obj, dt);
        if (feel && FeelAfter != null && SceneDb.Cameras.Valid(cam)) FeelAfter((IntPtr)beh, dt);
    }
}
