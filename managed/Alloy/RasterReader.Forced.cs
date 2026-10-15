using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The textures the engine forces uncompressed, read from a DDS anyway: <c>CAurTextureBasic::LoadImage</c> passes
/// <c>allowCompressed = 0</c> for a cube, a <c>numx/numy</c> grid and a controller texture, and
/// <c>AurGetImageWrapper</c> then tries only 4PC and TGA, so a DDS-only one never loads. Alloy unpacks level 0
/// into a buffer from the engine's <c>new[]</c> and clears the release mark, so <c>FreeImage</c> frees it.
/// </summary>
public sealed unsafe partial class RasterReader
{
    /// <summary>Under the engine-zones timer, so the unpack is counted in the <c>Image</c> zone.</summary>
    private const int ForcedPriority = 10;
    private static AurGetImageWrapperFn* _wrapperNext;
    private static AurTextureConstructImageFn* _constructNext;
    /// <summary><c>AurGetCompressedImage</c> / <c>AurReleaseRaster</c>: data slots, read at the call so a
    /// <see cref="DdsReader"/> swap is honoured.</summary>
    private static IntPtr* _ddsGetSlot, _ddsReleaseSlot;
    /// <summary>The texture whose <c>ConstructImage</c> is running, and the buffer handed out for it.</summary>
    private static IntPtr _building;
    private static byte* _unpacked;
    private static long _ddsUnpacked, _ddsRefused;
    /// <summary>The last texture built from an unpacked DDS: its pixels were BCn already, so the upload funnel
    /// does not transcode them a second time.</summary>
    internal static IntPtr Unpacked;

    private static void InstallForced(ExtenderLog log, HookService hooks)
    {
        _ddsGetSlot = (IntPtr*)Native.Sym(SymbolMap.AurGetCompressedImage);
        _ddsReleaseSlot = (IntPtr*)Native.Sym(SymbolMap.AurReleaseRaster);
        if (_ddsGetSlot == null || _ddsReleaseSlot == null)
        {
            log.Always("RasterReader: forced-uncompressed DDS REFUSED -- AurGetCompressedImage or AurReleaseRaster unresolved; a DDS-only cube, grid or controller texture stays unloaded");
            return;
        }
        _wrapperNext = hooks.ChainAurGetImageWrapper(&OnImage, ForcedPriority);
        _constructNext = hooks.ChainAurTextureConstructImage(&OnConstruct, ForcedPriority);
        log.Info(_wrapperNext != null && _constructNext != null
            ? "RasterReader: a DDS-only cube, grid or controller texture (which the engine never reads a DDS for) is unpacked by Alloy"
            : "RasterReader: forced-uncompressed DDS REFUSED -- AurGetImageWrapper or ConstructImage did not chain; such textures stay unloaded");
    }

