using System;

namespace CX.Graphics;

/// <summary>GL's half of the Vulkan A/B (<c>VkParity</c>): zero blocks on the points a Vulkan bind leaves on its zero
/// stand-ins, and a whole texture level read back into words. One dev frame at a time; it stalls that frame.</summary>
internal static unsafe class GlParity
{
    private sealed class ParityGlState
    {
        /// <summary>A buffer of zeros. ponytail: made once and never freed -- the context's end takes it.</summary>
        public uint Zero;

        /// <summary>The mappable copy <see cref="Sum"/> reads through, and its size. ponytail: never freed, as above.</summary>
        public uint Read;
        public long ReadBytes;
    }

    private static ParityGlState _s = new();

    /// <summary>Past every block a lit stage declares, as Vulkan's zero storage stand-in is.</summary>
    private const int ZeroBytes = 1 << 20;

    /// <summary>Point every storage binding in [<paramref name="ssboFrom"/>, <paramref name="ssboTo"/>) and every uniform
    /// binding in [<paramref name="uboFrom"/>, <paramref name="uboTo"/>) but <paramref name="uboKeep"/> at zeros.</summary>
    internal static void ZeroBlocks(uint ssboFrom, uint ssboTo, uint uboFrom, uint uboTo, uint uboKeep)
    {
        if (_s.Zero == 0)
        {
            uint b;
            Gl.CreateBuffers(1, &b);
            Gl.NamedBufferData(b, (IntPtr)ZeroBytes, null, Gl.STATIC_DRAW);
            Gl.ZeroBuffer(b, ZeroBytes);
            _s.Zero = b;
        }
        for (uint p = ssboFrom; p < ssboTo; p++) Gl.BindBufferBase(Gl.SHADER_STORAGE_BUFFER, p, _s.Zero);
        for (uint p = uboFrom; p < uboTo; p++) if (p != uboKeep) Gl.BindBufferBase(Gl.UNIFORM_BUFFER, p, _s.Zero);
    }

    /// <summary>Level 0 of <paramref name="tex"/> as (<paramref name="format"/>, <paramref name="type"/>), cropped to the
    /// <paramref name="w"/> x <paramref name="h"/> rectangle at (<paramref name="x"/>, <paramref name="y"/>) in
    /// <paramref name="words"/> words a texel, row 0 first; null when the texture is smaller than the rectangle.</summary>
    internal static uint[]? Read(uint tex, uint format, uint type, int x, int y, int w, int h, int words)
    {
        if (tex == 0 || Gl.GetTextureImage == null || Gl.GetTextureLevelParameteriv == null) return null;
        int tw, th;
        Gl.GetTextureLevelParameteriv(tex, 0, Gl.TEXTURE_WIDTH, &tw);
        Gl.GetTextureLevelParameteriv(tex, 0, Gl.TEXTURE_HEIGHT, &th);
        if (tw < x + w || th < y + h) return null;
        var all = new uint[(long)tw * th * words];
        Gl.PixelStorei(Gl.PACK_ALIGNMENT, 1);
        fixed (uint* p = all) Gl.GetTextureImage(tex, 0, format, type, all.Length * sizeof(uint), p);
        var crop = new uint[(long)w * h * words];
        for (int r = 0; r < h; r++)
            Array.Copy(all, ((long)(y + r) * tw + x) * words, crop, (long)r * w * words, (long)w * words);
        return crop;
    }

    /// <summary>The sum of the first <paramref name="n"/> words of <paramref name="buf"/>, read back by a full stall.</summary>
    internal static long Sum(uint buf, int n)
    {
        long bytes = (long)n * sizeof(uint);
        if (buf == 0 || n <= 0 || Gl.CopyNamedBufferSubData == null || Gl.MapNamedBufferRange == null) return -1;
        if (_s.ReadBytes < bytes)
        {
            uint b;
            Gl.CreateBuffers(1, &b);
            Gl.NamedBufferStorage(b, (IntPtr)bytes, null, Gl.MAP_READ_BIT);
            _s.Read = b; _s.ReadBytes = bytes;
        }
        Gl.CopyNamedBufferSubData(buf, _s.Read, IntPtr.Zero, IntPtr.Zero, (IntPtr)bytes);
        Gl.Finish();
        var p = (uint*)Gl.MapNamedBufferRange(_s.Read, IntPtr.Zero, (IntPtr)bytes, Gl.MAP_READ_BIT);
        if (p == null) return -1;
        long sum = 0;
        for (int i = 0; i < n; i++) sum += p[i];
        Gl.UnmapNamedBuffer(_s.Read);
        return sum;
    }
}
