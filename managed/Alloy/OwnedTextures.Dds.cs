using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The upload of a DDS format the engine's format translation table has no entry for (BC2, SNORM RGTC, BC6H,
/// BC7): its <c>CreateTexture</c> runs cleanly on the matching channel count but labels the blocks DXT5/DXT1/RGTC,
/// so this respecifies the same chain under the real format in immutable storage, sampler state copied across.
/// ponytail: the engine's upload is a throwaway kept for its sampler state; transcribe its setter to skip it.
/// </summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>Alloy performs every 2D upload in the engine's place right now: the only state in which a format
    /// the engine cannot name may be handed to it.</summary>
    internal static bool OwnsUploads => _armed && _single && _origCreate != null && GlContextService.Available;

    private static long _ddsFormatUploads, _ddsFormatFailed;

    /// <summary>The engine's sampler state on <paramref name="src"/> onto <paramref name="dst"/>; returns its MAX_LEVEL.</summary>
    private static int CopySampler(uint src, uint dst)
    {
        int minF, magF, wrapS, wrapT, maxLevel, aniso;
        Gl.GetTextureParameteriv(src, Gl.TEXTURE_MIN_FILTER, &minF);
        Gl.GetTextureParameteriv(src, Gl.TEXTURE_MAG_FILTER, &magF);
        Gl.GetTextureParameteriv(src, Gl.TEXTURE_WRAP_S, &wrapS);
        Gl.GetTextureParameteriv(src, Gl.TEXTURE_WRAP_T, &wrapT);
        Gl.GetTextureParameteriv(src, Gl.TEXTURE_MAX_LEVEL, &maxLevel);
        Gl.GetTextureParameteriv(src, Gl.TEXTURE_MAX_ANISOTROPY, &aniso);
        Gl.TextureParameteri(dst, Gl.TEXTURE_MIN_FILTER, minF);
        Gl.TextureParameteri(dst, Gl.TEXTURE_MAG_FILTER, magF);
        Gl.TextureParameteri(dst, Gl.TEXTURE_WRAP_S, wrapS);
        Gl.TextureParameteri(dst, Gl.TEXTURE_WRAP_T, wrapT);
        if (aniso > 0) Gl.TextureParameteri(dst, Gl.TEXTURE_MAX_ANISOTROPY, aniso);
        return maxLevel;
    }

    /// <summary>The engine's swizzle (it sets R,R,R,1 on a one-channel texture) onto <paramref name="dst"/>.</summary>
    private static void CopySwizzle(uint src, uint dst)
    {
        foreach (uint p in (System.ReadOnlySpan<uint>)[Gl.TEXTURE_SWIZZLE_R, Gl.TEXTURE_SWIZZLE_G, Gl.TEXTURE_SWIZZLE_B, Gl.TEXTURE_SWIZZLE_A])
        {
            int v; Gl.GetTextureParameteriv(src, p, &v);
            Gl.TextureParameteri(dst, p, v);
        }
    }

    /// <summary>The chain at <paramref name="chain"/> (<paramref name="i"/>'s padded geometric chain) under its real
    /// format, as many levels as the engine defined on <paramref name="src"/>. 0 = the driver refused it (logged).</summary>
    private static uint UploadDdsFormat(uint src, byte* chain, in DdsReader.Info i, int etid)
    {
        uint fmt = DdsReader.GlFormat(i.Format);
        int levels = 0;
        for (int w; levels < i.Levels; levels++)
        {
            Gl.GetTextureLevelParameteriv(src, levels, Gl.TEXTURE_WIDTH, &w);
            if (w <= 0) break;
        }
        if (levels == 0) levels = 1;
        uint dst; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &dst);
        if (dst == 0) return 0;
        int svUnpackBuf; Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &svUnpackBuf);
        if (svUnpackBuf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
        Gl.DrainErrors(8);
        int maxLevel = CopySampler(src, dst);
        CopySwizzle(src, dst);
        Gl.TextureStorage2D(dst, levels, fmt, i.W, i.H);
        Gl.TextureParameteri(dst, Gl.TEXTURE_MAX_LEVEL, System.Math.Min(maxLevel, levels - 1));
        long off = 0;
        for (int l = 0, w = i.W, h = i.H; l < levels; l++, w = System.Math.Max(1, w >> 1), h = System.Math.Max(1, h >> 1))
        {
            int sz = DdsReader.LevelSize(w, h, i.Channels);
            fixed (byte* level = new ReadOnlySpan<byte>(chain, (int)i.ChainBytes).Slice((int)off, sz))
                Gl.CompressedTextureSubImage2D(dst, l, 0, 0, w, h, fmt, sz, level);
            off += sz;
        }
        uint err = Gl.GetError();
        if (svUnpackBuf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)svUnpackBuf);
        if (err == 0) { _ddsFormatUploads++; return dst; }
        Gl.DeleteTextures(1, &dst);
        if (++_ddsFormatFailed <= BcnFailCap)
            _log?.Always($"OwnedTextures: tid {etid} ({i.Format} {i.W}x{i.H}) REFUSED by the driver (0x{err:x}) -- the engine's same-sized upload stands and draws as garbage");
        return 0;
    }

    private static string DdsFormatNote() => $", {_ddsFormatUploads} DDS-format uploads ({_ddsFormatFailed} REFUSED)";
}
