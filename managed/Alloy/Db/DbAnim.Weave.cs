using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

public static unsafe partial class DbAnim
{
    /// <summary>One creature's boundary stitch, start to finish: every skinned vertex of every collected part
    /// in the shared REST pose, the coincident ones grouped, each group made one, and the parts that changed
    /// published as per-instance geometry. Built, used and dropped inside one <see cref="Stitch"/>.</summary>
    private sealed class Weave
    {
        private const float Degree = MathF.PI / 180f;

        private readonly int _np;
        private readonly Matrix4x4[] _rest, _restInv;
        private readonly MeshDef[] _mesh;
        private readonly SkinDef[] _skin;
        private readonly int[][] _bone;
        private readonly List<string>[] _names;
        private readonly bool[] _changed;
        private readonly Vector3[]?[] _newNrm;
        private readonly Vector4[]?[] _newTan;
        private readonly ushort[]?[] _newIdx;
        private readonly Vector4[]?[] _newWeight;

        private int[] _vPart = new int[64], _vIdx = new int[64], _uf = new int[64];
        private Vector3[] _vPos = new Vector3[64], _vNrm = new Vector3[64];
        private Vector4[] _vTan = new Vector4[64];

        internal int Count;
        internal int Healed, Pairs, Gaps, Lines, Handed;
        internal float MaxAngle;

        internal Weave(int parts)
        {
            _np = parts;
            _rest = new Matrix4x4[parts];
            _restInv = new Matrix4x4[parts];
            _mesh = new MeshDef[parts];
            _skin = new SkinDef[parts];
            _bone = new int[parts][];
            _names = new List<string>[parts];
            _changed = new bool[parts];
            _newNrm = new Vector3[parts][];
            _newTan = new Vector4[parts][];
            _newIdx = new ushort[parts][];
            _newWeight = new Vector4[parts][];
        }

        /// <summary>Read part <paramref name="part"/> into the weave: its rest transform, its bone table in
        /// name form, and every vertex's rest position, normal and tangent. False when the part cannot be
        /// placed in the rest pose or its bone table cannot be read -- never a half-read part.</summary>
        internal bool Add(int k, int part)
        {
            MeshDef? mesh = ModelMesh(part);
            SkinDef? skin = ModelSkin(part);
            if (mesh == null || skin == null) return false;
            if (!RestWorldOfPart(part, out Matrix4x4 rest)
                || !Matrix4x4.Invert(rest, out Matrix4x4 inv)
                || !SlotBones(part, skin, out int[] bone, out string[] names)) return false;
            int nv = mesh.Positions.Length;
            if (skin.Weights.Length < nv || skin.BoneIndices.Length < nv * 4) return false;
            _rest[k] = rest;
            _restInv[k] = inv;
            _mesh[k] = mesh;
            _skin[k] = skin;
            _bone[k] = bone;
            _names[k] = new List<string>(names);
            Grow(Count + nv);
            // MtrFeed.Warm runs MdlPost.Tangents for a late .mtr at OBJECT CREATION, before a part tree
            // exists, so the arrays read here are the final ones -- and the weave re-reads them on every
            // re-stitch. ponytail: a frame that somehow arrived after a creature was stitched would not
            // reach its copy; the upgrade is to mark the composition due again from that fill.
            bool haveN = mesh.Normals.Length >= nv, haveT = mesh.Tangents.Length >= nv;
            for (int i = 0; i < nv; i++)
            {
                int v = Count++;
                _vPart[v] = k;
                _vIdx[v] = i;
                _uf[v] = v;
                _vPos[v] = Vector3.Transform(mesh.Positions[i], rest);
                _vNrm[v] = haveN ? Norm(Vector3.TransformNormal(mesh.Normals[i], rest)) : Vector3.Zero;
                _vTan[v] = haveT
                         ? new Vector4(Norm(Vector3.TransformNormal(AsV3(mesh.Tangents[i]), rest)),
                                       mesh.Tangents[i].W)
                         : Vector4.Zero;
            }
            return true;
        }

        private void Grow(int need)
        {
            if (need <= _vPart.Length) return;
            int n = _vPart.Length;
            while (n < need) n *= 2;
            Array.Resize(ref _vPart, n); Array.Resize(ref _vIdx, n); Array.Resize(ref _uf, n);
            Array.Resize(ref _vPos, n); Array.Resize(ref _vNrm, n); Array.Resize(ref _vTan, n);
        }

