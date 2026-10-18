using System;
using System.Numerics;

namespace CX.Alloy.Physics;

/// <summary>What one frame of <see cref="DanglyJoints.Step"/> is driven by: the part's world transform, the
/// wind at the part (world, the engine's units), the frame time and the knobs, already read.</summary>
public readonly struct DanglyInputs
{
    public readonly Matrix4x4 PartWorld;
    public readonly Vector3 Wind;
    public readonly float Dt, WindGain, Tilt, Drag, Cage;

    public DanglyInputs(Matrix4x4 partWorld, Vector3 wind, float dt, float windGain, float tilt, float drag, float cage)
    {
        PartWorld = partWorld; Wind = wind; Dt = dt; WindGain = windGain; Tilt = tilt; Drag = drag; Cage = cage;
    }
}

/// <summary>One part's joint state, in world space: positions and velocities of every joint, the previous
/// pose the teleport test reads, and whether the part has settled.</summary>
public sealed class DanglyJointsState
{
    public readonly Vector3[] X, V;
    public Vector3 PrevPos;
    public Quaternion PrevRot;
    public bool Seeded, Settled;
    /// <summary>The uniform scale the previous pose carried: a target's own velocity is taken against the
    /// whole previous transform, scale included.</summary>
    public float PrevScale = 1f;
    /// <summary>Last frame's inputs, so an unchanged frame on a settled part can publish nothing.</summary>
    public Matrix4x4 LastWorld;
    public Vector3 LastWind;
    public float LastGain, LastTilt, LastDrag, LastCage;

    public DanglyJointsState(int joints) { X = new Vector3[joints]; V = new Vector3[joints]; }
}

/// <summary>The joint solver Sway, Jiggle and Swing share (docs/spec/dangly-motions.md, section 5).</summary>
public static class DanglyJoints
{
    /// <summary>The longest substep the integrator takes.</summary>
    internal const float SubDt = 1f / 120f;

    /// <summary>A frame longer than this is a hitch, not motion: the solve advances by this much and the rest
    /// of the wall time is dropped.</summary>
    private const float MaxFrameDt = 0.1f;

    /// <summary>A world unit of part movement in a frame is a teleport, not a swing.</summary>
    private const float TeleportDist = 1f;

    /// <summary>Metres per second squared: NWN world units ARE metres (a tile is 10 by 10, a human creature a
    /// little under 2 tall), so the figure needs no conversion.</summary>
    private const float Gravity = 9.81f;

    /// <summary>Settled: every joint this close to its target and this slow.</summary>
    private const float QuietPos = 1e-4f, QuietVel = 1e-3f;

    /// <summary>Length-constraint iterations, and the strength a neighbouring column pulls at.</summary>
    private const int Iterations = 2;
    private const float NeighbourStrength = 0.5f;

    private const float Tiny = 1e-6f;

    private static readonly Vector3 DownWorld = -Vector3.UnitZ;

    /// <summary>Advance <paramref name="s"/> by one frame and write <see cref="DanglyRig.BoneCount"/> node-space
    /// bone matrices into <paramref name="bones"/>. False when the part is settled and nothing changed, so the
    /// caller publishes nothing.</summary>
    public static bool Step(DanglyRig rig, DanglyJointsState s, in DanglyInputs inp, Span<Matrix4x4> bones)
    {
        int nj = rig.JointRest.Length;
        int nb = Math.Min(rig.BoneCount, bones.Length);
        Matrix4x4 world = inp.PartWorld;
        float scale = new Vector3(world.M11, world.M12, world.M13).Length();
        if (nj == 0 || nj > DanglyRig.MaxJoints || s.X.Length < nj || !(scale > Tiny)
            || rig.JointParent.Length < nj || rig.JointCap.Length < nj)
        {
            for (int b = 0; b < nb; b++) bones[b] = Matrix4x4.Identity;
            return false;
        }

        var pos = new Vector3(world.M41, world.M42, world.M43);
        Matrix4x4 rot = Basis(world, scale);
        Matrix4x4 rotT = Matrix4x4.Transpose(rot);
        float dt = float.IsFinite(inp.Dt) ? Math.Clamp(inp.Dt, 0f, MaxFrameDt) : 0f;

        Span<Vector3> target = stackalloc Vector3[DanglyRig.MaxJoints];
        Span<Vector3> drift = stackalloc Vector3[DanglyRig.MaxJoints];
        for (int j = 0; j < nj; j++) target[j] = Vector3.Transform(rig.JointRest[j], world);

        if (!s.Seeded || (pos - s.PrevPos).Length() > TeleportDist)
        {
            for (int j = 0; j < nj; j++) { s.X[j] = target[j]; s.V[j] = default; }
            s.Seeded = true;
            s.Settled = false;
            Remember(s, inp, pos, scale);
            WriteBones(rig, s, world, nb, nj, bones);
            return true;
        }

        bool same = s.LastWorld == world && s.LastWind == inp.Wind && s.LastGain == inp.WindGain
                 && s.LastTilt == inp.Tilt && s.LastDrag == inp.Drag && s.LastCage == inp.Cage;
        Matrix4x4 before = Matrix4x4.CreateScale(s.PrevScale) * Matrix4x4.CreateFromQuaternion(s.PrevRot)
                         * Matrix4x4.CreateTranslation(s.PrevPos);
        Remember(s, inp, pos, scale);
        for (int j = 0; j < nj; j++)
            drift[j] = dt > 0f ? (target[j] - Vector3.Transform(rig.JointRest[j], before)) / dt : Vector3.Zero;

        // The solve runs before the settled test, never instead of it: a part standing in a steady wind is
        // not moving, and a frame skipped on that reading would be a part the wind never reached.
        Integrate(rig, s, inp, nj, dt, scale, rot, rotT, target, drift);
        bool quiet = true;
        for (int j = 0; j < nj && quiet; j++)
        {
            if (rig.JointParent[j] < 0) continue;
            quiet = (s.X[j] - target[j]).LengthSquared() <= QuietPos * QuietPos
                 && s.V[j].LengthSquared() <= QuietVel * QuietVel;
        }
        s.Settled = quiet && same;
        WriteBones(rig, s, world, nb, nj, bones);
        return !s.Settled;
    }

