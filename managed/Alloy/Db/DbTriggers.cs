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
/// BSP, so this keeps the live ones per scene as COPIES the pick (<see cref="Cast"/>) and <see cref="TriggerHighlight"/>
/// read: past creation nothing reads the engine object, so a free no hook saw leaves a stale entry. Cold.</summary>
internal static unsafe class DbTriggers
{
    /// <summary>One prism: its triangles and outline as created, and the highlight state the two setter hooks mirror.</summary>
    internal sealed class Entry
    {
        public nint Ptr;
        public DbId Scene;
        public Vector3[] Tris = [];
        public Vector3 Min, Max;
        public long Payload;
        public Vector3[] Outline = [];
        public bool Highlighted, Half;
        public float Height;
        public Vector4 Colour;
    }

    private static readonly List<Entry> _live = new();
    /// <summary>The five chained hooks' next-subscriber slots, filled at commit.</summary>
    private sealed class HookState
    {
        public AurPartTriggerCreateFn* Create;
        public AurPartTriggerDeleteFn* Delete;
        public AurPartTriggerReleaseMemoryFn* Release;
        public AurPartTriggerSetHighlightFn* SetHighlight;
        public AurPartTriggerSetHighlightParamsFn* SetParams;
    }

    private static readonly HookState _hooks = new();

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!Renderer.On) return;
        _log = log;
        _hooks.Create = hooks.ChainAurPartTriggerCreate(&OnCreate, 0);
        _hooks.Delete = hooks.ChainAurPartTriggerDelete(&OnDelete, 0);
        _hooks.Release = hooks.ChainAurPartTriggerReleaseMemory(&OnRelease, 0);
        _hooks.SetHighlight = hooks.ChainAurPartTriggerSetHighlight(&OnSetHighlight, 0);
        _hooks.SetParams = hooks.ChainAurPartTriggerSetHighlightParams(&OnSetParams, 0);
        if (_hooks.Create == null || _hooks.Delete == null || _hooks.Release == null)
            log.Always("DbTriggers: a trigger hook did not chain -- area transitions can NOT be hovered in DB mode");
        else if (_hooks.SetHighlight == null || _hooks.SetParams == null)
            log.Always("DbTriggers: a trigger highlight setter did not chain -- area transitions are pickable but NEVER highlight in DB mode");
        else
            log.Info("DbTriggers: area transitions are pickable and highlight on hover");
    }

    /// <summary>How many prisms are listed, and the one at <paramref name="i"/>, for the highlight pass. Game thread.</summary>
    internal static int Count => _live.Count;
    /// <inheritdoc cref="Count"/>
    internal static Entry At(int i) => _live[i];

    [UnmanagedCallersOnly]
    private static nint OnCreate(nint scene, nint points, int count, int snap, nint colour, nint data)
    {
        nint t = _hooks.Create != null && *_hooks.Create != null ? (*_hooks.Create)(scene, points, count, snap, colour, data) : 0;
        if (t != 0) Add(t, Facade.SceneId(scene));
        return t;
    }

    [UnmanagedCallersOnly]
    private static void OnDelete(nint scene, nint t)
    {
        Remove(t);
        if (_hooks.Delete != null && *_hooks.Delete != null) (*_hooks.Delete)(scene, t);
    }

    [UnmanagedCallersOnly]
    private static void OnRelease(nint t)
    {
        Remove(t);
        if (_hooks.Release != null && *_hooks.Release != null) (*_hooks.Release)(t);
    }

    [UnmanagedCallersOnly]
    private static void OnSetHighlight(nint t, byte on, float height, byte half)
    {
        if (_hooks.SetHighlight != null && *_hooks.SetHighlight != null) (*_hooks.SetHighlight)(t, on, height, half);
        if (Find(t) is not Entry e) return;
        e.Highlighted = on != 0;
        e.Half = half != 0;
        e.Height = MathF.Abs(height);
    }

    [UnmanagedCallersOnly]
    private static void OnSetParams(nint t, Vector3* rgb, float alpha, byte flag, float value)
    {
        if (_hooks.SetParams != null && *_hooks.SetParams != null) (*_hooks.SetParams)(t, rgb, alpha, flag, value);
        if (Find(t) is Entry e && rgb != null) e.Colour = new Vector4(*rgb, alpha);
    }

    private static Entry? Find(nint t)
    {
        for (int i = 0; i < _live.Count; i++)
            if (_live[i].Ptr == t) return _live[i];
        return null;
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
        var pts = (Vector3*)trig.Points;
        int np = pts == null ? 0 : Math.Max(trig.PointCount, 0);
        e.Outline = new Vector3[np];
        for (int i = 0; i < np; i++) e.Outline[i] = pts[i];
        e.Highlighted = trig.Highlighted != 0;
        e.Half = trig.Half != 0;
        e.Height = MathF.Abs(trig.Height);
        e.Colour = new Vector4(trig.HighlightColour[0], trig.HighlightColour[1], trig.HighlightColour[2], trig.HighlightColour[3]);
        _live.Add(e);
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
            if (_live[i].Ptr == t) { _live.RemoveAt(i); return; }
    }

    /// <summary>Off-device: register a prism directly, for the self-check.</summary>
    internal static void AddForCheck(Vector3[] tris, DbId scene, long payload)
    {
        var e = new Entry { Ptr = -1 - _live.Count, Scene = scene, Payload = payload, Tris = tris };
        e.Min = new Vector3(float.MaxValue);
        e.Max = new Vector3(float.MinValue);
        foreach (Vector3 v in tris) { e.Min = Vector3.Min(e.Min, v); e.Max = Vector3.Max(e.Max, v); }
        _live.Add(e);
    }

    internal static void ClearForCheck() => _live.Clear();

    /// <summary>A destroyed scene's prisms leave with it, so a release the hooks never saw does not leave a stale
    /// entry pickable or lit for the rest of the session.</summary>
    internal static void SceneRevoke(DbId scene)
    {
        int before = _live.Count;
        _live.RemoveAll(e => e.Scene == scene);
        if (_live.Count == before) return;
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
