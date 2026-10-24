namespace CX.Engine;

public readonly unsafe ref partial struct CAurTexture
{
    /// <summary>Level <paramref name="level"/> of a compressed image as <c>GLRender::CreateTexture</c> @0x4ca2b0 walks
    /// it: each level behind its own header run, level 0 sized by <c>GetCompressedSize()</c> and the rest by
    /// <c>GetCompressedSize(w, h)</c>. Returns its first byte, with its shape and byte size.</summary>
    public byte* CompressedLevel(int level, out int w, out int h, out int size)
    {
        byte* p = (byte*)GetImage();
        int hdr = GetNumMipHeaderBytes();
        w = GetWidth(); h = GetHeight(); size = GetCompressedSize();
        p += hdr;
        for (int l = 0; l < level; l++)
        {
            p += size + hdr;
            w = System.Math.Max(1, w >> 1); h = System.Math.Max(1, h >> 1);
            size = GetLevelSize(w, h);
        }
        return p;
    }

    /// <summary>An uncompressed image's pixels as <c>GLRender::CreateCubeTexture</c> @0x4caab0 reads them:
    /// <c>GetWidth() * GetHeight() * GetBPP()</c> bytes from <c>GetImage()</c>; empty when there is none.</summary>
    public System.ReadOnlySpan<byte> Pixels
    {
        get
        {
            byte* p = (byte*)GetImage();
            int w = GetWidth(), h = GetHeight(), bpp = GetBPP();
            return p == null || w <= 0 || h <= 0 || bpp <= 0 ? default : new System.ReadOnlySpan<byte>(p, w * h * bpp);
        }
    }
}
