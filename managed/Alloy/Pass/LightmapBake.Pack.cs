using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The atlas packing: one rect per charted instance in a block-aligned cell, placed by a shelf packer
/// that never mixes the two page classes, at the texel density its tier asks for and halved down until it
/// fits.</summary>
internal static unsafe partial class LightmapBake
{
    private enum RState : byte { Packing, Probing, Queued, Landed, PastCap }

    /// <summary>One bake key's rectangle: where it sits, whose geometry draws it, what it touches.</summary>
    private struct Rect
    {
        /// <summary>The bake key, and the key before the halvings were mixed in (every re-halving derives from it).</summary>
        internal ulong Key, Key0;
        internal int Slot, Vao, Item;
        internal int Page, X, Y, W, H;
        internal float S;
        internal Vector3 C; internal float R;
        internal RState State;
        internal ulong Content;
        /// <summary>Density halvings baked in: <see cref="Tier"/> + the area-wide halving. Mixed into the key.</summary>
        internal int Halved;
        /// <summary>The distance tier (0 = full density, <see cref="Baseline"/> = the coarsest, area-wide).</summary>
        internal int Tier;
        /// <summary>A baseline rect whose readers moved to a finer one; it keeps its page space as their
        /// fallback (an evicted fine rect hands them back) and is no orphan to the repack scans.</summary>
        internal bool Refined;
        /// <summary>This rect came off the area plan, not from an enrolment. It holds its page space
        /// until a slot claims its key; one nobody claims is counted at the release.</summary>
        internal bool Adopted;
        /// <summary>An adopted rect a slot has claimed at least once -- the silent hold's progress.</summary>
        internal bool Claimed;
        /// <summary>Its texels are in a page blob laid out as THIS packing places them -- baked into one this
        /// visit read back, or landed off a union of the adopted plan. A rect that landed from the per-rect
        /// index instead is not: that blob holds another packing, so the area plan cannot upload it whole and
        /// <see cref="AreaSnapRegion"/> re-reads the page it sits on.</summary>
        internal bool Unioned;
    }
    private static Rect[] _rects = new Rect[1024];
    /// <summary>Entries of evicted rects, reused before the table grows (a walk evicts thousands).</summary>
    private static readonly Stack<int> _free = new();
    /// <summary>Per rect, the slots reading it -- what a landing writes records for. Small (a placement is
    /// usually one instance); a vacated slot is removed, so nothing stale is ever walked.</summary>
    private static List<int>?[] _owners = new List<int>?[1024];
    private static int _rectN;
    private static readonly Dictionary<ulong, int> _byKey = new();
    private static readonly List<int> _toPack = new();

    /// <summary>The shelf packer, per page: the open shelf's top, height and cursor.</summary>
    private static readonly int[] _shelfY = new int[MaxPages], _shelfH = new int[MaxPages], _shelfX = new int[MaxPages];
    private static int _packPage;
    /// <summary>Pages belong to one class -- 0 unopened, 1 baseline (the area's, never evicted inside it),
    /// 2 fine (the walk's: dropped whole when the pack needs the space). The fine cursor is its own.</summary>
    private static readonly byte[] _pageClass = new byte[MaxPages];
    private const byte ClassBase = 1, ClassFine = 2;
    private static int _finePage;
    /// <summary>The refine sweep's cursor over the rects, and the frame it may run from.</summary>
    private static int _refineAt, _refineCursor;
    /// <summary>Fine rects evicted this period.</summary>
    private static int _evicted;
    /// <summary>Rects the refine sweep examines per frame (a distance each: a 26k table is one wave in 13 frames --
    /// at 256 a wave was one slice's finds, 84-249 rects, each paying the settle and the page bake), and the frames
    /// it stands down after a fine rect found no page: with every fine page inside its margin nothing frees until
    /// the player moves on.</summary>
    private const int RefinePerFrame = 2048, RefuseBackoffFrames = 600;
    /// <summary>A refining rect's readers, copied out before <see cref="Enrol(int, int, int, int)"/> edits the owner list.</summary>
    private static readonly List<int> _readers = new();
    /// <summary>The coarsest tier: the area-wide baseline the load bakes first. 0 = no tiers (<c>lightmap.tiers</c> 1).</summary>
    private static int Baseline => Lightmap.Tiers.V - 1;
    internal const int MaxTiers = 4;
    /// <summary>Tier t covers <c>lightmap.near</c> x 2^t metres from the player; the baseline covers everything.</summary>
    private static float Radius(int tier) => Lightmap.Near.V * (1 << tier);
    /// <summary>The finest tier whose radius holds a rect at distance <paramref name="d"/>.</summary>
    private static int DesiredTier(float d)
    {
        int t = 0;
        while (t < Baseline && d > Radius(t)) t++;
        return t;
    }

