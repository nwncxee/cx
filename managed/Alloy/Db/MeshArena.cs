using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>
/// <b>Alloy's one model-space vertex arena.</b> A distinct mesh is uploaded ONCE, in its own model space, and
/// every part that instances it draws that one range, placed by the matrix <c>GobXform</c> composes. Range ids
/// are refcounted and identity is CONTENT, so two models with the same geometry share a range; indices are
/// always <c>GL_UNSIGNED_SHORT</c>, and <see cref="Pump"/> is the one place a GL name is touched.
/// </summary>
public static unsafe partial class MeshArena
{
    /// <summary>Which vertex layout a range lives in. One VBO and one VAO each. The two <c>Uv</c> formats also
    /// carry the MDL's UV sets 1..3, and only a mesh that has one lives there, so no other mesh pays the stride.</summary>
    public enum Fmt : byte { Static = 0, Skinned = 1, StaticUv = 2, SkinnedUv = 3 }

    internal const int Formats = 4;
    /// <summary>Floats per vertex, per format: pos3 nrm3 uv2 chart2, plus a packed bone-index word and four
    /// weights for the skinned pair, then UV sets 1..3 (2 floats each) for the <c>Uv</c> pair, and LAST in all of
    /// them a packed tangent word and a packed vertex-paint word -- two words rather than five floats.</summary>
    private static readonly int[] StrideOf = { 12, 17, 18, 23 };

    /// <summary>Is <paramref name="f"/> one of the two skinned layouts?</summary>
    internal static bool IsSkinned(Fmt f) => f is Fmt.Skinned or Fmt.SkinnedUv;
    /// <summary>Does <paramref name="f"/> carry UV sets 1..3?</summary>
    internal static bool CarriesUvSets(Fmt f) => f >= Fmt.StaticUv;
    /// <summary>The layout a mesh lives in: skinned or not, with UV sets 1..3 or not.</summary>
    internal static Fmt FormatFor(bool skinned, bool uvSets) => (Fmt)((skinned ? 1 : 0) + (uvSets ? 2 : 0));
    /// <summary>Float index of UV set 1 in a <c>Uv</c> format; sets 2 and 3 follow it, right before the packed words.</summary>
    internal static int UvSetWord(Fmt f) => StrideOf[(int)f] - 8;
    /// <summary>Does <paramref name="m"/> carry any of UV sets 1..3? Vanilla uploads each one whose pool exists
    /// (<c>VertexPrimitiveFlat::SetupArrays</c> @0x458920) at <c>vTcIn1..3</c>, for a content shader to read.</summary>
    internal static bool HasUvSets(MeshDef m) => m.Uv[1].Length != 0 || m.Uv[2].Length != 0 || m.Uv[3].Length != 0;
    private static Section[] NewSections() => new Section[] { new(), new(), new(), new() };

    /// <summary>Float index of the packed tangent word, which is the second-to-last of either format: the
    /// two packed words sit after the format's own streams, so <see cref="Fmt.Skinned"/>'s bone slots and
    /// weights keep the offsets they have always had.</summary>
    internal static int TanWord(Fmt f) => StrideOf[(int)f] - 2;
    /// <inheritdoc cref="TanWord"/>
    internal static int PaintWord(Fmt f) => StrideOf[(int)f] - 1;
    /// <summary>Bytes one vertex of <paramref name="f"/> occupies -- the stride every attribute is bound at.</summary>
    internal static int StrideBytes(Fmt f) => StrideOf[(int)f] * 4;

    /// <summary>Vertices one format's section may hold (8M x 48 B = 384 MiB static, grown on demand; the
    /// skinned section is far smaller). Measured: 2M in a 27x24 city of 1654 placeables, 4M hopping INTO a
    /// ~3M-vertex area, because a load lands on the previous area's dead ranges and only the pump compacts.
    /// ponytail: past the ceiling it refuses and counts -- no spill to a second VBO, no mid-load compaction.</summary>
    private const int MaxVerts = 1 << 23;
    /// <summary>Indices the shared element arena may hold (16M x 2 B = 32 MiB): scaled with
    /// <see cref="MaxVerts"/>, since the same area hop doubles it.</summary>
    private const int MaxIndices = 16 << 20;
    /// <summary>A mesh past this cannot be addressed by a 16-bit index. The engine's own vertex count is a
    /// <c>ushort</c> (<c>MdlNodeTriMesh+0x284</c>), so no model file reaches it.</summary>
    private const int MaxMeshVerts = 65536;
    private const int LogCap = 8;

    private static ExtenderLog? _log;
    private static readonly object _gate = new();

    /// <summary>Why a mesh has no range. Counted and named on the period line, never a quiet hole.</summary>
    internal enum Refusal : byte { None, Empty, TooManyVerts, Section, Elements, Table, Again }
    private static readonly string[] RefusalName = { "none", "empty", "verts-over-64k", "section-full", "element-full", "table-full", "again-unchanged" };

    /// <summary>One distinct mesh in the arena: where its vertices are, its three LOD index runs (BYTE offsets
    /// into the shared element arena; a zero count means the simplifier declined that level), its own-space
    /// bounds, how many parts hold it, and the <see cref="MeshDef"/> keys that name it.</summary>
    private struct Range
    {
        internal ulong Content;
        /// <summary>Second hash and the two counts: a 64-bit content hash alone is not identity, so a HIT is
        /// verified before two different meshes are collapsed into one range.</summary>
        internal ulong Content2;
        internal int NVerts, NIndices;
        internal int Refs;
        internal Fmt Format;
        internal int Base, NV;
        internal nint Off0, Off1, Off2;
        internal int Cnt0, Cnt1, Cnt2;
        internal Vector3 BMin, BMax, Center;
        internal float Radius;
        internal bool Live, Charted;
        /// <summary>This range has been handed to the charter already -- the offer is once per mesh, not once
        /// per frame per instance that draws it.</summary>
        internal bool Offered;
        /// <summary>A seated part the bake may take (<c>AlloyRenderer.DbBakeable</c>) draws this range, so
        /// it is worth charting. Only these are offered: a creature's rigid parts never bake.</summary>
        internal bool Bake;
        /// <summary>Bumped every time this slot is handed out: a <see cref="_byMesh"/> entry stamped with an
        /// older one names a range since freed and REUSED, so the id must not be trusted.</summary>
        internal int Gen;
        /// <summary>The <see cref="MeshDef"/> keys that resolve to this range, so the free can take them out
        /// of <see cref="_byMesh"/>, which would otherwise grow for the session and pin every merged mesh's
        /// CPU arrays.</summary>
        internal List<MeshDef>? Keys;
        internal int NextFree;
    }

    private static Range[] _ranges = new Range[1 << 12];
    private static int _rangeHigh = 1, _rangeFree = -1;
    private static readonly Dictionary<ulong, int> _byContent = new();
    /// <summary>Reference identity of a <see cref="MeshDef"/> to its range and that range's generation: a
    /// model's mesh is acquired once per instance, and this spares the content hash on every one after the
    /// first. The generation is what makes a recycled range id safe to cache.</summary>
    private static readonly Dictionary<MeshDef, (int Range, int Gen)> _byMesh = new(ReferenceEqualityComparer.Instance);

    private sealed class Section
    {
        internal float[] Mirror = Array.Empty<float>();
        internal int Used, Uploaded, Capacity, Dead;
        internal uint Vbo, Vao;
        internal bool Full;
    }

    private static Section[] _sect = NewSections();
    private static ushort[] _iMirror = Array.Empty<ushort>();
    private static int _iUsed, _iUploaded, _iCapacity, _iDead;
    private static uint _ebo;
    private static bool _elementsFull;

    private static readonly List<int> _pendingFree = new();
    /// <summary>Ranges whose placement a compaction changed, for the one frame edge that follows it.</summary>
    private static int[] _moved = new int[64];
    private static (int Base, nint Off)[] _movedFrom = new (int, nint)[64];
    private static int _movedN, _movedTotal;

