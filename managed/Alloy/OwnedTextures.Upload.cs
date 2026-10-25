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

    /// <summary>Cell <paramref name="cell"/> of <paramref name="tex"/> into a texture of Alloy's own, as the engine
    /// would have uploaded it. 0 = the driver refused it (<paramref name="err"/>), the engine's format table has no
    /// entry for it, or level 0 is past <c>GL_MAX_TEXTURE_SIZE</c> (<paramref name="oversize"/>) -- the one of those
    /// the engine's own upload does better, by box-filtering the sheet down.</summary>
    private static uint UploadOwn(IntPtr tex, int cell, out uint err, out bool oversize)
    {
        err = 0; oversize = false;
        var t = new CAurTexture(tex);
        byte* img = (byte*)t.GetImage();
        int w = t.GetWidth(), h = t.GetHeight(), bpp = t.GetBPP();
        if (w <= 0 || h <= 0) return 0;
        bool compressed = t.GetCompressedSize() > 0;
        int nx = Math.Max(t.GetNumX(), 1), ny = Math.Max(t.GetNumY(), 1);
        int lw = compressed ? w : w / nx, lh = compressed ? h : h / ny;
        if (lw > MaxTexSize() || lh > MaxTexSize()) { oversize = true; return 0; }
        bool mip = (t.GetMipMap() & MipByte) != 0;
        Sampler s = EngineSampler(t.GetClamp(), t.GetFilter() != 0, mip);
        Gl.DrainErrors(8);   // the verdict is ours to read, so nobody else's error may be sitting here
        uint dst;
        DdsReader.Info dds = default;
        bool ddsFormat = img != null && DdsReader.Lookup(img, out dds) && !DdsReader.EngineUploads(dds.Format);
        if (ddsFormat)
            dst = UploadDdsFormat(img, dds, s, mip, bpp == 1);
        else if (img != null && compressed)
            dst = UploadCompressed(t, w, h, s, mip, bpp == 1);
        else
            dst = UploadRaw(t, img, cell, w, h, bpp, s, mip);
        err = Gl.GetError();
        if (ddsFormat && dst != 0)
        {
            if (err == 0) _ddsFormatUploads++;
            else if (++_ddsFormatFailed <= BcnFailCap)
                _log?.Always($"OwnedTextures: {dds.Format} {dds.W}x{dds.H} REFUSED by the driver (0x{err:x}) -- the engine's same-sized upload stands and draws as garbage");
        }
        if (err == 0) return dst;
        if (dst != 0) Gl.DeleteTextures(1, &dst);
        return 0;
    }

    /// <summary><c>GetMipMap()</c> answers a bool in its low byte.</summary>
    private const int MipByte = 0xff;   // not-an-offset: a byte mask

    /// <summary>The storage, format and packed type an engine image of <paramref name="bpp"/> bytes per texel is
    /// read through. 4/3/1 are byte channels as <c>GLRender::CreateTexture</c> @0x4ca2b0 names them; 2 is the
    /// four-bits-per-channel image <c>ImageRead16bit</c> @0x4b6a50 reads from a <c>.4pc</c> file, R,G,B,A from the
    /// high nibble down -- GL's 4_4_4_4 exactly, which the engine pairs with GL_RG and so uploads nowhere.</summary>
    private static bool RawFormat(int bpp, out uint sized, out uint fmt, out uint type)
    {
        type = Gl.UNSIGNED_BYTE;
        switch (bpp)
        {
            case 4: sized = Gl.RGBA8; fmt = Gl.RGBA; return true;
            case 3: sized = Gl.RGB8; fmt = Gl.RGB; return true;
            case 2: sized = Gl.RGBA4; fmt = Gl.RGBA; type = Gl.UNSIGNED_SHORT_4_4_4_4; return true;
            case 1: sized = Gl.R8; fmt = Gl.RED; return true;
            default: sized = fmt = 0; return false;
        }
    }

    /// <summary>An uncompressed sheet: the cell is read in place through the unpack rectangle (the engine copies
    /// it out with <c>ImageGetSubImage</c>), mips by <c>glGenerateMipmap</c> as the engine's are. A null image gets
    /// the storage the engine's <c>glTexImage2D(..., nullptr)</c> leaves, zeroed so it reads the same every run.</summary>
    private static uint UploadRaw(CAurTexture t, byte* img, int cell, int w, int h, int bpp, in Sampler s, bool mip)
    {
        int nx = t.GetNumX(), ny = t.GetNumY();
        if (!RawFormat(bpp, out uint sized, out uint fmt, out uint type) || nx <= 0 || ny <= 0) return 0;
        int cw = w / nx, ch = h / ny;
        if (cw <= 0 || ch <= 0 || cell < 0 || cell >= nx * ny) return 0;
        uint dst = Storage(Gl.TEXTURE_2D, mip ? EngineLevels(cw, ch) : 1, sized, cw, ch, s, bpp == 1);
        if (dst == 0) return 0;
        if (img == null) { GlTexture.ZeroFill(dst, fmt, type); return dst; }
        var u = Unpack.Take(w, (cell % nx) * cw, (cell / nx) * ch);
        Gl.TextureSubImage2D(dst, 0, 0, 0, cw, ch, fmt, type, img);
        u.Give();
        if (mip) Gl.GenerateTextureMipmap(dst);
        return dst;
    }

    /// <summary>The stand-in for a shape Alloy declines and the engine's own upload fails on as well: storage of
    /// the right size and no pixels, one level and an unmipped sampler so the texture is COMPLETE (a bindless
    /// handle may be minted on it) rather than the storage-less name a failed <c>glTexImage2D</c> leaves. 0 = the
    /// driver would not give a name at all.</summary>
    private static uint UploadEmpty(IntPtr tex)
    {
        var t = new CAurTexture(tex);
        int w = t.GetWidth(), h = t.GetHeight();
        int nx = Math.Max(t.GetNumX(), 1), ny = Math.Max(t.GetNumY(), 1);
        int cw = Math.Max(w / nx, 1), ch = Math.Max(h / ny, 1);
        if (cw > MaxTexSize() || ch > MaxTexSize()) return 0;
        Sampler s = EngineSampler(t.GetClamp(), t.GetFilter() != 0, mip: false);
        uint dst = Storage(Gl.TEXTURE_2D, 1, Gl.RGBA8, cw, ch, s, red: false);
        // Immutable storage starts UNDEFINED, which on screen is whatever the driver last had there; zeroed, it
        // is transparent black every time, and the loud line beside it is what says so.
        if (dst != 0) GlTexture.ZeroFill(dst, Gl.RGBA, Gl.UNSIGNED_BYTE);
        return dst;
    }

    /// <summary>A compressed image in a format the engine names: each level behind its own header run, sized
    /// by the texture's <c>GetCompressedSize(w, h)</c>, the whole chain when mipped and level 0 otherwise.</summary>
    private static uint UploadCompressed(CAurTexture t, int w, int h, in Sampler s, bool mip, bool red)
    {
        int fi = t.GetCompressionFormat();
        uint fmt = (uint)fi < (uint)EngineFormats.Length ? EngineFormats[fi] : 0;
        if (fmt == 0) return 0;
        int levels = mip ? EngineLevels(w, h) : 1;
        uint dst = Storage(Gl.TEXTURE_2D, levels, fmt, w, h, s, red);
        if (dst == 0) return 0;
        var u = Unpack.NoBuffer();
        for (int l = 0; l < levels; l++)
        {
            byte* level = t.CompressedLevel(l, out int lw, out int lh, out int size);
            Gl.CompressedTextureSubImage2D(dst, l, 0, 0, lw, lh, fmt, size, level);
        }
        u.Give();
        return dst;
    }

    /// <summary>A format the engine's table has no entry for (BC2, SNORM RGTC, BC6H, BC7), from the padded chain
    /// <see cref="DdsReader"/> parsed, under its real format; <see cref="UploadOwn"/> counts and names a refusal.</summary>
    private static uint UploadDdsFormat(byte* chain, in DdsReader.Info i, in Sampler s, bool mip, bool red)
    {
        uint fmt = DdsReader.GlFormat(i.Format);
        int levels = mip ? Math.Min(Math.Max(i.Levels, 1), EngineLevels(i.W, i.H)) : 1;
        uint dst = Storage(Gl.TEXTURE_2D, levels, fmt, i.W, i.H, s, red);
        if (dst == 0) return 0;
        var u = Unpack.NoBuffer();
        long off = 0;
        for (int l = 0, w = i.W, h = i.H; l < levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
        {
            int sz = DdsReader.LevelSize(w, h, i.Channels);
            fixed (byte* level = new ReadOnlySpan<byte>(chain, (int)i.ChainBytes).Slice((int)off, sz))
                Gl.CompressedTextureSubImage2D(dst, l, 0, 0, w, h, fmt, sz, level);
            off += sz;
        }
        u.Give();
        return dst;
    }

    /// <summary>A fresh immutable texture with the engine's sampler and, for one channel, its R,R,R,1 swizzle.</summary>
    private static uint Storage(uint target, int levels, uint fmt, int w, int h, in Sampler s, bool red)
    {
        uint dst; Gl.CreateTextures(target, 1, &dst);
        if (dst == 0) return 0;
        Gl.TextureStorage2D(dst, levels, fmt, w, h);
        Gl.TextureParameteri(dst, Gl.TEXTURE_MIN_FILTER, s.Min);
        Gl.TextureParameteri(dst, Gl.TEXTURE_MAG_FILTER, s.Mag);
        Gl.TextureParameteri(dst, Gl.TEXTURE_WRAP_S, s.WrapS);
        Gl.TextureParameteri(dst, Gl.TEXTURE_WRAP_T, s.WrapT);
        Gl.TextureParameteri(dst, Gl.TEXTURE_WRAP_R, s.WrapR);   // ignored by a 2D texture
        Gl.TextureParameteri(dst, Gl.TEXTURE_MAX_ANISOTROPY, s.Aniso ? TexRegistry.Aniso() : 1);
        if (red)
        {
            Gl.TextureParameteri(dst, Gl.TEXTURE_SWIZZLE_G, (int)Gl.RED);
            Gl.TextureParameteri(dst, Gl.TEXTURE_SWIZZLE_B, (int)Gl.RED);
            Gl.TextureParameteri(dst, Gl.TEXTURE_SWIZZLE_A, (int)Gl.ONE);
        }
        return dst;
    }

    /// <summary>A context's unpack state, set for one upload and put back as it was: no unpack buffer (it
    /// would turn the pointer into an offset), rows at <paramref name="align"/>, and the cell's rectangle in
    /// the sheet. The engine's context here; Alloy's own in <see cref="MovieFrame"/>.</summary>
    internal struct Unpack
    {
        private int _buf, _align, _row, _skipPx, _skipRows;

        internal static Unpack Take(int rowLength, int skipPixels, int skipRows, int align = 1)
        {
            Unpack u = default;
            Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &u._buf);
            Gl.GetIntegerv(Gl.UNPACK_ALIGNMENT, &u._align);
            Gl.GetIntegerv(Gl.UNPACK_ROW_LENGTH, &u._row);
            Gl.GetIntegerv(Gl.UNPACK_SKIP_PIXELS, &u._skipPx);
            Gl.GetIntegerv(Gl.UNPACK_SKIP_ROWS, &u._skipRows);
            if (u._buf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
            Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, align);
            Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, rowLength);
            Gl.PixelStorei(Gl.UNPACK_SKIP_PIXELS, skipPixels);
            Gl.PixelStorei(Gl.UNPACK_SKIP_ROWS, skipRows);
            return u;
        }

        /// <summary>Just the unpack buffer out of the way, for a compressed upload the rectangle does not apply to.</summary>
        internal static Unpack NoBuffer() => Take(0, 0, 0);

        internal readonly void Give()
        {
            Gl.PixelStorei(Gl.UNPACK_ALIGNMENT, _align);
            Gl.PixelStorei(Gl.UNPACK_ROW_LENGTH, _row);
            Gl.PixelStorei(Gl.UNPACK_SKIP_PIXELS, _skipPx);
            Gl.PixelStorei(Gl.UNPACK_SKIP_ROWS, _skipRows);
            if (_buf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)_buf);
        }
    }

    private static string DdsFormatNote() => $", {_ddsFormatUploads} DDS-format uploads ({_ddsFormatFailed} REFUSED)";
}
