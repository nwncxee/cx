using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Per-object animation state: the <c>Gob</c> gate bytes this replaces.</summary>
[Flags]
internal enum ObjAnim : uint
{
    None = 0,
    /// <summary><c>Gob::IsAnimating</c> -- the object's own gate, on at creation.</summary>
    Animating = 1 << 0,
    /// <summary><c>Gob::IsForceAnimated</c> -- exempt from the SCENE's gate.</summary>
    Forced = 1 << 1,
    /// <summary><c>Gob::AnimateOffScreen</c> -- exempt from the view-volume gate.</summary>
    OffScreen = 1 << 2,
    /// <summary><c>Gob::SetSynchronizeAnimations</c> -- take the step from the attach parent.</summary>
    Sync = 1 << 3,
    /// <summary>The record has been seated by <see cref="DbAnim.ObjectCreated"/>.</summary>
    Seated = 1 << 4,
    /// <summary>Something wrote this object's pose or placement since the last compose.</summary>
    Moved = 1 << 5,
    /// <summary>The composed matrices are current but the SKIN palette taken from them is not: the pose layer's
    /// compose ran inside the step and nothing has moved the object since. <see cref="DbAnim.Finish"/> consumes
    /// it. Set and cleared in the same step, so it needs no lifecycle edge beyond <c>OnCreate</c>'s reset.</summary>
    SkinDue = 1 << 6,
}

/// <summary>The engine's <c>AnimRun+0xc</c> flag bits, as <c>Gob::PlayAnimation</c> @0x49a750 and
/// <c>Gob::Animate</c> @0x49d8c0 use them. Public because the seam passes the game's own int through.</summary>
public static class RunFlags
{
    /// <summary>The run is fading out; its weight ramps down and it is dropped at zero.</summary>
    public const int FadeOut = 0x1;        // not-an-offset: AnimRun+0xc flag bit
    /// <summary>Start at full weight: no fade-in, and the animation's own cross-fade time is not taken.</summary>
    public const int NoFadeIn = 0x2;       // not-an-offset: AnimRun+0xc flag bit
    /// <summary>Loop, firing <c>loop&lt;name&gt;</c> at each wrap.</summary>
    public const int Loop = 0x4;           // not-an-offset: AnimRun+0xc flag bit
    /// <summary>An add-in run: it layers over the queue and does not evict the runs below it.</summary>
    public const int AddIn = 0x8;          // not-an-offset: AnimRun+0xc flag bit
    /// <summary>Take the starting time from the run below this one.</summary>
    public const int InheritTime = 0x10;   // not-an-offset: AnimRun+0xc flag bit
    /// <summary>Queued: this run does not advance until the run below it finishes.</summary>
    public const int Queued = 0x20;        // not-an-offset: AnimRun+0xc flag bit
    /// <summary>Do not look for an existing run of the same animation to reuse.</summary>
    public const int NoReuse = 0x40;       // not-an-offset: PlayAnimation argument bit
    /// <summary>Act only on a run that already exists (and end it); never create one.</summary>
    public const int EndOnly = 0x80;       // not-an-offset: PlayAnimation argument bit
    /// <summary>The trace channel. Nothing in ordinary play sets it and its engine reporter has no DB form, so
    /// a run carrying it is animated normally and the bit is counted (<c>trace</c> on the period line).</summary>
    public const int Trace = 0x100;        // not-an-offset: AnimRun+0xc flag bit
    /// <summary>Fade out NOW rather than ramping (and, on an existing run of the same name, fade that one).</summary>
    public const int FadeNow = 0x200;      // not-an-offset: AnimRun+0xc flag bit
}

