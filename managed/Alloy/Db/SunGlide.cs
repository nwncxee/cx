using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The sun glide's whole state: the direction shown, the glide in flight, and the cadence the
/// engine's direction changes arrive at. One lifetime owner, so a reset is one object rather than a dozen
/// fields.</summary>
internal sealed class SunGlideState
{
    /// <summary>Unit vectors pointing TO the light: what is shown this frame, and the pair the live glide
    /// runs between.</summary>
    public Vector3 Shown, From, Target;
    /// <summary>A sample has been taken, so <see cref="Shown"/> is a direction rather than zero.</summary>
    public bool HasShown;
    /// <summary>The sampled pair reaches far enough to be the area's SUN, not only its directional
    /// diffuse.</summary>
    public bool Sun;
    /// <summary>The next sample snaps: an area edge has been announced.</summary>
    public bool Edge;
    /// <summary>The live glide's start and length in seconds. Length 0 means it has landed.</summary>
    public double Start, Length;
    /// <summary>The move in flight is an eased cut: it runs on an ease-in-out curve, and a step arriving during
    /// it moves its end instead of starting a glide of its own.</summary>
    public bool Easing;
    /// <summary>When the last snap or retarget happened -- the base the next step's interval is measured
    /// from.</summary>
    public double Last;
    /// <summary>The fade the server gave the area's last colour packet, and when it arrived: a cut in a burst
    /// whose colours were set with no fade is a lightning flash.</summary>
    public float Fade;
    public double FadeAt = double.NegativeInfinity;
    /// <summary>When the last flash's cut landed, and when the area's first sample was shown.</summary>
    public double FlashAt = double.NegativeInfinity, EdgeAt = double.NegativeInfinity;
    /// <summary>No colour packet is heard, so a flash cannot be told from a time change and every cut lands
    /// at once.</summary>
    public bool Deaf;
    /// <summary>For the period line: changes far enough apart to be a server's STEPS, changes arriving every
    /// frame (an engine fade or an owned twilight, which is plain following), eased cuts, and cuts that
    /// landed at once.</summary>
    public long Steps, Follows, Eased, Snaps;
}

/// <summary>The colour leaf's chain slot: the one piece of the glide that a <see cref="SunGlide.Swap"/> must
/// not take away.</summary>
internal sealed unsafe class SunGlideHookState
{
    public AreaSetAreaLightColorFn* Next;
}

/// <summary><b>Continuous motion out of a stepped sun:</b> a server that moves the area light in small
/// unfaded steps hands the client a new direction every few seconds, which the cascades and the shading
/// would otherwise take on the frame it arrives. A small step glides over the interval the steps are
/// arriving at; a time change eases over a length that grows with its size; a lightning flash lands at once.</summary>
internal static unsafe class SunGlide
{
    /// <summary>A direction change beyond this is a cut, not a step: an area load, a time jump, a script's
    /// deliberate move and the vanilla dusk midpoint's sun-to-moon swap all move the direction much further
    /// than a server's nudge.</summary>
    private const float SnapDegrees = 3f;

    /// <summary>The shortest a glide may take, in seconds. The floor is what keeps a direction that changes
    /// every frame -- an engine fade, an owned twilight -- behaving as plain following.</summary>
    private const double MinGlide = 0.05;
    /// <summary>The census's line between a server's step and per-frame following: longer than any frame a
    /// playable client takes, far shorter than any server's step cadence.</summary>
    private const double StepFloor = 0.25;

    /// <summary>The longest a glide may take, in seconds: the ceiling on how long an unknown or very slow
    /// cadence can hold the shown direction behind the engine's.</summary>
    private const double MaxGlide = 30.0;

    /// <summary><see cref="SnapDegrees"/> as the dot product of two unit directions, which is what the test
    /// actually compares.</summary>
    private static readonly float SnapCos = MathF.Cos(SnapDegrees * (MathF.PI / 180f));

    /// <summary>Below this angle in radians there is nothing to interpolate between: the slerp's denominator
    /// vanishes and the target is already the answer to every visible precision.</summary>
    private const float Still = 1e-5f;

    /// <summary>How recent a colour packet must be, in seconds, to belong to a cut's burst. A server sends a
    /// burst's packets back to back and the next burst no sooner than a second later.</summary>
    private const double Burst = 0.5;

    /// <summary>How long after a flash a cut is its way back, in seconds. A flash holds for under a second,
    /// and the server's own sun tick can beat its restore.</summary>
    private const double Excursion = 2.0;

