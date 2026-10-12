using System;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>
/// What a parsed <c>.mtr</c> tells a draw record: its content-shader feature (by <c>customShaderVS</c>), the stock
/// PBR parameters, ADR 0026's <c>Cx</c> vocabulary and the feature's own parameters. Engine-free, so the recipe
/// reads the same answer on any thread.
/// </summary>
internal static class MtrParams
{
    /// <summary>The ceiling on <c>CxEmissiveStrength</c>: well into bloom territory, short of the RGBA16F
    /// target's 65504 once an 8-bit illumination texel of 1.0 is multiplied through.</summary>
    internal const float MaxEmissiveStrength = 100f;

    /// <summary>Custom fragment stages <see cref="NoteCustomFs"/> has already reported: the line is per NAME,
    /// not per material -- one content stage is named by hundreds of them.</summary>
    private static readonly System.Collections.Generic.HashSet<string> _fsSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Say what Alloy does with a material's <c>customShaderFS</c>, once per name. A content stage
    /// Alloy neither hosts nor reproduces does not run, and one whose own body held the coverage discard then
    /// draws SOLID with nothing anywhere saying why. Grep <c>mtr shader:</c> for the list.</summary>
    internal static void NoteCustomFs(string fs, int feat, bool coverNrm, ExtenderLog? log)
    {
        if (fs.Length == 0 || log == null) return;
        lock (_fsSeen)
            if (!_fsSeen.Add(fs)) return;
        string what = ShaderFeatures.Hosted(feat) ? "HOSTED -- its own body runs inside the Alloy lit stage"
            : coverNrm ? "coverage reproduced -- the cutout tests map slot 1's alpha at "
                         + $"{ShaderFeatures.CoverNrmCut}, and the rest of its body is shading Alloy does itself"
            : feat != 0 ? "reproduced as an Alloy shader feature"
            : "NOT RUN -- Alloy neither hosts nor reproduces it. A stock permutation (fslit*, fst*) loses "
              + "nothing, the Alloy stage IS that shading; a CUSTOM stage's own work -- a coverage discard, "
              + "its own colour -- simply does not happen, and such a part draws as a plain lit one";
        log.Info($"mtr shader: \"{fs}\" {what}");
    }

    /// <summary>The <see cref="ShaderFeature"/> a <c>customShaderVS</c> resref names, 0 for none or one Alloy does
    /// not reproduce. Whole-name and case-insensitive, so <c>shd_roofkn_vs</c> cannot match <c>shd_roofknm_vs</c>.</summary>
    internal static int Feature(string vs)
    {
        if (vs.Length == 0) return 0;
        string[] names = ShaderFeatures.VsNames;
        for (int f = 1; f < names.Length; f++)
            if (names[f].Length > 0 && vs.Equals(names[f], StringComparison.OrdinalIgnoreCase)) return f;
        return 0;
    }

    /// <summary>The extension floats before any material is read: -1 = "not declared" for every term a 0 is
    /// legal for (a dielectric, a mirror); the layer weights and the sheen colour are 0 = absent.</summary>
    internal static void ResetExt(Span<float> ext)
    {
        ext.Clear();
        ext[0] = ext[1] = ext[3] = -1f;   // CxMetallic, CxRoughness, CxClearcoatRoughness
        ext[7] = ext[8] = -1f;            // CxSheenRoughness, CxEmissiveStrength
    }

