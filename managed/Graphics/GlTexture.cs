namespace CX.Graphics;

/// <summary>Cold texture operations on whichever context is current -- the engine's at its upload funnel, Alloy's
/// inside the bracket -- so a caller outside the backend issues no GL itself.</summary>
public static unsafe class GlTexture
{
    /// <summary>GL 4.6's guaranteed <c>GL_MAX_TEXTURE_SIZE</c>, which is also what the engine falls back on
    /// (<c>GLRender::CreateTexture</c> takes 4096 when the query answers 0).</summary>
    private const int MinMaxSize = 16384;

    /// <summary>The widest level 0 a 2D texture may have on this driver. Never 0: a driver that answers nothing
    /// is read as the guaranteed minimum, so a caller compares against a real number.</summary>
    public static int MaxSize()
    {
        int v = 0;
        if (Gl.GetIntegerv != null) Gl.GetIntegerv(Gl.MAX_TEXTURE_SIZE, &v);
        return v > 0 ? v : MinMaxSize;
    }

    /// <summary>A freshly created <paramref name="tex"/> back with level 0 zeroed, or 0 with the name DELETED:
    /// immutable storage reads undefined until something writes it, and <c>glClearTexImage</c> is 4.4 core, so a
    /// driver without it is a refusal rather than a reason to hand back undefined contents.</summary>
    public static uint KeepZeroed(uint tex, uint fmt, uint type)
    {
        if (Gl.ClearTexImage != null) { Gl.ClearTexImage(tex, 0, fmt, type, null); return tex; }
        Gl.DeleteTextures(1, &tex);
        return 0;
    }

    /// <summary>Level 0 of 2D texture <paramref name="tex"/> as tightly packed RGBA8, with its filters and wraps (GL
    /// enums), in the current context; null when the name holds no level 0 or GL refuses a query. A synchronous
    /// readback: cold, once per texture.</summary>
    public static byte[]? ReadRgba8(uint tex, int maxSide, out int w, out int h, out int min, out int mag, out int wrapS, out int wrapT)
    {
        w = h = min = mag = wrapS = wrapT = 0;
        if (Gl.GetTextureImage == null || Gl.GetTextureLevelParameteriv == null || Gl.GetTextureParameteriv == null) return null;
        int lw = 0, lh = 0, lmin = 0, lmag = 0, ls = 0, lt = 0;
        Gl.DrainErrors();
        Gl.GetTextureLevelParameteriv(tex, 0, Gl.TEXTURE_WIDTH, &lw);
        Gl.GetTextureLevelParameteriv(tex, 0, Gl.TEXTURE_HEIGHT, &lh);
        Gl.GetTextureParameteriv(tex, Gl.TEXTURE_MIN_FILTER, &lmin);
        Gl.GetTextureParameteriv(tex, Gl.TEXTURE_MAG_FILTER, &lmag);
        Gl.GetTextureParameteriv(tex, Gl.TEXTURE_WRAP_S, &ls);
        Gl.GetTextureParameteriv(tex, Gl.TEXTURE_WRAP_T, &lt);
        w = lw; h = lh; min = lmin; mag = lmag; wrapS = ls; wrapT = lt;
        if (Gl.DrainErrors() != 0 || lw <= 0 || lh <= 0 || lw > maxSide || lh > maxSide) return null;
        byte[] px = new byte[lw * lh * 4];
        fixed (byte* p = px) Gl.GetTextureImage(tex, 0, Gl.RGBA, Gl.UNSIGNED_BYTE, px.Length, p);
        return Gl.DrainErrors() == 0 ? px : null;
    }
}
