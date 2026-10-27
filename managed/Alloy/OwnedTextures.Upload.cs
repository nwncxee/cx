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
        if (dst != 0) { VkTextures.Forget(dst); if (!Renderer.OnVulkan) Gl.DeleteTextures(1, &dst); }
        return 0;
    }

    /// <summary>The GL internal format <c>textureFormatTranslationTable</c> names for a compression-format index,
    /// or 0 -- which is what the entry itself holds for index 17 and what an index past the table reads as.</summary>
    private static uint EngineFormat(int fi) => (uint)fi < (uint)EngineFormats.Length ? EngineFormats[fi] : 0;

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

    /// <summary>An uncompressed sheet: the cell is read in place through the unpack rectangle (the engine copies it
    /// out with <c>ImageGetSubImage</c>), mips by <c>glGenerateMipmap</c> as the engine's are. A null image gets
    /// zeroed storage and keeps it: the only other GL that writes a cell's storage is the GPU-PLT branch of
    /// <c>glImageBasic</c>, which Alloy holds off, so nothing fills it behind us.</summary>
    private static uint UploadRaw(CAurTexture t, byte* img, int cell, int w, int h, int bpp, in Sampler s, bool mip)
    {
        int nx = t.GetNumX(), ny = t.GetNumY();
        if (!RawFormat(bpp, out uint sized, out uint fmt, out uint type) || nx <= 0 || ny <= 0) return 0;
        int cw = w / nx, ch = h / ny;
        if (cw <= 0 || ch <= 0 || cell < 0 || cell >= nx * ny) return 0;
        if (Renderer.OnVulkan)
        {
            uint n = VkTextures.NewName();
            MirrorRaw(n, img, w, (cell % nx) * cw, (cell / nx) * ch, cw, ch, bpp, s, mip);
            return VkTextures.Has(n) ? n : 0;
        }
        uint dst = Storage(Gl.TEXTURE_2D, mip ? EngineLevels(cw, ch) : 1, sized, cw, ch, s, bpp == 1);
        if (dst == 0) return 0;
        MirrorRaw(dst, img, w, (cell % nx) * cw, (cell / nx) * ch, cw, ch, bpp, s, mip);
        if (img == null) return GlTexture.KeepZeroed(dst, fmt, type);   // no pixels: zeroed storage, as the engine's null upload leaves
        var u = Unpack.Take(w, (cell % nx) * cw, (cell / nx) * ch);
        Gl.TextureSubImage2D(dst, 0, 0, 0, cw, ch, fmt, type, img);
        u.Give();
        if (mip) Gl.GenerateTextureMipmap(dst);
        return dst;
    }

    /// <summary>The stand-in for a shape the engine's own upload cannot produce pixels for either
    /// (<see cref="Decline.EngineToo"/>): storage of the cell's size, zeroed, one level and an unmipped sampler so
    /// the texture is COMPLETE -- a bindless handle may be minted on it, unlike the storage-less name a failed
    /// <c>glTexImage2D</c> leaves. 0 = the driver gave no name, or would not clear it.</summary>
    private static uint UploadEmpty(IntPtr tex)
    {
        var t = new CAurTexture(tex);
        int w = t.GetWidth(), h = t.GetHeight();
        int nx = Math.Max(t.GetNumX(), 1), ny = Math.Max(t.GetNumY(), 1);
        int cw = Math.Max(w / nx, 1), ch = Math.Max(h / ny, 1);
        if (cw > MaxTexSize() || ch > MaxTexSize()) return 0;
        Sampler s = EngineSampler(t.GetClamp(), t.GetFilter() != 0, mip: false);
        if (Renderer.OnVulkan)
        {
            uint n = VkTextures.NewName();
            MirrorSolid(n, cw, ch, 0, VkSampler(s));
            return VkTextures.Has(n) ? n : 0;
        }
        uint dst = Storage(Gl.TEXTURE_2D, 1, Gl.RGBA8, cw, ch, s, red: false);
        if (dst != 0) MirrorSolid(dst, cw, ch, 0, VkSampler(s));
        return dst == 0 ? 0 : GlTexture.KeepZeroed(dst, Gl.RGBA, Gl.UNSIGNED_BYTE);
    }

    /// <summary>A compressed image in a format the engine names: each level behind its own header run, sized
    /// by the texture's <c>GetCompressedSize(w, h)</c>, the whole chain when mipped and level 0 otherwise.</summary>
    private static uint UploadCompressed(CAurTexture t, int w, int h, in Sampler s, bool mip, bool red)
    {
        uint fmt = EngineFormat(t.GetCompressionFormat());
        if (fmt == 0) return 0;
        int levels = mip ? EngineLevels(w, h) : 1;
        if (Renderer.OnVulkan)
        {
            uint n = VkTextures.NewName();
            MirrorCompressed(n, t, fmt, w, h, levels, s, red);
            return VkTextures.Has(n) ? n : 0;
        }
        uint dst = Storage(Gl.TEXTURE_2D, levels, fmt, w, h, s, red);
        if (dst == 0) return 0;
        var u = Unpack.NoBuffer();
        for (int l = 0; l < levels; l++)
        {
            byte* level = t.CompressedLevel(l, out int lw, out int lh, out int size);
            Gl.CompressedTextureSubImage2D(dst, l, 0, 0, lw, lh, fmt, size, level);
        }
        u.Give();
        MirrorCompressed(dst, t, fmt, w, h, levels, s, red);
        return dst;
    }

    /// <summary>A format the engine's table has no entry for (BC2, SNORM RGTC, BC6H, BC7), from the padded chain
    /// <see cref="DdsReader"/> parsed, under its real format; <see cref="UploadOwn"/> counts and names a refusal.</summary>
    private static uint UploadDdsFormat(byte* chain, in DdsReader.Info i, in Sampler s, bool mip, bool red)
    {
        uint fmt = DdsReader.GlFormat(i.Format);
        int levels = mip ? Math.Min(Math.Max(i.Levels, 1), EngineLevels(i.W, i.H)) : 1;
        if (Renderer.OnVulkan)
        {
            uint n = VkTextures.NewName();
            MirrorDds(n, chain, i, levels, s, red);
            return VkTextures.Has(n) ? n : 0;
        }
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
        MirrorDds(dst, chain, i, levels, s, red);
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
