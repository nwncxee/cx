using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Headless checks of the packer and the bake key: rects stay apart and inside the page, the tiers double
/// out to the baseline, and a key moves with the placement, the density and a touching light and with nothing
/// else.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>Headless: the packer keeps rects apart by the gutter and inside the page, refuses past the
    /// cap, and the bake key moves with what it must (placement, density, a touching light) and only that.
    /// The tiers double out to the baseline, the two classes never share a page, an evicted fine page is the
    /// next fine rect's, and a re-halved key is the key a fresh enrol computes.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(ChartBufDecl.Contains($"binding = {BindChart})") && ChartBufDecl.Contains($"binding = {UnitA})") && ChartBufDecl.Contains($"binding = {UnitB})")
                     && ChartBufDecl.Contains($"binding = {UnitC})"),
          "the GLSL bindings drifted from BindChart/UnitA/UnitB/UnitC");
        check.Expect(ChartBufDecl.Contains($"CX_LM_PAGE = {PageSize}.0") && BakeVs.Contains($"CX_LM_PAGE = {PageSize}.0") && BakeGs.Contains($"CX_LM_PAGE = {PageSize}.0"), "the GLSL page size drifted from PageSize");
        check.Expect(BakeFs.Contains("CxPointLight(") && BakeFs.Contains("CxPointDiffuse(") && BakeFs.Contains("CxLocalShadow("),
          "the bake stage does not run the lit stage's own per-light body");
        // CX-54 step 0: the key sample must be the area's lowest key whatever order the rects arrive in
        check.Expect(KeySampleBeats(false, 0ul, ulong.MaxValue), "the first rect offered is not taken as the sample");
        check.Expect(KeySampleBeats(true, 9ul, 8ul) && !KeySampleBeats(true, 8ul, 9ul) && !KeySampleBeats(true, 8ul, 8ul),
          "the key sample is not the lowest key of the area, so two runs can sample different rects");
        // the packer, on a scratch page state
        int layers = Layers, page = _packPage;
        long placedWas = _placedArea;
        WindowState winWas = _win;
        _win = new WindowState();
        var sy = (int[])_shelfY.Clone(); var sh = (int[])_shelfH.Clone(); var sx = (int[])_shelfX.Clone();
        Layers = 1; _packPage = 0; Array.Clear(_shelfY); Array.Clear(_shelfH); Array.Clear(_shelfX);
        var a = new Rect { W = 100, H = 50 }; var b = new Rect { W = 100, H = 40 }; var c = new Rect { W = 4000, H = 60 };
        // oracle: hand-worked cells -- a side plus two gutters of 2, up to a multiple of 4
        check.Expect(Cell(1) == 8 && Cell(4) == 8 && Cell(5) == 12 && Cell(100) == 104 && Cell(50) == 56, "a cell is the rect and its gutters in whole blocks");
        check.Expect(Place(ref a) && Place(ref b), "two rects fit an empty page");
        check.Expect(a.Page == 0 && b.Page == 0 && a.X == 2 && a.Y == 2 && b.X == 106 && b.Y == 2, $"the second rect sits one cell past the first, on the first shelf (a {a.X},{a.Y} b {b.X},{b.Y})");
        check.Expect(Place(ref c) && c.X == 2 && c.Y == 58, $"a rect too wide for the shelf opens a new shelf one cell below (c {c.X},{c.Y})");
        var big = new Rect { W = 4000, H = 4000 };
        check.Expect(!Place(ref big), "a rect past the last page under the cap is refused, not placed");
        float near = Lightmap.Near.V;
        check.Expect(DesiredTier(0f) == 0 && DesiredTier(near * 100f) == Baseline && (Baseline < 1 || DesiredTier(near * 1.5f) == 1), "the tier doubles out from lightmap.near to the baseline");
        if (Baseline > 0 && _rectN == 0)
        {
            Layers = 2; _packPage = 0; _finePage = 0; Array.Clear(_pageClass);
            int rb = NewRect(), rf = NewRect();
            _rects[rb] = new Rect { Key = 1, W = 100, H = 50, Tier = Baseline, Page = -1 }; _rects[rf] = new Rect { Key = 2, W = 100, H = 50, Tier = 0, Page = -1, C = new Vector3(near * 1.5f, 0f, 0f) };
            _byKey[1] = rb; _byKey[2] = rf;
            check.Expect(Place(ref _rects[rb]) && Place(ref _rects[rf]) && _rects[rb].Page != _rects[rf].Page, "a baseline rect and a fine rect open pages of their own");
            _rects[rf].State = RState.Landed;
            var fill = new Rect { W = 4000, H = 4080, Tier = 0 };
            check.Expect(!Place(ref fill), "a fine rect past the cap is refused before eviction");
            int fp = _rects[rf].Page;
            check.Expect(EvictPage(1f) < 0, "a page inside the next tier's radius is never evicted (the hysteresis)");
            _rects[rf].C = new Vector3(near * 8f, 0f, 0f);
            check.Expect(EvictPage(1f) == fp && Place(ref fill) && fill.Page == fp, "the fine page past its tier is evicted and re-opened for the next fine rect");
            check.Expect(_rects[rf].State == RState.PastCap && !_byKey.ContainsKey(2) && _free.Count == 1, "the evicted rect is dead, unkeyed and its entry free for reuse");
            check.Expect(Halve(Halve(7, 0), 2) == Halve(7, 2) && Halve(7, 2) != Halve(7, 3) && Halve(7, 0) == 7, "a re-halved key is the key a fresh enrol computes");
            _rectN = 0; _byKey.Clear(); _free.Clear(); Array.Clear(_pageClass); _finePage = 0;
        }
        // the camera window. Oracle: hand-worked distances
        check.Expect(InWindow(new Vector3(100f, 0f, 0f), 0f, Vector3.Zero, 100f) && !InWindow(new Vector3(100.5f, 0f, 0f), 0f, Vector3.Zero, 100f)
                     && InWindow(new Vector3(0f, 105f, 0f), 5f, Vector3.Zero, 100f), "a rect is in the window when its sphere reaches the radius");
        check.Expect(RectDensity(8, false, true) == 4 && RectDensity(2, false, true) == 2 && RectDensity(8, true, true) == 8 && RectDensity(8, false, false) == 8,
                     "exterior ground bakes at 4 texels a metre at most; interiors and placeables at the knob");
        if (_rectN == 0)
        {
            float w = Lightmap.Window.V;
            var unionsWas = _areaUnions.ToArray();
            Layers = 2; _packPage = 0; _finePage = 0; Array.Clear(_pageClass); Array.Clear(_shelfY); Array.Clear(_shelfH); Array.Clear(_shelfX);
            int rb = NewRect();
            (_owners[rb] ??= new List<int>(2)).Clear();
            _rects[rb] = new Rect { Key = 3, W = 100, H = 50, Tier = Baseline, Page = -1, State = RState.Landed, C = new Vector3(w * 1.3f, 0f, 0f), R = w * 0.1f };
            _byKey[3] = rb;
            check.Expect(Place(ref _rects[rb]) && _rects[rb].Page == 0, "a baseline rect opens the first page");
            check.Expect(EvictPage(0f) < 0, "with no window a baseline page is never evicted");
            _win = new WindowState { On = true, C = Vector3.Zero };
            check.Expect(EvictPage(0f) < 0, "a baseline page whose rect's sphere is inside the window's 1.25 margin is never evicted, even for a baseline newcomer");
            _rects[rb].R = 0f;
            check.Expect(EvictPage(0f) == 0 && _rects[rb].State == RState.PastCap && _pageClass[0] == 0 && _packPage == 0,
                         "a baseline page past the window's margin is evicted and re-opened");
            _rectN = 0; _byKey.Clear(); _free.Clear(); Array.Clear(_pageClass); _finePage = 0;
            _areaUnions.Clear(); _areaUnions.AddRange(unionsWas);
        }
        _win = winWas; _placedArea = placedWas;
        Layers = layers; _packPage = page; Array.Copy(sy, _shelfY, sy.Length); Array.Copy(sh, _shelfH, sh.Length); Array.Copy(sx, _shelfX, sx.Length);
        // the key
        int setN = _setN;
        if (_setPosR.Length < 1) { _setPosR = new Vector4[1]; _setCol = new Vector3[1]; }
        _setPosR[0] = new Vector4(0f, 0f, 0f, 10f); _setCol[0] = Vector3.One; _setN = 1;
        ulong k0 = BakeKey(1, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f, 8);
        check.Expect(k0 == BakeKey(1, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f, 8), "the key is deterministic");
        check.Expect(k0 != BakeKey(2, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f, 8), "a different chart is a different key");
        check.Expect(k0 != BakeKey(1, Matrix4x4.CreateTranslation(0.5f, 0f, 0f), new Vector3(5f, 0f, 0f), 1f, 8), "a moved placement is a different key");
        check.Expect(k0 == BakeKey(1, Matrix4x4.CreateTranslation(0.001f, 0f, 0f), new Vector3(5f, 0f, 0f), 1f, 8), "a millimetre of placement is the same key (1 cm quantum)");
        check.Expect(k0 != BakeKey(1, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f, 4), "the rect's own density is in the key");
        check.Expect(k0 != BakeKey(1, Matrix4x4.Identity, new Vector3(50f, 0f, 0f), 1f, 8), "out of the light's reach is a different key");
        _setCol[0] = new Vector3(0.5f, 1f, 1f);
        check.Expect(k0 != BakeKey(1, Matrix4x4.Identity, new Vector3(5f, 0f, 0f), 1f, 8), "a touching light's colour is in the key");
        _setN = setN;
        // CX-56: a surface's pick -- the strongest four touching lights, strongest first, ties on position and
        // never on the order the set lists them in
        Vector4[] savedP = _setPosR; Vector3[] savedC = _setCol; int savedN = _setN;
        _setPosR = [new(0f, 0f, 0f, 10f), new(4f, 0f, 0f, 10f), new(1f, 0f, 0f, 10f), new(3f, 0f, 0f, 10f), new(2f, 0f, 0f, 10f), new(50f, 0f, 0f, 10f)];
        _setCol = [Vector3.One, Vector3.One, Vector3.One, Vector3.One, Vector3.One, Vector3.One]; _setN = 6;
        Pick pk = PickOf(Vector3.Zero, 0.1f);
        check.Expect(pk.L0 == 0 && pk.L1 == 2 && pk.L2 == 4 && pk.L3 == 3 && pk.ChannelOf(1) == Channels && pk.ChannelOf(5) == Channels,
          "the pick is the four nearest touching lights, strongest first; the fifth and an out-of-reach light hold no channel");
        _setPosR = [new(2f, 0f, 0f, 10f), new(-2f, 0f, 0f, 10f)]; _setN = 2;
        Pick tie = PickOf(Vector3.Zero, 0.1f);
        _setPosR = [new(-2f, 0f, 0f, 10f), new(2f, 0f, 0f, 10f)];
        Pick tieRev = PickOf(Vector3.Zero, 0.1f);
        check.Expect(tie.L0 == 1 && tieRev.L0 == 0, "an equal-weight tie breaks on position, not on the set's order");
        _setPosR = savedP; _setCol = savedC; _setN = savedN;
        // CX-102: the frame accounting. Every phase has a name, and the worst-frame clause names the phase
        // that dominated -- a mis-sized table would name the wrong one, or throw inside the period line.
        check.Expect(PhName.Length == (int)Ph.Count, "the phase names drifted from the Ph enum, so the worst-frame clause names the wrong phase");
        check.Expect(Dominant([1L, 5L, 3L]) == 1 && Dominant([5L, 1L, 3L]) == 0 && Dominant([0L, 0L, 0L]) == 0,
          "the worst frame's phase is not its largest slice");
        // oracle: hand-placed slices -- 10 ms of steps under the hold, then 6 ms of adopt in play
        bool holdWas = Hold;
        PhaseReset();
        Hold = true; _phFrame[(int)Ph.Steps] = Stopwatch.Frequency / 100; PhaseFrameEnd();
        Hold = false; _phFrame[(int)Ph.Adopt] = Stopwatch.Frequency * 6 / 1000; PhaseFrameEnd();
        check.Expect(_worstHeld.Ph == (int)Ph.Steps && Math.Abs(_worstHeld.Ms - 10.0) < 0.01 && _worstHeld.Frames == 1
                     && _worstSeen.Ph == (int)Ph.Adopt && Math.Abs(_worstSeen.Ms - 6.0) < 0.01 && _worstSeen.Frames == 1,
          "a frame under the hold is booked as a visible hitch, or the other way round");
        Hold = holdWas; PhaseReset();
        check.Expect(Halves(100, 12, 107, 0) == 1 && Halves(0, 112, 107, 0) == 1 && Halves(0, 50, 107, 0) == 0 && Halves(0, 1L << 40, 107, 2) == 2,
                     "a 5% overflow halves the area once, not to the floor");
        // oracle: hand-worked -- 960 / 96 = 10; a 2 GiB card caps at 512 MiB = 5 pages; 1504 MiB free less the
        // 1024 reserve is 480 = 5 pages; 1500 free on an unknown card is 476 = 4; 100 MiB still holds one page
        check.Expect(PoolPages(960, 8192, 0) == 10 && PoolPages(960, 2048, 0) == 5 && PoolPages(960, 0, 0) == 10
                     && PoolPages(960, 8192, 1504) == 5 && PoolPages(960, 0, 1500) == 4
                     && PoolPages(3072, 0, 0) == 32 && PoolPages(9999, 0, 0) == 32 && PoolPages(100, 0, 0) == 1,
          "the page pool is not lightmap.vram in 96 MiB pages under a quarter of the card and its free memory");
        // CX-102: the whole-page readback copies out of the open map in slices and stops at the total
        check.Expect(SliceEnd(0, 100, 30) == 30 && SliceEnd(90, 100, 30) == 100 && SliceEnd(100, 100, 30) == 100 && SliceEnd(0, 10, 30) == 10,
          "a readback slice runs past the bytes it has");
        // a host read of write-combined memory runs at an order of 1 MiB/ms, so the slice in MiB is its
        // milliseconds: keep it inside the floor the worst-frame clause counts against (the hold's is exempt --
        // the loading backdrop is what hides it)
        check.Expect(CopyPer(false) <= (long)WorstFloorMs << 20, "the readback's slice is big enough to BE the frame it is sliced to avoid");
        // CX-102: a stash and an open map both count as a readback in flight -- AreaSnapStep's cursor consumes a
        // page the moment it thinks one is idle, and consuming it drops that page's union for good
        int dw = _defW, dh = _defH, dx = _defX0, dy = _defY0, dp = _defPage, un = _uncached;
        var defWas = _defRects.ToArray();
        _defW = 0; _defRects.Clear();
        check.Expect(_uMap == null && _gl.Fence == IntPtr.Zero && ReadbackIdle, "nothing in flight is idle");
        var two = new List<int> { 0, 1 };
        StashReadback(3, 0, 0, 8, 8, two);
        check.Expect(!ReadbackIdle, "a landing stashed behind a draining copy still counts as a readback in flight");
        StashReadback(4, 0, 0, 8, 8, two);
        check.Expect(_uncached == un + 2 && _defPage == 4, "a stash replaced inside one window counts the rects it dropped and keeps the newer landing");
        AbortReadback(gl: false);
        check.Expect(ReadbackIdle && _uncached == un + 4, "abandoning a readback drops the stash and counts its rects");
        // CX-102: and the pack may reuse a stashed rect's entry or its page space between the stash and the
        // issue (EvictPage Kills landed rects and re-opens the page), which would put the new tenant's key over
        // the old tenant's pixels in the blob the NEXT session adopts
        if (_rectN == 0)
        {
            int un2 = _uncached;
            int r0 = NewRect(), r1 = NewRect();
            _rects[r0] = new Rect { Page = 2, State = RState.Landed };
            _rects[r1] = new Rect { Page = 2, State = RState.Landed };
            _defPage = 2; _defW = 8; _defH = 8; _defRects.Clear(); _defRects.Add(r0); _defRects.Add(r1);
            check.Expect(StashValid() == 2 && _uncached == un2, "a stash whose rects all still hold their page is issued whole");
            _free.Clear(); _free.Push(r1);
            check.Expect(NewRect() != r1, "a rect entry a stashed landing still names was recycled under it");
            _rects[r1].State = RState.PastCap;
            check.Expect(StashValid() == 1 && _uncached == un2 + 1, "a stashed rect the pack killed stays in the blob");
            _rects[r0].Page = 3;
            check.Expect(StashValid() == 0 && _uncached == un2 + 2, "a stashed rect that moved page stays in the blob");
            _rectN = 0; _free.Clear();
        }
        _defW = dw; _defH = dh; _defX0 = dx; _defY0 = dy; _defPage = dp; _defRects.Clear(); _defRects.AddRange(defWas); _uncached = un;
        // Both arms of the pack's gate: off-device no plan is ever adopted, so the first one alone always
        // holds however the gate is spelled.
        var planWas = _areaPlan; var upWas = _areaUp;
        _areaPlan = null;
        check.Expect(AreaUploadsIn, "with no area plan adopted the pack must not be gated on one");
        _areaPlan = new LightmapAreaPlan(); _areaUp = new();
        check.Expect(!AreaUploadsIn, "...but with one adopted the pack waits for its upload");
        _areaUp.Done = true;
        check.Expect(AreaUploadsIn, "...and goes on once the upload is in");
        _areaPlan = planWas; _areaUp = upWas;
        // oracle: hand-worked -- 4 MiB / (4096 texels * 6 B) = 170 rows, down to whole blocks = 168, so a full
        // page is 24 bands of 168 and one of 64
        check.Expect(BandRows(4096, 4096, 0) == 168 && BandRows(4096, 4096, 4080) == 16 && BandRows(16, 16, 0) == 16,
                     "a union band is 4 MiB of whole block rows, clipped to the union");
        int bands = 0;
        for (int row = 0; row < 4096; row += BandRows(4096, 4096, row)) bands++;
        check.Expect(bands == 25, $"a full page went up in {bands} bands, not 25");
        log.Info(check.Ok ? "LightmapBake.SelfCheck: passed" : "LightmapBake.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
