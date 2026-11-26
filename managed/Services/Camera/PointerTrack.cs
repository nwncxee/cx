using System;

namespace CX.Services.Camera;

/// <summary>Mouse-look's own pointer deltas where SDL's relative mode is warp emulation (SDL 2.0.8 on X11 without
/// XInput2): each motion counts from the last position seen here, never SDL's xrel. The cursor is warped back only
/// near the window edge; motion queued from before a warp is dropped, and a warp that lands however late resyncs
/// without turning, so no warp echo ever turns the view. Two warps that never land stop warping for the look.</summary>
internal sealed class PointerTrack
{
    /// <summary>Events far from a warp's target before that warp is judged lost and counting resumes from them.</summary>
    internal const int StaleLimit = 8;
    /// <summary>Lost warps in one look before warping stops and the look ends at the window edge.</summary>
    internal const int LostLimit = 2;
    private int _lastX, _lastY, _w, _h, _targetX, _targetY, _stale, _lost;
    private bool _awaiting, _late, _warping;
    /// <summary>Warps asked for, events dropped as queued from before a warp, and warps judged lost.</summary>
    internal int Warps, Dropped, Lost;
    /// <summary>The once-a-session "warps are ignored" log line has been written.</summary>
    internal bool Reported;

    /// <summary>A capture begins with the cursor at (<paramref name="x"/>, <paramref name="y"/>) in a
    /// <paramref name="w"/> x <paramref name="h"/> window; <paramref name="warped"/>: the cursor is being warped
    /// there (the action camera's centre), and motion from before that warp is dropped as any warp's is.</summary>
    internal void Begin(int x, int y, int w, int h, bool warped)
    {
        _lastX = x; _lastY = y; _lost = 0; _late = false; _warping = true;
        Size(w, h);
        if (warped) Await(x, y);
        else _awaiting = false;
    }

    /// <summary>The window's size now: the edge band and the warp target follow a resize mid-capture.</summary>
    internal void Size(int w, int h) { _w = Math.Max(w, 1); _h = Math.Max(h, 1); }

    /// <summary>One motion at window position (<paramref name="x"/>, <paramref name="y"/>): true with the delta to
    /// turn by when it moved; <paramref name="warp"/> asks the caller to warp the cursor to (wx, wy) now.</summary>
    internal bool Feed(int x, int y, out int dx, out int dy, out bool warp, out int wx, out int wy)
    {
        dx = dy = wx = wy = 0;
        warp = false;
        int far = Math.Min(_w, _h) / 4;
        bool atTarget = Math.Abs(x - _targetX) <= far && Math.Abs(y - _targetY) <= far;
        if (_awaiting)
        {
            if (!atTarget)
            {
                if (++_stale < StaleLimit) { Dropped++; return false; }
                _awaiting = false; _late = true; Lost++;
                if (++_lost >= LostLimit) _warping = false;
                _lastX = x; _lastY = y;
                return false;
            }
            _awaiting = false; _lost = 0;
        }
        else if (_late && atTarget && (Math.Abs(x - _lastX) > far || Math.Abs(y - _lastY) > far))
        {
            _late = false; _lost = 0; _warping = true;
            _lastX = x; _lastY = y;
            return false;
        }
        dx = x - _lastX; dy = y - _lastY;
        _lastX = x; _lastY = y;
        if (_warping && NearEdge(x, y))
        {
            warp = true; wx = _w / 2; wy = _h / 2;
            _lastX = wx; _lastY = wy;
            Await(wx, wy);
            Warps++;
        }
        return dx != 0 || dy != 0;
    }

    /// <summary>Warping has stopped for this capture: its warps never landed.</summary>
    internal bool WarpsIgnored => !_warping;

    private void Await(int x, int y) { _targetX = x; _targetY = y; _awaiting = true; _late = false; _stale = 0; }

    private bool NearEdge(int x, int y)
    {
        int mx = Math.Max(_w / 8, 16), my = Math.Max(_h / 8, 16);
        return x < mx || y < my || x >= _w - mx || y >= _h - my;
    }
}
