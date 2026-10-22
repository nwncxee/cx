using System;
using System.Numerics;

namespace CX.Alloy.Physics;

/// <summary>What one frame of <see cref="DanglyJoints.Step"/> is driven by: the part's world transform, the
/// air (mean wind and point-source push, m/s; the air clock; how much gust and turbulence to apply), the frame
/// time, the knobs, already read, the static faces a Swing part keeps clear of, and who wears it.</summary>
public readonly struct DanglyInputs
{
    public readonly Matrix4x4 PartWorld;
    public readonly Vector3 Mean, Point;
    public readonly float Time, Field, Dt, WindGain, Tilt, Cage;
    public readonly DanglyColliders? Walls;
    /// <summary>A creature wears the part: a turning rig on it still swings, but no further than the cage allows.
    /// Clothing authors a few centimetres of give; a free pendulum on a walking body throws it half a metre.</summary>
    public readonly bool Worn;
    /// <summary>The capsules a worn strand keeps out of, or null for none.</summary>
    public readonly DanglyBody? Body;
    /// <summary>How freely a worn strand swings: its spring is <c>StrandSpring / Strand</c>; 0 cages it like
    /// clothing.</summary>
    public readonly float Strand;

    public DanglyInputs(Matrix4x4 partWorld, Vector3 mean, Vector3 point, float time, float field, float dt,
                        float windGain, float tilt, float cage, DanglyColliders? walls = null, bool worn = false,
                        DanglyBody? body = null, float strand = 1f)
    {
        PartWorld = partWorld; Mean = mean; Point = point; Time = time; Field = field; Dt = dt;
        WindGain = windGain; Tilt = tilt; Cage = cage; Walls = walls; Worn = worn; Body = body; Strand = strand;
    }
}

/// <summary>One part's joint state: world positions and velocities of every joint, the node-space positions
/// its bones were last published at, and the previous pose the teleport test reads.</summary>
public sealed class DanglyJointsState
{
    public readonly Vector3[] X, V, Published;
    /// <summary>The largest node-space joint move the last publishing <see cref="DanglyJoints.Step"/> carried,
    /// in node units like the stock solve's vertex move; infinite on a seed or a teleport.</summary>
    public float Moved;
    public Vector3 PrevPos;
    public Quaternion PrevRot;
    public bool Seeded;
    /// <summary>The uniform scale the previous pose carried: a target's own velocity is taken against the
    /// whole previous transform, scale included.</summary>
    public float PrevScale = 1f;

    public DanglyJointsState(int joints)
    {
        X = new Vector3[joints]; V = new Vector3[joints]; Published = new Vector3[joints];
    }
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

    /// <summary>Settled: no joint is further than this (node units) from where its bones were last published,
    /// and none is faster relative to the part. Against the publish, so a creep cannot drift unseen.</summary>
    private const float QuietPos = 1e-4f, QuietVel = 1e-3f;

    /// <summary>Length-constraint iterations, and the strength a neighbouring column pulls at.</summary>
    private const int Iterations = 2;
    private const float NeighbourStrength = 0.5f;

    /// <summary>A Swing chain's own friction (1/s), on each joint's velocity relative to its parent's. The
    /// top-down length sweep moves only the child, which pumps energy into the chain's sideways modes until an
    /// edge-on strip flails; this drains them while hardly touching the whole chain's swing.</summary>
    private const float ChainFriction = 2f;

    /// <summary>A worn strand's pull back to its authored hang (rad/s at strand 1), its swing damping (1/s, on
    /// the motion relative to the wearer) and its cone (60 degrees): hair has a shape and air to move through,
    /// where a free pendulum on a turning body flails.</summary>
    private const float StrandSpring = 3f, StrandDamping = 2.5f, StrandCone = 1.0471976f;

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
        Span<Vector3> from = stackalloc Vector3[DanglyRig.MaxJoints];
        for (int j = 0; j < nj; j++) target[j] = Vector3.Transform(rig.JointRest[j], world);

        if (!s.Seeded || (pos - s.PrevPos).Length() > TeleportDist)
        {
            for (int j = 0; j < nj; j++) { s.X[j] = target[j]; s.V[j] = default; }
            s.Seeded = true;
            Remember(s, pos, scale, world);
            Span<Vector3> seed = stackalloc Vector3[DanglyRig.MaxJoints];
            Local(s, world, nj, seed);
            Publish(s, nj, seed, float.PositiveInfinity);
            WriteBones(rig, seed, nb, nj, bones);
            return true;
        }

        Matrix4x4 before = Matrix4x4.CreateScale(s.PrevScale) * Matrix4x4.CreateFromQuaternion(s.PrevRot)
                         * Matrix4x4.CreateTranslation(s.PrevPos);
        Remember(s, pos, scale, world);
        Span<Vector3> air = stackalloc Vector3[DanglyRig.MaxJoints];
        for (int j = 0; j < nj; j++)
        {
            from[j] = dt > 0f ? Vector3.Transform(rig.JointRest[j], before) : target[j];
            drift[j] = dt > 0f ? (target[j] - from[j]) / dt : Vector3.Zero;
            air[j] = rig.JointParent[j] < 0 ? Vector3.Zero
                   : DanglyAir.At(target[j], inp.Time, inp.Mean, inp.Field) + inp.Point;
        }

