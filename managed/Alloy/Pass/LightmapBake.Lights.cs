using System;
using System.Numerics;
using System.Threading;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;
using CX.Alloy.Db;

namespace CX.Alloy;

/// <summary>The static-light side of the bake: the three-branch rule classified once per light identity, the
/// per-frame static snapshot the bake reads, the static-set fingerprint that is the one re-key edge, and the
/// <c>CAurScene::ComputeStaticLighting</c> hook that voids the classification. Every reader here runs on the
/// light walk (the shadow bracket) and allocates nothing.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>The static set is ONE walk: the first walk after <c>CNWCArea::LoadArea</c> returned non-zero
    /// (<see cref="_loadArmed"/>), rather than a second of frame counting for an answer the load's own message
    /// already has. Closed, the set is final for the area: a light that appears later is dynamic, whatever its
    /// shape, and a server tile change reopens it (<see cref="OnTileChanged"/>).</summary>
    private static bool _closed, _reopen;
    private static int _walks;
    /// <summary>The area under the walk changed since the last close: the pages are the previous area's and
    /// rewind at this close (an in-module area change is no world edge -- run 30 kept 12 pages of the last
    /// area's rects resident under the next one and halved its density for them).</summary>
    private static bool _areaChanged;

    /// <summary><c>CNWCArea::LoadArea</c> @0x70e010 has RETURNED non-zero, so every light of this area is in
    /// the master list -- its tile loop, <c>SetSourceLightColor</c>'s gobs and both placeable blocks are all
    /// synchronous. Lowered at the call's ENTRY, because a walk that runs inside <c>LoadArea</c> (its own
    /// <c>UpdateScreen</c> ticks) sees a half-spawned area. It stays raised for the whole area.</summary>
    private static volatile bool _loadArmed;
    /// <summary>...and its refusal: <c>LoadArea</c> returned 0. Its error paths destroy the tile array, so
    /// there is no set to close and none is closed on the old walk-count path either -- the bake stays
    /// un-armed for this area (<c>SetHash</c> 0) and the hold has nothing to wait for.</summary>
    private static volatile bool _loadRefused;
    /// <summary>How many area loads the engine has refused this session; <see cref="Describe"/>'s honesty.</summary>
    internal static int LoadRefusals;
    /// <summary>Walks the close may spend waiting for a static light's gob to blend its animation in
    /// (<see cref="Settled"/>). <c>Gob::Animate</c> advances a weight by <c>dt/blendTime</c> from 0, so a
    /// settle is one blend time, about a dozen walks. Past this the set closes WITHOUT the still-blending
    /// lights and says so loudly: a blend that never finishes would hold the loading screen to its cap.</summary>
    private const int BlendCeiling = 60;
    private static int _regCountSeen = -1;
    private static IntPtr _clsArea;

    /// <summary>The area's static set over the UNCUT master list, accumulated across walks (the fingerprint
    /// must not fold the culled set): position, LIT radius, the same colour the light SSBO carries.</summary>
    private static Vector4[] _stPosR = new Vector4[256];
    private static Vector3[] _stCol = new Vector3[256];
    private static int _stN;
    /// <summary>The walk's census: static / dynamic lights, per frame.</summary>
    internal static int CensusStatic, CensusDynamic;
    /// <summary>Static lights this walk refused because their gob is still blending: the close waits on it.</summary>
    internal static int CensusUnsettled;
    /// <summary>The census of the closing walk: after the close the walk counts only static / dynamic.</summary>
    private static int _closeStatic, _closeDynamic;

    /// <summary>The closed static set: copied off the snapshot on the edge, read by the bake and the keys.</summary>
    private static Vector4[] _setPosR = new Vector4[256];
    private static Vector3[] _setCol = new Vector3[256];
    private static int _setN;
    /// <summary>The closed set's identity, 0 = none. Every edge that moves the set assigns it AFTER the arrays,
    /// so the setter is where a live light's cached set index goes stale (<see cref="SetIndexOf"/>).</summary>
    internal static ulong SetHash { get => _setHash; private set { _setHash = value; _setIdxPtr.Clear(); } }
    private static ulong _setHash;

    /// <summary>The static lights a baked surface holds a mover share for -- page C's four bytes, one per
    /// light of the surface's own pick (<see cref="PickLights"/>). Past them a light falls to the estimate.</summary>
    internal const int Channels = 4;
    /// <summary>A live light's index in <see cref="_setPosR"/> by object, -1 = none; resolved once per set.</summary>
    private static readonly Dictionary<IntPtr, int> _setIdxPtr = new(512);

    /// <summary>The set index of a static light the walk just recorded -- the set entry within
    /// <see cref="Near"/> of it, cached by object; -1 before the set closes or when it has no entry. The index
    /// is what a surface's pick names, so the lit stage can tell whose share a byte of page C is. Hot (every
    /// walk), allocation-free once each light is cached.</summary>
    private static int SetIndexOf(IntPtr light, Vector3 pos, float adjR)
    {
        if (_setHash == 0) return -1;
        if (_setIdxPtr.TryGetValue(light, out int c)) return c;
        c = -1;
        for (int l = 0; l < _setN && c < 0; l++)
            if (Near(_setPosR[l], pos, adjR)) c = l;
        _setIdxPtr[light] = c;
        return c;
    }

    /// <summary>The <see cref="Channels"/> static lights that light the sphere (<paramref name="c"/>,
    /// <paramref name="r"/>) most -- estimated luminance at the sphere's nearest point through the loop's own
    /// curve -- as set indices, strongest first. Ties break on the light's position, never its index, so the
    /// rect's key names the same pick in every session; channel k of its page C texels is pick k's share.</summary>
    private static void PickLights(Vector3 c, float r, Span<int> pick)
    {
        Span<float> best = stackalloc float[Channels];
        pick.Fill(-1); best.Fill(-1f);
        Vector2 at = AlloyLights.Atten;
        for (int l = 0; l < _setN; l++)
        {
            if (!Touches(l, c, r)) continue;
            Vector4 pr = _setPosR[l];
            float d = MathF.Max((new Vector3(pr.X, pr.Y, pr.Z) - c).Length() - r, 0f);
            float f2 = MathF.Min(d * d / MathF.Max(pr.W * pr.W, 1e-6f), 1f);
            Vector3 col = _setCol[l];
            float w = (1f - f2) / MathF.Max(at.X + at.Y * f2, 1e-6f) * (0.2126f * col.X + 0.7152f * col.Y + 0.0722f * col.Z);
            int k = Channels;
            while (k > 0 && (pick[k - 1] < 0 || Beats(w, l, best[k - 1], pick[k - 1]))) k--;
            if (k >= Channels) continue;
            for (int j = Channels - 1; j > k; j--) { pick[j] = pick[j - 1]; best[j] = best[j - 1]; }
            pick[k] = l; best[k] = w;
        }
    }

    /// <summary>Does light <paramref name="a"/> at weight <paramref name="wa"/> rank above <paramref name="b"/>?
    /// Weight first, then position -- a total order that does not depend on how the set is listed.</summary>
    private static bool Beats(float wa, int a, float wb, int b)
    {
        if (wa != wb) return wa > wb;
        Vector4 pa = _setPosR[a], pb = _setPosR[b];
        if (pa.X != pb.X) return pa.X < pb.X;
        if (pa.Y != pb.Y) return pa.Y < pb.Y;
        if (pa.Z != pb.Z) return pa.Z < pb.Z;
        return pa.W > pb.W;
    }
    /// <summary><c>ComputeStaticLighting</c> hook edges this period (the period line prints them).</summary>
    private static int _cslEdges;

    private const ulong FnvOffset = 14695981039346656037UL, FnvPrime = 1099511628211UL;
    private static ulong Mix(ulong h, long v) { h ^= (ulong)v; return h * FnvPrime; }
    private static long Q(float v, float step) => (long)MathF.Round(v / step);

    /// <summary>The static lights of this area by quantised position and reach, filled by the walks before the
    /// set closes and read-only after.</summary>
    private static readonly Dictionary<ulong, int> _seen = new(512);
    /// <summary>The same set by light object, so a light refused after the close can say whether it is a new
    /// object or one of the set that moved or changed reach (the drift diagnostic, 24 lines an area).</summary>
    private static readonly Dictionary<IntPtr, int> _seenPtr = new(512);

    /// <summary>An area edge: the next walk is a NEW area, whatever the area pointer says -- a server
    /// transition freed one CNWCArea and malloc handed the next one the same address, so a pointer compare saw
    /// nothing and the new area was baked against the old one's set. Raised by <c>LoadArea</c>'s entry, by the
    /// AreaLoaded send and again by <see cref="EndAreaEdge"/> after that send's <see cref="Revoke"/>.</summary>
    internal static void OnAreaLoaded() => _edgePending = true;
    private static volatile bool _edgePending;
    /// <summary><c>CNWCArea::SetTile</c>: one tile's model and lights re-spawn -- the set is walked again.</summary>
    internal static void OnTileChanged() => _reopen = true;

    /// <summary>Restart the per-frame snapshot: the area edge, the re-open, and the per-walk clear of the
    /// seen sets. Takes the area's IDENTITY and its placeable-light count as VALUES rather than a live
    /// <c>CNWCArea</c> -- the walk runs inside the per-view draw, which with <c>alloy.renderthread=1</c> is
    /// not the game thread. The placeable registry is not walked: the database hands over its own verdict.</summary>
    internal static void BeginWalkDb(nint areaPtr, int placeableLights)
    {
        CensusStatic = CensusDynamic = CensusUnsettled = 0;
        if (areaPtr != _clsArea || _edgePending) { _clsArea = areaPtr; _edgePending = false; _areaChanged = true; _regCountSeen = -1; Reopen(); }
        if (_reopen) { _reopen = false; Reopen(); }
        if (_closed) return;
        // open, every walk is the whole set again: the lights the engine re-sets after the edge must not leave
        // their first reading in the set beside their real one
        _seen.Clear(); _seenPtr.Clear(); _stN = 0;
        _walks++;
        _regCountSeen = placeableLights;
    }

    private static void Reopen() { _closed = false; _walks = 0; }

    /// <summary>Can a baked page exist in this mode at all? See the <c>Produces</c> test in
    /// <see cref="RecordDb"/>.</summary>
    // CX-120: the OFFER is the edge, not the rect, and that is not a looseness -- it is what breaks a
    // circle. A light only joins the static set once Produces says a page can exist; SetHash is that
    // set's hash; and Enrol -- the only thing that mints a rect -- refuses while SetHash is 0. Keyed on
    // the rect, the three wait on each other for ever and the bake never starts (measured: charts built,
    // no rect, no page). The offer is the first moment a page is possible, and the loading-screen hold
    // (#675) is what covers the gap between it and the first landed page.
    internal static bool Produces => _rectN > 0 || MeshChart.Offered > 0;

    /// <summary>One database light, and -- when static -- folded into the snapshot. Called for every light the
    /// walk admits, BEFORE its fade and cull, with the lightmap on or off (GI keys its cache on the same
    /// fingerprint). A light's identity here is its own database record, which unlike a <c>PartLight*</c> is
    /// not recycled by an allocator, and the static VERDICT is handed in, because the database knows it.</summary>
    internal static bool RecordDb(IntPtr id, Vector3 pos, float adjR, Vector3 color, bool ambient,
                                  bool isStatic, out int setIndex)
    {
        setIndex = -1;
        ulong key = Mix(Mix(Mix(Mix(FnvOffset, Q(pos.X, 0.05f)), Q(pos.Y, 0.05f)), Q(pos.Z, 0.05f)), Q(adjR, 0.05f));
        if (ambient) { CensusDynamic++; return false; }
        // CX-120: a STATIC light is lit from a baked page and from nowhere else -- the live loop passes over
        // it on purpose. Calling one static where no page can ever exist is therefore the same as switching
        // it off: nothing enrols, so no rect is ever minted (see LightmapBake.Hold's NoRectSource). Every
        // light stays dynamic until the arena ranges are charted, which is what keeps the tiles lit
        // meanwhile. Self-retiring: the first chart offered ends it.
        if (!Produces) { CensusDynamic++; return false; }
        // in the closed set = static, with the colour it was baked under (it is in every touching key);
        // anything else after the close is dynamic, whatever it is: a light that appears after load moved
        if (_seen.ContainsKey(key)) { CensusStatic++; setIndex = SetIndexOf(id, pos, adjR); return true; }
        if (_closed)
        {
            // the same record a hair off its close-time place (a bobbing lantern, a reach re-adjusted by the
            // simulation) is still the light the bake holds; a far move is a scripted move
            if (_seenPtr.TryGetValue(id, out int at) && Near(_stPosR[at], pos, adjR)) { CensusStatic++; setIndex = SetIndexOf(id, pos, adjR); return true; }
            CensusDynamic++;
            return false;
        }
        if (!isStatic) { CensusDynamic++; return false; }
        CensusStatic++;
        if (_stN == _stPosR.Length)
        {
            int n = _stN * 2;
            Array.Resize(ref _stPosR, n); Array.Resize(ref _stCol, n);
        }
        _seen[key] = _stN; _seenPtr[id] = _stN;
        _stPosR[_stN] = new Vector4(pos, adjR); _stCol[_stN++] = color;
        return true;
    }

    /// <summary>Within 1 u and 10% of reach of the set entry: the drift a static light is allowed after the close.</summary>
    private static bool Near(Vector4 was, Vector3 pos, float adjR)
        => (pos - new Vector3(was.X, was.Y, was.Z)).LengthSquared() < 1f && MathF.Abs(adjR - was.W) < 0.1f * was.W;

    /// <summary>The walk's tail: close the set on the first settled walk after <c>LoadArea</c>'s non-zero
    /// return, and every instance re-keys against it once. Closed, nothing here runs.</summary>
    internal static void EndWalk()
    {
        if (_closed) return;
        if (_clsArea == IntPtr.Zero) return;
        // the area's own message has not finished spawning it (or it failed): there is nothing to close over
        if (!_loadArmed) return;
        bool settled = CensusUnsettled == 0;
        if (!settled && _walks < BlendCeiling) return;
        // CX-120: and in DB mode, not before the charter has been handed something. A light's static verdict
        // asks Produces, Produces asks whether a chart has been OFFERED, and closing before the first offer
        // bins every light dynamic for the session (measured: "set closed at walk 1 ... dynamic=22,
        // static 0"). The offers come from the frame fold (MeshChart.DripDb), which runs while the area
        // loads, so this wait is short. Only while the charter can answer at all -- with no arena VAO and no
        // master knob there is nothing to wait for, which is also the off-device fixture's lightless area --
        // and under the same walk ceiling the settle wait uses.
        if (MeshChart.Armed && MeshChart.Offered == 0 && _walks < BlendCeiling) return;
        _closed = true;
        ulong acc = 0;
        for (int i = 0; i < _stN; i++) acc ^= SetTerm(_stPosR[i], _stCol[i]);
        ulong hash = _stN == 0 ? 0 : Mix(FnvOffset, (long)acc) | 1UL;   // 0 = nothing to bake: Enrol and Sweep stay refused
        // The registry clause is the count the fold read off the area, and it is not a check: the
        // classification it would be checked against is not asked at all -- the database supplies every
        // static verdict.
        string how = $"walk {_walks} past LoadArea's return, {_stN} lights "
                   + $"({_regCountSeen} placeable lights registered, class NOT asked), "
                   + $"{CensusDynamic} dynamic{(_stN == 0 ? " -- no static light, nothing to bake" : "")}";
        if (settled) _log?.Info($"LightmapBake: static set closed on final values -- {how}");
        else _log?.Always($"LightmapBake: static set closed at the {BlendCeiling}-walk CEILING with {CensusUnsettled} lights still blending an animation in -- "
                          + $"the set is closed WITHOUT them and they light the area dynamically; {how}");
        _closeStatic = CensusStatic; _closeDynamic = CensusDynamic;
        Arrived(WhyClose); StampClose();
        // CX-56: under an adopted area plan the close is a CHECK, not the edge that builds the set -- the tail
        // below would repack the whole area out from under the adoption on every first walk of an area.
        if (AreaConfirm(hash)) return;
        if (hash == SetHash && !_areaChanged) return;
        if (_setPosR.Length < _stN) { _setPosR = new Vector4[_stN]; _setCol = new Vector3[_stN]; }
        Array.Copy(_stPosR, _setPosR, _stN); Array.Copy(_stCol, _setCol, _stN);
        _setN = _stN;
        SetHash = hash;
        AbortBake();
        if (_areaChanged) { _areaChanged = false; _areaHalve = 0; _placedArea = 0; Repack(); }
        else if (hash == 0) Repack();
        else RekeyAll();
    }

    /// <summary>One light's term of <see cref="SetHash"/>: position and reach in 4 u bins, colour by hue in 0,1 steps.</summary>
    private static ulong SetTerm(Vector4 pr, Vector3 c)
    {
        float m = MathF.Max(MathF.Max(c.X, c.Y), MathF.Max(c.Z, 1e-3f));
        ulong h = FnvOffset;
        h = Mix(h, Q(pr.X, 4f)); h = Mix(h, Q(pr.Y, 4f)); h = Mix(h, Q(pr.Z, 4f)); h = Mix(h, Q(pr.W, 4f));
        h = Mix(h, Q(c.X / m, 0.1f)); h = Mix(h, Q(c.Y / m, 0.1f)); h = Mix(h, Q(c.Z / m, 0.1f));
        return h;
    }

    /// <summary>Why a saved set and the live one disagree, light by light: pairs within <see cref="Near"/> whose
    /// terms still differ (a bin edge or a colour), then the lights either side has alone. Cold: once per
    /// refusal or drift.</summary>
    private static string SetDiff(Vector4[] planPosR, Vector3[] planCol, int planN)
    {
        var liveUsed = new bool[_stN];
        var byIdx = new IntPtr[_stN];
        foreach (var kv in _seenPtr) if (kv.Value < _stN) byIdx[kv.Value] = kv.Key;
        var planOnly = new List<int>();
        int same = 0, binned = 0;
        var sb = new System.Text.StringBuilder();
        for (int p = 0; p < planN; p++)
        {
            int hit = -1;
            for (int l = 0; l < _stN && hit < 0; l++)
                if (!liveUsed[l] && Near(planPosR[p], new Vector3(_stPosR[l].X, _stPosR[l].Y, _stPosR[l].Z), _stPosR[l].W)) hit = l;
            if (hit < 0) { planOnly.Add(p); continue; }
            liveUsed[hit] = true;
            if (SetTerm(planPosR[p], planCol[p]) == SetTerm(_stPosR[hit], _stCol[hit])) { same++; continue; }
            if (binned++ < 6) sb.Append($"; paired but binned apart: plan {Fmt(planPosR[p], planCol[p])} vs live {Fmt(_stPosR[hit], _stCol[hit])} {Who(byIdx[hit])}");
        }
        for (int i = 0; i < planOnly.Count && i < 6; i++) sb.Append($"; plan only: {Fmt(planPosR[planOnly[i]], planCol[planOnly[i]])}");
        int liveOnly = 0;
        for (int l = 0; l < _stN; l++)
            if (!liveUsed[l] && liveOnly++ < 6) sb.Append($"; live only: {Fmt(_stPosR[l], _stCol[l])} {Who(byIdx[l])}");
        return $"{same} lights agree, {binned} pair up but hash apart, {planOnly.Count} plan-only, {liveOnly} live-only" + sb;

        static string Fmt(Vector4 pr, Vector3 c) => $"({pr.X:F2},{pr.Y:F2},{pr.Z:F2}) r {pr.W:F2} col ({c.X:F2},{c.Y:F2},{c.Z:F2})";
        static string Who(IntPtr light)
        {
            // a database light id, not a Part: there is nothing to dereference
            return light == IntPtr.Zero ? "'?'" : $"db light {light:x}";
        }
    }

    /// <summary>The per-instance half of the bake key: an order-independent xor over the static lights
    /// whose sphere touches the instance's bounds -- position and range at 5 cm, full colour at 1/255.
    /// Zero when nothing touches (a bake of black, still a bake).</summary>
    private static ulong TouchingLights(Vector3 c, float r)
    {
        ulong acc = 0;
        for (int i = 0; i < _setN; i++)
        {
            if (!Touches(i, c, r)) continue;
            Vector4 pr = _setPosR[i];
            Vector3 col = _setCol[i];
            ulong h = FnvOffset;
            h = Mix(h, Q(pr.X, 0.05f)); h = Mix(h, Q(pr.Y, 0.05f)); h = Mix(h, Q(pr.Z, 0.05f)); h = Mix(h, Q(pr.W, 0.05f));
            h = Mix(h, Q(col.X, 1f / 255f)); h = Mix(h, Q(col.Y, 1f / 255f)); h = Mix(h, Q(col.Z, 1f / 255f));
            acc ^= h;
        }
        return acc;
    }

    /// <summary>Does static light <paramref name="l"/> of the settled set reach the sphere?</summary>
    private static bool Touches(int l, Vector3 c, float r)
    {
        Vector4 pr = _setPosR[l];
        float dx = pr.X - c.X, dy = pr.Y - c.Y, dz = pr.Z - c.Z, rr = pr.W + r;
        return dx * dx + dy * dy + dz * dz <= rr * rr;
    }

    private static ComputeStaticLightingFn _origCsl;

    /// <summary>The edges the static set is built on: <c>LoadArea</c> (the one that CLOSES the set),
    /// <c>SetTile</c> (a tile re-spawns, the set is walked again), the AreaLoaded send (the hold) and the
    /// engine's own "the static lighting changed" signal (counted on the period line). All cold hooks; none
    /// allocates.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _loadAreaSlot = hooks.ChainAreaLoadArea(&OnLoadArea, EdgePriority);
        if (_loadAreaSlot == null)
        {
            // CX-45: our own mechanism is missing, so the pass sits out -- it must not fall back to a walk
            // count nobody would see, and a set that never closes is a hold that never releases.
            log.Always("LightmapBake: REFUSED -- CNWCArea::LoadArea did not resolve, so nothing can close the static set; the bake and its loading hold are off for this session and every surface keeps the real-time loop");
            _noLoadEdge = true;
            return;
        }
        _origCsl = hooks.InstallComputeStaticLighting(&OnComputeStaticLighting);
        _loadedSlot = hooks.ChainSendAreaLoaded(&OnSendAreaLoaded, EdgePriority);
        _setTileSlot = hooks.ChainAreaSetTile(&OnSetTile, EdgePriority);
        if (_loadedSlot == null) log.Always("LightmapBake: the AreaLoaded send did not resolve -- the set still closes at LoadArea, but there is no loading hold and the player is placed over an unbaked area");
        else InstallSurface(log);
    }

    /// <summary>Observers, bar the hold (<c>LightmapBake.Hold.cs</c>): they read nothing and call through, so any priority is fine.
    /// On <c>LoadArea</c> it puts this INSIDE <c>TexStream</c>'s own chain link, which times the whole call.</summary>
    private const int EdgePriority = 0;
    private static SendAreaLoadedFn* _loadedSlot;
    private static AreaSetTileFn* _setTileSlot;
    private static AreaLoadAreaFn* _loadAreaSlot;

    /// <summary>The area's own message, start to finish. Entry: the area is changing under the walk, and a
    /// walk that runs inside this call (its <c>UpdateScreen</c> ticks) would see a half-spawned one -- so the
    /// close is disarmed and the edge raised. Return: non-zero arms the close, zero refuses it (the error
    /// paths destroy the tile array) and is said out loud. Cold; the strings only run on a refusal.</summary>
    [UnmanagedCallersOnly]
    private static int OnLoadArea(IntPtr area, IntPtr camera, IntPtr loadscreen)
    {
        _loadArmed = false; _loadRefused = false; _edgePending = true;
        Interlocked.Exchange(ref _areaKeyPending, 0);   // the previous area's key must not outlive its load
        int ok = (*_loadAreaSlot)(area, camera, loadscreen);
        _loadArmed = ok != 0;
        _loadRefused = ok == 0;
        if (ok == 0)
        {
            LoadRefusals++;
            _log?.Always($"LightmapBake: CNWCArea::LoadArea returned 0 for area {area:x} (refusal {LoadRefusals}) -- the tile array it was building is gone, so no static set is closed and this area is lit entirely in real time");
        }
        return ok;
    }

    [UnmanagedCallersOnly]
    private static void OnSetTile(IntPtr area, IntPtr tile, int id, int orientation, int height)
    {
        if (_setTileSlot != null) (*_setTileSlot)(area, tile, id, orientation, height);
        OnTileChanged();
    }

    [UnmanagedCallersOnly]
    private static void OnComputeStaticLighting(IntPtr scene)
    {
        if (_origCsl != null) _origCsl(scene);
        _cslEdges++;
    }
}
