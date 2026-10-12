using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Wires <see cref="DbTriggers"/>.</summary>
public sealed class DbTriggersService : IExtenderService
{
    public void Initialize(ExtenderLog log, HookService hooks) => DbTriggers.Install(log, hooks);
}

/// <summary>The area transitions' prisms. The engine files each <c>AurPartTriggerCreate</c> @0x4da840 prism
/// into the scene BSP that both the hit test and <c>DoHighlightTriggers</c> read; the scene database has no
/// BSP, so this keeps the live ones per scene, a copy of their triangles for the pick (<see cref="Cast"/>,
/// <c>PartTrigger::HitCheckGeom</c>'s rule), and hands the pointers to <see cref="TriggerHighlight"/>. Cold.</summary>
internal static unsafe class DbTriggers
{
    private sealed class Entry
    {
        public nint Ptr;
        public DbId Scene;
        public Vector3[] Tris = [];
        public Vector3 Min, Max;
        public long Payload;
    }

    private static readonly List<Entry> _live = new();
    private static nint[] _ptrs = new nint[16];
    private static AurPartTriggerCreateFn* _create;
    private static AurPartTriggerDeleteFn* _delete;
    private static AurPartTriggerReleaseMemoryFn* _release;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!Renderer.On) return;
        _log = log;
        _create = hooks.ChainAurPartTriggerCreate(&OnCreate, 0);
        _delete = hooks.ChainAurPartTriggerDelete(&OnDelete, 0);
        _release = hooks.ChainAurPartTriggerReleaseMemory(&OnRelease, 0);
        log.Info(_create != null && _delete != null && _release != null
            ? "DbTriggers: area transitions are pickable and highlight on hover"
            : "DbTriggers: a trigger hook did not chain -- area transitions can NOT be hovered in DB mode");
    }

    /// <summary>The live triggers' engine objects, for the highlight pass. Game thread.</summary>
    internal static ReadOnlySpan<nint> Live => _ptrs.AsSpan(0, _live.Count);

    [UnmanagedCallersOnly]
    private static nint OnCreate(nint scene, nint points, int count, int snap, nint colour, nint data)
    {
        nint t = _create != null && *_create != null ? (*_create)(scene, points, count, snap, colour, data) : 0;
        if (t != 0) Add(t, Facade.SceneId(scene));
        return t;
    }

    [UnmanagedCallersOnly]
    private static void OnDelete(nint scene, nint t)
    {
        Remove(t);
        if (_delete != null && *_delete != null) (*_delete)(scene, t);
    }

    [UnmanagedCallersOnly]
    private static void OnRelease(nint t)
    {
        Remove(t);
        if (_release != null && *_release != null) (*_release)(t);
    }

    private static void Add(nint t, DbId scene)
    {
        Remove(t);
        var trig = new PartTrigger(t);
        int n = trig.TriVerts / 3 * 3;
        var src = (Vector3*)trig.Tris;
        var e = new Entry { Ptr = t, Scene = scene, Payload = trig.Payload, Tris = new Vector3[src == null ? 0 : n] };
        e.Min = new Vector3(float.MaxValue);
        e.Max = new Vector3(float.MinValue);
        for (int i = 0; i < e.Tris.Length; i++)
        {
            e.Tris[i] = src[i];
            e.Min = Vector3.Min(e.Min, src[i]);
            e.Max = Vector3.Max(e.Max, src[i]);
        }
        _live.Add(e);
        Sync();
        _created++;
        if (_saidCreate++ < 4)
            _log?.Info($"DbTriggers: prism {t:x} in scene {scene.Value:x} (world={SceneDb.Scenes.Valid(scene) && SceneDb.SceneIsWorld[scene.Index]}), "
                     + $"{e.Tris.Length / 3} triangle(s), box {e.Min}..{e.Max}, payload {e.Payload:x}");
    }

    private static ExtenderLog? _log;
    private static int _saidCreate;
    private static long _created, _asked, _askedWithBit, _hits;

    /// <summary>The period clause: whether prisms exist, whether casts ask for them, whether any is hit.</summary>
    internal static string Stats() => _created == 0 && _asked == 0 ? ""
        : $"[triggers] live {_live.Count}, created {_created}, world casts {_asked} ({_askedWithBit} with the trigger bit), hits {_hits}, "
        + $"folds with a lit trigger {TriggerHighlight.Enrolled}, highlight draws {TriggerHighlight.Drawn}";

    private static void Remove(nint t)
    {
        for (int i = 0; i < _live.Count; i++)
            if (_live[i].Ptr == t) { _live.RemoveAt(i); Sync(); return; }
    }

    private static void Sync()
    {
        if (_ptrs.Length < _live.Count) _ptrs = new nint[_live.Count * 2];
        for (int i = 0; i < _live.Count; i++) _ptrs[i] = _live[i].Ptr;
    }

    /// <summary>Off-device: register a prism directly, for the self-check.</summary>
    internal static void AddForCheck(Vector3[] tris, DbId scene, long payload)
    {
        var e = new Entry { Ptr = -1 - _live.Count, Scene = scene, Payload = payload, Tris = tris };
        e.Min = new Vector3(float.MaxValue);
        e.Max = new Vector3(float.MinValue);
        foreach (Vector3 v in tris) { e.Min = Vector3.Min(e.Min, v); e.Max = Vector3.Max(e.Max, v); }
        _live.Add(e);
        Sync();
    }

    internal static void ClearForCheck() { _live.Clear(); Sync(); }

    /// <summary>A destroyed scene's prisms leave with it: a release the hooks never saw would otherwise leave a
    /// freed pointer for <see cref="TriggerHighlight.Enroll"/> to read at the next frame.</summary>
    internal static void SceneRevoke(DbId scene)
    {
        int before = _live.Count;
        _live.RemoveAll(e => e.Scene == scene);
        if (_live.Count == before) return;
        Sync();
        _log?.Info($"DbTriggers: {before - _live.Count} prism(s) still listed when scene {scene.Value:x} was destroyed -- dropped with it, their release was never seen");
    }

    /// <summary><c>PartTrigger::HitCheckGeom</c> @0x4d9cb0 over every live prism of the cast's scene: gated on the
    /// trigger surface bit, the segment against the box and then each triangle, either face; a hit writes the
    /// point, the trigger surface and the payload, and shortens the segment like any static part.</summary>
    internal static void Cast(in HitQuery q, ref Vector3 to, ref HitResult r)
    {
        _asked++;
        if ((q.SurfaceMask >> HitQuery.TriggerSurface & 1) == 0) return;
        _askedWithBit++;
        foreach (Entry e in _live)
        {
            if (e.Scene != q.Scene || !HitTest.BoxHit(q.From, to, e.Min, e.Max)) continue;
            for (int i = 0; i + 2 < e.Tris.Length; i += 3)
            {
                if (!Segment(q.From, to, e.Tris[i], e.Tris[i + 1], e.Tris[i + 2], out Vector3 p)) continue;
                to = p;
                _hits++;
                r.Kind = HitKind.Static;
                r.Obj = default;
                r.Part = -1;
                r.Face = -1;
                r.Surface = HitQuery.TriggerSurface;
                r.Trigger = e.Payload;
                r.LocalPoint = p;
                r.Normal = Vector3.Zero;
            }
        }
    }

    /// <summary>Does the segment cross the triangle (either face)? Moller-Trumbore, bounded to the segment.</summary>
    internal static bool Segment(Vector3 a, Vector3 b, Vector3 v0, Vector3 v1, Vector3 v2, out Vector3 hit)
    {
        hit = default;
        Vector3 d = b - a, e1 = v1 - v0, e2 = v2 - v0;
        Vector3 pv = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, pv);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        Vector3 tv = a - v0;
        float u = Vector3.Dot(tv, pv) * inv;
        if (u < -HitTest.EdgeEps || u > 1f + HitTest.EdgeEps) return false;
        Vector3 qv = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, qv) * inv;
        if (v < -HitTest.EdgeEps || u + v > 1f + HitTest.EdgeEps) return false;
        float t = Vector3.Dot(e2, qv) * inv;
        if (t < 0f || t > 1f) return false;
        hit = a + d * t;
        return true;
    }
}
