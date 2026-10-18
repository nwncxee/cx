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

    /// <summary>Per dangly part: the solver state of whichever path it is on (never both), and the period
    /// counters. One owner, so a reset is one assignment.</summary>
    private sealed class DanglyState
    {
        internal readonly DanglyJointsState?[] Joints = new DanglyJointsState?[SceneDb.MaxParts];
        internal readonly VanillaDanglyState?[] Stock = new VanillaDanglyState?[SceneDb.MaxParts];
        internal int Parts, Verts, Sway, Jiggle, Swing, Stocked, Resting, Settled;
        internal int NoSkinRange, NoBoneRange;
        internal bool SaidNoSkinRange, SaidNoBoneRange;
        internal long Ticks;
    }

    private static DanglyState _dangly = new();

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
        DanglyPath path = ChooseDangly(cls, rig != null, Dangly.Motion.V == Dangly.MotionAuto, KindOn(cls),
                                       AlloyRenderer.Skinning);
        if (path == DanglyPath.Rest)
        {
            DanglyDropStock(part);
            DanglyDropRig(part);
            _dangly.Resting++;
            return;
        }
        if (path == DanglyPath.Rigged && DanglyRigged(part, rig!, dt, windy)) return;
        DanglyStock(part, dn, mesh, dt, windy);
    }

    /// <summary>The motion one part takes (spec section 4): the node's class, unless something stands the
    /// rigged path down -- no rig at all, stock motion, the kind's own knob off, or no skinning path to draw
    /// it with. A Static node has no moving vertex, so it is drawn at rest and costs nothing.</summary>
    private static DanglyPath ChooseDangly(DanglyClass cls, bool hasRig, bool auto, bool kindOn, bool skinning)
    {
        if (!hasRig) return DanglyPath.Vanilla;
        if (cls == DanglyClass.Static) return DanglyPath.Rest;
        return auto && kindOn && skinning ? DanglyPath.Rigged : DanglyPath.Vanilla;
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
    private static bool DanglyRigged(int part, DanglyRig rig, float dt, bool windy)
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
        var inp = new DanglyInputs(world, DanglyWind(in world, VanillaDangly.StepDt, windy), dt, Dangly.WindGain.V,
                                   Dangly.Tilt.V, Dangly.Drag.V, Dangly.Cage.V);
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
    private static void DanglyStock(int part, DanglyDef dn, MeshDef mesh, float dt, bool windy)
    {
        DanglyDropRig(part);
        int n = Math.Min(mesh.Positions.Length, dn.Constraints.Length);
        if (n <= 0) return;
        float* dst = DynTake(part, mesh.Positions);
        if (dst == null) return;
        VanillaDanglyState? st = _dangly.Stock[part];
        if (st == null || st.Cur.Length != mesh.Positions.Length)
            _dangly.Stock[part] = st = new VanillaDanglyState(mesh.Positions.Length);

        Matrix4x4 world = SceneDb.PartWorld[part];
        var wpos = new Vector3(world.M41, world.M42, world.M43);
        // UNIT, out of the unscaled basis: the stock step rotates its terms by this and by its conjugate, and
        // a quaternion of norm sqrt(scale) would scale every one of them.
        Quaternion wrot = RotOf(world);
        // ponytail: the snapshot stage is game-thread state the motion sweep reads; under alloy.renderthread it
        // needs the arena's publish handoff (MeshArena.WriteDynamic) before that sweep may run off-thread.
        AlloyRenderer.NoteDanglyPrev(part, dst, n);
        float move = VanillaDangly.Run(mesh.Positions, dn.Constraints, dn.Displacement, dn.Tightness, dn.Period,
                                       st, wpos, wrot, DanglyWind(in world, dt, windy), dt,
                                       new Span<Vector3>(dst, n));
        _dangly.Parts++; _dangly.Stocked++; _dangly.Verts += n;
        AlloyRenderer.NoteDanglyMoved(part, move, Dangly.Still.V);
        if (!(move > 0f)) return;
        _dynGen[part]++;
        _dynProduced++;
        DynPublish(part);
        SceneDb.TouchPart(part, Dirty.Anim);
    }

    /// <summary>The wind at a part over a step of <paramref name="dt"/>, world space: what the area reports
    /// (finite-checked) plus the breeze: a slowly turning horizontal wind in the area's own units (a weak area's
    /// base wind is 1, a strong one's 2), gusting with its noise field, under automatic motion only -- stock is the
    /// unmodified game. Zero in a GUI scene, which has no wind at all.</summary>
    private static Vector3 DanglyWind(in Matrix4x4 world, float dt, bool windy)
    {
        if (!windy) return Vector3.Zero;
        var at = new Vector3(world.M41, world.M42, world.M43);
        float step = Math.Clamp(dt, MinDt, MaxDt);
        Vector3 w = AlloyWind.Owned ? AlloyWind.Wind(at, step) : Vector3.Zero;
        if (!float.IsFinite(w.X) || !float.IsFinite(w.Y) || !float.IsFinite(w.Z)) w = Vector3.Zero;
        float breeze = Dangly.Motion.V == Dangly.MotionAuto ? Dangly.Breeze.V : 0f;
        if (breeze <= 0f) return w;
        float t = DanglyClock() * BreezeTurn;
        return w + new Vector3(MathF.Cos(t), MathF.Sin(t), 0f)
                 * (breeze * step * AlloyWind.Noise(at));
    }

    /// <summary>The breeze clock: wall seconds, wrapped at 1024 to keep float precision. Not a sum of animate
    /// dt -- <see cref="Animate"/> runs once per SCENE, several a frame, and a sum turned the breeze that many
    /// times too fast for the spring to follow.</summary>
    private static float DanglyClock()
        => (float)(System.Diagnostics.Stopwatch.GetTimestamp() % (System.Diagnostics.Stopwatch.Frequency * 1024L)
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
                 + $"settled {d.Settled} | {_danglyBones.Live} bone range(s), {_danglyBones.High} peak";
        if (d.NoSkinRange != 0) s += $", {d.NoSkinRange} dangly-noskinrange (moving the STOCK way)";
        if (d.NoBoneRange != 0) s += $", {d.NoBoneRange} dangly-nobonerange (moving the STOCK way)";
        d.Parts = d.Verts = d.Sway = d.Jiggle = d.Swing = 0;
        d.Stocked = d.Resting = d.Settled = 0;
        d.NoSkinRange = d.NoBoneRange = 0;
        d.Ticks = 0;
        return s;
    }
}
