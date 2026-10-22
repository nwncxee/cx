using System;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Services.Anim;

/// <summary>
/// Stride warping, a <see cref="PoseService"/> module under <c>anim.stride</c>: a body covering ground faster or slower
/// than its walk cycle does takes quicker AND longer (or slower and shorter) steps instead of playing the cycle faster,
/// and each foot stays where it landed until the clip lifts it. The contacts and the pace come from the clip's own keys
/// (<see cref="GaitClip"/>), so nothing here guesses from a measured height; a clip that is not a walk is left alone.
/// </summary>
internal static class StrideWarp
{
    /// <summary>The range the cycle's playback rate takes of a pace change before the stride length takes the rest.</summary>
    internal const float RateMin = 0.75f, RateMax = 1.35f;
    /// <summary>The range the stride length takes; past both, the rate takes what is left, so the feet never slide.</summary>
    internal const float StrideMin = 0.7f, StrideMax = 1.4f;
    /// <summary>The widest stride ratio drawn from a rate and speed that disagree for a frame (a pace still catching up).</summary>
    internal const float WarpMin = 0.6f, WarpMax = 1.6f;

    /// <summary>The share of a leg's length a planted foot is held against the warped animation before it is dragged.</summary>
    private const float HoldShare = 0.25f;

    /// <summary>What the module did this period: bodies warped, feet planted, plants dragged past their hold, the largest
    /// offset a foot lifted off with (what the warp left for the swing to take up) and the deepest pelvis drop, world units.</summary>
    private sealed class StrideWarpState
    {
        public int Warped, Planted, Dragged;
        public float Leftover, MaxDrop;
    }

    private static readonly StrideWarpState _s = new();

    /// <summary>Bodies warped and feet planted since the last ask, per frame over <paramref name="frames"/>; resets.</summary>
    internal static string Period(int frames)
    {
        (int walks, int others) = GaitClips.Count;
        string line = $"{_s.Warped / (float)frames:F1} stride-warped + {_s.Planted / (float)frames:F1} feet planted/f, "
                      + $"{_s.Dragged} plant frames dragged, largest lift-off offset {_s.Leftover:F3}, deepest pelvis drop {_s.MaxDrop:F3} "
                      + $"({walks} walk clips, {others} not walks)";
        _s.Warped = _s.Planted = _s.Dragged = 0;
        _s.Leftover = _s.MaxDrop = 0f;
        return line;
    }

    /// <summary>One frame for a body, after the animation is posed: the world offset each ankle is drawn at (the stride
    /// warp and the plant), the pelvis drop the longer stride needs, and the rate multiplier the next frame plays its runs
    /// at (<see cref="PoseService.Slot.RateNext"/>). Every walk run on the body counts by its weight, so a crossfade into or
    /// out of one carries the correction in and out with it. <paramref name="paced"/>: the body is locomoting now.</summary>
    internal static void Step(ref PoseService.Slot s, IntPtr gob, in PoseService.Bones bones, Quaternion legs, Vector3 origin,
                              float scale, float ground, bool paced, bool off, float dt, out Vector3 dR, out Vector3 dL, out float drop)
    {
        float need = Warps(ref s, gob, bones, legs, origin, scale, ground, paced, off, out dR, out dL, out float leg);
        if (off || !(dt > 0f)) { s.DropHold = s.Drop = s.DropV = 0f; drop = 0f; return; }
        drop = Settled(ref s.DropHold, ref s.Drop, ref s.DropV, need, leg, dt);
        if (drop > _s.MaxDrop) _s.MaxDrop = drop;
    }

    /// <summary>One frame of the pelvis drop toward this frame's <paramref name="need"/>: held at its peak, let back up at
    /// <see cref="DropFall"/>, and eased over <see cref="DropTime"/>.</summary>
    internal static float Settled(ref float hold, ref float x, ref float v, float need, float leg, float dt)
    {
        hold = MathF.Max(need, hold - DropFall * leg * dt);
        PoseMath.Smooth(ref x, ref v, hold, dt, DropTime);
        return MathF.Max(x, 0f);
    }

    /// <summary>Share of a leg's length per second a held pelvis drop rises back by once the stride stops needing it:
    /// slow enough that a steady gait's two reach peaks a cycle read as one lowered posture, not a bob.</summary>
    private const float DropFall = 0.05f;
    /// <summary>Seconds the pelvis eases toward its held drop over.</summary>
    private const float DropTime = 0.3f;

