using System.Numerics;
using System;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Per-face normals welded into smooth ones, once per newly enrolled gob part. NWN heads and many
/// placeables ship every triangle with its own three vertices and a flat normal, so a terminator on them is a
/// solid wedge no wrap width can soften. Crease-preserving: a vertex takes the area-weighted sum of every face at
/// its bitwise-equal position within <see cref="Angle"/> degrees of its own, so a box corner stays a box corner.</summary>
internal static unsafe class NormalWeld
{
    /// <summary>Crease threshold in degrees; 0 disables. <c>/alloy weld</c>. Applies to parts enrolled after
    /// the change, so a re-tune shows on the next area load or relogin.</summary>
    internal static float Angle => Renderer.Weld.V;

    /// <summary>Corner ceiling per part: ushort indices bound a part to 65536 distinct vertices, and a gob
    /// part's index count sits far under this. A part over it is left alone and counted.</summary>
    private const int MaxCorners = 1 << 17;
    private const int Slots = 1 << 18;   // open addressing at <= 50% load
    private const int MaxVerts = 1 << 16;

    private static long[] _key = null!;
    private static float[] _kx = null!, _ky = null!, _kz = null!;
    private static int[] _slotGen = null!, _head = null!, _next = null!, _cornerTri = null!, _vertGen = null!;
    private static float[] _fn = null!;
    private static int _gen;
    private static ExtenderLog? _log;
    private static int _logLeft = 12;

