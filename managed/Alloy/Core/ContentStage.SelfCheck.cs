using System.Collections.Generic;
using System.Linq;
using CX.Framework;

namespace CX.Alloy;

internal static partial class ContentStage
{
    /// <summary>A hand-written content stage and its one include -- the fixture <c>tools/content_stage.py</c> holds
    /// too, so the offline mirror and this translation answer to the same facts.</summary>
    private static readonly Dictionary<string, string> Fixture = new()
    {
        ["cx_fixture"] = "#define SHADER_TYPE 2\n#define LIGHTING 1\n#include \"inc_standard\"\n#include \"cx_fixinc\"\n"
                       + "uniform float fScale = 2.0;\nuniform int iMode;\nuniform vec4 vTint;\nuniform vec3 vNever;\n"
                       + "uniform sampler2D texUnit3;\nuniform sampler2D texMask;\nvarying float fVertexHeight;\n"
                       + "in vec3 vOwnVarying;\nfloat FixHelper(float x) { return x * fScale; }\n"
                       + "void main ()\n{\n    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n"
                       + "    FragmentColor.rgb *= vTint.rgb * FixHelper(fVertexHeight) + vOwnVarying;\n"
                       + "    ApplyStandardShader();\n    gl_FragColor = vec4(FragmentColor.rgb, texture2D(texUnit3, vTexCoords).r);\n}\n",
        ["cx_fixinc"] = "#ifndef FIX_INC\n#define FIX_INC\nfloat FixShared() { return 1.0; }\n#endif\n",
    };

    /// <summary>Off-device. Oracle: the fixture's own text and the facts written out by hand -- which uniform takes
    /// which lane, what a declaration the environment supplies or no upload fits becomes, whether the stage cuts --
    /// never a second run of the translation.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string? Chain(string n) => Fixture.TryGetValue(n, out string? t) ? t : null;
        Result r = Translate("cx_fixture", Chain, AlloyRenderer.ContentEnvFs, AlloyRenderer.VanillaEnvDefs, "", 8193, 37);
        check.Expect(r.Refused == null, $"the fixture was refused: {r.Refused}");
        if (r.Refused != null) { log.Info("ContentStage.SelfCheck: FAILED"); return false; }
        check.Expect(r.Params.SequenceEqual(new Param[] { new("fScale", ParamKind.Float, 0), new("iMode", ParamKind.Int, 1),
                                                          new("vTint", ParamKind.Vec4, 2) }),
                     $"the lanes are {string.Join(", ", r.Params)}, not fScale/Float/0, iMode/Int/1, vTint/Vec4/2");
        check.Expect(r.Host.Contains("#define fScale (CxCpOn(0) ? CxCp(0).x : (2.0))"), "fScale's default is not its own initialiser");
        check.Expect(r.Host.Contains("#define vNever vec3(0)"), "a vec3, which no .mtr upload fits, is not held at its default");
        check.Expect(r.Host.Contains("#define texMask uTex") && !r.Host.Contains("uniform sampler2D texUnit3"),
                     "a custom sampler is not unit 0, or the supplied texUnit3 kept its declaration");
        check.Expect(r.Host.Contains("vec3 vOwnVarying = vec3(0);") && !r.Host.Contains("float fVertexHeight"),
                     "an unknown varying is not held at zero, or the supplied fVertexHeight kept its declaration");
        check.Expect(r.Cuts, "a coverage written after the stock opener does not take the part out of the prepass");
        check.Expect(r.Host.Contains("void CxContentMain()") && !r.Host.Contains("gl_FragColor"),
                     "main is not renamed, or gl_FragColor survives");
        check.Expect(r.Host.Contains("#define CX_CON_HAS_FixHelper") && r.Host.Contains("float FixShared()"),
                     "a function the stage defines is not guarded against the environment's, or its include did not resolve");
        check.Expect(r.Notes.Any(n => n.Contains("after ApplyStandardShader")), "the edit after ApplyStandardShader is not noted");
        check.Expect(r.Host.Contains("#define CX_CONTENT_PROGRAM 1") && r.Host.Contains("cxConRow = CxContentRow(vDraw);"),
                     "the region does not mark its program a content one, or does not resolve the row its lanes read");
        Result miss = Translate("cx_fixture", n => n == "cx_fixinc" ? null : Chain(n), AlloyRenderer.ContentEnvFs,
                                AlloyRenderer.VanillaEnvDefs, "", 8193, 37);
        check.Expect(miss.Refused != null && miss.Refused.Contains("cx_fixinc"),
                     "an include the chain cannot answer does not refuse the stage, as it fails it in the engine");
        Result plain = Translate("cx_plain", n => n == "cx_plain"
                                     ? "#include \"inc_standard\"\nvoid main()\n{\n    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n"
                                       + "    FragmentColor = vec4(FragmentColor.rgb * 0.5, FragmentColor.a);\n    ApplyStandardShader();\n"
                                       + "    gl_FragColor = FragmentColor;\n}\n" : null,
                                 AlloyRenderer.ContentEnvFs, AlloyRenderer.VanillaEnvDefs, "", 8193, 37);
        check.Expect(plain.Refused == null && !plain.Cuts && plain.Notes.Count == 0,
                     "a stage that hands its alpha back unchanged is taken out of the prepass, or noted");
        log.Info(check.Ok ? "ContentStage.SelfCheck: passed" : "ContentStage.SelfCheck: FAILED");
        return check.Ok;
    }
}