    /// <summary>The warp and plant offsets of <see cref="Step"/>, and the pelvis drop this frame's warped stride needs
    /// (<paramref name="leg"/>: the leg length it is measured in, world units).</summary>
    private static float Warps(ref PoseService.Slot s, IntPtr gob, in PoseService.Bones bones, Quaternion legs, Vector3 origin,
                               float scale, float ground, bool paced, bool off, out Vector3 dR, out Vector3 dL, out float leg)
    {
        dR = dL = Vector3.Zero;
        leg = 0f;
        s.RateNext = 1f;
        s.Touchdown = s.Unvoiced = false;
        if (off || !AnimKnobs.Stride.V || !PoseRig.Db || s.SettleStage != Settle.None) { Reset(ref s); return 0f; }
        float speed = ground / scale, weight = 0f, strides = 0f, top = -1f;
        Vector3 dir = Vector3.Zero;
        GaitClip? lead = null;
        AnimDef? leadAnim = null;
        RunRef leadRun = default;
        int n = PoseRig.RunCount(gob);
        for (int i = 0; i < n; i++)
        {
            RunRef r = PoseRig.Run(gob, i);
            if (r.IsNull || r.Clip is not { } anim) continue;
            GaitClip? c = GaitClips.For(anim, bones, r.PosScale, paced);
            float w = Math.Clamp(r.Blend, 0f, 1f);
            if (c is not { Walk: true } || !(w > 0f)) continue;
            float rate = r.Rate;
            weight += w;
            strides += w * (paced && rate > 1e-4f ? Math.Clamp(speed / c.Speed / rate, WarpMin, WarpMax) : 1f);
            dir += w * c.Dir;
            if (w > top) { top = w; lead = c; leadAnim = anim; leadRun = r; }
        }
        if (lead == null || weight < 1e-3f) { Reset(ref s); return 0f; }
        float stride = strides / weight;
        weight = MathF.Min(weight, 1f);
        dir = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : lead.Dir;
        if (paced)
        {
            float engine = Stride.EngineRate(s, leadRun);
            if (engine > 1e-4f) s.RateNext = Pace(speed / lead.Speed).Rate / engine;
        }
        float phase = leadRun.Time / lead.Length;
        if (!ReferenceEquals(leadAnim, s.PlantClip))
        {
            Rebase(ref s.AnchorR, lead.Swing(true, phase));
            Rebase(ref s.AnchorL, lead.Swing(false, phase));
            s.PlantClip = leadAnim;
        }

        PoseRig.World(bones.RFoot, out Vector3 ra, out _);
        PoseRig.World(bones.LFoot, out Vector3 la, out _);
        PoseRig.World(bones.RThigh, out Vector3 rt, out _);
        PoseRig.World(bones.LThigh, out Vector3 lt, out _);
        Quaternion toModel = Quaternion.Conjugate(legs);
        Vector3 mr = Vector3.Transform(ra - origin, toModel) / scale, ml = Vector3.Transform(la - origin, toModel) / scale;
        Vector3 hip = 0.5f * (Vector3.Transform(rt - origin, toModel) + Vector3.Transform(lt - origin, toModel)) / scale;
        dR = Vector3.Transform(Warp(mr, hip, dir, lead.FloorR, stride, weight) - mr, legs) * scale;
        dL = Vector3.Transform(Warp(ml, hip, dir, lead.FloorL, stride, weight) - ml, legs) * scale;
        leg = lead.LegLength * scale;
        // the stride's own reach, not the plant's: a held foot's drift is the leg IK's to take up, not the pelvis's
        float need = weight * MathF.Max(Drop(rt, ra + dR, leg), Drop(lt, la + dL, leg));
        // a one-shot past its end times nothing: its plants keep their offsets, which fade with the run's weight
        bool timed = lead.Cyclic || phase < 1f;
        dR += new Vector3(weight * Plant(ref s.AnchorR, lead, true, phase, ra + dR, HoldShare * leg, timed, out bool downR), 0f);
        dL += new Vector3(weight * Plant(ref s.AnchorL, lead, false, phase, la + dL, HoldShare * leg, timed, out bool downL), 0f);
        s.Unvoiced = !lead.Authored;
        s.Touchdown = downR || downL;
        _s.Warped++;
        return need;
    }

