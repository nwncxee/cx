using System;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Physics;

public sealed partial class DanglyRig
{
    /// <summary>The slowest spring the solver still tracks, as <c>omega^2</c>. A node authored with no spring
    /// at all -- tightness 0, or every moving constraint 0 -- would otherwise divide by zero here and never
    /// return to its rest pose there, so its terms are taken at this floor instead.</summary>
    private const float MinSpring = 0.25f;

    /// <summary>Ceilings the fixed substep needs: an explicit step diverges once <c>omega * h</c> approaches
    /// 2, and once <c>2 * zeta * omega * h</c> does. ponytail: both lift together with an implicit solve.</summary>
    private const float MaxOmega = 120f, MaxZeta = 4f;

    /// <summary>Node units of chain per Swing segment, and the grid the spec bounds it by.</summary>
    private const float SegmentLength = 0.35f;
    private const int MaxColumns = 4, MaxSegments = 6, MinSegments = 2;

    /// <summary>A Sway shorter than this (node units, anchors to moving centroid) translates: turned, a short
    /// lever amplifies a small tip offset into a large swing of the whole set.</summary>
    private const float MinBendLength = 0.25f;

    /// <summary>The swing limits (radians): a plant bends 40 degrees at most, cloth swings 80.</summary>
    private const float MaxBend = 0.6981317f, MaxSwing = 1.3962634f;

    /// <summary>Air response per metre (section 5a): cloth, foliage, a body part. Cloth narrower than
    /// <see cref="ClothWidth"/> is cord, heavy for its area, and takes the response in proportion.</summary>
    private const float AirSwing = 1f, AirSway = 0.4f, AirJiggle = 0.1f, ClothWidth = 0.5f;

    /// <summary>The class thresholds: mobility that tracks height is Sway, and what is left is Jiggle when it
    /// is this well damped.</summary>
    private const float SwayCorrelation = 0.5f, JiggleZeta = 0.15f;

    /// <summary>The authored displacement (node units) that makes a Swing a strand. Worn clothing ships 0.02 to
    /// 0.05 and long hair, braids and tails 0.1 to 0.5, so the line sits between the two.</summary>
    private const float StrandGive = 0.1f;

    private const float Tiny = 1e-6f;