    [UnmanagedCallersOnly]
    private static void OnConstruct(IntPtr tex)
    {
        IntPtr outer = _building;
        _building = tex; _unpacked = null;
        (*_constructNext)(tex);
        var b = new CAurTextureBasic(tex);
        bool ours = _unpacked != null && (byte*)b.Image == _unpacked;
        if (ours) b.FlagRasterRelease = 0;
        Unpacked = ours ? tex : IntPtr.Zero;
        _building = outer; _unpacked = null;
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnImage(IntPtr name, int* w, int* h, int* bpp, int* size, int* container, int* format, float* alpha, int allow)
    {
        IntPtr r = (*_wrapperNext)(name, w, h, bpp, size, container, format, alpha, allow);
        if (r != IntPtr.Zero || allow != 0 || _building == IntPtr.Zero) return r;
        var b = new CAurTextureBasic(_building);
        // LoadImage's single read by the resolved name: not a filerange sub-file, not a PLT, not a bump map.
        if (name != (IntPtr)Unsafe.AsPointer(ref b.ResolvedName) || b.FileRange != 0 || b.Schemes != IntPtr.Zero || b.FlagBump != 0)
            return r;
        // The unpack is decode; the DDS getter inside it times its own read and decode, so both are subtracted
        // rather than counted twice.
        long t0 = FrameZones.Now;
        long rd = FrameZones.Ticks(FrameZones.Zone.TexRead), dc = FrameZones.Ticks(FrameZones.Zone.TexDecode);
        IntPtr u = Unpack(name, b.FlagCube != 0, w, h, bpp, size, container, format, alpha);
        FrameZones.Add(FrameZones.Zone.TexDecode, t0,
                       FrameZones.Ticks(FrameZones.Zone.TexRead) - rd + FrameZones.Ticks(FrameZones.Zone.TexDecode) - dc);
        return u;
    }

    /// <summary>The DDS through the engine's getter, level 0 unpacked into an engine <c>new[]</c>, the DDS released,
    /// and the out params written as <c>AurGetRaster</c>'s branch of the wrapper writes them. A cube DDS for a cube
    /// texture becomes the six-face strip the engine's cube upload cuts (<see cref="CubeStrip"/>).</summary>
    private static IntPtr Unpack(IntPtr name, bool cube, int* w, int* h, int* bpp, int* size, int* container, int* format, float* alpha)
    {
        var get = (delegate* unmanaged<IntPtr, int*, int*, int*, int*, int*, float*, byte*>)*_ddsGetSlot;
        var release = (delegate* unmanaged<byte*, int, int, int, int, int, void>)*_ddsReleaseSlot;
        if (get == null || release == null) return IntPtr.Zero;
        int c, dw, dh, ch, sz;
        float am = *alpha;
        DdsReader.ForUnpack = true;
        byte* data = get(name, &c, &dw, &dh, &ch, &sz, &am);
        DdsReader.ForUnpack = false;
        if (data == null) return IntPtr.Zero;   // no DDS either: the engine's own fallback stands
        // Parsed by DdsReader when it is armed; the engine's own answer otherwise, which names only DXT1/DXT5 here.
        bool parsed = DdsReader.Lookup(data, out DdsReader.Info i);
        var f = parsed ? i.Format : ch == 4 ? DdsReader.Fmt.Bc3 : ch == 3 ? DdsReader.Fmt.Bc1 : DdsReader.Fmt.None;
        int faces = parsed && cube ? i.Faces : 1;
        int px = BcnDecode.PixelBytes(f);
        string? why = c != DdsReader.ContainerLegacy && c != DdsReader.ContainerMs ? $"container {c} (KTX)"
            : !BcnDecode.Decodes(f) ? $"{(f == DdsReader.Fmt.None ? $"{ch} channel(s)" : f.ToString())}: only BC1/BC2/BC3/BC7 are unpacked"
            : dw <= 0 || dh <= 0 || (long)dw * dh * faces > TgaRleMax / 4 ? $"size {dw}x{dh}" : null;
        if (why != null)
        {
            release(data, dw, dh, ch, sz, c);
            if (++_ddsRefused <= LogCap) _log?.Always($"RasterReader: {Marshal.PtrToStringAnsi(name)} is forced uncompressed and ships only a DDS Alloy does not unpack ({why}) -- it stays unloaded, as in vanilla");
            return IntPtr.Zero;
        }
        int face = dw * dh * px, n = face * faces;
        byte* pixels = (byte*)_znam((nuint)(n + TgaTagTail));
        // Not the TGA tag (3): an AurReleaseRaster on this buffer logs a refusal instead of calling a stale owner.
        MemoryMarshal.Write(new Span<byte>(pixels, n + TgaTagTail).Slice(n + 4), (short)0);
        int l0 = DdsReader.LevelSize(dw, dh, DdsReader.ChannelsOf(f));
        if (faces == 1)
            BcnDecode.Unpack(new ReadOnlySpan<byte>(data, l0), dw, dh, f, new Span<byte>(pixels, n));
        else
        {
            byte[] tmp = new byte[face];   // cold: once per cube DDS load
            var faceBytes = new ReadOnlySpan<byte>(data, (int)(5 * i.FaceStride + i.Level0));
            for (int k = 0; k < faces; k++)
            {
                BcnDecode.Unpack(faceBytes.Slice((int)(k * i.FaceStride), l0), dw, dh, f, tmp);
                CubeStrip.Place(tmp, dw, px, k, new Span<byte>(pixels, n));
            }
        }
        release(data, dw, dh, ch, sz, c);
        *w = dw; *h = dh * faces; *bpp = px; *size = 0; *alpha = am;
        *container = RasterContainer; *format = px == 4 ? RasterRgba : RasterRgb;
        _unpacked = pixels;
        if (++_ddsUnpacked <= LogCap) _log?.Info($"RasterReader: {Marshal.PtrToStringAnsi(name)} is forced uncompressed and ships only a DDS -- unpacked by Alloy ({dw}x{dh}, {f}{(faces == 6 ? ", six cube faces into the engine's strip" : "")})");
        return (IntPtr)pixels;
    }

    /// <summary><c>AurGetImageWrapper</c>'s raster answer: container 0, format 0 for 4 bytes/pixel, 1 for 3.</summary>
    private const int RasterContainer = 0, RasterRgba = 0, RasterRgb = 1;

    private static string ForcedNote() => $"; dds unpacked: {_ddsUnpacked}, refused {_ddsRefused}";
}