    /// <summary>How long after an area's first sample a cut still lands at once, in seconds: the server
    /// corrects a freshly loaded area within a second, and a sun swinging into place there is the load showing.</summary>
    private const double Settle = 2.0;

    /// <summary>An eased cut's seconds per square-root degree: an hour's 15 deg turns in about 1.2 s, a quarter
    /// of the sky in about 2.8 s and the widest eased cut in under 4 s -- brisk when small, never dragging.</summary>
    private const double EaseRate = 0.3;

    /// <summary>A cut to a direction this close to opposite lands at once: no turn between the two is the
    /// obvious one, and the slerp's denominator heads for zero.</summary>
    private const float Opposed = -0.99f;

    /// <summary>Inside the lighting trace, which reports the packet as it arrived.</summary>
    private const int ColourPriority = 30;

    private static SunGlideState _state = new();
    private static SunGlideHookState _hook = new();

    /// <summary>Alloy's own frame clock -- seconds since load, so the glide is frame-rate independent and a
    /// frame that walks its scene twice gets one answer.</summary>
    private static double Now => TexStream.Clock;

    /// <summary>One engine sample in, the direction to SHOW out. <paramref name="sun"/> rides along so a
    /// frame that only coasts still knows whether the shown direction is the sun's.</summary>
    internal static Vector3 Follow(Vector3 engineToLight, bool sun)
    {
        bool edge = _state.Edge;
        _state.Edge = false;
        _state.Sun = sun;
        return Step(_state, engineToLight, Now, edge);
    }

    /// <summary>A frame that published no directional term of its own -- a reused rig, or blind lights
    /// suppressing the pair: the glide advances on the clock alone. False when nothing is shown yet or the
    /// glide has already landed.</summary>
    internal static bool Coast(out Vector3 shown, out bool sun)
    {
        sun = _state.Sun;
        shown = _state.Shown;
        if (!_state.HasShown || _state.Length <= 0) return false;
        shown = Advance(_state, Now);
        return true;
    }

    /// <summary>The area edge: the next sample is a different area's sun, so it snaps.</summary>
    internal static void AreaEdge() => _state.Edge = true;

