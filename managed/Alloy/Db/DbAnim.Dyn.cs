using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>Per-part DYNAMIC vertex positions: the one thing a pose cannot express in a matrix, because the
/// vertices themselves move. Two producers (the animmesh morph and the dangly solve) and one consumer
/// (seam-feed, which uploads the buffer into the part's mesh-arena range); the writable copy is the part's
/// own, so the arena never holds rest data for it. Layout and ownership: <c>Db/DbPose.LAYOUT.md</c>.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Per part: the dynamic position buffer (<c>3 * Count</c> floats, model space) and its vertex
    /// count.</summary>
    private static readonly nint[] _dyn = new nint[SceneDb.MaxParts];
    /// <summary>The same part's dynamic UV buffer (<c>2 * Count</c> floats), or 0 when nothing animates its
    /// UVs. It hangs off the position buffer -- same vertex count, same list slot, same arena range -- so only
    /// <see cref="DynTakeUv"/> and <see cref="DynDropAt"/> know it exists.</summary>
    private static readonly nint[] _dynUv = new nint[SceneDb.MaxParts];
    private static readonly int[] _dynN = new int[SceneDb.MaxParts];
    /// <summary>Parts holding a dynamic buffer, so a wipe does not walk the whole part table.</summary>
    private static readonly int[] _dynList = new int[MaxDynParts];
    private static int _dynCount;
    /// <summary>Parts that may hold a dynamic buffer at once (cloaks, hair, flags, morphed effects). Past this
    /// the part draws its REST geometry, which is said once and counted -- a still cloak, not a hole.</summary>
    private const int MaxDynParts = 2048;
    private static int _dynFull;

    private static int _dynProduced;

    /// <summary>Per part: its PRIVATE mesh-arena range, so the solved vertices are what the draw reads. -1
    /// until the first solve asks for one.</summary>
    private static readonly int[] _dynRange = new int[SceneDb.MaxParts];
    /// <summary>Per part: the private recipe it took (plus one) and that slot's hand-out generation, so the drop
    /// retires THAT recipe -- the part's current one may already be a new tenant's.</summary>
    private sealed class DynRecipeState
    {
        public readonly int[] Recipe = new int[SceneDb.MaxParts];
        public readonly uint[] Gen = new uint[SceneDb.MaxParts];
    }
    private static DynRecipeState _dynRecipe = new();
    private static int _dynNoRange;

    /// <summary>The private arena range holding <paramref name="part"/>'s solved vertices, or -1.</summary>
    public static int DynRange(int part)
        => (uint)part < (uint)SceneDb.MaxParts && _dyn[part] != 0 ? _dynRange[part] - 1 : -1;

    /// <summary>Give <paramref name="part"/> a dynamic buffer seeded from <paramref name="rest"/>, or null when
    /// the table is full (said once, counted).</summary>
    private static float* DynTake(int part, Vector3[] rest)
    {
        nint had = _dyn[part];
        // A buffer this part already holds is only its buffer while the VERTEX COUNT still matches: a tree
        // change re-tenants the part index, and a cloak replaced by a robe keeps the old, shorter allocation
        // while the solve writes the new mesh's count past the end of it. OnTreeChanged drops these too; this
        // is the belt to that brace, because a part range can also be re-tenanted by a fresh object.
        if (had != 0)
        {
            if (_dynN[part] == rest.Length) return (float*)had;
            DynDrop(part);
            had = 0;
        }
        if (rest.Length == 0) return null;
        if (_dynCount >= MaxDynParts)
        {
            _dynFull++;
            if (_dynFull == 1)
                _log?.Always($"DbAnim: more than {MaxDynParts} parts want a dynamic vertex buffer -- the rest "
                           + "draw their REST geometry (a still cloak, not a hole). Counted on [dbanim].");
            return null;
        }
        nuint bytes = (nuint)rest.Length * 3 * sizeof(float);
        float* buf = (float*)NativeMemory.Alloc(bytes);
        fixed (Vector3* src = rest) Buffer.MemoryCopy(src, buf, (long)bytes, (long)bytes);
        _dyn[part] = (nint)buf;
        _dynN[part] = rest.Length;
        _dynList[_dynCount++] = part;
        TakeRange(part);
        return buf;
    }

    /// <summary>The part's dynamic UV buffer, seeded from the mesh's rest set, or null when the part holds no
    /// position buffer to hang it off (the caller has already taken one, so that is a refusal it counted).
    /// A vertex the rest set does not reach keeps 0,0, which is what the arena wrote for it.</summary>
    private static float* DynTakeUv(int part, Vector2[] rest)
    {
        nint had = _dynUv[part];
        if (had != 0) return (float*)had;
        int n = _dynN[part];
        if (_dyn[part] == 0 || n == 0) return null;
        nuint bytes = (nuint)n * 2 * sizeof(float);
        var buf = (float*)NativeMemory.AllocZeroed(bytes);
        int seed = Math.Min(n, rest.Length);
        for (int i = 0; i < seed; i++) { buf[i * 2] = rest[i].X; buf[i * 2 + 1] = rest[i].Y; }
        _dynUv[part] = (nint)buf;
        return buf;
    }

    /// <summary>Give this part its own arena range and its own recipe, so the solved vertices are what the
    /// record feed draws. The key is the part index through splitmix, which cannot be another mesh's content
    /// key in practice and, if it ever were, would share a static range -- visible, never corrupt.</summary>
    private static void TakeRange(int part)
    {
        ModelDef? m = SceneDb.PartModel[part];
        int node = SceneDb.PartNode[part];
        if (m == null || (uint)node >= (uint)m.Nodes.Length) return;
        MeshDef? mesh = m.Nodes[node].Mesh;
        if (mesh == null) return;
        int range = MeshArena.AcquireDynamic(mesh, Mix(0x44594e41_00000000UL | (uint)part));
        if (range < 0)
        {
            _dynNoRange++;
            if (_dynNoRange == 1)
                _log?.Always("DbAnim: the mesh arena refused a per-instance range -- this dangly/animmesh part "
                           + "draws its REST frame (it will not move). Counted on [dbanim].");
            return;
        }
        int recipe = DrawRecipe.InternDynamic(m, node, range);
        if (recipe < 0)
        {
            MeshArena.Release(range);
            _dynNoRange++;
            if (_dynNoRange == 1)
                _log?.Always("DbAnim: the draw-recipe table refused a per-instance recipe -- this "
                           + "dangly/animmesh part draws its REST frame (it will not move). Counted on [dbanim].");
            return;
        }
        _dynRange[part] = range + 1;
        _dynRecipe.Recipe[part] = recipe + 1;
        _dynRecipe.Gen[part] = DrawRecipe.Gen(recipe);
        _dynRangeTaken = true;
        DrawRecipe.Assign(part, recipe);
        SceneDb.TouchPart(part, Dirty.Anim);
    }

    /// <summary>Hand this frame's solved positions to the arena, which uploads the slice at the next
    /// render-side pump.</summary>
    private static void DynPublish(int part)
    {
        int range = _dynRange[part] - 1;
        if (range < 0) return;
        MeshArena.WriteDynamic(range, (float*)_dyn[part], (float*)_dynUv[part], _dynN[part]);
    }

    /// <summary>Release every dynamic buffer of <paramref name="obj"/>'s part range (object retirement, a tree
    /// change, an LOD swap). The part records are about to be re-tenanted or freed, so nothing may hold their
    /// buffers, their arena ranges or their private recipes.</summary>
    private static void DanglyForget(DbId obj)
    {
        if (obj.IsNull) return;
        int i = obj.Index;
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        if (n <= 0) return;
        for (int k = _dynCount - 1; k >= 0; k--)
        {
            int p = _dynList[k];
            if (p < first || p >= first + n) continue;
            DynDropAt(p, k);
        }
        DanglyBonesForget(first, n);
    }

    /// <summary>Release one part's dynamic state, wherever it sits in the live list.</summary>
    private static void DynDrop(int part)
    {
        for (int k = _dynCount - 1; k >= 0; k--)
            if (_dynList[k] == part) { DynDropAt(part, k); return; }
    }

    /// <summary>...and with its list index already in hand. The private RECIPE goes back too: it pins a
    /// <see cref="ModelDef"/> and holds an arena range, and the recipe table is a bump allocator, so leaking
    /// one per cloak swap ends with every dangly part on rest geometry.</summary>
    private static void DynDropAt(int part, int k)
    {
        if (_dyn[part] != 0) NativeMemory.Free((void*)_dyn[part]);
        if (_dynUv[part] != 0) NativeMemory.Free((void*)_dynUv[part]);
        if (_dynRecipe.Recipe[part] > 0)
        {
            int r = _dynRecipe.Recipe[part] - 1;
            if (SceneDb.PartRecipe[part] == r) DrawRecipe.Assign(part, -1);
            DrawRecipe.RetireDynamic(r, _dynRecipe.Gen[part]);
        }
        _dynRecipe.Recipe[part] = 0;
        _dyn[part] = 0; _dynUv[part] = 0; _dynN[part] = 0;
        _dynRange[part] = 0;
        SceneDb.PartLitAnim[part] = 0;
        _dynList[k] = _dynList[--_dynCount];
    }

    /// <summary>
    /// The animmesh morph, from <c>MdlNodeAnimMesh::InternalControl</c> @0x499320: pick the frame from
    /// <c>t / samplePeriod</c>, lerp into the next frame inside the pool, and blend the result into the part's
    /// dynamic buffers by <paramref name="w"/>. BOTH pools are morphed -- positions and UVs, which is how a
    /// scrolling-UV effect moves -- and the engine's shared-frame-index quirk is kept.
    /// </summary>
    private static void Morph(int part, AnimMeshDef mesh, float t, float w)
    {
        Vector3[][] frames = mesh.Positions;
        Vector2[][] uvFrames = mesh.Uvs;
        if (frames.Length == 0 && uvFrames.Length == 0) return;
        MeshDef? rest = SceneDb.PartModel[part]?.Nodes[SceneDb.PartNode[part]].Mesh;
        if (rest == null) return;
        float* dst = DynTake(part, rest.Positions);
        if (dst == null) return;
        int n = _dynN[part];
        if (n == 0) return;

        float period = mesh.SamplePeriod;
        // Exact-zero test, as the engine's: a zero period parks every morph on frame 0.
        int f = period == 0f ? 0 : (int)(t / period);
        if (f < 0) f = 0;
        float u = 1f - w;
        bool moved = false;

        if (frames.Length != 0)
        {
            int vf = frames.Length;
            bool lerp = false;
            if (f < vf - 1) lerp = true;
            else if (f >= vf) f = vf - 1;
            float a = lerp ? t / period - f : 0f;
            Vector3[] k0 = frames[f];
            Vector3[] k1 = lerp ? frames[f + 1] : k0;
            // Both frames, not just the first: a model whose morph frames differ in length would index past
            // the shorter one. The engine's pools are rectangular, so this only bites on content that is not.
            int verts = Math.Min(n, Math.Min(k0.Length, k1.Length));
            for (int v = 0; v < verts; v++)
            {
                Vector3 src = lerp ? k0[v] * (1f - a) + k1[v] * a : k0[v];
                int o = v * 3;
                float x = dst[o] * u + src.X * w, y = dst[o + 1] * u + src.Y * w, z = dst[o + 2] * u + src.Z * w;
                if (dst[o] == x && dst[o + 1] == y && dst[o + 2] == z) continue;
                dst[o] = x; dst[o + 1] = y; dst[o + 2] = z;
                moved = true;
            }
        }

        if (uvFrames.Length != 0)
        {
            float* uvDst = DynTakeUv(part, rest.Uv[0]);
            if (uvDst != null)
            {
                bool uvMoved = false;
                int tf = uvFrames.Length;
                // f arrives already clamped by the position pool, which is the engine's own order.
                bool lerp = f < tf - 1;
                if (f >= tf) f = tf - 1;
                float a = lerp ? t / period - f : 0f;
                Vector2[] k0 = uvFrames[f];
                Vector2[] k1 = lerp ? uvFrames[f + 1] : k0;
                int verts = Math.Min(n, Math.Min(k0.Length, k1.Length));
                for (int v = 0; v < verts; v++)
                {
                    Vector2 src = lerp ? k0[v] * (1f - a) + k1[v] * a : k0[v];
                    int o = v * 2;
                    float x = uvDst[o] * u + src.X * w, y = uvDst[o + 1] * u + src.Y * w;
                    if (uvDst[o] == x && uvDst[o + 1] == y) continue;
                    uvDst[o] = x; uvDst[o + 1] = y;
                    moved = true;
                    uvMoved = true;
                }
                if (uvMoved) _uvMorphs++;
            }
        }

        if (!moved) return;
        _morphs++;
        _dynProduced++;
        DynPublish(part);
        SceneDb.TouchPart(part, Dirty.Anim);
    }

    private static int _uvMorphs;

    /// <summary>The engine's <c>AnimMeshHitCheck</c>: every face of <paramref name="part"/> against its MORPHED
    /// positions (rest until a morph runs), the plane re-derived per face, <paramref name="to"/> shortened to the
    /// nearest hit. A GUI scrollbar's thumb is a morph-stretched sliver, so its rest faces are not its shape.</summary>
    public static bool CastMorphed(int part, MeshDef mesh, Vector3 from, ref Vector3 to)
    {
        float* d = (uint)part < (uint)SceneDb.MaxParts && _dynN[part] == mesh.Positions.Length ? (float*)_dyn[part] : null;
        bool hit = false;
        foreach (FaceDef f in mesh.Faces)
            hit |= HitTest.PolyHit(Vert(d, mesh, f.V0), Vert(d, mesh, f.V1), Vert(d, mesh, f.V2), from, ref to);
        return hit;
    }

    private static Vector3 Vert(float* d, MeshDef mesh, int i)
        => d == null ? mesh.Positions[i] : new Vector3(d[i * 3], d[i * 3 + 1], d[i * 3 + 2]);

    /// <summary>Said once, at the end of the first period that produced dynamic vertices no part ever drew:
    /// the buffers are computed and the parts draw their rest geometry, which is a visible difference and not
    /// an error this code can fix on its own.</summary>
    private static void DynAudit()
    {
        if (_dynProduced == 0 || _dynRangeTaken || _dynAudited) return;
        _dynAudited = true;
        _log?.Always("DbAnim: dynamic vertex buffers (dangly/animmesh) are being produced but no part ever took "
                   + "a private arena range for them -- those parts are drawing their REST geometry "
                   + "(Db/DbPose.LAYOUT.md).");
    }

    /// <summary>splitmix64's finalizer: turns an index into well-spread bits.</summary>
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
        z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
        return z ^ (z >> 31);
    }

    /// <summary>True once a part got its own arena range, which is the OTHER way the solved vertices
    /// reach the screen (and the one that needs nothing from the feed).</summary>
    private static bool _dynRangeTaken;

    private static bool _dynAudited;
}