    /// <summary>Told, after a compaction, which ranges moved -- so a consumer that cached a base vertex or an
    /// index offset in a record can refresh exactly those. Set by the draw side
    /// (<c>AlloyRenderer.DbRangesMoved</c>) and run inside <see cref="Pump"/>, on the thread that owns the GL
    /// bracket with the arena's lock held, so the handler reads through <see cref="Runs"/> and nothing else.</summary>
    internal static Action<int[], int>? RangesMoved;
    private static int _acquired, _freed, _refused, _compactions, _logLeft = LogCap;
    private static readonly int[] _refusedBy = new int[RefusalName.Length];
    private static int _overReleased, _heldPastEviction, _collisions, _staleKeys, _unqueued, _glDeferred;
    private static bool _saidDeferred, _dumped;
    /// <summary>Meshes whose normals came from the parse (<see cref="MeshDef.Normals"/>, already welded and
    /// smoothing-group aware) and meshes that had none -- the arena's half of finding 11: the weld runs once
    /// per distinct mesh at the parse, never once per part at enrolment.</summary>
    private static int _weldedFromParse, _noNormals;
    /// <summary>Bytes the per-part (engine-VDO) path would have held for the same geometry: one copy per
    /// HOLDER rather than one per distinct mesh. The difference is the saving the ticket asks for.</summary>
    private static long _unsharedBytes;
    private static int _epoch = 1;

    // ---------------------------------------------------------------- the published snapshot

    /// <summary>What a draw reads, published as ONE immutable array at every edge that can move geometry (a
    /// new range, a free, a compaction, a VAO build, a context loss). The render thread reads this without a
    /// lock while the game thread acquires and releases under <see cref="_gate"/>; nothing a reader holds is
    /// ever written in place (finding 8).</summary>
    private sealed class Pub
    {
        internal readonly uint Vao;
        internal readonly int Base, Cnt0, Cnt1, Cnt2;
        internal readonly nint Off0, Off1, Off2;
        internal readonly Vector3 BMin, BMax, Center;
        internal readonly float Radius;
        internal readonly bool Live;
        /// <summary>The tenant's identity: <see cref="Sig"/> of its content and hand-out generation.</summary>
        internal readonly ulong Sig;
        /// <summary>The vertex layout the range lives in, for a caller that binds buffers rather than the VAO.</summary>
        internal readonly Fmt Format;

        internal Pub(in Range r, uint vao)
        {
            Vao = vao; Base = r.Base; Sig = MeshArena.Sig(r.Content, (uint)r.Gen); Format = r.Format;
            Cnt0 = r.Cnt0; Cnt1 = r.Cnt1; Cnt2 = r.Cnt2;
            Off0 = r.Off0; Off1 = r.Off1; Off2 = r.Off2;
            BMin = r.BMin; BMax = r.BMax; Center = r.Center; Radius = r.Radius;
            Live = r.Live;
        }
    }

    /// <summary>One box per range. A box is never mutated, so publishing ONE range is a single reference
    /// store and costs no array copy -- which matters: a full rewrite per acquired mesh would be quadratic in
    /// the area's mesh count.</summary>
    private static Pub?[] _pub = Array.Empty<Pub?>();

    /// <summary>Publish one range's answer. Under <see cref="_gate"/>.</summary>
    private static void PublishOne(int id)
    {
        if (id >= _pub.Length)
        {
            int cap = _pub.Length == 0 ? 1 << 10 : _pub.Length;
            while (cap <= id) cap *= 2;
            var grown = new Pub?[cap];
            Array.Copy(_pub, grown, _pub.Length);
            _pub = grown;
        }
        _pub[id] = _ranges[id].Live ? new Pub(in _ranges[id], _sect[(int)_ranges[id].Format].Vao) : null;
        _epoch++;
    }