    /// <summary>A rect entry: the last freed one, unless a readback still names it -- the cache file would take
    /// the newcomer's key over the old bits -- then the table grows and the entry waits its turn. BOTH lists:
    /// <c>_pendingRead</c> is the region queued or draining, <c>_defRects</c> a landing stashed behind it.</summary>
    private static int NewRect()
    {
        if (_free.Count > 0 && !_pendingRead.Contains(_free.Peek()) && !_defRects.Contains(_free.Peek())) return _free.Pop();
        if (_rectN == _rects.Length) { Array.Resize(ref _rects, _rectN * 2); Array.Resize(ref _owners, _rectN * 2); }
        return _rectN++;
    }

    /// <summary>The walk. Landed rects coarser than their distance asks re-enrol their readers at the finer
    /// tier -- a new rect (cache hit or bake) while the baseline record stays live. Round-robin over the rects
    /// while nothing is pending, so one wave of fine rects lands before the next is asked, and never inside a
    /// refusal's back-off. Not under the hold: the player is not placed yet.</summary>
    private static void RefineSweep()
    {
        if (Hold || Baseline == 0 || _rectN == 0 || _pending > 0 || _frame < _refineAt || SetHash == 0) return;
        Vector3 p = AlloyRenderer.PlayerPos;
        for (int k = 0; k < RefinePerFrame; k++)
        {
            int ri = _refineCursor; if (++_refineCursor >= _rectN) _refineCursor = 0;
            ref Rect r = ref _rects[ri];
            if (r.State != RState.Landed || r.Tier == 0) continue;
            var o = _owners[ri];
            if (o == null || o.Count == 0) continue;
            int want = DesiredTier((r.C - p).Length());
            if (want >= r.Tier) continue;
            r.Refined = true;
            _readers.Clear(); _readers.AddRange(o);
            for (int j = 0; j < _readers.Count; j++)
            {
                int i = _readers[j];
                if (!AlloyRenderer.SlotPlacement(i, out int vao, out int item, out _)) continue;
                _slotRect[i] = -1; Disown(ri, i);
                Enrol(i, vao, item, want);
                if (_slotRect[i] == -1) { _slotRect[i] = ri; o.Add(i); }
            }
        }
    }

