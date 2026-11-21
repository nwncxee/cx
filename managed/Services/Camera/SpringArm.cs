using System;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>One segment cast the spring arm makes: the nearest wall on it, if any.</summary>
internal interface IArmCast
{
    bool Cast(Vector3 from, Vector3 to, out Vector3 hit);
}

/// <summary>The spring arm across frames: where it sits, how long its rays have been clear, and the period's counts.</summary>
internal sealed class SpringArmState
{
    internal float Arm = 1f;
    internal float ClearFor;
    internal int Casts, NoScene, Pulls;
    /// <summary>This period's closest wall hit to the pivot, and the pivot it was cast from: what a stuck arm is stuck on.</summary>
    internal float NearHit = float.MaxValue;
    internal System.Numerics.Vector3 NearAt, NearFrom;
}

/// <summary>
/// Keeps the offset camera out of walls. A fan of rays from the pivot (the follow focus) to the wanted camera position
/// is cast against the area's static geometry alone, as the engine's own pull-in casts, and the camera slides to the
/// nearest wall minus the margin. A pull-in snaps; the way back out waits <see cref="Hold"/> of clear rays, then eases.
/// </summary>
internal static unsafe class SpringArm
{
    /// <summary>The arm's one lifetime owner: reset with the camera layer's other state.</summary>
    internal static SpringArmState S = new();

    /// <summary>The offset floor: the arm never pulls the camera closer than this fraction of its rig.</summary>
    internal const float ArmMin = 0.05f;
    internal const int ArmRays = 5;
    /// <summary>Seconds every ray must stay clear before a pulled-in arm starts back out: a ray grazing an edge flips
    /// hit and miss frame to frame, and without the hold every flip is a pull-in and the camera pumps.</summary>
    internal const float Hold = 0.3f;
    /// <summary>Shorter than this the rig has no direction to cast along.</summary>
    private const float MinLen = 0.05f;

    /// <summary>The fraction of the rig the camera may sit at this frame: the nearest wall any ray of the fan meets,
    /// less <paramref name="margin"/>, over the rig's length, held between <see cref="ArmMin"/> and 1; 1 with no wall.
    /// Every ray starts at the pivot, so one beside a wall never starts inside it.</summary>
    internal static float Reach<TCast>(ref TCast cast, Vector3 pivot, Vector3 want, float radius, float margin)
        where TCast : struct, IArmCast
    {
        Vector3 seg = want - pivot;
        float len = seg.Length();
        if (len <= MinLen) return 1f;
        Vector3 d = seg / len;
        Vector3 right = Vector3.Cross(d, Vector3.UnitZ);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        Vector3 up = Vector3.Cross(right, d);
        float nearest = float.MaxValue;
        for (int r = 0; r < ArmRays; r++)
        {
            Vector3 o = r switch { 1 => right * radius, 2 => -right * radius, 3 => up * radius, 4 => -up * radius, _ => Vector3.Zero };
            if (cast.Cast(pivot, want + o, out Vector3 hit))
                nearest = MathF.Min(nearest, Vector3.Dot(hit - pivot, d));
        }
        return nearest == float.MaxValue ? 1f : Math.Clamp((nearest - margin) / len, ArmMin, 1f);
    }

    /// <summary>Move the arm toward this frame's <paramref name="target"/>: in at once (or at <paramref name="rateIn"/>),
    /// out only after <see cref="Hold"/> seconds of a farther target, then at <paramref name="rateOut"/>.</summary>
    internal static float Settle(SpringArmState s, float target, float dt, float rateIn, float rateOut)
    {
        dt = Math.Min(dt, 0.1f);
        if (target <= s.Arm)
        {
            s.ClearFor = 0f;
            if (target < s.Arm) s.Arm = rateIn <= 0f ? target : s.Arm + (target - s.Arm) * Math.Clamp(rateIn * dt, 0f, 1f);
        }
        else
        {
            s.ClearFor += dt;
            if (s.ClearFor >= Hold)
                s.Arm = rateOut <= 0f ? target : s.Arm + (target - s.Arm) * Math.Clamp(rateOut * dt, 0f, 1f);
        }
        if (MathF.Abs(target - s.Arm) < 0.002f) s.Arm = target;
        return s.Arm;
    }

