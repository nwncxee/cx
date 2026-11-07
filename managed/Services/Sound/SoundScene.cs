using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Alloy;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Sound;

/// <summary>The area's static geometry as Steam Audio's scene -- what occlusion, transmission, reflections and pathing
/// trace against; it is Alloy's own (<see cref="AlloyRenderer.GatherStatic"/>), so with Alloy off those four stand down.</summary>
/// <remarks>An area change clears the scene at once (the last area's walls must not muffle this one); the new one is
/// sent once the static set has held still for <see cref="SettleSeconds"/>, and again if it grows. Render thread.</remarks>
internal static unsafe class SoundScene
{
    /// <summary>The static set must hold this long before it is taken as the area's.</summary>
    private const double SettleSeconds = 1.0;
    /// <summary>A settled set that later grows is re-sent at most this often, and only when it grew by
    /// <see cref="ResendGrowth"/>: a 438k-triangle area takes 0.2-0.8 s to gather on the render thread.</summary>
    private const double ResendSeconds = 10.0;
    private const float ResendGrowth = 1.1f;
    /// <summary>Triangles under this many square metres are dropped: prop detail no sound wave notices, and most of
    /// an area's triangle count.</summary>
    private const float MinTriArea = 0.25f;
    /// <summary>Only live for a send: the native scene keeps its own copy, and an area's soup is hundreds of megabytes.</summary>
    private static readonly List<float> _verts = new();
    private static readonly List<int> _tris = new();
    /// <summary>Seconds into an area with nothing to send before the stand-down is said.</summary>
    private const double EmptySeconds = 15.0;
    /// <summary>Frames between static-set counts: a linear scan of the slot table.</summary>
    private const int Every = 15;

    private static ExtenderLog? _log;
    private static IntPtr _appMgr;
    private static GetModuleFn _getModule;
    private static IntPtr _area;
    private static uint _areaId;
    private static int _frame, _count, _sent;
    private static long _changedAt, _sentAt, _enteredAt;
    private static bool _saidEmpty;
    /// <summary>The master toggle was off at the last tick: the native side drops the scene with it, so the area's
    /// geometry is sent again when it comes back rather than left traced against nothing.</summary>
    private static bool _wasOff;

    internal static void Arm(ExtenderLog log)
    {
        _log = log;
        _appMgr = Native.Sym(SymbolMap.AppManager);
        _getModule = EngineFn.GetModule;
    }

    private static double Since(long t) => (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;

    internal static void Tick()
    {
        if (!SoundKnobs.Enabled.V) { _wasOff = true; return; }
        if (_wasOff) { _wasOff = false; _sent = 0; _changedAt = Stopwatch.GetTimestamp(); }
        if (++_frame % Every != 0) return;
        CNWCArea area = CurrentArea();
        if (area.Ptr != _area || (!area.IsNull && area.Id != _areaId))
        {
            _area = area.Ptr;
            _areaId = area.IsNull ? 0 : area.Id;
            _count = _sent = 0;
            _changedAt = _enteredAt = Stopwatch.GetTimestamp();
            _saidEmpty = false;
            SoundCore.cxa_scene(null, 0, null, 0, 1, 0);
        }
        if (area.IsNull) return;
        int n = AlloyRenderer.StaticSlotCount();
        if (n != _count) { _count = n; _changedAt = Stopwatch.GetTimestamp(); }
        if (n == 0)
        {
            if (!_saidEmpty && Since(_enteredAt) > EmptySeconds)
            {
                _saidEmpty = true;
                Notice.Show(_log, "CX sound: this area has no Alloy geometry to trace (is alloy.enabled off?) -- walls, "
                                + "reverb and sound around corners are off here; direction and distance still work");
            }
            return;
        }
        if (Since(_changedAt) < SettleSeconds) return;
        if (_sent != 0 && (n < _sent * ResendGrowth || Since(_sentAt) < ResendSeconds)) return;
        Send(n);
    }

    private static void Send(int slots)
    {
        _verts.Clear();
        _tris.Clear();
        long t0 = Stopwatch.GetTimestamp();
        int count = AlloyRenderer.GatherStatic(_verts, _tris, MinTriArea);
        bool first = _sent == 0;
        int mat = Material(CurrentArea());
        fixed (float* pv = CollectionsMarshal.AsSpan(_verts))
        fixed (int* pt = CollectionsMarshal.AsSpan(_tris))
            SoundCore.cxa_scene(pv, _verts.Count / 3, pt, count, first ? 1 : 0, mat);
        _sent = slots;
        _sentAt = Stopwatch.GetTimestamp();
        _log?.Info($"sound: acoustic scene {(first ? "sent" : "re-sent")} -- {count} triangles over {MinTriArea} m², "
                 + $"{_verts.Count / 3} vertices from {slots} static slots, {MaterialNames[mat]}, gathered in {Since(t0) * 1000:F1} ms");
        _verts.Clear(); _verts.TrimExcess();
        _tris.Clear(); _tris.TrimExcess();
    }

    /// <summary>ARE Flags bits, as the engine's own script commands read them (<c>GetIsAreaAboveGround</c> =
    /// !bit 1, <c>GetIsAreaNatural</c> = bit 2, <c>GetIsAreaInterior</c> = <c>flags &amp; 3</c>).</summary>
    private const uint FlagInterior = 1, FlagUnderground = 2, FlagNatural = 4;
    /// <summary>cxa_scene's material indices (native/cxaudio.cpp <c>g_acMats</c>), for the log.</summary>
    private static readonly string[] MaterialNames = { "generic", "masonry", "cave rock" };

    /// <summary>The area's one acoustic material, from its flags: a natural enclosed area is a cave (bare rock,
    /// a long tail), an artificial underground one masonry (a dungeon, a sewer), anything else generic.
    /// ponytail: one per area; per-surface materials want a texture or walkmesh-material join.</summary>
    internal static int Material(CNWCArea area)
    {
        if (area.IsNull) return 0;
        uint f = area.Flags;
        if ((f & FlagNatural) != 0) return (f & (FlagInterior | FlagUnderground)) != 0 ? 2 : 0;
        return (f & FlagUnderground) != 0 ? 1 : 0;
    }

    /// <summary>The player's current area, or none (menus, loading).</summary>
    internal static CNWCArea CurrentArea()
    {
        if (_appMgr == IntPtr.Zero || _getModule == null) return default;
        IntPtr mgr = *(IntPtr*)_appMgr;
        if (mgr == IntPtr.Zero) return default;
        IntPtr app = *(IntPtr*)mgr;
        if (app == IntPtr.Zero) return default;
        var module = new CNWCModule(_getModule(app));
        return module.IsNull ? default : module.CurrentArea;
    }
}