    /// <summary>Fold <paramref name="mtr"/>'s parameters, in file order (a later line wins), into the record's
    /// <paramref name="param"/> floats (stock pair, then <see cref="AlloyRenderer.ParamExt"/>'s extension) and
    /// the feature vec4 <paramref name="feat"/> (x = the feature, yzw its parameters). A name nobody reads is
    /// left alone; an int-typed parameter counts only where the engine's shader reads it as one.</summary>
    internal static void Fold(MtrDef? mtr, Span<float> param, Span<float> feat)
    {
        int f = (int)feat[0];
        if (mtr != null)
            foreach ((string name, float[] v, bool isInt) in mtr.Parameters)
            {
                if (v.Length == 0) continue;
                if (isInt)
                {
                    if ((f == (int)ShaderFeature.RoofKn || f == (int)ShaderFeature.RoofKnM) && Is(name, "iRoofInterior"))
                        feat[3] = v[0];
                    if (Is(name, "CxOrm")) param[AlloyRenderer.ParamExt + 9] = v[0] != 0f ? 1f : 0f;
                    continue;
                }
                if (FeatureParam(f, name, v[0], feat)) continue;
                if (Ext(name, v, param[AlloyRenderer.ParamExt..])) continue;
                if (Is(name, "Specularity")) param[0] = v[0];
                else if (Is(name, "Roughness")) param[1] = v[0];
                else if (Is(name, "Metallicness")) param[2] = v[0];
                else if (Is(name, "CustomSpecularColor") && v.Length >= 3)
                {
                    param[4] = v[0]; param[5] = v[1]; param[6] = v[2];
                    // black IS the engine's "no custom colour" sentinel (inc_material.shd compares against it)
                    param[7] = v[0] != 0f || v[1] != 0f || v[2] != 0f ? 1f : 0f;
                }
            }
        // fSwaySpeed divides, and 1.0 is what every wind .mtr states: an undeclared one is the authored case.
        if (f >= (int)ShaderFeature.WindGrass && f <= (int)ShaderFeature.WindPlant && feat[1] == 0f) feat[1] = 1f;
        // The palette tint keeps its own id in feat.x while posed, so feat.y is the BONE BASE and -1 is the
        // unposed sentinel CxSkinned tests -- a 0 there would pose every unposed palette part off bone slot 0.
        if (f == (int)ShaderFeature.PaletteTint) feat[1] = -1f;
    }

    /// <summary>Terms a palette-tint material states, in <see cref="TintTerms"/>' row order: illumination
    /// colour (three), the coverage map's mip scale, anisotropy, specular intensity, sway speed, sway
    /// strength. The defaults are the feature OFF. PLACEHOLDER SPELLINGS: the pair is the author's content
    /// and not in this repo, so they follow the wind forks' convention and are also in the vanilla env.</summary>
    internal static void FoldTint(MtrDef? mtr, Span<float> t, ExtenderLog? log = null)
    {
        t.Clear();
        t[5] = 1f;
        if (mtr == null) return;
        int read = 0;
        foreach ((string name, float[] v, bool isInt) in mtr.Parameters)
        {
            if (isInt || v.Length == 0) continue;
            if (Is(name, "fIllumColor") && v.Length >= 3) { t[0] = v[0]; t[1] = v[1]; t[2] = v[2]; }
            else if (Is(name, "fAlphaMipScale")) t[3] = v[0];
            else if (Is(name, "fAnisotropy")) t[4] = Math.Clamp(v[0], 0f, 1f);
            else if (Is(name, "fSpecularIntensity")) t[5] = v[0];
            else if (Is(name, "fSwaySpeed")) t[6] = v[0];
            else if (Is(name, "fSwayStrength")) t[7] = v[0];
            else continue;
            read++;
        }
        // Not one term recognised, on a material that names the pair: the spellings below are PROVISIONAL
        // (see the summary), so the likely reading is that this material calls them something else and the
        // tint is drawing on defaults. Said once per material, because that is what a wrong guess looks like.
        if (read == 0)
            log?.Always($"MtrParams: '{mtr.Name}' names the palette-tint pair and states none of "
                      + "fIllumColor/fAlphaMipScale/fAnisotropy/fSpecularIntensity/fSwaySpeed/fSwayStrength "
                      + "-- its tint draws on defaults (no glow, the plain cut, an isotropic lobe). Those "
                      + "spellings are CX's guess at the shader's own uniforms; send the .mtr to have them fixed");
    }

    /// <summary>How many floats <see cref="FoldTint"/> fills.</summary>
    internal const int TintTermCount = 8;