    /// <summary>The live cast: <see cref="SceneRay.CastStatic"/> through the world scene.</summary>
    private struct SceneCast : IArmCast
    {
        internal IntPtr Scene;

        public bool Cast(Vector3 from, Vector3 to, out Vector3 hit)
        {
            if (!SceneRay.CastStatic(Scene, from, to, out hit)) return false;
            float at = Vector3.Distance(hit, from);
            if (at < S.NearHit) { S.NearHit = at; S.NearAt = hit; S.NearFrom = from; }
            return true;
        }
    }

    /// <summary>This frame's arm for a rig from <paramref name="pivot"/> to <paramref name="want"/>: the camera
    /// position it allows.</summary>
    internal static Vector3 Place(Vector3 pivot, Vector3 want, float dt)
    {
        IntPtr scene = SceneRay.Armed ? SceneRay.WorldScene() : IntPtr.Zero;
        float target = 1f;
        if (scene == IntPtr.Zero) S.NoScene++;
        else
        {
            S.Casts++;
            var cast = new SceneCast { Scene = scene };
            target = Reach(ref cast, pivot, want, Walls.ArmRadius.V, Walls.Margin.V);
        }
        if (target < S.Arm) S.Pulls++;
        Settle(S, target, dt, Walls.ArmIn.V, Walls.ArmOut.V);
        return pivot + (want - pivot) * S.Arm;
    }

    /// <summary>Stand the arm at its full rig (a gate, the free camera, the offset off).</summary>
    internal static void Rest() { S.Arm = 1f; S.ClearFor = 0f; }

    /// <summary>The period's counts for the camera line, then zeroed.</summary>
    internal static string Period()
    {
        string s = FormattableString.Invariant($"arm {S.Arm:0.00} over {S.Casts} casting frame(s), {S.Pulls} pull-in(s)")
                 + (S.NoScene == 0 ? "" : FormattableString.Invariant($" and {S.NoScene} with NO scene to cast against"));
        if (S.NearHit != float.MaxValue)
            s += FormattableString.Invariant($", nearest wall {S.NearHit:0.00} m from the pivot ({S.NearFrom.X:0.00}, {S.NearFrom.Y:0.00}, {S.NearFrom.Z:0.00}) at ({S.NearAt.X:0.00}, {S.NearAt.Y:0.00}, {S.NearAt.Z:0.00})");
        S.Casts = S.NoScene = S.Pulls = 0;
        S.NearHit = float.MaxValue;
        return s;
    }

    // ---- self-check ------------------------------------------------------------------------------

    /// <summary>A world of axis-aligned wall boxes for the self-check, hit where a segment enters one and, from
    /// inside it, where it leaves: the two faces a two-sided mesh shows a ray.</summary>
    private struct BoxCast : IArmCast
    {
        internal (Vector3 Lo, Vector3 Hi)[] Boxes;

        public bool Cast(Vector3 from, Vector3 to, out Vector3 hit)
        {
            hit = default;
            float best = float.MaxValue;
            Vector3 d = to - from;
            foreach (var b in Boxes)
                if (Slab(from, d, b.Lo, b.Hi, out float t) && t < best) best = t;
            if (best == float.MaxValue) return false;
            hit = from + d * best;
            return true;
        }

        private static bool Slab(Vector3 o, Vector3 d, Vector3 lo, Vector3 hi, out float t)
        {
            float t0 = 0f, t1 = 1f;
            for (int a = 0; a < 3; a++)
            {
                float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
                float la = a == 0 ? lo.X : a == 1 ? lo.Y : lo.Z, ha = a == 0 ? hi.X : a == 1 ? hi.Y : hi.Z;
                if (MathF.Abs(da) < 1e-9f) { if (oa < la || oa > ha) { t = 0f; return false; } continue; }
                float u = (la - oa) / da, v = (ha - oa) / da;
                if (u > v) (u, v) = (v, u);
                t0 = MathF.Max(t0, u); t1 = MathF.Min(t1, v);
                if (t0 > t1) { t = 0f; return false; }
            }
            t = t0 > 0f ? t0 : t1;
            return t > 0f && t <= 1f;
        }
    }

