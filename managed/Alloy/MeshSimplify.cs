using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Quadric-error-metric edge collapse over one index range, greedy and multi-pass: each pass costs every legal
/// collapse, sorts, and applies the cheapest ones whose endpoints no earlier collapse in the pass touched.
/// REINDEXES only -- the vertex stream, the base vertex and the VAO formats are untouched. A border vertex (on an
/// edge one triangle uses) and a seam vertex (two indices at one position) are never collapsed, so attributes live.
/// </summary>
internal static class MeshSimplify
{
    /// <summary>Indices are <c>GL_UNSIGNED_SHORT</c> everywhere in Alloy, so one part cannot address more.</summary>
    internal const int MaxVerts = 1 << 16;
    /// <summary>Corner ceiling per part: a part past it is left alone rather
    /// than given a scratch allocation sized by content.</summary>
    internal const int MaxCorners = 1 << 18;

    /// <summary>Passes before the simplifier gives up on reaching its target. Each pass can retire a large
    /// fraction of the mesh (collapses are applied in one sweep), so the target is normally hit in a few.</summary>
    private const int MaxPasses = 24;

    /// <summary>Position-hash mixers.</summary>
    private const long MixX = 0x9E3779B1L, MixY = 0x85EBCA77L, MixZ = 0xC2B2AE3DL;   // not-an-offset: hash mixers
    private const long Mix = unchecked((long)0x9E3779B97F4A7C15UL);   // not-an-offset: Knuth's multiplicative hash constant

    /// <summary>The farthest (world units) a collapse may move a surface, whatever the mesh's size. The relative
    /// budget alone let a 20-unit tile move a wall over a metre at level 2, flattening the recess a separate
    /// window object sits in -- and the two then fought. Near-coplanar collapses, where the saving is, pass.</summary>
    internal const double MaxShift = 0.02;

