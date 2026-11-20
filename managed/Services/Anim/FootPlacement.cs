using System;
using System.Numerics;
using CX.Engine;
using CX.Services.Camera;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Foot placement on uneven floors, a <see cref="PoseService"/> module. Each foot casts segments down under its sole through
/// <c>Scene::HitCheck</c> (<see cref="SceneRay"/>: tile render triangles, then placeable ones) over
/// <c>[rendered height +- footik_max]</c>; the root part's local Z takes the lower foot's offset, the two-bone IK lifts
/// the other ankle by the difference, and <c>anim.footik_tilt</c> lays each foot on its slope. Casts are cached per foot.
/// </summary>
internal static unsafe class FootPlacement
{
    /// <summary>Casts per foot: the first may land on a creature, a held item or a VFX, which are not ground.</summary>
    private const int MaxCasts = 3;
    /// <summary>Seconds the offset springs take to settle (critically damped).</summary>
    private const float SmoothTime = 0.12f;
    /// <summary>Seconds a swinging foot's offset spring takes: quick enough to be down on a falling slope as it lands.</summary>
    private const float SwingTime = 0.05f;
    /// <summary>Ankle moves below this are no move: the IK is skipped.</summary>
    internal const float MinLift = 1e-4f;
    /// <summary>World units (times the gob's scale) from a foothold to the cast that measures its slope: about
    /// half a sole, so a stair riser is past it rather than read as a slope.</summary>
    private const float ProbeReach = 0.1f;
    /// <summary>World units (times scale) along the foot's forward from the ankle to the toe-ward and the heel-ward sole
    /// samples: the toe is one probe reach out (the sole's far half), the heel half that back, the sole being longer ahead
    /// of the ankle than behind.</summary>
    private const float ToeReach = ProbeReach, HeelReach = -0.5f * ProbeReach;
    /// <summary>Walkmesh rise per unit of travel past which a MOVING character is on stairs (or a ramp that
    /// steep): foot placement drops to <see cref="StairWeight"/> there. The walk cycle is wide and runs at full
    /// speed up a steep flight, so no foothold per frame matches it.</summary>
    private const float StairGrade = 0.2f;
    /// <summary>Foot placement's strength while moving on stairs with no planted walk to land each foot ahead: halfway
    /// between vanilla's feet-on-the-ramp and full placement (a flickering full fade averaged to it).</summary>
    internal const float StairWeight = 0.5f;
    /// <summary>World units per second (times scale) above which a character is moving, not shuffling.</summary>
    internal const float MoveSpeed = 0.5f;
    /// <summary>Seconds a character still counts as moving after a frame without travel: the engine does not
    /// advance a walking gob's position every frame, and a gate that trusts one frame flickers.</summary>
    private const float MoveHold = 0.2f;
    /// <summary>Seconds of travel the measured grade is averaged over: one frame's step is noise.</summary>
    private const float GradeTime = 0.2f;
    /// <summary>Seconds the stairs fade takes, out and back in.</summary>
    private const float FadeTime = 0.25f;

    internal static int Rays, Tile, Placeable, Stairs;

    /// <summary>This frame's placement: each foot's floor offset from the reference height, and the world
    /// rotation laying it on its slope.</summary>
    internal struct Feet
    {
        public float OffR, OffL;
        public Quaternion TiltR, TiltL;
    }

