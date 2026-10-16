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

    /// <summary>Say what Alloy does with a material's <c>customShaderFS</c>, once per name: translated as a content
    /// stage, hosted, reproduced, or not run and why. Grep <c>mtr shader:</c> for the list, and <c>Alloy content
    /// stage</c> for what each translated one became.</summary>
    internal static void NoteCustomFs(string fs, int feat, bool coverNrm, ExtenderLog? log)
    {
        if (fs.Length == 0 || log == null) return;
        lock (_fsSeen)
            if (!_fsSeen.Add(fs)) return;
        string what = ShaderFeatures.IsContent(feat) ? "TRANSLATED -- it runs as a content stage in a program of its own, "
                      + "built the first time a part on it draws (grep \"Alloy content stage\")"
            : ShaderFeatures.Hosted(feat) ? "HOSTED -- its own body runs inside the Alloy lit stage"
            : coverNrm ? "coverage reproduced -- the cutout tests map slot 1's alpha at "
                         + $"{ShaderFeatures.CoverNrmCut}, and the rest of its body is shading Alloy does itself"
            : feat != 0 ? "reproduced as an Alloy shader feature"
            : "NOT RUN -- a stock stage (Alloy's own stage IS that shading, nothing is lost), a stage paired with a "
              + "vertex feature Alloy reproduces, or one named with no vertex stage (which the engine does not build "
              + "either)";
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
    internal static void ResetExt(ref MaterialExt ext)
    {
        ext = default;
        ext.Ext0.X = ext.Ext0.Y = ext.Ext0.W = -1f;   // CxMetallic, CxRoughness, CxClearcoatRoughness
        ext.Ext1.W = ext.Ext2.X = -1f;                // CxSheenRoughness, CxEmissiveStrength
    }

    /// <summary>Fold <paramref name="mtr"/>'s parameters, in file order (a later line wins), into the record's
    /// <paramref name="param"/> row and
    /// the feature vec4 <paramref name="feat"/> (x = the feature, yzw its parameters). A name nobody reads is
    /// left alone; an int-typed parameter counts only where the engine's shader reads it as one.</summary>
    internal static void Fold(MtrDef? mtr, ref ParamRow param, Span<float> feat)
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
                    if (Is(name, "CxOrm")) param.Ext.Ext2.Y = v[0] != 0f ? 1f : 0f;
                    continue;
                }
                if (FeatureParam(f, name, v[0], feat)) continue;
                if (Ext(name, v, ref param.Ext)) continue;
                if (Is(name, "Specularity")) param.Specularity = v[0];
                else if (Is(name, "Roughness")) param.Roughness = v[0];
                else if (Is(name, "Metallicness")) param.Metallicness = v[0];
                else if (Is(name, "CustomSpecularColor") && v.Length >= 3)
                {
                    param.CustomR = v[0]; param.CustomG = v[1]; param.CustomB = v[2];
                    // black IS the engine's "no custom colour" sentinel (inc_material.shd compares against it)
                    param.CustomDeclared = v[0] != 0f || v[1] != 0f || v[2] != 0f ? 1f : 0f;
                }
            }
        // fSwaySpeed divides, and 1.0 is what every wind .mtr states: an undeclared one is the authored case.
        if (f >= (int)ShaderFeature.WindGrass && f <= (int)ShaderFeature.WindPlant && feat[1] == 0f) feat[1] = 1f;
        // The palette tint and a content stage keep their own id in feat.x while posed, so feat.y is the BONE BASE
        // and -1 is the unposed sentinel CxSkinned tests -- a 0 there would pose every unposed such part off bone 0.
        if (ShaderFeatures.PosedToo(f)) feat[1] = -1f;
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
    private static bool Ext(string name, float[] p, ref MaterialExt x)
    {
        float v = Math.Clamp(p[0], 0f, 1f);
        if (Is(name, "CxMetallic")) x.Ext0.X = v;
        else if (Is(name, "CxRoughness")) x.Ext0.Y = v;
        else if (Is(name, "CxClearcoat")) x.Ext0.Z = v;
        else if (Is(name, "CxClearcoatRoughness")) x.Ext0.W = v;
        else if (Is(name, "CxSheenColor") && p.Length >= 3)
        { x.Ext1.X = v; x.Ext1.Y = Math.Clamp(p[1], 0f, 1f); x.Ext1.Z = Math.Clamp(p[2], 0f, 1f); }
        else if (Is(name, "CxSheenRoughness")) x.Ext1.W = v;
        else if (Is(name, "CxEmissiveStrength")) x.Ext2.X = Math.Clamp(p[0], 0f, MaxEmissiveStrength);
        else if (Is(name, "CxOrm")) x.Ext2.Y = p[0] > 0.5f ? 1f : 0f;
        else return false;
        return true;
    }

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A hand-written <c>.mtr</c> through <see cref="MtrDef.Parse"/>, <see cref="Fold"/>,
    /// <see cref="FoldTint"/> and the recipe's slot mapping and sourcing. Oracle: the literals in the text
    /// and in the hand-built meshes below, plus the slot contract in <c>docs/spec/materials.md</c> --
    /// <c>texture11</c> is the AO/ORM map on its own bit, <c>texture7</c>..<c>texture10</c> the palette's.</summary>
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
        var param = new ParamRow();
        Span<float> feat = stackalloc float[4];
        feat.Clear();
        ResetExt(ref param.Ext);
        feat[0] = Feature(m.ShaderVs);
        Fold(m, ref param, feat);
        check.Expect(feat[0] == 1f, "customShaderVS shd_windgrass_vs is not the wind-grass feature (1)");
        check.Expect(Feature("shd_roofkn_vs") == 4 && Feature("shd_roofknm_vs") == 5 && Feature("vs_other") == 0,
                     "the roof forks are not told apart, or an unknown VS names a feature");
        check.Expect(feat[1] == 1f && feat[2] == 0.25f, "wind: an undeclared fSwaySpeed is 1, fSwayStrength lands in feat.z");
        check.Expect(param.Specularity == 0.5f && param.Roughness == 0.3f, "the stock Specularity/Roughness do not land in pbr.xy");
        check.Expect(param.Metallicness == 0f, "an int-typed Metallicness was read (the engine binds it as an int uniform)");
        check.Expect(param.CustomR == 0.5f && param.CustomDeclared == 1f, "a non-black CustomSpecularColor did not land with its on flag");
        check.Expect(param.Ext.Ext0.X == 0f && param.Ext.Ext0.Y == -1f, "a declared CxMetallic 0 or an absent CxRoughness lost its meaning");
        check.Expect(param.Ext.Ext0.Z == 1f, "CxClearcoat is not case-folded and clamped to 1");
        check.Expect(param.Ext.Ext1.X == 0.2f && param.Ext.Ext1.Y == 0.3f && param.Ext.Ext1.Z == 0.4f, "CxSheenColor's three components");
        check.Expect(param.Ext.Ext2.X == -1f && param.Ext.Ext2.Y == 1f, "an int CxOrm 1 is not read, or a longer name matched CxEmissiveStrength");
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
        // Where a slot's resref comes from. A model's texture1..3 are map slots only on a mesh that carries a
        // tangent frame -- the engine compiles no shader that samples those units for any other mesh.
        var plain = new Mdl.MeshDef();
        plain.Textures[0] = "tile_diff"; plain.Textures[1] = "tile_lm"; plain.Textures[2] = "tile_x";
        check.Expect(DrawRecipe.SlotName(0, null, plain) == "tile_diff", "a mesh's texture0 is not the diffuse slot's source");
        check.Expect(DrawRecipe.SlotName(1, null, plain) == "" && DrawRecipe.SlotName(2, null, plain) == "",
                     "a mesh with no tangent frame still feeds its texture1/texture2 to the normal and spec slots");
        var hinted = new Mdl.MeshDef { RenderHintId = 2 };
        hinted.Textures[0] = "wall_diff"; hinted.Textures[1] = "wall_n"; hinted.Textures[2] = "wall_s";
        check.Expect(DrawRecipe.SlotName(1, null, hinted) == "wall_n" && DrawRecipe.SlotName(2, null, hinted) == "wall_s",
                     "a normalandspecmapped mesh's own texture1/texture2 do not reach the normal and spec slots");
        var tanned = new Mdl.MeshDef { Tangents = new System.Numerics.Vector4[1] };
        tanned.Textures[0] = "a"; tanned.Textures[1] = "a_n";
        check.Expect(DrawRecipe.SlotName(1, null, tanned) == "a_n", "a mesh whose file supplied tangents loses its texture1");
        MtrDef nm = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes("texture1 mtr_n\n"), "slotcheck");
        check.Expect(DrawRecipe.SlotName(1, nm, plain) == "mtr_n", "a .mtr's declared texture1 is gated on the MODEL's tangent frame");
        var nulled = new Mdl.MeshDef { RenderHintId = 2 };
        nulled.Textures[0] = "NULL"; nulled.Textures[1] = "null";
        check.Expect(DrawRecipe.SlotName(0, null, nulled) == "" && DrawRecipe.SlotName(1, null, nulled) == "",
                     "the MDL's \"null\" placeholder is taken as a resref");
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