    /// <summary>One foot's plant at its warped world position <paramref name="p"/>, timed by <paramref name="clip"/> at
    /// <paramref name="phase"/> (held as it is when not <paramref name="timed"/>), counted into the period;
    /// <paramref name="down"/>: it landed this frame out of a swing this plant saw.</summary>
    private static Vector2 Plant(ref Anchor f, GaitClip clip, bool right, float phase, Vector3 p, float hold, bool timed,
                                 out bool down)
    {
        down = false;
        if (!timed) return f.Off;
        bool on = clip.Contact(right, phase), was = f.Planted;
        down = on && !was && f.Aired;
        f.Aired = !on;
        Vector2 off = Hold(ref f, on, clip.Swing(right, phase), new Vector2(p.X, p.Y), hold);
        if (on) _s.Planted++;
        if (f.Dragged) _s.Dragged++;
        if (was && !on) _s.Leftover = MathF.Max(_s.Leftover, f.Off0.Length());
        return off;
    }

    /// <summary>No walk on the body: the plants are let go and the next one starts fresh.</summary>
    private static void Reset(ref PoseService.Slot s)
    {
        s.AnchorR = s.AnchorL = default;
        s.PlantClip = null;
    }

    /// <summary>The playback rate and stride length a clip whose feet cover the ground at <paramref name="k"/> times
    /// the body's speed plays at: the square root of it each, as a walker speeds up by both cadence and stride.</summary>
    internal static (float Rate, float Stride) Pace(float k)
    {
        if (!(k > 0f)) return (1f, 1f);
        float rate = Math.Clamp(MathF.Sqrt(k), RateMin, RateMax);
        float stride = Math.Clamp(k / rate, StrideMin, StrideMax);
        return (k / stride, stride);
    }

    /// <summary>Ankle <paramref name="a"/> (model frame) with its stride stretched by <paramref name="stride"/> about the
    /// hip midpoint <paramref name="hip"/> along <paramref name="dir"/>, its lift off <paramref name="floor"/> by the
    /// stride's square root, all at weight <paramref name="w"/>.</summary>
    internal static Vector3 Warp(Vector3 a, Vector3 hip, Vector3 dir, float floor, float stride, float w)
    {
        float along = (a.X - hip.X) * dir.X + (a.Y - hip.Y) * dir.Y;
        Vector3 reach = new Vector3(dir.X, dir.Y, 0f) * (along * (stride - 1f));
        float lift = (a.Z - floor) * (MathF.Sqrt(stride) - 1f);
        return a + w * new Vector3(reach.X, reach.Y, lift);
    }

    /// <summary>The pelvis drop that lets a leg from hip joint <paramref name="hip"/> reach <paramref name="target"/>
    /// (world, <paramref name="leg"/> long) at <see cref="Reach"/> of its length, capped at <see cref="MaxDrop"/>.</summary>
    internal static float Drop(Vector3 hip, Vector3 target, float leg)
    {
        float reach = Reach * leg, cap = MaxDrop * leg;
        float h2 = (target.X - hip.X) * (target.X - hip.X) + (target.Y - hip.Y) * (target.Y - hip.Y);
        if (h2 >= reach * reach) return cap;
        return Math.Clamp(hip.Z - target.Z - MathF.Sqrt(reach * reach - h2), 0f, cap);
    }

    /// <summary>The share of a leg's length it is stretched to at most.</summary>
    internal const float Reach = 0.97f;
    /// <summary>The share of a leg's length the pelvis drops at most to let a long stride reach.</summary>
    internal const float MaxDrop = 0.12f;

    /// <summary>One foot's plant: held at <see cref="At"/> (world XY) while the clip has it on the ground, the drawn
    /// offset from the warped animation <see cref="Off"/>, and the offset it lifted off with and the swing progress then.</summary>
    internal struct Anchor
    {
        /// <summary>Held this frame; dragged this frame, held further from the warped animation than allowed; and in the
        /// air last frame, so a plant now is a landing.</summary>
        public bool Planted, Dragged, Aired;
        public Vector2 At, Off, Off0;
        public float U0;
    }

