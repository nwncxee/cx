using System;
using CX.Alloy.Db;
using CX.Engine;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// Alloy's own 2D upload: <c>GLRender::CreateTexture</c> @0x4ca2b0 (Windows RVA 0xAF440, behind
/// <c>RenderInterface::CreateTexture</c>'s jump at 0xBE960, the same logic) transcribed onto immutable DSA storage --
/// the same cell, levels, sampler and swizzle, with no bind and no call into the engine. A DDS format the
/// engine's table cannot name goes up under its real format.
/// </summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>Alloy's upload stands in for the engine's right now: the only state in which a format the engine
    /// cannot name may be handed to it (a shape Alloy declines still goes up the engine's way).</summary>
    internal static bool OwnsUploads => _armed && _single && _origCreate != null && GlContextService.Available;

    private static long _ddsFormatUploads, _ddsFormatFailed;

    /// <summary><c>textureFormatTranslationTable</c> @0x12f5080 (Windows RVA 0xD8AD70, identical), indexed by
    /// <c>GetCompressionFormat()</c>: 7 DXT1, 8 DXT5, 12/13 ETC2, 14/15 RGTC. 0 = no entry.</summary>
    private static ReadOnlySpan<uint> EngineFormats =>
    [
        0x1908, 0x1907, 0x8227, 0x1903, 0x80E1, 0x8225, 0x8226, 0x83F0, 0x83F3,   // not-an-offset: GL enums
        0x8056, 0x84F9, 0x1902, 0x9274, 0x9278, 0x8DBB, 0x8DBD, 0x8C3A, 0,          // not-an-offset: GL enums
    ];

    /// <summary>The sampler state the engine gives one texture; <see cref="Aniso"/> = the player's level.</summary>
    private readonly record struct Sampler(int Min, int Mag, int WrapS, int WrapT, int WrapR, bool Aniso);

    /// <summary><c>GLRender::SetTextureParameters</c> @0x4ca180, fed <c>GetClamp()</c> (bit 2, R, read for a cube only),
    /// <c>GetFilter()</c> and <c>GetMipMap()</c>. A linear texture with no mips minifies NEAREST, as the engine's does.</summary>
    private static Sampler EngineSampler(int clamp, bool linear, bool mip) => new(
        mip ? (linear ? Gl.LINEAR_MIPMAP_LINEAR : Gl.NEAREST_MIPMAP_NEAREST) : Gl.NEAREST,
        linear ? Gl.LINEAR : Gl.NEAREST,
        (clamp & 1) != 0 ? Gl.CLAMP_TO_EDGE : Gl.REPEAT,
        (clamp & 2) != 0 ? Gl.CLAMP_TO_EDGE : Gl.REPEAT,
        (clamp & 4) != 0 ? Gl.CLAMP_TO_EDGE : Gl.REPEAT,
        linear && mip);

    /// <summary>The levels a mipped texture gets: <c>SetTextureMipmapLevelParameters</c> @0x4c9c70's MAX_LEVEL
    /// plus one, i.e. down to 1x1 on the longer side.</summary>
    private static int EngineLevels(int w, int h)
    {
        int n = 1;
        for (int v = w | h; (v >>= 1) != 0;) n++;
        return n;
    }

    /// <summary>What happened instead of Alloy's upload, which decides who draws the cell.</summary>
    private enum Decline
    {
        /// <summary>It happened.</summary>
        None,
        /// <summary>Level 0 is past <c>GL_MAX_TEXTURE_SIZE</c>, which <c>GLRender::CreateTexture</c> @0x4ca2b0
        /// box-filters down and whose compressed branch starts from a smaller level. Alloy does neither, so the
        /// cell stays the engine's.</summary>
        Oversize,
        /// <summary>A shape the engine's own upload cannot produce pixels for either, so an empty Alloy texture
        /// is the state it would have left: no usable size (nothing to upload), or a compression-format index
        /// <c>textureFormatTranslationTable</c> @0x12f5080 holds 0 for (<c>glCompressedTexImage2D</c> with
        /// internalformat 0 is <c>GL_INVALID_ENUM</c>).</summary>
        EngineToo,
        /// <summary>Alloy's upload did not happen and the engine's may still succeed -- the driver refused one of
        /// our calls, or the sheet is a shape we do not transcribe. A BCn format the engine mislabels uploads as
        /// garbage rather than nothing, so this cell goes back to it; the diagnostic line names which it was.</summary>
        Refused,
    }

    /// <summary>Cell <paramref name="cell"/> of <paramref name="tex"/> into a texture of Alloy's own, as the engine
    /// would have uploaded it. 0 names its reason in <paramref name="why"/>, and <paramref name="err"/> carries the
    /// driver's code when there was one.</summary>
    private static uint UploadOwn(IntPtr tex, int cell, out uint err, out Decline why)
    {
        err = 0; why = Decline.Refused;
        var t = new CAurTexture(tex);
        byte* img = (byte*)t.GetImage();
        int w = t.GetWidth(), h = t.GetHeight(), bpp = t.GetBPP();
        if (w <= 0 || h <= 0) { why = Decline.EngineToo; return 0; }
        bool compressed = t.GetCompressedSize() > 0;
        int nx = Math.Max(t.GetNumX(), 1), ny = Math.Max(t.GetNumY(), 1);
        int lw = compressed ? w : w / nx, lh = compressed ? h : h / ny;
        if (lw > MaxTexSize() || lh > MaxTexSize()) { why = Decline.Oversize; return 0; }
        bool mip = (t.GetMipMap() & MipByte) != 0;
        Sampler s = EngineSampler(t.GetClamp(), t.GetFilter() != 0, mip);
        uint dst;
        DdsReader.Info dds = default;
        bool ddsFormat = img != null && DdsReader.Lookup(img, out dds) && !DdsReader.EngineUploads(dds.Format);
        if (ddsFormat)
            dst = UploadDdsFormat(img, dds, s, mip, bpp == 1);
        else if (img != null && compressed)
        {
            if (EngineFormat(t.GetCompressionFormat()) == 0) { why = Decline.EngineToo; return 0; }
            dst = UploadCompressed(t, w, h, s, mip, bpp == 1);
        }
        else
            dst = UploadRaw(t, img, cell, w, h, bpp, s, mip);
        if (ddsFormat && dst != 0)
        {
            if (err == 0) _ddsFormatUploads++;
            else if (++_ddsFormatFailed <= BcnFailCap)
                _log?.Always($"OwnedTextures: {dds.Format} {dds.W}x{dds.H} REFUSED by the driver (0x{err:x}) -- the engine's same-sized upload stands and draws as garbage");
        }
        if (err == 0 && dst != 0) { why = Decline.None; return dst; }
        if (dst != 0) VkTextures.Forget(dst);
        return 0;
    }

    /// <summary>The GL internal format <c>textureFormatTranslationTable</c> names for a compression-format index,
    /// or 0 -- which is what the entry itself holds for index 17 and what an index past the table reads as.</summary>
    private static uint EngineFormat(int fi) => (uint)fi < (uint)EngineFormats.Length ? EngineFormats[fi] : 0;

    /// <summary><c>GetMipMap()</c> answers a bool in its low byte.</summary>
    private const int MipByte = 0xff;   // not-an-offset: a byte mask

    /// <summary>The texel widths an engine image is transcribed from. 4/3/1 are byte channels as
    /// <c>GLRender::CreateTexture</c> @0x4ca2b0 names them; 2 is the four-bits-per-channel image
    /// <c>ImageRead16bit</c> @0x4b6a50 reads from a <c>.4pc</c> file, R,G,B,A from the high nibble down.</summary>
    private static bool RawBpp(int bpp) => bpp is 1 or 2 or 3 or 4;

    /// <summary>An uncompressed sheet: the cell is packed out of the sheet in place (the engine copies it out with
    /// <c>ImageGetSubImage</c>) and mipped on the device as the engine mips it. A null image goes up zeroed, as the
    /// engine's null upload leaves it: the only other write of a cell's storage is the GPU-PLT branch of
    /// <c>glImageBasic</c>, which Alloy holds off, so nothing fills it behind us.</summary>
    private static uint UploadRaw(CAurTexture t, byte* img, int cell, int w, int h, int bpp, in Sampler s, bool mip)
    {
        int nx = t.GetNumX(), ny = t.GetNumY();
        if (!RawBpp(bpp) || nx <= 0 || ny <= 0) return 0;
        int cw = w / nx, ch = h / ny;
        if (cw <= 0 || ch <= 0 || cell < 0 || cell >= nx * ny) return 0;
        uint n = VkTextures.NewName();
        MirrorRaw(n, img, w, (cell % nx) * cw, (cell / nx) * ch, cw, ch, bpp, s, mip);
        return VkTextures.Has(n) ? n : 0;
    }

    /// <summary>The stand-in for a shape the engine's own upload cannot produce pixels for either
    /// (<see cref="Decline.EngineToo"/>): an image of the cell's size, zeroed, one level and an unmipped sampler so
    /// it is COMPLETE and a bindless index may be taken on it. 0 = the device refused it.</summary>
    private static uint UploadEmpty(IntPtr tex)
    {
        var t = new CAurTexture(tex);
        int w = t.GetWidth(), h = t.GetHeight();
        int nx = Math.Max(t.GetNumX(), 1), ny = Math.Max(t.GetNumY(), 1);
        int cw = Math.Max(w / nx, 1), ch = Math.Max(h / ny, 1);
        if (cw > MaxTexSize() || ch > MaxTexSize()) return 0;
        Sampler s = EngineSampler(t.GetClamp(), t.GetFilter() != 0, mip: false);
        uint n = VkTextures.NewName();
        MirrorSolid(n, cw, ch, 0, VkSampler(s));
        return VkTextures.Has(n) ? n : 0;
    }

    /// <summary>A compressed image in a format the engine names: each level behind its own header run, sized
    /// by the texture's <c>GetCompressedSize(w, h)</c>, the whole chain when mipped and level 0 otherwise.</summary>
    private static uint UploadCompressed(CAurTexture t, int w, int h, in Sampler s, bool mip, bool red)
    {
        uint fmt = EngineFormat(t.GetCompressionFormat());
        if (fmt == 0) return 0;
        int levels = mip ? EngineLevels(w, h) : 1;
        uint n = VkTextures.NewName();
        MirrorCompressed(n, t, fmt, w, h, levels, s, red);
        return VkTextures.Has(n) ? n : 0;
    }

    /// <summary>A format the engine's table has no entry for (BC2, SNORM RGTC, BC6H, BC7), from the padded chain
    /// <see cref="DdsReader"/> parsed, under its real format; <see cref="UploadOwn"/> counts and names a refusal.</summary>
    private static uint UploadDdsFormat(byte* chain, in DdsReader.Info i, in Sampler s, bool mip, bool red)
    {
        int levels = mip ? Math.Min(Math.Max(i.Levels, 1), EngineLevels(i.W, i.H)) : 1;
        uint n = VkTextures.NewName();
        MirrorDds(n, chain, i, levels, s, red);
        return VkTextures.Has(n) ? n : 0;
    }

    private static string DdsFormatNote() => $", {_ddsFormatUploads} DDS-format uploads ({_ddsFormatFailed} REFUSED)";
}
