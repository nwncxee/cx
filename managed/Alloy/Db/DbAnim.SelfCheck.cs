using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The properties the animation runtime lives by, over a SYNTHETIC model and a synthetic database --
/// no engine, no GL, so <c>run-demo.sh</c> exercises them. Each is a failure that would be chased somewhere
/// else entirely in a live session: a sampler off by a key reads as "the animations are wrong", an event
/// window that misses a wrap as "footsteps are silent", a transposed bone palette as "folded inside out".</summary>
public static unsafe partial class DbAnim
{
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbAnim.SelfCheck");
        try
        {
            Sampling(check);
            check.Expect(AnimMathCheck.SelfCheck(log), "the animation arithmetic suite (key search, slerp, morph frames) passes");
            LodChoice(check);
            Playing(check);
            Blocks(check);
            UvMorph(check);
            ThumbHit(check);
            Composition(check);
            PoseWrite(check);
            Amputation(check);
            Crossfade(check, log);
            StateReplay(check);
            Events(check, log);
            Reentrancy(check, log);
            Attach(check);
            NoOrientation(check);
            Scaled(check);
            Carried(check);
            Refilled(check);
            Still(check);
            SkinTail(check);
            Skin(check);
            DanglyMotions(check);
            LookAtEase(check);
            Lights(check);
            ControllerKinds(check);
            LodBands(check);
            check.Expect(Physics.DanglySelfCheck.Run(log), "the dangly motion suite (rig, joints, the stock solve) passes");
        }
        finally { _evN = 0; }
        log.Info(check.Ok ? "DbAnim.SelfCheck: passed"
                          : "DbAnim.SelfCheck: FAILED -- DB-mode animation would sample, blend, fire events, "
                          + "compose or skin wrongly");
        return check.Ok;
    }

    // ================= the synthetic subject =================

    /// <summary>A three-node chain -- <c>root</c> -&gt; <c>mid</c> -&gt; <c>leaf</c> -- whose animation keys
    /// <c>mid</c>'s position over two seconds and <c>leaf</c>'s orientation over the same window, and which
    /// fires three events at 0.25, 1.0 and 1.75 s. Small enough to hand-compute and shaped like a limb.</summary>
    private static ModelDef Chain()
    {
        var m = new ModelDef { Name = "cx_selfcheck_chain", AnimScale = 1f, BMin = new Vector3(-1f), BMax = new Vector3(1f) };
        m.Nodes = new[]
        {
            new NodeDef { Name = "root", Index = 0, Parent = -1, Children = new[] { 1 }, Kind = NodeKind.Dummy },
            new NodeDef { Name = "mid", Index = 1, Parent = 0, Children = new[] { 2 }, Kind = NodeKind.Dummy,
                          Position = new Vector3(0f, 0f, 1f) },
            new NodeDef { Name = "leaf", Index = 2, Parent = 1, Children = Array.Empty<int>(), Kind = NodeKind.Dummy,
                          Position = new Vector3(0f, 0f, 1f) },
        };
        for (int i = 0; i < m.Nodes.Length; i++) m.NodeByName[m.Nodes[i].Name] = i;

        var pos = new ControllerTrack
        {
            Type = Ctl.Position, Columns = 3,
            Times = new[] { 0f, 1f, 2f },
            Values = new[] { 0f, 0f, 0f, 2f, 0f, 0f, 4f, 0f, 0f },
        };
        var turn = new ControllerTrack
        {
            Type = Ctl.Orientation, Columns = 4,
            Times = new[] { 0f, 2f },
            // (x, y, z, w): identity, then a quarter turn about +Z.
            Values = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0.70710678f, 0.70710678f },
        };
        var anim = new AnimDef
        {
            Name = "wave", Length = 2f, TransTime = 0.5f, AnimRoot = "root",
            Events = new[]
            {
                new AnimEvent { Time = 0.25f, Name = "snd_one" },
                new AnimEvent { Time = 1.0f, Name = "snd_two" },
                new AnimEvent { Time = 1.75f, Name = "snd_three" },
            },
            Nodes = new[]
            {
                new AnimNode { Name = "root", Parent = -1 },
                new AnimNode { Name = "mid", Parent = 0, Controllers = new[] { pos } },
                new AnimNode { Name = "leaf", Parent = 1, Controllers = new[] { turn } },
            },
        };
        m.Anims = new[] { anim };
        return m;
    }

    /// <summary>An object with <paramref name="m"/>'s three parts, seated exactly as the seam would seat them:
    /// one contiguous pre-order range, parents ahead of children.</summary>
    private static DbId Subject(ModelDef m, DbId scene, out int first)
    {
        DbId obj = SceneDb.Objects.Alloc();
        int o = obj.Index;
        SceneDb.ObjModel[o] = m;
        SceneDb.ObjScene[o] = scene;
        SceneDb.ObjPos[o] = Vector3.Zero;
        SceneDb.ObjRot[o] = Quaternion.Identity;
        SceneDb.ObjScale[o] = 1f;
        SceneDb.ObjVisible[o] = true;
        SceneDb.ObjAttachTo[o] = default;
        SceneDb.ObjAttachPart[o] = -1;
        // A recycled object slot keeps the dead tenant's attach MODE and text offset (DbFeed.InitRecord is
        // what clears those on the live path), and a translate-attach offset left behind displaces every
        // placement this fixture then asserts.
        SceneDb.ObjAttachMode[o] = 0;
        SceneDb.TextPosOffset[o] = default;
        ObjectCreated(obj);

        // A part range is CONTIGUOUS and pre-order by PartTree.Build's contract, and the ONE allocator that
        // promises that is AllocRange: three single Alloc()s are only adjacent while the single-slot free list
        // happens to be empty, which a sort hid rather than fixed.
        if (!SceneDb.Parts.AllocRange(3, out first))
        {
            SceneDb.Objects.Free(obj);
            return default;
        }
        for (int k = 0; k < 3; k++)
        {
            int q = first + k;
            SceneDb.PartObj[q] = obj;
            SceneDb.PartModel[q] = m;
            SceneDb.PartNode[q] = k;
            SceneDb.PartParent[q] = k == 0 ? -1 : first + k - 1;
            SceneDb.PartPos[q] = m.Nodes[k].Position;
            SceneDb.PartRot[q] = Quaternion.Identity;
            SceneDb.PartScale[q] = 1f;
            SceneDb.PartEnabled[q] = true;
            SceneDb.PartMerged[q] = false;
            SceneDb.PartAlpha[q] = 1f;
            SceneDb.PartBoneBase[q] = -1;
            // The pose dispatches a controller id against this, so the fixture seats it the way PartTree does.
            SceneDb.PartKindOf[q] = PartTree.Kind(m.Nodes[k].Kind);
        }
        SceneDb.ObjPartFirst[o] = first;
        SceneDb.ObjPartCount[o] = 3;
        return obj;
    }

    private static void Drop(DbId obj)
    {
        int o = obj.Index;
        int first = SceneDb.ObjPartFirst[o], n = SceneDb.ObjPartCount[o];
        ObjectRetired(obj);
        if (n > 0) SceneDb.Parts.FreeRange(first, n);
        SceneDb.Objects.Free(obj);
    }

    /// <summary>Give <paramref name="obj"/> a FACADE, so the event path runs through the live callback list
    /// (the engine-layout array on the shell that <c>CallbackList</c> owns) rather than a second list kept for
    /// tests. The seam's vtable block is built here only when nothing has built it yet -- which is run-demo,
    /// where the seam stands down before building it -- and handed back by <see cref="DropEventShell"/>.</summary>
    private static nint EventShell(Checker check, ExtenderLog log, DbId obj, out bool freshTables)
    {
        freshTables = !Seam.Facade.Built;
        if (freshTables && Seam.Facade.Build(new Seam.SeamRegistry(), log, census: false) < 0)
        {
            check.Expect(false, "the seam's vtable block could not be built, so the event order cannot be checked");
            return 0;
        }
        nint shell = Seam.Facade.Mint(Seam.FacadeKind.Object, obj);
        check.Expect(shell != 0, "the fixture object takes a facade, which is what carries its callback list");
        // The fixture's objects live in a world scene, which is what keeps the retired shell on the reuse
        // ring instead of the held list.
        Seam.SeamShell.NoteScene(shell, world: true);
        return shell;
    }

    /// <summary>Hand back what <see cref="EventShell"/> took: the callbacks, the shell, and the vtable block
    /// when this suite was the one that built it.</summary>
    private static void DropEventShell(int o, nint shell, bool freshTables)
    {
        if (shell != 0)
        {
            Seam.CallbackList.FreeAll(shell);
            Seam.Facade.Retire(shell);
            SceneDb.ObjFacade[o] = 0;
        }
        if (freshTables) Seam.Facade.DropTables();
    }

    /// <summary>Register one test handler on <paramref name="shell"/>'s live list, as
    /// <c>Gob::RegisterCallBack</c> does.</summary>
    private static nint Listen(nint shell, nint fn, byte* name) => Seam.CallbackList.Add(shell, fn, (nint)name, 0);

    /// <summary>The one test handler, as a function pointer the engine's list can hold.</summary>
    private static nint TestEvent => (nint)(delegate* unmanaged<nint, byte*, nint, void>)&OnTestEvent;

    // ================= 1: the sampler =================

    /// <summary>The pose at known times: an exact key, a midpoint, and past the end -- the three branches
    /// <c>GetVectorValue</c> has. The quarter turn is checked by where it sends +X, because a quaternion read
    /// with its components rotated passes every length test and fails this one.</summary>
    private static void Sampling(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out int first);
        AnimDef anim = m.Anims[0];
        check.Expect(!obj.IsNull, "the fixture takes one contiguous part range");

        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn, 0f);
        check.Expect(_runN[obj.Index] == 1, "PlayAnimation seats one run");

        Sample(obj, anim, 0f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(0f, 0f, 0f)), "key 0 is taken exactly");
        Sample(obj, anim, 0.5f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(1f, 0f, 0f)), "a midpoint is the linear blend");
        Sample(obj, anim, 2f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(4f, 0f, 0f)), "the last key is taken exactly");
        Sample(obj, anim, 7f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(4f, 0f, 0f)), "past the end HOLDS the last key");

        Sample(obj, anim, 2f);
        Vector3 turned = Vector3.Transform(Vector3.UnitX, SceneDb.PartRot[first + 2]);
        check.Expect(NearV(turned, Vector3.UnitY), "a quarter turn about +Z sends +X to +Y");

        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>Pose the subject at one time at full weight, without the queue.</summary>
    private static void Sample(DbId obj, AnimDef anim, float t)
    {
        int s = Slot(obj.Index, 0);
        _rTime[s] = t;
        _rPosScale[s] = 1f;
        Pose(obj, anim, s, 1f);
    }

    // ================= 2: crossfade weights =================

    /// <summary>The weight ladder: a run that starts faded in reaches full weight in its fade time and no
    /// sooner, and a run with NO fade time is at full weight on its first frame. Both matter because the blend
    /// IS the crossfade -- there is no separate pose buffer, so a weight that jumps is a pose that snaps.</summary>
    private static void Crossfade(Checker check, ExtenderLog log)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out _);

        PlayAnimation(obj, "wave", 1f, 0, 0f);
        int s = Slot(obj.Index, 0);
        check.Expect(_rBlend[s] == 0f && _rFade[s] == 0.5f,
                     "a faded-in run starts at weight 0 with the animation's own cross-fade time");
        Advance(obj, 0.25f, 1);
        check.Expect(Math.Abs(_rBlend[s] - 0.5f) < 1e-5f, "half the fade time is half the weight");
        Advance(obj, 0.25f, 1);
        check.Expect(_rBlend[s] == 1f, "the fade time reaches full weight, and clamps there");
        Advance(obj, 0.25f, 1);
        check.Expect(_rBlend[s] == 1f, "...and stays");
        Drop(obj);

        obj = Subject(m, scene, out _);
        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn, 0f);
        check.Expect(_rBlend[Slot(obj.Index, 0)] == 1f, "a NoFadeIn run is at full weight on its first frame");

        // A plain PlayAnimation EVICTS the runs below the new one (the engine's own loop, which announces each
        // with a done<name> first). The crossfade does not come from keeping them: it comes from the new run's
        // weight blending its keys over the pose the COLUMNS still hold from last frame, which is the whole
        // mechanism -- there is no separate pose buffer.
        _evN = 0;
        byte* any = stackalloc byte[2];
        any[0] = (byte)'*'; any[1] = 0;
        nint shell = EventShell(check, log, obj, out bool freshTables);
        nint cb = Listen(shell, TestEvent, any);
        check.Expect(new CallBack(cb).SeamName.Length == 32, "a callback block's name holds the engine's 32 bytes");
        PlayAnimation(obj, "wave", 1f, RunFlags.NoReuse, 0f);
        check.Expect(_runN[obj.Index] == 1, "a new run evicts the runs below it, as the engine's does");
        check.Expect(_evN >= 1 && _ev[0] == "donewave", "...announcing each evicted run first");
        DropEventShell(obj.Index, shell, freshTables);

        // An add-in run does NOT evict, which is what layers a gesture over a walk.
        PlayAnimation(obj, "wave", 1f, RunFlags.NoReuse | RunFlags.AddIn, 0f);
        check.Expect(_runN[obj.Index] == 2, "an add-in run layers over the queue instead of evicting it");
        Drop(obj);

        // The crossfade itself, through the columns: pose A at full weight, then blend the SAME keys in at
        // half weight from a different column value, and the result must be halfway.
        obj = Subject(m, scene, out int first);
        AnimDef anim = m.Anims[0];
        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn, 0f);
        Sample(obj, anim, 2f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(4f, 0f, 0f)), "the pose lands at the last key");
        int ps = Slot(obj.Index, 0);
        _rTime[ps] = 0f;
        Pose(obj, anim, ps, 0.5f);
        check.Expect(NearV(SceneDb.PartPos[first + 1], new Vector3(2f, 0f, 0f)),
                     "half weight is halfway between the column's previous pose and the new key");
        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>A GUI panel's state animation, played the way <c>CGuiObject::PlayStateAnimation</c> @0x586a20 does
    /// on every update: end the run, replay it at 1000x the state, step 1 ms, freeze. Oracle: hand-computed, 500
    /// replays at weight 0.002 (1 ms of the 0.5 s fade) leave mid's x at 2(1 - 0.998^500) = 1.2650.</summary>
    private static void StateReplay(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        SceneDb.SceneIsWorld[scene.Index] = false;
        DbId obj = Subject(m, scene, out int first);
        for (int k = 0; k < 500; k++)
        {
            PlayAnimation(obj, "wave", 1f, RunFlags.EndOnly, 0f);
            PlayAnimation(obj, "wave", 1000f, 0, 0f);
            Advance(obj, 0.001f, _runN[obj.Index]);
            PlayAnimation(obj, "wave", 0f, 0, 0f);
        }
        check.Expect(MathF.Abs(SceneDb.PartPos[first + 1].X - 1.2650f) < 2e-3f,
                     $"a replayed GUI state settles by its linear weights, uneased (mid x {SceneDb.PartPos[first + 1].X:F4}, want 1.2650)");
        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    // ================= 3: events =================

    private const int MaxEv = 32;
    private static readonly string[] _ev = new string[MaxEv];
    private static int _evN;

    [UnmanagedCallersOnly]
    private static void OnTestEvent(nint self, byte* name, nint data) => Capture(name);

    /// <summary>What the two test handlers record: the event name, as a managed string.</summary>
    private static void Capture(byte* name)
    {
        if (_evN >= MaxEv) return;
        int n = 0;
        while (name[n] != 0 && n < 64) n++;
        var chars = new char[n];
        for (int i = 0; i < n; i++) chars[i] = (char)name[i];
        _ev[_evN++] = new string(chars);
    }

    /// <summary>The window, the order and the wrap. The window is half-open at the low end, so an event ON the
    /// previous time does NOT fire again -- which is what keeps a looping footstep from double-firing every
    /// cycle -- and a frame that wraps fires the tail of the cycle before its head, so the sounds stay in time
    /// order across the seam.</summary>
    private static void Events(Checker check, ExtenderLog log)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out _);
        AnimDef anim = m.Anims[0];
        byte* any = stackalloc byte[2];
        any[0] = (byte)'*'; any[1] = 0;
        nint shell = EventShell(check, log, obj, out bool freshTables);
        nint cb = Listen(shell, TestEvent, any);
        check.Expect(cb != 0, "a callback registers");

        _evN = 0;
        FireEvents(obj, anim, 0f, 0.5f, false);
        check.Expect(_evN == 1 && _ev[0] == "snd_one", "only the event inside the window fires");

        _evN = 0;
        FireEvents(obj, anim, 0.25f, 1.0f, false);
        check.Expect(_evN == 1 && _ev[0] == "snd_two",
                     "the window is half-open at the low end: an event ON the previous time does not re-fire");

        _evN = 0;
        FireEvents(obj, anim, 0f, 2f, false);
        check.Expect(_evN == 3 && _ev[0] == "snd_one" && _ev[1] == "snd_two" && _ev[2] == "snd_three",
                     "a whole cycle fires every event in order");

        // The wrap: 1.5 -> 0.5 across the loop point fires snd_three (the tail) and then snd_one (the head).
        _evN = 0;
        FireEvents(obj, anim, 1.5f, 0.5f, false);
        check.Expect(_evN == 2 && _ev[0] == "snd_three" && _ev[1] == "snd_one",
                     "a wrapped window fires the cycle's TAIL before its head, so events stay in time order");

        _evN = 0;
        FireEvents(obj, anim, 2f, 0f, true);
        check.Expect(_evN == 3 && _ev[0] == "snd_three" && _ev[1] == "snd_two" && _ev[2] == "snd_one",
                     "a reverse run fires them in reverse order");

        // The named filter, and that a non-matching name hears nothing.
        _evN = 0;
        byte* named = stackalloc byte[8];
        named[0] = (byte)'s'; named[1] = (byte)'n'; named[2] = (byte)'d'; named[3] = (byte)'_';
        named[4] = (byte)'t'; named[5] = (byte)'w'; named[6] = (byte)'o'; named[7] = 0;
        Seam.CallbackList.Remove(shell, cb);
        nint only = Listen(shell, TestEvent, named);
        FireEvents(obj, anim, 0f, 2f, false);
        check.Expect(_evN == 1 && _ev[0] == "snd_two", "a named callback hears only its own event");
        Seam.CallbackList.Remove(shell, only);

        // The three lifecycle names, as the engine's sprintf builds them.
        _evN = 0;
        Listen(shell, TestEvent, any);
        Lifecycle(obj, StartPrefix, anim);
        Lifecycle(obj, LoopPrefix, anim);
        Lifecycle(obj, DonePrefix, anim);
        check.Expect(_evN == 3 && _ev[0] == "startwave" && _ev[1] == "loopwave" && _ev[2] == "donewave",
                     "the lifecycle events are start/loop/done + the animation's name");

        DropEventShell(obj.Index, shell, freshTables);
        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    // ================= 4: the attach chain =================

    /// <summary>The composition, against a matrix worked out by hand. A part's world is its own local times its
    /// parent's, and an attached object multiplies in each ancestor's visual scale -- the one term
    /// <c>Part::GetWorld</c> does not carry. Both are the kind of thing that looks right in a screenshot until
    /// a weapon is held at the wrong scale.</summary>
    private static void Attach(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out int first);
        int o = obj.Index;

        SceneDb.ObjPos[o] = new Vector3(10f, 0f, 0f);
        SceneDb.ObjRot[o] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        SceneDb.ObjScale[o] = 2f;
        Moved(o);
        ComposeObject(obj);

        // leaf sits at (0,0,1) under mid at (0,0,1) under the root: (0,0,2) in object space, scaled by 2 and
        // turned a quarter about +Z (which leaves +Z alone), then offset to (10,0,0).
        Matrix4x4 leaf = SceneDb.PartWorld[first + 2];
        check.Expect(NearV(new Vector3(leaf.M41, leaf.M42, leaf.M43), new Vector3(10f, 0f, 4f)),
                     "a leaf's world is its own local times its parents' times the object's placement");
        check.Expect(Math.Abs(ScaleOf(leaf) - 2f) < 1e-4f, "...and carries the object's scale");

        // Hand-computed, the long way: the same product written out.
        Matrix4x4 want = Build(1f, Quaternion.Identity, new Vector3(0f, 0f, 1f))
                       * Build(1f, Quaternion.Identity, new Vector3(0f, 0f, 1f))
                       * Build(1f, Quaternion.Identity, Vector3.Zero)
                       * Build(2f, SceneDb.ObjRot[o], SceneDb.ObjPos[o]);
        check.Expect(NearV(new Vector3(want.M41, want.M42, want.M43),
                          new Vector3(leaf.M41, leaf.M42, leaf.M43)),
                     "...and equals the hand-written S_leaf T_leaf ... S_obj T_obj product");

        // An attached child takes its parent's visual scale into its own composed scale.
        DbId child = Subject(m, scene, out int cFirst);
        SceneDb.ObjAttachTo[child.Index] = obj;
        SceneDb.ObjAttachPart[child.Index] = first + 2;
        SetVisualScale(obj, 3f, 0f);
        Moved(child.Index);
        ComposeObject(child);
        check.Expect(Math.Abs(ScaleOf(SceneDb.ObjWorld[child.Index]) - 3f) < 1e-4f,
                     "an attached object multiplies in each ancestor's visual scale");
        check.Expect(Math.Abs(ScaleOf(SceneDb.PartWorld[cFirst]) - 3f) < 1e-4f,
                     "...and its parts inherit it");

        // A grafted body piece (Gob::Append) is part of the creature's one tree in the engine: it carries its
        // anchor part's whole world scale (here the object's 2), not the ancestors' visual scale alone.
        DbId graft = Subject(m, scene, out int gFirst);
        SceneDb.ObjAttachTo[graft.Index] = obj;
        SceneDb.ObjAttachPart[graft.Index] = first + 2;
        SceneDb.ObjSeamFlags[graft.Index] |= (uint)SeamFlag.Composed;
        Moved(graft.Index);
        ComposeObject(graft);
        check.Expect(Math.Abs(ScaleOf(SceneDb.ObjWorld[graft.Index]) - 2f) < 1e-4f && GraftRoot(graft.Index) == o,
                     "a grafted piece takes its anchor part's world scale and belongs to its creature");
        SceneDb.ObjSeamFlags[graft.Index] &= ~(uint)SeamFlag.Composed;
        Drop(graft);

        // The sphere the visible-set index files on must exist and must grow with the scale.
        check.Expect(SceneDb.ObjSphere[o].W > 0f, "the transform step writes the object's cull sphere");

        Drop(child);
        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>An effect on a creature: a no-orientation rider takes the part's position and keeps its own
    /// orientation as the parent turns, and a world rotation solved back to a part's local composes to itself.
    /// Oracle: the leaf's place worked out by hand and literal rotations.</summary>
    private static void NoOrientation(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out int first);
        DbId fx = Subject(m, scene, out _);
        int o = obj.Index, f = fx.Index;
        Quaternion held = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f);
        SceneDb.ObjPos[o] = new Vector3(10f, 0f, 0f);
        SceneDb.ObjRot[o] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        SceneDb.ObjAttachTo[f] = obj;
        SceneDb.ObjAttachPart[f] = first + 2;
        SceneDb.ObjAttachMode[f] = SceneDb.AttachNoOrientation;
        SceneDb.ObjRot[f] = held;
        Moved(o);
        Moved(f);
        ComposeObject(fx);
        check.Expect(NearV(Translation(SceneDb.ObjWorld[f]), new Vector3(10f, 0f, 2f)),
                     "a no-orientation rider sits on the part it rides");
        check.Expect(MathF.Abs(Quaternion.Dot(RotOf(SceneDb.ObjWorld[f]), held)) > 0.9999f,
                     "...and keeps its own orientation, not the part's");

        SceneDb.ObjRot[o] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        Moved(o);
        ComposeObject(fx);
        check.Expect(NearV(Translation(SceneDb.ObjWorld[f]), new Vector3(10f, 0f, -2f)),
                     "...follows the part when the creature turns over");
        check.Expect(MathF.Abs(Quaternion.Dot(RotOf(SceneDb.ObjWorld[f]), held)) > 0.9999f,
                     "...and still keeps its own orientation");

        Matrix4x4 parent = Build(2f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f), new Vector3(1f, 2f, 3f));
        Quaternion want = Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1f, 1f, 0f)), 1.1f);
        Quaternion got = RotOf(Build(1f, LocalRot(want, parent), Vector3.Zero) * parent);
        check.Expect(MathF.Abs(Quaternion.Dot(got, want)) > 0.9999f,
                     "a world rotation solved to a part's local composes back to itself under a turned parent");

        Drop(fx);
        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    // ================= 4b: a parent that moves carries its children =================

    /// <summary>A window-resize repro, as the engine's own sequence: a label attached to a GUI gob, then
    /// <c>CGuiMan::SetGuiSize</c> @0x57efc0 re-centring the gob under it. Vanilla's
    /// <c>CAurBehaviorAttach::Control</c> @0x4a2810 re-derives the child's placement from the parent's part
    /// every animate; the compose visits only what changed, so one pass with only the PARENT marked tests it.</summary>
    private static void Carried(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId panel = Subject(m, scene, out int pFirst);
        DbId label = Subject(m, scene, out _);
        int l = label.Index, p = panel.Index;
        SceneDb.ObjKindOf[l] = ObjKind.Text;
        SceneDb.ObjAttachTo[l] = panel;
        SceneDb.ObjAttachPart[l] = pFirst;
        SceneDb.ObjAttachMode[l] = SceneDb.AttachTranslate;
        SceneDb.TextPosOffset[l] = new Vector3(0.25f, 0f, 0f);

        Moved(l);
        ComposePending();
        // The frame boundary seam-feed makes: the list is consumed, so from here only what the test marks is
        // in it -- which is the parent, and never the label.
        SceneDb.Objects.TakeDirty();
        _dirtyUpTo = 0;
        Vector3 at = Translation(SceneDb.ObjWorld[l]);
        check.Expect(NearV(at, Translation(SceneDb.PartWorld[pFirst]) + SceneDb.TextPosOffset[l]),
                     "an attached label composes onto its parent's part, offset and all");

        // The resize: the panel's placement changes and NOTHING marks the label.
        var moveTo = new Vector3(3f, -2f, 1f);
        _poseGen++;
        SceneDb.ObjPos[p] = moveTo;
        Moved(p);
        ComposePending();
        check.Expect(NearV(Translation(SceneDb.ObjWorld[l]), at + moveTo),
                     "...and FOLLOWS the parent when the parent alone moves");

        // The link is the parent's, so a retired label leaves it before its slot can be re-tenanted.
        Drop(label);
        check.Expect(_kidHead[p] == 0, "...and a retired child is off its parent's carry list");

        Drop(panel);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>A panel rebuilt between two composes: the list is taken at the present and refills PAST the old
    /// cursor before the next compose. Every object on it composes -- a cursor kept on the list's length started
    /// mid-list, and the labels whose slot was recycled kept the previous tenant's world matrix.</summary>
    private static void Refilled(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId a = Subject(m, scene, out _);
        DbId b = Subject(m, scene, out _);
        ComposePending();
        SceneDb.Objects.TakeDirty();
        _poseGen++;
        SceneDb.ObjPos[b.Index] = new Vector3(7f, 8f, 9f);
        Moved(b.Index);
        Moved(a.Index);
        ComposePending();
        check.Expect(NearV(Translation(SceneDb.ObjWorld[b.Index]), new Vector3(7f, 8f, 9f)),
                     "a list refilled past the old cursor composes its FIRST entry too");
        // ...and an object moved after the frame's last scene animate composes at the boundary's Settle,
        // before the publish takes the list.
        _poseGen++;
        SceneDb.ObjPos[a.Index] = new Vector3(-4f, 2f, 5f);
        Moved(a.Index);
        Settle();
        SceneDb.Objects.TakeDirty();
        check.Expect(NearV(Translation(SceneDb.ObjWorld[a.Index]), new Vector3(-4f, 2f, 5f)),
                     "an object marked after the last animate is composed before the dirty list is taken");
        Drop(b);
        Drop(a);
        SceneDb.Scenes.Free(scene);
    }

    // ================= 5: change-driven =================

    /// <summary>A still frame composes NOTHING -- the change-driven claim, and the one that is invisible when
    /// it breaks: a compose that runs over every object every frame still draws the right picture.</summary>
    private static void Still(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out int first);
        SceneDb.Objects.TakeDirty();
        _dirtyUpTo = 0;
        ComposePending();
        TakeComposeCounts();

        ComposePending();
        var (objects, parts) = TakeComposeCounts();
        check.Expect(objects == 0 && parts == 0, "a still frame composes no object and no part");

        SceneDb.ObjPos[obj.Index] = new Vector3(1f, 2f, 3f);
        Moved(obj.Index);
        ComposePending();
        (objects, parts) = TakeComposeCounts();
        check.Expect(objects == 1 && parts == 3, "one moved object composes exactly itself and its parts");

        // ...and a getter straight after a setter answers the new value without waiting for a pass.
        SceneDb.ObjPos[obj.Index] = new Vector3(4f, 5f, 6f);
        Moved(obj.Index);
        Matrix4x4 w = ObjectWorld(obj);
        check.Expect(NearV(new Vector3(w.M41, w.M42, w.M43), new Vector3(4f, 5f, 6f)),
                     "a by-value getter after a setter composes on demand");

        // A STATIC object restages a part when its world moved, never for having been recomposed: an animated
        // tile replaying a still pose rewrote its whole range every frame.
        SceneDb.ObjFlags[obj.Index] |= ObjFlag.Static;
        SceneDb.Parts.TakeDirty(); _partDirtyUpTo = 0;
        SceneDb.ObjPos[obj.Index] = new Vector3(7f, 8f, 9f);
        Moved(obj.Index);
        ComposeObject(obj);
        check.Expect(SceneDb.Parts.DirtyCount == 3, "a moved static object marks each part whose world moved");
        SceneDb.Parts.TakeDirty(); _partDirtyUpTo = 0;
        Moved(obj.Index);
        ComposeObject(obj);
        check.Expect(SceneDb.Parts.DirtyCount == 0, "...and a recompose that moved nothing marks none");
        SceneDb.ObjFlags[obj.Index] &= ~ObjFlag.Static;
        SceneDb.Parts.TakeDirty(); _partDirtyUpTo = 0;

        // CX-188: the PART-only signal, which is the one change the object's own staleness test cannot see.
        // Dropping Settle's _poseGen bump is only safe because of it, and the hazard is both ways round: a
        // mark that stops recomposing is a stale pose, and a mark that recomposes forever is the 977-object
        // walk coming back through the part list instead of the object list.
        TakeComposeCounts();
        int mid = first + 1;
        SceneDb.PartPos[mid] = new Vector3(0f, 0f, 5f);
        SceneDb.Parts.Mark(mid, Dirty.Anim);
        ComposePending();
        (objects, parts) = TakeComposeCounts();
        check.Expect(objects == 1, "a part marked after its object's compose recomposes that object");
        check.Expect(NearV(Translation(SceneDb.PartWorld[first + 2]),
                           Translation(SceneDb.ObjWorld[obj.Index]) + new Vector3(0f, 0f, 6f)),
                     "...and the part below it rides the new pose");
        // The boundary re-walks the WHOLE part list, and the same mark must not buy a second compose.
        _partDirtyUpTo = 0;
        ComposePending();
        (objects, parts) = TakeComposeCounts();
        check.Expect(objects == 0 && parts == 0, "...the same mark, re-walked at the boundary, composes nothing");
        // ...but a SECOND mark on that entry -- which appends nothing to the list and adds no new bit, so
        // neither the list length nor the dirty bits move -- must still be caught by the re-walk.
        SceneDb.PartPos[mid] = new Vector3(0f, 0f, 7f);
        SceneDb.Parts.Mark(mid, Dirty.Anim);
        _partDirtyUpTo = 0;
        ComposePending();
        (objects, parts) = TakeComposeCounts();
        check.Expect(objects == 1, "...and a RE-mark of an entry behind the cursor recomposes it");
        check.Expect(NearV(Translation(SceneDb.PartWorld[first + 2]),
                           Translation(SceneDb.ObjWorld[obj.Index]) + new Vector3(0f, 0f, 8f)),
                     "...with the pose that second mark carried");
        SceneDb.Parts.TakeDirty(); _partDirtyUpTo = 0;

        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>A creature's step composes its part tree ONCE. The pose layer's compose runs inside
    /// <see cref="StepObject"/> and the tail still owes the skin palette; the two are keyed separately, and
    /// this fails if either half is lost -- a tail that no longer skins does not count, and a tail that
    /// recomposes counts a second object.</summary>
    private static void SkinTail(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        SetSceneGate(scene, true);
        DbId obj = Subject(m, scene, out int _);
        int i = obj.Index;
        SceneDb.ObjFlags[i] |= ObjFlag.Creature;
        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn, 0f);

        var before = PoseBefore; var after = PoseAfter;
        // The layer's shape in the measured frame: it paces the runs (so the object is marked moved) and then
        // corrects nothing, which is every frame in which no procedural correction is due.
        PoseBefore = &LayerPaced;
        PoseAfter = &LayerQuiet;
        try
        {
            SceneDb.Objects.TakeDirty(); _dirtyUpTo = 0;
            ComposePending();
            TakeComposeCounts();
            int skinOnly = _skinOnly;
            _animAt[i] = 0;
            StepObject(obj, 1f / 60f, 0);
            var (objects, _) = TakeComposeCounts();
            check.Expect(objects == 1, "a creature's step composes its part tree exactly once");
            check.Expect(_skinOnly == skinOnly + 1,
                         "...and its tail still re-skins, from the matrices that compose wrote");
            check.Expect((_flags[i] & ObjAnim.SkinDue) == 0, "...and the skin debt is consumed, not carried");
        }
        finally { PoseBefore = before; PoseAfter = after; }

        Drop(obj);
        SetSceneGate(scene, false);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>The pose layer's <c>PoseBefore</c> for <see cref="SkinTail"/>: it wrote a pose column.</summary>
    private static bool LayerPaced(DbId obj, float dt) => true;

    /// <summary>The pose layer's <c>PoseAfter</c> for <see cref="SkinTail"/>: nothing to correct.</summary>
    private static bool LayerQuiet(DbId obj, float dt) => false;

    // ================= 7: re-entrancy =================

    /// <summary>What an event HANDLER is allowed to do, and the three ways it can corrupt the queue it was
    /// called from: destroy its own object, play another animation (which inserts at or below the running
    /// index and SHIFTS the queue), and deregister a callback mid-walk. The engine survives by re-reading its
    /// list and testing a SafePointer; CX re-derives a positional slot instead, and only a test says it does.</summary>
    private static void Reentrancy(Checker check, ExtenderLog log)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        AnimDef anim = m.Anims[0];

        // (a) a handler that plays another animation while the advance is iterating.
        DbId obj = Subject(m, scene, out _);
        _reentryObj = obj;
        _reentry = Reentry.Play; _reentryFired = false;
        byte* any = stackalloc byte[2];
        any[0] = (byte)'*'; any[1] = 0;
        nint reentry = (nint)(delegate* unmanaged<nint, byte*, nint, void>)&OnReentry;
        nint shell = EventShell(check, log, obj, out bool freshTables);
        Listen(shell, reentry, any);
        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn | RunFlags.Loop, 0f);
        for (int i = 0; i < 30; i++) Advance(obj, 0.2f, _runN[obj.Index]);
        check.Expect(SceneDb.Objects.Valid(obj) && _runN[obj.Index] >= 1,
                     "a handler may play an animation from inside the advance");
        _reentry = Reentry.None;
        DropEventShell(obj.Index, shell, freshTables);
        Drop(obj);

        // (b) a handler that clears the queue -- which gives the run block back under the iteration.
        obj = Subject(m, scene, out _);
        _reentryObj = obj;
        _reentry = Reentry.Clear; _reentryFired = false;
        shell = EventShell(check, log, obj, out freshTables);
        Listen(shell, reentry, any);
        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn | RunFlags.Loop, 0f);
        for (int i = 0; i < 10; i++) Advance(obj, 0.5f, _runN[obj.Index]);
        check.Expect(_runN[obj.Index] == 0 && _block[obj.Index] < 0,
                     "a handler may clear the queue from inside the advance");
        _reentry = Reentry.None;
        DropEventShell(obj.Index, shell, freshTables);
        Drop(obj);

        // (c) a handler that destroys its own object: nothing after it may touch the record, and the engine's
        // own AnimateEvents does NOT make that check -- this is the one deliberate improvement.
        obj = Subject(m, scene, out _);
        _reentryObj = obj;
        _reentry = Reentry.Destroy; _reentryFired = false;
        int destroyed = obj.Index;
        shell = EventShell(check, log, obj, out freshTables);
        Listen(shell, reentry, any);
        _evN = 0;
        bool survived = FireEvents(obj, anim, 0f, 2f, false);
        check.Expect(!survived, "an event that destroys the object stops the walk");
        int keyframes = 0;
        for (int k = 0; k < _evN; k++) if (_ev[k].StartsWith("snd_", StringComparison.Ordinal)) keyframes++;
        check.Expect(keyframes == 1, "...and the remaining events do NOT fire on a freed record");
        // ...while the record's own death edge DOES go out, which is Gob::~Gob's second statement and the
        // only thing allowed to take an attached child with it.
        bool dying = false;
        for (int k = 0; k < _evN; k++) if (_ev[k] == "Dying") dying = true;
        check.Expect(dying, "retiring an object raises \"Dying\" at its own callbacks");
        _reentry = Reentry.None;
        DropEventShell(destroyed, shell, freshTables);

        // (d) the callback-list hazard, kept as the engine has it: deregistering a LOWER index mid-walk
        // shifts the array and the entry that moves into the current index fires again.
        DbId o2 = Subject(m, scene, out _);
        _evN = 0;
        nint shell2 = EventShell(check, log, o2, out bool freshTables2);
        nint low = Listen(shell2, TestEvent, any);
        _dropTarget = low;
        _dropShell = shell2;
        Listen(shell2, (nint)(delegate* unmanaged<nint, byte*, nint, void>)&OnDropLower, any);
        EventFire(o2, Names(anim)!.Life(StartPrefix), 0);
        // The walk runs newest-first: the dropper fires, removes the entry BELOW it, and the shift moves the
        // dropper itself into the index the walk steps to next -- so it fires twice and the entry it removed
        // never fires at all. That is the engine's own arithmetic, kept rather than papered over.
        check.Expect(_evN == 2 && _ev[0] == _ev[1],
                     "deregistering a lower callback mid-walk re-fires the one that moves down "
                   + "(the engine's own hazard, kept)");
        _dropTarget = 0;
        DropEventShell(o2.Index, shell2, freshTables2);
        Drop(o2);
        SceneDb.Scenes.Free(scene);
    }

    private enum Reentry { None, Play, Clear, Destroy }
    private static Reentry _reentry;
    private static bool _reentryFired;
    private static DbId _reentryObj;
    private static nint _dropTarget, _dropShell;

    [UnmanagedCallersOnly]
    private static void OnReentry(nint self, byte* name, nint data)
    {
        Capture(name);
        // Once per armed case: the handler's own action announces another event to the same handler, and a
        // test that recursed for ever would only be proving that the production guard below it exists.
        if (_reentryFired) return;
        _reentryFired = true;
        switch (_reentry)
        {
            case Reentry.Play: PlayAnimation(_reentryObj, "wave", 1f, RunFlags.NoReuse, 0f); break;
            case Reentry.Clear: PlayAnimation(_reentryObj, null, 1f, 0, 0f); break;
            case Reentry.Destroy:
                ObjectRetired(_reentryObj);
                SceneDb.Objects.Free(_reentryObj);
                break;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnDropLower(nint self, byte* name, nint data)
    {
        Capture(name);
        if (_dropTarget == 0) return;
        nint t = _dropTarget;
        _dropTarget = 0;
        Seam.CallbackList.Remove(_dropShell, t);
    }

    // ================= 8: scale =================

    /// <summary>A scaled object's rotation must come back as a UNIT quaternion. The composed matrix's basis is
    /// <c>s</c> times a rotation's, so decomposing it as it stands yields norm <c>sqrt(s)</c> -- which the
    /// attach solver then writes into <c>ObjRot</c>, scaling the attached weapon a second time, and which
    /// <c>GetOrientation</c> hands the game as a "rotation".</summary>
    private static void Scaled(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out int first);
        int o = obj.Index;
        SceneDb.ObjScale[o] = 4f;
        SceneDb.ObjRot[o] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        Moved(o);
        ComposeObject(obj);
        PartGetWorld(first + 2, out Vector3 pos, out Quaternion rot);
        check.Expect(MathF.Abs(rot.Length() - 1f) < 1e-4f, "PartGetWorld answers a UNIT quaternion under scale");
        check.Expect(MathF.Abs(Vector3.Transform(Vector3.UnitX, rot).Length() - 1f) < 1e-4f,
                     "...and it rotates without scaling");
        check.Expect(NearV(pos, new Vector3(SceneDb.PartWorld[first + 2].M41, SceneDb.PartWorld[first + 2].M42,
                                            SceneDb.PartWorld[first + 2].M43)),
                     "...and the position is the matrix's own");
        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    // ================= 9: animated light terms =================

    /// <summary>A light controller writes the DB column, not the immutable node, and the column starts from the
    /// node's REST key so the first blend does not come up out of zero.</summary>
    private static void Lights(Checker check)
    {
        ModelDef m = Chain();
        m.Nodes[2].Kind = NodeKind.Light;
        m.Nodes[2].Light = new LightDef();
        var colour = new ControllerTrack
        {
            Type = Ctl.LightColor, Columns = 3,
            Times = new[] { 0f, 2f },
            Values = new[] { 1f, 0f, 0f, 0f, 0f, 1f },
        };
        var radius = new ControllerTrack
        {
            Type = Ctl.LightRadius, Columns = 1, Times = new[] { 0f, 2f }, Values = new[] { 2f, 6f },
        };
        m.Nodes[2].Controllers = new[] { colour, radius };
        m.Anims[0].Nodes[2].Controllers = new[] { colour, radius };

        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out int first);
        int lp = first + 2;
        check.Expect(SceneDb.PartLitAnim[lp] == 0, "an unanimated light part reads its rest keys");
        check.Expect(NearV(LitColour3(lp, Vector3.One), Vector3.One), "...which is what the feed is handed");

        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn, 0f);
        Sample(obj, m.Anims[0], 2f);
        check.Expect((SceneDb.PartLitAnim[lp] & (LitColour | LitRadius)) == (LitColour | LitRadius),
                     "a light controller claims its column");
        check.Expect(NearV(LitColour3(lp, Vector3.One), new Vector3(0f, 0f, 1f)),
                     "the animated colour is the sampled key, not the rest value");
        check.Expect(MathF.Abs(LitRadius1(lp, 1f) - 6f) < 1e-4f, "...and so is the radius");
        check.Expect(MathF.Abs(LitMul1(lp, 0.5f) - 0.5f) < 1e-4f,
                     "a term with no controller still reads the caller's rest value");
        check.Expect(LitMoving(lp), "a two-key light track is motion: the bake keeps the light dynamic");
        check.Expect(Settled(obj), "a no-fade-in run is settled at once");
        SceneDb.PartLitAnim[lp] = LitRadius;
        // A SECOND model, not this node's controller array rewritten: LitMoving's node half is memoised on the
        // NodeDef (NodeDef.LitKeyed), which is sound only because a parsed node is immutable -- so a fixture
        // that mutated one would test something production cannot do, and would hide the memo's one failure
        // mode instead of catching it. Two nodes, two answers, which is also the test that it is per node.
        ModelDef settled = Chain();
        settled.Nodes[2].Kind = NodeKind.Light;
        settled.Nodes[2].Light = new LightDef();
        settled.Nodes[2].Controllers = new[]
            { new ControllerTrack { Type = Ctl.LightRadius, Columns = 1, Times = new[] { 0f }, Values = new[] { 2f } } };
        SceneDb.PartModel[lp] = settled;
        check.Expect(!LitMoving(lp), "a single-key track settles: the light is bakeable");
        SceneDb.PartModel[lp] = m;
        check.Expect(LitMoving(lp), "...and the multi-key node still reads as motion (the memo is per node)");

        // Half weight blends over the column, from the rest key on the first write.
        SceneDb.PartLitAnim[lp] = 0;
        int ps = Slot(obj.Index, 0);
        _rTime[ps] = 2f;
        Pose(obj, m.Anims[0], ps, 0.5f);
        check.Expect(NearV(LitColour3(lp, Vector3.One), new Vector3(0.5f, 0f, 0.5f)),
                     "the first blend starts from the node's rest key, not from zero");
        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    // ================= 9b: one id, three kinds =================

    /// <summary>The colliding controller ids land in the column their node's KIND names. One track set is fed
    /// to a mesh, a light and an emitter part: file id 100 is a mesh's selfIllumColor and a light's
    /// verticalDisplacement, 128 a mesh's alpha and an emitter's fps, 88 a light's radius and an emitter's
    /// birth rate. Dispatching on the id alone puts an emitter's rate in the light columns.</summary>
    private static void ControllerKinds(Checker check)
    {
        var illum = new ControllerTrack
        {
            Type = Ctl.SelfIllumColor, Columns = 3,
            Times = new[] { 0f, 2f }, Values = new[] { 0f, 0f, 0f, 0.25f, 0.5f, 0.75f },
        };
        var alpha = new ControllerTrack
        {
            Type = Ctl.Alpha, Columns = 1, Times = new[] { 0f, 2f }, Values = new[] { 1f, 0.25f },
        };
        var rate = new ControllerTrack
        {
            Type = Ctl.EmBirthRate, Columns = 1, Times = new[] { 0f, 2f }, Values = new[] { 0f, 9f },
        };
        var tracks = new[] { illum, alpha, rate };

        // ---- a mesh: 100 and 128 are its own two, and nothing of the light's is claimed.
        int lp = Feed(NodeKind.TriMesh, tracks, out DbId obj, out DbId scene);
        check.Expect(NearV(SceneDb.PartSelfIllum[lp], new Vector3(0.25f, 0.5f, 0.75f)),
                     "a mesh's id 100 is selfIllumColor");
        check.Expect(MathF.Abs(SceneDb.PartAlpha[lp] - 0.25f) < 1e-4f, "...and its 128 is alpha");
        check.Expect(SceneDb.PartLitAnim[lp] == 0, "...and no light column is claimed by either");
        Release(lp, obj, scene);

        // ---- a light: 88 is its radius, and the mesh's two ids write nothing.
        int before = _litUncarried;
        lp = Feed(NodeKind.Light, tracks, out obj, out scene);
        check.Expect((SceneDb.PartLitAnim[lp] & LitRadius) != 0 && MathF.Abs(LitRadius1(lp, 1f) - 9f) < 1e-4f,
                     "a light's id 88 is its radius, not an emitter's birth rate");
        check.Expect(NearV(SceneDb.PartSelfIllum[lp], Vector3.Zero),
                     "...its 100 is verticalDisplacement, so it is NOT the mesh's self-illumination");
        check.Expect(MathF.Abs(SceneDb.PartAlpha[lp] - 1f) < 1e-4f, "...and 128 is not a light term at all");
        check.Expect(_litUncarried == before + 2, "...both uncarried light tracks are counted, not dropped");
        check.Expect(DbEmitterChannels.Value(lp, Ctl.EmFps, new Vector4(-1f)).X == -1f,
                     "...and a light spends no emitter channel block");
        Release(lp, obj, scene);

        // ---- an emitter: both ids are its own tracks, and no mesh or light column moves.
        lp = Feed(NodeKind.Emitter, tracks, out obj, out scene);
        check.Expect(MathF.Abs(DbEmitterChannels.Value(lp, Ctl.EmBirthRate, new Vector4(-1f)).X - 9f) < 1e-4f,
                     "an emitter's id 88 is its birth rate");
        check.Expect(MathF.Abs(DbEmitterChannels.Value(lp, Ctl.EmFps, new Vector4(-1f)).X - 0.25f) < 1e-4f,
                     "...and its 128 is fps");
        check.Expect(SceneDb.PartLitAnim[lp] == 0 && MathF.Abs(SceneDb.PartAlpha[lp] - 1f) < 1e-4f
                     && NearV(SceneDb.PartSelfIllum[lp], Vector3.Zero),
                     "...and neither touched a light or mesh column");
        Release(lp, obj, scene);
    }

    /// <summary>Pose <paramref name="tracks"/> at t=2 onto a leaf part of the given kind; answers the part.
    /// </summary>
    private static int Feed(NodeKind node, ControllerTrack[] tracks, out DbId obj, out DbId scene)
    {
        ModelDef m = Chain();
        m.Nodes[2].Kind = node;
        if (node == NodeKind.Light) m.Nodes[2].Light = new LightDef();
        if (node == NodeKind.Emitter) m.Nodes[2].Emitter = new EmitterDef();
        m.Nodes[2].Controllers = tracks;
        m.Anims[0].Nodes[2].Controllers = tracks;

        scene = SceneDb.Scenes.Alloc();
        obj = Subject(m, scene, out int first);
        int lp = first + 2;
        SceneDb.PartSelfIllum[lp] = Vector3.Zero;
        SceneDb.PartLitAnim[lp] = 0;
        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn, 0f);
        Sample(obj, m.Anims[0], 2f);
        return lp;
    }

    /// <inheritdoc cref="Feed"/>
    private static void Release(int lp, DbId obj, DbId scene)
    {
        DbEmitterChannels.Forget(lp);
        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    // ================= 10: LOD bands =================

    /// <summary>A recycled object index must not inherit an LOD level: the feed refuses every part of an
    /// alternate the object does not have, so the object is INVISIBLE with nothing logged.</summary>
    private static void LodBands(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out _);
        SetLodModels(obj, m, m);
        SetLodDistances(obj, 10f, 20f);
        SceneDb.ObjLodShown[obj.Index] = 2;
        int wanted = _lodWanted;
        check.Expect(wanted >= 1, "an object with alternates is counted");
        Drop(obj);
        check.Expect(_lodWanted == wanted - 1, "retiring it gives the count back");

        DbId again = Subject(m, scene, out _);
        check.Expect(SceneDb.ObjLodShown[again.Index] == 0 && _lodLevels[again.Index] == 0,
                     "a recycled index starts at level 0 with no alternates -- never invisible");
        Drop(again);
        SceneDb.Scenes.Free(scene);
    }


    // ================= 11: "is it playing" =================

    /// <summary>
    /// <c>Gob::GetAnimation</c>'s contract, which is the one the game branches on: the handle answers
    /// <b>is this animation PLAYING</b>, not <b>does the model have it</b>. Getting that wrong crashes the
    /// client: <c>CNWCObject::SetAnimation</c> calls <c>ShowEquippedItems</c> through a slot that answers null
    /// on a placeable, with no guard, so a handle for an animation the model merely OWNS passes it null.
    /// </summary>
    private static void Playing(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out _);

        nint h = GetAnimation(obj, "wave", out float t, out float len);
        check.Expect(h == 0, "an animation the model HAS but is not playing answers a NULL handle");
        check.Expect(t == -1f, "...with time -1");
        check.Expect(MathF.Abs(len - 2f) < 1e-4f, "...and the animation's real length");

        h = GetAnimation(obj, "no_such_animation", out t, out len);
        check.Expect(h == 0 && t == -1f && len == -1f,
                     "a name that does not resolve answers null and -1/-1");

        PlayAnimation(obj, "wave", 1f, RunFlags.NoFadeIn, 0f);
        _rTime[Slot(obj.Index, 0)] = 0.75f;
        h = GetAnimation(obj, "wave", out t, out len);
        check.Expect(h != 0, "a PLAYING animation answers a non-null handle");
        check.Expect(MathF.Abs(t - 0.75f) < 1e-4f, "...the run's current time through the FIRST out-param");
        check.Expect(MathF.Abs(len - 2f) < 1e-4f, "...and the length through the second");
        check.Expect(GetAnimation(obj, "WAVE", out _, out _) != 0, "the name match is case-insensitive");

        // ...and it stops answering the moment the queue does.
        PlayAnimation(obj, null, 1f, 0, 0f);
        check.Expect(GetAnimation(obj, "wave", out t, out _) == 0 && t == -1f,
                     "clearing the queue stops it being 'playing'");

        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>Two objects animating at once keep SEPARATE run queues. A zero-filled block column read as
    /// "already holds block 0", so every object shared one queue and the whole area played whatever was
    /// started last -- and the free list, pushed block 0 twice, pointed at itself from then on.</summary>
    private static void Blocks(Checker check)
    {
        ModelDef m = Chain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId a = Subject(m, scene, out _);
        DbId b = Subject(m, scene, out _);

        PlayAnimation(a, "wave", 1f, RunFlags.NoFadeIn | RunFlags.Loop, 0f);
        check.Expect(_block[b.Index] < 0 && GetAnimation(b, "wave", out _, out _) == 0,
                     "playing an animation on one object leaves another one alone");
        PlayAnimation(b, "wave", 1f, RunFlags.NoFadeIn | RunFlags.Loop, 0f);
        check.Expect(_block[a.Index] >= 0 && _block[b.Index] >= 0 && _block[a.Index] != _block[b.Index],
                     "two animating objects hold run blocks of their own");

        // And the free list survives the round trip: a block given back is handed out once, not forever.
        int was = _block[a.Index];
        Drop(a);
        DbId c = Subject(m, scene, out _);
        PlayAnimation(c, "wave", 1f, RunFlags.NoFadeIn, 0f);
        check.Expect(_block[c.Index] == was && _block[c.Index] != _block[b.Index],
                     "a freed block is reused once, not shared");

        Drop(c);
        Drop(b);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>An animmesh whose <c>animtverts</c> pool is its ONLY pool scrolls its UVs: the frame index,
    /// the lerp between two frames and the weight blend, over a node with no position frames at all -- which
    /// is the shape the load screen's smoke has, and the one that used to morph nothing.</summary>
    private static void UvMorph(Checker check)
    {
        ModelDef m = UvChain();
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out int first);
        int part = first + 1;   // "mid", the animmesh node

        PlayAnimation(obj, "smoke", 1f, RunFlags.NoFadeIn, 0f);
        _rTime[Slot(obj.Index, 0)] = 0f;
        Pose(obj, m.Anims[0], Slot(obj.Index, 0), 1f);
        float* uv = (float*)_dynUv[part];
        check.Expect(uv != null, "an animmesh with UV frames and no position frames still morphs");
        if (uv != null)
        {
            check.Expect(MathF.Abs(uv[0] - 0f) < 1e-4f && MathF.Abs(uv[1] - 0f) < 1e-4f,
                         "...frame 0 of the UV pool at t=0");
            _rTime[Slot(obj.Index, 0)] = 0.5f;
            Pose(obj, m.Anims[0], Slot(obj.Index, 0), 1f);
            check.Expect(MathF.Abs(uv[0] - 0.5f) < 1e-4f, "...halfway between frames 0 and 1 at t=0.5");
            _rTime[Slot(obj.Index, 0)] = 5f;
            Pose(obj, m.Anims[0], Slot(obj.Index, 0), 1f);
            check.Expect(MathF.Abs(uv[0] - 1f) < 1e-4f, "...and the LAST frame past the end, never past it");
            _rTime[Slot(obj.Index, 0)] = 0f;
            Pose(obj, m.Anims[0], Slot(obj.Index, 0), 0.5f);
            check.Expect(MathF.Abs(uv[0] - 0.5f) < 1e-4f, "a half-weight run blends the UVs, as it does positions");
        }

        Drop(obj);
        check.Expect(_dynUv[part] == 0, "the UV buffer goes back with the part's dynamic state");
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>A GUI scrollbar thumb: an animmesh sliver whose morph stretches it down. Oracle: the hand-built quad
    /// below -- a segment at y=-1 misses the rest sliver, hits the stretched one at z=0, and the hit box reaches
    /// the last frame's y=-2.</summary>
    private static void ThumbHit(Checker check)
    {
        Vector3[] rest = { new(-1f, 0.1f, 0f), new(1f, 0.1f, 0f), new(1f, 0f, 0f), new(-1f, 0f, 0f) };
        Vector3[] tall = { new(-1f, 0.1f, 0f), new(1f, 0.1f, 0f), new(1f, -2f, 0f), new(-1f, -2f, 0f) };
        ModelDef m = UvChain();
        MeshDef mesh = m.Nodes[1].Mesh!;
        mesh.Positions = rest;
        mesh.Indices = new uint[] { 0, 1, 2, 0, 2, 3 };
        mesh.Uv[0] = new Vector2[4];
        mesh.Faces = new[] { new FaceDef { V0 = 0, V1 = 1, V2 = 2 }, new FaceDef { V0 = 0, V1 = 2, V2 = 3 } };
        var anim = new AnimMeshDef { SamplePeriod = 1f, Positions = new[] { rest, tall } };
        m.Nodes[1].AnimMesh = anim;
        m.Anims[0].Nodes[1].AnimMesh = anim;
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out int first);
        int part = first + 1;

        Vector3 from = new(0f, -1f, -1f), to = new(0f, -1f, 1f);
        check.Expect(!CastMorphed(part, mesh, from, ref to), "an unmorphed thumb is hit only on its rest sliver");
        PlayAnimation(obj, "smoke", 1f, RunFlags.NoFadeIn, 0f);
        _rTime[Slot(obj.Index, 0)] = 5f;
        Pose(obj, m.Anims[0], Slot(obj.Index, 0), 1f);
        check.Expect(CastMorphed(part, mesh, from, ref to) && MathF.Abs(to.Z) < 1e-4f, $"the stretched thumb is hit where it is drawn ({to})");
        Vector3 lo = new(-1f, 0f, 0f), hi = new(1f, 0.1f, 0f);
        HitBounds.MorphBox(m, "mid", ref lo, ref hi);
        check.Expect(lo.Y == -2f && hi.Y == 0.1f, $"the hit box covers every morph frame ({lo.Y}..{hi.Y})");

        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary><see cref="Chain"/>'s three nodes with "mid" a one-triangle animmesh whose only pool is two UV
    /// frames, scrolling u from 0 to 1 over one sample period.</summary>
    private static ModelDef UvChain()
    {
        var m = new ModelDef { Name = "cx_selfcheck_uv", AnimScale = 1f, BMin = new Vector3(-1f), BMax = new Vector3(1f) };
        var mesh = new MeshDef
        {
            Positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
            Indices = new uint[] { 0, 1, 2 },
        };
        mesh.Uv[0] = new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero };
        var anim = new AnimMeshDef
        {
            SamplePeriod = 1f,
            Uvs = new[]
            {
                new[] { Vector2.Zero, Vector2.Zero, Vector2.Zero },
                new[] { Vector2.UnitX, Vector2.UnitX, Vector2.UnitX },
            },
        };
        m.Nodes = new[]
        {
            new NodeDef { Name = "root", Index = 0, Parent = -1, Children = new[] { 1 }, Kind = NodeKind.Dummy },
            new NodeDef { Name = "mid", Index = 1, Parent = 0, Children = new[] { 2 }, Kind = NodeKind.AnimMesh,
                          Mesh = mesh, AnimMesh = anim },
            new NodeDef { Name = "leaf", Index = 2, Parent = 1, Children = Array.Empty<int>(), Kind = NodeKind.Dummy },
        };
        for (int i = 0; i < m.Nodes.Length; i++) m.NodeByName[m.Nodes[i].Name] = i;
        m.Anims = new[]
        {
            new AnimDef
            {
                Name = "smoke", Length = 2f, TransTime = 0f, AnimRoot = "root",
                Nodes = new[]
                {
                    new AnimNode { Name = "root", Parent = -1 },
                    new AnimNode { Name = "mid", Parent = 0, AnimMesh = anim },
                    new AnimNode { Name = "leaf", Parent = 1 },
                },
            },
        };
        return m;
    }

    /// <summary>A part pose written from outside the runtime is seen by the next getter in the same pass:
    /// <c>SetHeightByAnchor</c> moves "text1" and reads it straight back.</summary>
    private static void PoseWrite(Checker check)
    {
        DbId scene = SceneDb.Scenes.Alloc();
        DbId body = Subject(Chain(), scene, out int first);
        int leaf = first + 2;
        float y0 = PartWorld(leaf).M42;
        SceneDb.PartPos[leaf] += new Vector3(0f, 1f, 0f);
        PoseWritten(body);
        check.Expect(MathF.Abs(PartWorld(leaf).M42 - (y0 + 1f)) < 1e-4f,
                     "a part pose written between two getters is composed by the second");
        // An object moved twice in one pass is on the dirty list once: the getter still answers the second move.
        ComposeFor(body);
        float y1 = SceneDb.PartWorld[leaf].M42;
        SceneDb.ObjPos[body.Index] += new Vector3(0f, 2f, 0f);
        SceneDb.Touch(body, Dirty.Xform);
        ComposeFor(body);
        SceneDb.ObjPos[body.Index] += new Vector3(0f, 1f, 0f);
        SceneDb.Touch(body, Dirty.Xform);
        ComposeFor(body);
        check.Expect(MathF.Abs(SceneDb.PartWorld[leaf].M42 - (y1 + 3f)) < 1e-4f,
                     "a part getter answers an object moved twice in one pass, not its first move");
        Drop(body);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>A name that only an APPENDED piece carries resolves against the whole composition, and both
    /// overloads of the question give the same answer. <c>Gob::Append</c> asks with a managed string, and a
    /// creature's later body parts anchor to nodes the earlier ones brought -- so an overload that searched
    /// only the base model refused those appends and the pieces never existed.</summary>
    private static void Composition(Checker check)
    {
        DbId scene = SceneDb.Scenes.Alloc();
        DbId body = Subject(Chain(), scene, out _);
        DbId piece = Subject(Graft(), scene, out int pieceFirst);
        SceneDb.ObjSeamAppendHead[body.Index] = piece;
        SceneDb.ObjSeamAppendNext[piece.Index] = default;

        check.Expect(CX.Alloy.Seam.ObjectSlots.PartByName(body, "gmid") == pieceFirst + 1,
                     "a part only an appended piece carries resolves against the composition");
        check.Expect(CX.Alloy.Seam.ObjectSlots.PartByName(body, "mid") != pieceFirst + 1,
                     "...and the base model's own name still wins");
        check.Expect(CX.Alloy.Seam.ObjectSlots.PartByName(body, "no_such_node") < 0,
                     "...while a name nothing carries is still -1");

        SceneDb.ObjSeamAppendHead[body.Index] = default;
        Drop(piece);
        Drop(body);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>An amputation takes the piece ROOTED at the name, not the first that merely holds a part of
    /// it. Two grafted pieces share every inner node name here, as a creature's body parts do -- matching on
    /// containment took whichever was appended last, so a re-equip removed the wrong piece and left the old
    /// one under the new one.</summary>
    private static void Amputation(Checker check)
    {
        DbId scene = SceneDb.Scenes.Alloc();
        DbId body = Subject(Chain(), scene, out _);
        DbId chest = Subject(Graft("chest_g"), scene, out _);
        DbId arm = Subject(Graft("arm_g"), scene, out _);
        // Appended head-first, as Gob::Append links them: arm, then chest.
        SceneDb.ObjSeamAppendHead[body.Index] = chest;
        SceneDb.ObjSeamAppendNext[chest.Index] = arm;
        SceneDb.ObjSeamAppendNext[arm.Index] = default;

        DbId take = CX.Alloy.Seam.ObjectSlots.AmputateTarget(body, "arm_g", out bool byContainment);
        check.Expect(take.Value == arm.Value, "an amputation takes the piece ROOTED at the name");
        check.Expect(!byContainment, "...on its root, not by containment");

        // "gmid" is a node BOTH pieces carry, which is the ambiguous case the engine never reaches.
        take = CX.Alloy.Seam.ObjectSlots.AmputateTarget(body, "gmid", out byContainment);
        check.Expect(take.Value == chest.Value && byContainment,
                     "a name no piece is rooted at falls back to the first that holds it, and says so");
        check.Expect(CX.Alloy.Seam.ObjectSlots.AmputateTarget(body, "no_such_node", out _).IsNull,
                     "...and a name nothing carries takes nothing");

        SceneDb.ObjSeamAppendHead[body.Index] = default;
        Drop(arm);
        Drop(chest);
        Drop(body);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary><see cref="Chain"/>'s shape under names nothing else uses, for the composition check.</summary>
    private static ModelDef Graft(string root = "groot")
    {
        var m = new ModelDef { Name = "cx_selfcheck_graft", AnimScale = 1f, BMin = new Vector3(-1f), BMax = new Vector3(1f) };
        m.Nodes = new[]
        {
            new NodeDef { Name = root, Index = 0, Parent = -1, Children = new[] { 1 }, Kind = NodeKind.Dummy },
            new NodeDef { Name = "gmid", Index = 1, Parent = 0, Children = new[] { 2 }, Kind = NodeKind.Dummy },
            new NodeDef { Name = "gleaf", Index = 2, Parent = 1, Children = Array.Empty<int>(), Kind = NodeKind.Dummy },
        };
        for (int i = 0; i < m.Nodes.Length; i++) m.NodeByName[m.Nodes[i].Name] = i;
        return m;
    }

    private static bool NearV(Vector3 a, Vector3 b) => (a - b).Length() < 1e-4f;

    /// <summary><see cref="ChooseLod"/>: the engine's plain verdict, hysteresis both ways, and its four stand-downs.</summary>
    private static void LodChoice(Checker check)
    {
        const float near = 10f, far = 20f, k = 1f, h = 0.1f;
        float up = (1f + h) * (1f + h);
        check.Expect(ChooseLod(150f, near, far, 0, true, k, 0f, true) == 1, "past the near boundary (10 units, squared), level 0 rises one step");
        check.Expect(ChooseLod(150f, near, far, 1, true, k, 0f, true) == 1, "...and holds there");
        check.Expect(ChooseLod(500f, near, far, 1, true, k, 0f, true) == 2, "past the far boundary, level 1 rises");
        check.Expect(ChooseLod(500f, near, far, 0, true, k, 0f, true) == 1, "...one level at a time, never two");
        check.Expect(ChooseLod(150f, near, far, 2, true, k, 0f, true) == 1, "inside the middle band, level 2 falls one step");
        check.Expect(ChooseLod(1f, near, far, 1, true, k, 0f, true) == 0, "inside the near band, level 1 falls");
        check.Expect(ChooseLod(near * near * 1.02f, near, far, 0, true, k, h, true) == 0,
                     "a distance just past the near boundary does not switch: that is the pop");
        check.Expect(ChooseLod(near * near * up * 1.01f, near, far, 0, true, k, h, true) == 1, "...and one past the widened boundary does");
        check.Expect(ChooseLod(near * near * 0.98f, near, far, 1, true, k, h, true) == 1, "coming back, level 1 holds inside the band");
        check.Expect(ChooseLod(near * near / up * 0.99f, near, far, 1, true, k, h, true) == 0, "...and drops past it");
        check.Expect(ChooseLod(500f, near, far, 0, false, k, h, true) == 0, "g_bEnableLOD 0 leaves the level alone");
        check.Expect(ChooseLod(500f, near, far, 0, true, k, h, false) == 0, "an object with no alternate root has no choice");
        check.Expect(ChooseLod(-1f, near, far, 0, true, k, h, true) == 0, "a negative distance (never measured) stands down");
        check.Expect(ChooseLod(500f, near, far, 0, true, 0f, h, true) == 0, "a zero LOD scale factor stands down");
        check.Expect(ChooseLod(99f * 99f, near, far, 0, true, 10f, 0f, true) == 0 && ChooseLod(101f * 101f, near, far, 0, true, 10f, 0f, true) == 1,
                     "g_fLODScaleFactor multiplies the switch distance before it is squared");
    }
}
