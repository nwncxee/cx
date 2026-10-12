using System;
using System.Collections.Generic;
using System.Numerics;

namespace CX.Alloy.Db;

/// <summary>The arena read back on the HOST, for consumers that want triangles rather than a draw -- today the
/// acoustic scene and the lightmap charter. It reads the arena's own mirrors, so it costs no GL call and no
/// readback, which the driver would answer by waiting out the whole queued frame.</summary>
public static unsafe partial class MeshArena
{
    /// <summary>Append <paramref name="range"/>'s finest triangle list to <paramref name="verts"/> (xyz triples,
    /// MESH space -- the instance matrix is the caller's business) and its triangles to <paramref name="tris"/>,
    /// each index biased by where this range's vertices landed. False = gone or no triangles, nothing appended.
    /// Cold, off the render thread, so it takes the live table under the lock rather than the draw snapshot.</summary>
    internal static bool HostTriangles(int range, List<float> verts, List<int> tris)
    {
        lock (_gate)
        {
            if (!Alive(range)) return false;
            ref Range r = ref _ranges[range];
            if (r.Cnt0 < 3 || r.NV <= 0) return false;
            Section s = _sect[(int)r.Format];
            int stride = StrideOf[(int)r.Format];
            long at = (long)r.Off0 / 2;
            if ((long)(r.Base + r.NV) * stride > s.Mirror.Length || at + r.Cnt0 > _iUsed) return false;

            int first = verts.Count / 3;
            for (int v = 0; v < r.NV; v++)
            {
                int o = (r.Base + v) * stride;
                verts.Add(s.Mirror[o]); verts.Add(s.Mirror[o + 1]); verts.Add(s.Mirror[o + 2]);
            }
            for (long k = 0; k + 2 < r.Cnt0; k += 3)
            {
                int a = _iMirror[at + k], b = _iMirror[at + k + 1], c = _iMirror[at + k + 2];
                if ((uint)a >= (uint)r.NV || (uint)b >= (uint)r.NV || (uint)c >= (uint)r.NV) continue;
                tris.Add(first + a); tris.Add(first + b); tris.Add(first + c);
            }
            return true;
        }
    }

    /// <summary>The shape of <paramref name="range"/>'s finest run, for a consumer that must size its
    /// own buffers before it copies -- <see cref="CX.Alloy.MeshChart"/>'s DB offer. False = nothing to chart
    /// (gone, charted already, skinned, or under a triangle).</summary>
    internal static bool HostMeshSize(int range, out int nv, out int ni, out ulong content, out uint gen)
    {
        nv = ni = 0; content = 0; gen = 0;
        lock (_gate)
        {
            if (!Alive(range)) return false;
            ref Range r = ref _ranges[range];
            // A charted range is the chart's OWN output: charting it again would key a chart on a chart.
            if (r.Charted || r.Offered || r.Format != Fmt.Static || r.Cnt0 < 3 || r.NV <= 0) return false;
            nv = r.NV; ni = (int)r.Cnt0; content = r.Content; gen = (uint)r.Gen;
            return true;
        }
    }

    /// <summary>Copy that run's attributes out, densely: the arena's vertices are already range-local, so the
    /// indices come back as they are and the caller needs no remap. Spans must be at least the sizes
    /// <see cref="HostMeshSize"/> gave, for the same range, nothing freed in between -- both calls take the lock,
    /// and a range that died between them answers false. Cold: once per mesh per session.</summary>
    internal static bool HostMeshCopy(int range, Span<float> pos, Span<float> nrm, Span<float> uv,
                                      Span<uint> tangent, Span<uint> paint, Span<ushort> idx)
    {
        lock (_gate)
        {
            if (!Alive(range)) return false;
            ref Range r = ref _ranges[range];
            if (r.Charted || r.Format != Fmt.Static || r.Cnt0 < 3 || r.NV <= 0) return false;
            Section s = _sect[(int)Fmt.Static];
            int stride = StrideOf[(int)Fmt.Static];
            int tanWord = TanWord(Fmt.Static), paintWord = PaintWord(Fmt.Static);
            long at = (long)r.Off0 / 2;
            if ((long)(r.Base + r.NV) * stride > s.Mirror.Length || at + r.Cnt0 > _iUsed) return false;
            if (pos.Length < r.NV * 3 || nrm.Length < r.NV * 3 || uv.Length < r.NV * 2 || idx.Length < r.Cnt0) return false;
            if (tangent.Length < r.NV || paint.Length < r.NV) return false;
            for (int v = 0; v < r.NV; v++)
            {
                int o = (r.Base + v) * stride;
                pos[v * 3] = s.Mirror[o]; pos[v * 3 + 1] = s.Mirror[o + 1]; pos[v * 3 + 2] = s.Mirror[o + 2];
                nrm[v * 3] = s.Mirror[o + 3]; nrm[v * 3 + 1] = s.Mirror[o + 4]; nrm[v * 3 + 2] = s.Mirror[o + 5];
                uv[v * 2] = s.Mirror[o + 6]; uv[v * 2 + 1] = s.Mirror[o + 7];
                // The packed words ride through the chart untouched: a chart SPLITS vertices and adds a UV
                // set, so a charted vertex's tangent and paint are its source vertex's.
                tangent[v] = BitConverter.SingleToUInt32Bits(s.Mirror[o + tanWord]);
                paint[v] = BitConverter.SingleToUInt32Bits(s.Mirror[o + paintWord]);
            }
            for (long k = 0; k < r.Cnt0; k++)
            {
                ushort v = _iMirror[at + k];
                if (v >= r.NV) return false;
                idx[(int)k] = v;
            }
            return true;
        }
    }

