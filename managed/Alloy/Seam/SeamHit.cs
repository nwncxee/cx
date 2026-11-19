using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>Where the scene and camera hit slots meet the cast: three slots take a world-space segment and
/// answer in three shapes -- the <c>CHitInfo</c> KIND, the <c>CAurObject*</c> hit, or that from a pixel -- so
/// the cast, the point write-back and the <c>CHitInfo</c> fill are shared here. The record is passed, never held
/// in a <c>CurrentHitInfo</c> global, so a cast inside a cast's callback cannot clobber its caller's answer.</summary>
internal static unsafe class SeamHit
{
    /// <summary>Cast a world-space segment through <paramref name="scene"/>: write the hit point to
    /// <paramref name="at"/>, fill <paramref name="info"/> byte-identically to the engine's <c>CHitInfo</c>,
    /// and answer the KIND (0 nothing, 1 static, 2 an object) -- which is what <c>Scene::HitCheck</c> returns
    /// and what <c>GetGameobjectHit</c> reads back out of <c>+0x24</c>.</summary>
    internal static HitKind Cast(DbId scene, Vector3 from, Vector3 to, Vec3* at, nint info)
        => Cast(scene, from, to, at, info, out _);

    /// <inheritdoc cref="Cast(DbId, Vector3, Vector3, Vec3*, nint)"/>
    /// <param name="obj">The object hit, for the slot that returns one.</param>
    internal static HitKind Cast(DbId scene, Vector3 from, Vector3 to, Vec3* at, nint info, out DbId obj)
    {
        obj = default;
        // The ignore list comes from a small STACK of buffers, not one shared static: a cast can re-enter (a
        // hit handler that casts again), and a single buffer would hand the inner cast's list to the outer
        // one. HitQuery wants a DbId[], so this cannot be a stackalloc -- it is a depth-indexed pool instead,
        // which allocates nothing and is honest about its ceiling.
        int depth = _depth;
        if (depth >= MaxDepth)
        {
            _tooDeep++;
            return HitKind.None;
        }
        _depth = depth + 1;
        try
        {
            // Records created since the last fold are not in the index yet, and the engine casts MID-LOAD
            // (see HitFeed.Sync). Outermost cast only: a re-entrant one is inside the same frame's data.
            if (depth == 0) HitFeed.Sync();
            HitQuery q = Query(scene, from, to, info, _ignore[depth], EngineBoxExpand());
            bool hit;
            HitResult r;
            try { hit = HitTest.Cast(in q, out r); }
            finally { Ignore(info, _ignore[depth], false); }
            if (!hit) return HitKind.None;
            obj = r.Obj;
            if (at != null) { at->X = r.Point.X; at->Y = r.Point.Y; at->Z = r.Point.Z; }
            if (info != 0) HitTest.Fill(in r, (byte*)info);
            return r.Kind;
        }
        finally { _depth = depth; }
    }

    /// <summary>The query a slot's <c>CHitInfo</c> describes: the caller's surface mask, box-only bit,
    /// statics-only gate and ignore list, all of which the real client fills (it replaces the mask with
    /// <c>CClientExoApp::GetHitCheckMaterialMask</c>'s value and ignores the followed creature). A null record
    /// is a plain pick. <paramref name="boxExpand"/> is the engine's box-pick margin.</summary>
    internal static HitQuery Query(DbId scene, Vector3 from, Vector3 to, nint info, ulong[] ignore, Vector3 boxExpand)
    {
        HitQuery q = HitQuery.Pick(scene, from, to);
        if (info == 0) return q;
        var h = new CHitInfo(info);
        q.SurfaceMask = (ulong)h.SurfaceMask;
        q.BoxOnly = h.BoxOnly != 0;
        q.BoxExpand = boxExpand;
        // CHitInfo+0x00 non-zero is the engine's "statics only": Scene::HitCheck runs the BSP pass and stops.
        q.Objects = h.Mode == 0;
        int n = h.IgnoreCount;
        nint* list = (nint*)h.IgnoreList;
        // HitPartOnBSPNode's ground arm is gated on BOTH ignore words being zero, so a record that carries
        // an ignore list (every mouse pick: the followed creature) does not fall through to the plane.
        q.Ground = HitTest.GroundPlane && n == 0 && list == null;
        if (n <= 0 || list == null) return q;
        Ignore(info, ignore, true);
        q.IgnoreBits = ignore;
        return q;
    }

    /// <summary>Set (or clear, after the cast) one bit a record for every object on the record's ignore list. The
    /// list is long: <c>CNWCObject::SetUseable</c> @0x7e9450 puts EVERY non-usable object on it (bloodstains, decor),
    /// and <c>SetHitcheckIgnoreList</c> @0x645970 hands the mouse picks the whole of it, so a cap here made the
    /// surplus pickable. Clearing re-walks the same list, so a cast costs O(list) and nothing else.</summary>
    private static void Ignore(nint info, ulong[] bits, bool on)
    {
        if (info == 0) return;
        var h = new CHitInfo(info);
        int n = h.IgnoreCount;
        nint* list = (nint*)h.IgnoreList;
        if (n <= 0 || list == null) return;
        for (int i = 0; i < n; i++)
        {
            DbId o = Facade.ObjectId(list[i]);
            if (!SceneDb.Objects.Valid(o)) continue;
            if (on) bits[o.Index >> 6] |= 1UL << (o.Index & 63);
            else bits[o.Index >> 6] &= ~(1UL << (o.Index & 63));
        }
    }

    private static long _tooDeep;

    /// <summary>Nested casts one thread may have in flight. Two is the engine's deepest shape; four is slack.</summary>
    private const int MaxDepth = 4;
    /// <summary><c>g_HitCheckBoxExpansion</c>, the margin <c>CClientExoAppInternal::UpdateCamera</c> @0x63fbb0 sets
    /// each frame from the camera distance and <c>PartTriMesh::HitCheckGeom</c> @0x438570 adds to every box pick.</summary>
    private static readonly nint BoxExpansion = Native.Sym(SymbolMap.HitCheckBoxExpansion);

    /// <summary>The engine's box-pick margin as it stands this frame; zero where the global is not resolved.</summary>
    private static Vector3 EngineBoxExpand() => BoxExpansion == 0 ? default : *(Vector3*)BoxExpansion;

    /// <summary>The engine's box-pick margin as it stands, for the pick diagnostic.</summary>
    internal static Vector3 BoxMargin => EngineBoxExpand();

    [ThreadStatic] private static int _depth;
    /// <summary>One ignore bitset a nesting depth, a bit a record index.</summary>
    private static readonly ulong[][] _ignore = Pool();
    private static ulong[][] Pool()
    {
        var a = new ulong[MaxDepth][];
        for (int i = 0; i < MaxDepth; i++) a[i] = new ulong[(SceneDb.MaxObjects + 63) / 64];
        return a;
    }

    /// <summary>Casts refused past <see cref="MaxDepth"/> -- counted, never silent.</summary>
    internal static string Stats() => _tooDeep == 0 ? "" : $"hit seam: {_tooDeep} casts REFUSED (nested past {MaxDepth})";
}
