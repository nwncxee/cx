using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Diagonal moves and turns, a <see cref="PoseService"/> module (<c>anim.diagonal</c>, <c>anim.pivot</c>). A forward
/// diagonal (<c>pausewalkfl/fr</c>, <c>runfl/fr</c>, 0x54..0x57) plays the straight walk or run instead, swapped by id
/// inside <c>CNWCAnimBase::GetAnimationName</c> so the length, the pacing and a module's <c>ReplaceObjectAnimation</c>
/// all follow; the hips then spring toward the travel over a planted foot, the chest taking <see cref="UpperShare"/>.
/// </summary>
internal static unsafe class Diagonal
{
    /// <summary>Creature anim ids (<c>CNWCObject+0x5c</c>): the straight cycles, the walk-back, and the forward diagonals.</summary>
    private const ushort Walk = 2, WalkBack = 3, Run = 4;
    private const ushort WalkFl = 0x54, WalkFr = 0x55, RunFl = 0x56, RunFr = 0x57; // not-an-offset: anim ids
    /// <summary>Radians the body turns: the engine's diagonal is exactly 45° off the facing, both ways.</summary>
    private const float Quarter = MathF.PI / 4f;
    /// <summary>Seconds the turn eases over (SmoothCD time): nine tenths of it inside a quarter second, within the one stance
    /// it pivots over. A slower turn spans two stances and drags both feet round with the hips.</summary>
    private const float TurnTime = 0.1f;
    /// <summary>Radians of turn still to go under which the hips have come round: a foot planted after that is an
    /// ordinary step, not the pivot.</summary>
    private const float PivotLeft = MathF.PI / 180f;
    /// <summary>Share of the hips' turn the chest takes: enough that the waist does not read as a hinge.</summary>
    private const float UpperShare = 0.2f;
    /// <summary>World units per second of backward travel below which a walk-back's sideways share is noise.</summary>
    private const float MinBack = 0.2f;
    /// <summary>tan 22.5°: a walk-back drifting further off straight back than this is the diagonal.</summary>
    private const float BackSide = 0.414f;
    /// <summary>Radians under which the turn is settled at straight and the hips are left alone.</summary>
    private const float MinTurn = 1e-3f;
    /// <summary>Radians per second of facing turn a walking or running body's legs go round with directly: the
    /// engine's own fastest turn (355°/s), so every turn it makes along a path and every steady mouse turn is walked,
    /// not pivoted.</summary>
    private const float WalkTurn = 2f * MathF.PI;
    /// <summary>Radians the hips may trail off the facing, a diagonal's turn included: past it the waist reads as
    /// broken, so a sharper change (a reversal) drags the hips round with it for the rest.</summary>
    private const float MaxTwist = MathF.PI / 3f;
    /// <summary>Creature anim ids of the turns on the spot (<c>pauseturn</c>, <c>cturnr</c>): each steps its feet in
    /// place, so a planted one can be held through the turn.</summary>
    private const ushort TurnFirst = 0x50, TurnLast = 0x53; // not-an-offset: anim ids