    /// <summary>Republish every range: the edges that move geometry wholesale (a compaction, a VAO build, a
    /// context loss). Under <see cref="_gate"/>.</summary>
    private static void Publish()
    {
        if (_rangeHigh > _pub.Length)
        {
            int cap = _pub.Length == 0 ? 1 << 10 : _pub.Length;
            while (cap < _rangeHigh) cap *= 2;
            _pub = new Pub?[cap];
        }
        for (int i = 1; i < _pub.Length; i++)
            _pub[i] = i < _rangeHigh && _ranges[i].Live ? new Pub(in _ranges[i], _sect[(int)_ranges[i].Format].Vao) : null;
        _epoch++;
    }

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        // the draw side's refresh for the records that cache a range's placement
        RangesMoved = AlloyRenderer.DbRangesMoved;
    }

    /// <summary>The log this arena names its refusals on. Settable so the off-device suite can silence the
    /// per-mesh lines its fixtures would otherwise fill <c>cx.log</c> with.</summary>
    internal static ExtenderLog? Log { get => _log; set => _log = value; }

    /// <summary>Bumped by anything that can change a <see cref="Draw"/> answer (a free, a compaction, a context
    /// loss): a cached answer stamped with an older one must re-probe.</summary>
    internal static int Epoch => _epoch;

    /// <summary>Distinct meshes RESIDENT right now. Counted over the live ranges, not over the key map: a
    /// key whose range has been freed is not a mesh, and counting the map made the period line claim meshes
    /// the arena no longer held.</summary>
    internal static int MeshCount
    {
        get
        {
            int n = 0;
            lock (_gate) for (int i = 1; i < _rangeHigh; i++) if (_ranges[i].Live) n++;
            return n;
        }
    }

    /// <summary>Live holders summed over every range: the sharing factor's numerator. DERIVED, not tracked --
    /// a counter kept in step with every acquire and release drifted on the first live area, and a number on a
    /// report line is not worth a bookkeeping invariant.</summary>
    internal static long Holders
    {
        get
        {
            long n = 0;
            lock (_gate) for (int i = 1; i < _rangeHigh; i++) if (_ranges[i].Live) n += _ranges[i].Refs;
            return n;
        }
    }

    /// <summary>Arena bytes on the GPU: both vertex sections plus the shared element arena. What the
    /// <c>[alloy]</c> line reports as world-geometry VRAM.</summary>
    internal static long VramBytes
    {
        get
        {
            long b = (long)_iUsed * 2;
            for (int f = 0; f < Formats; f++) b += (long)_sect[f].Used * StrideOf[f] * 4;
            return b;
        }
    }

    /// <summary>The VAO over <paramref name="f"/>'s section, zero until the first mesh lands in this context.</summary>
    internal static uint Vao(Fmt f) => _sect[(int)f].Vao;

    // ---------------------------------------------------------------- acquire

    /// <summary>The arena range for <paramref name="mesh"/>, uploading it on first use; -1 when it cannot be
    /// held (counted, the first few named in the log). Game thread, cold; a caller for content already held
    /// gets that range with one more reference. <paramref name="skin"/> non-null puts the mesh in
    /// <see cref="Fmt.Skinned"/>, with the bone slots and weights the pose stage pulls at locations 8 and 9.</summary>
    public static int Acquire(MeshDef mesh, SkinDef? skin = null)
    {
        if (mesh == null) return -1;
        bool skinned = skin != null && skin.Bones.Length >= mesh.Positions.Length
                                    && skin.Weights.Length >= mesh.Positions.Length;
        lock (_gate)
        {
            if (_byMesh.TryGetValue(mesh, out (int Range, int Gen) have)
                && Alive(have.Range) && _ranges[have.Range].Gen == have.Gen)
            {
                Take(have.Range, mesh);
                return have.Range;
            }
            _byMesh.Remove(mesh);
            if (mesh.Indices.Length < 3 || mesh.Positions.Length == 0) return Refuse(Refusal.Empty);
            if (mesh.Positions.Length > MaxMeshVerts) return Refuse(Refusal.TooManyVerts);
            // the feed re-asks a refused part every frame; skip the allocating hash + LOD cut until the arena changes
            if (mesh.ArenaRefused == _epoch) return Refuse(Refusal.Again);
        }

        // The content hash and the LOD cut are the expensive half and touch nothing shared, so they run OFF
        // the lock -- the render thread takes the same one every frame in Pump (finding 7).
        ulong c1 = ContentKey(mesh, skinned ? skin : null, out ulong c2);
        lock (_gate)
        {
            if (_byContent.TryGetValue(c1, out int r) && Alive(r))
            {
                if (Same(in _ranges[r], mesh, c2)) { Take(r, mesh); return r; }
                _collisions++;   // same 64-bit hash, different mesh: it gets a range of its own
            }
        }
        ushort[] i0 = Indices16(mesh);
        CutLods(i0, mesh.Positions, out ushort[]? i1, out int n1, out ushort[]? i2, out int n2);
        lock (_gate)
        {
            // a second thread may have filed the same content while this one was simplifying
            if (_byContent.TryGetValue(c1, out int r) && Alive(r) && Same(in _ranges[r], mesh, c2))
            {
                Take(r, mesh);
                return r;
            }
            int id = Build(mesh, skinned ? skin : null, c1, c2, i0, i1, n1, i2, n2);
            if (id >= 0) _byMesh[mesh] = (id, _ranges[id].Gen);
            else mesh.ArenaRefused = _epoch;
            return id;
        }
    }

    /// <summary>Does <paramref name="range"/> live in one of the skinned layouts -- the bone slots and weights
    /// a palette draw reads at locations 8 and 9? False for a range that is gone.</summary>
    internal static bool SkinnedRange(int range)
    {
        lock (_gate) return Alive(range) && IsSkinned(_ranges[range].Format);
    }

    /// <summary>One more holder, and <paramref name="mesh"/> becomes one of the keys the free takes back.</summary>
    private static void Take(int range, MeshDef mesh)
    {
        _ranges[range].Refs++;
        _unsharedBytes += (long)_ranges[range].NV * StrideOf[(int)_ranges[range].Format] * 4;
        ref List<MeshDef>? keys = ref _ranges[range].Keys;
        keys ??= new List<MeshDef>(1);
        if (!keys.Contains(mesh)) keys.Add(mesh);
        _byMesh[mesh] = (range, _ranges[range].Gen);
    }

    /// <summary>Is this really the same mesh, or a 64-bit hash collision? Counts and the second hash, so two
    /// unrelated meshes are never collapsed into one range (finding 26).</summary>
    private static bool Same(in Range r, MeshDef m, ulong c2)
        => r.Content2 == c2 && r.NVerts == m.Positions.Length && r.NIndices == m.Indices.Length;

    /// <summary>One more holder of <paramref name="range"/> (another object instanced from a placed model).</summary>
    internal static void AddRef(int range)
    {
        lock (_gate)
        {
            if (!Alive(range)) return;
            _ranges[range].Refs++;
            _unsharedBytes += (long)_ranges[range].NV * StrideOf[(int)_ranges[range].Format] * 4;
        }
    }

    /// <summary>Drop one reference; the last one frees the range at the next <see cref="Pump"/> -- a
    /// render-side frame edge, so no draw in flight holds its offsets.</summary>
    public static void Release(int range)
    {
        lock (_gate)
        {
            if (!Alive(range)) return;
            if (_ranges[range].Refs <= 0)
            {
                _overReleased++;
                if (_logLeft-- > 0) _log?.Info($"MeshArena: range {range} released more times than it was taken");
                _pendingFree.Add(range);   // nothing holds it: recover the bytes rather than leak them
                return;
            }
            _unsharedBytes -= (long)_ranges[range].NV * StrideOf[(int)_ranges[range].Format] * 4;
            if (--_ranges[range].Refs > 0) return;
            _pendingFree.Add(range);
        }
    }

    private static bool Alive(int r) => (uint)r < (uint)_rangeHigh && _ranges[r].Live;

    /// <summary>The model-cache eviction edge (<c>ModelCache.Evicting</c>): the model is leaving, so the
    /// reference-identity shortcuts to its meshes go with it. It holds no arena REFERENCE -- those belong to
    /// the parts, and <c>PartTree.Free</c> returns them when the objects die -- so nothing is released here; a
    /// range still held is a part that outlived its model, named once and counted.</summary>
    internal static void Forget(ModelDef model)
    {
        lock (_gate)
            foreach (NodeDef n in model.Nodes)
            {
                if (n.Mesh == null || !_byMesh.Remove(n.Mesh, out (int Range, int Gen) at)) continue;
                if (!Alive(at.Range) || _ranges[at.Range].Gen != at.Gen || _ranges[at.Range].Refs <= 0) continue;
                _heldPastEviction++;
                if (_logLeft-- > 0)
                    _log?.Info($"MeshArena: model '{model.Name}' was evicted while {_ranges[at.Range].Refs} part(s) still draw its mesh");
            }
    }

    private static int Refuse(Refusal why)
    {
        _refused++;
        _refusedBy[(int)why]++;
        if (_logLeft-- > 0) _log?.Info($"MeshArena: REFUSED {RefusalName[(int)why]} -- this mesh draws nothing");
        return -1;
    }

    /// <summary>FNV-1a over the mesh's GPU content: counts, positions, normals, all four UV sets, the two
    /// packed words, the indices and, for a skinned mesh, its bone slots and weights. <paramref name="second"/>
    /// is an independent FNV over the same bytes in the other direction, so a hit is verified rather than
    /// assumed.</summary>
    internal static ulong ContentKey(MeshDef m, SkinDef? skin, out ulong second)
    {
        ulong h = 14695981039346656037UL, g = 1469598103934665603UL;
        void MixU(uint v)
        {
            for (int k = 0; k < 4; k++)
            {
                byte b = (byte)(v >> (k * 8));
                h ^= b; h *= 1099511628211UL;
                g = (g + b) * 0x100000001B3UL ^ (g >> 23);
            }
        }
        void MixF(float v) => MixU(BitConverter.SingleToUInt32Bits(v));
        MixU((uint)m.Positions.Length);
        MixU((uint)m.Indices.Length);
        MixU((uint)(m.Normals.Length != 0 ? 1 : 0) | (uint)(m.Uv[0].Length != 0 ? 2 : 0) | (skin != null ? 4u : 0u)
           | (uint)(m.Tangents.Length != 0 ? 8 : 0) | (uint)(m.Colors.Length != 0 ? 16 : 0) | (HasUvSets(m) ? 32u : 0u));
        for (int i = 0; i < m.Positions.Length; i++) { MixF(m.Positions[i].X); MixF(m.Positions[i].Y); MixF(m.Positions[i].Z); }
        for (int i = 0; i < m.Normals.Length; i++) { MixF(m.Normals[i].X); MixF(m.Normals[i].Y); MixF(m.Normals[i].Z); }
        for (int k = 0; k < m.Uv.Length; k++)
        {
            Vector2[] uv = m.Uv[k];
            MixU((uint)uv.Length);
            for (int i = 0; i < uv.Length; i++) { MixF(uv[i].X); MixF(uv[i].Y); }
        }
        // The two packed words AS UPLOADED: two meshes that differ only in their tangent frame or their paint
        // are different GPU content, so they must not share a range.
        for (int i = 0; i < m.Tangents.Length; i++) MixU(PackTangent(m.Tangents[i]));
        for (int i = 0; i < m.Colors.Length; i++) MixU(m.Colors[i]);
        for (int i = 0; i < m.Indices.Length; i++) MixU(m.Indices[i]);
        if (skin != null)
        {
            int n = Math.Min(m.Positions.Length, Math.Min(skin.Bones.Length, skin.Weights.Length));
            for (int i = 0; i < n; i++)
            {
                MixU(skin.Bones[i]);
                MixF(skin.Weights[i].X); MixF(skin.Weights[i].Y); MixF(skin.Weights[i].Z); MixF(skin.Weights[i].W);
            }
        }
        second = g;
        return h;
    }

    private static ushort[] Indices16(MeshDef mesh)
    {
        var i0 = new ushort[mesh.Indices.Length];
        for (int i = 0; i < i0.Length; i++) i0[i] = (ushort)mesh.Indices[i];
        return i0;
    }

    /// <summary>Interleave one mesh into its section and file the range. Under <see cref="_gate"/>; the index
    /// runs were cut by the caller, off the lock.</summary>
    private static int Build(MeshDef mesh, SkinDef? skin, ulong content, ulong content2,
                            ushort[] i0, ushort[]? i1, int n1, ushort[]? i2, int n2)
    {
        int nv = mesh.Positions.Length;
        Fmt fmt = FormatFor(skin != null, HasUvSets(mesh));
        int stride = StrideOf[(int)fmt];
        Section s = _sect[(int)fmt];
        if (s.Used + nv > MaxVerts) { s.Full = true; return Refuse(Refusal.Section); }

        int id = NewRange();
        if (id < 0) return Refuse(Refusal.Table);

        GrowMirror(s, (s.Used + nv) * stride, stride);
        int tanWord = TanWord(fmt), paintWord = PaintWord(fmt), setWord = CarriesUvSets(fmt) ? UvSetWord(fmt) : -1;
        Vector2[] uv = mesh.Uv[0];
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        bool haveNormals = mesh.Normals.Length >= nv;
        if (haveNormals) _weldedFromParse++; else _noNormals++;
        for (int i = 0; i < nv; i++)
        {
            int o = (s.Used + i) * stride;
            Vector3 p = mesh.Positions[i];
            s.Mirror[o] = p.X; s.Mirror[o + 1] = p.Y; s.Mirror[o + 2] = p.Z;
            bmin = Vector3.Min(bmin, p);
            bmax = Vector3.Max(bmax, p);
            // finding 11: the normals are the PARSE's, welded and smoothing-group aware once per distinct
            // mesh -- there is no per-part weld on this path at all.
            if (i < mesh.Normals.Length)
            {
                Vector3 n = mesh.Normals[i];
                s.Mirror[o + 3] = n.X; s.Mirror[o + 4] = n.Y; s.Mirror[o + 5] = n.Z;
            }
            else { s.Mirror[o + 3] = s.Mirror[o + 4] = s.Mirror[o + 5] = 0f; }
            if (i < uv.Length) { s.Mirror[o + 6] = uv[i].X; s.Mirror[o + 7] = uv[i].Y; }
            else { s.Mirror[o + 6] = s.Mirror[o + 7] = 0f; }
            s.Mirror[o + 8] = s.Mirror[o + 9] = 0f;
            // The two packed words. A mesh with no tangent pool uploads the zero word, which is what tells
            // the lit stage to build its frame off derivatives instead.
            s.Mirror[o + tanWord] = BitConverter.UInt32BitsToSingle(
                i < mesh.Tangents.Length ? PackTangent(mesh.Tangents[i]) : 0u);
            s.Mirror[o + paintWord] = BitConverter.UInt32BitsToSingle(
                i < mesh.Colors.Length ? mesh.Colors[i] : PaintWhite);
            for (int k = 1; setWord >= 0 && k <= 3; k++)
            {
                Vector2 t = i < mesh.Uv[k].Length ? mesh.Uv[k][i] : default;
                s.Mirror[o + setWord + 2 * k - 2] = t.X; s.Mirror[o + setWord + 2 * k - 1] = t.Y;
            }
            if (skin == null) continue;
            // four bone SLOTS packed one per byte, pulled as a vec4 of 0..255 at location 8 (UNSIGNED_BYTE,
            // not normalized), and four weights at location 9
            s.Mirror[o + 10] = BitConverter.UInt32BitsToSingle(i < skin.Bones.Length ? skin.Bones[i] : 0u);
            Vector4 w = i < skin.Weights.Length ? skin.Weights[i] : Vector4.Zero;
            s.Mirror[o + 11] = w.X; s.Mirror[o + 12] = w.Y; s.Mirror[o + 13] = w.Z; s.Mirror[o + 14] = w.W;
        }

        ref Range rr = ref _ranges[id];
        rr.Content = content; rr.Content2 = content2;
        rr.NVerts = nv; rr.NIndices = mesh.Indices.Length;
        rr.Refs = 1; rr.Format = fmt; rr.Base = s.Used; rr.NV = nv;
        rr.BMin = bmin; rr.BMax = bmax;
        rr.Center = (bmin + bmax) * 0.5f;
        rr.Radius = Radius(mesh.Positions, rr.Center);
        rr.Live = true; rr.Charted = false; rr.Offered = false; rr.Bake = false;
        rr.Keys = new List<MeshDef>(1) { mesh };
        s.Used += nv;

        if (!AppendRun(i0, i0.Length, out rr.Off0, out rr.Cnt0))
        {
            s.Used -= nv;
            rr.Live = false;
            rr.Keys = null;
            FreeRangeSlot(id);
            return Refuse(Refusal.Elements);
        }
        rr.Off1 = rr.Off2 = 0; rr.Cnt1 = rr.Cnt2 = 0;
        if (n1 > 0 && i1 != null) AppendRun(i1, n1, out rr.Off1, out rr.Cnt1);
        if (n2 > 0 && i2 != null) AppendRun(i2, n2, out rr.Off2, out rr.Cnt2);

        _byContent[content] = id;
        _acquired++;
        _unsharedBytes += (long)nv * stride * 4;
        if (_logLeft-- > 0)
            _log?.Info($"MeshArena: mesh {i0.Length / 3} tris {nv} verts -> range {id} {(IsSkinned(fmt) ? "(skinned) " : "")}{(CarriesUvSets(fmt) ? "(uv sets 1-3) " : "")}"
                       + $"(lod {rr.Cnt1 / 3}/{rr.Cnt2 / 3})"
                       + (mesh.Tangents.Length != 0 ? " +tangents" : " NO tangents")
                       + (mesh.Colors.Length != 0 ? " +paint" : "") + $", arena {Size()}");
        PublishOne(id);
        return id;
    }

    private static float Radius(Vector3[] pos, Vector3 c)
    {
        float r2 = 0f;
        for (int i = 0; i < pos.Length; i++)
        {
            float d = (pos[i] - c).LengthSquared();
            if (d > r2) r2 = d;
        }
        return MathF.Sqrt(r2);
    }

    /// <summary><see cref="MeshLod"/>'s two levels, cut ONCE per distinct mesh instead of once per part: the
    /// same targets, the same error budgets, the same "kept only if it saves enough" rule, and the same master
    /// knob (finding 23). Runs OFF <see cref="_gate"/>, on the acquiring thread.</summary>
    private static void CutLods(ushort[] i0, Vector3[] pos, out ushort[]? o1, out int c1, out ushort[]? o2, out int c2)
    {
        o1 = null; o2 = null; c1 = 0; c2 = 0;
        int n0 = i0.Length, tris = n0 / 3;
        if (tris < MeshLod.MinTris || !MeshLod.On) return;
        var flat = new float[pos.Length * 3];
        for (int i = 0; i < pos.Length; i++) { flat[i * 3] = pos[i].X; flat[i * 3 + 1] = pos[i].Y; flat[i * 3 + 2] = pos[i].Z; }
        var a = new ushort[n0];
        var b = new ushort[n0];
        int ca = MeshSimplify.Run(i0, n0, flat, pos.Length, (int)(tris * MeshLod.Target1), MeshLod.Error1, a);
        if (ca > tris * 3 * (1f - MeshLod.MinSaving)) ca = 0;
        int cb = MeshSimplify.Run(i0, n0, flat, pos.Length, (int)(tris * MeshLod.Target2), MeshLod.Error2, b);
        if (cb > tris * 3 * (1f - MeshLod.MinSaving) || (cb >= ca && ca > 0)) cb = 0;
        if (ca > 0) { o1 = a; c1 = ca; }
        if (cb > 0) { o2 = b; c2 = cb; }
    }

    // ---------------------------------------------------------------- the charted path (MeshChart's)

    /// <summary>A range from vertices the caller has already interleaved in <paramref name="fmt"/>'s layout --
    /// <see cref="MeshChart"/>'s charted meshes, whose chart UVs split vertices and so cannot be derived from
    /// a <see cref="MeshDef"/>. Content-keyed and refcounted like <see cref="Acquire(MeshDef, SkinDef)"/>, but
    /// on a SEPARATE salted key space: the two hashes are computed differently and must never collide.</summary>
    internal static int AcquireRaw(ulong content, Fmt fmt, int nv, ReadOnlySpan<float> interleaved,
                                  ushort[] i0, int n0, ushort[]? i1, int n1, ushort[]? i2, int n2,
                                  Vector3 bmin, Vector3 bmax)
    {
        ulong key = content ^ 0x6368_6172_7465_6400UL;   // not-an-offset: "charted" -- the charted key namespace
        lock (_gate)
        {
            if (_byContent.TryGetValue(key, out int have) && Alive(have)
                && _ranges[have].Content2 == content && _ranges[have].NVerts == nv && _ranges[have].NIndices == n0)
            {
                _ranges[have].Refs++;
                _unsharedBytes += (long)_ranges[have].NV * StrideOf[(int)_ranges[have].Format] * 4;
                return have;
            }
            int stride = StrideOf[(int)fmt];
            if (nv <= 0 || n0 < 3 || interleaved.Length < nv * stride) return Refuse(Refusal.Empty);
            if (nv > MaxMeshVerts) return Refuse(Refusal.TooManyVerts);
            Section s = _sect[(int)fmt];
            if (s.Used + nv > MaxVerts) { s.Full = true; return Refuse(Refusal.Section); }
            int id = NewRange();
            if (id < 0) return Refuse(Refusal.Table);

            GrowMirror(s, (s.Used + nv) * stride, stride);
            interleaved[..(nv * stride)].CopyTo(s.Mirror.AsSpan(s.Used * stride));

            ref Range rr = ref _ranges[id];
            rr.Content = key; rr.Content2 = content;
            rr.NVerts = nv; rr.NIndices = n0;
            rr.Refs = 1; rr.Format = fmt; rr.Base = s.Used; rr.NV = nv;
            rr.BMin = bmin; rr.BMax = bmax; rr.Center = (bmin + bmax) * 0.5f;
            rr.Radius = Radius2(bmin, bmax);
            rr.Live = true; rr.Charted = true; rr.Offered = true; rr.Bake = false; rr.Keys = null;
            s.Used += nv;

            if (!AppendRun(i0, n0, out rr.Off0, out rr.Cnt0))
            {
                s.Used -= nv;
                rr.Live = false;
                FreeRangeSlot(id);
                return Refuse(Refusal.Elements);
            }
            rr.Off1 = rr.Off2 = 0; rr.Cnt1 = rr.Cnt2 = 0;
            if (n1 > 0 && i1 != null) AppendRun(i1, n1, out rr.Off1, out rr.Cnt1);
            if (n2 > 0 && i2 != null) AppendRun(i2, n2, out rr.Off2, out rr.Cnt2);

            _byContent[key] = id;
            _acquired++;
            _unsharedBytes += (long)nv * stride * 4;
            PublishOne(id);
            return id;
        }
    }

    /// <summary>A box's bounding-sphere radius about its centre: the same answer
    /// <see cref="Radius(Vector3[], Vector3)"/> gives for a mesh that fills its box, and never a smaller
    /// one (finding 30 -- the two now agree in the only sense they can, an upper bound).</summary>
    private static float Radius2(Vector3 bmin, Vector3 bmax) => (bmax - bmin).Length() * 0.5f;

    // ---------------------------------------------------------------- read (lock-free, off the snapshot)

    /// <summary>Which mesh a range holds: content hash and hand-out generation, so a recycled range index is a
    /// different signature. What <see cref="MeshChart"/> keys its charted copy on.</summary>
    internal static ulong Sig(ulong content, uint gen) => content ^ ((ulong)gen << 48);

    /// <summary>The published tenant signature of <paramref name="range"/>, 0 for none. Lock-free.</summary>
    internal static ulong SigOf(int range)
    {
        Pub?[] pub = _pub;
        Pub? r = (uint)range < (uint)pub.Length ? pub[range] : null;
        return r != null && r.Live ? r.Sig : 0;
    }

    /// <summary>What one draw needs: the VAO for the range's format, its base vertex, and the index BYTE
    /// offset and count at <paramref name="lod"/>, falling back to the finest level that exists. False means
    /// the range is gone, empty, or the arena has not been uploaded in this context yet.</summary>
    internal static bool Draw(int range, int lod, out uint vao, out int baseVertex, out nint off, out int count, out int level)
    {
        vao = 0; baseVertex = 0; off = 0; count = 0; level = 0;
        Pub?[] pub = _pub;
        Pub? r = (uint)range < (uint)pub.Length ? pub[range] : null;
        if (r == null || r.Vao == 0) return false;
        vao = r.Vao;
        baseVertex = r.Base;
        if (lod >= 2 && r.Cnt2 > 0) { off = r.Off2; count = r.Cnt2; level = 2; return true; }
        if (lod >= 1 && r.Cnt1 > 0) { off = r.Off1; count = r.Cnt1; level = 1; return true; }
        off = r.Off0; count = r.Cnt0;
        return r.Cnt0 > 0;
    }

    /// <summary>The index runs alone, for a caller that has already picked its VAO. Same LOD fallback as
    /// <see cref="Draw"/>, and the same lock-free snapshot read.</summary>
    internal static bool Runs(int range, int lod, out int baseVertex, out nint off, out int count, out int level)
    {
        baseVertex = 0; off = 0; count = 0; level = 0;
        Pub?[] pub = _pub;
        Pub? r = (uint)range < (uint)pub.Length ? pub[range] : null;
        if (r == null) return false;
        baseVertex = r.Base;
        if (lod >= 2 && r.Cnt2 > 0) { off = r.Off2; count = r.Cnt2; level = 2; return true; }
        if (lod >= 1 && r.Cnt1 > 0) { off = r.Off1; count = r.Cnt1; level = 1; return true; }
        off = r.Off0; count = r.Cnt0;
        return r.Cnt0 > 0;
    }

    /// <summary>The range's two GENERATED runs, raw and with no fallback: byte offset and count at level 1
    /// and at level 2, both 0 for a level the simplifier declined. What a caller picking a level per VIEW
    /// needs -- it has to know which levels exist. <see cref="Runs"/>'s own snapshot read.</summary>
    internal static bool Levels(int range, out nint off1, out int cnt1, out nint off2, out int cnt2)
    {
        off1 = 0; cnt1 = 0; off2 = 0; cnt2 = 0;
        Pub?[] pub = _pub;
        Pub? r = (uint)range < (uint)pub.Length ? pub[range] : null;
        if (r == null) return false;
        off1 = r.Off1; cnt1 = r.Cnt1; off2 = r.Off2; cnt2 = r.Cnt2;
        return true;
    }

    /// <summary>The mesh's own-space box and bounding sphere. False = no such range.</summary>
    internal static bool Bounds(int range, out Vector3 bmin, out Vector3 bmax, out Vector3 center, out float radius)
    {
        bmin = bmax = center = default; radius = 0f;
        Pub?[] pub = _pub;
        Pub? r = (uint)range < (uint)pub.Length ? pub[range] : null;
        if (r == null) return false;
        bmin = r.BMin; bmax = r.BMax; center = r.Center; radius = r.Radius;
        return true;
    }

    /// <summary>Holders of <paramref name="range"/>: the dedup proof. Cold -- the self-checks and the report
    /// only -- so it reads the live table under the lock rather than the snapshot.</summary>
    internal static int RefsOf(int range) { lock (_gate) return Alive(range) ? _ranges[range].Refs : 0; }

    /// <summary>Which vertex format <paramref name="range"/> lives in, for the draw that has to pick a VAO.</summary>
    internal static Fmt FormatOf(int range) { lock (_gate) return Alive(range) ? _ranges[range].Format : Fmt.Static; }

    // ---------------------------------------------------------------- the frame edge

    /// <summary>Collect the frame's frees, compact if the dead bytes have piled up, and push both mirrors to
    /// the GPU. Render thread, inside Alloy's GL bracket, once a frame before the batches are built.</summary>
    internal static void Pump()
    {
        // The CPU half runs wherever the caller is: frees and the readers' snapshot touch no GL.
        lock (_gate) { if (DrainFrees()) Publish(); }
        if (Gl.CreateBuffers == null) return;
        // The GL half may ONLY run with Alloy's 4.6 context current. Buffers are share-listed with the
        // engine's context, but a VERTEX ARRAY OBJECT is a container and is never shared -- so a VAO built
        // on the game thread at the present boundary (where the ENGINE's context is current) is a name the
        // 4.6 context does not know, and every draw that binds it rasterises nothing. That is exactly what
        // DB mode hit: the feed pumps at the boundary, the VAO was built there, `s.Vao != 0` from then on,
        // and the in-bracket pump never rebuilt it. Deferred and SAID, never done in the wrong context.
        if (!GlContextService.Inside)
        {
            _glDeferred++;
            if (!_saidDeferred)
            {
                _saidDeferred = true;
                _log?.Info("MeshArena: a pump arrived outside Alloy's 4.6 bracket -- its GL half is deferred to "
                         + "the next in-bracket pump (a VAO is not shared between contexts)");
            }
            return;
        }
        // An in-bracket pump clears the deferral tally: the feed pumps at the present boundary every frame
        // BY DESIGN, so a running count of those would read as a fault. What matters is a deferral with no
        // in-bracket pump after it, which is the state that leaves the arena unuploaded.
        _glDeferred = 0;
        lock (_gate)
        {
            bool changed = false;
            bool compact = _iDead > MaxIndices / 4 || (_elementsFull && _iDead > 0);
            for (int f = 0; f < Formats && !compact; f++)
                compact = _sect[f].Dead > MaxVerts / 4 || (_sect[f].Full && _sect[f].Dead > 0);
            if (compact) { Compact(); changed = true; }

            for (int f = 0; f < Formats; f++)
            {
                Section s = _sect[f];
                if (s.Used > 0 && s.Uploaded != s.Used) UploadVerts(s, StrideOf[f]);
            }
            if (_iUsed > 0 && _iUploaded != _iUsed) UploadIndices();
            // CX-120 seam-anim: the dangly and animmesh ranges whose vertices moved on the game thread
            // (MeshArena.Dyn.cs). Inside the same bracket and under the same lock as every other upload.
            PumpVk();
            DynUpload();
            for (int f = 0; f < Formats; f++)
            {
                Section s = _sect[f];
                if (s.Vao == 0 && s.Vbo != 0 && _ebo != 0) { BuildVao(s, (Fmt)f); changed = true; }
            }
            if (changed) Publish();
            if (changed) Dump();
        }
    }

    private static bool DrainFrees()
    {
        // A LIVE range nothing references is a leak whichever way it got there: the queue is the normal path,
        // and this is the backstop. The live area showed 177 resident meshes against 5 references for exactly
        // this reason, so it is swept here and COUNTED -- a resource at zero holders is never allowed to stay
        // resident quietly.
        for (int i = 1; i < _rangeHigh; i++)
            if (_ranges[i].Live && _ranges[i].Refs <= 0 && !_pendingFree.Contains(i))
            {
                _pendingFree.Add(i);
                _unqueued++;
            }
        if (_pendingFree.Count == 0) return false;
        for (int k = 0; k < _pendingFree.Count; k++)
        {
            int id = _pendingFree[k];
            if (!Alive(id) || _ranges[id].Refs > 0) continue;
            ref Range r = ref _ranges[id];
            _sect[(int)r.Format].Dead += r.NV;
            _iDead += r.Cnt0 + r.Cnt1 + r.Cnt2;
            // only when the key still names THIS range: a second mesh of the same content acquired after this
            // one was queued owns the key now, and unmapping it would orphan a live range
            if (_byContent.TryGetValue(r.Content, out int at) && at == id) _byContent.Remove(r.Content);
            // and every MeshDef shortcut to it goes, or the map grows for the session and pins the CPU arrays
            if (r.Keys != null)
            {
                foreach (MeshDef m in r.Keys)
                    if (_byMesh.TryGetValue(m, out (int Range, int Gen) e) && e.Range == id) _byMesh.Remove(m);
                r.Keys.Clear();
                r.Keys = null;
            }
            r.Live = false;
            FreeRangeSlot(id);
            PublishOne(id);
            _freed++;
        }
        _pendingFree.Clear();
        // a key left pointing at a freed range is not a mesh: sweep it, or the map outlives its ranges and
        // the period line counts meshes the arena no longer holds
        List<ulong>? dead = null;
        foreach (var kv in _byContent) if (!Alive(kv.Value)) (dead ??= new List<ulong>()).Add(kv.Key);
        if (dead != null)
        {
            foreach (ulong k in dead) _byContent.Remove(k);
            _staleKeys += dead.Count;
        }
        return true;
    }

    /// <summary>Rewrite every section and the element arena with only the live ranges, in place under the same
    /// buffer names (so the VAOs stay valid), and re-upload. Cold: an area change's worth of copying. TWO
    /// passes with their own monotone cursors -- vertices PER FORMAT by base vertex, the shared element arena
    /// by first index offset -- because one pass by base vertex is not monotone where two formats interleave.</summary>
    private static void Compact()
    {
        var live = new List<int>();
        for (int i = 1; i < _rangeHigh; i++) if (_ranges[i].Live) live.Add(i);
        long before = VramBytes;
        // What a compaction MOVES is what a consumer that cached a base vertex or an index offset has to be
        // told about. Without this a record staged once keeps the old numbers and draws out of whatever
        // slid into that place -- the giant-nonsense-mesh class of bug (VDO address recycling, chart
        // garbage meshes). Recorded before the move so the comparison is against the old placement.
        _movedN = 0;
        if (_moved.Length < live.Count) _moved = new int[Math.Max(live.Count, 64)];
        if (_movedFrom.Length < live.Count) _movedFrom = new (int, nint)[Math.Max(live.Count, 64)];
        foreach (int id in live) { _movedFrom[_movedN] = (_ranges[id].Base, _ranges[id].Off0); _moved[_movedN++] = id; }

        for (int f = 0; f < Formats; f++)
        {
            var mine = new List<int>();
            foreach (int id in live) if ((int)_ranges[id].Format == f) mine.Add(id);
            mine.Sort((a, b) => _ranges[a].Base.CompareTo(_ranges[b].Base));
            Section s = _sect[f];
            int stride = StrideOf[f], cursor = 0;
            foreach (int id in mine)
            {
                ref Range r = ref _ranges[id];
                if (cursor != r.Base)
                    Array.Copy(s.Mirror, (long)r.Base * stride, s.Mirror, (long)cursor * stride, (long)r.NV * stride);
                r.Base = cursor;
                cursor += r.NV;
            }
            s.Used = cursor; s.Uploaded = 0; s.Dead = 0; s.Full = false;
        }

        live.Sort((a, b) => _ranges[a].Off0.CompareTo(_ranges[b].Off0));
        int iCursor = 0;
        foreach (int id in live)
        {
            ref Range r = ref _ranges[id];
            r.Off0 = MoveRun(r.Off0, r.Cnt0, ref iCursor);
            r.Off1 = MoveRun(r.Off1, r.Cnt1, ref iCursor);
            r.Off2 = MoveRun(r.Off2, r.Cnt2, ref iCursor);
        }
        _iUsed = iCursor; _iUploaded = 0; _iDead = 0; _elementsFull = false;
        VkCompacted();
        _compactions++;
        // keep only the ranges whose placement actually changed
        int w = 0;
        for (int k = 0; k < _movedN; k++)
        {
            int id = _moved[k];
            if (_ranges[id].Base == _movedFrom[k].Base && _ranges[id].Off0 == _movedFrom[k].Off) continue;
            _moved[w++] = id;
        }
        _movedN = w;
        _movedTotal += w;
        _log?.Info($"MeshArena: compacted -- {(before - VramBytes) >> 10} KiB reclaimed, {live.Count} meshes kept, "
                 + $"{w} range(s) moved");
        // Publish BEFORE telling anyone: the handler refreshes its cached offsets by asking this arena, and
        // it must get the new placement, not the one it already holds.
        if (w > 0)
        {
            Publish();
            RangesMoved?.Invoke(_moved, w);
            _movedN = 0;
        }
    }

    /// <summary>Slide one index run down to the compaction cursor. A range's three runs were appended back to
    /// back, and the visit is ordered by the first of them, so the cursor is always at or below the source.</summary>
    private static nint MoveRun(nint off, int count, ref int cursor)
    {
        if (count <= 0) return 0;
        int from = (int)((long)off / 2);
        if (from != cursor) Array.Copy(_iMirror, from, _iMirror, cursor, count);
        nint at = (nint)((long)cursor * 2);
        cursor += count;
        return at;
    }

    /// <summary>The context is gone: forget every name, never delete. Both mirrors go back up on the next
    /// <see cref="Pump"/>, which is terminal-safe -- nothing here is rebuilt from engine memory.</summary>
    internal static void ContextLost()
    {
        lock (_gate)
        {
            for (int f = 0; f < Formats; f++)
            {
                Section s = _sect[f];
                s.Vao = 0; s.Vbo = 0; s.Uploaded = 0; s.Capacity = 0;
            }
            _ebo = 0; _iUploaded = 0; _iCapacity = 0;
            Publish();
        }
    }

    private static int NewRange()
    {
        if (_rangeFree >= 0)
        {
            int i = _rangeFree;
            _rangeFree = _ranges[i].NextFree;
            int gen = _ranges[i].Gen;
            _ranges[i] = default;
            _ranges[i].Gen = gen + 1;   // a cached (range, gen) from the previous tenant is now stale
            return i;
        }
        if (_rangeHigh >= _ranges.Length)
        {
            if (_ranges.Length >= 1 << 20) return -1;
            Array.Resize(ref _ranges, _ranges.Length * 2);
        }
        return _rangeHigh++;
    }

    private static void FreeRangeSlot(int id)
    {
        _ranges[id].NextFree = _rangeFree;
        _rangeFree = id;
    }

    private static void GrowMirror(Section s, int floats, int stride)
    {
        if (s.Mirror.Length >= floats) return;
        int cap = s.Mirror.Length == 0 ? (1 << 14) * stride : s.Mirror.Length;
        while (cap < floats) cap *= 2;
        if (cap > MaxVerts * stride) cap = MaxVerts * stride;
        Array.Resize(ref s.Mirror, cap);
    }

    private static bool AppendRun(ushort[] run, int n, out nint off, out int count)
    {
        off = 0; count = 0;
        if (n <= 0) return false;
        if (_iUsed + n > MaxIndices) { _elementsFull = true; return false; }
        if (_iMirror.Length < _iUsed + n)
        {
            int cap = _iMirror.Length == 0 ? 1 << 16 : _iMirror.Length;
            while (cap < _iUsed + n) cap *= 2;
            if (cap > MaxIndices) cap = MaxIndices;
            Array.Resize(ref _iMirror, cap);
        }
        Array.Copy(run, 0, _iMirror, _iUsed, n);
        off = (nint)((long)_iUsed * 2);
        count = n;
        _iUsed += n;
        return true;
    }

    /// <summary>By NAME, touching no binding; a grow respecifies under the same name, so the VAO over it stays
    /// valid.</summary>
    private static void UploadVerts(Section s, int stride)
    {
        if (s.Vbo == 0) { uint b; Gl.CreateBuffers(1, &b); if (b == 0) return; s.Vbo = b; s.Uploaded = 0; s.Capacity = 0; }
        if (s.Uploaded < s.Used && s.Capacity >= s.Used)
        {
            fixed (float* p = s.Mirror.AsSpan(checked((int)((long)s.Uploaded * stride))))
                Gl.NamedBufferSubData(s.Vbo, (IntPtr)((long)s.Uploaded * stride * 4),
                                      (IntPtr)((long)(s.Used - s.Uploaded) * stride * 4), p);
        }
        else
        {
            s.Capacity = s.Mirror.Length / stride;
            fixed (float* p = s.Mirror)
                Gl.NamedBufferData(s.Vbo, (IntPtr)((long)s.Mirror.Length * 4), p, Gl.STATIC_DRAW);
        }
        s.Uploaded = s.Used;
    }

    private static void UploadIndices()
    {
        if (_ebo == 0) { uint b; Gl.CreateBuffers(1, &b); if (b == 0) return; _ebo = b; _iUploaded = 0; _iCapacity = 0; }
        if (_iUploaded < _iUsed && _iCapacity >= _iUsed)
        {
            fixed (ushort* p = _iMirror.AsSpan(_iUploaded))
                Gl.NamedBufferSubData(_ebo, (IntPtr)((long)_iUploaded * 2), (IntPtr)((long)(_iUsed - _iUploaded) * 2), p);
        }
        else
        {
            _iCapacity = _iMirror.Length;
            fixed (ushort* p = _iMirror)
                Gl.NamedBufferData(_ebo, (IntPtr)((long)_iCapacity * 2), p, Gl.STATIC_DRAW);
        }
        _iUploaded = _iUsed;
    }

    /// <summary>ONE VAO per format: the arena carries every stream a stage reads, so there is nothing per mesh
    /// to vary. Built by name, so the draw loops' bound-VAO latch is never disturbed.</summary>
    private static void BuildVao(Section s, Fmt f)
    {
        uint vao; Gl.CreateVertexArrays(1, &vao);
        if (vao == 0) return;
        int bytes = StrideBytes(f);
        VertexArrays.Bind(vao, AlloyRenderer.LocPos, new VertexStream { Buf = s.Vbo, Size = 3, Type = Gl.FLOAT, Stride = bytes, Off = 0 });
        VertexArrays.Bind(vao, AlloyRenderer.LocNrm, new VertexStream { Buf = s.Vbo, Size = 3, Type = Gl.FLOAT, Stride = bytes, Off = 12 });
        VertexArrays.Bind(vao, AlloyRenderer.LocUv, new VertexStream { Buf = s.Vbo, Size = 2, Type = Gl.FLOAT, Stride = bytes, Off = 24 });
        VertexArrays.Bind(vao, MeshChart.LocChartUv, new VertexStream { Buf = s.Vbo, Size = 2, Type = Gl.FLOAT, Stride = bytes, Off = 32 });
        // The two packed words, both NORMALIZED: the tangent's lanes arrive as a vec4 in -1..1 with the
        // handedness in w, the paint's four bytes as a vec4 in 0..1 in r,g,b,a order.
        VertexArrays.Bind(vao, AlloyRenderer.LocTangent, new VertexStream
        { Buf = s.Vbo, Size = 4, Type = Gl.INT_2_10_10_10_REV, Stride = bytes, Off = TanWord(f) * 4, Norm = 1 });
        VertexArrays.Bind(vao, AlloyRenderer.LocPaint, new VertexStream
        { Buf = s.Vbo, Size = 4, Type = Gl.UNSIGNED_BYTE, Stride = bytes, Off = PaintWord(f) * 4, Norm = 1 });
        for (uint k = 0; CarriesUvSets(f) && k < 3; k++)
            VertexArrays.Bind(vao, AlloyRenderer.LocUvSet1 + k, new VertexStream
            { Buf = s.Vbo, Size = 2, Type = Gl.FLOAT, Stride = bytes, Off = (UvSetWord(f) + 2 * (int)k) * 4 });
        if (IsSkinned(f))
        {
            // location 8 is a vec4 in the shader: UNSIGNED_BYTE with normalize OFF converts 0..255 to 0.0..255.0
            VertexArrays.Bind(vao, AlloyRenderer.LocBoneIdx, new VertexStream { Buf = s.Vbo, Size = 4, Type = Gl.UNSIGNED_BYTE, Stride = bytes, Off = 40 });
            VertexArrays.Bind(vao, AlloyRenderer.LocBoneW, new VertexStream { Buf = s.Vbo, Size = 4, Type = Gl.FLOAT, Stride = bytes, Off = 44 });
        }
        Gl.VertexArrayElementBuffer(vao, _ebo);
        s.Vao = vao;
    }

    // ---------------------------------------------------------------- report

    /// <summary>One clause of the <c>[alloy]</c> period line: the distinct meshes held, the parts they serve,
    /// the arena's own bytes, the bytes the per-part path would have held for the same geometry and the
    /// DIFFERENCE -- which is the saving the ticket asks for -- and any refusal.</summary>
    internal static string Stats(int frames)
    {
        if (_acquired == 0 && _refused == 0) return "";
        string why = "";
        for (int k = 1; k < RefusalName.Length; k++) if (_refusedBy[k] > 0) why += $" {RefusalName[k]} {_refusedBy[k]}";
        long holders = Holders;
        long vram = VramBytes, unshared = _unsharedBytes;
        int meshes = MeshCount;
        float share = meshes > 0 ? (float)holders / meshes : 0f;
        return $"arena {meshes} meshes / {holders} holders ({share:0.0}x shared), {Bytes(vram)}"
               + (unshared > vram ? $" against {Bytes(unshared)} one-copy-per-holder -- {Bytes(unshared - vram)} saved" : "")
               + $", normals {_weldedFromParse} from the parse" + (_noNormals > 0 ? $" / {_noNormals} WITHOUT" : "")
               + (_freed > 0 ? $", {_freed} freed" : "")
               + (_compactions > 0 ? $", {_compactions} compactions" : "")
               + (_refused > 0 ? $", REFUSED{why}" : "")
               + (_overReleased > 0 ? $", {_overReleased} OVER-RELEASED" : "")
               + (_heldPastEviction > 0 ? $", {_heldPastEviction} HELD PAST EVICTION" : "")
               + (_collisions > 0 ? $", {_collisions} content collisions" : "")
               + (_staleKeys > 0 ? $", {_staleKeys} stale keys swept" : "")
               + (_unqueued > 0 ? $", {_unqueued} UNQUEUED zero-ref ranges reclaimed" : "")
               + (_movedTotal > 0 ? $", {_movedTotal} range(s) moved by a compaction (records refreshed)" : "")
               + (_glDeferred > 0 ? $", {_glDeferred} pump(s) STILL DEFERRED (no in-bracket pump since)" : "")
               + DynStats();
    }

    /// <summary>One-shot proof that the VAO the draw binds is the one this arena built, in THIS context:
    /// every attribute's enable/size/type/stride/offset/buffer, the element binding, and both buffers' sizes.
    /// A context mismatch shows up here as zeros on every row -- the name resolves to nothing.</summary>
    private static void Dump()
    {
        if (_dumped || _log == null || Gl.GetVertexArrayiv == null || Gl.GetVertexAttribiv == null) return;
        _dumped = true;
        int prevVao = 0;
        Gl.GetIntegerv(Gl.VERTEX_ARRAY_BINDING, &prevVao);
        for (int f = 0; f < Formats; f++)
        {
            Section s = _sect[f];
            if (s.Vao == 0) continue;
            int elem = 0;
            Gl.GetVertexArrayiv(s.Vao, Gl.ELEMENT_ARRAY_BUFFER_BINDING, &elem);
            Gl.BindVertexArray(s.Vao);
            string rows = "";
            foreach (uint loc in new uint[] { 0, 1, 2, 3, 4, 5, 8, 9 })
            {
                int en = 0, sz = 0, ty = 0, st = 0, bf = 0;
                Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_ENABLED, &en);
                if (en == 0) continue;
                Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_SIZE, &sz);
                Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_TYPE, &ty);
                Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_STRIDE, &st);
                Gl.GetVertexAttribiv(loc, Gl.VERTEX_ATTRIB_ARRAY_BUFFER_BINDING, &bf);
                void* ptr; Gl.GetVertexAttribPointerv(loc, Gl.VERTEX_ATTRIB_ARRAY_POINTER, &ptr);
                rows += $" [{loc}]size={sz} type=0x{ty:x} stride={st} off={(nint)ptr} buf={bf}";
            }
            _log.Always($"MeshArena: [vaodump] fmt={(Fmt)f} vao={s.Vao} vbo={s.Vbo} ebo={_ebo} elemBinding={elem} "
                      + $"verts={s.Used} uploaded={s.Uploaded} indices={_iUsed}{rows}");
        }
        Gl.BindVertexArray((uint)prevVao);
    }

    /// <summary>The arena's GPU bytes, in the biggest unit that still reads as a number.</summary>
    private static string Size() => Bytes(VramBytes);

    private static string Bytes(long b)
        => b >= 1 << 20 ? $"{b >> 20} MiB" : b >= 1 << 10 ? $"{b >> 10} KiB" : $"{b} B";

    internal static string Describe()
        => _acquired == 0 ? "no meshes held"
         : $"{MeshCount} distinct meshes serving {Holders} holders, {Size()}"
           + (_elementsFull ? "; the element arena is full" : "");

    /// <summary>Reset the arena to an empty session. The off-device suite's precondition, ENFORCED: it refuses
    /// (loudly) while anything is held, because this does run on a live client -- the self-check walk is part
    /// of boot -- and wiping an arena with live holders would hand them freed ranges (finding 20).</summary>
    internal static bool ResetForCheck()
    {
        lock (_gate)
        {
            long held = 0;
            for (int i = 1; i < _rangeHigh; i++) if (_ranges[i].Live) held += _ranges[i].Refs;
            if (held > 0)
            {
                _log?.Info($"MeshArena: a check asked for a reset while {held} reference(s) are held -- REFUSED");
                return false;
            }
            _ranges = new Range[1 << 12];
            _pub = Array.Empty<Pub?>();
            _rangeHigh = 1; _rangeFree = -1;
            _byContent.Clear(); _byMesh.Clear(); _pendingFree.Clear();
            _sect = NewSections();
            _iMirror = Array.Empty<ushort>();
            _iUsed = _iUploaded = _iCapacity = _iDead = 0;
            _ebo = 0; _elementsFull = false;
            _acquired = _freed = _refused = _compactions = _overReleased = _heldPastEviction = 0;
            _collisions = _weldedFromParse = _noNormals = _staleKeys = _unqueued = 0;
            _movedN = 0; _movedTotal = 0;
            _unsharedBytes = 0;
            _logLeft = LogCap;
            Array.Clear(_refusedBy);
            Publish();
            return true;
        }
    }

    /// <summary>The frame edge, driven off-device: frees land and the dead counters move, with no GL.</summary>
    internal static void FrameEdgeForCheck()
    {
        lock (_gate) { if (DrainFrees()) Publish(); }
    }

    /// <summary>Compaction, driven off-device.</summary>
    internal static void CompactForCheck()
    {
        lock (_gate) { DrainFrees(); Compact(); Publish(); }
    }

    /// <summary>Pretend a format's VAO exists, for an off-device check of a caller whose gate is the VAO
    /// name. No GL is involved and nothing draws.</summary>
    internal static void FakeVaoForCheck(Fmt f, uint name)
    {
        lock (_gate) { _sect[(int)f].Vao = name; Publish(); }
    }

    /// <summary>Where <paramref name="range"/> sits, for the self-check's compaction assertions.</summary>
    internal static (int Base, nint Off0, int Cnt0) PlaceForCheck(int range)
    {
        lock (_gate) return Alive(range) ? (_ranges[range].Base, _ranges[range].Off0, _ranges[range].Cnt0) : (-1, 0, 0);
    }

    /// <summary>The interleaved vertex this range uploaded, for a bring-up dump: position, normal and the UV
    /// lane the lit stage samples. Reading the MIRROR, which is byte-for-byte what went to the GPU.</summary>
    internal static bool VertexProbe(int range, int v, out Vector3 pos, out Vector3 nrm, out Vector2 uv, out Vector2 chart)
    {
        pos = default; nrm = default; uv = default; chart = default;
        lock (_gate)
        {
            if (!Alive(range)) return false;
            ref Range r = ref _ranges[range];
            if ((uint)v >= (uint)r.NV) return false;
            Section s = _sect[(int)r.Format];
            int o = (r.Base + v) * StrideOf[(int)r.Format];
            if (o + StrideOf[(int)r.Format] > s.Mirror.Length) return false;
            pos = new Vector3(s.Mirror[o], s.Mirror[o + 1], s.Mirror[o + 2]);
            nrm = new Vector3(s.Mirror[o + 3], s.Mirror[o + 4], s.Mirror[o + 5]);
            uv = new Vector2(s.Mirror[o + 6], s.Mirror[o + 7]);
            chart = new Vector2(s.Mirror[o + 8], s.Mirror[o + 9]);
            return true;
        }
    }

    /// <summary>Does <paramref name="mesh"/> still have a shortcut into the arena? The leak check.</summary>
    internal static bool MappedForCheck(MeshDef mesh) { lock (_gate) return _byMesh.ContainsKey(mesh); }
}

internal static class MeshArenaDraw
{
    /// <summary>The VAO <paramref name="range"/> actually draws from. A SKINNED range lives in its own
    /// section with the bone streams at locations 8 and 9, so a caller that latched the static format's VAO
    /// for every record would pull a skinned mesh's vertices through it. 0 = not resident.</summary>
    internal static uint VaoOf(int range)
        => MeshArena.Draw(range, 0, out uint vao, out _, out _, out _, out _) ? vao : 0u;

    /// <summary>Range <paramref name="range"/>'s draw arguments at LOD <paramref name="lod"/>: the index
    /// count, the byte offset into the shared element buffer and the base vertex. False when the range is not
    /// resident or has no geometry -- a counted refusal the feed re-asks next frame, never a silent hole.</summary>
    internal static bool Draw(int range, int lod, out int indexCount, out nint indexOffset, out int baseVertex)
        => MeshArena.Runs(range, lod, out baseVertex, out indexOffset, out indexCount, out _);
}
