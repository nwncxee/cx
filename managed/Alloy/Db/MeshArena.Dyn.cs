using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>The arena's DYNAMIC half -- a range whose vertices one instance owns, because a dangly mesh and an
/// animmesh move the vertices themselves and cannot share a content-keyed range with another instance of the
/// same model. <c>Build</c> takes a caller-chosen key so nothing dedups onto it; a solve patches the POSITIONS
/// in the host mirror and queues the range, which <see cref="MeshArena.Pump"/> uploads on the render thread.</summary>
public static unsafe partial class MeshArena
{
    /// <summary>Ranges whose vertices changed since the last <see cref="Pump"/>. Small: one entry per dangly or
    /// animmesh part in view, and a settled cloak does not enter it at all.</summary>
    private static readonly int[] _dynDirty = new int[DynDirtyCap];
    private static int _dynDirtyN;
    private const int DynDirtyCap = 4096;
    private static long _dynUploads, _dynBytes;
    private static int _dynOverflow;
    /// <summary>Per-period counts of the dynamic half's refusals. <c>Stale</c>: cached per-instance ranges
    /// that held another mesh -- a part index re-tenanted by a different model must not draw the old run.</summary>
    private sealed class DynLedgerState { internal long Stale; }
    private static DynLedgerState _dynLedger = new();

    /// <summary>A PRIVATE range holding <paramref name="mesh"/>'s rest vertices, keyed on
    /// <paramref name="unique"/> so no other instance can share it. -1 on a refusal (counted by the arena's own
    /// refusal ledger, like every other).</summary>
    internal static int AcquireDynamic(MeshDef mesh, ulong unique)
    {
        if (mesh == null) return -1;
        lock (_gate)
        {
            if (_byContent.TryGetValue(unique, out int have) && Alive(have))
            {
                if (_ranges[have].Keys is { Count: > 0 } keys && ReferenceEquals(keys[0], mesh))
                {
                    _ranges[have].Refs++;
                    return have;
                }
                _dynLedger.Stale++;
            }
            if (mesh.Indices.Length < 3 || mesh.Positions.Length == 0) return Refuse(Refusal.Empty);
            if (mesh.Positions.Length > MaxMeshVerts) return Refuse(Refusal.TooManyVerts);
            return Build(mesh, null, unique, 0, Indices16(mesh), null, 0, null, 0);   // per-instance: no LOD runs, no second key
        }
    }

    /// <summary>Patch <paramref name="range"/>'s positions from <paramref name="pos"/> (<c>3 * n</c> floats,
    /// model space) and, when <paramref name="uv"/> is given, its diffuse UV lane from it (<c>2 * n</c>
    /// floats); queue the range for the next upload. Game thread. False = no such range, or its vertex count
    /// does not match (a mesh and a solve that disagree is a refusal, never a partial write).</summary>
    internal static bool WriteDynamic(int range, float* pos, float* uv, int n)
    {
        if (pos == null || n <= 0) return false;
        lock (_gate)
        {
            if (!Alive(range)) return false;
            ref Range rr = ref _ranges[range];
            if (n > rr.NV) return false;
            int f = (int)rr.Format, stride = StrideOf[f];
            Section s = _sect[f];
            float[] mirror = s.Mirror;
            int bas = rr.Base;
            if ((long)(bas + n) * stride > mirror.Length) return false;
            var bmin = new Vector3(float.MaxValue);
            var bmax = new Vector3(float.MinValue);
            for (int i = 0; i < n; i++)
            {
                int o = (bas + i) * stride, k = i * 3;
                float x = pos[k], y = pos[k + 1], z = pos[k + 2];
                mirror[o] = x; mirror[o + 1] = y; mirror[o + 2] = z;
                // The UVs ride the same slice, so an animmesh that scrolls its texture needs no second write.
                if (uv != null) { mirror[o + 6] = uv[i * 2]; mirror[o + 7] = uv[i * 2 + 1]; }
                if (x < bmin.X) bmin.X = x;
                if (y < bmin.Y) bmin.Y = y;
                if (z < bmin.Z) bmin.Z = z;
                if (x > bmax.X) bmax.X = x;
                if (y > bmax.Y) bmax.Y = y;
                if (z > bmax.Z) bmax.Z = z;
            }
            // The bound moves with the vertices, or a flapping cloak is culled at the edge of a pose it no
            // longer holds. The centre and radius are what the cull and the caster bake read.
            rr.BMin = bmin; rr.BMax = bmax;
            rr.Center = (bmin + bmax) * 0.5f;
            rr.Radius = (bmax - rr.Center).Length();
            Queue(range);
            return true;
        }
    }

