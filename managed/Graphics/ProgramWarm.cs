using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Graphics;

/// <summary>One throwaway draw of an already-linked program under the state a real draw of it would set, so the
/// driver spends whatever it defers to a program's first use here. An NVIDIA program loaded from a binary
/// (<see cref="ProgramBinary"/>) finishes its code on that first draw, which is seconds on a cold cache.</summary>
internal static unsafe class ProgramWarm
{
    /// <summary>Draw <paramref name="program"/> once with <paramref name="vao"/>'s vertex input and the given
    /// state. The scissor box is empty, so nothing is rasterised: the point is the draw CALL. State is left as
    /// Alloy's frame preamble sets it, and the GL error the draw may raise is drained, not the frame's to carry.</summary>
    internal static void Draw(uint program, uint vao, in DepthState depth, in RasterState raster,
                              in AttachmentBlend blend, bool coverage, float biasFactor, float biasUnits)
    {
        if (Gl.DrawArrays == null || Gl.Scissor == null || program == 0) return;
        int prev;
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        Gl.Enable(Gl.SCISSOR_TEST);
        Gl.Scissor(0, 0, 0, 0);
        Gl.UseProgram(program);
        Gl.BindVertexArray(vao);
        if (blend.Enable)
        {
            Gl.Enable(Gl.BLEND);
            Gl.BlendFunc(GlDevice.Factor(blend.SrcRgb), GlDevice.Factor(blend.DstRgb));
        }
        else Gl.Disable(Gl.BLEND);
        Gl.DepthMask(depth.Write ? (byte)1 : (byte)0);
        Gl.DepthFunc(GlDevice.Compare(depth.Compare));
        bool noCull = raster.Cull == CullMode.None;
        if (noCull) Gl.Disable(Gl.CULL_FACE);
        bool bias = raster.DepthBias && Gl.PolygonOffset != null;
        if (bias) { Gl.PolygonOffset(biasFactor, biasUnits); Gl.Enable(Gl.POLYGON_OFFSET_FILL); }
        if (coverage) Gl.Enable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        if (coverage) Gl.Disable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        if (bias) Gl.Disable(Gl.POLYGON_OFFSET_FILL);
        if (noCull) { Gl.Enable(Gl.CULL_FACE); Gl.CullFace(Gl.BACK); }
        Gl.Disable(Gl.BLEND);
        Gl.DepthMask(1);
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.Disable(Gl.SCISSOR_TEST);
        Gl.BindVertexArray(0);
        Gl.UseProgram((uint)prev);
        Gl.DrainErrors();
    }
}