/// <summary>The animation runtime, behaviour solvers, dangly and skin poses over <see cref="SceneDb"/>, and the
/// transform step that composes <c>ObjWorld</c>/<c>PartWorld</c>. Game thread. This replaces
/// <c>Scene::Animate</c> + <c>Gob::Animate</c>: the queue advance, the pose walk, the keyframe and lifecycle
/// events, the behaviour solve and the look-at. The one engine-layout object it reads is <c>CAurBehavior</c>.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Objects that may hold a run queue at once. An object takes a block on its first
    /// <see cref="PlayAnimation"/> and gives it back when its queue empties, so this bounds the objects
    /// ANIMATING at one instant, not the objects in the area (the heaviest measured area is ~3k gobs, nearly
    /// all of them static).</summary>
    private const int MaxBlocks = 4096;
    /// <summary>Runs one object may stack. The engine's queue is unbounded; two is the common depth (a pose and
    /// the one fading out of it) and add-ins take the rest. An overflow drops the OLDEST run, is counted and is
    /// said once -- never a quiet hole.</summary>
    private const int MaxRuns = 8;
    /// <summary>The attach chain's runaway guard, and the depth the dependency walk descends.</summary>
    private const int MaxAttach = 8, MaxDepth = 64;
    /// <summary>The engine's <c>sprintf</c> buffer for a lifecycle event name.</summary>
    private const int NameMax = 0x40;   // not-an-offset: the engine's sprintf buffer size

    // ---- per-object state (index-aligned columns; the game thread is the only writer) ----

    private static readonly ObjAnim[] _flags = new ObjAnim[SceneDb.MaxObjects];
    /// <summary>The add-in animation source searched ahead of the object's own model (<c>Gob+0x80</c>).</summary>
    private static readonly ModelDef?[] _addIn = new ModelDef?[SceneDb.MaxObjects];
    /// <summary>The animation-speed multiplier and its linear ramp (the engine's <c>Gob+0x400</c> lerp).</summary>
    private static readonly float[] _speed = new float[SceneDb.MaxObjects];
    private static readonly float[] _speedTo = new float[SceneDb.MaxObjects];
    private static readonly float[] _speedRate = new float[SceneDb.MaxObjects];
    /// <summary>The visual-transform scale an ATTACHED child multiplies in (<c>Gob+0x1c0</c>).</summary>
    private static readonly float[] _visScale = new float[SceneDb.MaxObjects];
    private static readonly float[] _visScaleTo = new float[SceneDb.MaxObjects];
    private static readonly float[] _visScaleRate = new float[SceneDb.MaxObjects];
    /// <summary>Carries a script visual transform (the compose folds the adjusted getters in), and one of its
    /// lerps is still travelling (recompose every frame until it lands).</summary>
    private static readonly bool[] _vt = new bool[SceneDb.MaxObjects], _vtLerp = new bool[SceneDb.MaxObjects];
    /// <summary>The run block this object holds, or -1. Seeded to -1, never 0-filled: 0 is a VALID block
    /// index, so a zero column reads as "this object already holds block 0" -- every object would then share
    /// one run queue, and the first two creations would push block 0 onto the free list twice and leave it
    /// pointing at itself, so every later take handed out block 0 as well.</summary>
    private static readonly int[] _block = NoBlocks();

    /// <inheritdoc cref="_block"/>
    private static int[] NoBlocks()
    {
        var a = new int[SceneDb.MaxObjects];
        Array.Fill(a, -1);
        return a;
    }

    private static readonly byte[] _runN = new byte[SceneDb.MaxObjects];
    /// <summary>The frame this object was last animated and last composed, so a second visit in one frame is
    /// the engine's own double-step refusal and an on-demand compose is free.</summary>
    private static readonly int[] _animAt = new int[SceneDb.MaxObjects];
    private static readonly int[] _composeAt = new int[SceneDb.MaxObjects];

    // ---- the run pool ----

    private static readonly AnimDef?[] _rAnim = new AnimDef?[MaxBlocks * MaxRuns];
    /// <summary>The model whose chain resolved this run's animation: the binding cache's key, and what the
    /// animation scale was taken against.</summary>
    private static readonly ModelDef?[] _rModel = new ModelDef?[MaxBlocks * MaxRuns];
    private static readonly float[] _rRate = new float[MaxBlocks * MaxRuns];
    private static readonly float[] _rPrev = new float[MaxBlocks * MaxRuns];
    private static readonly float[] _rTime = new float[MaxBlocks * MaxRuns];
    private static readonly float[] _rBlend = new float[MaxBlocks * MaxRuns];
    private static readonly float[] _rFade = new float[MaxBlocks * MaxRuns];
    private static readonly float[] _rPosScale = new float[MaxBlocks * MaxRuns];
    private static readonly int[] _rFlags = new int[MaxBlocks * MaxRuns];
    private static readonly byte[] _rDone = new byte[MaxBlocks * MaxRuns];
    private static readonly int[] _blockFree = new int[MaxBlocks];
    private static int _blockHead = -1, _blockHigh;

    private static ExtenderLog? _log;
    private static int _frame = 1;
    /// <summary>The dirty count the hit test was last folded at: the fold is per FRAME, and Animate is per
    /// scene. A shrink means the consumer took the list, so the next fold is due.</summary>
    private static long _foldedAt = -1;
    private static int _foldSkipped;
    private static Vector3 _eye;

    /// <summary>Per-period counters. Each refusal is a hole in a pose, so each has its own count rather than a
    /// shared "errors".</summary>
    private static int _objects, _skipped, _runs, _crossfades, _dropped, _lookAts, _posed, _controllers;
    private static int _queueFull, _unbound, _noBlock, _bezier, _morphs, _traced;

    /// <summary>The animation clock the GAME handed this period, summed. NOT the global <c>DeltaT</c> the particle
    /// sim reads (the pause-frozen timer at <c>CClientExoAppInternal+0x50</c>): the <c>Animate</c> slot's dt comes
    /// from <c>CClientAIMaster::UpdateState</c> @0xa91c30 via the timer at <c>+0x60</c>, snapshotted
    /// unconditionally. So animation time with no particle time is a PAUSED game, and neither is no game update.</summary>
    private static float _periodDt;
    /// <inheritdoc cref="_periodDt"/>
    private static int _periodCalls;

    /// <summary>Bind the log. seam-obj's <c>SeamService</c> calls this once at boot, in DB mode only.</summary>
    public static void Bind(ExtenderLog log) => _log = log;

    /// <summary>The frame counter the per-object stamps are taken against. Read by the self-check.</summary>
    internal static int Frame => _frame;

    private static void Eye(Vector3 eye) => _eye = eye;

    /// <summary>Advance every animated object of <paramref name="scene"/> by <paramref name="dt"/> seconds
    /// (the CAurScene <c>Animate</c> slot), then compose the world matrices of what moved.</summary>
    public static void Animate(DbId scene, float dt)
    {
        if (!SceneDb.Scenes.Valid(scene)) return;
        long t0 = FrameZones.Now, hit0 = FrameZones.Ticks(FrameZones.Zone.HitFoldAi);
        _frame++;
        // No _poseGen bump: Animate runs once per SCENE (~8 a frame), and a bump here recomposed every dirty
        // object at each one (24k part composes a frame for ~94 that stepped). A step marks Moved, which
        // Stale() honours under any generation. CX-188: nothing bumps it in a live frame any more -- Settle's
        // once-a-frame bump was the same mistake one level up (see DbXform._poseGen).
        // Each frame re-walks its whole dirty list once; inside the frame the tail cursor makes a getter's
        // compose O(what has changed since the last one).
        _dirtyUpTo = 0;
        _danglyDt = dt;
        _periodDt += dt; _periodCalls++;
        int si = scene.Index;
        DbList? members = SceneDb.SceneObjects[si];
        // Scene::Animate @0x469810: BACKWARDS, re-reading the count every step, so a member that removes
        // itself -- or anything above it -- during its own animate is neither skipped nor visited twice.
        if (members != null)
            for (int k = members.Count - 1; k >= 0; k--)
            {
                if (k >= members.Count) continue;
                StepObject(members[k], dt, 0);
            }
        DanglyDrain();
        LodChoose(si);
        ComposePending();
        // After the world matrices and before anything consumes the dirty list: the hit test's bounds and its
        // index are folded from the same change set (CONTRACTS, the hit stream). It does not consume the list
        // -- which is exactly why it must not be called once per SCENE. Animate runs per scene and the dirty
        // list is global, so a frame with fifteen panel scenes folded the same entries fifteen times: the
        // ladder measured 409207 folds and 7134468 index refiles of which TWELVE had actually moved. The list
        // only grows within a frame, so its length is the whole test.
        long dirtyNow = SceneDb.Objects.DirtyMark;
        if (dirtyNow != _foldedAt)
        {
            _foldedAt = dirtyNow;
            long tHit = FrameZones.Now;
            HitFeed.Fold();
            FrameZones.Add(FrameZones.Zone.HitFoldAi, tHit);
        }
        else _foldSkipped++;
        // Net of the fold, which has its own bracket in the same (Ai) zone: counting it in both would make the
        // two nested numbers add up to more than the Animate slot actually costs.
        FrameZones.Add(FrameZones.Zone.Anim, t0, FrameZones.Ticks(FrameZones.Zone.HitFoldAi) - hit0);
    }

    /// <summary>Compose world matrices for the dirty objects of <paramref name="scene"/> (also called on demand
    /// by the by-value getters, so a getter after a setter answers the new value without waiting for a
    /// frame).</summary>
    public static void Compose(DbId scene) => ComposePending();

    /// <summary>The frame boundary's last compose, just before the dirty lists are taken: an object created or
    /// moved after the frame's last scene animate (the main menu rebuilt as a NUI window closes) would otherwise
    /// leave the list uncomposed and keep its slot's previous tenant's world matrix.</summary>
    public static void Settle()
    {
        long t = FrameZones.Now, hit0 = FrameZones.Ticks(FrameZones.Zone.HitFold);
        // The WHOLE list, as Animate walks it: an object already on it, behind the cursor, that moved again is
        // not re-appended, so a tail walk would miss it. The compose stamp and staleness test keep this cheap.
        // CX-188: and NO _poseGen bump. The walk is a compare per entry; a bump made it a COMPOSE per entry,
        // which is 977 objects / 10,6k parts a frame recomposed for the ~94 that moved. See DbXform._poseGen.
        _dirtyUpTo = 0;
        _partDirtyUpTo = 0;
        ComposePending();
        _foldedAt = SceneDb.Objects.DirtyMark;
        // CX-188: an ORDINARY fold. It used to be Fold(full: true) because a re-mark that added no bit and
        // drove no compose (a text box set twice) got past the fold's stamp -- and paying 798 HitBounds
        // refreshes a frame to catch it was the wrong trade. HitFeed compares the table's mark clock now,
        // which sees that re-mark directly.
        long tHit = FrameZones.Now;
        HitFeed.Fold();
        FrameZones.Add(FrameZones.Zone.HitFold, tHit);
        FrameZones.Add(FrameZones.Zone.Settle, t, FrameZones.Ticks(FrameZones.Zone.HitFold) - hit0);
    }

    /// <summary>One object advanced outside any scene walk (<c>CAurObject::AnimateExternal</c> @0x53a560).
    /// The engine brackets the <c>Gob::Animate</c> call with the scene's external-animate counter; in DB mode
    /// the counter has no reader, and the step that matters is that the object advances exactly once --
    /// which is the frame stamp, bumped here so an external animate and the walk do not both run.</summary>
    private static void AnimateOneExternal(DbId obj, float dt)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        long t0 = FrameZones.Now;
        // The engine's AnimateExternal bumps the scene's counter either side of the Gob::Animate call, which
        // is exactly "let this one object step again". Forcing its own stamp says that without giving every
        // OTHER object a second step this frame.
        _animAt[obj.Index] = 0;
        StepObject(obj, dt, 0);
        ComposePending();
        FrameZones.Add(FrameZones.Zone.Anim, t0);
    }

    // ================= one object =================

    /// <summary>One object's whole animate step, in the engine's order (<c>Gob::Animate</c> @0x49d8c0): the
    /// frame-serial guard, the behaviour solve, the gates, the look-at, the queue advance (pose then events per
    /// run), the part walk and the compose. <paramref name="depth"/> bounds the dependency recursion the base
    /// behaviour solve makes. False = the object died under something this step ran.</summary>
    private static bool StepObject(DbId obj, float dt, int depth)
    {
        if (!SceneDb.Objects.Valid(obj)) return false;
        int i = obj.Index;
        // The engine's frame-serial guard (Gob+0x458 against Scene+0x1b8): an object reached twice in one
        // frame -- once by the walk and once as somebody's attach dependency -- advances once.
        if (_animAt[i] == _frame) { _skipped++; return true; }
        MovedGust(i);
        if (Idle(i)) { _idle++; return true; }
        _animAt[i] = _frame;
        _objects++;

        Ramp(i, dt);

        // BEFORE the pose and before any event: an attachment is solved against the transform its parent had
        // at the end of the previous frame, and the base solver animates each dependency first.
        if (!BehaviorControl(obj, dt, depth)) return false;
        if (!SceneDb.Objects.Valid(obj)) return false;

        if (!Gates(obj)) { Finish(obj); return true; }

        // the pose layer puts its last corrections back BEFORE the pose: a crossfade blends from the column
        bool layer = PoseLayer(i);
        if (layer && PoseBefore(obj, dt)) Moved(i);
        int n = _runN[i];
        if (n != 0 && !StartQueued(obj, n - 1)) return false;
        if (!Advance(obj, SyncDt(i, dt), _runN[i])) return false;
        // AFTER the queue: the engine's look-at is an add-in run at index 0, so it is the LAST thing to write
        // the head's pose. Before the advance it would be overwritten by the animation's own head track at
        // weight 1 and the head would never turn.
        if (!LookAtStep(obj, dt)) return false;
        if (layer && SceneDb.Objects.Valid(obj))
        {
            // The compose clears the moved bit Finish keys the final compose and the skin on. CX-188: the two
            // are keyed SEPARATELY. A creature whose pose layer corrected nothing still needs the skin --
            // its palette is read back out of the matrices this compose just wrote -- but it does NOT need
            // those matrices composed a second time, and re-asserting Moved to reach the skin recomposed
            // every creature's whole part tree twice a frame (ComposeIndex + SnapAttached, 5.4% of the game
            // thread measured, of which half was this).
            bool moved = (_flags[i] & ObjAnim.Moved) != 0;
            ComposeObject(obj);
            if (PoseAfter(obj, dt)) Moved(i);
            else if (moved) _flags[i] |= ObjAnim.SkinDue;
        }
        Finish(obj);
        return true;
    }

    /// <summary>
    /// Has this record anything to do at all? A scene's member list is walked every frame and in a settled area
    /// nearly every member has no run queue, behaviour, look-at, ramp or dangly part, so this test is what makes
    /// stepping them free. Every term is a transition something else already records, so nothing needs
    /// invalidating; <see cref="ObjAnim.Moved"/> is deliberately NOT one -- a moved object composes from the dirty list.
    /// </summary>
    private static bool Idle(int i)
        => _runN[i] == 0 && _behavior[i] == 0 && !_lookAt[i].Active
        && _speedRate[i] <= 0f && _visScaleRate[i] <= 0f && !_vtLerp[i]
        && !_hasDangly[i] && !_hasSkin[i];

    private static int _idle;

    /// <summary><c>Gob::Animate</c>'s point-source gust @0x59dc66: a wind-enabled object that MOVED since its last
    /// step registers one source at its new position -- radius 1.0, life the distance moved, strength the object's
    /// own <c>Gob+0x1a0</c>. This term is the whole of the felt wind. It also stamps the previous position
    /// <c>Gob::GetPreviousPosition</c> answers. Hot, once per member per frame: flag test first, no allocation.</summary>
    private static void MovedGust(int i)
    {
        if ((SceneDb.ObjSeamFlags[i] & (uint)SeamFlag.Wind) == 0) return;
        Vector3 now = SceneDb.ObjWorld[i].Translation;
        Vector3 was = SceneDb.ObjSeamPrevPos[i];
        SceneDb.ObjSeamPrevPos[i] = now;
        // The engine's "no previous position yet" sentinel, which Gob::Gob seeds all three words with.
        if (was.X == SceneDb.NoPrevPos || was == now) return;
        if (!CX.Alloy.AlloyWind.Owned) return;
        Vector3 d = now - was;
        CX.Alloy.AlloyWind.AddGust(now, GustRadius, d.Length(), SceneDb.ObjSeamWind[i]);
        _gusts++;
    }

    /// <summary>The radius <c>Gob::Animate</c>'s gust call site passes.</summary>
    private const float GustRadius = 1f;

    private static long _gusts;

    /// <summary>Per object, scanned once per part range: does it hold a dangly part, and does it hold a
    /// skinned one? Both were a scan of the whole range on EVERY step before.</summary>
    private static readonly bool[] _hasDangly = new bool[SceneDb.MaxObjects];
    private static readonly bool[] _hasSkin = new bool[SceneDb.MaxObjects];

    /// <summary>Re-derive the two tree facts. Called wherever the part range changes -- creation, a tree
    /// swap -- and nowhere else.</summary>
    private static void ScanTree(int i)
    {
        bool dangly = false, skin = false;
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            ModelDef? m = SceneDb.PartModel[p];
            if (m == null) continue;
            int node = SceneDb.PartNode[p];
            if ((uint)node >= (uint)m.Nodes.Length) continue;
            NodeDef nd = m.Nodes[node];
            if (nd.Kind == NodeKind.Dangly) dangly = true;
            if (nd.Skin != null) skin = true;
        }
        _hasDangly[i] = dangly;
        _hasSkin[i] = skin;
    }

    /// <summary>The three animate gates, in the engine's order: the scene's unless the object is
    /// force-animated, then the object's own.</summary>
    private static bool Gates(DbId obj)
    {
        int i = obj.Index;
        DbId scene = SceneDb.ObjScene[i];
        if (SceneDb.Scenes.Valid(scene) && !SceneGate(scene) && (_flags[i] & ObjAnim.Forced) == 0) return false;
        return (_flags[i] & ObjAnim.Animating) != 0;
    }

    /// <summary>
    /// The gate the engine has and this does NOT: <c>Gob::Animate</c> @0x59d8c0 advances no clock and fires no
    /// event when <c>Gob::IsInViewVolume</c> @0x544780 is false and the model is not effect-classified. Unportable
    /// as written -- the flag is <c>ManageSceneBSP</c>'s @0x57fa80, from the render Alloy replaced. ponytail: a
    /// per-object route stamp buys it, once <c>HitBounds</c>, <c>DbLights</c> and <c>CasterPass</c> are answered.
    /// </summary>
    private static void VisibilityGateNotPorted() { }

    /// <summary>The time a synchronised object advances by: its attach parent's own rate, so a head, a cloak
    /// and a held weapon stay on the body's frame (<c>Gob::SetSynchronizeAnimations</c>).</summary>
    private static float SyncDt(int i, float dt)
    {
        if ((_flags[i] & ObjAnim.Sync) == 0) return dt;
        DbId p = SceneDb.ObjAttachTo[i];
        return SceneDb.Objects.Valid(p) ? dt * _speed[p.Index] : dt;
    }

    /// <summary>The two linear ramps that stand in for the engine's <c>LerpFloat</c> pair.</summary>
    private static void Ramp(int i, float dt)
    {
        if (_speedRate[i] > 0f)
        {
            float s = _speed[i], t = _speedTo[i], step = _speedRate[i] * dt;
            _speed[i] = MathF.Abs(t - s) <= step ? t : s + MathF.CopySign(step, t - s);
            if (_speed[i] == t) _speedRate[i] = 0f;
        }
        if (_visScaleRate[i] > 0f)
        {
            float s = _visScale[i], t = _visScaleTo[i], step = _visScaleRate[i] * dt;
            _visScale[i] = MathF.Abs(t - s) <= step ? t : s + MathF.CopySign(step, t - s);
            if (_visScale[i] == t) _visScaleRate[i] = 0f;
            Moved(i);
        }
        if (_vtLerp[i]) Moved(i);
    }

    /// <summary>The tail of a step: the part walk (which is where the dangly solve hangs) and the compose, so a
    /// getter right after this answers from a current matrix.</summary>
    private static void Finish(DbId obj)
    {
        int i = obj.Index;
        PartWalk(obj);
        ObjAnim f = _flags[i];
        if ((f & (ObjAnim.Moved | ObjAnim.SkinDue)) == 0) return;
        // Only when the matrices are actually out of date: ObjAnim.SkinDue says they are current and only the
        // palette is owed (CX-188, the double compose).
        if ((f & ObjAnim.Moved) != 0) ComposeObject(obj);
        else _skinOnly++;
        _flags[i] &= ~ObjAnim.SkinDue;
        // Before the palettes, because a stitch replaces the skin a palette is solved against. Rest-pose
        // work, so it is owed once per tree change and never per frame.
        StitchIfDue(obj);
        // Last, because a palette is composed world matrices read back: the pose, then the compose, then the
        // skin. The engine reached the same order by accident -- it solved inside the draw.
        SkinObject(obj);
        SkinPieces(obj, 0);
    }

    /// <summary>Steps whose tail re-skinned from matrices that were already current -- the compose
    /// <see cref="ObjAnim.SkinDue"/> replaced. On the period line because a drop to zero would mean the
    /// separation stopped working and the double compose is back.</summary>
    private static int _skinOnly;

    /// <summary>Note that this object's placement or pose changed, for the change-driven compose and for
    /// seam-feed's record rewrite.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Moved(int i)
    {
        _flags[i] |= ObjAnim.Moved;
        SceneDb.Objects.Mark(i, Dirty.Xform);
    }

    /// <summary>A part's pose was written from outside the runtime (<c>Gob::SetPartPosition</c> and kin): the
    /// next getter recomposes even inside a pass that already composed the object -- staleness reads only the
    /// object's own placement, so <c>CGuiEditScroll::SetHeightByAnchor</c> @0x693a10, which moves "text1"
    /// and reads it back in one breath, got the old corner and a box of the old height.</summary>
    public static void PoseWritten(DbId obj)
    {
        if (SceneDb.Objects.Valid(obj)) Moved(obj.Index);
    }

    // ================= state setters (the API's bodies) =================

    private static void OnCreate(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        // EVERY per-object column, because this index may be a recycled one: a leftover LOD level makes the
        // feed refuse every part of the new object (an INVISIBLE object with no error anywhere), and a
        // leftover compose stamp or placement makes its first matrix a stale one.
        _flags[i] = ObjAnim.Animating | ObjAnim.Seated;
        _addIn[i] = null;
        _speed[i] = 1f; _speedTo[i] = 1f; _speedRate[i] = 0f;
        _visScale[i] = 1f; _visScaleTo[i] = 1f; _visScaleRate[i] = 0f;
        _vt[i] = false; _vtLerp[i] = false;
        ClearQueue(i);
        _animAt[i] = 0;
        _composeAt[i] = 0;
        _partClock[i] = -1;
        _behavior[i] = 0;
        _behaveRan[i] = false;
        _dispSaid[i] = 0;
        _hasKids[i] = false;
        _attachParent[i] = default;
        ForgetKids(i);
        LodForget(i);
        _lodNear[i] = 0f; _lodFar[i] = 0f;
        _fromPos[i] = new Vector3(float.NaN);
        _fromRot[i] = default;
        _fromScale[i] = float.NaN;
        LookAtClear(i);
        CbClear(i);
        ScanTree(i);
        Moved(i);
    }

    private static void OnRetire(DbId obj)
    {
        if (obj.IsNull) return;
        int i = obj.Index;
        // FIRST, while the record and the facade are both still readable: the death edge. Gob::~Gob raises
        // "Dying" before it tears anything down, and an object attached to this one may have to go with it.
        ObjectDying(obj);
        if (!SceneDb.Objects.Valid(obj)) return;
        ClearQueue(i);
        CbClear(i);
        LookAtClear(i);
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        for (int k = 0; k < n; k++) Unskin(first + k);
        DanglyForget(obj);
        StitchForget(obj);
        LodForget(i);
        // The engine's own order: ~CAurBehaviorAttach calls Gob::RemoveAttachment @0x43f8f0, so a dying object
        // is off its parent's carry list before the slot can be re-tenanted.
        ForgetKids(i);
        _behavior[i] = 0;
        _flags[i] = ObjAnim.None;
        _addIn[i] = null;
    }

    private static void OnTreeChanged(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        // The part range is re-tenanted, so everything keyed on a PART index has to go with it: a dynamic
        // vertex buffer sized for the old mesh, its arena range and recipe, and the bone slots. A re-equip
        // (a robe for a cloak) is exactly this, and keeping the old buffer is a heap overflow the next time
        // the dangly solve writes its vertex count.
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        for (int k = 0; k < n; k++) Unskin(first + k);
        DanglyForget(obj);
        // ...and the part boundaries this composition had healed are a composition that no longer exists:
        // the stitched geometry goes back and the whole creature is owed a new one.
        StitchForget(obj);
        StitchDue(obj);
        // The look-at holds PART indices on both ends; the engine drops its whole LookAtAnimation when the
        // tree goes, and so must this.
        LookAtClear(i);
        // The per-(model, animation) bindings are the model cache's and are keyed on the model, so a tree
        // built from the same model re-uses them and one from another model gets its own.
        ScanTree(i);
        // Every object RIDING this one holds an absolute index into the range that just changed. Re-resolve
        // each by the node name it was attached to, or a rider silently stops being placed and keeps the
        // world matrix it last had -- a body part standing beside its creature.
        Reanchor(obj);
        Moved(i);
        SceneDb.Objects.Mark(i, Dirty.Tree);
    }

    /// <inheritdoc cref="OnTreeChanged"/>
    private static void Reanchor(DbId parent)
    {
        int high = SceneDb.Objects.High;
        for (int c = 1; c < high; c++)
        {
            if (!SceneDb.Objects.LiveAt(c)) continue;
            if (SceneDb.ObjAttachTo[c].Value != parent.Value) continue;
            string? name = SceneDb.ObjAttachNode[c];
            if (name == null) continue;   // attached to the root, or to a part whose node had no name
            int now = PartByName(parent, name);
            if (now == SceneDb.ObjAttachPart[c]) continue;
            SceneDb.ObjAttachPart[c] = now;
            _reanchored++;
            SceneDb.Objects.Mark(c, Dirty.Xform);
        }
    }

    private static int _reanchored;

    private static bool Flag(DbId obj, ObjAnim f)
        => SceneDb.Objects.Valid(obj) && (_flags[obj.Index] & f) != 0;

    private static void SetFlag(DbId obj, ObjAnim f, bool on)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        if (on) _flags[obj.Index] |= f; else _flags[obj.Index] &= ~f;
    }

    private static void SetAnimGate(DbId obj, bool on) => SetFlag(obj, ObjAnim.Animating, on);
    private static void SetForce(DbId obj, bool on) => SetFlag(obj, ObjAnim.Forced, on);
    private static void SetSync(DbId obj, bool on) => SetFlag(obj, ObjAnim.Sync, on);

    private static void SetSpeed(DbId obj, float speed, float over)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        _speedTo[i] = speed;
        if (over > 0f) _speedRate[i] = MathF.Abs(speed - _speed[i]) / over;
        else { _speed[i] = speed; _speedRate[i] = 0f; }
    }

    private static void SetVisScale(DbId obj, float scale, float over)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        _visScaleTo[i] = scale;
        if (over > 0f) _visScaleRate[i] = MathF.Abs(scale - _visScale[i]) / over;
        else { _visScale[i] = scale; _visScaleRate[i] = 0f; Moved(i); }
    }

    /// <summary>The scene's animate gate is seam-scene's own column (<see cref="SceneDb.SceneAnimOn"/>, set
    /// true at creation as <c>Scene::Scene</c> leaves it); these two are the <c>CAurScene</c> slots' bodies
    /// over it, so there is one gate and not two that can disagree.</summary>
    private static void SetSceneGate(DbId scene, bool on)
    {
        if (!SceneDb.Scenes.Valid(scene)) return;
        SceneDb.SceneAnimOn[scene.Index] = on;
    }

    /// <inheritdoc cref="SetSceneGate"/>
    private static bool SceneGate(DbId scene)
        => SceneDb.Scenes.Valid(scene) && SceneDb.SceneAnimOn[scene.Index];

    /// <summary>The period line: what moved, what was refused, and why. In a still scene the compose counts read
    /// 0, which makes the change-driven claim checkable from a running session.</summary>
    public static string Period(int frames)
    {
        if (!Renderer.On) return "";
        var (cObj, cParts) = TakeComposeCounts();
        string s = $"[dbanim] {frames} frame(s): clock {_periodDt:F2}s over {_periodCalls} animate(s), "
                 + $"{_objects} object step(s), {_skipped} double-step(s) refused, "
                 + $"{_runs} run(s), {_posed} part pose(s) over {_controllers} controller(s), "
                 + $"{_crossfades} crossfade step(s), {_dropped} run(s) dropped, {_lookAts} look-at step(s), "
                 + $"{_morphs} animmesh morph(s), {_bezier} bezier sample(s), {_idle} idle record(s) skipped, "
                 + $"{_skinOnly} skin-only tail(s) (compose already current), "
                 + $"{_foldSkipped} redundant hit fold(s) skipped"
                 + $" | compose {cObj} object(s)/{cParts} part(s)"
                 + $" | events: {EventPeriod()} | behaviours: {BehaviorPeriod()} | skin: {SkinPeriod()}"
                 + $" | {LodPeriod()}, {_litWrites} light track write(s)"
                 + (_litUncarried != 0 ? $" ({_litUncarried} with no column)" : "")
                 + $" | dangly {_danglyParts} part(s)/{_danglyVerts} vert(s)"
                 + $" | {_gusts} moved-object gust(s)";
        if (_unbound != 0) s += $", {_unbound} UNBOUND animation node(s) -- no part of that name";
        if (_queueFull != 0) s += $", {_queueFull} queue overflow(s) -- oldest run dropped";
        if (_noBlock != 0) s += $", {_noBlock} REFUSED play(s) -- no run block left";
        if (_traced != 0) s += $", {_traced} trace-flagged run(s) (no DB trace channel)";
        if (_uvMorphs != 0) s += $", {_uvMorphs} uv morph(s)";
        if (_offChannel != 0) s += $", {_offChannel} light/emitter track(s) sampled by NOBODY (set DbAnim.ChannelSink)";
        if (_dynFull != 0) s += $", {_dynFull} part(s) past the dynamic-buffer ceiling (drawing rest geometry)";
        if (_lookAtNoHead != 0) s += $", {_lookAtNoHead} look-at(s) with no head part";
        if (_reanchored != 0) s += $", {_reanchored} rider(s) re-anchored after a tree change";
        int pieces = TakeComposedPieces();
        if (pieces != 0) s += $", {pieces} appended piece(s) posed with the object they are grafted onto";
        _gusts = 0;
        _objects = _skipped = _runs = _crossfades = _dropped = _lookAts = _posed = _controllers = 0;
        _unbound = _queueFull = _noBlock = _bezier = _morphs = _traced = 0;
        _uvMorphs = _offChannel = _dynFull = _lookAtNoHead = _idle = _foldSkipped = _reanchored = _skinOnly = 0;
        _danglyParts = _danglyVerts = 0;
        _periodDt = 0f; _periodCalls = 0;
        _fired = _starts = _loops = _dones = _cbFull = 0;
        _solved = _dispatched = _unresolvedPart = _noSync = _noDestroy = 0;
        _skinned = _skinNoBind = _skinNoSlot = _litWrites = _litUncarried = 0;
        return s;
    }
}