        // The solve runs before the settled test, never instead of it: a frame skipped on a stale reading
        // would be a part the wind never reached. Settled is "nothing new to publish", not "back at rest": a
        // part leaning still in a steady wind is settled.
        Integrate(rig, s, inp, nj, dt, scale, rot, rotT, from, target, drift, air);
        Span<Vector3> local = stackalloc Vector3[DanglyRig.MaxJoints];
        Local(s, world, nj, local);
        bool quiet = true;
        float moveSq = 0f;
        for (int j = 0; j < nj; j++)
        {
            if (rig.JointParent[j] < 0) continue;
            float d = (local[j] - s.Published[j]).LengthSquared();
            moveSq = MathF.Max(moveSq, d);
            quiet &= d <= QuietPos * QuietPos && (s.V[j] - drift[j]).LengthSquared() <= QuietVel * QuietVel;
        }
        WriteBones(rig, local, nb, nj, bones);
        if (quiet) return false;
        Publish(s, nj, local, MathF.Sqrt(moveSq));
        return true;
    }

    /// <summary>Every joint in the part's node space: what the bones are made of, and what settles.</summary>
    private static void Local(DanglyJointsState s, in Matrix4x4 world, int nj, Span<Vector3> local)
    {
        if (!Matrix4x4.Invert(world, out Matrix4x4 inv)) inv = Matrix4x4.Identity;
        for (int j = 0; j < nj; j++) local[j] = Vector3.Transform(s.X[j], inv);
    }

    private static void Publish(DanglyJointsState s, int nj, ReadOnlySpan<Vector3> local, float moved)
    {
        for (int j = 0; j < nj; j++) s.Published[j] = local[j];
        s.Moved = moved;
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

    private static void Remember(DanglyJointsState s, Vector3 pos, float scale, in Matrix4x4 world)
    {
        s.PrevPos = pos;
        s.PrevRot = Quaternion.CreateFromRotationMatrix(Basis(world, scale));
        s.PrevScale = scale;
    }

    /// <summary>The spec's force law over substeps of at most 1/120 s, each followed by the length constraints
    /// and the limits, and the velocity then taken from where they left each joint -- so a chain at full length
    /// or a joint on its limit stops there instead of storing the motion they refused. The targets travel from
    /// last frame's pose to this one's across the substeps, or a part moving steadily never settles.</summary>
    private static void Integrate(DanglyRig rig, DanglyJointsState s, in DanglyInputs inp, int nj, float dt,
                                  float scale, in Matrix4x4 rot, in Matrix4x4 rotT, ReadOnlySpan<Vector3> from,
                                  ReadOnlySpan<Vector3> target, ReadOnlySpan<Vector3> drift,
                                  ReadOnlySpan<Vector3> air)
    {
        bool pendulum = rig.Class == DanglyClass.Swing;
        bool strand = pendulum && rig.Strand && inp.Worn && inp.Strand > 0f;
        float spring = strand ? StrandSpring / inp.Strand : 0f;
        float om2 = pendulum ? spring * spring : rig.Omega * rig.Omega;
        float damp = strand ? StrandDamping : 2f * rig.Zeta * rig.Omega;
        float friction = pendulum ? ChainFriction : 0f;
        Vector3 fall = Gravity * inp.Tilt * (DownWorld - Vector3.TransformNormal(rig.RestDown, rot));
        if (pendulum) fall += Gravity * Vector3.TransformNormal(rig.Hang, rot);
        float k = rig.Air * inp.WindGain;
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / SubDt));
        float h = dt / steps;

        Span<Vector3> now = stackalloc Vector3[DanglyRig.MaxJoints];
        Span<Vector3> was = stackalloc Vector3[DanglyRig.MaxJoints];
        for (int step = 0; step < steps; step++)
        {
            float frac = (step + 1f) / steps;
            for (int j = 0; j < nj; j++) { now[j] = Vector3.Lerp(from[j], target[j], frac); was[j] = s.X[j]; }
            for (int j = 0; j < nj; j++)
            {
                int up = rig.JointParent[j];
                if (up < 0) { s.X[j] = now[j]; s.V[j] = drift[j]; continue; }
                Vector3 a = om2 * (now[j] - s.X[j]) - damp * (s.V[j] - drift[j]) - friction * (s.V[j] - s.V[up])
                          + fall;
                if (k > 0f)
                {
                    Quaternion turn = rig.Shear ? Quaternion.Identity
                                    : Arc(now[j] - now[up], s.X[j] - s.X[up]);
                    Vector3 face = Facing(rig, j, air[j] - s.V[j], turn, rot, rotT);
                    a += k * face.Length() * face;
                }
                s.V[j] += a * h;
                s.X[j] += s.V[j] * h;
                // A NaN passes the limits as "inside" and would reach the bone palette.
                if (!IsFinite(s.X[j]) || !IsFinite(s.V[j])) { s.X[j] = now[j]; s.V[j] = default; }
            }
            if (pendulum) Constrain(rig, s, scale, now, inp.Walls, was, nj, strand ? StrandCone : rig.Cone);
            else if (!rig.Shear) Cone(rig, s, nj, now);
            if (strand) inp.Body?.Push(s.X, now, rig.JointParent, nj);
            else if (rig.Shear || inp.Worn) Cage(rig, s, inp.Cage, scale, nj, now);
            if (h > 0f)
                for (int j = 0; j < nj; j++)
                    if (rig.JointParent[j] >= 0) s.V[j] = (s.X[j] - was[j]) / h;
        }
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>The air speed a joint's faces catch: <paramref name="wind"/> (world) through its drag tensor, the
    /// tensor turned with the segment by <paramref name="turn"/>, so a banner lifting edge-on catches less.</summary>
    private static Vector3 Facing(DanglyRig rig, int j, Vector3 wind, Quaternion turn, in Matrix4x4 rot,
                                  in Matrix4x4 rotT)
        => Vector3.Transform(Tensor(rig, j, Vector3.Transform(wind, Quaternion.Conjugate(turn)), rot, rotT), turn);

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

    /// <summary>Swing's constraints: every segment turned back inside its cone and put onto its parent at its
    /// rest length, which one top-down sweep settles exactly for a single column, then the neighbouring columns
    /// back to their rest spacing at half strength, then every joint back out of the faces it keeps clear of.</summary>
    private static void Constrain(DanglyRig rig, DanglyJointsState s, float scale, ReadOnlySpan<Vector3> target,
                                  DanglyColliders? walls, ReadOnlySpan<Vector3> was, int nj, float cone)
    {
        int k = rig.Columns, j = rig.Segments;
        float limit = MathF.Cos(cone);
        for (int it = 0; it < Iterations; it++)
        {
            for (int c = 0; c < k; c++)
                for (int seg = 1; seg <= j; seg++)
                {
                    int at = c * (j + 1) + seg, up = at - 1;
                    float rest = (rig.JointRest[at] - rig.JointRest[up]).Length() * scale;
                    Vector3 d = Within(s.X[at] - s.X[up], target[at] - target[up], limit);
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
            walls?.Push(s.X, was, nj);
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

    /// <summary>The bend limit for a turning Sway: its segment turned back inside the cone, at its own length.</summary>
    private static void Cone(DanglyRig rig, DanglyJointsState s, int nj, ReadOnlySpan<Vector3> target)
    {
        float limit = MathF.Cos(rig.Cone);
        for (int j = 0; j < nj; j++)
        {
            int up = rig.JointParent[j];
            if (up >= 0) s.X[j] = s.X[up] + Within(s.X[j] - s.X[up], target[j] - target[up], limit);
        }
    }

    /// <summary><paramref name="cur"/>, turned toward <paramref name="rest"/> just far enough to sit inside the
    /// cone of half-angle <c>acos(<paramref name="cosLimit"/>)</c> about it; its length is kept.</summary>
    private static Vector3 Within(Vector3 cur, Vector3 rest, float cosLimit)
    {
        float lr = rest.Length(), lc = cur.Length();
        if (lr < Tiny || lc < Tiny) return cur;
        Vector3 a = rest / lr, b = cur / lc;
        float cos = Vector3.Dot(a, b);
        if (cos >= cosLimit) return cur;
        Vector3 side = b - a * cos;
        float ls = side.Length();
        if (ls < Tiny) return cur;
        return (a * cosLimit + side * (MathF.Sqrt(1f - cosLimit * cosLimit) / ls)) * lc;
    }

    /// <summary>The bone matrices: node space, row-vector, each mapping its vertices' REST positions to where
    /// the joints have taken them. A shear is a pure translation; a segment turns the rest segment onto the
    /// current one about its root.</summary>
    private static void WriteBones(DanglyRig rig, ReadOnlySpan<Vector3> local, int nb, int nj, Span<Matrix4x4> bones)
    {
        if (nb <= 0) return;
        bones[0] = Matrix4x4.Identity;

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
            bones[b] = Matrix4x4.CreateTranslation(-rig.JointRest[up])
                     * Matrix4x4.CreateFromQuaternion(Arc(was, now))
                     * Matrix4x4.CreateTranslation(local[up]);
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
            // Parallel too: normalising the same vector twice can leave dot just under the identity test.
            if (dot > 0f) return Quaternion.Identity;
            Vector3 any = MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(a, any)), MathF.PI);
        }
        return Quaternion.CreateFromAxisAngle(axis / al, MathF.Acos(dot));
    }
}
