using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>One run of a fixture stage: the dye and its palette alpha, the slot's shine and roughness, the
    /// reflectivity Alloy hands the stage, and 1 where the material is the modern model's.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TintCase(Vector4 Dye, Vector2 Slot, float SeedEnv, float Modern = 0f);

    /// <summary>What the stage left: the material, the specular colour, the surface colour and the reflectivity.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TintAnswer(Vector3 Pbr, Vector3 Spec, Vector3 Albedo, float Env);

    /// <summary>Floats in a <see cref="TintCase"/> and in a <see cref="TintAnswer"/>: the golden's two strides.</summary>
    private static readonly int TintIn = Unsafe.SizeOf<TintCase>() / sizeof(float), TintOut = Unsafe.SizeOf<TintAnswer>() / sizeof(float);

    /// <summary>The material Alloy hands every tint case's stage before it runs, unlike any a rebuild gives.</summary>
    private static readonly Vector3 TintSeedPbr = new(0.5f, 0.3f, 0.7f), TintSeedSpec = new(0.2f, 0.3f, 0.4f);

    /// <summary>What a tint case's stage compiles against in place of the lit program: the engine's globals as plain
    /// ones, an environment probe that answers pure blue, and the two lanes fed from the case.</summary>
    private const string TintEnv = """
        struct CxGoldRec { vec4 rot, pbr, spc; };
        CxGoldRec uDraw[1];
        int vDraw;
        vec4 FragmentColor; vec3 vFragmentNormal;
        float fSpecularity, fRoughness, fMetallicness; vec3 SpecularColor;
        #define MATERIAL_SPECULARITY_VALUE fSpecularity
        #define MATERIAL_ROUGHNESS_VALUE fRoughness
        #define MATERIAL_METALLICNESS_VALUE fMetallicness
        #define MATERIAL_SPECULAR_COLOR_VALUE SpecularColor
        #define FRAGMENT_LIGHTING 1
        #define materialFrontDiffuse (vec4(1.0))
        vec4 CxHostTexel, CxHostMat; vec2 CxHostUV, cxConUV; int cxConRow;
        float fEnvMapLevel, fOcclusion;
        vec4 cxGoldDye, cxGoldSlot;
        int CxContentRow(int d) { return 0; }
        bool CxCoverCut(float c, float a) { return false; }
        bool CxCpOn(int l) { return true; }
        vec4 CxCp(int l) { return l == 0 ? cxGoldDye : cxGoldSlot; }
        vec3 CxLinear(vec3 c) { return pow(max(c, vec3(0.0)), vec3(2.2)); }
        vec3 ApplyColorSpace(vec3 c) { return pow(c, vec3(2.2)); }
        vec3 RevertColorSpace(vec3 c) { return pow(c, vec3(1.0 / 2.2)); }
        vec3 SampleEnvironmentMapCube(vec3 d, float lod) { return vec3(0.0, 0.0, 1.0); }

        """;

    /// <summary>Fixture stage <paramref name="resref"/> translated by the shipped translator and run once per case, with
    /// the shipped <c>SetupSpecularity</c> and synthesis under it. Null, with the check failed, when a piece is gone or
    /// the device refused the stage.</summary>
    private static TintAnswer[]? ContentTintRun(VkBackend vk, Checker check, ExtenderLog log, string resref, ReadOnlySpan<TintCase> cases)
    {
        string head = ShaderSource.Get("cx_ownlit_head.glsl"), mat = ShaderSource.Get("cx_material.glsl");
        string env = ShaderSource.Get("cx_content_f.glsl");
        const string Open = "#ifndef CX_CON_HAS_SetupSpecularity\n";
        int from = env.IndexOf(Open, StringComparison.Ordinal);
        int to = from < 0 ? -1 : env.IndexOf("#endif\n", from, StringComparison.Ordinal);
        string synth = Function(mat, "vec3 CxSynthPbr("), spec = Function(mat, "vec3 CxSpecColor(");
        string consts = Declaration(ShaderSource.Get("cx_slotbits.glsl"), "CX_SLOT_NORMAL") + Declaration(head, "CX_SPEC_MIN")
                      + Declaration(head, "CX_METAL_GAIN_MAP") + Declaration(head, "CX_MID_GREY") + Declaration(head, "CX_ENV_SPEC_MAX");
        string feat = ShaderSource.Get("cx_featdecl.glsl");
        int featAt = feat.IndexOf("#define CX_FEAT_CONTENT0", StringComparison.Ordinal);
        bool found = to > from && synth.Length > 0 && spec.Length > 0 && featAt >= 0;
        check.Expect(found, "the content environment lost SetupSpecularity, or the material library its synthesis");
        if (!found) return null;
        ContentStage.Result r = ContentStage.Translate(resref, true, ContentStage.FixtureText,
                                                       TintEnv + consts + synth + spec + env[(from + Open.Length)..to], "", "", 8193, 37);
        check.Expect(r.Refused == null, $"{resref} was refused: {r.Refused}");
        if (r.Refused != null) return null;
        string label = "golden content tint " + resref;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(TintIn, TintOut)
            + feat[featAt..(feat.IndexOf('\n', featAt) + 1)] + r.Host
            + "void main()\n{\n    int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;\n"
            + "    cxGoldDye = vec4(uIn[i], uIn[i + 1], uIn[i + 2], uIn[i + 3]);\n    cxGoldSlot = vec4(uIn[i + 4], uIn[i + 5], 0.0, 0.0);\n"
            + "    vDraw = 0; uDraw[0].rot = vec4(1.0); uDraw[0].pbr = vec4(0.0); uDraw[0].spc = vec4(0.0);\n"
            + "    CxHostTexel = vec4(1.0); CxHostUV = vec2(0.0); CxHostMat = vec4(0.0, 0.0, uIn[i + 6], uIn[i + 7]);\n"
            + "    FragmentColor = vec4(1.0); vFragmentNormal = vec3(0.0, 0.0, 1.0);\n"
            + FormattableString.Invariant($"    fSpecularity = {TintSeedPbr.X}; fRoughness = {TintSeedPbr.Y}; fMetallicness = {TintSeedPbr.Z};\n")
            + FormattableString.Invariant($"    SpecularColor = vec3({TintSeedSpec.X}, {TintSeedSpec.Y}, {TintSeedSpec.Z});\n")
            + "    CxHostSurface(CX_FEAT_CONTENT0);\n"
            + "    uOut[o] = fSpecularity; uOut[o + 1] = fRoughness; uOut[o + 2] = fMetallicness;\n"
            + "    uOut[o + 3] = SpecularColor.r; uOut[o + 4] = SpecularColor.g; uOut[o + 5] = SpecularColor.b;\n"
            + "    uOut[o + 6] = FragmentColor.r; uOut[o + 7] = FragmentColor.g; uOut[o + 8] = FragmentColor.b;\n"
            + "    uOut[o + 9] = fEnvMapLevel;\n}\n", label);
        float[]? got = pipe.IsNull ? null
                     : RunCases(vk, log, pipe, cases.Length, MemoryMarshal.Cast<TintCase, float>(cases), cases.Length * TintOut, label);
        vk.Destroy(pipe);
        check.Expect(got != null, $"the content tint golden's {resref} stage was refused (logged above)");
        return got == null ? null : MemoryMarshal.Cast<float, TintAnswer>(got).ToArray();
    }

    /// <summary>Are <paramref name="a"/> and <paramref name="b"/> one value, to the tint goldens' tolerance?</summary>
    private static bool TintNear(Vector3 a, Vector3 b) => Vector3.Distance(a, b) <= 2e-4f;

    /// <summary>The same test for one term.</summary>
    private static bool TintNear(float a, float b) => MathF.Abs(a - b) <= 2e-4f;

    /// <summary>The tint golden's regression checks, neither an approved example: a stage that neither sets its
    /// reflectivity nor asks for a rebuild, and a rebuild asked of a modern material. Oracle: the material and
    /// reflectivity seeded by hand.</summary>
    private static void ContentTintSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        // Given a part whose material shader never sets its reflectivity and never asks for its shine to be rebuilt
        // When it is drawn with Alloy on, before and after this change
        // Then it looks the same
        TintAnswer[]? got = ContentTintRun(vk, check, log, "cx_fixplain", [new(new Vector4(0.5f, 0.25f, 1f, 1f), Vector2.Zero, 0.3f)]);
        if (got == null) return;
        TintAnswer a = got[0];
        log.Info(FormattableString.Invariant($"GpuGolden: content tint, a plain stage leaves {a.Pbr} {a.Spec} env {a.Env:F4}"));
        check.Expect(TintNear(a.Pbr, TintSeedPbr) && TintNear(a.Spec, TintSeedSpec),
                     FormattableString.Invariant($"a stage that asks for no rebuild keeps the material it was handed, not {a.Pbr} {a.Spec}"));
        check.Expect(TintNear(a.Albedo, new Vector3(0.5f, 0.25f, 1f)),
                     FormattableString.Invariant($"a stage that asks for no rebuild keeps its own colour, not {a.Albedo}"));
        check.Expect(TintNear(a.Env, 0.3f),
                     FormattableString.Invariant($"a stage that never sets its reflectivity keeps the 0.3 it was handed, not {a.Env:F4}"));
        TintAnswer[]? modern = ContentTintRun(vk, check, log, "cx_fixtint", [new(new Vector4(0.4f, 0.05f, 0.05f, 0.5f), Vector2.Zero, 0f, 1f)]);
        if (modern == null) return;
        check.Expect(TintNear(modern[0].Pbr, TintSeedPbr) && TintNear(modern[0].Spec, TintSeedSpec),
                     FormattableString.Invariant($"a rebuild leaves a modern material as declared, not {modern[0].Pbr} {modern[0].Spec}"));
    }
}
