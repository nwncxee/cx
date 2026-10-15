using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The TGA hand-back. <c>AurGetImageWrapper</c> tries the DDS slot, then 4PC, then the <c>AurGetRaster</c> data
/// slot, whose getter demands a <c>CResTGA</c> and reports w, h and bytes per pixel -- the wrapper turns that
/// depth into a format and nothing after it reads a TGA header, so answering the slot with those three and the
/// pixels skips the whole <c>CRes</c> path. <see cref="RasterTag"/> is what tells a buffer of ours apart.
/// </summary>
internal static unsafe partial class TexLoad
{
    /// <summary>The tag at a buffer of ours: not the engine's TGA tag (3) nor <see cref="RasterReader"/>'s forced 0,
    /// so the engine's release, if it ever saw one, refuses it with a logged error.</summary>
    private const short RasterTag = 0x0cd6;   // not-an-offset: a tag value
    /// <summary>The tag's place past the pixels, inside <see cref="RasterReader.TgaTagTail"/>.</summary>
    private const int RasterTagAt = 4;

    private static delegate* unmanaged<IntPtr, int*, int*, int*, byte*> _engRaster;
    private static IntPtr* _rasterSlot, _releaseSlot;
    private static long _rasterSwaps;

    /// <summary>From <see cref="Attach"/>: the two slots, or a loud line and DDS alone.</summary>
    private static void AttachRaster(ExtenderLog log)
    {
        _rasterSlot = (IntPtr*)Native.Sym(SymbolMap.AurGetRaster);
        _releaseSlot = (IntPtr*)Native.Sym(SymbolMap.AurReleaseRaster);
        if (_rasterSlot == null || _releaseSlot == null)
        {
            _rasterSlot = null;
            log.Always("TexLoad: TGA REFUSED -- AurGetRaster or AurReleaseRaster unresolved; every TGA is read by the engine");
        }
    }

    /// <summary>Once a frame, from <see cref="Drip"/>, and at <c>LoadArea</c>'s entry: store our getter once the constructor has filled the slot,
    /// and again if it refills it -- only while the release that frees our buffers is <see cref="DdsReader"/>'s.</summary>
    internal static void SwapRaster()
    {
        if (!_armed || _rasterSlot == null || !DdsReader.OwnsRelease) return;
        IntPtr ours = (IntPtr)(delegate* unmanaged<IntPtr, int*, int*, int*, byte*>)&OnRaster;
        IntPtr g = *_rasterSlot;
        if (g == ours || g == IntPtr.Zero) return;
        _engRaster = (delegate* unmanaged<IntPtr, int*, int*, int*, byte*>)g;
        *_rasterSlot = ours;
        _log?.Info($"TexLoad: raster slot swapped (#{++_rasterSwaps})");
    }

    [UnmanagedCallersOnly]
    private static byte* OnRaster(IntPtr name, int* w, int* h, int* bpp)
    {
        byte* px = TakeTga(name, out RasterReader.Raster r);
        if (px == null)
        {
            byte* read = _engRaster(name, w, h, bpp);
            if (read != null) NoteEngineTga(name);
            return read;
        }
        *(short*)&px[r.Bytes + RasterTagAt] = RasterTag;
        *w = r.W; *h = r.H; *bpp = r.Px;
        return px;
    }

    /// <summary>From the release slot: true when the buffer is a TGA of ours, and it is freed.</summary>
    internal static bool FreeRaster(byte* data, int w, int h, int bpp)
    {
        if (data == null || bpp == 2 || w <= 0 || h <= 0 || *(short*)&data[(long)w * h * bpp + RasterTagAt] != RasterTag) return false;
        NativeMemory.Free(data);
        return true;
    }
}
