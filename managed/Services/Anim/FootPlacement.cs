using System;
using System.Numerics;
using CX.Engine;
using CX.Services.Camera;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Foot placement on uneven floors, a <see cref="PoseService"/> module. Each foot casts one segment down through
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
    /// <summary>Ankle moves below this are no move: the IK is skipped.</summary>
    internal const float MinLift = 1e-4f;
    /// <summary>World units (times the gob's scale) from a foothold to the cast that measures its slope: about
    /// half a sole, so a stair riser is past it rather than read as a slope.</summary>
    private const float ProbeReach = 0.1f;
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

    /// <summary>Casts, eases and weighs this frame's footholds into <paramref name="feet"/>. With the knob off or
    /// the animation gated the targets are 0, so a correction in place eases out rather than popping.
    /// <paramref name="refZ"/> is the rendered height -- the root part's world position less its own local offset,
    /// i.e. <c>Gob::GetAdjustedPosition</c> including a script's <c>SetObjectVisualTransform</c> translation.</summary>
    internal static void Compute(ref PoseService.Slot s, IntPtr scene, IntPtr gob, Vector3 rAnkle, Vector3 lAnkle,
                                 float refZ, Vector3 fwdH, Vector3 right, float scale, float weight, bool gated,
                                 bool snap, float dt, out Feet feet)
    {
        bool on = AnimKnobs.FootIk.V && !gated;
        float max = AnimKnobs.Max.V;
        float tr = 0f, tl = 0f;
        bool hr = false, hl = false;
        if (on)
        {
            hr = Floor(ref s.R, scene, gob, rAnkle, refZ, max, scale, snap, dt, out tr);
            hl = Floor(ref s.L, scene, gob, lAnkle, refZ, max, scale, snap, dt, out tl);
            // a foot with no floor in range borrows the other foot's: "none" is not "at the walkmesh"
            if (!hr && hl) tr = tl; else if (hr && !hl) tl = tr;
            tr = Ahead(ref s.NextR, s.AheadR, scene, gob, refZ, max, scale, snap, dt, tr);
            tl = Ahead(ref s.NextL, s.AheadL, scene, gob, refZ, max, scale, snap, dt, tl);
        }
        // a walk whose feet are planted and landed ahead of time stands each on its own stair
        if (s.PlantClip != null) weight = 1f;

        float maxSlope = MathF.Tan(AnimKnobs.Tilt.V * (MathF.PI / 180f));
        float reach = ProbeReach * scale;
        float tfr = s.FR, tsr = s.SR, tfl = s.FL, tsl = s.SL;
        if (on && maxSlope > 1e-4f)
            switch (s.Phase++ & 3)
            {
                // a slope is re-measured only after its foot's floor was (a foot that has not moved stands on
                // the slope it stood on)
                case 0: if (s.R.Slopes > 0) { s.R.Slopes--; tfr = Slope(scene, gob, rAnkle, fwdH, hr, refZ + tr, reach, maxSlope); } break;
                case 1: if (s.R.Slopes > 0) { s.R.Slopes--; tsr = Slope(scene, gob, rAnkle, right, hr, refZ + tr, reach, maxSlope); } break;
                case 2: if (s.L.Slopes > 0) { s.L.Slopes--; tfl = Slope(scene, gob, lAnkle, fwdH, hl, refZ + tl, reach, maxSlope); } break;
                default: if (s.L.Slopes > 0) { s.L.Slopes--; tsl = Slope(scene, gob, lAnkle, right, hl, refZ + tl, reach, maxSlope); } break;
            }
        else tfr = tsr = tfl = tsl = 0f;

        if (weight < 1f) Stairs++;
        if (snap)
        {
            s.W = weight; s.VW = 0f;
            s.OffR = tr; s.OffL = tl; s.FR = tfr; s.SR = tsr; s.FL = tfl; s.SL = tsl;
            s.VelR = s.VelL = s.VFR = s.VSR = s.VFL = s.VSL = 0f;
        }
        else
        {
            Smooth(ref s.OffR, ref s.VelR, tr, dt, SmoothTime); Smooth(ref s.OffL, ref s.VelL, tl, dt, SmoothTime);
            Smooth(ref s.FR, ref s.VFR, tfr, dt, SmoothTime); Smooth(ref s.SR, ref s.VSR, tsr, dt, SmoothTime);
            Smooth(ref s.FL, ref s.VFL, tfl, dt, SmoothTime); Smooth(ref s.SL, ref s.VSL, tsl, dt, SmoothTime);
            Smooth(ref s.W, ref s.VW, weight, dt, FadeTime);
        }

        float w = Math.Clamp(s.W, 0f, 1f);
        feet.OffR = s.OffR * w;
        feet.OffL = s.OffL * w;
        feet.TiltR = FootTilt(fwdH, right, s.FR * w, s.SR * w);
        feet.TiltL = FootTilt(fwdH, right, s.FL * w, s.SL * w);
    }

    /// <summary>A swinging foot's floor offset <paramref name="under"/> it, raised toward the floor where it will land
    /// (<paramref name="ahead"/>, cast into <paramref name="c"/>) as its swing goes on; a foot on the ground keeps its own.</summary>
    private static float Ahead(ref FootCast c, StrideWarp.Ahead a, IntPtr scene, IntPtr gob, float refZ, float max, float scale,
                               bool snap, float dt, float under)
    {
        if (!a.Swing || !Floor(ref c, scene, gob, new Vector3(a.At.X, a.At.Y, refZ), refZ, max, scale, snap, dt, out float land)) return under;
        return SwingTarget(under, land, a.U);
    }

    /// <summary>The floor a swinging foot aims at, <paramref name="u"/> through its swing: rising early toward the landing
    /// floor <paramref name="land"/> (eased out, three quarters there at mid-swing), never below the floor under it now.</summary>
    internal static float SwingTarget(float under, float land, float u)
    {
        float k = 1f - (1f - Math.Clamp(u, 0f, 1f)) * (1f - Math.Clamp(u, 0f, 1f));
        return MathF.Max(under, under + (land - under) * k);
    }

    /// <summary>World units (times scale) a foot must move, or the reference height change, before its floor
    /// is cast again; and the seconds a cached floor is trusted at most. A character standing still casts
    /// nothing, where every foot used to cast every frame.</summary>
    private const float Recast = 0.03f, RecastHeight = 0.02f, RecastAge = 0.5f;

    /// <summary>One foot's cached floor: where it was cast from, the floor's world height (NaN = none in range),
    /// how old it is, and how many of its two slopes still want re-measuring.</summary>
    internal struct FootCast
    {
        public Vector2 At;
        public float RefZ, FloorZ, Age;
        public bool Valid;
        public int Slopes;
    }

    /// <summary>The floor offset under <paramref name="ankle"/>, from the cache while the foot and the
    /// reference height hold still (<see cref="Recast"/>), else cast.</summary>
    private static bool Floor(ref FootCast c, IntPtr scene, IntPtr gob, Vector3 ankle, float refZ, float max,
                              float scale, bool snap, float dt, out float off)
    {
        c.Age += dt;
        Vector2 at = new(ankle.X, ankle.Y);
        float move = Recast * scale;
        if (snap || !c.Valid || c.Age > RecastAge || Vector2.DistanceSquared(at, c.At) > move * move
            || MathF.Abs(refZ - c.RefZ) > RecastHeight * scale)
        {
            c.FloorZ = FloorOffset(scene, gob, ankle, refZ, max, out float cast) ? refZ + cast : float.NaN;
            c.At = at; c.RefZ = refZ; c.Age = 0f; c.Valid = true; c.Slopes = 2;
        }
        off = float.IsNaN(c.FloorZ) ? 0f : c.FloorZ - refZ;
        return !float.IsNaN(c.FloorZ) && MathF.Abs(off) <= max;
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