    /// <summary>The rig for one dangly node, or null when it has nothing the joints can move.</summary>
    public static DanglyRig? Build(ModelDef m, NodeDef n)
    {
        MeshDef? mesh = n.Mesh;
        DanglyDef? dn = n.Dangly;
        if (mesh == null || dn == null) return null;
        Vector3[] pos = mesh.Positions;
        int nv = pos.Length;
        if (nv <= 0) return null;

        var rig = new DanglyRig { RestDown = AuthoredDown(m, n) };
        var mob = new float[nv];
        int moving = 0;
        double csum = 0;
        for (int i = 0; i < nv; i++)
        {
            float c = i < dn.Constraints.Length ? dn.Constraints[i] : 0f;
            if (!float.IsFinite(c) || !(c > 0f) || c >= VanillaDangly.ConstraintMax) continue;
            mob[i] = 1f - c / VanillaDangly.ConstraintMax;
            moving++;
            csum += c;
        }
        rig.Mobility = mob;

        float tight = float.IsFinite(dn.Tightness) && dn.Tightness > 0f ? dn.Tightness : 0f;
        float period = float.IsFinite(dn.Period) && dn.Period > 0f ? dn.Period : 0f;
        float disp = float.IsFinite(dn.Displacement) && dn.Displacement > 0f ? dn.Displacement : 0f;
        float cbar = moving > 0 ? (float)(csum / moving) : 0f;
        float ct = MathF.Max(cbar * tight, MinSpring / VanillaDangly.StiffnessGain);
        rig.Omega = MathF.Min(MathF.Sqrt(VanillaDangly.StiffnessGain * ct), MaxOmega);
        rig.Zeta = MathF.Min(MathF.Min(VanillaDangly.DampingGain * period / (2f * rig.Omega), MaxZeta),
                             0.5f / (DanglyJoints.SubDt * rig.Omega));
        if (moving == 0)
        {
            rig.Class = DanglyClass.Static;
            return rig;
        }

        Vector3 anchorAt = Vector3.Zero, tipAt = Vector3.Zero;
        int anchors = Anchors(mob, pos, ref anchorAt);
        float msum = 0f;
        for (int i = 0; i < nv; i++)
            if (mob[i] > 0f) { tipAt += pos[i] * mob[i]; msum += mob[i]; }
        tipAt = msum > 0f ? tipAt / msum : anchorAt;
        if (anchors <= 0) anchorAt = tipAt;

        Vector3 hang = tipAt - anchorAt;
        float hangLen = hang.Length();
        hang = hangLen > Tiny ? hang / hangLen : rig.RestDown;

        float corr = HeightCorrelation(mob, pos);
        rig.Class = corr > SwayCorrelation ? DanglyClass.Sway
                  : rig.Zeta > JiggleZeta ? DanglyClass.Jiggle
                  : DanglyClass.Swing;

        var grid = new SwingGrid(rig.Class, mob, pos, anchorAt, hang);
        // Nothing to chain down: a moving set on the anchor plane jiggles in place.
        if (rig.Class == DanglyClass.Swing && grid.Length <= 0f) rig.Class = DanglyClass.Jiggle;
        rig.Columns = grid.Columns;
        rig.Segments = grid.Segments;
        Joints(rig, grid, anchorAt, tipAt, hang);
        rig.Hang = hang;
        rig.Shear = rig.Class == DanglyClass.Jiggle || rig.Class == DanglyClass.Sway && hangLen < MinBendLength;
        rig.Strand = rig.Class == DanglyClass.Swing && disp >= StrandGive;
        rig.Cone = rig.Shear ? 0f : rig.Class == DanglyClass.Swing ? MaxSwing : MaxBend;
        rig.Air = rig.Class switch
        {
            DanglyClass.Swing => AirSwing * MathF.Min(1f, grid.Width / ClothWidth),
            DanglyClass.Sway => AirSway,
            _ => AirJiggle,
        };
        rig.Arm = LongestArm(rig);
        Weights(rig, grid, mob, pos, anchorAt, hang, disp, msum / moving);
        Drag(rig, mesh, pos);
        return rig;
    }

    /// <summary>The longest distance from a column's kinematic root to one of its joints, at rest.</summary>
    private static float LongestArm(DanglyRig rig)
    {
        float arm = 0f;
        int root = 0;
        for (int j = 0; j < rig.JointRest.Length; j++)
        {
            if (rig.JointParent[j] < 0) { root = j; continue; }
            arm = MathF.Max(arm, (rig.JointRest[j] - rig.JointRest[root]).Length());
        }
        return arm;
    }

    /// <summary>Model-down in the node's authored frame: the node's rest orientation composed up its parent
    /// chain, inverted. Zero for every tile and unrotated placeable, which is what keeps the authored rest
    /// pose the equilibrium.</summary>
    private static Vector3 AuthoredDown(ModelDef m, NodeDef n)
    {
        Matrix4x4 rot = Matrix4x4.Identity;
        int at = n.Index;
        for (int hop = 0; hop <= m.Nodes.Length && (uint)at < (uint)m.Nodes.Length; hop++)
        {
            NodeDef nd = m.Nodes[at];
            rot *= Matrix4x4.CreateFromQuaternion(Unit(nd.Orientation));
            at = nd.Parent;
            if (at < 0) break;
        }
        Vector3 down = Vector3.TransformNormal(-Vector3.UnitZ, Matrix4x4.Transpose(rot));
        float len = down.Length();
        return len > Tiny ? down / len : -Vector3.UnitZ;
    }

    private static Quaternion Unit(Quaternion q)
        => q.LengthSquared() > Tiny ? Quaternion.Normalize(q) : Quaternion.Identity;

