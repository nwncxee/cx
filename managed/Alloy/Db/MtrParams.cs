using System;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>
/// What a parsed <c>.mtr</c> tells a draw record: the stock PBR parameters and ADR 0026's <c>Cx</c> vocabulary. A
/// content pair's own parameters are its content row's (<see cref="ContentRows"/>). Engine-free, so the recipe reads
/// the same answer on any thread.
/// </summary>
internal static class MtrParams
{
    /// <summary>The ceiling on <c>CxEmissiveStrength</c>: well into bloom territory, short of the RGBA16F
    /// target's 65504 once an 8-bit illumination texel of 1.0 is multiplied through.</summary>
    internal const float MaxEmissiveStrength = 100f;

    /// <summary>Content pairs <see cref="NoteCustomPair"/> has already reported: the line is per PAIR, not per material
    /// -- one pair is named by hundreds of them.</summary>
    private static readonly System.Collections.Generic.HashSet<string> _pairSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Say what Alloy does with a material's content pair, once per pair: translated, or not run and why. Grep
    /// <c>mtr shader:</c> for the list, and <c>Alloy content pair</c> for what each translated one became.</summary>
    internal static void NoteCustomPair(MtrDef? mtr, int feat, ExtenderLog? log)
    {
        if (mtr == null || log == null || (mtr.ShaderVs.Length == 0 && mtr.ShaderFs.Length == 0)) return;
        string pair = mtr.ShaderVs + "/" + mtr.ShaderFs;
        lock (_pairSeen)
            if (!_pairSeen.Add(pair)) return;
        string what = ShaderFeatures.IsContent(feat) ? "TRANSLATED -- it runs in a program of its own, built the first "
                      + "time a part on it draws (grep \"Alloy content pair\")"
            : mtr.ShaderVs.Length == 0 || mtr.ShaderFs.Length == 0
              ? "NOT RUN -- the material names only one half, and the engine builds no program from that either"
            : "NOT RUN -- both halves add nothing to their stock counterparts, so Alloy's own lit stage IS that shading, "
              + "or the session's content id range is spent (said once, above)";
        log.Info($"mtr shader: \"{pair}\" {what}");
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
    /// <paramref name="param"/> row; <paramref name="feat"/> is the record's feature vec4 (x = the feature). A name
    /// nobody reads is left alone; an int-typed parameter counts only where the engine's shader reads it as one.</summary>
    internal static void Fold(MtrDef? mtr, ref ParamRow param, Span<float> feat)
    {
        int f = (int)feat[0];
        if (mtr != null)
            foreach ((string name, float[] v, bool isInt) in mtr.Parameters)
            {
                if (v.Length == 0) continue;
                if (isInt)
                {
                    if (Is(name, "CxOrm")) param.Ext.Ext2.Y = v[0] != 0f ? 1f : 0f;
                    continue;
                }
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
        // A content pair keeps its own id in feat.x while posed, so feat.y is the BONE BASE and -1 is the unposed
        // sentinel CxSkinned tests -- a 0 there would pose every unposed such part off bone 0.
        if (ShaderFeatures.PosedToo(f)) feat[1] = -1f;
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

    /// <summary>A hand-written <c>.mtr</c> through <see cref="MtrDef.Parse"/>, <see cref="Fold"/> and the recipe's slot
    /// mapping and sourcing. Oracle: the literals in the text
    /// and in the hand-built meshes below, plus the slot contract in <c>docs/spec/materials.md</c> --
    /// <c>texture11</c> is the AO/ORM map on its own bit, <c>texture7</c>..<c>texture10</c> the palette's.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MtrParams.SelfCheck");
        const string text = "customShaderVS my_vs\ncustomShaderFS my_fs\n"
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
        feat[0] = (int)ShaderFeature.Content0;
        Fold(m, ref param, feat);
        check.Expect(m.ShaderVs == "my_vs" && m.ShaderFs == "my_fs", "the two content shader names did not parse");
        check.Expect(feat[1] == -1f && feat[2] == 0f, "a content record is not seeded unposed, or a parameter reached its feature lanes");
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
        check.Expect(DrawRecipe.MaskBitOf(6, 0) == 0 && DrawRecipe.MaskBitOf(6, 2) == 32,
                     "texture6 is bound for a part with no content pair, or not for one (2)");
        // the four palette slots, against hand-written numbers -- recipe slots 8..11 read .mtr texture7..texture10,
        // and their mask bits are 512, 1024, 2048, 4096, set for a content pair (2 and up) and for nothing else. A
        // slot that read the wrong texture would sample a normal map.
        for (int k = 0; k < 4; k++)
        {
            check.Expect(DrawRecipe.MtrTextureOf(8 + k) == 7 + k,
                         $"recipe slot {8 + k} does not read .mtr texture{7 + k}");
            check.Expect(DrawRecipe.MaskBitOf(8 + k, 2) == 512 << k,
                         $"palette slot {k}'s mask bit is not {512 << k} under a content pair");
            check.Expect(DrawRecipe.MaskBitOf(8 + k, 0) == 0 && DrawRecipe.MaskBitOf(8 + k, 1) == 0,
                         $"palette slot {k} is bound for the plain path or the skin class");
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
        MtrDef tm = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes("texture7 pal_a\ntexture10 pal_d\n"), "palcheck");
        check.Expect(tm.Textures[7] == "pal_a" && tm.Textures[10] == "pal_d" && tm.Textures[8] == "",
                     "texture7/texture10 did not parse, or an unstated texture8 is not empty");
        log.Info(check.Ok ? "MtrParams.SelfCheck: passed" : "MtrParams.SelfCheck: FAILED");
        return check.Ok;
    }
}
