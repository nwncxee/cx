using System;

namespace CX.Services.Camera;

/// <summary>Mouse-look's own pointer deltas where SDL's relative mode is warp emulation (SDL 2.0.8 on X11 without
/// XInput2): each motion counts from the last position seen here, never SDL's xrel. The cursor is warped back only
/// near the window edge and motion queued from before a warp is dropped, so a lost or late warp echo costs one event,
/// never a jump; a compositor that ignores warps leaves the look stopping at the edge.</summary>
internal sealed class PointerTrack
{
    /// <summary>Events a warp may leave queued behind it before the warp is judged ignored and warping stops.</summary>
    internal const int StaleLimit = 8;
    private int _lastX, _lastY, _w, _h, _targetX, _targetY, _stale;
    private bool _awaiting, _warping;
    /// <summary>Warps asked for, events dropped as queued from before a warp, and warps the compositor ignored.</summary>
    internal int Warps, Dropped, Ignored;

    /// <summary>A capture begins with the cursor at (<paramref name="x"/>, <paramref name="y"/>) in a
    /// <paramref name="w"/> x <paramref name="h"/> window.</summary>
    internal void Begin(int x, int y, int w, int h)
    {
        _lastX = x; _lastY = y; _w = w > 0 ? w : 1920; _h = h > 0 ? h : 1080;
        _awaiting = false; _stale = 0; _warping = true;
    }

    /// <summary>A capture that begins by warping the cursor to (<paramref name="x"/>, <paramref name="y"/>): the
    /// action camera's centre. Motion from before that warp is dropped as any warp's is.</summary>
    internal void BeginWarped(int x, int y, int w, int h)
    {
        Begin(x, y, w, h);
        Await(x, y);
    }

    /// <summary>One motion at window position (<paramref name="x"/>, <paramref name="y"/>): true with the delta to
    /// turn by when it moved; <paramref name="warp"/> asks the caller to warp the cursor to (wx, wy) now.</summary>
    internal bool Feed(int x, int y, out int dx, out int dy, out bool warp, out int wx, out int wy)
    {
        dx = dy = wx = wy = 0;
        warp = false;
        if (_awaiting)
        {
            int far = Math.Min(_w, _h) / 4;
            if (Math.Abs(x - _targetX) > far || Math.Abs(y - _targetY) > far)
            {
                if (++_stale < StaleLimit) { Dropped++; return false; }
                _awaiting = false; _warping = false; Ignored++;
                _lastX = x; _lastY = y;
                return false;
            }
            _awaiting = false;
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

    /// <summary>Warping has stopped for this capture: the compositor ignored a warp.</summary>
    internal bool WarpsIgnored => !_warping;

    private void Await(int x, int y) { _targetX = x; _targetY = y; _awaiting = true; _stale = 0; }

    private bool NearEdge(int x, int y)
    {
        int mx = Math.Max(_w / 8, 16), my = Math.Max(_h / 8, 16);
        return x < mx || y < my || x >= _w - mx || y >= _h - my;
    }
}
