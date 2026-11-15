using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Alloy.Physics;

namespace CX.Alloy.Db;

/// <summary>The dangly dispatch over DB columns (docs/spec/dangly-motions.md, sections 4 and 4a): per part,
/// the motion its node's own rig asks for. A rigged part is solved into bone matrices and drawn through the
/// skinning path; every other one runs the stock solve through its own dynamic vertex range. Everything read
/// here is a column or an immutable def, so there is no engine state and no liveness question.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Which path one dangly part takes this frame.</summary>
    private enum DanglyPath : byte { Rest, Rigged, Vanilla }

    /// <summary>Radians/s the ambient breeze direction wanders through.</summary>
    private const float BreezeTurn = 0.05f;
    /// <summary>The engine's own <c>dt</c> clamp, which the wind sample inherits.</summary>
    private const float MinDt = 0.0125f, MaxDt = 0.035f;
    /// <summary>A node counts as posed once its animation turns it more than a degree off its rest orientation
    /// (the cosine of half that angle) or moves it more than a centimetre off its rest position.</summary>
    private const float PoseCosHalf = 0.99996192f, PoseMove = 0.01f;
    /// <summary>How close to its rest shape, model units, a posed part must hang before the CX motion takes it
    /// back: under a quarter of a centimetre, so the hand-back cannot be seen.</summary>
    private const float PoseSettle = 0.005f;
    /// <summary>Seconds a posed part's animation must hold the rest pose before the hold lets go: longer than an
    /// idle loop spends crossing it.</summary>
    private const float PoseRestHold = 0.5f;
    /// <summary>Seconds over which the part of a handed-over shape the stock solve cannot reach fades out: the
    /// cross-fade time a door's animations state.</summary>
    private const float HandOverFade = 0.25f;
    /// <summary>Parent links a posed walk follows before it gives up on a broken tree.</summary>
    private const int PoseHops = 64;

    /// <summary>Per dangly part: the solver state of whichever path it is on (never both), and the period
    /// counters. One owner, so a reset is one assignment.</summary>
    private sealed class DanglyState
    {
        internal readonly DanglyJointsState?[] Joints = new DanglyJointsState?[SceneDb.MaxParts];
        internal readonly VanillaDanglyState?[] Stock = new VanillaDanglyState?[SceneDb.MaxParts];
        internal int Parts, Verts, Sway, Jiggle, Swing, Stocked, Resting, Settled, Posed;
        /// <summary>Per part: its animation posed it, so it runs the stock solve until it hangs at rest again.</summary>
        internal readonly bool[] Held = new bool[SceneDb.MaxParts];
        /// <summary>Per held part: seconds its animation has held the rest pose.</summary>
        internal readonly float[] RestFor = new float[SceneDb.MaxParts];
        /// <summary>Per part handed to the stock solve: what of the rig's shape lies past the stock clamp, kept for
        /// the part's next switch.</summary>
        internal readonly DanglyFade?[] Fade = new DanglyFade?[SceneDb.MaxParts];
        internal int NoSkinRange, NoBoneRange;
        internal bool SaidNoSkinRange, SaidNoBoneRange;
        internal long Ticks;
        /// <summary>Per rigged Swing part, the static faces it keeps clear of; the one gather's scratch; the
        /// gather budget's window; and the period's collision counters.</summary>
        internal readonly DanglyWall?[] Walls = new DanglyWall?[SceneDb.MaxParts];
        internal readonly DanglyGather Gather = new();
        internal readonly int[] WallScratch = new int[WallMeshFaces];
        internal long WallWindowAt, WallSpent, GatherTicks;
        internal int WallParts, Gathers, GatheredFaces, WallDropped, WallTruncated;
        /// <summary>Per worn strand, its wearer's body, refilled every frame; and the period's counters.</summary>
        internal readonly DanglyBody?[] Bodies = new DanglyBody?[SceneDb.MaxParts];
        internal int StrandParts, Capsules, CapsulesDropped;
        /// <summary>The air clock a self-check holds, seconds, so its breeze blows the same way every run; NaN
        /// reads the wall clock.</summary>
        internal float PinnedClock = float.NaN;
    }

    private static DanglyState _dangly = new();

    /// <summary>The offset, per vertex, from the shape the stock solve was seeded with to the rig's shape, and
    /// the seconds left before it is gone.</summary>
    private sealed class DanglyFade
    {
        internal readonly Vector3[] Excess;
        internal float Left;
        internal DanglyFade(int verts) => Excess = new Vector3[verts];
    }

    /// <summary>One dangly part, from the part walk. Wind applies only when <paramref name="windy"/>: the
    /// engine's GUI scenes (chargen, portraits) have none.</summary>
    private static void DanglyPart(int part, float dt, bool windy)
    {
        ModelDef? model = SceneDb.PartModel[part];
        int node = SceneDb.PartNode[part];
        if (model == null || (uint)node >= (uint)model.Nodes.Length) return;
        NodeDef n = model.Nodes[node];
        DanglyDef? dn = n.Dangly;
        MeshDef? mesh = n.Mesh;
        if (dn == null || mesh == null) return;
        DanglyRig? rig = n.Rig;
        DanglyClass cls = rig?.Class ?? DanglyClass.Static;
        bool auto = Dangly.Motion.V == Dangly.MotionAuto;
        bool posed = auto && rig != null && cls != DanglyClass.Static && DanglyHeld(part, mesh, dt);
        DanglyPath path = ChooseDangly(cls, rig != null, auto, KindOn(cls), AlloyRenderer.Skinning, posed);
        if (path == DanglyPath.Rest)
        {
            DanglyDropStock(part);
            DanglyDropRig(part);
            _dangly.Resting++;
            return;
        }
        if (path == DanglyPath.Rigged && DanglyRigged(part, rig!, mesh, dt, windy)) return;
        if (posed) _dangly.Posed++;
        DanglyStock(part, dn, mesh, rig, dt, windy);
    }

    /// <summary>Does the pose rule hold <paramref name="part"/> on the stock solve this frame? From the frame
    /// its animation poses it until it hangs back at its rest shape; never for a part a creature wears.</summary>
    private static bool DanglyHeld(int part, MeshDef mesh, float dt)
    {
        if (DanglyWorn(part, out _)) return _dangly.Held[part] = false;
        if (DanglyPosed(part)) { _dangly.RestFor[part] = 0f; return _dangly.Held[part] = true; }
        if (!_dangly.Held[part]) return false;
        if (float.IsFinite(dt) && dt > 0f) _dangly.RestFor[part] += dt;
        if (_dangly.RestFor[part] < PoseRestHold) return true;
        VanillaDanglyState? st = _dangly.Stock[part];
        if (st != null && st.Seeded && !DanglyNearRest(st.Cur, mesh.Positions)) return true;
        return _dangly.Held[part] = false;
    }

    /// <summary>An animation holds this part's node, or a node above it short of the object's root, off its
    /// rest pose. The root's own motion moves the whole object, which every solve already follows.</summary>
    private static bool DanglyPosed(int part)
    {
        int p = part;
        for (int hop = 0; hop < PoseHops && (uint)p < (uint)SceneDb.MaxParts; hop++)
        {
            int up = SceneDb.PartParent[p];
            if (up < 0) return false;
            ModelDef? m = SceneDb.PartModel[p];
            int node = SceneDb.PartNode[p];
            if (m == null || (uint)node >= (uint)m.Nodes.Length) return false;
            NodeDef n = m.Nodes[node];
            if (MathF.Abs(Quaternion.Dot(SceneDb.PartRot[p], n.Orientation)) < PoseCosHalf) return true;
            if (Vector3.DistanceSquared(SceneDb.PartPos[p], n.Position) > PoseMove * PoseMove) return true;
            p = up;
        }
        return false;
    }

    /// <summary>Every vertex of <paramref name="cur"/> hangs within <see cref="PoseSettle"/> of its rest.</summary>
    private static bool DanglyNearRest(ReadOnlySpan<Vector3> cur, ReadOnlySpan<Vector3> rest)
    {
        int n = Math.Min(cur.Length, rest.Length);
        for (int i = 0; i < n; i++)
            if (Vector3.DistanceSquared(cur[i], rest[i]) > PoseSettle * PoseSettle) return false;
        return true;
    }

    /// <summary>The motion one part takes (spec section 4): the node's class, unless something stands the
    /// rigged path down -- no rig at all, stock motion, the kind's own knob off, no skinning path, or an
    /// animation holding the part off its rest pose. A Static node has no moving vertex, so it is drawn at rest.</summary>
    private static DanglyPath ChooseDangly(DanglyClass cls, bool hasRig, bool auto, bool kindOn, bool skinning,
                                           bool posed = false)
    {
        if (!hasRig) return DanglyPath.Vanilla;
        if (cls == DanglyClass.Static) return DanglyPath.Rest;
        return auto && kindOn && skinning && !posed ? DanglyPath.Rigged : DanglyPath.Vanilla;
    }

    /// <summary>Is this class's own knob on?</summary>
    private static bool KindOn(DanglyClass cls) => cls switch
    {
        DanglyClass.Sway => Dangly.Sway.V,
        DanglyClass.Jiggle => Dangly.Jiggle.V,
        DanglyClass.Swing => Dangly.Swing.V,
        _ => true,
    };

    /// <summary>Solve and publish one rigged part (spec section 4a): the joint solver's node-space bones into
    /// the part's own cell of the bone palette, and the part named as a skinned draw. False = this part cannot
    /// take the skinning path, which is counted and said once -- the caller then runs the stock solve.</summary>
    private static bool DanglyRigged(int part, DanglyRig rig, MeshDef mesh, float dt, bool windy)
    {
        int shared = SceneDb.PartMesh[part];
        if (rig.Skin == null || rig.BoneCount <= 0 || shared < 0 || !MeshArena.SkinnedRange(shared))
        {
            DanglyDropRig(part);
            _dangly.NoSkinRange++;
            if (!_dangly.SaidNoSkinRange)
            {
                _dangly.SaidNoSkinRange = true;
                _log?.Always("DbAnim: a rigged dangly node's mesh holds no SKINNED arena range, so its bones "
                           + "cannot reach the vertex stage -- those parts move the STOCK way instead. Counted "
                           + "on [dbanim] as dangly-noskinrange.");
            }
            return false;
        }
        int at = DanglyBoneAlloc(part, rig.BoneCount, DanglyReach(rig));
        if (at < 0)
        {
            DanglyDropRig(part);
            _dangly.NoBoneRange++;
            if (!_dangly.SaidNoBoneRange)
            {
                _dangly.SaidNoBoneRange = true;
                _log?.Always("DbAnim: the renderer's bone stage refused a range for a rigged dangly part -- "
                           + "those parts move the STOCK way instead. Counted on [dbanim] as "
                           + "dangly-nobonerange.");
            }
            return false;
        }
        DanglyDropStock(part);

        int joints = rig.JointRest.Length;
        DanglyJointsState? js = _dangly.Joints[part];
        if (js == null || js.X.Length != joints) _dangly.Joints[part] = js = new DanglyJointsState(joints);

        Matrix4x4 world = SceneDb.PartWorld[part];
        bool worn = DanglyWorn(part, out DbId wearer);
        var inp = new DanglyInputs(world, DanglyMean(windy), DanglyPoint(in world, windy), DanglyClock(),
                                   windy ? 1f : 0f, dt, Dangly.WindGain.V, Dangly.Tilt.V, Dangly.Cage.V,
                                   DanglyWalls(part, rig, mesh, in world, Dangly.Collide.V && RateWallsNear(part)), worn,
                                   DanglyBodyOf(part, rig, wearer, worn), Dangly.Strand.V);
        int cell = _danglyBones.Cell[part];
        Span<Matrix4x4> bones = _danglyBones.Scratch.AsSpan(0, Math.Min(rig.BoneCount, cell));
        bool moved = DanglyJoints.Step(rig, js, inp, bones);
        _dangly.Parts++;
        Tally(rig.Class);
        // A part whose base changed is new to this cell: its history must be its own pose, or it smears from
        // whatever the cell last held.
        bool fresh = SceneDb.PartBoneBase[part] != at;
        SceneDb.PartBoneBase[part] = at;
        if (!moved && !fresh) { _dangly.Settled++; return true; }
        DanglyWriteBones(at, cell, bones);
        if (fresh) AlloyRenderer.DbBoneSeedPrev(at, cell);
        AlloyRenderer.NoteDanglyMoved(part, fresh ? float.PositiveInfinity : js.Moved, Dangly.Still.V);
        SceneDb.TouchPart(part, Dirty.Anim);
        return true;
    }

    /// <summary>A creature wears <paramref name="part"/>: its object is one, or rides one up the attach chain.
    /// <paramref name="wearer"/> is that creature.</summary>
    private static bool DanglyWorn(int part, out DbId wearer)
    {
        DbId o = SceneDb.PartObj[part];
        for (int hop = 0; hop < 8 && SceneDb.Objects.Valid(o); hop++)
        {
            if ((SceneDb.ObjFlags[o.Index] & ObjFlag.Creature) != 0) { wearer = o; return true; }
            o = SceneDb.ObjAttachTo[o.Index];
        }
        wearer = default;
        return false;
    }

    private static void Tally(DanglyClass cls)
    {
        switch (cls)
        {
            case DanglyClass.Sway: _dangly.Sway++; break;
            case DanglyClass.Jiggle: _dangly.Jiggle++; break;
            case DanglyClass.Swing: _dangly.Swing++; break;
        }
    }

    /// <summary>The stock solve for one part: its own dynamic vertex range, the positions
    /// <see cref="VanillaDangly.Run"/> writes into it, and the renderer's two notes -- the outgoing positions
    /// for the motion vectors, the measured displacement for the shadow-caster verdict.</summary>
    private static void DanglyStock(int part, DanglyDef dn, MeshDef mesh, DanglyRig? rig, float dt, bool windy)
    {
        int n = Math.Min(mesh.Positions.Length, dn.Constraints.Length);
        Span<Vector3> dst = n > 0 ? DynTake(part, mesh.Positions) : default;
        if (dst.IsEmpty) { DanglyDropRig(part); return; }
        VanillaDanglyState? st = _dangly.Stock[part];
        if (st == null || st.Cur.Length != mesh.Positions.Length)
        {
            _dangly.Stock[part] = st = new VanillaDanglyState(mesh.Positions.Length);
            if (rig != null && DanglyHandOver(part, rig, mesh, dn, n, st))
            {
                st.Cur.AsSpan(0, n).CopyTo(dst);
                DanglyFadeOut(part, dst[..n], 0f);
            }
        }
        DanglyDropRig(part);

        Matrix4x4 world = SceneDb.PartWorld[part];
        var wpos = new Vector3(world.M41, world.M42, world.M43);
        // UNIT, out of the unscaled basis: the stock step rotates its terms by this and by its conjugate, and
        // a quaternion of norm sqrt(scale) would scale every one of them.
        Quaternion wrot = RotOf(world);
        // ponytail: the snapshot stage is game-thread state the motion sweep reads; on a render thread it
        // would need the arena's publish handoff (MeshArena.WriteDynamic) before that sweep may run off-thread.
        AlloyRenderer.NoteDanglyPrev(part, dst[..n]);
        float move = VanillaDangly.Run(mesh.Positions, dn.Constraints, dn.Displacement, dn.Tightness, dn.Period,
                                       st, wpos, wrot, DanglyWind(in world, dt, windy), dt,
                                       dst[..n]);
        move = MathF.Max(move, DanglyFadeOut(part, dst[..n], dt));
        _dangly.Parts++; _dangly.Stocked++; _dangly.Verts += n;
        AlloyRenderer.NoteDanglyMoved(part, move, Dangly.Still.V);
        if (!(move > 0f)) return;
        _dynProduced++;
        DynPublish(part);
        SceneDb.TouchPart(part, Dirty.Anim);
    }

    /// <summary>Seed a fresh stock state with the shape the rig drew last frame and the pose it drew it at, so
    /// the cloth carries on from where it hung. False when the part held no rigged shape to hand over.</summary>
    private static bool DanglyHandOver(int part, DanglyRig rig, MeshDef mesh, DanglyDef dn, int n,
                                       VanillaDanglyState st)
    {
        int at = SceneDb.PartBoneBase[part];
        DanglyJointsState? js = _dangly.Joints[part];
        SkinDef? skin = rig.Skin;
        if (at < 0 || js == null || !js.Seeded || skin == null) return false;
        Span<Matrix4x4> bones = AlloyRenderer.DbBoneCell(at, rig.BoneCount);
        Vector3[] rest = mesh.Positions;
        n = Math.Min(n, Math.Min(skin.Weights.Length, skin.Bones.Length));
        if (bones.IsEmpty || n <= 0) return false;
        DanglyFade? fade = _dangly.Fade[part];
        if (fade == null || fade.Excess.Length < n) _dangly.Fade[part] = fade = new DanglyFade(n);
        fade.Left = HandOverFade;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = skin.Skin(i, rest[i], bones);
            float c = dn.Constraints[i];
            float cap = c > 0f ? MathF.Max(VanillaDangly.Cap(dn.Displacement, c), 0f) : 0f;
            Vector3 r = rest[i];
            Vector3 seat = r + Vector3.Clamp(p - r, new Vector3(-cap), new Vector3(cap));
            st.Cur[i] = st.Prev[i] = seat;
            st.Vel[i] = default;
            fade.Excess[i] = p - seat;
        }
        fade.Excess.AsSpan(n).Clear();
        st.PrevPos = js.PrevPos;
        st.PrevRot = js.PrevRot;
        st.Acc = 0f;
        st.Seeded = true;
        return true;
    }

    /// <summary>Add what is left of the handed-over shape past the stock clamp onto <paramref name="dst"/>, then
    /// age it by <paramref name="dt"/>. Returns how far that moved the drawn shape.</summary>
    private static float DanglyFadeOut(int part, Span<Vector3> dst, float dt)
    {
        DanglyFade? f = _dangly.Fade[part];
        if (f == null || !(f.Left > 0f)) return 0f;
        float before = f.Left / HandOverFade;
        if (float.IsFinite(dt) && dt > 0f) f.Left -= dt;
        float k = MathF.Max(f.Left, 0f) / HandOverFade, far = 0f;
        int n = Math.Min(dst.Length, f.Excess.Length);
        for (int i = 0; i < n; i++)
        {
            dst[i] += f.Excess[i] * k;
            far = MathF.Max(far, f.Excess[i].Length());
        }
        return far * (before - k);
    }

    /// <summary>The wind the stock solve reads at a part: the engine's own, finite-checked. Zero in a GUI scene,
    /// which has no wind at all.</summary>
    private static Vector3 DanglyWind(in Matrix4x4 world, float dt, bool windy)
    {
        if (!windy || !AlloyWind.Owned) return Vector3.Zero;
        Vector3 w = AlloyWind.Wind(new Vector3(world.M41, world.M42, world.M43), Math.Clamp(dt, MinDt, MaxDt));
        return float.IsFinite(w.X) && float.IsFinite(w.Y) && float.IsFinite(w.Z) ? w : Vector3.Zero;
    }

    /// <summary>The mean wind the automatic motions move in, world metres per second: the area's base wind,
    /// raised to <c>dangly.breeze</c> outdoors where the area sets less. A breeze with no area direction to
    /// follow turns slowly. Zero in a GUI scene.</summary>
    private static Vector3 DanglyMean(bool windy)
    {
        if (!windy) return Vector3.Zero;
        Vector3 b = AlloyWind.Owned ? AlloyWind.Base : Vector3.Zero;
        var flat = new Vector2(b.X, b.Y);
        float area = float.IsFinite(flat.X) && float.IsFinite(flat.Y) ? flat.Length() : 0f;
        float units = MathF.Max(area, FrameInputs.Interior ? 0f : Dangly.Breeze.V);
        if (!(units > 0f)) return Vector3.Zero;
        float t = DanglyClock() * BreezeTurn;
        Vector2 dir = area > 1e-4f ? flat / area : new Vector2(MathF.Cos(t), MathF.Sin(t));
        return new Vector3(dir * (units * DanglyAir.SpeedPerUnit), 0f);
    }

    /// <summary>The point-source push at the part's origin, world metres per second -- sampled where the stock
    /// solve samples, so a bush feels the creature walking through its base.</summary>
    private static Vector3 DanglyPoint(in Matrix4x4 world, bool windy)
    {
        if (!windy || !AlloyWind.Owned) return Vector3.Zero;
        Vector3 p = AlloyWind.Point(new Vector3(world.M41, world.M42, world.M43)) * DanglyAir.PointSpeed;
        return float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) ? p : Vector3.Zero;
    }

    /// <summary>The air clock: wall seconds wrapped at an hour as the shaders' clock is, or a self-check's pinned
    /// clock while it runs. Not a sum of animate dt -- <see cref="Animate"/> runs once per SCENE, several a frame,
    /// and a sum turned the breeze that many times too fast for the spring to follow.</summary>
    private static float DanglyClock()
        => !float.IsNaN(_dangly.PinnedClock) ? _dangly.PinnedClock
         : (float)(System.Diagnostics.Stopwatch.GetTimestamp() % (System.Diagnostics.Stopwatch.Frequency * 3600L)
                   / (double)System.Diagnostics.Stopwatch.Frequency);

    /// <summary>End of the scene walk: nothing is queued (the dispatch is inline, on the part walk, as the
    /// engine's is), so this is where the audit speaks and a lost context's bone ranges go back.</summary>
    private static void DanglyDrain()
    {
        DynAudit();
        DanglyBonesPoll();
    }

    /// <summary>The period's dangly clause: the dispatch's own CPU time, what moved by motion, and what was
    /// refused.</summary>
    private static string DanglyPeriod(int frames)
    {
        DanglyState d = _dangly;
        float ms = frames > 0 ? (float)(d.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / frames) : 0f;
        string s = $"dangly {ms:F2} ms/frame, {d.Parts} part(s): sway {d.Sway}, jiggle {d.Jiggle}, "
                 + $"swing {d.Swing}, stock {d.Stocked}/{d.Verts} vert(s), rest {d.Resting}, "
                 + $"settled {d.Settled}, posed {d.Posed} | {_danglyBones.Live} bone range(s), {_danglyBones.High} peak";
        if (d.NoSkinRange != 0) s += $", {d.NoSkinRange} dangly-noskinrange (moving the STOCK way)";
        if (d.NoBoneRange != 0) s += $", {d.NoBoneRange} dangly-nobonerange (moving the STOCK way)";
        s += DanglyWallPeriod(frames) + DanglyBodyPeriod(frames);
        d.Parts = d.Verts = d.Sway = d.Jiggle = d.Swing = 0;
        d.Stocked = d.Resting = d.Settled = d.Posed = 0;
        d.NoSkinRange = d.NoBoneRange = 0;
        d.Ticks = 0;
        return s;
    }
}
