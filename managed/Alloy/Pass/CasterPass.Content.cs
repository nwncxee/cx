namespace CX.Alloy;

/// <summary>The caster pass's CONTENT stage: a content pair whose depth is its own (it moves its vertices or cuts its
/// own coverage) is its own caster batch, so a plain stage cannot cast it unmoved off its diffuse.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>The draw loops' stage number for a content pair's twin: this plus its feature id. Above the plain
    /// stages' 0..2, so a mode compare still tells a program switch.</summary>
    private const int ContentMode = 16;

    /// <summary>The stage batch <paramref name="g"/> draws through, as both draw loops number them: 0 the solid stage, 1
    /// the bound-texture mask, 2 the handle mask, <see cref="ContentMode"/> plus the pair's id a content caster twin.</summary>
    private static int StageOf(int g)
        => _gContent[g] != 0 ? ContentMode + _gContent[g] : _gH[g] ? 2 : _gTex[g] != 0 ? 1 : 0;
}