    /// <summary>Hear every area colour packet's fade: what tells a lightning flash from a time change.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _hook.Next = hooks.ChainAreaSetAreaLightColor(&OnColour, ColourPriority);
        if (_hook.Next != null) return;
        _state.Deaf = true;
        log.Always("SunGlide: the area colour leaf did not chain -- a flash cannot be told from a time change, "
                 + "so every large sun move lands at once");
    }

    [UnmanagedCallersOnly]
    private static void OnColour(nint area, uint slot, VectorArg colour, float fade)
    {
        Heard(_state, fade, Now);
        (*_hook.Next)(area, slot, colour, fade);
    }

    /// <summary>A colour packet arrived with this fade: the pure half of <see cref="OnColour"/>.</summary>
    internal static void Heard(SunGlideState s, float fade, double now)
    {
        s.Fade = fade;
        s.FadeAt = now;
    }

    /// <summary>Drive the glide from a state of one's own, and hand back the live one: a fixture's steps must
    /// not land in the session's census, nor the session's sun inherit a fixture's direction.</summary>
    internal static SunGlideState Swap(SunGlideState s)
    {
        SunGlideState was = _state;
        _state = s;
        return was;
    }

    /// <summary>The glide's clause on the period line: whether the direction arrives in steps or every frame,
    /// how long each glide runs, and how far behind the engine the shown direction sits.</summary>
    internal static string Note()
        => $"sunglide: {_state.Steps} glided, {_state.Follows} followed, {_state.Eased} eased, {_state.Snaps} snapped, "
         + $"{_state.Length:F2} s glide, {Degrees(_state.Shown, _state.Target):F2} deg behind";

    /// <summary>The pure seam: the engine's current to-light direction and the clock in, the direction to
    /// show out. A change under <see cref="SnapDegrees"/> glides over the measured interval between recent
    /// steps; a larger one is a <see cref="Cut"/>; a first sample or an area edge snaps.</summary>
    internal static Vector3 Step(SunGlideState s, Vector3 engineToLight, double now, bool areaEdge)
    {
        if (engineToLight.LengthSquared() <= 1e-8f) return s.Shown;
        Vector3 to = Vector3.Normalize(engineToLight);
        if (areaEdge || !s.HasShown) { s.EdgeAt = now; return Snap(s, to, now); }
        if (to == s.Target) return Advance(s, now);
        if (Vector3.Dot(s.Target, to) < SnapCos) return Cut(s, to, now);
        // The sun's own tick during an eased cut: the cut keeps its curve and lands on the newer direction.
        if (s.Easing && s.Length > 0) { s.Target = to; s.Last = now; return Advance(s, now); }
        // Measured before Last moves: this interval is both the glide's length and what tells a server's step
        // from a direction that changes every frame.
        double interval = now - s.Last;
        Vector3 from = Advance(s, now);
        s.From = from;
        s.Target = to;
        s.Start = now;
        s.Length = Math.Clamp(interval, MinGlide, MaxGlide);
        s.Last = now;
        if (interval > StepFloor) s.Steps++; else s.Follows++;
        return from;
    }

    /// <summary>The glide's position at <paramref name="now"/>, with no new sample: a slerp from where the
    /// glide started to the engine's direction -- at constant rate for a glide, eased in and out for a cut --
    /// which lands and stays landed.</summary>
    internal static Vector3 Advance(SunGlideState s, double now)
    {
        if (!s.HasShown) return s.Shown;
        if (s.Length <= 0) { s.Shown = s.Target; return s.Shown; }
        double t = (now - s.Start) / s.Length;
        if (t >= 1.0) { s.Length = 0; s.Easing = false; s.Shown = s.Target; return s.Shown; }
        if (s.Easing) t = t * t * (3.0 - 2.0 * t);
        s.Shown = t <= 0.0 ? s.From : Slerp(s.From, s.Target, (float)t);
        return s.Shown;
    }

    /// <summary>A change too large to be a step. A lightning flash -- its burst sets the colours with no fade
    /// -- lands at once, and so does its way back and a freshly loaded area's correction. Anything else is a
    /// time change: a jump of hours, a script's move, a dusk's sun-to-moon swap, eased over
    /// <see cref="EaseSeconds"/>.</summary>
    private static Vector3 Cut(SunGlideState s, Vector3 to, double now)
    {
        Vector3 from = Advance(s, now);
        if (now - s.FadeAt <= Burst && s.Fade <= 0f) s.FlashAt = now;
        if (s.Deaf || now - s.FlashAt <= Excursion || now - s.EdgeAt <= Settle || Vector3.Dot(from, to) < Opposed)
            return Snap(s, to, now);
        s.From = from;
        s.Target = to;
        s.Start = now;
        s.Length = EaseSeconds(Degrees(from, to));
        s.Easing = true;
        s.Last = now;
        s.Eased++;
        return from;
    }

    /// <summary>How long a cut of <paramref name="deg"/> degrees turns for: the square root keeps a small jump
    /// brisk and a large one from dragging.</summary>
    internal static double EaseSeconds(double deg) => EaseRate * Math.Sqrt(deg);

    /// <summary>This direction, now, with no glide: a cut, a first sample or an area edge. The interval clock
    /// restarts here, so the next step is measured from the cut rather than from before it.</summary>
    private static Vector3 Snap(SunGlideState s, Vector3 to, double now)
    {
        s.Shown = s.From = s.Target = to;
        s.HasShown = true;
        s.Start = now;
        s.Length = 0;
        s.Easing = false;
        s.Last = now;
        s.Snaps++;
        return to;
    }

    /// <summary>Constant-rate interpolation between two unit directions. <see cref="Cut"/> refuses a pair
    /// near <see cref="Opposed"/>, so the denominator stays clear of the antiparallel case.</summary>
    private static Vector3 Slerp(Vector3 a, Vector3 b, float t)
    {
        float theta = MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b));
        if (theta < Still) return b;
        float sin = MathF.Sin(theta);
        return Vector3.Normalize((a * (MathF.Sin((1f - t) * theta) / sin)) + (b * (MathF.Sin(t * theta) / sin)));
    }

    /// <summary>The angle between two unit directions, in degrees. Through the cross product rather than
    /// <c>acos</c> of the dot, which loses its precision exactly where a step lives.</summary>
    private static float Degrees(Vector3 a, Vector3 b)
        => MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b)) * (180f / MathF.PI);

    /// <summary>A unit to-light direction <paramref name="deg"/> degrees off straight up, in the XZ plane.
    /// Two of these slerp to the one at the interpolated angle, which is what lets this check and
    /// <c>DbLights.SelfCheck</c> write their direction expectations as literals.</summary>
    internal static Vector3 Overhead(double deg)
    {
        double r = deg * Math.PI / 180.0;
        return new Vector3((float)Math.Sin(r), 0f, (float)Math.Cos(r));
    }

    /// <summary>Off-device: the glide's contract over a hand-driven clock -- snaps, glides, eased time changes,
    /// flashes landed at once, re-targets, the measured cadence. Oracle: hand-worked literals -- a slerp in the
    /// XZ plane sits at the interpolated angle, 3t^2 - 2t^3 is 0.15625 at a quarter, 0.3 * sqrt(36) is 1.8.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SunGlide.SelfCheck");
        static bool Near(Vector3 a, Vector3 b) => (a - b).Length() < 1e-5f;
        var s = new SunGlideState();

        // Entering an area shows its sun on the first frame.
        Vector3 shown = Step(s, Overhead(0), 0.0, areaEdge: false);
        check.Expect(Near(shown, Overhead(0)) && s.Length == 0.0, $"the first sample snaps ({shown})");

        // A 0.5 deg step 20 s later glides, over the 20 s measured since the last change.
        shown = Step(s, Overhead(0.5), 20.0, areaEdge: false);
        check.Expect(Near(shown, Overhead(0)), $"a small step does not move the sun on its own frame ({shown})");
        check.Expect(Math.Abs(s.Length - 20.0) < 1e-9, $"...and its glide runs for the measured 20 s ({s.Length})");

        // Halfway: sin and cos of 0.25 deg, which is where a slerp between 0 and 0.5 deg is at t = 0.5.
        shown = Advance(s, 30.0);
        check.Expect(Near(shown, new Vector3(0.004363323f, 0f, 0.999990480f)),
                     $"halfway through, the sun is 0.25 deg from both ends ({shown})");

        // ...and it lands on the engine's direction rather than overshooting or stopping short.
        shown = Advance(s, 40.0);
        check.Expect(Near(shown, Overhead(0.5)) && s.Length == 0.0, $"the glide lands on the step's direction ({shown})");
        check.Expect(Advance(s, 1000.0) == shown, "...and stays there with no further sample");
        check.Expect(s.Steps == 1 && s.Follows == 0 && s.Snaps == 1,
                     $"one step glided, nothing followed, one snap ({s.Steps}/{s.Follows}/{s.Snaps})");

        // With no colour packet heard, a flash cannot be told from a time change: a 30 deg cut lands at once.
        s.Deaf = true;
        shown = Step(s, Overhead(30.5), 60.0, areaEdge: false);
        check.Expect(Near(shown, Overhead(30.5)) && s.Length == 0.0, $"a deaf glide lands a 30 deg cut at once ({shown})");
        s.Deaf = false;

        // An area edge snaps even a change small enough to glide.
        shown = Step(s, Overhead(30.9), 80.0, areaEdge: true);
        check.Expect(Near(shown, Overhead(30.9)) && s.Length == 0.0, $"an area edge snaps a 0.4 deg change ({shown})");

        // A 36 deg time jump turns for 0.3 * sqrt(36) = 1.8 s, starting where the sun was.
        var e = new SunGlideState();
        Step(e, Overhead(0), 0.0, areaEdge: false);
        Heard(e, 2f, 10.0);
        shown = Step(e, Overhead(36), 10.0, areaEdge: false);
        check.Expect(Near(shown, Overhead(0)) && Math.Abs(e.Length - 1.8) < 1e-4,
                     $"a 36 deg time jump starts where the sun was and turns for 1.8 s ({shown}, {e.Length})");
        // A quarter of the way in, 3t^2 - 2t^3 = 0.15625 of 36 deg: 5.625 deg, sin and cos of pi/32.
        check.Expect(Near(Advance(e, 10.45), new Vector3(0.0980171403f, 0f, 0.995184727f)),
                     $"a quarter of the way in, the turn has eased to 5.625 deg ({e.Shown})");
        // Halfway in time is halfway in angle: sin and cos of 18 deg.
        check.Expect(Near(Advance(e, 10.9), new Vector3(0.309016994f, 0f, 0.951056516f)),
                     $"halfway in, the sun is at 18 deg ({e.Shown})");

        // The sun's own 0.25 deg tick mid-turn moves where the turn ends, not its curve.
        Step(e, Overhead(36.25), 10.9, areaEdge: false);
        check.Expect(e.Start == 10.0 && Math.Abs(e.Length - 1.8) < 1e-4,
                     $"a tick mid-turn keeps the turn's start and length ({e.Start}, {e.Length})");
        check.Expect(Near(Advance(e, 12.0), Overhead(36.25)) && e.Length == 0.0, "...and the turn lands on the tick");
        check.Expect(e.Eased == 1 && e.Snaps == 1 && e.Steps == 0,
                     $"one cut eased, the first sample snapped, no glide started ({e.Eased}/{e.Snaps}/{e.Steps})");

        // A lightning flash sets its colours with no fade: the sun lands on it at once...
        Heard(e, 0f, 20.0);
        shown = Step(e, Overhead(80), 20.0, areaEdge: false);
        check.Expect(Near(shown, Overhead(80)) && e.Length == 0.0, $"a cut in a fade-0 colour burst lands at once ({shown})");
        // ...and back at once 0.7 s later, though the restore fades its colours over 0.3 s.
        Heard(e, 0.3f, 20.7);
        shown = Step(e, Overhead(36.25), 20.7, areaEdge: false);
        check.Expect(Near(shown, Overhead(36.25)) && e.Length == 0.0, $"the flash's way back lands at once ({shown})");

        // Long after the flash, a cut with no colour packet at all -- a script's move -- is a time change.
        shown = Step(e, Overhead(0.25), 30.0, areaEdge: false);
        check.Expect(Near(shown, Overhead(36.25)) && Math.Abs(e.Length - 1.8) < 1e-4,
                     $"a direction-only 36 deg cut 9.3 s after the flash eases for 1.8 s ({shown}, {e.Length})");

        // A freshly loaded area's correction lands at once: a sun swinging into place would be the load showing.
        var a = new SunGlideState();
        Step(a, Overhead(0), 0.0, areaEdge: true);
        Heard(a, 2f, 0.6);
        shown = Step(a, Overhead(20), 0.6, areaEdge: false);
        check.Expect(Near(shown, Overhead(20)) && a.Length == 0.0, $"a correction 0.6 s after an area edge lands at once ({shown})");

        // Nearly opposite: no turn between the two is the obvious one.
        var o = new SunGlideState();
        Step(o, Overhead(0), 0.0, areaEdge: false);
        shown = Step(o, Overhead(179), 10.0, areaEdge: false);
        check.Expect(Near(shown, Overhead(179)) && o.Length == 0.0, $"a 179 deg cut lands at once ({shown})");

        // A step arriving mid-glide re-targets from where the sun is SHOWN, with no jump.
        var m = new SunGlideState();
        Step(m, Overhead(0), 0.0, areaEdge: false);
        Step(m, Overhead(0.5), 20.0, areaEdge: false);
        Vector3 mid = Advance(m, 30.0);
        shown = Step(m, Overhead(1.0), 30.0, areaEdge: false);
        check.Expect(Near(shown, mid), $"a step mid-glide shows the same direction that frame ({shown} vs {mid})");
        check.Expect(Math.Abs(m.Length - 10.0) < 1e-9, $"...over the 10 s since the last step ({m.Length})");
        check.Expect(Near(Advance(m, 40.0), Overhead(1.0)), "...and lands on the new direction");

        // The floor: a direction that changes every frame is followed, and the census says so.
        var f = new SunGlideState();
        Step(f, Overhead(0), 0.0, areaEdge: false);
        Step(f, Overhead(0.01), 0.001, areaEdge: false);
        check.Expect(Math.Abs(f.Length - 0.05) < 1e-9, $"a per-frame change glides for the floor ({f.Length})");
        check.Expect(f.Steps == 0 && f.Follows == 1, $"...and counts as following, not as a step ({f.Steps}/{f.Follows})");

        // The ceiling: a first step after a long quiet spell must not glide for the whole spell.
        var q = new SunGlideState();
        Step(q, Overhead(0), 0.0, areaEdge: false);
        Step(q, Overhead(0.5), 600.0, areaEdge: false);
        check.Expect(Math.Abs(q.Length - 30.0) < 1e-9, $"a 600 s gap glides for the ceiling ({q.Length})");
        check.Expect(q.Steps == 1 && q.Follows == 0, $"...and still counts as one step ({q.Steps}/{q.Follows})");

        // A degenerate sample is not a direction: the engine's light sits at the origin before it is placed.
        var z = new SunGlideState();
        check.Expect(Step(z, Vector3.Zero, 0.0, areaEdge: false) == Vector3.Zero && !z.HasShown,
                     "a zero direction is not a sample");

        log.Info(check.Ok ? "SunGlide.SelfCheck: passed"
                          : "SunGlide.SelfCheck: FAILED -- the area's sun would teleport on a server step or a "
                          + "time change, sweep through a lightning flash, or drift from the engine's direction");
        return check.Ok;
    }
}
