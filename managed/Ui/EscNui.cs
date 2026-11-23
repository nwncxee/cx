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

    /// <summary>The most windows one walk visits, so a list that rings cannot hold the pump.</summary>
    private const int MaxWindows = 256;

    /// <summary>The prefix the client's JSON window constructor gives a server NUI window's name.</summary>
    private static ReadOnlySpan<byte> JsonPrefix => "JsonWindow::"u8;

    /// <summary>Closes the topmost shown, closable server NUI window in <paramref name="ctx"/> whose bounds overlap the
    /// rect, and returns it; zero when none does. The window nuklear is inside a begin for is never touched.</summary>
    internal static IntPtr CloseOver(IntPtr ctx, float x, float y, float w, float h)
    {
        if (ctx == IntPtr.Zero) return IntPtr.Zero;
        var c = new NkContext(ctx);
        IntPtr current = c.Current, top = IntPtr.Zero;
        int seen = 0;
        for (var win = new NkWindow(c.WindowBegin); !win.IsNull && seen < MaxWindows; win = new NkWindow(win.Next), seen++)
            if (win.Ptr != current && Closes(win) && Overlaps(win.Bounds, x, y, w, h)) top = win.Ptr;
        if (top != IntPtr.Zero) new NkWindow(top).Flags |= Hidden | Closed;
        return top;
    }

    /// <summary>A shown server NUI window whose close button the player could press.</summary>
    private static bool Closes(NkWindow win)
        => (win.Flags & (Hidden | Closed | Minimized)) == 0 && (win.Flags & Closable) != 0 && win.Name.StartsWith(JsonPrefix);

    /// <summary>Whether nuklear bounds (x, y, w, h) and the rect share any point.</summary>
    private static bool Overlaps(Span<float> b, float x, float y, float w, float h)
        => b[0] < x + w && x < b[0] + b[2] && b[1] < y + h && y < b[1] + b[3];
}