    /// <summary>Drop the fine page farthest past its tier -- every rect on it whose distance is beyond
    /// <paramref name="margin"/> x the next tier's radius (the hysteresis: a page in reach of a refine is never
    /// the one dropped) -- and re-open it. Its readers take their baseline rect at once (landed, keyed, its
    /// page space kept), or re-enrol. -1 = nothing to drop.</summary>
    private static int EvictFine(float margin)
    {
        Vector3 p = AlloyRenderer.PlayerPos;
        float* score = stackalloc float[MaxPages];
        for (int i = 0; i < MaxPages; i++) score[i] = _pageClass[i] == ClassFine && i != _bakePage ? float.MaxValue : -1f;
        for (int ri = 0; ri < _rectN; ri++)
        {
            ref Rect r = ref _rects[ri];
            if (r.Page < 0 || r.State == RState.PastCap || score[r.Page] < 0f) continue;
            float rel = (r.C - p).Length() / Radius(r.Tier + 1);
            if (rel < score[r.Page]) score[r.Page] = rel;
        }
        int best = -1, n = 0; float bs = 0f;
        for (int i = 0; i < MaxPages; i++) if (score[i] >= margin && (best < 0 || score[i] > bs)) { bs = score[i]; best = i; }
        if (best < 0) return -1;
        for (int ri = 0; ri < _rectN; ri++)
        {
            ref Rect r = ref _rects[ri];
            if (r.Page != best || r.State == RState.PastCap) continue;
            if (r.State != RState.Landed) _pending--;
            _placedArea -= Area(r); r.Page = -1;
            var o = _owners[ri];
            if (o != null)
            {
                for (int k = 0; k < o.Count; k++)
                {
                    int i = o[k];
                    _slotRect[i] = -1;
                    if (AlloyRenderer.SlotPlacement(i, out int vao, out int item, out _)) Enrol(i, vao, item, Baseline);
                    if (_slotRect[i] < 0 || _rects[_slotRect[i]].State != RState.Landed) SetRecord(i, -1f, 0f, 0f, 0f, Pick.None);   // no landed baseline: the record named the dropped page
                }
                o.Clear();
            }
            Kill(ri); _evicted++; n++;
        }
        // CX-102: the page's shelves re-open, so its texels are the next rect's -- a landing stashed off this
        // page would read someone else's pixels into its own keys' blob
        if (_defW > 0 && _defPage == best) { _uncached += _defRects.Count; _defW = 0; _defRects.Clear(); }
        _shelfY[best] = _shelfH[best] = _shelfX[best] = 0; _pageClass[best] = 0;
        _finePage = best; _packPage = Math.Min(_packPage, best);   // either cursor must reach the re-opened page
        if (_logLeft > 0) _log?.Info($"LightmapBake: page {best} evicted -- {n} rects, {bs:F1}x past their tier");
        return best;
    }

    /// <summary>The one re-key edge: the static-set fingerprint moved, or the density knob. Every seen slot
    /// forgets its rect and enrols again; a key that comes out the same keeps its rect, page and record, a
    /// changed one reads unbaked at once and never a stale page. The orphans a re-key leaves are reclaimed by
    /// one <see cref="Repack"/> when a page first refuses.</summary>
    internal static void RekeyAll() => _rekeyPending = true;

    private static void Rekey()
    {
        _rekeyPending = false;
        AbortBake();   // a page in flight is baking rects its readers are about to leave
        for (int ri = 0; ri < _rectN; ri++) _rects[ri].Refined = false;   // every key moves: nothing is anyone's fallback
        int changed = 0;
        for (int i = 0; i < _slotRect.Length; i++)
        {
            if (_slotRect[i] < 0) { if (_slotRect[i] == -2) _slotRect[i] = -1; continue; }
            int ri = _slotRect[i];
            _slotRect[i] = -1;
            Disown(ri, i);
            if (!AlloyRenderer.SlotPlacement(i, out int vao, out int item, out _)) continue;
            Enrol(i, vao, item, Math.Min(_rects[ri].Tier, Baseline));   // at its tier: a static light toggled is no reason to drop to the baseline
            // an unchanged key keeps its page and its record; a changed one keeps the OLD record until the
            // new rect lands (a rect's page space is never reused before Repack) -- clearing it flashed every
            // touched surface through the live loop, the "rebuild" of run 21 as the camera zoomed out
            if (_slotRect[i] != ri) changed++;
        }
        _log?.Info($"LightmapBake: re-keyed (static set {SetHash:x16}) -- {changed} instances re-bake");
    }

    /// <summary>Forget every rect and the packing; the pool stays, cleared, so no gap a later readback files
    /// still holds a texel of the packing before.</summary>
    private static void Repack()
    {
        Array.Fill(_slotRect, -1);
        for (int i = 0; i < _rec.Length; i += RecFloats) if (_rec[i] >= 0f) { _rec[i] = -1f; _dirtyLo = 0; _dirtyHi = _slotRect.Length - 1; }
        _rectN = 0; _byKey.Clear(); _toPack.Clear(); _free.Clear(); _pending = 0; _placedArea = 0;
        Array.Clear(_shelfY); Array.Clear(_shelfH); Array.Clear(_shelfX); Array.Clear(_pageClass); _packPage = 0; _finePage = 0; _refineCursor = 0;
        AbortBake();
        AreaSnapAbandon();   // the packing goes here: a plan waiting on one more readback is written as it stands
        if (_gl.Layers > 0 && Gl.DeleteTextures != null) ClearPages();
        AbortReadback(gl: true);
        if (_gl.Fence != IntPtr.Zero) { _uncached += _pendingRead.Count; if (Gl.DeleteSync != null) Gl.DeleteSync(_gl.Fence); _gl.Fence = IntPtr.Zero; }
        _pendingRead.Clear();
        _cache.Drain();
    }

