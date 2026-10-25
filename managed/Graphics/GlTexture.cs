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
}
