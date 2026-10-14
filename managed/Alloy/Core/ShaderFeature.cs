using System;

namespace CX.Alloy;

/// <summary>
/// Which content custom shader a part's SharedMaterial asks for, recognised by name so the Alloy pass can
/// reproduce it as a switch inside its own program instead of porting seven forks. Each fork differs from the
/// stock <c>vslit_nm</c>/<c>fslit_nm</c> pair by a handful of lines, and the ids below are those lines, not
/// those files. The value IS the shader-side switch: it is staged into the per-draw record verbatim.
/// </summary>
internal enum ShaderFeature
{
    /// <summary>No custom program, or one this pass does not recognise -- drawn with the plain Alloy lit
    /// shading.</summary>
    None = 0,
    /// <summary>Plane-based mesh grass: sine sway anchored at the roots, tileset-mesh Z lift.</summary>
    WindGrass = 1,
    /// <summary>Plane-based mesh leaves: the same sway with a flat multiplier instead of a root anchor.</summary>
    WindLeaf = 2,
    /// <summary>3D mesh plants: the grass sway at double frequency and a thousandth of the strength.</summary>
    WindPlant = 3,
    /// <summary>Roof/ceiling toggle, no keyholing: raised to the ceiling while the camera is below the
    /// tileset's clip height, dropped 500m under otherwise, with the toolset vertex anchor discarded.</summary>
    RoofKn = 4,
    /// <summary>The same toggle with keyholing on, the clip height measured from the PLAYER, and no anchor
    /// discard.</summary>
    RoofKnM = 5,
    /// <summary>Stock lit with <c>KEYHOLING 0</c> -- tileset architecture that must never dissolve.</summary>
    NoKeyhole = 6,
    /// <summary>Diffuse UVs slid along an RG flow map in texture slot 6, two half-offset phases blended.</summary>
    Flowmap = 7,
    /// <summary>Not a content shader at all -- the TRUE skinmesh (a cloak, a robe, a tail), posed by the
    /// engine's 64-matrix bone array rather than the node matrix alone. It rides in this field because the
    /// record already carries it to both vertex stages, and <c>feat.y</c> carries the bone base. A feature
    /// that may be posed keeps its own id instead (<see cref="ShaderFeatures.PosedToo"/>).</summary>
    Skin = 8,
    /// <summary><c>fs_noirwet</c> -- a still liquid. Normal-only animated ripples anchored in world
    /// space, a low roughness and a white sheen. The pool stays dead flat: nothing here displaces, so unlike
    /// the wind forks this feature is not in <see cref="Displaces"/> and its parts stay in the prepass.</summary>
    NoirWet = 9,
    /// <summary><c>fs_noirgloss</c> -- still wet blood. The same normal hook, but the bump is frozen
    /// and finite-differenced out of the diffuse's own luminance, plus a mild darken and a deep-red specular
    /// tint. Same no-displacement note as <see cref="NoirWet"/>.</summary>
    NoirGloss = 10,
    /// <summary><c>td_ground_fs</c> -- the weather ground. Snow banked into the diffuse's own crevices, a wet
    /// darken with the roughness and specularity to match, standing puddles that flatten the normal map and
    /// take a Fresnel of the sky, and rain ripples over both. Around eighty materials name it, which is most
    /// of the outdoor tileset; drawn with the plain stage, none of it happened.</summary>
    GroundWeather = 11,
    /// <summary><c>vs_plt_wind</c>/<c>fs_plt_tinter</c> -- a part-based character's palette tint: the wearer's
    /// ten PLT palette rows against the material's own palette maps (<c>texture7</c>..<c>texture10</c>), a
    /// glow, an anisotropic lobe, and a sway weighted by the mesh's vertex paint. Its per-material terms ride
    /// <see cref="TintTerms"/>' side table, reached by the row index in <c>feat.z</c>.</summary>
    PaletteTint = 12,
    /// <summary><c>vs_snowed01</c>/<c>fs_snowed01</c> -- scripted snow cover. The slot-6 snow map laid over
    /// upward-facing surfaces in world space, broken up by the engine noise, with a buildup the server eases
    /// through the scriptable slots. Its vertex stage is stock lit, so nothing displaces.</summary>
    Snowed = 13,
}

internal static class ShaderFeatures
{
    /// <summary>The <c>customShaderVS</c> resrefs, indexed by <see cref="ShaderFeature"/>. The VS name alone
    /// separates all seven -- flowmap's vertex fork is stock code, but the .mtr still names it.
    /// <see cref="ShaderFeature.Skin"/> is no content shader and keeps an empty slot, which the array still
    /// has to hold because it is indexed by the enum; an empty name never matches, which is what Skin wants.</summary>
    internal static readonly string[] VsNames =
    {
        "", "shd_windgrass_vs", "shd_windleaf_vs", "shd_windplant_vs",
        "shd_roofkn_vs", "shd_roofknm_vs", "shd_nokeyhole_vs", "shd_flowmap_vs",
        "", "vs_noirwet", "vs_noirgloss", "td_ground_vs", "vs_plt_wind", "vs_snowed01",
    };

    /// <summary>The <c>customShaderFS</c> resrefs Alloy HOSTS, indexed like <see cref="VsNames"/>. An entry
    /// here is a <c>// $VANILLA-HOST</c> region in <see cref="AlloyRenderer.HostFs"/> plus an arm of its
    /// dispatcher, and the file itself comes off the resource chain at program build; empty = a feature whose
    /// fragment side is Alloy's own switch. ponytail: static, so an unknown FS name draws the plain lit path.</summary>
    internal static readonly string[] FsNames =
    {
        "", "", "", "",
        "", "", "", "",
        "", "fs_noirwet", "fs_noirgloss", "td_ground_fs", "fs_plt_tinter", "fs_snowed01",
    };