        /// <summary>Put every vertex in a cell of a <see cref="StitchEps"/> grid and union the pairs that are
        /// really within that distance. The eight cells around a point are probed, not one, so two coincident
        /// vertices that land either side of a cell boundary are still found.</summary>
        internal void Group()
        {
            var grid = new Dictionary<long, int>(Count);
            var next = new int[Count];
            for (int v = 0; v < Count; v++)
            {
                long key = Cell(_vPos[v], 0f, 0f, 0f);
                next[v] = grid.TryGetValue(key, out int had) ? had : -1;
                grid[key] = v;
            }
            float h = StitchEps * 0.5f;
            Span<long> seen = stackalloc long[8];
            for (int v = 0; v < Count; v++)
            {
                int ns = 0;
                for (int c = 0; c < 8; c++)
                {
                    long key = Cell(_vPos[v], (c & 1) == 0 ? -h : h, (c & 2) == 0 ? -h : h,
                                    (c & 4) == 0 ? -h : h);
                    bool dup = false;
                    for (int s = 0; s < ns; s++) if (seen[s] == key) { dup = true; break; }
                    if (dup) continue;
                    seen[ns++] = key;
                    for (int u = grid.TryGetValue(key, out int at) ? at : -1; u >= 0; u = next[u])
                        if (u != v && _vPart[u] != _vPart[v]
                            && (_vPos[u] - _vPos[v]).LengthSquared() <= StitchEps * StitchEps) Union(u, v);
                }
            }
        }

        /// <summary>The cell of a point offset by half a grid step, packed into one key. 21 bits per axis
        /// covers a rest pose a thousand units across at a tenth of a millimetre.</summary>
        private static long Cell(Vector3 p, float dx, float dy, float dz)
        {
            const long mask = (1L << 21) - 1;   // not-an-offset: 21 bits per axis of the cell key
            long x = (long)MathF.Floor((p.X + dx) / StitchEps) & mask;
            long y = (long)MathF.Floor((p.Y + dy) / StitchEps) & mask;
            long z = (long)MathF.Floor((p.Z + dz) / StitchEps) & mask;
            return (x << 42) | (y << 21) | z;
        }

        private int Find(int v)
        {
            while (_uf[v] != v) { _uf[v] = _uf[_uf[v]]; v = _uf[v]; }
            return v;
        }