    /// <summary>The arm's geometry and its timing, against a box world laid out by hand: the oracles are the hand
    /// distances of each wall, worked in the comments. What counts as a wall is <c>HitTestCheck.CameraArm</c>'s.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Vector3 pivot = new(0f, 0f, 1f), back = new(0f, -5f, 1f);
        const float Radius = 0.3f, Margin = 0.3f;
        // A wall 3 m behind the pivot, 0.2 m thick, wide and tall enough that every ray of the fan meets its face.
        var wall = (new Vector3(-5f, -3.2f, -5f), new Vector3(5f, -3f, 5f));

        var clear = new BoxCast { Boxes = Array.Empty<(Vector3, Vector3)>() };
        check.Expect(Reach(ref clear, pivot, back, Radius, Margin) == 1f, "nothing in the way: the full rig, no margin taken");

        // The face is 3 m out along every ray (they all run due south here): (3 - 0.3) / 5 = 0.54.
        var walled = new BoxCast { Boxes = new[] { wall } };
        float w = Reach(ref walled, pivot, back, Radius, Margin);
        check.Expect(MathF.Abs(w - 0.54f) < 1e-3f, $"a wall 3 m back holds the camera at 0.54 of a 5 m rig (got {w:0.###})");

        // A post 2 m back that only the fan's ray toward -x meets (at y -2 that ray is at x -0.12), in front of the
        // same wall: the nearest of the rays wins, (2 - 0.3) / 5 = 0.34.
        var post = (new Vector3(-0.2f, -2.1f, 0f), new Vector3(-0.1f, -2f, 2f));
        var posted = new BoxCast { Boxes = new[] { post, wall } };
        float pw = Reach(ref posted, pivot, back, Radius, Margin);
        check.Expect(MathF.Abs(pw - 0.34f) < 1e-3f, $"one ray meeting a nearer post holds the camera in front of it (got {pw:0.###}, want 0.34)");

        // A wall 0.1 m to the character's right (x 0.1..0.4) with the camera off to the left and behind: every ray
        // starts at the pivot (x 0) and runs left, so none reaches x 0.1 and the rig stays whole.
        var side = new BoxCast { Boxes = new[] { (new Vector3(0.1f, -20f, -5f), new Vector3(0.4f, 20f, 5f)) } };
        float sd = Reach(ref side, pivot, new Vector3(-1.5f, -5f, 1f), Radius, Margin);
        check.Expect(sd == 1f, $"a wall beside the character on the side away from the camera does not collapse the arm (got {sd:0.###})");

        // Timing: snap in to 0.5, then a target flickering 0.5 / 1 every 60 Hz frame for a second holds the arm still.
        var st = new SpringArmState();
        Settle(st, 0.5f, 1f / 60f, 0f, 4f);
        check.Expect(st.Arm == 0.5f, "a pull-in snaps");
        float lo = 1f, hi = 0f;
        for (int i = 0; i < 60; i++)
        {
            Settle(st, (i & 1) == 0 ? 1f : 0.5f, 1f / 60f, 0f, 4f);
            lo = MathF.Min(lo, st.Arm); hi = MathF.Max(hi, st.Arm);
        }
        check.Expect(hi - lo < 1e-6f, $"a ray grazing an edge on and off does not pump the arm (it moved {hi - lo:0.###})");
        // Clear from here on: still 0.5 inside the hold, then out, and at 4/s whole within two seconds.
        for (int i = 0; i < 6; i++) Settle(st, 1f, 1f / 60f, 0f, 4f);
        check.Expect(st.Arm == 0.5f, "a tenth of a second of clear rays does not start the arm out");
        for (int i = 0; i < 14; i++) Settle(st, 1f, 1f / 60f, 0f, 4f);
        // 20 clear frames are 0.333 s: the hold ran out on the 18th, so two frames of 4/s ease have moved it a little.
        check.Expect(st.Arm > 0.5f && st.Arm < 0.6f, $"once the hold runs out the arm eases out, it does not jump (got {st.Arm:0.###})");
        for (int i = 0; i < 106; i++) Settle(st, 1f, 1f / 60f, 0f, 4f);
        check.Expect(st.Arm == 1f, "two seconds of clear rays bring the arm all the way back");
        log.Info(check.Ok ? "SpringArm.SelfCheck: passed" : "SpringArm.SelfCheck: FAILED");
    }
}
