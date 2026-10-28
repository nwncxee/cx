using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The DDS files the engine's reader cannot answer -- a <c>DX10</c> header, a DXT3 or BC4U/BC5U-style FourCC, a
/// real cube map. <c>GetCompressedRaster</c> still demands such a file and returns 1, with 0 channels and the
/// data pointer at <c>header + 128</c> even behind the 20-byte DX10 extension, so vanilla uploads garbage. Here
/// the parse is Alloy's alone, and a format only Alloy uploads is served only while it owns the upload.
/// </summary>
internal static unsafe partial class DdsReader
{
    /// <summary>Set by <see cref="RasterReader"/> around its getter call: the answer is decoded on the CPU and
    /// released at once, so it is never padded, a cube keeps all six faces (packed into the engine's strip by
    /// <see cref="CubeStrip"/>; anywhere else the engine sees face 0), and no upload has to be owned.</summary>
    internal static bool ForUnpack;
    /// <summary>What each live answer that needs it was parsed as, keyed by the pointer handed out; dropped by the release.</summary>
    private static readonly Dictionary<nint, Info> _answers = new();
    private static long _beyond, _beyondRefused;
    /// <summary>Formats Alloy admits beyond the engine's four, as a bit per <see cref="Fmt"/>.</summary>
    private static int _probed = -1;

    /// <summary>What <paramref name="data"/> (a pointer this reader handed out and has not seen released) was parsed as.</summary>
    internal static bool Lookup(byte* data, out Info info) => _answers.TryGetValue((nint)data, out info);

    /// <summary>A format only Alloy can upload, on a device that samples it.</summary>
    private static bool Uploadable(Fmt f, out string? no)
    {
        no = !OwnedTextures.OwnsUploads ? "Alloy does not own the upload (alloy.ownedtex/texsingle off, or the render device is not up yet)"
           : _probed < 0 ? "the format admission has not run yet"
           : (_probed & (1 << (int)f)) == 0 ? "it is not an admitted format"
           : null;
        return no == null;
    }

    /// <summary>The answer for a file the engine's reader gets wrong. The engine's buffer is always released here.</summary>
    private static byte* Beyond(IntPtr name, byte* data, ReadOnlySpan<byte> bytes, in Info i, int* container, int* w, int* h, int* ch, int* size, float* alpha)
    {
        int c = *container;
        if (!ForUnpack && !EngineUploads(i.Format) && !Uploadable(i.Format, out string? no))
            return Drop(name, data, c, w, h, ch, size, $"{Describe(i)}: {no}");
        // The forced path decodes level 0 of every face straight from the copy; a 2D answer is the padded chain the engine walks.
        long n = !ForUnpack ? i.ChainBytes : i.Faces == 6 ? 5 * i.FaceStride + i.Level0 : i.Level0;
        byte* mem = (byte*)NativeMemory.Alloc((nuint)(PrefixBytes + n));
        byte* ours = (byte*)Headroom<DdsMsPrefix>.PayloadOf(mem);
        if (ForUnpack) bytes.Slice(i.DataOffset, (int)n).CopyTo(new Span<byte>(ours, (int)n));
        else CopyChain(bytes, i, new Span<byte>(ours, (int)n));
        var tag = new DdsTag(ours, c == ContainerMs);
        tag.Type = OurTag; tag.Owner = IntPtr.Zero;
        _engRelease(data, *w, *h, *ch, *size, c);
        *w = i.W; *h = i.H; *ch = i.Channels; *size = i.Level0; *alpha = i.AlphaMean;
        _answers[(nint)ours] = i;
        if (++_beyond <= LogCap)
            _log?.Info($"DdsReader: {Marshal.PtrToStringAnsi(name)} is {Describe(i)} -- read by Alloy (the engine's reader knows only DXT1/DXT5/ATI1/ATI2 behind one header)"
                       + (EngineUploads(i.Format) || ForUnpack ? "" : "; Alloy uploads it"));
        return ours;
    }

    /// <summary>Release the engine's buffer and answer nothing, loudly: the engine's fallbacks (16-bit, TGA, its
    /// failed-texture flag) take over.</summary>
    private static byte* Drop(IntPtr name, byte* data, int c, int* w, int* h, int* ch, int* size, string why)
    {
        _engRelease(data, *w, *h, *ch, *size, c);
        if (++_beyondRefused <= LogCap)
            _log?.Always($"DdsReader: {Marshal.PtrToStringAnsi(name)} NOT LOADED -- {why} (the engine's own read of it is garbage)");
        return null;
    }

    private static string Describe(in Info i)
        => $"{i.Format}{(i.Srgb ? " sRGB (sampled as UNORM)" : "")}{(i.Faces == 6 ? " cube" : "")} {i.W}x{i.H}";

    /// <summary>Once: admit every format only Alloy uploads. The device was refused outright without
    /// <c>textureCompressionBC</c>, which carries all of them, so there is nothing left to ask it.</summary>
    private static void Probe()
    {
        if (_probed >= 0) return;
        _probed = 0;
        foreach (Fmt f in (ReadOnlySpan<Fmt>)[Fmt.Bc2, Fmt.Bc4S, Fmt.Bc5S, Fmt.Bc6U, Fmt.Bc6S, Fmt.Bc7]) _probed |= 1 << (int)f;
        _log?.Info("DdsReader: [formats] BC2/4/5/6H/7 sampled natively (textureCompressionBC, required of the device)");
    }

    private static string FormatsNote() => $"; dds beyond the engine: {_beyond} read, {_beyondRefused} NOT LOADED";
}
