using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The vis-ID target: alongside the depth prepass the opaque range writes a second attachment, one
/// <c>R32UI</c> per render pixel packing the RECORD slot (stable across frames, not the per-pass draw index
/// the cull reorders) and <c>gl_PrimitiveID</c>. Zero is "no owned opaque draw" and coverage is the prepass's.
/// ponytail: 18 bits of slot, 14 of primitive -- a draw past 16383 triangles wraps; RG32UI is the upgrade.
/// </summary>
internal static class IdTarget
{
    /// <summary>Bits the primitive id keeps below the slot.</summary>
    internal const int PrimBits = 14;
    /// <summary><c>/alloy viz</c> mode that paints the target.</summary>
    internal const int VizMode = 40;
    /// <summary><c>/alloy idtarget</c>: is the second attachment written at all (default on).</summary>
    internal static bool On => Renderer.IdTarget.V;

    /// <summary>The GLSL both halves share: the fragment output and the pack. Concatenated into every depth
    /// fragment stage so the three write the identical word.</summary>
    internal static string OutDecl => ShaderSource.Get("cx_idout.glsl");

    /// <summary>C# twin of <c>CxVisId</c>, for the self-check and a later CPU readback.</summary>
    internal static uint Pack(int slot, int prim) => ((uint)(slot + 1) << PrimBits) | ((uint)prim & ((1u << PrimBits) - 1));
    internal static int Slot(uint id) => (int)(id >> PrimBits) - 1;
    internal static int Prim(uint id) => (int)(id & ((1u << PrimBits) - 1));

    /// <summary>Integer hash (lowbias32) of the whole word to rgb: neighbouring triangles land on unrelated
    /// colours, the same triangle on the same colour every frame. Black = no owned opaque draw.</summary>
    internal static string VizFs => ShaderSource.Get("cx_idviz_f.glsl");

    /// <summary>Off-device: the pack round-trips at the record ceiling, the three depth stages carry the one
    /// output text, and the viz mode is inside <see cref="AlloyRenderer.LightVizMax"/>.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        int top = AlloyRenderer.MaxRecords - 1;
        check.Expect(Slot(Pack(top, 16383)) == top && Prim(Pack(top, 16383)) == 16383, "slot and primitive round-trip at the ceilings");
        check.Expect(Pack(0, 0) != 0 && Slot(Pack(0, 0)) == 0, "slot 0 packs non-zero (zero is the empty pixel)");
        check.Expect((long)AlloyRecordsPlusOne() << PrimBits <= uint.MaxValue, "MaxRecords + 1 fits above the primitive bits");
        check.Expect(OutDecl.Contains($"<< {PrimBits}"), "the GLSL pack and PrimBits agree");
        check.Expect(AlloyRenderer.DepthFs.Contains(OutDecl) && AlloyRenderer.DepthKeyFs.Contains(OutDecl) && AlloyRenderer.DepthFeatFs.Contains(OutDecl),
          "every depth fragment stage declares the vis-ID output");
        check.Expect(AlloyRenderer.DepthFs.Contains("oVisId = CxVisId(vDraw, gl_PrimitiveID);") && AlloyRenderer.DepthKeyFs.Contains("oVisId = CxVisId(vDraw, gl_PrimitiveID);")
          && AlloyRenderer.DepthFeatFs.Contains("oVisId = CxVisId(vDraw, gl_PrimitiveID);"), "...and writes the record slot, not the draw index");
        check.Expect(AlloyRenderer.DepthVs.Contains("flat out int vDraw;") && AlloyRenderer.DepthVs.Contains("vDraw = id;"), "DepthVs hands the slot down");
        check.Expect(VizMode <= AlloyRenderer.LightVizMax, "the viz mode is inside LightVizMax (or /alloy viz clamps it away)");
        check.Expect(VizFs.Contains("usampler2D") && VizFs.Contains("texelFetch"), "the viz reads the integer target unfiltered");
        log.Info(check.Ok ? "IdTarget.SelfCheck: passed" : "IdTarget.SelfCheck: FAILED");
        return check.Ok;
    }

    private static long AlloyRecordsPlusOne() => AlloyRenderer.MaxRecords + 1;
}
