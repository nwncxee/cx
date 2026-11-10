namespace CX.Alloy;

/// <summary>The caster pass's CONTENT stand-down: a content pair whose depth is its own (it moves its vertices or cuts
/// its own coverage) has a caster twin the pass does not run, so its parts cast through the plain stages instead.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Said once a session, from the staging walk, for the first content pair's caster that the pass admits:
    /// its twin is not run, so the part casts off its diffuse, unmoved. The twins exist in
    /// <see cref="ContentPrograms"/>; nothing here reaches them yet.</summary>
    private static void NoteContentTwin()
    {
        if (_vk.ContentSaid) return;
        _vk.ContentSaid = true;
        _log?.Always("CasterPass: a content pair's caster twin is NOT RUN -- its parts cast through the plain "
                   + "stages, so a stage that moves its vertices or cuts its own coverage casts them unmoved");
    }

    /// <summary>The stage batch <paramref name="g"/> draws through, as the draw loop numbers them: 0 the solid
    /// stage, 1 the bound-texture mask.</summary>
    private static int StageOf(int g) => StageForTex(_gTex[g]);

    /// <summary>...for a mask name read straight out of a batch key.</summary>
    private static int StageForTex(int tex) => tex != 0 ? 1 : 0;
}