    /// <summary>A different size is a different file: the halvings mixed into the key, none at zero.</summary>
    private static ulong Halve(ulong key0, int halved) => halved > 0 ? Mix(key0, halved) : key0;
    /// <summary>Texel area a rect takes on a page: its cell.</summary>
    private static long Area(in Rect r) => (long)Cell(r.W) * Cell(r.H);

    /// <summary>Shelf-pack the new rects, tallest first (so the cap drops the smallest last), once no
    /// newcomer has arrived for the settle window: an enrolment drains over many frames, and a shelf opened
    /// per frame's trickle packed an area's 3.7 pages of rects into 8.</summary>
    private static void Pack()
    {
        // CX-102: a union uploads as ONE rectangle -- the bbox of the plan's rects on that page -- and Place
        // continues the plan's own shelves, so a newcomer can land inside a bbox that has not gone up yet and
        // be blanked by it (and, with a stash, read back blank under its key). A plan can hold 1152 MiB of
        // unions at 256 MiB a frame, which is five frames against a settle window of four. Nothing is placed
        // until every union is in; adopting needs _rectN == 0, so no OTHER rect can be inside one.
        if (!AreaUploadsIn) return;
        if (_toPack.Count == 0 || _frame - _newAt < (Hold ? HoldSettleFrames : SettleFrames)) return;
        if (!FitDensity()) return;
        _toPack.Sort((a, b) => _rects[b].H.CompareTo(_rects[a].H));
        int placedN = 0;
        for (int k = 0; k < _toPack.Count; k++)
        {
            int ri = _toPack[k];
            ref Rect r = ref _rects[ri];
            if (!Place(ref r))
            {
                if (EvictFine(r.Tier < Baseline ? 1f : 0f) >= 0 && Place(ref r)) goto placed;
                if (r.Tier < Baseline) { Drop(ri, queued: false); _refineAt = _frame + RefuseBackoffFrames; continue; }
                // the pages are full of re-key orphans (a shelf cannot free): repack everything from nothing
                // once, and only then refuse. The slots re-enrol next frame and adopt their files from the cache.
                if (_rectN > _toPack.Count && !_repacked) { _repacked = true; Repack(); return; }
                // past the cap: half the density rather than no bake, down to a quarter (its own key: the file
                // is a different size). ponytail: the LAST rects packed halve, not the largest
                if (r.S > 0f && r.W > 4 && r.H > 4 && r.Halved - r.Tier < 2)
                {
                    _byKey.Remove(r.Key);
                    r.Halved++; r.S *= 0.5f; r.W = (r.W + 1) / 2; r.H = (r.H + 1) / 2; r.Key = Halve(r.Key0, r.Halved);
                    _byKey[r.Key] = ri;
                    if (Place(ref r)) goto placed;
                }
                r.State = RState.PastCap; _pastCap++;
                continue;
            }
            placed:
            _repacked = false;
            _placedArea += Area(r);
            r.State = RState.Probing; _pending++; placedN++;
            _cache.Probe(ri, r.Key, r.W, r.H);
        }
        StampPack(placedN);
        _toPack.Clear();
    }