    /// <summary>Does this feature's fragment side come from a HOSTED content stage? Such a batch draws through
    /// the second lit program, the one that carries the bodies -- keeping them out of the program every other
    /// fragment runs, where they cost registers on thousands of draws that never take the branch (measured:
    /// opaque 15,1 -> 5,4 ms with the regions stubbed).</summary>
    internal static bool Hosted(int feat) => (uint)feat < (uint)FsNames.Length && FsNames[feat].Length > 0;

    /// <summary>Content fragment stages whose COVERAGE is map slot 1's alpha: they discard on
    /// <c>texUnit1.a</c> below <see cref="CoverNrmCut"/> and leave the diffuse alpha to the env-map level.
    /// Reproduced rather than hosted -- the discard is all Alloy lacks. A stage reading another channel, or
    /// cutting at another threshold, does NOT belong here: its number would be a silent divergence.</summary>
    internal static readonly string[] CoverNrmFs = { "fs_plt_hair", "fs_plt_hair2" };

    /// <summary>The threshold those stages discard at, which is also <c>AlloyClasses.Discard</c>'s cutout
    /// value -- so such a part needs no per-draw threshold, only the right channel.</summary>
    internal const float CoverNrmCut = 0.2f;

    /// <summary>Does this <c>customShaderFS</c> resref take its coverage from map slot 1's alpha? Whole-name
    /// and case-insensitive, as <c>MtrParams.Feature</c> matches the vertex names.</summary>
    internal static bool CoversFromNormalAlpha(string fs)
    {
        foreach (string n in CoverNrmFs)
            if (fs.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Does this feature read material texture slot 6? Three do -- the flow map's RG direction field
    /// and two snow overlays -- because the SLOT is the contract and the name beside it is only what one
    /// shader calls it. Everything else leaves the bit clear, which is vanilla's own <c>texture6Bound</c>
    /// test: a material that binds nothing there gets nothing sampled.</summary>
    internal static bool ReadsAuxSlot(int feat)
        => feat == (int)ShaderFeature.Flowmap || feat == (int)ShaderFeature.GroundWeather
           || feat == (int)ShaderFeature.Snowed;

    /// <summary>Does this feature read the four PALETTE slots, <c>texture7</c>..<c>texture10</c>? Only the
    /// palette tint does, and the same reading as <see cref="ReadsAuxSlot"/>'s applies: a material that binds
    /// nothing there gets nothing sampled, and a material that binds something there under any other feature
    /// leaves the bits clear rather than handing an unrelated stage four maps it never asked for.</summary>
    internal static bool ReadsPaletteSlots(int feat) => feat == (int)ShaderFeature.PaletteTint;

    /// <summary>Does this feature take its COVERAGE from somewhere other than the diffuse alpha? The palette
    /// tint does -- its coverage is the third palette map's blue channel, the <c>.mtr</c>'s own mip rescale
    /// over it -- because its diffuse carries the palette intensity, not an opacity.</summary>
    internal static bool HostedCoverage(int feat) => feat == (int)ShaderFeature.PaletteTint;

    /// <summary>May a record carry this content feature AND a bone base at once? The palette tint must: it
    /// is a CHARACTER shader, and a character's body parts are posed. Every other feature is tileset or
    /// placeable work that never meets a bone, so the skinmesh class takes <c>feat.x</c> from it and the
    /// vertex stage finds the bone base by that id.</summary>
    internal static bool PosedToo(int feat) => feat == (int)ShaderFeature.PaletteTint;

    /// <summary>The feature a record carries in <c>feat.x</c>: the content one, unless a POSE displaces it.
    /// The one expression behind every site that writes or keys on it -- the record, the sort digit, the batch
    /// audit and the caster's coverage source -- so a feature that coexists with a pose cannot coexist in some
    /// of them and not others. <c>feat.y</c> is the bone base either way (-1 unposed).</summary>
    internal static int WithPose(int feat, bool posed)
        => posed && !PosedToo(feat) ? (int)ShaderFeature.Skin : feat;

    /// <summary>Does this feature move vertices? Such a part must stay out of the depth prepass: the prepass
    /// stage does not displace, so its depth is the part's UNDISPLACED silhouette and the colour pass's LEQUAL
    /// then kills real fragments (a roof offset by ±500 fails everywhere; geometry behind a swayed plant dies
    /// at the plant's rest outline). Silent holes.</summary>
    internal static bool Displaces(int feat)
        => feat >= (int)ShaderFeature.WindGrass && feat <= (int)ShaderFeature.RoofKnM;

    /// <summary>Must this batch stay OUT of the depth prepass? Exactly what <see cref="Displaces"/> names, and
    /// nothing else. The skinmesh class does not: the constraint is that both stages compute
    /// <c>gl_Position</c> from a character-for-character IDENTICAL expression under <c>invariant</c>, and the
    /// depth stage has the same <c>CxSkinMat</c> text over the same <c>uBone</c> binding in the same order.</summary>
    internal static bool OutOfPrepass(int feat) => Displaces(feat);

    /// <summary>Does this feature switch keyholing OFF? Those parts keep their ownership when the camera
    /// would dissolve them, because their vanilla shader ignores the dissolve too. Read off each fork's
    /// FRAGMENT stage, which is where the dissolve happens -- flowmap declares <c>KEYHOLING 1</c> in its
    /// vertex stage and <c>0</c> in its fragment stage, so the vertex define would read the wrong side.</summary>
    internal static bool NoKeyholing(int feat)
        => feat == (int)ShaderFeature.NoKeyhole || feat == (int)ShaderFeature.RoofKn
           || feat == (int)ShaderFeature.Flowmap;
}