    /// <summary>A bakeable part was seated on this range; <see cref="NextOfferable"/> hands it to the charter.</summary>
    internal static void WantBake(int range)
    {
        lock (_gate) { if (Alive(range)) _ranges[range].Bake = true; }
    }

    /// <summary>The offer was made: this range is the charter's now, and no later frame re-offers it.
    /// <see cref="HostMeshSize"/> refuses a marked range, which is what keeps the DB offer once per mesh.</summary>
    internal static void MarkOffered(int range)
    {
        lock (_gate) { if (Alive(range)) _ranges[range].Offered = true; }
    }

    /// <summary>One range's offer mark goes back -- its chart entry was swept, so nothing will file one.</summary>
    internal static void ClearOffered(int range)
    {
        lock (_gate) { if (Alive(range) && !_ranges[range].Charted) _ranges[range].Offered = false; }
    }

    /// <summary>Every offer mark goes back. The mark means "this range is the charter's already", which a world
    /// edge undoes: <see cref="CX.Alloy.MeshChart.Revoke"/> drops every chart entry, so a range that kept its mark
    /// would never be offered again and the next area would never chart. A CHARTED range keeps its mark -- it is
    /// the charter's own output, not an input to anything.</summary>
    internal static void ClearOfferMarks()
    {
        lock (_gate)
            for (int i = 0; i < _ranges.Length; i++)
                if (_ranges[i].Live && !_ranges[i].Charted) _ranges[i].Offered = false;
    }

    /// <summary>The next range the charter has not been offered, from <paramref name="cursor"/> onwards (the
    /// cursor advances past it and wraps to 0 when it runs out). -1 = none left this sweep. Offers run on the
    /// arena's creation edge, not the first draw: an area's geometry is in the arena while the loading screen is
    /// up, and the bake's hold is waiting for exactly those charts.</summary>
    internal static int NextOfferable(ref int cursor)
    {
        lock (_gate)
        {
            int n = _ranges.Length;
            if (cursor >= n) cursor = 0;
            for (int k = 0; k < n; k++)
            {
                int i = cursor + k;
                if (i >= n) i -= n;
                ref Range r = ref _ranges[i];
                if (r.Live && r.Bake && !r.Charted && !r.Offered && r.Format == Fmt.Static && r.Cnt0 >= 3 && r.NV > 0)
                { cursor = i + 1; return i; }
            }
            cursor = 0;
            return -1;
        }
    }

    /// <summary>Transform the vertices from <paramref name="from"/> (a vertex index, not a float index) to the
    /// end of <paramref name="verts"/> in place by <paramref name="m"/>: what turns a just-appended mesh-space
    /// run into world space.</summary>
    internal static void WorldizeFrom(List<float> verts, int from, in Matrix4x4 m)
    {
        for (int v = from; v * 3 + 2 < verts.Count; v++)
        {
            var p = Vector3.Transform(new Vector3(verts[v * 3], verts[v * 3 + 1], verts[v * 3 + 2]), m);
            verts[v * 3] = p.X; verts[v * 3 + 1] = p.Y; verts[v * 3 + 2] = p.Z;
        }
    }
}