    /// <summary>One frame of a foot's plant at warped position <paramref name="p"/>: on the ground it holds where it
    /// landed (dragged along only past <paramref name="maxHold"/>); in the air the offset it lifted off with shrinks
    /// with the swing's own progress <paramref name="u"/>, reaching nothing exactly at landing. The drawn offset.</summary>
    internal static Vector2 Hold(ref Anchor f, bool contact, float u, Vector2 p, float maxHold)
    {
        if (contact)
        {
            if (!f.Planted) { f.Planted = true; f.At = p + f.Off; }
            Vector2 o = f.At - p;
            float len = o.Length();
            f.Dragged = len > maxHold;
            if (f.Dragged) { o *= maxHold / len; f.At = p + o; }
            f.Off = o;
            return o;
        }
        f.Dragged = false;
        if (f.Planted) { f.Planted = false; f.Off0 = f.Off; f.U0 = u; }
        float span = 1f - f.U0;
        // a swing with no progress left to measure keeps its offset: the landing takes it into the plant, not a pop
        f.Off = f.Off0 * (1f - (span > 1e-4f ? Math.Clamp((u - f.U0) / span, 0f, 1f) : 0f));
        return f.Off;
    }

    /// <summary>A swing's progress measured against a different clip from here on: the offset left shrinks from now.</summary>
    internal static void Rebase(ref Anchor f, float u)
    {
        if (!f.Planted) { f.Off0 = f.Off; f.U0 = u; }
    }

    /// <summary>The pace split, the reach, the plant and the drop's easing against hand-worked values, and a walk run through speed
    /// ramps between half and twice its clip's pace. Oracles: the square-root split and the leg triangle worked by
    /// hand, and the physical rule that a stance foot does not move on the ground and no foot jumps between frames.</summary>
    internal static bool SelfCheck()
    {
        (float r1, float s1) = Pace(1f);
        (float r15, float s15) = Pace(1.5f);
        (float r3, float s3) = Pace(3f);
        (float rs, float ss) = Pace(0.36f);
        bool paces = MathF.Abs(r1 - 1f) < 1e-5f && MathF.Abs(s1 - 1f) < 1e-5f
                     && MathF.Abs(r15 - 1.224745f) < 1e-4f && MathF.Abs(s15 - 1.224745f) < 1e-4f
                     && MathF.Abs(r3 - 2.142857f) < 1e-4f && MathF.Abs(s3 - 1.4f) < 1e-5f
                     && MathF.Abs(rs - 0.514286f) < 1e-4f && MathF.Abs(ss - 0.7f) < 1e-5f;

        Vector3 hipJ = new(0f, 0f, 0.85f);
        bool reach = Drop(hipJ, new Vector3(0f, 0.3f, 0.08f), 0.9f) == 0f
                     && MathF.Abs(Drop(hipJ, new Vector3(0f, 0.5f, 0.08f), 0.9f) - 0.054368f) < 1e-4f
                     && MathF.Abs(Drop(hipJ, new Vector3(0f, 1.0f, 0.08f), 0.9f) - 0.108f) < 1e-5f;

        Anchor f = default;
        Vector2 o = Hold(ref f, true, 1f, Vector2.Zero, 0.05f);
        bool landed = f.Planted && f.At == Vector2.Zero && o == Vector2.Zero;
        for (int i = 1; i <= 10; i++) o = Hold(ref f, true, 1f, new Vector2(0.01f * i, 0f), 0.05f);
        bool dragged = MathF.Abs(o.X + 0.05f) < 1e-6f && MathF.Abs(f.At.X - 0.05f) < 1e-6f;
        Vector2 lift = Hold(ref f, false, 0f, new Vector2(0.1f, 0f), 0.05f);
        Vector2 mid = Hold(ref f, false, 0.5f, new Vector2(0.3f, 0f), 0.05f);
        Vector2 end = Hold(ref f, false, 1f, new Vector2(0.5f, 0f), 0.05f);
        Vector2 down = Hold(ref f, true, 1f, new Vector2(0.5f, 0f), 0.05f);
        bool swings = MathF.Abs(lift.X + 0.05f) < 1e-6f && MathF.Abs(mid.X + 0.025f) < 1e-6f && end == Vector2.Zero
                      && down == Vector2.Zero && f.At == new Vector2(0.5f, 0f);

        return paces && reach && landed && dragged && swings && Settles() && Walks();
    }