    /// <summary>The anchor set's centroid and size: the pinned vertices, or the lowest-mobility tenth when a
    /// node pins none.</summary>
    private static int Anchors(float[] mob, Vector3[] pos, ref Vector3 centroid)
    {
        int nv = mob.Length, n = 0;
        Vector3 sum = Vector3.Zero;
        for (int i = 0; i < nv; i++)
            if (mob[i] == 0f) { sum += pos[i]; n++; }
        if (n == 0)
        {
            int quota = Math.Max(1, nv / 10);
            float[] sorted = (float[])mob.Clone();
            Array.Sort(sorted);
            float limit = sorted[Math.Min(quota - 1, nv - 1)];
            for (int i = 0; i < nv && n < quota; i++)
                if (mob[i] <= limit) { sum += pos[i]; n++; }
        }
        if (n > 0) centroid = sum / n;
        return n;
    }

    /// <summary>Pearson correlation of mobility with node-local height over every vertex; 0 when either is
    /// constant and the correlation is undefined.</summary>
    private static float HeightCorrelation(float[] mob, Vector3[] pos)
    {
        int n = mob.Length;
        double sm = 0, sz = 0;
        for (int i = 0; i < n; i++) { sm += mob[i]; sz += pos[i].Z; }
        double mm = sm / n, mz = sz / n, cov = 0, vm = 0, vz = 0;
        for (int i = 0; i < n; i++)
        {
            double a = mob[i] - mm, b = pos[i].Z - mz;
            cov += a * b; vm += a * a; vz += b * b;
        }
        return vm > 1e-12 && vz > 1e-12 ? (float)(cov / Math.Sqrt(vm * vz)) : 0f;
    }

    /// <summary>Swing's grid over the moving set: the chain length down the hang axis, the width across its
    /// principal axis, and the column and segment counts those give. One column of one segment for the other
    /// classes, whose single joint is placed by <see cref="Joints"/> instead.</summary>
    private readonly struct SwingGrid
    {
        public readonly int Columns, Segments;
        public readonly Vector3 Lateral;
        public readonly float Width, Length, LateralMin;

        public SwingGrid(DanglyClass cls, float[] mob, Vector3[] pos, Vector3 anchorAt, Vector3 hang)
        {
            Columns = 1; Segments = 1;
            Lateral = Vector3.Zero; Width = 0f; Length = 0f; LateralMin = 0f;
            if (cls != DanglyClass.Swing) return;

            int nv = mob.Length;
            for (int i = 0; i < nv; i++)
                if (mob[i] > 0f) Length = MathF.Max(Length, Vector3.Dot(pos[i] - anchorAt, hang));
            if (Length < 1e-3f) { Length = 0f; return; }
            Lateral = Principal(mob, pos, anchorAt, hang);
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < nv; i++)
                if (mob[i] > 0f)
                {
                    float t = Vector3.Dot(pos[i] - anchorAt, Lateral);
                    lo = MathF.Min(lo, t); hi = MathF.Max(hi, t);
                }
            LateralMin = lo;
            Width = MathF.Max(hi - lo, 0f);
            Columns = Math.Clamp((int)MathF.Round(2f * Width / Length), 1, MaxColumns);
            Segments = Math.Clamp((int)MathF.Round(Length / SegmentLength), MinSegments, MaxSegments);
        }

