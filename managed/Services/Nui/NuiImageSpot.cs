using System;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// Where NUI drew some textures last frame: the image command in nuklear's queue whose handle is that GL texture, in the
/// layout space the pointer is in. Each model view watches its own texture with it, so a press can tell which view it landed on.
/// </summary>
internal static unsafe class NuiImageSpot
{
    private const int CmdImage = 17;   // not-an-offset: nk_command_type NK_COMMAND_IMAGE
    /// <summary>The flags that take a window out of play (hidden, closed).</summary>
    private const uint WinOutOfPlay = 0x4400;   // not-an-offset: NK_WINDOW_HIDDEN | NK_WINDOW_CLOSED
    private const int MaxWindows = 256;

    /// <summary>A texture watched, and the rectangle it was last drawn at.</summary>
    internal sealed class Spot
    {
        public uint Tex;
        public bool Found;
        public float X, Y, W, H;
    }

    private sealed class SpotState
    {
        public IntPtr* CtxSlot;
        public bool Any;
    }

    private static readonly SpotState _s = new();

    internal static void Init() => _s.CtxSlot = (IntPtr*)Native.Sym(SymbolMap.Nuklear);

    /// <summary>Watch <paramref name="tex"/> as <paramref name="slot"/>'s from the next frame on; 0 stops.</summary>
    internal static void Watch(NuiModelDoll.ModelSlot slot, uint tex)
    {
        Spot p = slot.Spot;
        p.Tex = tex;
        if (tex == 0) p.Found = false;
        bool any = false;
        foreach (NuiModelDoll.ModelSlot q in NuiModelDoll.Slots) any |= q.Spot.Tex != 0;
        _s.Any = any;
    }

    /// <summary>Is nuklear's pointer over where slot <paramref name="slot"/>'s watched texture was drawn last frame?</summary>
    internal static bool PointerOn(NuiModelDoll.ModelSlot slot)
    {
        SpotState s = _s;
        Spot p = slot.Spot;
        if (!p.Found || s.CtxSlot == null) return false;
        IntPtr ctx = *s.CtxSlot;
        return NuiHitTest.Pointer(ctx, out float x, out float y) && NuiHitTest.Contains(p.X, p.Y, p.W, p.H, x, y);
    }

    /// <summary>Once per frame at the flush: find each watched texture's image command, the topmost window's if several.</summary>
    internal static void Track()
    {
        SpotState s = _s;
        if (!s.Any || s.CtxSlot == null) return;
        IntPtr ctx = *s.CtxSlot;
        if (ctx == IntPtr.Zero) return;
        var c = new NkContext(ctx);
        ulong allocated = c.MemAllocated;
        uint seq = c.Seq;
        foreach (NuiModelDoll.ModelSlot q in NuiModelDoll.Slots) q.Spot.Found = false;
        int n = 0;
        for (IntPtr w = c.WindowBegin; w != IntPtr.Zero && n < MaxWindows; w = new NkWindow(w).Next, n++)
        {
            var win = new NkWindow(w);
            if (win.Seq != seq || (win.Flags & WinOutOfPlay) != 0) continue;
            ulong off = win.BufBegin, end = Math.Min(win.BufEnd, allocated);
            while (off < end)
            {
                var cmd = c.CommandAt(off);
                var img = new NkCommandImage(cmd.Ptr);
                if (cmd.Type == CmdImage)
                    foreach (NuiModelDoll.ModelSlot m in NuiModelDoll.Slots)
                    {
                        Spot q = m.Spot;
                        if (q.Tex != 0 && (uint)img.Handle == q.Tex)
                        {
                            q.Found = true;
                            q.X = img.X; q.Y = img.Y;
                            q.W = img.W; q.H = img.H;
                        }
                    }
                ulong next = cmd.Next;
                if (next <= off) break;
                off = next;
            }
        }
    }
}