    /// <summary>Under <see cref="_gate"/>: note the range as dirty, once.</summary>
    private static void Queue(int range)
    {
        for (int i = 0; i < _dynDirtyN; i++) if (_dynDirty[i] == range) return;
        if (_dynDirtyN >= DynDirtyCap)
        {
            _dynOverflow++;
            return;
        }
        _dynDirty[_dynDirtyN++] = range;
    }

    /// <summary>Render thread, inside the GL bracket, under <see cref="_gate"/> (called from
    /// <see cref="Pump"/>): send each dirty range's vertex slice. A range whose section has not reached the GPU
    /// yet is skipped -- the append upload that follows carries its bytes anyway.</summary>
    private static void DynUpload()
    {
        for (int i = 0; i < _dynDirtyN; i++)
        {
            int range = _dynDirty[i];
            if (!Alive(range)) continue;
            ref Range rr = ref _ranges[range];
            int f = (int)rr.Format, stride = StrideOf[f];
            Section s = _sect[f];
            long bytes = (long)rr.NV * stride * 4;
            if (!DynUploadVk(in rr, stride)) continue;
            _dynUploads++;
            _dynBytes += bytes;
        }
        _dynDirtyN = 0;
    }

    /// <summary>One clause for the arena's period line: how much the moving vertices cost.</summary>
    internal static string DynStats()
    {
        if (_dynUploads == 0 && _dynOverflow == 0 && _dynLedger.Stale == 0) return "";
        string s = $", dynamic: {_dynUploads} upload(s) / {_dynBytes >> 10} KiB"
                 + (_dynOverflow == 0 ? "" : $", {_dynOverflow} DROPPED past the dirty cap (a frozen cloak)")
                 + (_dynLedger.Stale == 0 ? "" : $", {_dynLedger.Stale} cached range(s) held another mesh and were rebuilt");
        _dynUploads = 0; _dynBytes = 0; _dynOverflow = 0; _dynLedger = new();
        return s;
    }
}

/// <summary>A recipe whose geometry is ONE instance's: interned outside <c>DrawRecipe</c>'s per-(model, node)
/// table so it is never handed to another part, and carrying a range the pose step owns. A dangly part draws
/// its solved vertices with no change to the record feed -- the feed reads the part's recipe, and this is it.</summary>
internal static partial class DrawRecipe
{
    /// <summary>Give back the private recipe <see cref="InternDynamic"/> handed out as (<paramref name="r"/>,
    /// <paramref name="gen"/>), with its range. A slot its model's eviction already retired -- or retired and
    /// handed on -- took the range with it, so nothing is released twice; one a part still names waits for it.</summary>
    internal static void RetireDynamic(int r, uint gen)
    {
        if ((uint)r >= (uint)_n || _own.Gen[r] != gen || !_own.Held[r]) return;
        if (_own.Users[r] > 0) { _own.Orphan[r] = true; return; }
        Retire(r);
    }

    /// <summary>A private copy of node <paramref name="node"/>'s recipe pointing at <paramref name="range"/>;
    /// -1 when the node draws nothing or the table is full. Called from the game thread like the fold, so no
    /// second writer and the fixed-size table never reallocates.</summary>
    internal static int InternDynamic(ModelDef model, int node, int range)
    {
        if ((uint)node >= (uint)model.Nodes.Length || range < 0) return -1;
        NodeDef nd = model.Nodes[node];
        MeshDef? mesh = nd.Mesh;
        if (mesh == null || !mesh.Render || mesh.Indices.Length == 0) return -1;
        int r = Take();
        if (r < 0) return -1;
        // Deliberately NOT filed in _byNode: another instance of this model must get its own.
        _model[r] = model; _node[r] = node; _mesh[r] = mesh;
        Fill(r, nd, mesh);
        // A dangly part on a range of its own is the one drawing SOLVED vertices, which is the stock path: it
        // has no palette, whatever its node's rig would have given the shared recipe.
        if (nd.Kind == NodeKind.Dangly) _flags[r] &= ~RecipeFlag.Skinned;
        // Fill took a reference on the SHARED range; hand it back and take the private one instead.
        if (_range[r] >= 0 && _range[r] != range) MeshArena.Release(_range[r]);
        _range[r] = range;
        return r;
    }
}