    /// <summary>Simplify <paramref name="idxCount"/> indices towards <paramref name="targetTris"/> triangles and
    /// write the survivors to <paramref name="outIdx"/>. <paramref name="pos"/> is three floats per index VALUE
    /// (<c>pos[3*i]</c> is what index <c>i</c> points at); <paramref name="errorLimit"/> is a fraction of the
    /// squared bounding-box diagonal past which a collapse is refused. 0 = no useful reduction.</summary>
    internal static int Run(ushort[] idx, int idxCount, float[] pos, int vertCount,
                            int targetTris, float errorLimit, ushort[] outIdx, double maxShift = MaxShift)
    {
        if (idxCount < 6 || idxCount % 3 != 0 || idxCount > MaxCorners
            || vertCount <= 0 || vertCount > MaxVerts || targetTris < 1) return 0;
        int tris = idxCount / 3;
        if (targetTris >= tris) return 0;

        float lox = float.MaxValue, loy = float.MaxValue, loz = float.MaxValue;
        float hix = float.MinValue, hiy = float.MinValue, hiz = float.MinValue;
        for (int i = 0; i < idxCount; i++)
        {
            int v = idx[i];
            float x = pos[v * 3], y = pos[v * 3 + 1], z = pos[v * 3 + 2];
            if (x < lox) lox = x; if (x > hix) hix = x;
            if (y < loy) loy = y; if (y > hiy) hiy = y;
            if (z < loz) loz = z; if (z > hiz) hiz = z;
        }
        double dx = hix - lox, dy = hiy - loy, dz = hiz - loz;
        double diag2 = dx * dx + dy * dy + dz * dz;
        if (!(diag2 > 0d)) return 0;
        double costLimit = Math.Min(errorLimit * diag2, maxShift * maxShift);

        var q = new double[vertCount * 10];
        var locked = new bool[vertCount];
        var rep = new int[vertCount];
        for (int v = 0; v < vertCount; v++) rep[v] = v;

        // Adjacency: one chain of CORNERS per representative vertex, spliced on collapse.
        var head = new int[vertCount];
        var tail = new int[vertCount];
        var next = new int[idxCount];
        for (int v = 0; v < vertCount; v++) { head[v] = -1; tail[v] = -1; }

        for (int t = 0; t < tris; t++)
        {
            int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
            Plane(pos, a, b, c, out double nx, out double ny, out double nz, out double d);
            if (nx != 0d || ny != 0d || nz != 0d || d != 0d)
            {
                AddPlane(q, a, nx, ny, nz, d);
                AddPlane(q, b, nx, ny, nz, d);
                AddPlane(q, c, nx, ny, nz, d);
            }
            for (int k = 0; k < 3; k++)
            {
                int corner = t * 3 + k;
                int v = idx[corner];
                next[corner] = -1;
                if (head[v] < 0) head[v] = corner; else next[tail[v]] = corner;
                tail[v] = corner;
            }
        }

        LockSeams(idx, idxCount, pos, locked);
        LockBorders(idx, idxCount, locked);

        var eKey = new double[idxCount];
        var eFrom = new int[idxCount];
        var eTo = new int[idxCount];
        var order = new int[idxCount];
        var stamp = new int[vertCount];
        int gen = 0;
        int live = tris;

        for (int pass = 0; pass < MaxPasses && live > targetTris; pass++)
        {
            int n = 0;
            for (int t = 0; t < tris; t++)
            {
                int a = Rep(rep, idx[t * 3]), b = Rep(rep, idx[t * 3 + 1]), c = Rep(rep, idx[t * 3 + 2]);
                n = Offer(q, pos, locked, a, b, eKey, eFrom, eTo, n, costLimit);
                n = Offer(q, pos, locked, b, c, eKey, eFrom, eTo, n, costLimit);
                n = Offer(q, pos, locked, c, a, eKey, eFrom, eTo, n, costLimit);
            }
            if (n == 0) break;
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(eKey, order, 0, n);

            gen++;
            int applied = 0;
            for (int i = 0; i < n && live > targetTris; i++)
            {
                int from = eFrom[order[i]], to = eTo[order[i]];
                if (stamp[from] == gen || stamp[to] == gen) continue;
                if (Rep(rep, from) != from || Rep(rep, to) != to) continue;
                if (Flips(idx, pos, rep, head, next, from, to)) continue;
                int died = Retired(idx, rep, head, next, from, to);
                rep[from] = to;
                for (int k = 0; k < 10; k++) q[to * 10 + k] += q[from * 10 + k];
                Splice(head, tail, next, from, to);
                stamp[from] = gen; stamp[to] = gen;
                applied++;
                live -= died;
            }
            if (applied == 0) break;
        }

        if (live >= tris) return 0;
        int outN = 0;
        for (int t = 0; t < tris; t++)
        {
            int a = Rep(rep, idx[t * 3]), b = Rep(rep, idx[t * 3 + 1]), c = Rep(rep, idx[t * 3 + 2]);
            if (a == b || b == c || a == c) continue;
            outIdx[outN++] = (ushort)a; outIdx[outN++] = (ushort)b; outIdx[outN++] = (ushort)c;
        }
        return outN;
    }

    /// <summary>Two distinct indices at the same position ARE the mesh's attribute discontinuity -- a UV
    /// seam, a hard normal crease, a material edge. Both are locked, so the discontinuity cannot be welded
    /// shut by a collapse that would have to pick one side's attributes.</summary>
    private static void LockSeams(ushort[] idx, int idxCount, float[] pos, bool[] locked)
    {
        int slots = 1;
        while (slots < idxCount * 2) slots <<= 1;
        var key = new long[slots];
        var owner = new int[slots];
        var used = new bool[slots];
        for (int i = 0; i < idxCount; i++)
        {
            int v = idx[i];
            int bx = BitConverter.SingleToInt32Bits(pos[v * 3]);
            int by = BitConverter.SingleToInt32Bits(pos[v * 3 + 1]);
            int bz = BitConverter.SingleToInt32Bits(pos[v * 3 + 2]);
            long k = ((long)bx * MixX) ^ ((long)by * MixY) ^ ((long)bz * MixZ);
            int s = (int)(k ^ (k >> 29)) & (slots - 1);
            while (used[s] && key[s] != k) s = (s + 1) & (slots - 1);
            if (!used[s]) { used[s] = true; key[s] = k; owner[s] = v; continue; }
            if (owner[s] == v) continue;
            locked[v] = true; locked[owner[s]] = true;
        }
    }