    /// <summary>Casts this frame's floor samples into <paramref name="f"/>: under each drawn foot, under where a swinging one
    /// lands, and one of the four slopes in turn, each only once its foot's floor was re-cast. Engine side; casts are cached
    /// per foot. Nothing with foot placement off or the animation gated.</summary>
    internal static void Cast(ref PoseService.Slot s, IntPtr scene, IntPtr gob, ref LegFrame f, in LegPose p)
    {
        if (!Legs.Placing(f)) return;
        float refZ = f.Origin.Z, max = AnimKnobs.Max.V, scale = f.Scale;
        float tol = FootFloor.Agree * scale;
        Sole(ref s.R, ref f.FloorR, scene, gob, p.FootR, p.LegFwdH, p.AheadR.Swing, refZ, max, scale, tol, f.Snap, f.Dt);
        Sole(ref s.L, ref f.FloorL, scene, gob, p.FootL, p.LegFwdH, p.AheadL.Swing, refZ, max, scale, tol, f.Snap, f.Dt);
        f.FloorR.Land = Landing(ref s.NextR, p.AheadR, scene, gob, refZ, max, scale, f.Snap, f.Dt);
        f.FloorL.Land = Landing(ref s.NextL, p.AheadL, scene, gob, refZ, max, scale, f.Snap, f.Dt);
        if (!Legs.Tilting(f)) return;
        Legs.Targets(f, p, out bool hr, out float tr, out bool hl, out float tl);
        float maxSlope = f.Knobs.MaxSlope, reach = ProbeReach * scale;
        switch (s.Phase++ & 3)
        {
            // a slope is re-measured only after its foot's floor was (a foot that has not moved stands on
            // the slope it stood on)
            case 0: if (s.R.Slopes > 0) { s.R.Slopes--; f.FloorR.NewF = true; f.FloorR.SlopeF = Slope(scene, gob, p.FootR, p.LegFwdH, hr, refZ + tr, reach, maxSlope); } break;
            case 1: if (s.R.Slopes > 0) { s.R.Slopes--; f.FloorR.NewS = true; f.FloorR.SlopeS = Slope(scene, gob, p.FootR, p.LegRight, hr, refZ + tr, reach, maxSlope); } break;
            case 2: if (s.L.Slopes > 0) { s.L.Slopes--; f.FloorL.NewF = true; f.FloorL.SlopeF = Slope(scene, gob, p.FootL, p.LegFwdH, hl, refZ + tl, reach, maxSlope); } break;
            default: if (s.L.Slopes > 0) { s.L.Slopes--; f.FloorL.NewS = true; f.FloorL.SlopeS = Slope(scene, gob, p.FootL, p.LegRight, hl, refZ + tl, reach, maxSlope); } break;
        }
    }

    /// <summary>Eases and weighs this frame's footholds into <paramref name="feet"/>, from the floors in <paramref name="f"/>.
    /// With placement off or the animation gated the targets are 0, so a correction in place eases out rather than popping.
    /// The offsets are from the rendered height, the gob's adjusted position including a visual transform's translation.</summary>
    internal static void Ease(ref LegState s, in LegFrame f, in LegPose p, out Feet feet)
    {
        Legs.Targets(f, p, out _, out float tr, out _, out float tl);
        // a walk whose feet are planted and landed ahead of time stands each on its own stair
        float weight = p.Landing ? 1f : f.Weight;
        float tfr = s.FR, tsr = s.SR, tfl = s.FL, tsl = s.SL;
        if (Legs.Tilting(f))
        {
            if (f.FloorR.NewF) tfr = f.FloorR.SlopeF;
            if (f.FloorR.NewS) tsr = f.FloorR.SlopeS;
            if (f.FloorL.NewF) tfl = f.FloorL.SlopeF;
            if (f.FloorL.NewS) tsl = f.FloorL.SlopeS;
        }
        else tfr = tsr = tfl = tsl = 0f;

        if (weight < 1f) Stairs++;
        float dt = f.Dt;
        if (f.Snap)
        {
            s.W = weight; s.VW = 0f;
            s.OffR = tr; s.OffL = tl; s.FR = tfr; s.SR = tsr; s.FL = tfl; s.SL = tsl;
            s.VelR = s.VelL = s.VFR = s.VSR = s.VFL = s.VSL = 0f;
        }
        else
        {
            // the offsets ease in the world: the reference height's own rise this frame is no move of a foot's floor
            if (Legs.Placing(f)) { s.OffR -= f.Rise; s.OffL -= f.Rise; }
            Smooth(ref s.OffR, ref s.VelR, tr, dt, p.AheadR.Swing ? SwingTime : SmoothTime);
            Smooth(ref s.OffL, ref s.VelL, tl, dt, p.AheadL.Swing ? SwingTime : SmoothTime);
            Smooth(ref s.FR, ref s.VFR, tfr, dt, SmoothTime); Smooth(ref s.SR, ref s.VSR, tsr, dt, SmoothTime);
            Smooth(ref s.FL, ref s.VFL, tfl, dt, SmoothTime); Smooth(ref s.SL, ref s.VSL, tsl, dt, SmoothTime);
            Smooth(ref s.W, ref s.VW, weight, dt, FadeTime);
        }

        float w = Math.Clamp(s.W, 0f, 1f);
        feet.OffR = s.OffR * w;
        feet.OffL = s.OffL * w;
        feet.TiltR = FootTilt(p.LegFwdH, p.LegRight, s.FR * w, s.SR * w);
        feet.TiltL = FootTilt(p.LegFwdH, p.LegRight, s.FL * w, s.SL * w);
    }