    internal static int Parts, Welded, Kept, Refused;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _key = new long[Slots]; _kx = new float[Slots]; _ky = new float[Slots]; _kz = new float[Slots];
        _slotGen = new int[Slots]; _head = new int[Slots];
        _next = new int[MaxCorners]; _cornerTri = new int[MaxCorners];
        _fn = new float[MaxCorners];   // 3 floats per tri, tris <= MaxCorners/3
        _vertGen = new int[MaxVerts];
    }

    /// <summary>Weld one part's range off the pools' permanent host shadows -- nothing is read back off the GPU,
    /// and the one GL call is a sub-data upload of the touched span to <paramref name="nrm"/>'s buffer.
    /// <para>ponytail: idempotence holds only while a fold's face membership is stable -- a shared range welded
    /// twice near the threshold can drift by one face. Track welded ranges by (vdo,item) if it ever shows.</para></summary>
    internal static void Run(byte* elemHost, byte* vertHost, long first, int count, int baseVertex,
                             in VertexStream pos, in VertexStream nrm)
    {
        if (Angle <= 0f || _key == null || count < 3 || count > MaxCorners) { Refused++; return; }
        if (pos.Type != Gl.FLOAT || pos.Size != 3 || pos.Buf != nrm.Buf) { Refused++; return; }
        var idx = new HostIndices(elemHost, first);
        int minI = int.MaxValue, maxI = -1;
        for (int i = 0; i < count; i++) { int v = idx[i]; if (v < minI) minI = v; if (v > maxI) maxI = v; }
        if (maxI - minI >= MaxVerts) { Refused++; return; }
        int gen = ++_gen;
        int tris = count / 3;
        // pass 1: face normals (area-weighted) + cluster corners by exact position
        for (int t = 0; t < tris; t++)
        {
            ref Vector3 p0 = ref Pos(vertHost, pos, baseVertex + idx[t * 3]);
            ref Vector3 p1 = ref Pos(vertHost, pos, baseVertex + idx[t * 3 + 1]);
            ref Vector3 p2 = ref Pos(vertHost, pos, baseVertex + idx[t * 3 + 2]);
            float ax = p1.X - p0.X, ay = p1.Y - p0.Y, az = p1.Z - p0.Z;
            float bx = p2.X - p0.X, by = p2.Y - p0.Y, bz = p2.Z - p0.Z;
            float fx = ay * bz - az * by, fy = az * bx - ax * bz, fz = ax * by - ay * bx;
            // winding-indifferent: sign the face by its own corners' authored normals, not by a guess at
            // the engine's winding -- a mesh that winds the other way would otherwise weld nothing, silently
            ref Vector3 n0 = ref Normal(vertHost, nrm, baseVertex + idx[t * 3]);
            ref Vector3 n1 = ref Normal(vertHost, nrm, baseVertex + idx[t * 3 + 1]);
            ref Vector3 n2 = ref Normal(vertHost, nrm, baseVertex + idx[t * 3 + 2]);
            if (fx * (n0.X + n1.X + n2.X) + fy * (n0.Y + n1.Y + n2.Y) + fz * (n0.Z + n1.Z + n2.Z) < 0f)
            { fx = -fx; fy = -fy; fz = -fz; }
            _fn[t * 3] = fx; _fn[t * 3 + 1] = fy; _fn[t * 3 + 2] = fz;
            for (int c = 0; c < 3; c++)
            {
                int corner = t * 3 + c;
                ref Vector3 p = ref (c == 0 ? ref p0 : ref (c == 1 ? ref p1 : ref p2));
                int s = Slot(p, gen);
                _cornerTri[corner] = t; _next[corner] = _head[s]; _head[s] = corner;
            }
        }
        // pass 2: each vertex sums the faces at its position within the crease threshold of its own normal
        float cosT = MathF.Cos(Angle * MathF.PI / 180f);
        int welded = 0, kept = 0;
        for (int i = 0; i < count; i++)
        {
            int v = idx[i];
            if (_vertGen[v - minI] == gen) continue;
            _vertGen[v - minI] = gen;
            ref Vector3 p = ref Pos(vertHost, pos, baseVertex + v);
            ref Vector3 n = ref Normal(vertHost, nrm, baseVertex + v);
            float nl = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            if (nl < 1e-8f) { kept++; continue; }
            float sx = 0, sy = 0, sz = 0; int faces = 0;
            for (int c = _head[Slot(p, gen)]; c >= 0; c = _next[c])
            {
                int t = _cornerTri[c] * 3;
                float fx = _fn[t], fy = _fn[t + 1], fz = _fn[t + 2];
                float fl = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
                if (fl < 1e-12f) continue;
                if ((fx * n.X + fy * n.Y + fz * n.Z) / (fl * nl) < cosT) continue;
                sx += fx; sy += fy; sz += fz; faces++;
            }
            float sl = MathF.Sqrt(sx * sx + sy * sy + sz * sz);
            if (faces < 2 || sl < 1e-12f) { kept++; continue; }
            n.X = sx / sl; n.Y = sy / sl; n.Z = sz / sl; welded++;
        }
        Parts++; Welded += welded; Kept += kept;
        if (welded == 0) return;
        new HostShadow(vertHost).UploadVertices(nrm, baseVertex + minI, maxI - minI + 1);
        if (_logLeft-- > 0)
            _log?.Info($"NormalWeld: {welded} welded / {kept} kept of {maxI - minI + 1} verts ({tris} tris, {Angle:F0} deg)");
    }

    private static ref Vector3 Pos(byte* host, in VertexStream s, int v) => ref new HostVertices(host, s).Pos(v);

    private static ref Vector3 Normal(byte* host, in VertexStream s, int v) => ref new HostVertices(host, s).Normal(v);

    /// <summary>Parts fed from a parsed model's own post-process, and the ones that could not be.</summary>
    internal static int ModelParts, ModelRefused;

    /// <summary>Copy welded, smoothing-group aware normals straight from a parsed <c>MeshDef</c> rather than
    /// re-deriving them from the host shadow: one mesh is parsed once however many parts instance it.
    /// <para>The engine's <c>GenVertices</c> emits one GPU vertex per post-processed mesh vertex in order, so part
    /// vertex <c>i</c> is mesh vertex <c>i</c>; a part whose range outruns the mesh is refused, not half-written.</para></summary>
    internal static bool FromModel(ReadOnlySpan<System.Numerics.Vector3> normals, byte* vertHost,
                                   int baseVertex, int vertCount, in VertexStream nrm)
    {
        if (vertHost == null || vertCount <= 0 || normals.Length < vertCount
            || nrm.Type != Gl.FLOAT || nrm.Size < 3) { ModelRefused++; return false; }
        for (int v = 0; v < vertCount; v++)
        {
            ref Vector3 n = ref Normal(vertHost, nrm, baseVertex + v);
            n.X = normals[v].X; n.Y = normals[v].Y; n.Z = normals[v].Z;
        }
        new HostShadow(vertHost).UploadVertices(nrm, baseVertex, vertCount);
        ModelParts++;
        return true;
    }

    /// <summary>Slot for a bitwise-equal position; a fresh one is claimed with an empty corner chain.</summary>
    private static int Slot(in Vector3 p, int gen)
    {
        int bx = BitConverter.SingleToInt32Bits(p.X), by = BitConverter.SingleToInt32Bits(p.Y), bz = BitConverter.SingleToInt32Bits(p.Z);
        long k = ((long)bx * 0x9E3779B1L) ^ ((long)by * 0x85EBCA77L) ^ ((long)bz * 0xC2B2AE3DL);
        int s = (int)(k ^ (k >> 29)) & (Slots - 1);
        while (true)
        {
            if (_slotGen[s] != gen)
            { _slotGen[s] = gen; _key[s] = k; _kx[s] = p.X; _ky[s] = p.Y; _kz[s] = p.Z; _head[s] = -1; return s; }
            if (_key[s] == k && _kx[s] == p.X && _ky[s] == p.Y && _kz[s] == p.Z) return s;
            s = (s + 1) & (Slots - 1);
        }
    }

    /// <summary>Off-device: two flat-shaded triangles meeting at a 20-degree fold (six vertices, two per shared
    /// position) weld to one blended normal on the shared edge, leave the unshared corners alone, and a
    /// second pass changes nothing.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        if (_key == null) Attach(log);
        // pos(3) + nrm(3) interleaved, stride 24; quad in XY tilted: tri A normal +Z, tri B normal rotated 20deg
        float[] verts = {
            0,0,0, 0,0,1,  1,0,0, 0,0,1,  0,1,0, 0,0,1,          // tri A: flat +Z
            1,0,0, 0,-0.342f,0.94f,  1,1,0.364f, 0,-0.342f,0.94f,  0,1,0, 0,-0.342f,0.94f };  // tri B: tilted 20deg
        ushort[] idx = { 0, 1, 2, 3, 4, 5 };
        var pos = new VertexStream { Buf = 1, Size = 3, Type = Gl.FLOAT, Stride = 24, Off = 0 };
        var nrm = new VertexStream { Buf = 1, Size = 3, Type = Gl.FLOAT, Stride = 24, Off = 12 };
        bool ok = true;
        fixed (float* v = verts) fixed (ushort* e = idx)
        {
            var sub = Gl.NamedBufferSubData;
            Gl.NamedBufferSubData = &NoSub;
            // Plain field writes on the knob: nothing runs, nothing is written to the file.
            float save = Angle; int logLeft = _logLeft;
            Renderer.Weld.V = 60f; Run((byte*)e, (byte*)v, 0, 6, 0, pos, nrm);
            // shared verts (1,0,0) and (0,1,0) now carry the same blended normal on both triangles
            ok &= MathF.Abs(verts[1 * 6 + 5] - verts[3 * 6 + 5]) < 1e-6f && verts[1 * 6 + 4] < -0.1f;
            ok &= verts[0 * 6 + 5] == 1f;                                   // unshared corner untouched
            float once = verts[1 * 6 + 5];
            Run((byte*)e, (byte*)v, 0, 6, 0, pos, nrm);                     // idempotent on the welded result
            ok &= verts[1 * 6 + 5] == once;
            Renderer.Weld.V = save; Gl.NamedBufferSubData = sub;
            _logLeft = logLeft; Parts = Welded = Kept = Refused = 0;
        }
        log.Info(ok ? "NormalWeld.SelfCheck: passed" : "NormalWeld.SelfCheck FAIL");
        return ok;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly] private static void NoSub(uint a, IntPtr b, IntPtr c, void* d) { }
}