    /// <summary>An undirected edge used by exactly one triangle is a mesh border; both its ends are locked,
    /// which is what keeps a wall's outline and an open shell's rim where they were.</summary>
    private static void LockBorders(ushort[] idx, int idxCount, bool[] locked)
    {
        int slots = 1;
        while (slots < idxCount * 4) slots <<= 1;
        var key = new long[slots];
        var count = new int[slots];
        var used = new bool[slots];
        for (int t = 0; t < idxCount; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = idx[t + k], b = idx[t + (k + 1) % 3];
                long e = a < b ? ((long)a << 20) | (long)b : ((long)b << 20) | (long)a;
                int s = (int)((e * Mix) >> 40) & (slots - 1);
                while (used[s] && key[s] != e) s = (s + 1) & (slots - 1);
                if (!used[s]) { used[s] = true; key[s] = e; }
                count[s]++;
            }
        for (int t = 0; t < idxCount; t += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = idx[t + k], b = idx[t + (k + 1) % 3];
                long e = a < b ? ((long)a << 20) | (long)b : ((long)b << 20) | (long)a;
                int s = (int)((e * Mix) >> 40) & (slots - 1);
                while (key[s] != e) s = (s + 1) & (slots - 1);
                if (count[s] == 1) { locked[a] = true; locked[b] = true; }
            }
    }

    /// <summary>Record the cheaper of the two directions of one edge, when both ends are collapsible and the
    /// cost is inside the error budget.</summary>
    private static int Offer(double[] q, float[] pos, bool[] locked, int a, int b,
                             double[] eKey, int[] eFrom, int[] eTo, int n, double limit)
    {
        if (a == b || locked[a] || locked[b]) return n;
        double ca = Eval(q, a, b, pos, a);
        double cb = Eval(q, a, b, pos, b);
        double cost = ca < cb ? ca : cb;
        if (!(cost <= limit)) return n;
        eKey[n] = cost;
        if (ca < cb) { eFrom[n] = b; eTo[n] = a; } else { eFrom[n] = a; eTo[n] = b; }
        return n + 1;
    }

    /// <summary>The summed quadric of <paramref name="a"/> and <paramref name="b"/> evaluated at
    /// <paramref name="at"/>'s position: the squared distance the merged vertex would sit from every plane
    /// the two vertices touch.</summary>
    private static double Eval(double[] q, int a, int b, float[] pos, int at)
    {
        double x = pos[at * 3], y = pos[at * 3 + 1], z = pos[at * 3 + 2];
        int ia = a * 10, ib = b * 10;
        double a2 = q[ia] + q[ib], ab = q[ia + 1] + q[ib + 1], ac = q[ia + 2] + q[ib + 2], ad = q[ia + 3] + q[ib + 3];
        double b2 = q[ia + 4] + q[ib + 4], bc = q[ia + 5] + q[ib + 5], bd = q[ia + 6] + q[ib + 6];
        double c2 = q[ia + 7] + q[ib + 7], cd = q[ia + 8] + q[ib + 8], d2 = q[ia + 9] + q[ib + 9];
        double r = a2 * x * x + 2 * ab * x * y + 2 * ac * x * z + 2 * ad * x
                   + b2 * y * y + 2 * bc * y * z + 2 * bd * y
                   + c2 * z * z + 2 * cd * z + d2;
        return r > 0d ? r : 0d;
    }

