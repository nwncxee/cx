using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The per-draw record's layout contract.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Headless: the per-draw record's layout contract -- <c>CxDraw</c>, one 72-float, 288-byte std430
    /// block at <see cref="SsboSlots.Draw"/> indexed by ownership slot, sound only while the GLSL declares that same layout.</summary>
    internal static bool StaticSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.StaticSelfCheck");

        check.Expect(ExtFloat == PrevFloat + 16 && ExtFloat + sizeof(MaterialExt) / sizeof(float) == DrawFloats && sizeof(ParamRow) == 80,
          "ADR 0026's ext0..ext2 do not sit between prev and the record's end");
        var rec = new DrawRecordBlock();
        check.Expect(sizeof(DrawRecordBlock) == 288 && (byte*)&rec.Prev - (byte*)&rec == 176 && (byte*)&rec.Ext - (byte*)&rec == 240
                     && sizeof(BonePalette) == 4096 && sizeof(Sphere) == 16 && sizeof(Box) == 24 && sizeof(TexHandles) == 64,
          "the draw record, the bone palette, the sphere, the box or the handle block is not the size the shaders stride by");
        check.Expect(ShaderContract.RecordSafe(DrawBufDecl), "the GLSL CxDraw declaration does not add up to DrawFloats");
        check.Expect(DrawBufDecl.Contains($"binding = {BindDraw})"), $"CxDrawBuf is not declared at binding {BindDraw}");
        check.Expect(DrawBufDecl.Contains($"binding = {BindDrawIdx})"), $"CxDrawIdxBuf is not declared at binding {BindDrawIdx}");
        check.Expect(CullPass.SourceStrideSafe(),
          "the cull or sort stage strides the source list by something other than CullPass.SrcUints");

        log.Info(check.Ok ? "AlloyRenderer.StaticSelfCheck: passed" : "AlloyRenderer.StaticSelfCheck: FAILED");
        return check.Ok;
    }
}