    /// <summary>The parameters the community content shaders declare, by feature.</summary>
    private static bool FeatureParam(int f, string name, float v, Span<float> feat)
    {
        switch (f)
        {
            case (int)ShaderFeature.WindGrass:
            case (int)ShaderFeature.WindLeaf:
            case (int)ShaderFeature.WindPlant:
                if (Is(name, "fSwaySpeed")) { feat[1] = v; return true; }
                if (Is(name, "fSwayStrength")) { feat[2] = v; return true; }
                if (Is(name, "fTilesetMeshOffsetZ")) { feat[3] = v; return true; }
                return false;
            case (int)ShaderFeature.RoofKn:
            case (int)ShaderFeature.RoofKnM:
                if (Is(name, "fRoofOffsetZ")) { feat[1] = v; return true; }
                if (Is(name, "fRoofCameraClipZ")) { feat[2] = v; return true; }
                return false;
            case (int)ShaderFeature.Flowmap:
                if (Is(name, "fSlideSpeed")) { feat[1] = v; return true; }
                return false;
        }
        return false;
    }

    /// <summary>ADR 0026's vocabulary into the three extension vec4s: (CxMetallic, CxRoughness, CxClearcoat,
    /// CxClearcoatRoughness), (CxSheenColor.rgb, CxSheenRoughness), (CxEmissiveStrength, CxOrm, _, _). Clamped
    /// here, so a typo'd 50 reads as the maximum rather than an HDR blow-out.</summary>
    private static bool Ext(string name, float[] p, Span<float> x)
    {
        float v = Math.Clamp(p[0], 0f, 1f);
        if (Is(name, "CxMetallic")) x[0] = v;
        else if (Is(name, "CxRoughness")) x[1] = v;
        else if (Is(name, "CxClearcoat")) x[2] = v;
        else if (Is(name, "CxClearcoatRoughness")) x[3] = v;
        else if (Is(name, "CxSheenColor") && p.Length >= 3)
        { x[4] = v; x[5] = Math.Clamp(p[1], 0f, 1f); x[6] = Math.Clamp(p[2], 0f, 1f); }
        else if (Is(name, "CxSheenRoughness")) x[7] = v;
        else if (Is(name, "CxEmissiveStrength")) x[8] = Math.Clamp(p[0], 0f, MaxEmissiveStrength);
        else if (Is(name, "CxOrm")) x[9] = p[0] > 0.5f ? 1f : 0f;
        else return false;
        return true;
    }

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A hand-written <c>.mtr</c> through <see cref="MtrDef.Parse"/>, <see cref="Fold"/>,
    /// <see cref="FoldTint"/> and the recipe's slot mapping. Oracle: the literals in the text, and the slot
    /// contract in <c>docs/spec/materials.md</c> -- <c>texture11</c> is the AO/ORM map on its own mask bit,
    /// and <c>texture7</c>..<c>texture10</c> are the palette tint's four, on theirs.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MtrParams.SelfCheck");
        const string text = "customShaderVS SHD_WINDGRASS_VS\n"
            + "parameter float Specularity 0.5\nparameter float roughness 0.3\n"
            + "parameter float CustomSpecularColor 0.5 0 0\nparameter float CxMetallic 0\n"
            + "parameter float cxclearcoat 2\nparameter float CxSheenColor 0.2 0.3 0.4\n"
            + "parameter int CxOrm 1\nparameter float CxEmissiveStrengthX 7\n"
            + "parameter float fSwayStrength 0.25\nparameter int Metallicness 1\n"
            + "texture7 community_t7\ntexture11 my_orm\ntexture15 last\n";
        MtrDef m = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes(text), "selfcheck");
        Span<float> param = stackalloc float[20], feat = stackalloc float[4];
        param.Clear(); feat.Clear();
        ResetExt(param[8..]);
        feat[0] = Feature(m.ShaderVs);
        Fold(m, param, feat);
        check.Expect(feat[0] == 1f, "customShaderVS shd_windgrass_vs is not the wind-grass feature (1)");
        check.Expect(Feature("shd_roofkn_vs") == 4 && Feature("shd_roofknm_vs") == 5 && Feature("vs_other") == 0,
                     "the roof forks are not told apart, or an unknown VS names a feature");
        check.Expect(feat[1] == 1f && feat[2] == 0.25f, "wind: an undeclared fSwaySpeed is 1, fSwayStrength lands in feat.z");
        check.Expect(param[0] == 0.5f && param[1] == 0.3f, "the stock Specularity/Roughness do not land in pbr.xy");
        check.Expect(param[2] == 0f, "an int-typed Metallicness was read (the engine binds it as an int uniform)");
        check.Expect(param[4] == 0.5f && param[7] == 1f, "a non-black CustomSpecularColor did not land with its on flag");
        check.Expect(param[8] == 0f && param[9] == -1f, "a declared CxMetallic 0 or an absent CxRoughness lost its meaning");
        check.Expect(param[10] == 1f, "CxClearcoat is not case-folded and clamped to 1");
        check.Expect(param[12] == 0.2f && param[13] == 0.3f && param[14] == 0.4f, "CxSheenColor's three components");
        check.Expect(param[16] == -1f && param[17] == 1f, "an int CxOrm 1 is not read, or a longer name matched CxEmissiveStrength");
        check.Expect(m.Textures[7] == "community_t7" && m.Textures[11] == "my_orm" && m.Textures[15] == "last",
                     "texture7/11/15 are not all parsed");
        check.Expect(DrawRecipe.MtrTextureOf(7) == 11 && DrawRecipe.MtrTextureOf(6) == 6,
                     "the AO recipe slot does not read texture11, or the flow slot moved off texture6");
        check.Expect(DrawRecipe.MaskBitOf(7, 0) == 128 && DrawRecipe.MaskBitOf(1, 0) == 1,
                     "AO is not on mask bit 128 (64 is the env bit), or texture1 is not bit 1");
        check.Expect(DrawRecipe.MaskBitOf(6, 0) == 0 && DrawRecipe.MaskBitOf(6, 7) == 32,
                     "texture6 is bound for a feature that does not read it, or not for the flow map (7)");
        // CX-223: the four palette slots, against hand-written numbers -- recipe slots 8..11 read .mtr
        // texture7..texture10, and their mask bits are 512, 1024, 2048, 4096, set for the palette tint (12)
        // and for nothing else. A slot that read the wrong texture would tint off a normal map.
        for (int k = 0; k < 4; k++)
        {
            check.Expect(DrawRecipe.MtrTextureOf(8 + k) == 7 + k,
                         $"recipe slot {8 + k} does not read .mtr texture{7 + k}");
            check.Expect(DrawRecipe.MaskBitOf(8 + k, 12) == 512 << k,
                         $"palette slot {k}'s mask bit is not {512 << k} under the palette tint");
            check.Expect(DrawRecipe.MaskBitOf(8 + k, 0) == 0 && DrawRecipe.MaskBitOf(8 + k, 11) == 0,
                         $"palette slot {k} is bound for a feature that does not read it");
        }
        check.Expect(DrawRecipe.TexSlots == 12, "a recipe does not name 12 texture slots (diffuse, 7 maps, 4 palette)");
        // ...and the terms a palette .mtr states, through FoldTint, against the literals in the text below.
        const string tintText = "customShaderVS VS_PLT_WIND\ncustomShaderFS fs_plt_tinter\n"
            + "parameter float fIllumColor 0.1 0.2 0.3\nparameter float fAlphaMipScale 0.5\n"
            + "parameter float fAnisotropy 7\nparameter float fSwayStrength 0.75\n"
            + "texture7 pal_a\ntexture10 pal_d\n";
        MtrDef tm = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes(tintText), "tintcheck");
        Span<float> terms = stackalloc float[TintTermCount];
        FoldTint(tm, terms);
        check.Expect(Feature(tm.ShaderVs) == 12, "customShaderVS vs_plt_wind is not the palette-tint feature (12)");
        check.Expect(terms[0] == 0.1f && terms[1] == 0.2f && terms[2] == 0.3f, "fIllumColor's three components");
        check.Expect(terms[3] == 0.5f, "fAlphaMipScale did not land in the mip-scale lane");
        check.Expect(terms[4] == 1f, "fAnisotropy 7 was not clamped to 1");
        check.Expect(terms[5] == 1f, "an undeclared fSpecularIntensity is not full intensity");
        check.Expect(terms[6] == 0f && terms[7] == 0.75f, "the sway's speed or strength did not land");
        check.Expect(tm.Textures[7] == "pal_a" && tm.Textures[10] == "pal_d" && tm.Textures[8] == "",
                     "texture7/texture10 did not parse, or an unstated texture8 is not empty");
        log.Info(check.Ok ? "MtrParams.SelfCheck: passed" : "MtrParams.SelfCheck: FAILED");
        return check.Ok;
    }
}