    /// <summary>The floor under where a swinging foot lands (<paramref name="next"/>, cast into <paramref name="cast"/>);
    /// none for a foot on the ground.</summary>
    private static FloorSample Landing(ref FootCast cast, StrideWarp.Ahead next, IntPtr scene, IntPtr gob, float refZ, float max,
                                       float scale, bool snap, float dt)
    {
        if (!next.Swing) return default;
        bool hit = Floor(ref cast, scene, gob, new Vector3(next.At.X, next.At.Y, refZ), refZ, max, scale, snap, dt, out float land);
        return new FloorSample(hit, land);
    }

    /// <summary>The floor a swinging foot aims at, <paramref name="u"/> through its swing: up to a higher landing floor
    /// <paramref name="land"/> early (three quarters there at mid-swing), down to a lower one only in the second half,
    /// when the foot is past the edge it leaves.</summary>
    internal static float SwingTarget(float under, float land, float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        if (land >= under) return under + (land - under) * (1f - (1f - u) * (1f - u));
        float w = Math.Clamp(2f * u - 1f, 0f, 1f);
        return under + (land - under) * w * w * (3f - 2f * w);
    }

    /// <summary>World units (times scale) a foot must move, or the reference height change, before its floor
    /// is cast again; and the seconds a cached floor is trusted at most. A character standing still casts
    /// nothing.</summary>
    private const float Recast = 0.03f, RecastHeight = 0.02f, RecastAge = 0.5f;

    /// <summary>One foot's cached floor: where it was cast from, the floors' world heights (NaN = none in range),
    /// how old it is, and how many of its two slopes still want re-measuring.</summary>
    internal struct FootCast
    {
        public Vector2 At;
        public float RefZ, FloorZ, ToeZ, HeelZ, Age;
        public bool Valid;
        /// <summary>Slopes still to re-measure, and how many of the sole's samples (under the ankle, toe-ward, heel-ward) the cache holds.</summary>
        public int Slopes, Count;
    }

    /// <summary>Whether the cache for a foot at <paramref name="ankle"/> is out of date (<see cref="Recast"/>, or <paramref name="force"/>); when it is, it
    /// is restamped for the cast the caller now makes.</summary>
    private static bool Stale(ref FootCast c, Vector3 ankle, float refZ, float scale, bool snap, float dt, bool force = false)
    {
        c.Age += dt;
        Vector2 at = new(ankle.X, ankle.Y);
        float move = Recast * scale;
        if (!(force || snap || !c.Valid || c.Age > RecastAge || Vector2.DistanceSquared(at, c.At) > move * move
              || MathF.Abs(refZ - c.RefZ) > RecastHeight * scale)) return false;
        c.At = at; c.RefZ = refZ; c.Age = 0f; c.Valid = true; c.Slopes = 2;
        return true;
    }

    /// <summary>One cached floor height as a sample off <paramref name="refZ"/>; NaN is no floor in range.</summary>
    private static FloorSample Sample(float z, float refZ, float max)
    {
        float off = float.IsNaN(z) ? 0f : z - refZ;
        return new FloorSample(!float.IsNaN(z) && MathF.Abs(off) <= max, off);
    }

    private static float CastZ(IntPtr scene, IntPtr gob, Vector3 at, float refZ, float max)
        => FloorOffset(scene, gob, at, refZ, max, out float cast) ? refZ + cast : float.NaN;

    /// <summary>The floor under a landing <paramref name="ankle"/>, from the cache while the foot and the reference height
    /// hold still (<see cref="Recast"/>), else cast: one ray down, no sole.</summary>
    private static bool Floor(ref FootCast c, IntPtr scene, IntPtr gob, Vector3 ankle, float refZ, float max,
                              float scale, bool snap, float dt, out float off)
    {
        if (Stale(ref c, ankle, refZ, scale, snap, dt)) c.FloorZ = CastZ(scene, gob, ankle, refZ, max);
        FloorSample s = Sample(c.FloorZ, refZ, max);
        off = s.Off;
        return s.Hit;
    }

