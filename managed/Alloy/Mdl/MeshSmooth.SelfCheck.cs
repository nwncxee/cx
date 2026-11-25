using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy.Mdl;

internal static partial class MeshSmooth
{
    /// <summary>Off-device: a smooth-normalled icosahedron rounds toward its sphere and stays closed even with every face
    /// on its own vertices (a seam on every edge), and a hard-edged cube is left untouched.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MeshSmooth.SelfCheck");
        foreach (bool split in (ReadOnlySpan<bool>)[false, true])
        {
            MeshDef m = Icosahedron(split);
            int before = m.Indices.Length / 3;
            check.Expect(Smooth(m), $"split={split}: the icosahedron is smoothed");
            check.Expect(m.Indices.Length / 3 == before * FineLevel * FineLevel && m.Faces.Length * 3 == m.Indices.Length,
                         $"split={split}: each face became {FineLevel * FineLevel}, faces kept aligned");
            float lo = float.MaxValue, hi = 0f;
            foreach (Vector3 p in m.Positions) { lo = MathF.Min(lo, p.Length()); hi = MathF.Max(hi, p.Length()); }
            check.Expect(lo > 0.8f && hi < 1.05f, $"split={split}: points lie near the sphere ({lo:F3}..{hi:F3})");
            check.Expect(Closed(m), $"split={split}: every edge is shared by exactly two faces, bit for bit");
        }
        MeshDef cube = Cube();
        check.Expect(!Smooth(cube) && cube.Indices.Length == 36, "a hard-edged cube is left as it is");
        MeshDef plate = Plate();
        check.Expect(!Smooth(plate), "a flat top whose rim normals lean out is not domed");
        MeshDef pole = Pole();
        check.Expect(!Smooth(pole), "a long pole whose normals all lean one way is not swollen");
        if (check.Ok) log.Info("MeshSmooth.SelfCheck: passed");
        return check.Ok;
    }

    /// <summary>A unit icosahedron, normals along the positions; <paramref name="split"/> gives each face its own vertices.</summary>
    private static MeshDef Icosahedron(bool split)
    {
        const float phi = 1.618034f;
        var c = new List<Vector3>();
        foreach (float a in (ReadOnlySpan<float>)[-1f, 1f])
            foreach (float b in (ReadOnlySpan<float>)[-phi, phi])
            { c.Add(new Vector3(0f, a, b)); c.Add(new Vector3(a, b, 0f)); c.Add(new Vector3(b, 0f, a)); }
        // the faces are the corner triples two apart from each other, wound outward
        var faces = new List<Tri>();
        for (int i = 0; i < c.Count; i++)
            for (int j = i + 1; j < c.Count; j++)
                for (int k = j + 1; k < c.Count; k++)
                {
                    if (!Two(c[i], c[j]) || !Two(c[j], c[k]) || !Two(c[i], c[k])) continue;
                    bool outward = Vector3.Dot(Vector3.Cross(c[j] - c[i], c[k] - c[i]), c[i] + c[j] + c[k]) > 0f;
                    faces.Add(outward ? new Tri { A = (uint)i, B = (uint)j, C = (uint)k } : new Tri { A = (uint)i, B = (uint)k, C = (uint)j });
                }
        for (int i = 0; i < c.Count; i++) c[i] = Vector3.Normalize(c[i]);
        var pos = new List<Vector3>();
        var tris = new List<Tri>();
        if (split)
            foreach (Tri t in faces)
            {
                uint at = (uint)pos.Count;
                pos.Add(c[(int)t.A]); pos.Add(c[(int)t.B]); pos.Add(c[(int)t.C]);
                tris.Add(new Tri { A = at, B = at + 1, C = at + 2 });
            }
        else { pos.AddRange(c); tris.AddRange(faces); }
        return Mesh(pos.ToArray(), pos.ToArray(), tris);
    }

    private static bool Two(Vector3 a, Vector3 b) => MathF.Abs(Vector3.Distance(a, b) - 2f) < 1e-3f;

    /// <summary>A unit cube with a normal per face.</summary>
    private static MeshDef Cube()
    {
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var tris = new List<Tri>();
        foreach (Vector3 n in (ReadOnlySpan<Vector3>)[Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ])
        {
            Vector3 u = MathF.Abs(n.X) > 0.5f ? Vector3.UnitY : Vector3.UnitX, v = Vector3.Cross(n, u);
            uint at = (uint)pos.Count;
            foreach ((float su, float sv) in (ReadOnlySpan<(float, float)>)[(-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f)])
            { pos.Add(n + u * su + v * sv); nrm.Add(n); }
            tris.Add(new Tri { A = at, B = at + 1, C = at + 2 });
            tris.Add(new Tri { A = at, B = at + 2, C = at + 3 });
        }
        return Mesh(pos.ToArray(), nrm.ToArray(), tris);
    }

    /// <summary>A flat square fan around a centre, the rim's normals leaning 20 deg out as a table top's often do.</summary>
    private static MeshDef Plate()
    {
        var pos = new List<Vector3> { Vector3.Zero };
        var nrm = new List<Vector3> { Vector3.UnitZ };
        var tris = new List<Tri>();
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.PI / 4f;
            var out_ = new Vector3(MathF.Cos(a), MathF.Sin(a), 0f);
            pos.Add(out_);
            nrm.Add(Vector3.Normalize(Vector3.UnitZ + out_ * MathF.Tan(20f * MathF.PI / 180f)));
            tris.Add(new Tri { A = 0, B = (uint)(1 + k), C = (uint)(1 + (k + 1) % 8) });
        }
        return Mesh(pos.ToArray(), nrm.ToArray(), tris);
    }

    /// <summary>An eight-sided pole 3 m long and 5 cm thick, open at both ends, its normals radial but leaning 25 deg along it.</summary>
    private static MeshDef Pole()
    {
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var tris = new List<Tri>();
        float lean = MathF.Tan(25f * MathF.PI / 180f);
        for (int end = 0; end < 2; end++)
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4f;
                var radial = new Vector3(0f, MathF.Cos(a), MathF.Sin(a));
                pos.Add(new Vector3(end * 3f, 0f, 0f) + radial * 0.05f);
                nrm.Add(Vector3.Normalize(radial + Vector3.UnitX * lean));
            }
        for (int k = 0; k < 8; k++)
        {
            uint a = (uint)k, b = (uint)((k + 1) % 8);
            tris.Add(new Tri { A = a, B = 8 + a, C = 8 + b });
            tris.Add(new Tri { A = a, B = 8 + b, C = b });
        }
        return Mesh(pos.ToArray(), nrm.ToArray(), tris);
    }

    private static MeshDef Mesh(Vector3[] pos, Vector3[] nrm, List<Tri> tris)
    {
        var m = new MeshDef { Positions = pos, Normals = nrm };
        m.Indices = MemoryMarshal.Cast<Tri, uint>(CollectionsMarshal.AsSpan(tris)).ToArray();
        m.Faces = new FaceDef[tris.Count];
        m.BMin = -Vector3.One; m.BMax = Vector3.One; m.Radius = 1f;
        return m;
    }

    /// <summary>Every edge, keyed by its two end points' exact bits, met by exactly two faces.</summary>
    private static bool Closed(MeshDef m)
    {
        var count = new Dictionary<(Vector3, Vector3), int>();
        foreach (Tri t in MemoryMarshal.Cast<uint, Tri>(m.Indices.AsSpan()))
            foreach ((uint a, uint b) in (ReadOnlySpan<(uint, uint)>)[(t.A, t.B), (t.B, t.C), (t.C, t.A)])
            {
                Vector3 p = m.Positions[a], q = m.Positions[b];
                var k = Order(p, q);
                count[k] = count.GetValueOrDefault(k) + 1;
            }
        foreach (int n in count.Values) if (n != 2) return false;
        return true;
    }

    private static (Vector3, Vector3) Order(Vector3 p, Vector3 q)
        => (p.X, p.Y, p.Z).CompareTo((q.X, q.Y, q.Z)) <= 0 ? (p, q) : (q, p);
}