        private void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) _uf[ra < rb ? rb : ra] = ra < rb ? ra : rb;
        }

        /// <summary>Make every group of coincident vertices one: the fullest influence set wins and is written
        /// into each member's own table, and the normals and tangents are averaged in the rest pose. A vertex
        /// with no partner in another part is never touched.</summary>
        internal void Heal()
        {
            var head = new int[Count];
            var link = new int[Count];
            for (int v = 0; v < Count; v++) head[v] = -1;
            for (int v = Count - 1; v >= 0; v--)
            {
                int r = Find(v);
                link[v] = head[r];
                head[r] = v;
            }
            var pairs = new HashSet<int>();
            for (int r = 0; r < Count; r++)
            {
                if (head[r] < 0 || link[head[r]] < 0) continue;
                HealGroup(head[r], link, pairs);
            }
            Pairs = pairs.Count;
        }

        /// <summary>One group, given as a chain through <paramref name="link"/>.</summary>
        private void HealGroup(int first, int[] link, HashSet<int> pairs)
        {
            int rep = -1, repN = -1, parts = 0, partMask = 0;
            for (int v = first; v >= 0; v = link[v])
            {
                int n = Influences(v, default, default);
                if (n > repN) { repN = n; rep = v; }
                if ((partMask & (1 << _vPart[v])) == 0) { partMask |= 1 << _vPart[v]; parts++; }
            }
            if (parts < 2 || rep < 0 || repN <= 0) return;
            var nm = new string[4];
            var wt = new float[4];
            int inf = Influences(rep, nm, wt);
            if (inf <= 0) return;

            // The tangent frame is averaged only where the partners AGREE on handedness: w is read as a
            // sign, never interpolated, and two frames of opposite handedness have no meaningful average --
            // those vertices keep the frame their own file states.
            Vector3 nSum = Vector3.Zero, tSum = Vector3.Zero;
            int hand = 0;
            bool mixed = false;
            for (int v = first; v >= 0; v = link[v])
            {
                nSum += _vNrm[v];
                float w = _vTan[v].W;
                if (w == 0f) continue;
                int sign = w < 0f ? -1 : 1;
                if (hand == 0) hand = sign; else if (hand != sign) mixed = true;
                tSum += AsV3(_vTan[v]);
            }
            if (mixed) Handed++;
            Vector3 nAvg = Norm(nSum), tAvg = mixed || hand == 0 ? Vector3.Zero : Norm(tSum);
            float repW = hand < 0 ? -1f : 1f;

            for (int v = first; v >= 0; v = link[v])
            {
                int k = _vPart[v];
                for (int u = first; u >= 0; u = link[u])
                    if (_vPart[u] != k) pairs.Add(_vPart[u] < k ? (_vPart[u] << 8) | k : (k << 8) | _vPart[u]);
                float ang = MathF.Acos(Math.Clamp(Vector3.Dot(_vNrm[v], nAvg), -1f, 1f));
                if (_vNrm[v] != Vector3.Zero && ang > Degree) { Lines++; MaxAngle = MathF.Max(MaxAngle, ang / Degree); }
                if (!SameInfluences(v, nm, wt, inf)) Gaps++;
                if (!Write(v, nm, wt, inf, nAvg, tAvg, repW)) continue;
                Healed++;
            }
        }

        /// <summary>Vertex <paramref name="v"/>'s live influences, as (name, weight) pairs in its own part's
        /// table; the count alone when the spans are empty. An influence whose slot named no part is dropped
        /// and the rest renormalized, since that slot deforms nothing.</summary>
        private int Influences(int v, string[]? names, float[]? weights)
        {
            int k = _vPart[v], i = _vIdx[v];
            SkinDef s = _skin[k];
            int[] bone = _bone[k];
            Vector4 w = s.Weights[i];
            int n = 0;
            float sum = 0f;
            for (int c = 0; c < 4; c++)
            {
                float wc = c == 0 ? w.X : c == 1 ? w.Y : c == 2 ? w.Z : w.W;
                if (wc <= 0f) continue;
                int slot = s.BoneIndices[i * 4 + c];
                if ((uint)slot >= (uint)bone.Length || bone[slot] < 0) continue;
                string nm = _names[k][slot];
                if (nm.Length == 0) continue;
                if (names != null && weights != null) { names[n] = nm; weights[n] = wc; }
                sum += wc;
                n++;
            }
            if (weights != null && n > 0 && sum > 0f)
                for (int c = 0; c < n; c++) weights[c] /= sum;
            return n;
        }

        /// <summary>Does this vertex already deform exactly as the group's representative does? If not, it
        /// would move away from its partner under a pose -- which is the GAP half of the diagnosis.</summary>
        private bool SameInfluences(int v, string[] names, float[] weights, int n)
        {
            var mine = new string[4];
            var wt = new float[4];
            if (Influences(v, mine, wt) != n) return false;
            for (int a = 0; a < n; a++)
            {
                bool hit = false;
                for (int b = 0; b < n && !hit; b++)
                    hit = string.Equals(mine[a], names[b], StringComparison.OrdinalIgnoreCase)
                          && MathF.Abs(wt[a] - weights[b]) < 1e-4f;
                if (!hit) return false;
            }
            return true;
        }

        /// <summary>Write the unified influences and the averaged frame into one vertex's own part, taking a
        /// bone-table slot for any name that part does not already address. False when the table is full
        /// (64 slots is what a palette can hold), which leaves that vertex exactly as authored.</summary>
        private bool Write(int v, string[] names, float[] weights, int n, Vector3 nAvg, Vector3 tAvg,
                           float tanW)
        {
            int k = _vPart[v], i = _vIdx[v];
            Span<int> slot = stackalloc int[4];
            for (int c = 0; c < n; c++)
            {
                int at = -1;
                for (int b = 0; b < _names[k].Count && at < 0; b++)
                    if (string.Equals(_names[k][b], names[c], StringComparison.OrdinalIgnoreCase)) at = b;
                if (at < 0)
                {
                    if (_names[k].Count >= BoneCount) return false;
                    _names[k].Add(names[c]);
                    at = _names[k].Count - 1;
                }
                slot[c] = at;
            }
            MeshDef mesh = _mesh[k];
            SkinDef skin = _skin[k];
            int nv = mesh.Positions.Length;
            _newIdx[k] ??= (ushort[])skin.BoneIndices.Clone();
            _newWeight[k] ??= (Vector4[])skin.Weights.Clone();
            ushort[] idx = _newIdx[k]!;
            Vector4[] wgt = _newWeight[k]!;
            Span<float> four = stackalloc float[4];
            for (int c = 0; c < 4; c++)
            {
                idx[i * 4 + c] = (ushort)(c < n ? slot[c] : 0);
                four[c] = c < n ? weights[c] : 0f;
            }
            wgt[i] = new Vector4(four[0], four[1], four[2], four[3]);
            if (mesh.Normals.Length >= nv)
            {
                _newNrm[k] ??= (Vector3[])mesh.Normals.Clone();
                _newNrm[k]![i] = Norm(Vector3.TransformNormal(nAvg, _restInv[k]));
            }
            if (mesh.Tangents.Length >= nv && tAvg != Vector3.Zero)
            {
                // A degenerate xyz is "no frame" to the packing, so a cancelled average is not written at
                // all; the handedness lane stays exactly +1 or -1.
                Vector3 t = Norm(Vector3.TransformNormal(tAvg, _restInv[k]));
                if (t != Vector3.Zero)
                {
                    _newTan[k] ??= (Vector4[])mesh.Tangents.Clone();
                    _newTan[k]![i] = new Vector4(t, tanW < 0f ? -1f : 1f);
                }
            }
            _changed[k] = true;
            return true;
        }

        /// <summary>Publish one part's healed geometry: a copy of its mesh and skin, an arena range of its own
        /// (content-keyed, so two creatures assembled the same way share it) and a private draw recipe. A part
        /// nothing stitched is left exactly as it was, holding its model's own mesh and recipe.</summary>
        internal void Publish(int k, int part)
        {
            if (!_changed[k]) return;
            MeshDef mesh = _mesh[k].Copy();
            if (_newNrm[k] != null) mesh.Normals = _newNrm[k]!;
            if (_newTan[k] != null) mesh.Tangents = _newTan[k]!;
            SkinDef skin = _skin[k].Copy();
            skin.BoneNames = _names[k].ToArray();
            skin.BoneNodes = new int[skin.BoneNames.Length];
            for (int b = 0; b < skin.BoneNodes.Length; b++) skin.BoneNodes[b] = -1;
            skin.BoneNodesAreMap = false;
            skin.BindRotation = Array.Empty<Quaternion>();
            skin.BindTranslation = Array.Empty<Vector3>();
            skin.BoneIndices = _newIdx[k] ?? skin.BoneIndices;
            skin.Weights = _newWeight[k] ?? skin.Weights;
            skin.Bones = Packed(skin);
            int range = MeshArena.Acquire(mesh, skin);
            if (range < 0) { StitchRefuse($"the arena would not hold the stitched \"{PartTree.NameOf(part)}\""); return; }
            ModelDef? model = SceneDb.PartModel[part];
            int node = SceneDb.PartNode[part];
            int recipe = model == null ? -1 : DrawRecipe.InternDynamic(model, node, range);
            if (recipe < 0)
            {
                MeshArena.Release(range);
                StitchRefuse($"the recipe table would not hold the stitched \"{PartTree.NameOf(part)}\"");
                return;
            }
            _stitch.Skin[part] = skin;
            _stitch.Held[part] = (recipe, DrawRecipe.Gen(recipe));
            DrawRecipe.Assign(part, recipe);
            // the palette is solved against the skin, and this part's is now the stitched one
            _namedBind[part] = null;
            SceneDb.TouchPart(part, Dirty.Anim);
        }

        /// <summary>The byte-packed per-vertex slot word the skinned vertex format carries, rebuilt from the
        /// faithful <see cref="SkinDef.BoneIndices"/> the stitch wrote.</summary>
        private static uint[] Packed(SkinDef s)
        {
            int nv = s.BoneIndices.Length / 4;
            var o = new uint[nv];
            for (int i = 0; i < nv; i++)
                o[i] = (uint)((s.BoneIndices[i * 4] & 0xFF) | ((s.BoneIndices[i * 4 + 1] & 0xFF) << 8)
                            | ((s.BoneIndices[i * 4 + 2] & 0xFF) << 16) | ((s.BoneIndices[i * 4 + 3] & 0xFF) << 24));
            return o;
        }

        private static Vector3 AsV3(Vector4 v) => new(v.X, v.Y, v.Z);

        private static Vector3 Norm(Vector3 v)
        {
            float l = v.Length();
            return l > 1e-12f ? v / l : Vector3.Zero;
        }
    }
}
