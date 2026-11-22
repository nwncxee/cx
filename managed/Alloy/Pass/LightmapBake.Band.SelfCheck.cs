using CX.Framework;

namespace CX.Alloy;

/// <summary>Found test of the half scratch's band: which of a page's rects one band of columns bakes. Oracle: cells
/// worked by hand -- a rect's side plus two gutters of 2, up to a multiple of 4, its x two texels left.</summary>
internal static unsafe partial class LightmapBake
{
    /// <summary>Five rects on one page, cut to a half scratch's band and then to the whole scratch, on a scratch bake state.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void BandSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LightmapBake.BandSelfCheck");
        BakeVkState was = _vkb;
        Rect[] rects = _rects;
        int[] keep = _bakeRects.ToArray();
        Pick[] picks = _bakePicks.ToArray();
        try
        {
            _rects = new Rect[5];
            _rects[0] = new Rect { X = 3102, W = 1000 };
            _rects[1] = new Rect { X = 1002, W = 96 };
            _rects[2] = new Rect { X = 3062, W = 4 };
            _rects[3] = new Rect { X = 2502, W = 500 };
            _rects[4] = new Rect { X = 3058, W = 4 };
            _vkb = new BakeVkState { Cols = HalfCols };
            Queue5();
            Band();
            check.Expect(_vkb.BandX == 1000, $"the band starts at column {_vkb.BandX}, not the leftmost cell's 1000");
            check.Expect(string.Join(",", _bakeRects) == "1,3,4" && PicksFollow(),
                         $"the band kept rects {string.Join(",", _bakeRects)}, not 1,3,4 (cells ending at 1100, 3004 and its last column 3064), or a pick left its rect");
            _vkb = new BakeVkState { Cols = PageSize };
            Queue5();
            Band();
            check.Expect(_vkb.BandX == 0 && _bakeRects.Count == 5, $"the whole scratch banded at {_vkb.BandX} and kept {_bakeRects.Count} of 5 rects");
        }
        finally
        {
            _vkb = was; _rects = rects;
            _bakeRects.Clear(); _bakeRects.AddRange(keep);
            _bakePicks.Clear(); _bakePicks.AddRange(picks);
        }
        log.Info(check.Ok ? "LightmapBake.BandSelfCheck: passed" : "LightmapBake.BandSelfCheck: FAILED");
    }

    /// <summary>The five fixture rects queued in order, each with a pick naming its own index.</summary>
    private static void Queue5()
    {
        _bakeRects.Clear(); _bakePicks.Clear();
        for (int i = 0; i < 5; i++) { _bakeRects.Add(i); _bakePicks.Add(new Pick { L0 = i, L1 = -1, L2 = -1, L3 = -1 }); }
    }

    /// <summary>Every queued rect's pick still names that rect.</summary>
    private static bool PicksFollow()
    {
        for (int k = 0; k < _bakeRects.Count; k++) if (_bakePicks[k].L0 != _bakeRects[k]) return false;
        return _bakeRects.Count == _bakePicks.Count;
    }
}
