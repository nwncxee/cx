using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The dangly solve over DB columns: <c>Alloy/Scene/DanglyPhysics.cs</c>'s integrator, unchanged,
/// with its setup taken from records. The per-vertex arithmetic is a verbatim port (same gains, cage, body
/// proxy and turbulence phase); the rest positions come from the <see cref="MeshDef"/>, the writable copy is
/// the part's own dynamic buffer, and the per-instance phase is seeded from the part's INDEX, not its address.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>One world unit of movement in a frame is a teleport: snap flat to rest and skip the sim.</summary>
    private const float TeleportDist = 1.0f;
    private const float ConstraintMax = 255.0f;
    /// <summary>Vanilla's gains on the two terms it does have.</summary>
    private const float StiffnessGain = 0.5f, DampingGain = 1.5f;
    /// <summary>Runaway guard: the cage bounds position but not velocity.</summary>
    private const float MaxSpeed = 60f;
    /// <summary>Radians/s the ambient breeze direction wanders through.</summary>
    private const float BreezeTurn = 0.05f;
    /// <summary>The engine's own <c>dt</c> clamp, which the substep derivation inherits.</summary>
    private const float MinDt = 0.0125f, MaxDt = 0.035f;

    /// <summary>Per part: the velocity buffer that rides with the dynamic positions, and the previous world
    /// pose the inertia term is taken against.</summary>
    private static readonly nint[] _velo = new nint[SceneDb.MaxParts];
    private static readonly Vector3[] _prevPos = new Vector3[SceneDb.MaxParts];
    private static readonly Quaternion[] _prevRot = new Quaternion[SceneDb.MaxParts];

    private static int _danglyParts, _danglyVerts;

    /// <summary>One dangly part's solve, driven from the part walk. Everything it reads is a column or an
    /// immutable <see cref="DanglyDef"/>, so there is no engine state and no liveness question. Wind and breeze
    /// apply only when <paramref name="windy"/>: the engine's GUI scenes (chargen, portraits) have none.</summary>
    private static void DanglySolve(int part, float dt, bool windy)
    {
        NodeDef? node = SceneDb.PartModel[part]?.Nodes[SceneDb.PartNode[part]];
        DanglyDef? dn = node?.Dangly;
        MeshDef? mesh = node?.Mesh;
        if (dn == null || mesh == null) return;
        int n = Math.Min(mesh.Positions.Length, dn.Constraints.Length);
        if (n <= 0) return;
        float* dst = DynTake(part, mesh.Positions);
        if (dst == null) return;
        float* vel = (float*)_velo[part];
        if (vel == null)
        {
            vel = (float*)NativeMemory.AllocZeroed((nuint)n * 3 * sizeof(float));
            _velo[part] = (nint)vel;
        }

        Matrix4x4 m = SceneDb.PartWorld[part];
        var wpos = new Vector3(m.M41, m.M42, m.M43);
        // UNIT, out of the unscaled basis: every term below (the inertia delta, the wind, the gravity) is
        // rotated by this and by its conjugate, and a quaternion of norm sqrt(scale) scales them all.
        Quaternion wrot = RotOf(m);

        Vector3 ppos = _prevPos[part];
        Quaternion prot = _prevRot[part];
        // Order is the engine's: teleport test FIRST, seed second. A fresh part has a zero previous pose, so
        // anywhere but the origin it takes the teleport branch on frame one -- and that copy is the only thing
        // that ever initialises the dynamic buffer.
        if ((wpos - ppos).Length() > TeleportDist)
        {
            fixed (Vector3* src = mesh.Positions)
                Buffer.MemoryCopy(src, dst, (long)n * 12, (long)n * 12);
            new Span<float>(vel, n * 3).Clear();
            _prevPos[part] = wpos; _prevRot[part] = wrot;
            _dynGen[part]++;
            _dynProduced++;
            DynPublish(part);
            SceneDb.TouchPart(part, Dirty.Anim);
            AlloyRenderer.NoteDanglyMoved(part, float.PositiveInfinity, Dangly.Still.V);
            return;
        }
        // A zero previous rotation means the pose was never written, and it would collapse every vertex onto
        // the part; seeding makes this frame's deltas identity instead. Vanilla's own guard.
        bool seeded = prot.X == 0f && prot.Y == 0f && prot.Z == 0f && prot.W == 0f;
        if (seeded) { ppos = wpos; prot = wrot; }

        float t = Clock();
        // This instance's own phase and rate, from the part INDEX: every instance of one model shares every
        // authored number, and a position hash would jump every frame on a walking character's cloak.
        ulong hash = Mix((ulong)(uint)part * 0x9e3779b97f4a7c15UL);
        float phaseOff = (hash & 0xffff) * (MathF.Tau / 65535f);
        float rateVar = 1f + ((((hash >> 16) & 0xffff) * (1f / 65535f)) - 0.5f) * 2f * Dangly.Vary.V;

        // The part's own motion, carried into every vertex once rather than per substep: this is where all the
        // inertia comes from. The inverse rotation is the conjugate, w negated as the engine's operator- does.
        Quaternion inv = new(wrot.X, wrot.Y, wrot.Z, -wrot.W);
        Quaternion d = Qmul(inv, prot);
        Vector3 tLocal = Qrot(new Quaternion(prot.X, prot.Y, prot.Z, -prot.W), ppos - wpos);

        Vector3 windW = Vector3.Zero;
        if (windy)
        {
            windW = AlloyWind.Owned ? AlloyWind.Wind(wpos, Math.Clamp(dt, MinDt, MaxDt)) : Vector3.Zero;
            if (!float.IsFinite(windW.X) || !float.IsFinite(windW.Y) || !float.IsFinite(windW.Z)) windW = Vector3.Zero;
            windW += new Vector3(MathF.Cos(t * BreezeTurn), MathF.Sin(t * BreezeTurn), 0f) * Dangly.Breeze.V;
        }
        Vector3 windL = Qrot(inv, windW) * Dangly.Wind.V;
        Vector3 gravL = Qrot(inv, new Vector3(0f, 0f, -Dangly.Gravity.V));
        Vector3 gust = Vector3.Cross(windL, Qrot(inv, Vector3.UnitZ));
        float gl = gust.Length();
        gust = gl > 1e-4f ? gust * (windL.Length() / gl) : Vector3.Zero;

        float hz = Dangly.Hz.V, turb = Dangly.Turb.V, turbFreq = Dangly.TurbFreq.V,
              turbSpeed = Dangly.TurbSpeed.V, cage = Dangly.Cage.V, body = Dangly.Body.V;
        int maxSteps = Dangly.Steps.V;
        float sdt = MathF.Min(dt, maxSteps / hz);
        int steps = Math.Clamp((int)MathF.Round(sdt * hz), 1, maxSteps);
        float h = sdt / steps;
        float disp = dn.Displacement, stiff = StiffnessGain * dn.Tightness, damp = DampingGain * dn.Period;

        // ponytail: the snapshot stage is game-thread state the motion sweep reads; under alloy.renderthread it
        // needs the arena's publish handoff (MeshArena.WriteDynamic) before that sweep may run off-thread.
        AlloyRenderer.NoteDanglyPrev(part, dst, n);
        float maxSq = 0f;
        fixed (Vector3* rest = mesh.Positions)
        fixed (float* cons = dn.Constraints)
        for (int i = 0; i < n; i++)
        {
            float c = cons[i];
            if (!(c > 0f)) continue;              // vanilla skips these outright; so must we

            int o = i * 3;
            Vector3 r = rest[i];
            Vector3 p = Qrot(d, new Vector3(dst[o], dst[o + 1], dst[o + 2]) + tLocal);
            Vector3 v = Qrot(d, new Vector3(vel[o], vel[o + 1], vel[o + 2]));

            // The authored constraint IS the pinning field: 255 is welded, 0 is skipped.
            float wgt = 1f - c / ConstraintMax;
            float phase = (r.X * 0.71f + r.Y * 1.31f + r.Z * 0.93f) * turbFreq
                        + phaseOff - t * turbSpeed * rateVar;
            float g = MathF.Sin(phase);
            float g2 = 1f - 2f * g * g;           // cos(2*phase), free: no second sine
            Vector3 acc = (windL * (1f + turb * g) + gust * (turb * g2) + gravL) * wgt;

            float k = c * stiff;
            for (int s = 0; s < steps; s++)
            {
                v += (acc + (r - p) * k - v * damp) * h;
                p += v * h;
            }

            float speed = v.Length();
            if (speed > MaxSpeed) v *= MaxSpeed / speed;

            // A constraint over 255 or a negative displacement (custom models) would invert Clamp's bounds.
            float capv = MathF.Max(disp * wgt * cage, 0f);
            Vector3 off = p - r;
            p = r + new Vector3(Math.Clamp(off.X, -capv, capv),
                                Math.Clamp(off.Y, -capv, capv),
                                Math.Clamp(off.Z, -capv, capv));
            if (body > 0f)
            {
                float minSq = (r.X * r.X + r.Y * r.Y) * body * body;
                float pSq = p.X * p.X + p.Y * p.Y;
                if (pSq < minSq && pSq > 1e-8f)
                {
                    float sc = MathF.Sqrt(minSq / pSq);
                    p.X *= sc; p.Y *= sc;
                    // Kill the INWARD radial velocity too, or the vertex buzzes against a wall it is pushed
                    // back out of every frame.
                    float iv = 1f / MathF.Sqrt(minSq);
                    float vr = (v.X * p.X + v.Y * p.Y) * iv;
                    if (vr < 0f) { v.X -= vr * p.X * iv; v.Y -= vr * p.Y * iv; }
                }
            }
            // NaN passes both clamps as "inside" and would land in the GPU buffer.
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) { p = r; v = default; }

            float ex = p.X - dst[o], ey = p.Y - dst[o + 1], ez = p.Z - dst[o + 2];
            maxSq = MathF.Max(maxSq, ex * ex + ey * ey + ez * ez);
            dst[o] = p.X; dst[o + 1] = p.Y; dst[o + 2] = p.Z;
            vel[o] = v.X; vel[o + 1] = v.Y; vel[o + 2] = v.Z;
        }

        _prevPos[part] = wpos; _prevRot[part] = wrot;
        _danglyParts++; _danglyVerts += n;
        AlloyRenderer.NoteDanglyMoved(part, MathF.Sqrt(maxSq), Dangly.Still.V);
        if (maxSq <= 0f && !seeded) return;
        _dynGen[part]++;
        _dynProduced++;
        DynPublish(part);
        SceneDb.TouchPart(part, Dirty.Anim);
    }

    /// <summary>The turbulence and breeze clock: wall seconds, wrapped at 1024 to keep float precision. Not a
    /// sum of animate dt -- <see cref="Animate"/> runs once per SCENE, several a frame, and a sum ran the
    /// flutter that many times too fast for the spring to follow.</summary>
    private static float Clock()
        => (float)(System.Diagnostics.Stopwatch.GetTimestamp() % (System.Diagnostics.Stopwatch.Frequency * 1024L)
                   / (double)System.Diagnostics.Stopwatch.Frequency);

    /// <summary>End of the scene walk: nothing is queued in DB mode (the solve is inline, on the part walk, as
    /// the engine's is), so this is where the frame's clock moves and the audit speaks.</summary>
    private static void DanglyDrain()
    {
        DynAudit();
    }

    /// <summary>splitmix64's finalizer: turns an index into well-spread bits.</summary>
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return z ^ (z >> 31);
    }

    /// <summary>Hamilton product in the engine's own association, over <see cref="Quaternion"/>'s
    /// (x, y, z, w) storage -- the same arithmetic as <c>DanglyPhysics.Qmul</c>, which is w-first.</summary>
    private static Quaternion Qmul(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>Rotate by a unit quaternion, with the engine's three early-outs kept: they are the difference
    /// between returning the vector's exact bits and a rounded product of them.</summary>
    private static Vector3 Qrot(Quaternion q, Vector3 v)
    {
        if (q.W == 1f) return v;
        if (v == Vector3.Zero) return v;
        if (q.X == 0f && q.Y == 0f && q.Z == 0f) return v;
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float a = y * y + z * z, b = z * z + x * x, c = x * x + y * y;
        float d = x * y - w * z, e = w * z + x * y, f = y * z - x * w, g = x * z + y * w;
        float hh = x * z - y * w, ii = y * z + x * w;
        return new Vector3((g + g) * v.Z + (1f - (a + a)) * v.X + (d + d) * v.Y,
                           (e + e) * v.X + (1f - (b + b)) * v.Y + (f + f) * v.Z,
                           (hh + hh) * v.X + (ii + ii) * v.Y + (1f - (c + c)) * v.Z);
    }
}
