using CX.Graphics;

namespace CX.Alloy;

/// <summary>The caster pass's CONTENT stage: a content pair whose depth is its own (it moves its vertices or cuts its
/// own coverage) casts through the pair's caster twin -- its colour program's own vertex half and coverage, stopped at
/// the depth (<see cref="ContentPrograms"/>) -- instead of a plain stage that would cast it unmoved, off its diffuse.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>The draw loops' stage number for a content pair's twin: this plus its feature id. Above the plain
    /// stages' 0..2, so a mode compare still tells a program switch.</summary>
    private const int ContentMode = 16;

    /// <summary>The stage batch <paramref name="g"/> draws through, as both draw loops number them: 0 the solid stage, 1
    /// the bound-texture mask, 2 the handle mask, <see cref="ContentMode"/> plus the pair's id a content caster twin.</summary>
    private static int StageOf(int g)
        => _gContent[g] != 0 ? ContentMode + _gContent[g] : _gH[g] ? 2 : _gTex[g] != 0 ? 1 : 0;

    /// <summary>Bind pair <paramref name="feat"/>'s caster twin -- the layered one for <paramref name="layered"/>, which
    /// takes its projection from the view table -- with the terms its halves read: the scene's feature terms (the
    /// sway's clock and wind), the content table and the PLT palette.</summary>
    private static void BindContentCaster(int feat, bool layered, System.Numerics.Matrix4x4* vp, int size)
    {
        ref AlloyRenderer.LitLoc L = ref ContentPrograms.CastLoc(feat, layered);
        Gl.UseProgram(ContentPrograms.CastProgram(feat, layered));
        if (vp != null) Gl.UniformMatrix4fv(L.Vp, 1, 0, (float*)vp);
        Gl.Uniform1i(L.Tex, 0);
        Gl.Uniform1f(L.AlphaMip, Renderer.CutMip.V);
        int* viewport = stackalloc int[4] { 0, 0, size > 0 ? size : 1, size > 0 ? size : 1 };
        AlloyRenderer.UploadFeatureTerms(L.Feat, viewport);
        Db.ContentTable.Bind();
        Db.PltPalettes.Bind();
        ContentPrograms.BindTwinExtras(feat, cast: true, layered);
    }

    /// <summary>The cut a content caster batch makes where its stage takes the stock one: the batch kind's threshold.</summary>
    private static void ContentCasterCut(int feat, bool layered, CasterCut kind)
        => Gl.Uniform1f(ContentPrograms.CastLoc(feat, layered).Cut, Threshold(kind));
}
