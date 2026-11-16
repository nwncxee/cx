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
    /// <summary>One run of the glow fixture: its slot's clear and glow, and the linear texel the stand-in multiplies in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GlowCase(float Clear, float Glow, Vector3 Texel);

    /// <summary>What the fixture's surface body left: the content self-illumination term and the surface colour.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GlowAnswer(Vector3 SelfIllum, Vector3 Albedo);

    /// <summary>What the term holds before the body runs, unlike any value a case gives: a host that forgot to zero it
    /// hands this on.</summary>
    private static readonly Vector3 GlowSeed = new(7f, 7f, 7f);

    /// <summary><c>cx_fixselfillum</c> through the shipped translator on the device, against the tint golden's 2.2 curve.
    /// Oracle: the term worked by hand, <c>(texel^(1/2.2) * glow)^2.2</c>, and zero where the stage leaves it alone.</summary>
    private static void ContentSelfIllumSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string env = ShaderSource.Get("cx_content_f.glsl");
        string decl = Declaration(env, "vec3 " + ContentStage.SelfIllumName);
        const string Capability = "#define CX_SELFILLUM 1\n";
        if (env.Contains(Capability, StringComparison.Ordinal)) decl += Capability;
        string feat = ShaderSource.Get("cx_featdecl.glsl");
        int featAt = feat.IndexOf("#define CX_FEAT_CONTENT0", StringComparison.Ordinal);
        bool hasTerm = decl.Length > Capability.Length && decl.EndsWith(Capability, StringComparison.Ordinal);
        check.Expect(hasTerm && featAt >= 0, "the content environment lost its self-illumination term, or the feature list its content id");
        if (!hasTerm || featAt < 0) return;
        ContentStage.Result r = ContentStage.Translate("cx_fixselfillum", true, ContentStage.FixtureText, TintEnv + decl, "", "", 8193, 37);
        check.Expect(r.Refused == null && r.SelfIllum, $"cx_fixselfillum was refused or names no term: {r.Refused}");
        if (r.Refused != null) return;
        int inF = Unsafe.SizeOf<GlowCase>() / sizeof(float), outF = Unsafe.SizeOf<GlowAnswer>() / sizeof(float);
        const string Label = "golden content self-illumination";
        // the stand-in's coverage cut is a discard, which a compute stage refuses; TintEnv's CxCoverCut never takes it
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n#define discard return\n" + CaseBlocks(inF, outF)
            + feat[featAt..(feat.IndexOf('\n', featAt) + 1)] + r.Host
            + "void main()\n{\n    int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;\n"
            + "    cxGoldDye = vec4(0.0, 0.0, uIn[i], uIn[i + 1]);\n    cxGoldSlot = vec4(0.0);\n"
            + "    vDraw = 0; uDraw[0].rot = vec4(1.0); uDraw[0].pbr = vec4(0.0); uDraw[0].spc = vec4(0.0);\n"
            + "    CxHostTexel = vec4(uIn[i + 2], uIn[i + 3], uIn[i + 4], 1.0); CxHostUV = vec2(0.0); CxHostMat = vec4(0.0);\n"
            + "    FragmentColor = vec4(1.0); vFragmentNormal = vec3(0.0, 0.0, 1.0);\n"
            + FormattableString.Invariant($"    {ContentStage.SelfIllumName} = vec3({GlowSeed.X});\n")
            + "    CxHostSurface(CX_FEAT_CONTENT0);\n"
            + $"    uOut[o] = {ContentStage.SelfIllumName}.r; uOut[o + 1] = {ContentStage.SelfIllumName}.g; uOut[o + 2] = {ContentStage.SelfIllumName}.b;\n"
            + "    uOut[o + 3] = FragmentColor.r; uOut[o + 4] = FragmentColor.g; uOut[o + 5] = FragmentColor.b;\n}\n", Label);
        GlowCase[] cases = [new(0f, 2f, new Vector3(0.25f, 0.5f, 1f)), new(0f, -1f, new Vector3(0.25f, 0.5f, 1f))];
        float[]? got = pipe.IsNull ? null
                     : RunCases(vk, log, pipe, cases.Length, MemoryMarshal.Cast<GlowCase, float>(cases), cases.Length * outF, Label);
        vk.Destroy(pipe);
        check.Expect(got != null, "the content self-illumination golden's stage was refused (logged above)");
        if (got == null) return;
        GlowAnswer[] a = MemoryMarshal.Cast<float, GlowAnswer>(got).ToArray();
        log.Info(FormattableString.Invariant($"GpuGolden: content self-illumination {a[0].SelfIllum} over {a[0].Albedo}, unlit {a[1].SelfIllum}"));
        // 2^2.2 = 4.594794: a glow of 2 on the display colour is 4.59 times the linear texel
        check.Expect(TintNear(a[0].SelfIllum, new Vector3(1.148699f, 2.297397f, 4.594794f)),
                     FormattableString.Invariant($"a glow of 2 sets the term to {a[0].SelfIllum}, not the linear (1.1487, 2.2974, 4.5948)"));
        check.Expect(TintNear(a[0].Albedo, new Vector3(0.25f, 0.5f, 1f)),
                     FormattableString.Invariant($"the term is not kept off the surface colour, which is {a[0].Albedo}"));
        check.Expect(TintNear(a[1].SelfIllum, Vector3.Zero),
                     FormattableString.Invariant($"a stage that leaves the term alone hands on {a[1].SelfIllum}, not the zero its host sets"));
    }
}
