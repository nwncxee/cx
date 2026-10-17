namespace CX.Alloy;

/// <summary>
/// What a draw record's <c>feat.x</c> says about the program its part draws through. The value IS the shader-side
/// switch (<c>cx_featdecl.glsl</c>), staged into the record verbatim. Content shaders are never recognised by name: any
/// pair a <c>.mtr</c> names runs translated under an id of its own (<see cref="ContentPrograms"/>).
/// </summary>
internal enum ShaderFeature
{
    /// <summary>No content pair: the plain Alloy lit shading.</summary>
    None = 0,
    /// <summary>Not a content shader at all -- the TRUE skinmesh (a cloak, a robe, a tail), posed by the engine's 64-matrix
    /// bone array rather than the node matrix alone. It rides in this field because the record already carries it to
    /// both vertex stages, and <c>feat.y</c> carries the bone base. A content pair keeps its own id while posed.</summary>
    Skin = 1,
    /// <summary>The first CONTENT pair: from here up, one id per <c>customShaderVS</c>/<c>customShaderFS</c> pair, handed
    /// out as pairs are first seen (<see cref="ShaderFeatures.MaxContent"/> of them).</summary>
    Content0 = 2,
}

internal static class ShaderFeatures
{
    /// <summary>Content pair ids a session may hand out. The whole corpus measured names 36 distinct pairs, stock ones
    /// included; a pair past the last id draws the plain lit path, said once.</summary>
    internal const int MaxContent = 128;

    /// <summary>Every feature id the record can carry: the skin class, then the content range.</summary>
    internal const int FeatureCount = (int)ShaderFeature.Content0 + MaxContent;

    /// <summary>Is this a content pair's id (<see cref="ShaderFeature.Content0"/> and up)?</summary>
    internal static bool IsContent(int feat) => feat >= (int)ShaderFeature.Content0 && feat < FeatureCount;

    /// <summary>Does this feature read material texture slot 6 or the four palette slots, <c>texture7</c>..<c>texture10</c>?
    /// Any content pair may -- the SLOT is the contract, the name is only what one shader calls it. Everything else
    /// leaves the bits clear, which is vanilla's own <c>textureNBound</c> test: a material that binds nothing there gets
    /// nothing sampled.</summary>
    internal static bool ReadsExtraSlots(int feat) => IsContent(feat);

    /// <summary>May a record carry this feature AND a bone base at once? Every content pair must: they are CHARACTER
    /// shaders as often as not, and a character's body parts are posed. The plain path takes the skin class's id.</summary>
    internal static bool PosedToo(int feat) => IsContent(feat);

    /// <summary>The feature a record carries in <c>feat.x</c>: the content one, unless a POSE displaces it. The one
    /// expression behind every site that writes or keys on it -- the record, the sort digit, the batch audit and the
    /// caster's coverage source -- so a feature that coexists with a pose cannot coexist in some of them and not others.
    /// <c>feat.y</c> is the bone base either way (-1 unposed).</summary>
    internal static int WithPose(int feat, bool posed)
        => posed && !PosedToo(feat) ? (int)ShaderFeature.Skin : feat;

    /// <summary>Must this batch stay OUT of the plain depth prepass? A content pair whose depth is its own
    /// (<see cref="ContentPrograms.OwnDepth"/>): the plain prepass neither runs its vertex half nor its coverage, so it
    /// would write depth where the pair leaves a hole or has moved away. The prepass's depth twin takes it instead.</summary>
    internal static bool OutOfPrepass(int feat) => ContentPrograms.OwnDepth(feat);

    /// <summary>Does this feature switch keyholing OFF? Those parts keep their ownership when the camera would dissolve
    /// them, because their own fragment stage ignores the dissolve too (<c>#define KEYHOLING 0</c>).</summary>
    internal static bool NoKeyholing(int feat) => ContentPrograms.NoKeyhole(feat);
}