    /// <summary>A long stride's need, 0.1 at each double support and 0 between, twice a 1 s cycle for 4 s at 60 fps: the
    /// drawn drop moves by under 1 cm a frame and, once settled, swings by under 0.025 -- a lowered posture, not a bob that
    /// follows the need's full 0.1. Oracle: the hand-written need and the bounds a head seen bobbing would break.</summary>
    private static bool Settles()
    {
        const float Dt = 1f / 60f;
        float hold = 0f, x = 0f, v = 0f, prev = 0f, lo = 1f, hi = 0f, step = 0f;
        for (int n = 0; n < 240; n++)
        {
            float u = n * Dt * 2f;
            float need = u - MathF.Floor(u) < 0.2f ? 0.1f : 0f;
            float d = Settled(ref hold, ref x, ref v, need, 0.9f, Dt);
            step = MathF.Max(step, MathF.Abs(d - prev));
            prev = d;
            if (n >= 120) { lo = MathF.Min(lo, d); hi = MathF.Max(hi, d); }
        }
        return step < 0.01f && hi - lo < 0.025f && lo > 0.05f;
    }

    /// <summary>World units a lift-off may bend a drawn path past the animation's own: the drift a ramping pace leaves
    /// on a planted foot, picked up in one frame; a foot let go and snapped back moves ten times this.</summary>
    private const float LiftBend = 0.02f;
    /// <summary>The synthetic walk's own stance speed and floor, known by construction: the pace and the warp read them,
    /// not the analysis, which times only the plants.</summary>
    private const float SyntheticSpeed = 1.5f, SyntheticFloor = 0.08f;

    /// <summary>The synthetic walk (<see cref="GaitClip.Synthetic"/>) driven 4 s at 60 fps from half its pace to twice it
    /// and back, the rate a frame late as live: held steady, a planted foot's warped spot does not move, and no frame's
    /// plant bends a drawn path past the warped animation's own by more than <see cref="LiftBend"/>.</summary>
    private static bool Walks()
    {
        GaitClip c = GaitClip.Synthetic(0.85f, SyntheticFloor, SyntheticSpeed, 0.12f);
        if (!c.Walk) return false;
        const float Dt = 1f / 60f, MaxHold = 0.25f * 0.9f;
        Vector3 hip = new(0f, 0f, 0.85f);
        float t = 0f, body = 0f, rateNext = Pace(0.75f / SyntheticSpeed).Rate, worstSlide = 0f, worstBend = 0f;
        Span<Anchor> anchors = stackalloc Anchor[2];
        Span<Vector2> p1 = stackalloc Vector2[2], p2 = stackalloc Vector2[2], d1 = stackalloc Vector2[2], d2 = stackalloc Vector2[2];
        Span<bool> was = stackalloc bool[2];
        for (int n = 0; n < 240; n++)
        {
            float time = n * Dt;
            float g = time < 1f ? 0.75f + 2.25f * time : time < 2f ? 3f : time < 3f ? 3f - 2.25f * (time - 2f) : 0.75f;
            float rate = rateNext;
            t += rate * Dt;
            body += g * Dt;
            float k = g / SyntheticSpeed;
            rateNext = Pace(k).Rate;
            float phase = t, stride = Math.Clamp(k / rate, WarpMin, WarpMax);
            bool steady = time is > 1.2f and < 2f or > 3.2f;
            for (int side = 0; side < 2; side++)
            {
                bool right = side == 0;
                float own = right ? phase : phase + 0.5f;
                bool stance = own - MathF.Floor(own) < 0.6f;
                Vector3 a = GaitClip.SyntheticFoot(own, right ? 0.1f : -0.1f, SyntheticFloor, SyntheticSpeed, 0.12f);
                Vector3 w = Warp(a, hip, Vector3.UnitY, SyntheticFloor, stride, 1f);
                Vector2 p = new(w.X, w.Y + body);
                bool contact = c.Contact(right, phase);
                Vector2 drawn = p + Hold(ref anchors[side], contact, c.Swing(right, phase), p, MaxHold);
                if (steady && stance && was[side]) worstSlide = MathF.Max(worstSlide, Vector2.Distance(p, p1[side]) / Dt / g);
                if (n >= 2)
                {
                    float ex = (drawn - 2f * d1[side] + d2[side]).Length() - (p - 2f * p1[side] + p2[side]).Length();
                    worstBend = MathF.Max(worstBend, ex);
                }
                was[side] = stance;
                p2[side] = p1[side];
                p1[side] = p;
                d2[side] = d1[side];
                d1[side] = drawn;
            }
        }
        return worstSlide < 0.02f && worstBend < LiftBend;
    }
}
