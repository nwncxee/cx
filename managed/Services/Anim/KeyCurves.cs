using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Keyframe curves (<c>anim.curves</c>). The engine interpolates every linear key track in a straight line between
/// the two keys around the time (<c>NewController::GetVectorValue</c> / <c>GetQuaternionValue</c>), so a limb changes
/// speed abruptly at each key; with the knob on the segment curves through the neighbouring keys instead, timed by the
/// real key times -- positions a monotone cubic, rotations squad -- and still passes through every key.
/// </summary>
public sealed unsafe class KeyCurveService : IExtenderService
{
    private static CtrlGetVectorFn _vector;
    private static CtrlGetQuaternionFn _rotation;
    private static float* _positionScale;

    /// <summary>A <see cref="EngineLayout.NewControllerFormat"/> byte: the value's width with no Bezier bit.</summary>
    private const byte VectorKeys = 3, RotationKeys = 4;
    /// <summary>Seconds under which two key times are one: a neighbour that close gives no slope and is dropped.</summary>
    private const float SameTime = 1e-5f;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _ = AnimKnobs.G;
        SelfCheck(log);
        if (!EngineLayout.Require(log, "KeyCurves", "NewController", "MdlNode")) return;
        if (!SymbolMap.Require(log, "KeyCurves", SymbolMap.CtrlGetVector, SymbolMap.CtrlGetQuaternion, SymbolMap.PositionScaleFactor)) return;
        _positionScale = (float*)Native.Sym(SymbolMap.PositionScaleFactor);
        if (_positionScale == null) { log.Info("KeyCurves: standing down (position_scale_factor unresolved)"); return; }
        _vector = hooks.InstallCtrlGetVector(&OnVector);
        _rotation = hooks.InstallCtrlGetQuaternion(&OnRotation);
        log.Info(_vector != null && _rotation != null
            ? $"KeyCurves: hooked the vector and rotation key getters (curves {(AnimKnobs.Curves.V ? "on" : "off")})"
            : "KeyCurves: a key getter did not hook -- animations keep the engine's straight segments");
    }

    [UnmanagedCallersOnly]
    private static int OnVector(IntPtr ctrl, float t, IntPtr node, Vector3* v)
    {
        if (AnimKnobs.Curves.V && Track(ctrl, node, VectorKeys, out ReadOnlySpan<float> times, out ReadOnlySpan<float> values, out int n)
            && Position(times, values, n, t, out Vector3 p))
        {
            *v = new NewController(ctrl).Id == EngineLayout.PartPos ? p * *_positionScale : p;
            return 1;
        }
        return _vector(ctrl, t, node, v);
    }

    [UnmanagedCallersOnly]
    private static int OnRotation(IntPtr ctrl, float t, IntPtr node, Quat* rot)
    {
        if (AnimKnobs.Curves.V && Track(ctrl, node, RotationKeys, out ReadOnlySpan<float> times, out ReadOnlySpan<float> values, out int n)
            && Turn(times, values, n, t, out Quaternion q))
        {
            *rot = q;
            return 1;
        }
        return _rotation(ctrl, t, node, rot);
    }

    /// <summary>The controller's key times and values in its node's pool, when it is a linear track
    /// <paramref name="width"/> floats wide; anything else (Bezier, another width) is the engine's.</summary>
    private static bool Track(IntPtr ctrl, IntPtr node, byte width, out ReadOnlySpan<float> times, out ReadOnlySpan<float> values, out int n)
    {
        NewController c = new(ctrl);
        float* pool = (float*)new MdlNode(node).ControllerData;
        n = (ushort)c.KeyCount;
        times = values = default;
        if (c.Format != width || pool == null) return false;
        times = new Span<float>(pool, c.TimeKey + n).Slice(c.TimeKey);
        values = new Span<float>(pool, c.ValueKey + width * n).Slice(c.ValueKey);
        return true;
    }

    /// <summary>A vector track's curve at <paramref name="t"/>, or false where the engine's own value stands.</summary>
    internal static bool Position(ReadOnlySpan<float> times, ReadOnlySpan<float> values, int n, float t, out Vector3 v)
    {
        v = default;
        if (!Segment(times, n, t, out int a, out float u)) return false;
        ReadOnlySpan<Vector3> p = MemoryMarshal.Cast<float, Vector3>(values);
        bool loop = p[0] == p[n - 1];
        float h = times[a + 1] - times[a];
        Vector3 d = (p[a + 1] - p[a]) / h;
        Vector3 m0 = Before(times, n, a, loop, out int i, out float hb) ? Slope((p[a] - p[i]) / hb, hb, d, h) : d;
        Vector3 m1 = After(times, n, a + 1, loop, out int j, out float ha) ? Slope(d, h, (p[j] - p[a + 1]) / ha, ha) : d;
        float u2 = u * u, u3 = u2 * u;
        v = (2f * u3 - 3f * u2 + 1f) * p[a] + (u3 - 2f * u2 + u) * h * m0 + (3f * u2 - 2f * u3) * p[a + 1] + (u3 - u2) * h * m1;
        return true;
    }

    /// <summary>A rotation track's curve at <paramref name="t"/> (keys x, y, z, w), or false where the engine's own
    /// value stands. Squad's inner points are placed from each end's spin, so the curve leaves and reaches its keys
    /// at those spins; spins of the segment's own even pace give back the engine's slerp exactly.</summary>
    internal static bool Turn(ReadOnlySpan<float> times, ReadOnlySpan<float> values, int n, float t, out Quaternion q)
    {
        q = default;
        if (!Segment(times, n, t, out int a, out float u)) return false;
        ReadOnlySpan<Quaternion> k = MemoryMarshal.Cast<float, Quaternion>(values);
        bool loop = MathF.Abs(Quaternion.Dot(k[0], k[n - 1])) >= 1f - 1e-6f;
        float h = times[a + 1] - times[a];
        Quaternion q0 = Quaternion.Normalize(k[a]), q1 = Quaternion.Normalize(k[a + 1]);
        if (Quaternion.Dot(q0, q1) < 0f) q1 = Quaternion.Negate(q1);
        Quaternion back0 = Quaternion.Conjugate(q0), back1 = Quaternion.Conjugate(q1);
        Vector3 l = Log(back0 * q1);
        Vector3 w0 = Before(times, n, a, loop, out int i, out float hb) ? Spin(Log(back0 * k[i]), hb, l, h) : l / h;
        Vector3 w1 = After(times, n, a + 1, loop, out int j, out float ha) ? Spin(-l, h, Log(back1 * k[j]), ha) : l / h;
        Quaternion s0 = q0 * Exp(0.5f * (w0 * h - l)), s1 = q1 * Exp(0.5f * (l - w1 * h));
        q = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Slerp(q0, q1, u), Quaternion.Slerp(s0, s1, u), 2f * u * (1f - u)));
        return true;
    }

    /// <summary>The engine's own key search (<c>GetKeyIndexAndAlpha</c>): the segment from key <paramref name="a"/>
    /// that <paramref name="t"/> falls in, and how far along. False under three keys or at or past an end key,
    /// where the engine holds that key.</summary>
    private static bool Segment(ReadOnlySpan<float> times, int n, float t, out int a, out float u)
    {
        int b = 0;
        while (b < n && t > times[b]) b++;
        a = b - 1;
        u = a >= 0 && b < n ? (t - times[a]) / (times[b] - times[a]) : 0f;
        return n >= 3 && b > 0 && b < n;
    }

    /// <summary>The key before key <paramref name="k"/>, and the time between them: on a loop, key 0's is the
    /// second-to-last. False at a plain track's start, or when the two keys share a time.</summary>
    private static bool Before(ReadOnlySpan<float> times, int n, int k, bool loop, out int i, out float h)
    {
        i = k > 0 ? k - 1 : loop ? n - 2 : -1;
        h = i < 0 ? 0f : k > 0 ? times[k] - times[i] : times[n - 1] - times[i];
        return h > SameTime;
    }

    /// <summary>The key after key <paramref name="k"/>, and the time between them: on a loop, the last key's is
    /// key 1. False at a plain track's end, or when the two keys share a time.</summary>
    private static bool After(ReadOnlySpan<float> times, int n, int k, bool loop, out int j, out float h)
    {
        j = k < n - 1 ? k + 1 : loop ? 1 : -1;
        h = j < 0 ? 0f : k < n - 1 ? times[j] - times[k] : times[1] - times[0];
        return h > SameTime;
    }

    /// <summary>A key's slope from the slopes into it (<paramref name="d0"/>, over <paramref name="h0"/> seconds)
    /// and out of it, per axis (Fritsch-Butland): zero where the track stops or turns back there, else their
    /// time-weighted harmonic mean, which never exceeds three times the smaller -- so the cubic stays monotone.</summary>
    internal static Vector3 Slope(Vector3 d0, float h0, Vector3 d1, float h1) =>
        new(Slope(d0.X, h0, d1.X, h1), Slope(d0.Y, h0, d1.Y, h1), Slope(d0.Z, h0, d1.Z, h1));

    private static float Slope(float d0, float h0, float d1, float h1)
    {
        if (d0 * d1 <= 0f) return 0f;
        float w0 = 2f * h1 + h0, w1 = h1 + 2f * h0;
        return (w0 + w1) / (w0 / d0 + w1 / d1);
    }

    /// <summary>The spin (rad/s, in the key's own frame) a rotation track passes a key with, from the arc back to the
    /// previous key and the arc on to the next: the time-weighted mean of the two, zero where the track stops or
    /// turns back there, and never over three times the slower side, so a near-hold is not overshot.</summary>
    internal static Vector3 Spin(Vector3 back, float hb, Vector3 on, float ho)
    {
        Vector3 vb = -back / hb, vo = on / ho;
        if (Vector3.Dot(vb, vo) <= 0f) return Vector3.Zero;
        Vector3 w = (ho * vb + hb * vo) / (hb + ho);
        float cap = 3f * MathF.Sqrt(MathF.Min(vb.LengthSquared(), vo.LengthSquared())), len = w.Length();
        return len > cap ? w * (cap / len) : w;
    }

    /// <summary>The curves on made-up tracks: a hold is never overshot, every key is passed through, evenly paced
    /// keys in a line give the engine's own straight segment, monotone keys give a monotone curve, and a loop is as
    /// smooth across its seam as anywhere else; rotations stay unit and ignore a key's sign.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        float* t = stackalloc float[8];
        float* p = stackalloc float[8 * 3];
        float* q = stackalloc float[8 * 4];
        Span<Quaternion> qk = new(q, 8);

        Keys(t, 0f, 1f, 2f, 3f);
        Along(p, 0f, 1f, 1f, 0f);
        bool held = true;
        for (float x = 0.01f; x < 3f; x += 0.01f)
        {
            if (!Position(new(t, 8), new(p, 24), 4, x, out Vector3 v)) { held = false; break; }
            held &= v.X <= 1f + 1e-5f && v.X >= -1e-5f && (x < 1f || x > 2f || MathF.Abs(v.X - 1f) < 1e-5f);
        }
        check.Expect(held, "positions: a hold between two equal keys stays put, and the curve never passes it");

        Keys(t, 0f, 0.3f, 0.5f, 1.4f, 2f);
        Along(p, 0f, 2f, 2.1f, 5f, 5.2f);
        bool through = true, rising = true;
        float last = 0f;
        for (int k = 1; k < 5; k++) through &= Position(new(t, 8), new(p, 24), 5, t[k], out Vector3 v) && MathF.Abs(v.X - p[3 * k]) < 1e-5f;
        for (float x = 0.005f; x < 2f; x += 0.005f)
        {
            if (!Position(new(t, 8), new(p, 24), 5, x, out Vector3 v)) { rising = false; break; }
            rising &= v.X >= last - 1e-5f;
            last = v.X;
        }
        check.Expect(through && rising, "positions: the curve passes through every key, and rising keys at uneven times give a curve that only rises");

        Keys(t, 0f, 0.5f, 1f, 1.5f, 2f);
        for (int k = 0; k < 5; k++) { p[3 * k] = k; p[3 * k + 1] = -2f * k; p[3 * k + 2] = 0.5f * k; }
        bool straight = true;
        for (float x = 0.01f; x < 2f; x += 0.01f)
            straight &= Position(new(t, 8), new(p, 24), 5, x, out Vector3 v) && Vector3.Distance(v, new Vector3(2f * x, -4f * x, x)) < 1e-4f;
        check.Expect(straight, "positions: evenly paced keys in a line give the engine's own straight segment");

        Keys(t, 0f, 1f, 2f, 3f, 4f);
        Along(p, 0f, 2f, 0f, -1f, 0f);
        bool seam = Position(new(t, 8), new(p, 24), 5, 3.999f, out Vector3 end) && Position(new(t, 8), new(p, 24), 5, 0.001f, out Vector3 start)
                    && MathF.Abs((0f - end.X) / 0.001f - start.X / 0.001f) < 0.01f;
        check.Expect(seam, "positions: a loop leaves its last key at the speed it enters its first");

        Keys(t, 0f, 1f, 2f, 3f);
        Spun(qk, 0f, 0.5f, 0.5f, 0f);
        bool turnHeld = true;
        for (float x = 0.01f; x < 3f; x += 0.01f)
        {
            if (!Turn(new(t, 8), new(q, 32), 4, x, out Quaternion r)) { turnHeld = false; break; }
            if (r.W < 0f) r = Quaternion.Negate(r);
            float a = 2f * MathF.Atan2(r.Z, r.W);
            turnHeld &= MathF.Abs(r.Length() - 1f) < 1e-4f && a <= 0.5f + 1e-4f && a >= -1e-4f
                         && (x < 1f || x > 2f || MathF.Abs(a - 0.5f) < 1e-4f);
        }
        check.Expect(turnHeld, "rotations: stay unit, hold between two equal keys, and never turn past the hold");

        Keys(t, 0f, 0.4f, 0.6f, 1.5f, 2f);
        Spun(qk, 0f, 0.3f, 1.1f, 1.2f, 2f);
        Quaternion* flip = stackalloc Quaternion[5];
        for (int k = 0; k < 5; k++) flip[k] = k % 2 == 1 ? Quaternion.Negate(qk[k]) : qk[k];
        bool keysHit = true, signFree = true;
        for (int k = 1; k < 5; k++)
            keysHit &= Turn(new(t, 8), new(q, 32), 5, t[k], out Quaternion r) && MathF.Abs(Quaternion.Dot(r, qk[k])) > 1f - 1e-5f;
        for (float x = 0.01f; x < 2f; x += 0.01f)
            signFree &= Turn(new(t, 8), new(q, 32), 5, x, out Quaternion a) && Turn(new(t, 8), new((float*)flip, 20), 5, x, out Quaternion b)
                        && MathF.Abs(Quaternion.Dot(a, b)) > 1f - 1e-5f;
        check.Expect(keysHit && signFree, "rotations: squad passes through every key and does not care which sign a key is stored with");

        Keys(t, 0f, 0.5f, 1f, 1.5f, 2f);
        Spun(qk, 0f, 0.3f, 0.6f, 0.9f, 1.2f);
        bool slerped = true;
        for (float x = 0.01f; x < 2f; x += 0.01f)
            slerped &= Turn(new(t, 8), new(q, 32), 5, x, out Quaternion r) && MathF.Abs(Quaternion.Dot(r, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f * x))) > 1f - 1e-6f;
        check.Expect(slerped, "rotations: an even spin about one axis gives the engine's own slerp");

        check.Expect(!Position(new(t, 8), new(p, 24), 2, 0.5f, out _) && !Position(new(t, 8), new(p, 24), 5, 0f, out _) && !Position(new(t, 8), new(p, 24), 5, 3f, out _),
                     "a track under three keys, and a time at or past an end key, keep the engine's value");
        log.Info(check.Ok ? "KeyCurves.SelfCheck: passed" : "KeyCurves.SelfCheck: FAILED");
    }

    private static void Keys(float* t, params ReadOnlySpan<float> at) => at.CopyTo(new Span<float>(t, at.Length));

    /// <summary>Vector keys with <paramref name="x"/> on X and the other axes at rest.</summary>
    private static void Along(float* p, params ReadOnlySpan<float> x)
    {
        for (int k = 0; k < x.Length; k++) { p[3 * k] = x[k]; p[3 * k + 1] = p[3 * k + 2] = 0f; }
    }

    /// <summary>Rotation keys turned <paramref name="a"/> radians about Z, stored x, y, z, w.</summary>
    private static void Spun(Span<Quaternion> q, params ReadOnlySpan<float> a)
    {
        for (int k = 0; k < a.Length; k++) q[k] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, a[k]);
    }
}