    /// <summary>The part's rotation alone, with its uniform scale and its translation taken out.</summary>
    private static Matrix4x4 Basis(in Matrix4x4 world, float scale)
    {
        float k = 1f / scale;
        return new Matrix4x4(world.M11 * k, world.M12 * k, world.M13 * k, 0f,
                             world.M21 * k, world.M22 * k, world.M23 * k, 0f,
                             world.M31 * k, world.M32 * k, world.M33 * k, 0f,
                             0f, 0f, 0f, 1f);
    }

    private static void Remember(DanglyJointsState s, in DanglyInputs inp, Vector3 pos, float scale)
    {
        s.PrevPos = pos;
        s.PrevRot = Quaternion.CreateFromRotationMatrix(Basis(inp.PartWorld, scale));
        s.PrevScale = scale;
        s.LastWorld = inp.PartWorld;
        s.LastWind = inp.Wind;
        s.LastGain = inp.WindGain;
        s.LastTilt = inp.Tilt;
        s.LastDrag = inp.Drag;
        s.LastCage = inp.Cage;
    }

    /// <summary>The spec's force law over substeps of at most 1/120 s, each followed by the length constraints
    /// and the cage.</summary>
    private static void Integrate(DanglyRig rig, DanglyJointsState s, in DanglyInputs inp, int nj, float dt,
                                  float scale, in Matrix4x4 rot, in Matrix4x4 rotT,
                                  ReadOnlySpan<Vector3> target, ReadOnlySpan<Vector3> drift)
    {
        float om2 = rig.Omega * rig.Omega, damp = 2f * rig.Zeta * rig.Omega;
        Vector3 fall = Gravity * inp.Tilt * (DownWorld - Vector3.TransformNormal(rig.RestDown, rot));
        bool windy = inp.WindGain > 0f && inp.Wind != Vector3.Zero;
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / SubDt));
        float h = dt / steps;

        for (int j = 0; j < nj; j++)
            if (rig.JointParent[j] < 0) { s.X[j] = target[j]; s.V[j] = drift[j]; }

        for (int step = 0; step < steps; step++)
        {
            for (int j = 0; j < nj; j++)
            {
                if (rig.JointParent[j] < 0) continue;
                Vector3 a = om2 * (target[j] - s.X[j]) - damp * (s.V[j] - drift[j]) + fall;
                if (windy)
                {
                    Vector3 lean = Tensor(rig, j, inp.Wind, rot, rotT);
                    float mag = lean.Length();
                    if (mag > Tiny)
                        a += lean * (om2 * rig.JointCap[j] * scale * inp.WindGain
                                     * MathF.Tanh(rig.WindGain * mag) / mag);
                }
                if (inp.Drag > 0f) a -= inp.Drag * Tensor(rig, j, s.V[j], rot, rotT);
                s.V[j] += a * h;
                s.X[j] += s.V[j] * h;
                // A NaN passes the cage as "inside" and would reach the bone palette.
                if (!IsFinite(s.X[j]) || !IsFinite(s.V[j])) { s.X[j] = target[j]; s.V[j] = default; }
            }
            if (rig.Class == DanglyClass.Swing) Constrain(rig, s, scale);
            Cage(rig, s, inp.Cage, scale, nj, target);
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>The joint's drag tensor applied to a world vector: into the node frame the tensor was measured
    /// in, through it, and back.</summary>
    private static Vector3 Tensor(DanglyRig rig, int j, Vector3 world, in Matrix4x4 rot, in Matrix4x4 rotT)
    {
        if (j >= rig.DragDiag.Length || j >= rig.DragOff.Length) return world;
        Vector3 v = Vector3.TransformNormal(world, rotT);
        Vector3 d = rig.DragDiag[j], o = rig.DragOff[j];
        var n = new Vector3(d.X * v.X + o.X * v.Y + o.Y * v.Z,
                            o.X * v.X + d.Y * v.Y + o.Z * v.Z,
                            o.Y * v.X + o.Z * v.Y + d.Z * v.Z);
        return Vector3.TransformNormal(n, rot);
    }

    /// <summary>Swing's length constraints: every segment back onto its parent at its rest length, which one
    /// top-down sweep settles exactly for a single column, then the neighbouring columns back to their rest
    /// spacing at half strength.</summary>
    private static void Constrain(DanglyRig rig, DanglyJointsState s, float scale)
    {
        int k = rig.Columns, j = rig.Segments;
        for (int it = 0; it < Iterations; it++)
        {
            for (int c = 0; c < k; c++)
                for (int seg = 1; seg <= j; seg++)
                {
                    int at = c * (j + 1) + seg, up = at - 1;
                    float rest = (rig.JointRest[at] - rig.JointRest[up]).Length() * scale;
                    Vector3 d = s.X[at] - s.X[up];
                    float len = d.Length();
                    if (len > Tiny && rest > Tiny) s.X[at] = s.X[up] + d * (rest / len);
                }
            for (int c = 0; c + 1 < k; c++)
                for (int seg = 1; seg <= j; seg++)
                {
                    int a = c * (j + 1) + seg, b = (c + 1) * (j + 1) + seg;
                    float rest = (rig.JointRest[b] - rig.JointRest[a]).Length() * scale;
                    Vector3 d = s.X[b] - s.X[a];
                    float len = d.Length();
                    if (len <= Tiny || rest <= Tiny) continue;
                    Vector3 fix = d * ((rest - len) / len * NeighbourStrength * 0.5f);
                    s.X[a] -= fix;
                    s.X[b] += fix;
                }
        }
    }

    /// <summary>The cage: no joint may leave its own authored excursion, scaled by the knob and the part.</summary>
    private static void Cage(DanglyRig rig, DanglyJointsState s, float knob, float scale, int nj,
                             ReadOnlySpan<Vector3> target)
    {
        for (int j = 0; j < nj; j++)
        {
            if (rig.JointParent[j] < 0) continue;
            float cap = knob * rig.JointCap[j] * scale;
            if (!(cap >= 0f)) continue;
            Vector3 off = s.X[j] - target[j];
            float len = off.Length();
            if (len > cap && len > Tiny) s.X[j] = target[j] + off * (cap / len);
        }
    }

    /// <summary>The bone matrices: node space, row-vector, each mapping its vertices' REST positions to where
    /// the joints have taken them. A shear is a pure translation; a segment turns the rest segment onto the
    /// current one about its root, and a stretchy one then lands its tip exactly.</summary>
    private static void WriteBones(DanglyRig rig, DanglyJointsState s, in Matrix4x4 world, int nb, int nj,
                                   Span<Matrix4x4> bones)
    {
        if (nb <= 0) return;
        bones[0] = Matrix4x4.Identity;
        if (nb == 1) return;
        if (!Matrix4x4.Invert(world, out Matrix4x4 inv))
        {
            for (int b = 1; b < nb; b++) bones[b] = Matrix4x4.Identity;
            return;
        }
        Span<Vector3> local = stackalloc Vector3[DanglyRig.MaxJoints];
        for (int j = 0; j < nj; j++) local[j] = Vector3.Transform(s.X[j], inv);

        for (int b = 1; b < nb; b++)
        {
            int j = b < rig.BoneJoint.Length ? rig.BoneJoint[b] : -1;
            if ((uint)j >= (uint)nj) { bones[b] = Matrix4x4.Identity; continue; }
            if (rig.Shear)
            {
                bones[b] = Matrix4x4.CreateTranslation(local[j] - rig.JointRest[j]);
                continue;
            }
            int up = rig.JointParent[j];
            if ((uint)up >= (uint)nj) { bones[b] = Matrix4x4.Identity; continue; }
            Vector3 was = rig.JointRest[j] - rig.JointRest[up], now = local[j] - local[up];
            Matrix4x4 m = Matrix4x4.CreateTranslation(-rig.JointRest[up])
                        * Matrix4x4.CreateFromQuaternion(Arc(was, now))
                        * Matrix4x4.CreateTranslation(local[up]);
            if (rig.Stretch)
            {
                Vector3 slack = local[j] - Vector3.Transform(rig.JointRest[j], m);
                m.M41 += slack.X; m.M42 += slack.Y; m.M43 += slack.Z;
            }
            bones[b] = m;
        }
    }

    /// <summary>The shortest-arc rotation that turns <paramref name="from"/> onto <paramref name="to"/>.</summary>
    private static Quaternion Arc(Vector3 from, Vector3 to)
    {
        float lf = from.Length(), lt = to.Length();
        if (lf < Tiny || lt < Tiny) return Quaternion.Identity;
        Vector3 a = from / lf, b = to / lt;
        float dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (dot > 0.9999999f) return Quaternion.Identity;
        Vector3 axis = Vector3.Cross(a, b);
        float al = axis.Length();
        if (al < 1e-7f)
        {
            Vector3 any = MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(a, any)), MathF.PI);
        }
        return Quaternion.CreateFromAxisAngle(axis / al, MathF.Acos(dot));
    }
}
