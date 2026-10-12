using System;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// Where NUI drew one texture last frame: the image command in nuklear's queue whose handle is that GL texture, in the
/// layout space the pointer is in. A model view watches its own texture with it, so a press can tell it landed on the view.
/// </summary>
internal static unsafe class NuiImageSpot
{
    private const int CmdImage = 17;   // not-an-offset: nk_command_type NK_COMMAND_IMAGE
    /// <summary><c>nk_command_image</c> after its 16-byte header: <c>short x, y</c> then <c>ushort w, h</c>.</summary>
    private const int CmdX = 0x10, CmdY = 0x12, CmdW = 0x14, CmdH = 0x16, CmdNext = 0x08;   // not-an-offset: nk_command ABI
    /// <summary>The flags that take a window out of play (hidden, closed).</summary>
    private const uint WinOutOfPlay = 0x4400;   // not-an-offset: NK_WINDOW_HIDDEN | NK_WINDOW_CLOSED
    private const int MaxCommands = 1 << 20, MaxWindows = 256;

    /// <summary>The texture watched, and the rectangle it was last drawn at.</summary>
    private sealed class SpotState
    {
        public IntPtr* CtxSlot;
        public uint Tex;
        public bool Found;
        public float X, Y, W, H;
    }

    private static readonly SpotState _s = new();

    internal static void Init() => _s.CtxSlot = (IntPtr*)Native.Sym(SymbolMap.Nuklear);

    /// <summary>Watch <paramref name="tex"/> from the next frame on; 0 stops.</summary>
    internal static void Watch(uint tex)
    {
        _s.Tex = tex;
        if (tex == 0) _s.Found = false;
    }

    /// <summary>Is nuklear's pointer over where the watched texture was drawn last frame?</summary>
    internal static bool PointerOn()
    {
        SpotState s = _s;
        if (!s.Found || s.CtxSlot == null) return false;
        IntPtr ctx = *s.CtxSlot;
        return NuiHitTest.Pointer(ctx, out float x, out float y) && NuiHitTest.Contains(s.X, s.Y, s.W, s.H, x, y);
    }

    /// <summary>Once per frame at the flush: find the watched texture's image command, the topmost window's if several.</summary>
    internal static void Track()
    {
        SpotState s = _s;
        if (s.Tex == 0 || s.CtxSlot == null) return;
        IntPtr ctx = *s.CtxSlot;
        if (ctx == IntPtr.Zero) return;
        var c = new NkContext(ctx);
        byte* mem = (byte*)c.MemPtr;
        ulong allocated = c.MemAllocated;
        uint seq = c.Seq;
        bool found = false;
        int n = 0;
        for (IntPtr w = c.WindowBegin; w != IntPtr.Zero && n < MaxWindows; w = new NkWindow(w).Next, n++)
        {
            var win = new NkWindow(w);
            if (win.Seq != seq || (win.Flags & WinOutOfPlay) != 0) continue;
            ulong off = win.BufBegin, end = Math.Min(win.BufEnd, allocated);
            for (int k = 0; off < end && k < MaxCommands; k++)
            {
                byte* cmd = mem + off;
                if (*(int*)cmd == CmdImage && (uint)new NkCommandImage((IntPtr)cmd).Handle == s.Tex)
                {
                    found = true;
                    s.X = *(short*)(cmd + CmdX); s.Y = *(short*)(cmd + CmdY);
                    s.W = *(ushort*)(cmd + CmdW); s.H = *(ushort*)(cmd + CmdH);
                }
                ulong next = *(ulong*)(cmd + CmdNext);
                if (next <= off) break;
                off = next;
            }
        }
        s.Found = found;
    }
}