    private static void AddPlane(double[] q, int v, double a, double b, double c, double d)
    {
        int i = v * 10;
        q[i] += a * a; q[i + 1] += a * b; q[i + 2] += a * c; q[i + 3] += a * d;
        q[i + 4] += b * b; q[i + 5] += b * c; q[i + 6] += b * d;
        q[i + 7] += c * c; q[i + 8] += c * d; q[i + 9] += d * d;
    }

    /// <summary>The unit plane of one triangle, or all zeroes for a degenerate one.</summary>
    private static void Plane(float[] pos, int a, int b, int c,
                              out double nx, out double ny, out double nz, out double d)
    {
        double ax = pos[b * 3] - pos[a * 3], ay = pos[b * 3 + 1] - pos[a * 3 + 1], az = pos[b * 3 + 2] - pos[a * 3 + 2];
        double bx = pos[c * 3] - pos[a * 3], by = pos[c * 3 + 1] - pos[a * 3 + 1], bz = pos[c * 3 + 2] - pos[a * 3 + 2];
        nx = ay * bz - az * by; ny = az * bx - ax * bz; nz = ax * by - ay * bx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-20d) { nx = ny = nz = d = 0d; return; }
        nx /= len; ny /= len; nz /= len;
        d = -(nx * pos[a * 3] + ny * pos[a * 3 + 1] + nz * pos[a * 3 + 2]);
    }

    private static int Rep(int[] rep, int v)
    {
        int r = v;
        while (rep[r] != r) r = rep[r];
        while (rep[v] != r) { int nx = rep[v]; rep[v] = r; v = nx; }
        return r;
    }

    /// <summary>Would moving <paramref name="from"/> onto <paramref name="to"/> turn any surviving triangle
    /// inside out? A collapse that passes the quadric and fails this is the classic fold-over, and it is the
    /// one defect a purely metric simplifier ships.</summary>
    private static bool Flips(ushort[] idx, float[] pos, int[] rep, int[] head, int[] next, int from, int to)
    {
        for (int c = head[from]; c >= 0; c = next[c])
        {
            int t = c - c % 3;
            int a = Rep(rep, idx[t]), b = Rep(rep, idx[t + 1]), d = Rep(rep, idx[t + 2]);
            if (a == b || b == d || a == d) continue;
            int na = a == from ? to : a, nb = b == from ? to : b, nd = d == from ? to : d;
            if (na == nb || nb == nd || na == nd) continue;   // the collapse retires this triangle
            Plane(pos, a, b, d, out double ox, out double oy, out double oz, out _);
            Plane(pos, na, nb, nd, out double px, out double py, out double pz, out _);
            if (ox * px + oy * py + oz * pz <= 0d) return true;
        }
        return false;
    }

    private static void Splice(int[] head, int[] tail, int[] next, int from, int to)
    {
        if (head[from] < 0) return;
        if (head[to] < 0) { head[to] = head[from]; tail[to] = tail[from]; }
        else { next[tail[to]] = head[from]; tail[to] = tail[from]; }
        head[from] = -1; tail[from] = -1;
    }

    /// <summary>How many still-live triangles this collapse retires -- the incremental form of recounting the
    /// whole mesh, which is what keeps a pass linear in the collapses rather than quadratic in them.</summary>
    private static int Retired(ushort[] idx, int[] rep, int[] head, int[] next, int from, int to)
    {
        int died = 0;
        for (int c = head[from]; c >= 0; c = next[c])
        {
            int t = c - c % 3;
            int a = Rep(rep, idx[t]), b = Rep(rep, idx[t + 1]), d = Rep(rep, idx[t + 2]);
            if (a == b || b == d || a == d) continue;
            int na = a == from ? to : a, nb = b == from ? to : b, nd = d == from ? to : d;
            if (na == nb || nb == nd || na == nd) died++;
        }
        return died;
    }

    /// <summary>Off-device: a subdivided grid (an interior a border ring encloses) simplifies to fewer
    /// triangles, every output index is one the input already carried, and no output triangle is degenerate.
    /// A grid whose every vertex is a border vertex cannot be reduced at all, which is the lock working.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        const int N = 12;                         // 12x12 vertices, 11x11 quads, 242 triangles
        int verts = N * N, tris = (N - 1) * (N - 1) * 2;
        var pos = new float[verts * 3];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                int v = y * N + x;
                pos[v * 3] = x; pos[v * 3 + 1] = y;
                // A gentle dome, so the quadrics are not all coplanar and the collapse order has to matter.
                pos[v * 3 + 2] = 0.35f * MathF.Sin(x * 0.5f) * MathF.Cos(y * 0.5f);
            }
        var idx = new ushort[tris * 3];
        int n = 0;
        for (int y = 0; y < N - 1; y++)
            for (int x = 0; x < N - 1; x++)
            {
                int v = y * N + x;
                idx[n++] = (ushort)v; idx[n++] = (ushort)(v + 1); idx[n++] = (ushort)(v + N);
                idx[n++] = (ushort)(v + 1); idx[n++] = (ushort)(v + N + 1); idx[n++] = (ushort)(v + N);
            }
        var outIdx = new ushort[tris * 3];

        int got = Run(idx, n, pos, verts, tris / 2, 1f, outIdx, double.MaxValue);
        check.Expect(got > 0 && got % 3 == 0, "the grid produced no simplified index run");
        check.Expect(got < n, $"the output is not smaller than the input ({got} of {n})");
        bool bad = false, degenerate = false;
        for (int i = 0; i < got; i += 3)
        {
            for (int k = 0; k < 3; k++) if (outIdx[i + k] >= verts) bad = true;
            if (outIdx[i] == outIdx[i + 1] || outIdx[i + 1] == outIdx[i + 2] || outIdx[i] == outIdx[i + 2])
                degenerate = true;
        }
        check.Expect(!bad, "an output index is out of range");
        check.Expect(!degenerate, "an output triangle is degenerate");

        // Every vertex of a 2x2 grid is on its border, so there is nothing legal to collapse.
        var sq = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 };
        var sqIdx = new ushort[] { 0, 1, 2, 1, 3, 2 };
        check.Expect(Run(sqIdx, 6, sq, 4, 1, 1f, outIdx) == 0, "an all-border mesh was collapsed anyway");
        // A zero error budget refuses every collapse that is not exactly free.
        check.Expect(Run(idx, n, pos, verts, 1, 0f, outIdx) == 0, "a zero error budget still collapsed a curved mesh");

        // A 22-unit flat wall with a 0.3-deep recess in its middle: the relative budget alone would flatten the
        // recess (it is 1% of the diagonal); the absolute cap must keep it, and still thin the flat rest.
        var wall = new float[verts * 3];
        for (int v = 0; v < verts; v++)
        {
            int x = v % N, y = v / N;
            wall[v * 3] = x * 2f; wall[v * 3 + 1] = y * 2f;
            wall[v * 3 + 2] = x >= 5 && x <= 6 && y >= 5 && y <= 6 ? -0.3f : 0f;
        }
        int wn = Run(idx, n, wall, verts, tris / 5, MeshLod.Error2, outIdx);
        bool recess = false;
        for (int i = 0; i < wn; i++) if (wall[outIdx[i] * 3 + 2] < -0.29f) recess = true;
        check.Expect(wn > 0 && wn < n, $"the flat part of a large wall still thins ({wn} of {n})");
        check.Expect(recess, "a large mesh keeps a 0.3-unit recess at level 2: no collapse moves a surface past MaxShift");
        int un = Run(idx, n, wall, verts, tris / 5, MeshLod.Error2, outIdx, double.MaxValue);
        bool kept = false;
        for (int i = 0; i < un; i++) if (wall[outIdx[i] * 3 + 2] < -0.29f) kept = true;
        check.Expect(un > 0 && !kept, "fixture: with no cap the same budget DOES flatten the recess (else the check above proves nothing)");

        log.Info(check.Ok ? "MeshSimplify.SelfCheck: passed" : "MeshSimplify.SelfCheck: FAILED");
        return check.Ok;
    }
}
