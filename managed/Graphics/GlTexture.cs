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

    /// <summary>Level 0 of <paramref name="tex"/> to zeros. False = <c>glClearTexImage</c> (4.4) did not load, so
    /// the caller's storage keeps whatever the driver had there.</summary>
    public static bool ZeroFill(uint tex, uint fmt, uint type)
    {
        if (Gl.ClearTexImage == null) return false;
        Gl.ClearTexImage(tex, 0, fmt, type, null);
        return true;
    }
}