    /// <summary>The engine's <c>GetAnimationName</c>. Its <c>CExoString</c> comes back through a hidden buffer --
    /// Itanium <c>(sret, this, id)</c>, Win64 <c>(this, sret, id)</c> -- and both return that buffer in rax, so the
    /// two leading pointers pass through in the order they came.</summary>
    private static delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr> _name;
    internal static int Swapped;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!SymbolMap.Require(log, "Diagonal", SymbolMap.AnimGetName)) return;
        _name = (delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr>)hooks.Install(SymbolMap.AnimGetName,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, uint, IntPtr>)&OnName);
        if (_name == null) log.Info("Diagonal: GetAnimationName did not hook -- diagonals keep the engine's animations");
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnName(IntPtr a, IntPtr b, uint anim)
    {
        ushort id = (ushort)anim;
        ushort to = Straight(id);
        if (to != id && PoseService.Swaps(new CNWCAnimBase(OperatingSystem.IsWindows() ? a : b).Gob.Ptr))
        {
            Swapped++;
            return _name(a, b, to);
        }
        return _name(a, b, anim);
    }

    /// <summary>The anim id to play for <paramref name="anim"/>: a forward diagonal's straight walk or run, else itself.</summary>
    internal static ushort Straight(ushort anim) => anim switch { WalkFl or WalkFr => Walk, RunFl or RunFr => Run, _ => anim };

    /// <summary>The anim id whose name the gob plays for the creature's <paramref name="anim"/>: the straight cycle when
    /// its name was <paramref name="swapped"/>.</summary>
    internal static int Played(int anim, bool swapped) => swapped ? Straight((ushort)anim) : anim;

    /// <summary>True while the hips have more than <see cref="PivotLeft"/> of the turn toward <paramref name="target"/>
    /// still to go: a foot planted now is the one they turn over (<see cref="Stride.Foot"/>).</summary>
    internal static bool Pivoting(float turn, float target) => MathF.Abs(target - turn) > PivotLeft;

    /// <summary>The body's turn off the facing, radians, positive = left, from the anim id, whether its name was
    /// <paramref name="swapped"/>, and the travel along the facing's forward (<paramref name="fwd"/>) and right
    /// (<paramref name="side"/>): a swapped forward diagonal faces its travel, a walk-back drifting sideways past
    /// <see cref="BackSide"/> points its back along it, everything else straight.</summary>
    internal static float Target(int anim, bool swapped, float fwd, float side) => anim switch
    {
        WalkFl or RunFl when swapped => Quarter,
        WalkFr or RunFr when swapped => -Quarter,
        WalkBack when -fwd > MinBack && MathF.Abs(side) > BackSide * -fwd => MathF.CopySign(Quarter, side),
        _ => 0f,
    };

    /// <summary>The hips' turn off the facing once the facing has turned by <paramref name="step"/> radians (positive =
    /// left, any winding): they keep their world heading, never more than <see cref="MaxTwist"/> off the facing.</summary>
    internal static float Lag(float turn, float step) =>
        Math.Clamp(turn - MathF.IEEERemainder(step, 2f * MathF.PI), -MaxTwist, MaxTwist);

    /// <summary>The share of a facing step of <paramref name="step"/> radians over <paramref name="dt"/> seconds that a
    /// body's legs go round with directly: up to <see cref="WalkTurn"/> while it is <paramref name="moving"/>, none standing.</summary>
    internal static float Walked(float step, bool moving, float dt) => moving ? Math.Clamp(step, -WalkTurn * dt, WalkTurn * dt) : 0f;

    /// <summary>True for a turn on the spot: the foot lock holds a planted foot through it while the hips pivot.</summary>
    internal static bool OnTheSpot(int anim) => anim is >= TurnFirst and <= TurnLast;

    /// <summary>One frame of the turn's spring toward <paramref name="target"/>; a <paramref name="snap"/> (teleport,
    /// first sight) lands on it.</summary>
    internal static void Ease(ref float turn, ref float v, float target, bool snap, float dt)
    {
        if (snap) { turn = target; v = 0f; return; }
        Smooth(ref turn, ref v, target, dt, TurnTime);
    }

    /// <summary>This frame's hip turn for a body playing <paramref name="anim"/>, facing <paramref name="fwdH"/> /
    /// <paramref name="right"/>; true when there is one to write (<see cref="Write"/>). With <c>anim.pivot</c> the
    /// change of facing since the last <paramref name="tracked"/> frame, less what a moving body walks round
    /// (<see cref="Walked"/>), is left on the hips (<see cref="Lag"/>); the legs' share is <see cref="PoseService.Slot.Walked"/>, for the foot lock.</summary>
    internal static bool Step(ref PoseService.Slot s, int anim, Vector3 fwdH, Vector3 right, bool snap, bool tracked, float dt)
    {
        float target = AnimKnobs.Diagonal.V && _name != null && s.Hips
            ? Target(anim, s.Swapped, s.Vel.X * fwdH.X + s.Vel.Y * fwdH.Y, s.Vel.X * right.X + s.Vel.Y * right.Y) : 0f;
        float heading = MathF.Atan2(fwdH.Y, fwdH.X);
        float step = tracked && !snap ? MathF.IEEERemainder(heading - s.Heading, 2f * MathF.PI) : 0f;
        bool moving = PoseService.IsLocomotion(anim);
        float walked = Walked(step, moving, dt);
        if (AnimKnobs.Pivot.V && s.Hips && step != 0f && Transition.Movement(anim)) s.Turn = Lag(s.Turn, step - walked);
        else walked = moving ? step : 0f;
        s.Walked = walked;
        s.Heading = heading;
        Ease(ref s.Turn, ref s.TurnV, target, snap, dt);
        s.Pivot = Pivoting(s.Turn, target);
        return MathF.Abs(s.Turn) > MinTurn;
    }

    /// <summary>Turns the hips by <paramref name="turn"/> about the vertical and the chest by <see cref="UpperShare"/>
    /// of it, each about its own pivot and over whatever the animation posed.</summary>
    internal static void Write(in PoseService.Bones b, float turn)
    {
        Yaw(b.Pelvis, turn);
        Yaw(b.Torso, UpperShare * turn);
    }

    private static void Yaw(IntPtr part, float angle)
    {
        IntPtr parent = part == IntPtr.Zero ? IntPtr.Zero : PoseRig.Parent(part);
        if (parent == IntPtr.Zero) return;
        World(parent, out _, out Quaternion qp);
        World(part, out _, out Quaternion q);
        SetLocal(part, Quaternion.Conjugate(qp) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle) * q);
    }

    /// <summary>The swap, the turn's direction for every diagonal the engine drives (none for a diagonal left
    /// unswapped), and the ease.</summary>
    internal static bool SelfCheck()
    {
        const ushort sideL = 0x4e, sideR = 0x4f; // not-an-offset: the pure sidesteps, which stay
        bool swap = Straight(WalkFl) == Walk && Straight(WalkFr) == Walk && Straight(RunFl) == Run && Straight(RunFr) == Run
                    && Straight(Walk) == Walk && Straight(WalkBack) == WalkBack && Straight(Run) == Run
                    && Straight(sideL) == sideL && Straight(sideR) == sideR;

        // model frame: +Y forward, +X right; the engine's diagonals are forward (or back) plus the perpendicular, normalized
        float d = MathF.Sqrt(0.5f), walk = 2f * d;
        Vector2 fl = new(-d, d), fr = new(d, d), bl = new(-d, -d), br = new(d, -d);
        bool faces = Leads(Target(WalkFl, true, walk, -walk), Vector2.UnitY, fl) && Leads(Target(RunFl, true, walk, -walk), Vector2.UnitY, fl)
                     && Leads(Target(WalkFr, true, walk, walk), Vector2.UnitY, fr) && Leads(Target(RunFr, true, walk, walk), Vector2.UnitY, fr);
        bool backs = Leads(Target(WalkBack, false, -walk, -walk), -Vector2.UnitY, bl) && Leads(Target(WalkBack, false, -walk, walk), -Vector2.UnitY, br);
        bool still = Target(WalkBack, false, -2f, 0.1f) == 0f && Target(WalkBack, false, 0f, 0f) == 0f && Target(WalkBack, false, -0.05f, 0.05f) == 0f
                     && Target(Walk, true, 1.4f, -1.4f) == 0f && Target(sideL, true, 0f, -1.6f) == 0f && Target(0, true, 0f, 0f) == 0f
                     && Target(WalkFl, false, walk, -walk) == 0f && Target(RunFr, false, walk, walk) == 0f;

        float x = 0f, v = 0f, t = Quarter, first = 0f, peak = 0f, stance = 0f;
        bool monotonic = true;
        for (int i = 0; i < 60; i++)
        {
            float before = x;
            Ease(ref x, ref v, t, false, 1f / 60f);
            if (i == 0) first = x;
            if (i == 14) stance = x;
            if (x < before) monotonic = false;
            peak = MathF.Max(peak, x);
        }
        float held = 0f, hv = 0f;
        Ease(ref held, ref hv, t, true, 1f / 60f);
        bool eases = first > 0f && first < 0.25f * t && stance >= 0.9f * t && monotonic && peak <= t + 1e-4f
                     && t - x < MathF.PI / 180f && held == t && Pivoting(0f, t) && !Pivoting(x, t);
        bool played = Played(WalkFl, true) == Walk && Played(RunFr, true) == Run && Played(WalkFl, false) == WalkFl
                      && Played(WalkBack, true) == WalkBack && Played(sideL, true) == sideL;
        return swap && faces && backs && still && eases && played;
    }

    /// <summary>A heading change: the hips keep their world heading as the facing turns, across the ±180° seam and never
    /// past <see cref="MaxTwist"/>, then come round inside a quarter second without overshoot; a steady bend trails by
    /// its rate times <see cref="TurnTime"/>, not a growing lag; only the turns on the spot join the foot lock. A moving
    /// body walks a bend with its legs and pivots only the part of a snap it cannot walk.</summary>
    internal static bool LagCheck()
    {
        float step = MathF.PI / 6f, deg = MathF.PI / 180f, dt = 1f / 60f;
        bool keeps = MathF.Abs(Lag(0f, step) + step) < 1e-5f && MathF.Abs(Lag(0f, -340f * deg) + 20f * deg) < 1e-4f
                     && MathF.Abs(Lag(0f, MathF.PI)) == MaxTwist && Lag(Quarter, -step) == MaxTwist
                     && MathF.Abs(Lag(Quarter, step) - (Quarter - step)) < 1e-5f;

        float x = Lag(0f, step), v = 0f, stance = 0f;
        bool starts = Pivoting(x, 0f), monotonic = true;
        for (int i = 0; i < 60; i++)
        {
            float before = x;
            Ease(ref x, ref v, 0f, false, dt);
            if (i == 14) stance = x;
            if (x < before || x > 1e-4f) monotonic = false;
        }
        bool settles = starts && monotonic && MathF.Abs(stance) <= 0.1f * step && !Pivoting(x, 0f);

        float rate = 1.5f, b = 0f, bv = 0f;
        for (int i = 0; i < 120; i++)
        {
            b = Lag(b, rate * dt);
            Ease(ref b, ref bv, 0f, false, dt);
        }
        bool bends = MathF.Abs(b + rate * TurnTime) < 0.3f * rate * TurnTime;
        bool spot = OnTheSpot(0x50) && OnTheSpot(0x53) && !OnTheSpot(0) && !OnTheSpot(Walk) && !OnTheSpot(0x54);

        // walking a 1.5 rad/s bend leaves nothing on the hips; a 120° snap as a drive sets off leaves all but one
        // frame's walkable turn, which the twist cap then holds to its limit; standing leaves every step
        float w = 0f, wv = 0f;
        for (int i = 0; i < 120; i++)
        {
            float st = rate * dt;
            w = Lag(w, st - Walked(st, true, dt));
            Ease(ref w, ref wv, 0f, false, dt);
        }
        float snapTurn = 120f * deg;
        bool walks = MathF.Abs(w) < 1e-5f && Lag(0f, snapTurn - Walked(snapTurn, true, dt)) == -MaxTwist
                     && Walked(step, false, dt) == 0f && Walked(0.01f, true, dt) == 0.01f;
        return keeps && settles && bends && spot && walks;
    }

    /// <summary>True when the body axis <paramref name="axis"/> turned by <paramref name="turn"/> points along
    /// <paramref name="travel"/> (within a degree).</summary>
    private static bool Leads(float turn, Vector2 axis, Vector2 travel)
    {
        Vector3 a = Vector3.Transform(new Vector3(axis, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, turn));
        return Vector2.Dot(new Vector2(a.X, a.Y), travel) > MathF.Cos(MathF.PI / 180f);
    }
}