    /// <summary>Adds the floor samples under a drawn foot to <paramref name="floor"/>: under the ankle and toe-ward
    /// (<see cref="ToeReach"/>), and heel-ward (<see cref="HeelReach"/>) only when those two disagree by <paramref name="tol"/>.
    /// A swinging foot takes the ankle alone: it is on no floor and its landing is cast apart. Cached with the foot.</summary>
    private static void Sole(ref FootCast c, ref FootFloor floor, IntPtr scene, IntPtr gob, Vector3 ankle, Vector3 fwd,
                             bool swinging, float refZ, float max, float scale, float tol, bool snap, float dt)
    {
        if (Stale(ref c, ankle, refZ, scale, snap, dt, force: !swinging && c.Count == 1))
        {
            c.FloorZ = CastZ(scene, gob, ankle, refZ, max);
            c.Count = 1;
            if (!swinging)
            {
                c.ToeZ = CastZ(scene, gob, ankle + fwd * (ToeReach * scale), refZ, max);
                c.Count = 2;
                if (FootFloor.Disagree(Sample(c.FloorZ, refZ, max), Sample(c.ToeZ, refZ, max), tol))
                {
                    c.HeelZ = CastZ(scene, gob, ankle + fwd * (HeelReach * scale), refZ, max);
                    c.Count = 3;
                }
            }
        }
        floor.Add(Sample(c.FloorZ, refZ, max));
        if (c.Count > 1) floor.Add(Sample(c.ToeZ, refZ, max));
        if (c.Count > 2) floor.Add(Sample(c.HeelZ, refZ, max));
    }

    /// <summary>The weight foot placement should have: <see cref="StairWeight"/> while the character moves
    /// (<see cref="MoveSpeed"/>, held <see cref="MoveHold"/> across frames without travel) along a walkmesh steeper
    /// than <see cref="StairGrade"/>, else 1. <paramref name="grade"/> averages the rise per unit of travel over
    /// <see cref="GradeTime"/>; <paramref name="moving"/> is the hold timer every module reads as "moving".</summary>
    internal static float Weight(ref float grade, ref float moving, Vector3 moved, float dt, float scale)
    {
        if (!(dt > 0f)) return 1f;
        float run = MathF.Sqrt(moved.X * moved.X + moved.Y * moved.Y);
        if (run >= MoveSpeed * scale * dt)
        {
            moving = MoveHold;
            grade += (MathF.Abs(moved.Z) / run - grade) * MathF.Min(1f, dt / GradeTime);
        }
        else moving -= dt;
        return moving > 0f && grade > StairGrade ? StairWeight : 1f;
    }

    /// <summary>The floor's rise per unit from a foothold at <paramref name="z"/> toward <paramref name="dir"/>,
    /// clamped to <paramref name="maxSlope"/>: one cast <paramref name="reach"/> away, since in triangle mode
    /// <c>PartTriMesh::HitCheckGeom</c> clears the hit normal (<c>CHitInfo+0x5c</c> = 0) and the engine hands back none.
    /// 0 (flat) when the foot has no foothold of its own or the cast finds no floor in range -- a stair edge, a drop.</summary>
    private static float Slope(IntPtr scene, IntPtr gob, Vector3 ankle, Vector3 dir, bool hit, float z, float reach, float maxSlope)
    {
        if (!hit) return 0f;
        Vector3 at = ankle + dir * reach;
        return FloorOffset(scene, gob, at, z, reach * maxSlope, out float rise) ? Math.Clamp(rise / reach, -maxSlope, maxSlope) : 0f;
    }

    /// <summary>The world rotation laying a foot flat on a floor rising <paramref name="f"/> per unit toward
    /// <paramref name="fwd"/> and <paramref name="s"/> toward <paramref name="right"/> (both horizontal).</summary>
    internal static Quaternion FootTilt(Vector3 fwd, Vector3 right, float f, float s)
        => MathF.Abs(f) + MathF.Abs(s) < 1e-5f ? Quaternion.Identity : Arc(Vector3.UnitZ, Vector3.UnitZ - f * fwd - s * right);

    /// <summary>The floor under <paramref name="ankle"/> within <c>refZ +- max</c>, as an offset from
    /// <paramref name="refZ"/>; 0 when there is none. Tiles and static placeables (BSP parts, counted as tile
    /// hits), tile gobs and placeables are ground; anything else hit -- a creature, a held item, a VFX -- is
    /// cast past.</summary>
    private static bool FloorOffset(IntPtr scene, IntPtr self, Vector3 ankle, float refZ, float max, out float off)
    {
        Vector3 from = new(ankle.X, ankle.Y, refZ + max), to = new(ankle.X, ankle.Y, refZ - max);
        off = 0f;
        for (int i = 0; i < MaxCasts && from.Z > to.Z; i++)
        {
            Rays++;
            if (!SceneRay.Cast(scene, from, to, self, out Vector3 hit, out IntPtr g, statics: true)) break;
            if (g == IntPtr.Zero || SceneRay.IsTile(g)) { Tile++; off = hit.Z - refZ; SceneRay.Release(); return true; }
            if (SceneRay.IsPlaceable(g)) { Placeable++; off = hit.Z - refZ; SceneRay.Release(); return true; }
            from.Z = hit.Z - SceneRay.Nudge;
        }
        SceneRay.Release();
        return false;
    }
}
