using System;
using CX.Engine;

namespace CX.Services.Nui;

/// <summary>
/// The nuklear panel state a leaf widget's hook reads: where the parent put this child (its rectangle in layout space, scroll
/// offset removed) and the panel's clip. nuklear 4.08 as compiled, the same on both builds: <c>nk_widget</c> @0xde84f0,
/// <c>nk_layout_widget_space</c> @0xdd8340, <c>nk_layout_space_push</c> @0xde7830 (Windows rva 0xa86e40) and Windows' <c>Layoutable::Render</c> read these values.
/// </summary>
internal static unsafe class NuiNuklear
{
        private const int OffsetXOff = 0x18, OffsetYOff = 0x20, AtXOff = 0x28, AtYOff = 0x2c;  // not-an-offset: nuklear panel ABI, not an engine class
    private const int RowIndexOff = 0x74, RowHeightOff = 0x78, RowColumnsOff = 0x80, RowItemOffsetOff = 0x98;  // not-an-offset: nuklear panel ABI, not an engine class
    internal const int MaxXOff = 0x30;  // not-an-offset: nuklear panel ABI, not an engine class
    private const int ClipOff = 0x44, RowTypeOff = 0x70, RowItemOff = 0xa0;  // not-an-offset: nuklear panel ABI, not an engine class
    /// <summary><c>NK_LAYOUT_STATIC_FREE</c>: the row type of <c>nk_layout_space_begin(ctx, NK_STATIC, h, INT_MAX)</c>,
    /// which every NUI group's <c>RenderInside</c> opens (Linux @0x84e0f0, Windows rva 0x952920).</summary>
    private const int StaticFree = 6;

    /// <summary>A widget's rectangle and its panel's clip, in the space nuklear's input and bounds use. <see cref="Panel"/> and
    /// <see cref="Left"/> let the off-screen skip credit the rectangle to the panel's horizontal extent.</summary>
    internal struct Box
    {
        public byte* Panel;
        public float Left, X, Y, W, H, ClipX, ClipY, ClipW, ClipH;
    }

    /// <summary>The panel fields <see cref="StartRow"/> changed, to put back.</summary>
    internal struct RowSave
    {
        public bool Moved;
        public int Index;
        public float AtY, ItemOffset;
    }

    /// <summary>When the panel's current row is full, the next widget starts a new one: <c>nk_panel_alloc_space</c> first runs the row
    /// advance (index 0, <c>at_y</c> down by the row height, the running offset cleared). This applies that advance to the panel's fields
    /// so a read-only layout query sees where that widget will land; <see cref="EndRow"/> puts the fields back.</summary>
    internal static RowSave StartRow(byte* panel)
    {
        var save = new RowSave { Index = *(int*)(panel + RowIndexOff), AtY = *(float*)(panel + AtYOff), ItemOffset = *(float*)(panel + RowItemOffsetOff) };
        if (*(int*)(panel + RowColumnsOff) > save.Index) return save;
        save.Moved = true;
        *(int*)(panel + RowIndexOff) = 0;
        *(float*)(panel + AtYOff) = save.AtY + *(float*)(panel + RowHeightOff);
        *(float*)(panel + RowItemOffsetOff) = 0;
        return save;
    }

    /// <summary>Undoes <see cref="StartRow"/>.</summary>
    internal static void EndRow(byte* panel, in RowSave save)
    {
        if (!save.Moved) return;
        *(int*)(panel + RowIndexOff) = save.Index;
        *(float*)(panel + AtYOff) = save.AtY;
        *(float*)(panel + RowItemOffsetOff) = save.ItemOffset;
    }

    /// <summary>Fills <paramref name="b"/>'s clip from <paramref name="panel"/>'s.</summary>
    internal static void ReadClip(byte* panel, ref Box b)
    {
        float* clip = (float*)(panel + ClipOff);
        b.ClipX = clip[0]; b.ClipY = clip[1]; b.ClipW = clip[2]; b.ClipH = clip[3];
    }

    /// <summary>The rectangle <paramref name="self"/> is about to render into, or false when the parent pushed none for it: a
    /// child with <c>noPush</c> reuses the previous child's rect, and a panel not in a free-space row has no such rect.</summary>
    internal static bool TryBox(IntPtr self, IntPtr ctx, out Box b)
    {
        b = default;
        if (new Nui__Layout__Layoutable(self).NoPush != 0) return false;
        IntPtr win = new NkContext(ctx).Current;
        if (win == IntPtr.Zero) return false;
        byte* p = (byte*)new NkWindow(win).Layout;
        if (p == null || *(int*)(p + RowTypeOff) != StaticFree) return false;
        float* item = (float*)(p + RowItemOff);
        b.Panel = p;
        b.Left = *(float*)(p + AtXOff) + item[0];
        b.X = b.Left - **(uint**)(p + OffsetXOff);
        b.Y = *(float*)(p + AtYOff) + item[1] - **(uint**)(p + OffsetYOff);
        b.W = item[2];
        b.H = item[3];
        ReadClip(p, ref b);
        return true;
    }
}