        /// <summary>The column's centre line, as a lateral coordinate.</summary>
        public float Centre(int k) => Width > Tiny ? LateralMin + (k + 0.5f) * Width / Columns : LateralMin;
    }

    /// <summary>The widest-spread direction across the hang axis, from the moving set's lateral covariance.</summary>
    private static Vector3 Principal(float[] mob, Vector3[] pos, Vector3 anchorAt, Vector3 hang)
    {
        Vector3 e1 = MathF.Abs(hang.Z) < 0.9f ? Vector3.Cross(hang, Vector3.UnitZ)
                                              : Vector3.Cross(hang, Vector3.UnitX);
        float l1 = e1.Length();
        if (l1 < Tiny) return Vector3.UnitX;
        e1 /= l1;
        Vector3 e2 = Vector3.Cross(hang, e1);
        int nv = mob.Length, n = 0;
        double sx = 0, sy = 0;
        for (int i = 0; i < nv; i++)
            if (mob[i] > 0f)
            {
                Vector3 d = pos[i] - anchorAt;
                sx += Vector3.Dot(d, e1); sy += Vector3.Dot(d, e2); n++;
            }
        if (n == 0) return e1;
        double mx = sx / n, my = sy / n, axx = 0, axy = 0, ayy = 0;
        for (int i = 0; i < nv; i++)
            if (mob[i] > 0f)
            {
                Vector3 d = pos[i] - anchorAt;
                double x = Vector3.Dot(d, e1) - mx, y = Vector3.Dot(d, e2) - my;
                axx += x * x; axy += x * y; ayy += y * y;
            }
        double tr = axx + ayy, det = axx * ayy - axy * axy;
        double lam = tr / 2 + Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
        double vx, vy;
        if (Math.Abs(axy) > 1e-12) { vx = axy; vy = lam - axx; }
        else if (axx >= ayy) { vx = 1; vy = 0; }
        else { vx = 0; vy = 1; }
        double vl = Math.Sqrt(vx * vx + vy * vy);
        if (vl < 1e-12) return e1;
        Vector3 axis = e1 * (float)(vx / vl) + e2 * (float)(vy / vl);
        float al = axis.Length();
        return al > Tiny ? axis / al : e1;
    }

    /// <summary>The joints and the bones over them: one segment from the anchor centroid to the moving
    /// centroid for Sway and Jiggle, and Swing's grid of columns hanging off the anchor plane.</summary>
    private static void Joints(DanglyRig rig, in SwingGrid grid, Vector3 anchorAt, Vector3 tipAt, Vector3 hang)
    {
        int k = grid.Columns, j = grid.Segments;
        var rest = new Vector3[k * (j + 1)];
        var parent = new int[rest.Length];
        if (rig.Class == DanglyClass.Swing)
            for (int c = 0; c < k; c++)
            {
                Vector3 top = anchorAt + grid.Lateral * grid.Centre(c);
                for (int seg = 0; seg <= j; seg++)
                {
                    int at = c * (j + 1) + seg;
                    rest[at] = top + hang * (seg * grid.Length / j);
                    parent[at] = seg == 0 ? -1 : at - 1;
                }
            }
        else
        {
            rest[0] = anchorAt; rest[1] = tipAt;
            parent[0] = -1; parent[1] = 0;
        }
        rig.JointRest = rest;
        rig.JointParent = parent;
        rig.JointCap = new float[rest.Length];

        var bone = new int[k * j + 1];
        bone[0] = -1;
        for (int c = 0; c < k; c++)
            for (int seg = 1; seg <= j; seg++) bone[c * j + seg] = c * (j + 1) + seg;
        rig.BoneJoint = bone;
    }

    /// <summary>The skin weights that bind the mesh to the bones, and the per-joint excursion caps the owned
    /// vertices' mobility gives. Four influences a vertex, summing to 1, with the part's own bone taking
    /// whatever the hats leave.</summary>
    private static void Weights(DanglyRig rig, in SwingGrid grid, float[] mob, Vector3[] pos,
                                Vector3 anchorAt, Vector3 hang, float disp, float meanMob)
    {
        int nv = mob.Length, nj = rig.JointRest.Length;
        int k = grid.Columns, j = grid.Segments;
        float m90 = Percentile90(mob);
        var weights = new Vector4[nv];
        var packed = new uint[nv];
        var wide = new ushort[nv * 4];
        var influences = new int[nv];
        var capSum = new double[nj];
        var capN = new int[nj];
        Span<int> slot = stackalloc int[5];
        Span<float> wgt = stackalloc float[5];

        for (int i = 0; i < nv; i++)
        {
            int cnt = 0;
            float m = mob[i];
            if (rig.Class == DanglyClass.Swing)
            {
                float f = Smoothstep(m, m90);
                float s = Math.Clamp(Vector3.Dot(pos[i] - anchorAt, hang) / (grid.Length / j), 0f, j);
                int s0 = Math.Clamp((int)MathF.Floor(s - 0.5f), 0, j - 1);
                int s1 = Math.Min(s0 + 1, j - 1);
                float h0 = MathF.Max(0f, 1f - MathF.Abs(s - (s0 + 0.5f)));
                float h1 = s1 != s0 ? MathF.Max(0f, 1f - MathF.Abs(s - (s1 + 0.5f))) : 0f;
                float hs = h0 + h1;
                if (hs <= Tiny) { h0 = 1f; h1 = 0f; hs = 1f; }
                h0 /= hs; h1 /= hs;

                int k0 = 0, k1 = 0;
                float c0 = 1f, c1 = 0f;
                if (k > 1 && grid.Width > Tiny)
                {
                    float cs = Math.Clamp((Vector3.Dot(pos[i] - anchorAt, grid.Lateral) - grid.LateralMin)
                                          / (grid.Width / k) - 0.5f, 0f, k - 1f);
                    k0 = Math.Clamp((int)MathF.Floor(cs), 0, k - 2);
                    k1 = k0 + 1;
                    c1 = cs - k0;
                    c0 = 1f - c1;
                }
                slot[cnt] = 0; wgt[cnt++] = 1f - f;
                Add(slot, wgt, ref cnt, k0 * j + s0 + 1, f * c0 * h0);
                Add(slot, wgt, ref cnt, k0 * j + s1 + 1, f * c0 * h1);
                if (k1 != k0)
                {
                    Add(slot, wgt, ref cnt, k1 * j + s0 + 1, f * c1 * h0);
                    Add(slot, wgt, ref cnt, k1 * j + s1 + 1, f * c1 * h1);
                }
            }
            else
            {
                float wj = m90 > Tiny ? MathF.Min(1f, m / m90) : m > 0f ? 1f : 0f;
                slot[cnt] = 0; wgt[cnt++] = 1f - wj;
                Add(slot, wgt, ref cnt, 1, wj);
            }

            Trim(slot, wgt, ref cnt);
            uint bytes = 0;
            float x = 0f, y = 0f, z = 0f, w = 0f;
            int owner = -1;
            float best = 0f;
            for (int e = 0; e < 4; e++)
            {
                int b = e < cnt ? slot[e] : 0;
                float v = e < cnt ? wgt[e] : 0f;
                bytes |= (uint)(b & 0xff) << (e * 8);
                wide[i * 4 + e] = (ushort)b;
                switch (e)
                {
                    case 0: x = v; break;
                    case 1: y = v; break;
                    case 2: z = v; break;
                    default: w = v; break;
                }
                if (v > 0f) influences[i]++;
                if (b > 0 && v > best) { best = v; owner = rig.BoneJoint[b]; }
            }
            weights[i] = new Vector4(x, y, z, w);
            packed[i] = bytes;
            if (owner >= 0) { capSum[owner] += m; capN[owner]++; }
        }

        for (int at = 0; at < nj; at++)
            rig.JointCap[at] = capN[at] > 0 ? disp * (float)(capSum[at] / capN[at]) : disp * meanMob;
        rig.Skin = new SkinDef
        {
            Weights = weights, Bones = packed, BoneIndices = wide, Influences = influences,
        };
    }

    /// <summary>Fold one bone's weight into the candidate list, merging a bone already named.</summary>
    private static void Add(Span<int> slot, Span<float> wgt, ref int cnt, int bone, float weight)
    {
        if (!(weight > 0f)) return;
        for (int e = 0; e < cnt; e++)
            if (slot[e] == bone) { wgt[e] += weight; return; }
        if (cnt >= slot.Length) return;
        slot[cnt] = bone;
        wgt[cnt++] = weight;
    }

    /// <summary>Order the candidates heaviest first, drop everything past the fourth and renormalise what is
    /// left, so a vertex the grid gives five influences still sums to 1 over four.</summary>
    private static void Trim(Span<int> slot, Span<float> wgt, ref int cnt)
    {
        for (int a = 0; a < cnt; a++)
            for (int b = a + 1; b < cnt; b++)
                if (wgt[b] > wgt[a])
                {
                    (wgt[a], wgt[b]) = (wgt[b], wgt[a]);
                    (slot[a], slot[b]) = (slot[b], slot[a]);
                }
        cnt = Math.Min(cnt, 4);
        float sum = 0f;
        for (int e = 0; e < cnt; e++) sum += wgt[e];
        if (sum > Tiny)
            for (int e = 0; e < cnt; e++) wgt[e] /= sum;
        else { cnt = 1; slot[0] = 0; wgt[0] = 1f; }
    }

    /// <summary>The moving set's 90th-percentile mobility: what counts as fully mobile on this node.</summary>
    private static float Percentile90(float[] mob)
    {
        int n = 0;
        foreach (float m in mob) if (m > 0f) n++;
        if (n == 0) return 0f;
        var vals = new float[n];
        n = 0;
        foreach (float m in mob) if (m > 0f) vals[n++] = m;
        Array.Sort(vals);
        return vals[Math.Clamp((int)MathF.Ceiling(0.9f * n) - 1, 0, n - 1)];
    }

    private static float Smoothstep(float m, float m90)
    {
        float t = m90 > Tiny ? Math.Clamp(m / m90, 0f, 1f) : m > 0f ? 1f : 0f;
        return t * t * (3f - 2f * t);
    }

    /// <summary>Per joint, the area-weighted normal outer product of the faces whose centroid it owns: its
    /// eigenvalues say how broadside the joint is to a wind from each direction. A joint that owns no face
    /// takes the whole mesh's tensor, and a mesh with no face at all is isotropic.</summary>
    private static void Drag(DanglyRig rig, MeshDef mesh, Vector3[] pos)
    {
        int nj = rig.JointRest.Length;
        var diag = new Vector3[nj];
        var off = new Vector3[nj];
        var area = new float[nj];
        Vector3 allDiag = Vector3.Zero, allOff = Vector3.Zero;
        float allArea = 0f;
        foreach (FaceDef f in mesh.Faces)
        {
            if ((uint)f.V0 >= (uint)pos.Length || (uint)f.V1 >= (uint)pos.Length
             || (uint)f.V2 >= (uint)pos.Length) continue;
            Vector3 a = pos[f.V0], b = pos[f.V1], c = pos[f.V2];
            Vector3 cross = Vector3.Cross(b - a, c - a);
            float len = cross.Length();
            if (!(len > Tiny)) continue;
            Vector3 nrm = cross / len;
            float wa = 0.5f * len;
            var d = new Vector3(nrm.X * nrm.X, nrm.Y * nrm.Y, nrm.Z * nrm.Z) * wa;
            var o = new Vector3(nrm.X * nrm.Y, nrm.X * nrm.Z, nrm.Y * nrm.Z) * wa;
            allDiag += d; allOff += o; allArea += wa;
            int owner = Nearest(rig, (a + b + c) / 3f);
            if (owner < 0) continue;
            diag[owner] += d; off[owner] += o; area[owner] += wa;
        }
        for (int at = 0; at < nj; at++)
            if (area[at] > Tiny) { diag[at] /= area[at]; off[at] /= area[at]; }
            else if (allArea > Tiny) { diag[at] = allDiag / allArea; off[at] = allOff / allArea; }
            else { diag[at] = new Vector3(1f / 3f); off[at] = Vector3.Zero; }
        rig.DragDiag = diag;
        rig.DragOff = off;
    }

    /// <summary>The moving joint nearest <paramref name="at"/>, or -1 when the rig has none.</summary>
    private static int Nearest(DanglyRig rig, Vector3 at)
    {
        int best = -1;
        float bestSq = float.MaxValue;
        for (int j = 0; j < rig.JointRest.Length; j++)
        {
            if (rig.JointParent[j] < 0) continue;
            float d = (rig.JointRest[j] - at).LengthSquared();
            if (d < bestSq) { bestSq = d; best = j; }
        }
        return best;
    }
}
