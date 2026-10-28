namespace CX.Graphics;

/// <summary>Cold diagnostic reads of whichever context is current, so a caller outside the backend issues no GL itself.</summary>
public static unsafe class GlProbe
{
    /// <summary>The bound program, or 0 before the loader has run.</summary>
    public static int CurrentProgram()
    {
        int prog = 0;
        if (Gl.GetIntegerv != null) Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prog);
        return prog;
    }

    /// <summary>The RGBA pixel at the viewport's centre of framebuffer 0, or 0 when unloaded or the viewport is tiny.
    /// Stalls the pipeline.</summary>
    public static uint ScreenCentre()
    {
        if (Gl.ReadPixels == null) return 0;
        int* vp = stackalloc int[4];
        Gl.GetIntegerv(Gl.VIEWPORT, vp);
        if (vp[2] <= 16 || vp[3] <= 16) return 0;
        uint px = 0;
        int prev; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prev);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        Gl.ReadPixels(vp[0] + vp[2] / 2, vp[1] + vp[3] / 2, 1, 1, EngineTokens.RGBA, EngineTokens.UNSIGNED_BYTE, &px);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)prev);
        Gl.DrainErrors(4);
        return px;
    }
}