    /// <summary>The newcomers' texel demand against the pages left: over it, the area's density halves until
    /// it fits (to <see cref="MaxAreaHalve"/>), counting the baseline's demand alone. Nothing placed yet = the
    /// newcomers shrink in place; else everything repacks at the new size. False = this Pack is over.</summary>
    private static bool FitDensity()
    {
        long demand = 0, baseDemand = 0;
        for (int k = 0; k < _toPack.Count; k++)
        {
            ref Rect r = ref _rects[_toPack[k]];
            demand += Area(r);
            if (r.Tier >= Baseline) baseDemand += Area(r);
        }
        int fit = _gl.Layers;
        long cap = (long)(fit * (long)PageSize * PageSize * PackShare);
        if (_placedArea + baseDemand <= cap) return true;   // only the baseline's overflow halves the area: a fine rect evicts or drops
        demand = baseDemand;
        // the pages hold the last area's orphans (an in-module area change is no world edge, so nothing
        // rewound them): repack the live rects once before shrinking anyone's texels
        if (!_repacked)
        {
            long orphan = 0;
            for (int ri = 0; ri < _rectN; ri++)
                if (_rects[ri].Page >= 0 && _rects[ri].State != RState.PastCap && !_rects[ri].Refined && (_owners[ri] == null || _owners[ri]!.Count == 0))
                    orphan += Area(_rects[ri]);
            if (orphan > 0) { _repacked = true; _placedArea = 0; Repack(); return false; }
        }
        int halve = Halves(_placedArea, demand, cap, _areaHalve);
        if (halve == 0) return true;
        _areaHalve += halve;
        _log?.Info($"LightmapBake: {(_placedArea + demand) / 1e6:F1} Mtexels asked of {cap / 1e6:F0} across {fit} pages -- the area bakes at 1/{1 << _areaHalve} of lightmap.density");
        if (_placedArea > 0) { _placedArea = 0; Repack(); return false; }
        for (int k = 0; k < _toPack.Count; k++)
        {
            ref Rect r = ref _rects[_toPack[k]];
            _byKey.Remove(r.Key);
            r.Halved = r.Tier + _areaHalve; r.S /= 1 << halve; r.W = Math.Max((r.W + (1 << halve) - 1) >> halve, 1); r.H = Math.Max((r.H + (1 << halve) - 1) >> halve, 1);
            r.Key = Halve(r.Key0, r.Halved);
            _byKey[r.Key] = _toPack[k];
        }
        return true;
    }

    /// <summary>Halvings that fit the area under <paramref name="cap"/> texels. With rects placed, ALL of them
    /// repack at the new density, so the placed area shrinks too -- holding it fixed ran a 5% overflow to the
    /// floor (1/16) and rebaked the whole area at it.</summary>
    internal static int Halves(long placed, long demand, long cap, int areaHalve)
    {
        int halve = 0;
        while ((placed > 0 ? (placed + demand) >> (2 * halve) : demand >> (2 * halve)) > cap && areaHalve + halve < MaxAreaHalve) halve++;
        return halve;
    }

    private static bool Place(ref Rect r)
    {
        byte cls = r.Tier < Baseline ? ClassFine : ClassBase;
        ref int cursor = ref (cls == ClassFine ? ref _finePage : ref _packPage);
        int cw = Cell(r.W), ch = Cell(r.H);
        while (true)
        {
            if (cursor >= _gl.Layers) return false;
            int p = cursor;
            if (_pageClass[p] != 0 && _pageClass[p] != cls) { cursor++; continue; }
            if (_shelfX[p] + cw <= PageSize && ch <= _shelfH[p])
            {
                _pageClass[p] = cls;   // only once a rect is on it: PagesUsed is what the area plan says it needs
                r.Page = p; r.X = _shelfX[p] + Gutter; r.Y = _shelfY[p] + Gutter;
                _shelfX[p] += cw;
                return true;
            }
            int ny = _shelfY[p] + _shelfH[p];
            if (ny + ch <= PageSize && cw <= PageSize)
            {
                _shelfY[p] = ny; _shelfH[p] = ch; _shelfX[p] = 0;
                continue;
            }
            cursor++;
        }
    }

    /// <summary>Pure: the side of a rect's cell -- the rect and a gutter each side, rounded up to whole
    /// blocks, so no compressed block of a page ever holds two rects.</summary>
    internal static int Cell(int n) => (n + 2 * Gutter + Block - 1) & ~(Block - 1);

    /// <summary>A rect's cell on its page: block-aligned, the rect <see cref="Gutter"/> texels in.</summary>
    private static void CellOf(in Rect r, out int x, out int y, out int w, out int h)
    {
        x = r.X - Gutter; y = r.Y - Gutter; w = Cell(r.W); h = Cell(r.H);
    }
}
