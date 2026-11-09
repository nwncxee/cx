using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    /// <summary>Simplify <paramref name="idx"/> towards <paramref name="targetTris"/> triangles and write the
    /// survivors to <paramref name="outIdx"/>. <paramref name="pos"/> is one position per index VALUE, so the
    /// caller slices both to their live lengths; <paramref name="errorLimit"/> is a fraction of the squared
    /// bounding-box diagonal past which a collapse is refused. 0 = no useful reduction.</summary>
    internal static int Run(ReadOnlySpan<ushort> idx, ReadOnlySpan<Vector3> pos,
                            int targetTris, float errorLimit, ushort[] outIdx, double maxShift = MaxShift)
    {
        int idxCount = idx.Length, vertCount = pos.Length;
        if (idxCount < 6 || idxCount % 3 != 0 || idxCount > MaxCorners
            || vertCount <= 0 || vertCount > MaxVerts || targetTris < 1) return 0;
        var tri = MemoryMarshal.Cast<ushort, TriCorners>(idx);
        int tris = tri.Length;
        if (targetTris >= tris) return 0;

        float lox = float.MaxValue, loy = float.MaxValue, loz = float.MaxValue;
        float hix = float.MinValue, hiy = float.MinValue, hiz = float.MinValue;
        for (int i = 0; i < idxCount; i++)
        {
            Vector3 p = pos[idx[i]];
            if (p.X < lox) lox = p.X; if (p.X > hix) hix = p.X;
            if (p.Y < loy) loy = p.Y; if (p.Y > hiy) hiy = p.Y;
            if (p.Z < loz) loz = p.Z; if (p.Z > hiz) hiz = p.Z;
        }
        double dx = hix - lox, dy = hiy - loy, dz = hiz - loz;
        double diag2 = dx * dx + dy * dy + dz * dz;
        if (!(diag2 > 0d)) return 0;
        double costLimit = Math.Min(errorLimit * diag2, maxShift * maxShift);

        var q = new Quadric[vertCount];
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
            TriCorners tc = tri[t];
            TriPlane plane = Plane(pos, tc.A, tc.B, tc.C);
            if (!plane.IsZero) { q[tc.A].Add(in plane); q[tc.B].Add(in plane); q[tc.C].Add(in plane); }
            int first = t * 3;
            Link(head, tail, next, tc.A, first);
            Link(head, tail, next, tc.B, first + 1);
            Link(head, tail, next, tc.C, first + 2);
        }

        LockSeams(idx, pos, locked);
        LockBorders(tri, locked);

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
                TriCorners tc = tri[t];
                int a = Rep(rep, tc.A), b = Rep(rep, tc.B), c = Rep(rep, tc.C);
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
                if (Flips(tri, pos, rep, head, next, from, to)) continue;
                int died = Retired(tri, rep, head, next, from, to);
                rep[from] = to;
                q[to].Add(in q[from]);
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
            TriCorners tc = tri[t];
            int a = Rep(rep, tc.A), b = Rep(rep, tc.B), c = Rep(rep, tc.C);
            if (a == b || b == c || a == c) continue;
            outIdx[outN++] = (ushort)a; outIdx[outN++] = (ushort)b; outIdx[outN++] = (ushort)c;
        }
        return outN;
    }

    /// <summary>Append one corner to its vertex's chain.</summary>
    private static void Link(int[] head, int[] tail, int[] next, int v, int corner)
    {
        next[corner] = -1;
        if (head[v] < 0) head[v] = corner; else next[tail[v]] = corner;
        tail[v] = corner;
    }

    /// <summary>Two distinct indices at the same position ARE the mesh's attribute discontinuity -- a UV
    /// seam, a hard normal crease, a material edge. Both are locked, so the discontinuity cannot be welded
    /// shut by a collapse that would have to pick one side's attributes.</summary>
    private static void LockSeams(ReadOnlySpan<ushort> idx, ReadOnlySpan<Vector3> pos, bool[] locked)
    {
        int slots = 1;
        while (slots < idx.Length * 2) slots <<= 1;
        var key = new long[slots];
        var owner = new int[slots];
        var used = new bool[slots];
        for (int i = 0; i < idx.Length; i++)
        {
            int v = idx[i];
            Vector3 p = pos[v];
            int bx = BitConverter.SingleToInt32Bits(p.X);
            int by = BitConverter.SingleToInt32Bits(p.Y);
            int bz = BitConverter.SingleToInt32Bits(p.Z);
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
    private static void LockBorders(ReadOnlySpan<TriCorners> tri, bool[] locked)
    {
        int slots = 1;
        while (slots < tri.Length * 12) slots <<= 1;
        var key = new long[slots];
        var count = new int[slots];
        var used = new bool[slots];
        for (int t = 0; t < tri.Length; t++)
        {
            TriCorners tc = tri[t];
            Count(key, count, used, slots, tc.A, tc.B);
            Count(key, count, used, slots, tc.B, tc.C);
            Count(key, count, used, slots, tc.C, tc.A);
        }
        for (int t = 0; t < tri.Length; t++)
        {
            TriCorners tc = tri[t];
            LockBorder(key, count, slots, locked, tc.A, tc.B);
            LockBorder(key, count, slots, locked, tc.B, tc.C);
            LockBorder(key, count, slots, locked, tc.C, tc.A);
        }
    }

    /// <summary>One undirected edge as a key: the lower index first, so both triangles sharing it agree.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EdgeKey(ushort a, ushort b)
        => a < b ? ((long)a << 20) | b : ((long)b << 20) | a;

    /// <summary>The slot an edge already occupies.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Probe(long[] key, int slots, long k)
    {
        int s = (int)((k * Mix) >> 40) & (slots - 1);
        while (key[s] != k) s = (s + 1) & (slots - 1);
        return s;
    }

    /// <summary>Tally one more triangle on one edge, claiming its slot on first sight.</summary>
    private static void Count(long[] key, int[] count, bool[] used, int slots, ushort a, ushort b)
    {
        long k = EdgeKey(a, b);
        int s = (int)((k * Mix) >> 40) & (slots - 1);
        while (used[s] && key[s] != k) s = (s + 1) & (slots - 1);
        if (!used[s]) { used[s] = true; key[s] = k; }
        count[s]++;
    }

    /// <summary>Lock both ends of an edge only one triangle uses.</summary>
    private static void LockBorder(long[] key, int[] count, int slots, bool[] locked, ushort a, ushort b)
    {
        if (count[Probe(key, slots, EdgeKey(a, b))] != 1) return;
        locked[a] = true; locked[b] = true;
    }

    /// <summary>Record the cheaper of the two directions of one edge, when both ends are collapsible and the
    /// cost is inside the error budget. The merged quadric is the same whichever end survives, so it is
    /// summed once and scored at both: the squared distance the merged vertex would sit from every plane.</summary>
    private static int Offer(Quadric[] q, ReadOnlySpan<Vector3> pos, bool[] locked, int a, int b,
                             double[] eKey, int[] eFrom, int[] eTo, int n, double limit)
    {
        if (a == b || locked[a] || locked[b]) return n;
        Quadric sum = Quadric.Sum(in q[a], in q[b]);
        Vector3 pa = pos[a], pb = pos[b];
        double ca = sum.At(pa.X, pa.Y, pa.Z);
        double cb = sum.At(pb.X, pb.Y, pb.Z);
        double cost = ca < cb ? ca : cb;
        if (!(cost <= limit)) return n;
        eKey[n] = cost;
        if (ca < cb) { eFrom[n] = b; eTo[n] = a; } else { eFrom[n] = a; eTo[n] = b; }
        return n + 1;
    }

    /// <summary>The unit plane of one triangle, or all zeroes for a degenerate one.</summary>
    private static TriPlane Plane(ReadOnlySpan<Vector3> pos, int a, int b, int c)
    {
        Vector3 pa = pos[a], u = pos[b] - pa, v = pos[c] - pa;
        double ax = u.X, ay = u.Y, az = u.Z;
        double bx = v.X, by = v.Y, bz = v.Z;
        double nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-20d) return default;
        nx /= len; ny /= len; nz /= len;
        return new TriPlane(nx, ny, nz, -(nx * pa.X + ny * pa.Y + nz * pa.Z));
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
    private static bool Flips(ReadOnlySpan<TriCorners> tri, ReadOnlySpan<Vector3> pos,
                              int[] rep, int[] head, int[] next, int from, int to)
    {
        for (int c = head[from]; c >= 0; c = next[c])
        {
            TriCorners tc = tri[c / 3];
            int a = Rep(rep, tc.A), b = Rep(rep, tc.B), d = Rep(rep, tc.C);
            if (a == b || b == d || a == d) continue;
            int na = a == from ? to : a, nb = b == from ? to : b, nd = d == from ? to : d;
            if (na == nb || nb == nd || na == nd) continue;   // the collapse retires this triangle
            if (Plane(pos, a, b, d).NormalDot(Plane(pos, na, nb, nd)) <= 0d) return true;
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
    private static int Retired(ReadOnlySpan<TriCorners> tri, int[] rep, int[] head, int[] next, int from, int to)
    {
        int died = 0;
        for (int c = head[from]; c >= 0; c = next[c])
        {
            TriCorners tc = tri[c / 3];
            int a = Rep(rep, tc.A), b = Rep(rep, tc.B), d = Rep(rep, tc.C);
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

        // A unit plane through z = 2: the quadric of one plane scores the squared distance from it, so a
        // point one unit away scores 1 -- not the 25 or 49 a crossed lane would read off x or y.
        Quadric zPlane = default;
        zPlane.Add(new TriPlane(0d, 0d, 1d, -2d));
        double one = zPlane.At(5d, 7d, 3d);
        check.Expect(Math.Abs(one - 1d) < 1e-12d, $"a quadric crossed its lanes: {one} one unit from its plane");
        Quadric xPlane = default;
        xPlane.Add(new TriPlane(1d, 0d, 0d, -1d));
        double both = Quadric.Sum(in zPlane, in xPlane).At(3d, 0d, 4d);
        check.Expect(Math.Abs(both - 8d) < 1e-12d, $"two quadrics did not sum field by field: {both}, not 4 + 4");
        zPlane.Add(in xPlane);
        double folded = zPlane.At(3d, 0d, 4d);
        check.Expect(Math.Abs(folded - 8d) < 1e-12d,
                     $"folding a quadric in place scores {folded}, not the 4 + 4 the pair summed to");

        var run = new ushort[] { 10, 11, 12, 20, 21, 22, 30, 31, 32 };
        var runTris = MemoryMarshal.Cast<ushort, TriCorners>(run.AsSpan());
        check.Expect(runTris.Length == 3, $"a nine-index run read as {runTris.Length} triangles, not three");
        check.Expect(runTris[1].A == 20 && runTris[1].B == 21 && runTris[1].C == 22,
                     $"the second triangle of a run is {runTris[1]}, not its second three indices");

        const int N = 12;                         // 12x12 vertices, 11x11 quads, 242 triangles
        int verts = N * N, tris = (N - 1) * (N - 1) * 2;
        var pos = new Vector3[verts];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                int v = y * N + x;
                // A gentle dome, so the quadrics are not all coplanar and the collapse order has to matter.
                pos[v] = new Vector3(x, y, 0.35f * MathF.Sin(x * 0.5f) * MathF.Cos(y * 0.5f));
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

        int got = Run(idx.AsSpan(0, n), pos, tris / 2, 1f, outIdx, double.MaxValue);
        check.Expect(got > 0 && got % 3 == 0, "the grid produced no simplified index run");
        check.Expect(got < n, $"the output is not smaller than the input ({got} of {n})");
        bool bad = false, degenerate = false;
        foreach (TriCorners tc in MemoryMarshal.Cast<ushort, TriCorners>(outIdx.AsSpan(0, got)))
        {
            if (tc.A >= verts || tc.B >= verts || tc.C >= verts) bad = true;
            if (tc.A == tc.B || tc.B == tc.C || tc.A == tc.C) degenerate = true;
        }
        check.Expect(!bad, "an output index is out of range");
        check.Expect(!degenerate, "an output triangle is degenerate");

        // Every vertex of a 2x2 grid is on its border, so there is nothing legal to collapse.
        var sq = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, new Vector3(1f, 1f, 0f) };
        var sqIdx = new ushort[] { 0, 1, 2, 1, 3, 2 };
        check.Expect(Run(sqIdx, sq, 1, 1f, outIdx) == 0, "an all-border mesh was collapsed anyway");
        // A zero error budget refuses every collapse that is not exactly free.
        check.Expect(Run(idx.AsSpan(0, n), pos, 1, 0f, outIdx) == 0, "a zero error budget still collapsed a curved mesh");

        // A 22-unit flat wall with a 0.3-deep recess in its middle: the relative budget alone would flatten the
        // recess (it is 1% of the diagonal); the absolute cap must keep it, and still thin the flat rest.
        var wall = new Vector3[verts];
        for (int v = 0; v < verts; v++)
        {
            int x = v % N, y = v / N;
            wall[v] = new Vector3(x * 2f, y * 2f, x >= 5 && x <= 6 && y >= 5 && y <= 6 ? -0.3f : 0f);
        }
        int wn = Run(idx.AsSpan(0, n), wall, tris / 5, MeshLod.Error2, outIdx);
        bool recess = false;
        for (int i = 0; i < wn; i++) if (wall[outIdx[i]].Z < -0.29f) recess = true;
        check.Expect(wn > 0 && wn < n, $"the flat part of a large wall still thins ({wn} of {n})");
        check.Expect(recess, "a large mesh keeps a 0.3-unit recess at level 2: no collapse moves a surface past MaxShift");
        int un = Run(idx.AsSpan(0, n), wall, tris / 5, MeshLod.Error2, outIdx, double.MaxValue);
        bool kept = false;
        for (int i = 0; i < un; i++) if (wall[outIdx[i]].Z < -0.29f) kept = true;
        check.Expect(un > 0 && !kept, "fixture: with no cap the same budget DOES flatten the recess (else the check above proves nothing)");

        log.Info(check.Ok ? "MeshSimplify.SelfCheck: passed" : "MeshSimplify.SelfCheck: FAILED");
        return check.Ok;
    }
}
