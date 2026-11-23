using System;
using CX.Engine;

namespace CX.Ui;

/// <summary>Esc's reach into NUI: the topmost server NUI window with a close button drawn over a CX window's Esc rect
/// is closed the way <c>nk_window_close</c> closes it, so the client's next NUI update runs that window's own close and
/// tells the server. Stateless; read and written on the input thread, which is the thread nuklear runs on.</summary>
internal static class EscNui
{
    /// <summary>nuklear's window flags as this build tests them: the close button drawn (the panel begin's check), hidden
    /// and closed (<c>nk_window_is_hidden</c>, <c>nk_window_is_closed</c>; <c>nk_window_close</c> sets both), minimized.</summary>
    private const uint Closable = 0x8, Hidden = 0x4000, Closed = 0x8000, Minimized = 0x10000;  // not-an-offset: nuklear window flags

    /// <summary>The prefix the client's JSON window constructor gives a server NUI window's name.</summary>
    private static ReadOnlySpan<byte> JsonPrefix => "JsonWindow::"u8;

    /// <summary>Closes the topmost shown, closable server NUI window in <paramref name="ctx"/> whose bounds overlap the
    /// rect; true when one closed. The window nuklear is inside a begin for is never touched.</summary>
    internal static bool CloseOver(IntPtr ctx, float x, float y, float w, float h)
    {
        if (ctx == IntPtr.Zero) return false;
        var c = new NkContext(ctx);
        IntPtr current = c.Current, top = IntPtr.Zero;
        var walk = new RingGuard(c.WindowBegin);
        for (var win = new NkWindow(c.WindowBegin); !win.IsNull; win = new NkWindow(win.Next))
        {
            if (win.Ptr != current && Closes(win) && Overlaps(win.Bounds, x, y, w, h)) top = win.Ptr;
            if (walk.Rings(win.Next)) break;
        }
        if (top != IntPtr.Zero) new NkWindow(top).Flags |= Hidden | Closed;
        return top != IntPtr.Zero;
    }

    /// <summary>A walk of nuklear's window list that stops when the list rings, however long it is: a pointer that
    /// moves at half the walk's pace (Floyd's check) meets the next window only on a ring.</summary>
    internal struct RingGuard(IntPtr begin)
    {
        private IntPtr _slow = begin;
        private int _step;

        /// <summary>True when <paramref name="next"/>, the window after the one just visited, closes a ring.</summary>
        internal bool Rings(IntPtr next)
        {
            if ((++_step & 1) == 0) _slow = new NkWindow(_slow).Next;
            return next != IntPtr.Zero && next == _slow;
        }
    }

    /// <summary>A shown server NUI window whose close button the player could press.</summary>
    private static bool Closes(NkWindow win)
        => (win.Flags & (Hidden | Closed | Minimized)) == 0 && (win.Flags & Closable) != 0 && win.Name.StartsWith(JsonPrefix);

    /// <summary>Whether nuklear bounds (x, y, w, h) and the rect share any point.</summary>
    private static bool Overlaps(Span<float> b, float x, float y, float w, float h)
        => b[0] < x + w && x < b[0] + b[2] && b[1] < y + h && y < b[1] + b[3];
}
