using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Graphics;

/// <summary>One throwaway draw of an already-linked program, so the driver spends whatever it defers to a
/// program's first use here. An NVIDIA program loaded from a binary (<see cref="ProgramBinary"/>) finishes its
/// code on that first draw, which is seconds on a cold cache.</summary>
internal static unsafe class ProgramWarm
{
    /// <summary>Draw <paramref name="program"/> once with <paramref name="vao"/>'s vertex input, inside an empty
    /// scissor box so nothing is rasterised: the point is the draw CALL. Sets the blend enable and pair, the
    /// depth test, function and write, culling, winding, the depth bias and alpha-to-coverage, puts the scissor
    /// box and the enables it moved back, and returns the GL errors the draw raised for the caller.</summary>
    internal static int Draw(uint program, uint vao, in DepthState depth, in RasterState raster,
                             in AttachmentBlend blend, bool coverage, float biasFactor, float biasUnits)
    {
        if (Gl.DrawArrays == null || Gl.Scissor == null || program == 0) return 0;
        Gl.DrainErrors();   // whatever the frame left behind is not this draw's
        int prev;
        Gl.GetIntegerv(Gl.CURRENT_PROGRAM, &prev);
        int* box = stackalloc int[4];
        Gl.GetIntegerv(Gl.SCISSOR_BOX, box);
        byte hadScissor = Was(Gl.SCISSOR_TEST), hadDepth = Was(Gl.DEPTH_TEST), hadCull = Was(Gl.CULL_FACE);
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
        if (depth.Test) Gl.Enable(Gl.DEPTH_TEST); else Gl.Disable(Gl.DEPTH_TEST);
        Gl.DepthMask(depth.Write ? (byte)1 : (byte)0);
        Gl.DepthFunc(GlDevice.Compare(depth.Compare));
        if (raster.Cull == CullMode.None) Gl.Disable(Gl.CULL_FACE);
        else { Gl.Enable(Gl.CULL_FACE); Gl.CullFace(raster.Cull == CullMode.Front ? Gl.FRONT : Gl.BACK); }
        Gl.FrontFace(raster.FrontCw ? Gl.CW : Gl.CCW);
        bool bias = raster.DepthBias && Gl.PolygonOffset != null;
        if (bias) { Gl.PolygonOffset(biasFactor, biasUnits); Gl.Enable(Gl.POLYGON_OFFSET_FILL); }
        if (coverage) Gl.Enable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        int errors = Gl.DrainErrors();
        if (coverage) Gl.Disable(Gl.SAMPLE_ALPHA_TO_COVERAGE);
        // the bias constants back to GL's own defaults: the passes that use them set their own on entry
        if (bias) { Gl.Disable(Gl.POLYGON_OFFSET_FILL); Gl.PolygonOffset(0f, 0f); }
        Gl.Disable(Gl.BLEND);
        Gl.DepthMask(1);
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.FrontFace(Gl.CCW);
        Gl.CullFace(Gl.BACK);
        Restore(Gl.CULL_FACE, hadCull);
        Restore(Gl.DEPTH_TEST, hadDepth);
        Gl.Scissor(box[0], box[1], box[2], box[3]);
        Restore(Gl.SCISSOR_TEST, hadScissor);
        Gl.BindVertexArray(0);
        Gl.UseProgram((uint)prev);
        return errors;
    }

    /// <summary>Is <paramref name="cap"/> on? On a driver with no <c>glIsEnabled</c> the answer is the state the
    /// frame's preamble leaves depth and culling in, and scissor is then put back off.</summary>
    private static byte Was(uint cap) => Gl.IsEnabled == null ? (cap == Gl.SCISSOR_TEST ? (byte)0 : (byte)1)
                                                              : Gl.IsEnabled(cap);

    private static void Restore(uint cap, byte on)
    {
        if (on != 0) Gl.Enable(cap);
        else Gl.Disable(cap);
    }
}
