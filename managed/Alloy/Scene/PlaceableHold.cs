using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Keeps a dynamic placeable the server range-culled (<c>CNWSMessage::TestObjectVisible</c> drops one
/// past its <c>VisibleDistance</c> and sends a delete flagged "still in this area") on screen as a static one
/// is, and as dead to the mouse as vanilla's faded-out state leaves it: the fade is landed at once, the expiry
/// cleared so the gob stays and the object is never deleted, and Alloy draws that gob at alpha 1.</summary>
internal static unsafe class PlaceableHold
{
    /// <summary>The hold the delete handler passes for a placeable still in the area: Linux
    /// <c>-(flag == 0) &amp; 45000</c>, Windows <c>45000, or 0 if flag</c>. Vanilla writes it only beside a set
    /// expiry, so "expiry clear, hold 45000" is this class's mark and nobody else's.</summary>
    internal const uint OutOfRangeHoldMs = 45000;
    /// <summary>Metres inside which a delete is not a range cull: the server's default <c>VisibleDistance</c> is 45
    /// (the <c>CNWSObject</c> ctor's 0x42340000) less 5 m of lag. Nearer, it is a script hide or a short custom
    /// distance, and fades as vanilla does.</summary>
    private const float KeepBeyond = 40f;
    /// <summary>How long the player may stand inside <see cref="KeepBeyond"/> of a held placeable before its
    /// missing re-add counts as the server saying it is gone.</summary>
    private const float RetireSeconds = 3f;
    /// <summary>ponytail: a fixed table; past it a placeable fades as vanilla does, counted and logged once.
    /// Grow it if that line ever shows up.</summary>
    private const int MaxHeld = 4096;
    private const byte TypePlaceable = 9;
    /// <summary>Sanity ceiling on the area's object list, as <see cref="DoorFade"/> has on the gob list: a list
    /// this long is a bad read, not a busy area, and the scan sits out.</summary>
    private const int MaxObjects = 1 << 16;
    /// <summary><see cref="EngineLayout.ObjVisible"/>'s values: landed out, landed in, and fading out
    /// (<c>FadeObject</c> also writes 2, fading in).</summary>
    private const byte FadedOut = 0, FadedIn = 1, FadingOut = 3;

    /// <summary>Held server object ids, which <c>CGameObjectArray::AddInternalObject</c> allocates
    /// monotonically, so one is never handed to another object in a session.</summary>
    private static readonly uint[] _id = new uint[MaxHeld];
    private static readonly uint[] _area = new uint[MaxHeld];
    private static readonly long[] _nearSince = new long[MaxHeld];
    /// <summary>The server re-added it and its fade-in has not landed yet: the mark still covers the draw.</summary>
    private static readonly bool[] _returning = new bool[MaxHeld];
    private static int _n;
    private static readonly long RetireTicks = (long)(RetireSeconds * Stopwatch.Frequency);
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private static long _overflow;
    private static bool _cappedLogged;
    private static ExtenderLog? _log;
    /// <summary>The self-check passed and the delete hook is in. Off, nothing is held and every placeable fades as
    /// vanilla does.</summary>
    private static bool _armed, _checked;
    /// <summary>A placeable delete arrived since the last scan: the only frames a new range cull can appear on.</summary>
    private static bool _deleteSeen;
    private static PlaceableUpdateDeleteFn _origDelete;

    /// <summary>Is this gob (a seam shell carries the gob's external words) a held placeable's? Its alpha is the
    /// vanilla fade's 0 and the frame draws 1. Once per fade step: one count test while nothing is held.</summary>
    internal static bool Held(IntPtr gob) => _n != 0 && gob != IntPtr.Zero && Marked(gob);

    private static bool Marked(IntPtr gob)
    {
        var g = new Gob(gob);
        if (g.ExternalType != TypePlaceable) return false;
        var obj = new CNWCObject(g.ExternalData);
        return !obj.IsNull && obj.FadeExpire == 0 && obj.FadeHoldMs == OutOfRangeHoldMs;
    }

    /// <summary>Once per world frame, ahead of the frame anchor: after this frame's network messages and object
    /// AI (<c>CClientExoAppInternal::MainLoop</c> runs both before <c>UpdateAndRender</c>) and before anything
    /// draws, so a delete or re-add is answered in the frame it arrived. Allocation-free.</summary>
    internal static void Tick()
    {
        if (!_armed || _appMgr == IntPtr.Zero) return;
        IntPtr mgr = *(IntPtr*)_appMgr;
        IntPtr app = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
        if (app == IntPtr.Zero) return;
        var ca = new CClientExoApp(app);
        var module = new CNWCModule(EngineFn.GetModule(app));
        CNWCArea area = module.IsNull ? default : module.CurrentArea;
        if (area.IsNull || ca.Internal.IsNull) return;
        uint areaId = area.Id;
        CNWCObject me = Object(ca, ca.Internal.PlayerCreatureId, 0);
        long now = Stopwatch.GetTimestamp();

        for (int i = _n - 1; i >= 0; i--)
        {
            if (Step(i, ca, areaId, me, now)) continue;
            _n--;
            _id[i] = _id[_n]; _area[i] = _area[_n]; _nearSince[i] = _nearSince[_n]; _returning[i] = _returning[_n];
        }
        // After the steps: an entry they just re-held has its expiry cleared, so the scan cannot list it twice.
        // Kept armed without a player: the vanilla fade lands (and detaches) 2 s after the delete, not sooner.
        if (_deleteSeen && !me.IsNull) { _deleteSeen = false; Discover(ca, area, areaId, me.Position); }
    }

    /// <summary>One held placeable's frame. False = drop the entry, having left the object in vanilla's hands.</summary>
    private static bool Step(int i, CClientExoApp ca, uint areaId, CNWCObject me, long now)
    {
        CNWCObject obj = Object(ca, _id[i], TypePlaceable);
        if (obj.IsNull) return false;
        if (_area[i] != areaId) { Let(obj); return false; }
        if (obj.Retired == 0)
        {
            if (!_returning[i])
            {
                if (obj.FadeTarget != FadedIn || obj.Visible == FadedIn) { Let(obj); return false; }
                // The re-add's SetDesiredFadeState wrote the hold to 0 and SetFadeState(0) put the alpha there:
                // FadeObject lands a fade-in from 1 on its next step, and the mark covers this frame's draw.
                obj.FadeAlpha = 1f;
                obj.FadeHoldMs = OutOfRangeHoldMs;
                _returning[i] = true;
                return true;
            }
            if (obj.Visible != FadedIn && obj.FadeTarget == FadedIn) return true;
            Let(obj);
            return false;
        }
        _returning[i] = false;
        if (obj.FadeExpire != 0)
        {
            // Deleted again (it was returning): re-held only if this delete is a range cull too.
            if (me.IsNull || !Holdable(obj, me.Position)) return false;
            Hold(obj);
        }
        if (me.IsNull) return true;
        if (Dist2D(obj.Position, me.Position) >= KeepBeyond) { _nearSince[i] = 0; return true; }
        if (_nearSince[i] == 0) { _nearSince[i] = now; return true; }
        if (now - _nearSince[i] < RetireTicks) return true;
        Let(obj);
        return false;
    }

    /// <summary>Every placeable in the area the server has just range-culled: cleared of its expiry before the fade
    /// lands, which is what keeps its gob in the scene.</summary>
    private static void Discover(CClientExoApp ca, CNWCArea area, uint areaId, Vector3 player)
    {
        uint* ids = (uint*)area.ObjectIds;
        int n = area.ObjectCount;
        if (ids == null || n <= 0) return;
        if (n > MaxObjects)
        {
            if (!_cappedLogged)
            {
                _cappedLogged = true;
                _log?.Always($"PlaceableHold: the area's object list read {n} entries (cap {MaxObjects}) -- not "
                           + "sane, so out-of-range placeables fade as vanilla does while it reads that");
            }
            return;
        }
        for (int k = 0; k < n; k++)
        {
            CGameObject go = ca.GetGameObject(ids[k]);
            if (go.IsNull || go.Type != TypePlaceable) continue;
            CNWCObject obj = go.AsNWCObject();
            if (obj.IsNull || !Holdable(obj, player)) continue;
            if (_n == MaxHeld)
            {
                if (_overflow++ == 0)
                    _log?.Always($"PlaceableHold: {MaxHeld} placeables already held -- every further one out of "
                               + "range fades and is deleted as vanilla does");
                continue;
            }
            Hold(obj);
            _id[_n] = ids[k]; _area[_n] = areaId; _nearSince[_n] = 0; _returning[_n] = false;
            _n++;
        }
    }

    private static bool Holdable(CNWCObject obj, Vector3 player)
        => Holdable(obj.Retired, obj.FadeExpire, obj.FadeHoldMs, obj.Visible, Dist2D(obj.Position, player));

    /// <summary>Is this the delete vanilla gives a placeable that left the player's range? Retired with the
    /// in-area hold, its fade not yet landed (landing detaches the gob), and far enough not to be a script hide.</summary>
    private static bool Holdable(int retired, int expire, uint holdMs, byte visible, float dist)
        => retired == 1 && expire != 0 && holdMs == OutOfRangeHoldMs && visible != FadedOut && dist >= KeepBeyond;

    /// <summary>Back to vanilla. Still retired: fading out with an expiry and no hold, so <c>FadeObject</c> lands
    /// it, detaches its gob and deletes it on the step after -- the order a destroyed placeable takes. Re-added:
    /// just the mark off.</summary>
    private static void Let(CNWCObject obj)
    {
        if (obj.Retired == 1)
        {
            obj.Visible = FadingOut;
            obj.FadeExpire = 1;
            obj.FadeHoldMs = 0;
        }
        else if (obj.FadeExpire == 0 && obj.FadeHoldMs == OutOfRangeHoldMs) obj.FadeHoldMs = 0;
    }

    /// <summary>Clear the expiry before the fade lands (landing with it set detaches the gob), and land the fade
    /// on the next AI step: alpha 0, state 0. <c>Scene::HitCheckGob</c> skips a gob at alpha 0 and
    /// <c>CNWCArea::HiliteAreaObjects</c> an object at fade state 0, so it is unpickable now, not in 2 s.</summary>
    private static void Hold(CNWCObject obj)
    {
        obj.FadeExpire = 0;
        obj.FadeAlpha = 0f;
    }

    /// <summary>The object behind <paramref name="id"/>, or null; <paramref name="type"/> 0 takes any type.</summary>
    private static CNWCObject Object(CClientExoApp ca, uint id, byte type)
    {
        CGameObject go = ca.GetGameObject(id);
        return go.IsNull || (type != 0 && go.Type != type) ? default : go.AsNWCObject();
    }

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _origDelete = hooks.InstallPlaceableUpdateDelete(&OnDelete);
        _armed = _checked && _origDelete != null;
        if (_origDelete == null)
            log.Always("PlaceableHold: HandleServerToPlayerPlaceableUpdate_Delete did not resolve -- out-of-range "
                     + "placeables fade and are deleted as vanilla does");
    }

    /// <summary>Cold (one network message). Only flags the scan: the object id is parsed inside.</summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static int OnDelete(IntPtr msg)
    {
        int r = _origDelete(msg);
        _deleteSeen = true;
        return r;
    }

    private static float Dist2D(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

    /// <summary>Off-device: the hold decision, and the draw side read through the same views a live gob is.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        _log = log;
        var check = new Checker(log);
        float far = KeepBeyond + 1f;
        check.Expect(Holdable(1, 1, OutOfRangeHoldMs, FadingOut, far), "a range cull still fading out is held");
        check.Expect(!Holdable(1, 1, 0, FadingOut, far), "a destroyed placeable (no hold) is not");
        check.Expect(!Holdable(1, 1, OutOfRangeHoldMs, FadedOut, far), "nor one whose fade landed: its gob is detached");
        check.Expect(!Holdable(1, 1, OutOfRangeHoldMs, FadingOut, KeepBeyond - 1f), "nor a delete inside the range");
        check.Expect(!Holdable(1, 0, OutOfRangeHoldMs, FadingOut, far), "nor one already held");
        check.Expect(!Holdable(0, 1, OutOfRangeHoldMs, FadingOut, far), "nor a live object");

        byte* gob = stackalloc byte[0x500];
        byte* obj = stackalloc byte[0x200];
        NativeMemory.Clear(gob, 0x500);
        NativeMemory.Clear(obj, 0x200);
        var gv = new Gob((IntPtr)gob);
        var ov = new CNWCObject((IntPtr)obj);
        gv.ExternalType = TypePlaceable;
        gv.ExternalData = (IntPtr)obj;
        ov.FadeExpire = 1;
        ov.FadeHoldMs = OutOfRangeHoldMs;
        int saved = _n;
        _n = 1;
        check.Expect(!Held(IntPtr.Zero), "a gobless call is not held, and is not read");
        check.Expect(!Held((IntPtr)gob), "vanilla's fade draws as it is until the hold takes it");
        ov.FadeExpire = 0;
        check.Expect(Held((IntPtr)gob), "a held placeable draws opaque");
        gv.ExternalType = 5;
        check.Expect(!Held((IntPtr)gob), "the mark counts on a placeable's gob only");
        gv.ExternalType = TypePlaceable;
        _n = 0;
        check.Expect(!Held((IntPtr)gob), "nothing is held while the table is empty");
        _n = saved;
        _checked = check.Ok;
        log.Info(check.Ok ? "PlaceableHold.SelfCheck: passed"
                          : "PlaceableHold.SelfCheck: FAILED -- out-of-range placeables fade as vanilla does");
        return check.Ok;
    }
}
