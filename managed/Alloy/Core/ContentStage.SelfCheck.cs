using System.Collections.Generic;
using System.Linq;
using CX.Framework;

namespace CX.Alloy;

internal static partial class ContentStage
{
    /// <summary>Hand-written content stages and their one include -- the fixture <c>tools/content_stage.py</c> holds too,
    /// so the offline mirror and this translation answer to the same facts.</summary>
    private static readonly Dictionary<string, string> Fixture = new()
    {
        ["cx_fixture"] = "#define SHADER_TYPE 2\n#define LIGHTING 1\n#include \"inc_standard\"\n#include \"cx_fixinc\"\n"
                       + "uniform float fScale = 2.0;\nuniform int iMode;\nuniform vec4 vTint;\nuniform vec3 vNever;\n"
                       + "uniform sampler2D texUnit3;\nuniform sampler2D texMask;\nvarying float fVertexHeight;\n"
                       + "in vec3 vOwnVarying;\nin vec2 vNobody;\nfloat FixHelper(float x) { return x * fScale; }\n"
                       + "void main ()\n{\n    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n"
                       + "    FragmentColor.rgb *= vTint.rgb * FixHelper(fVertexHeight) + vOwnVarying + vNobody.x;\n"
                       + "    ApplyStandardShader();\n    gl_FragColor = vec4(FragmentColor.rgb, texture2D(texUnit3, vTexCoords).r);\n}\n",
        ["cx_fixinc"] = "#ifndef FIX_INC\n#define FIX_INC\nfloat FixShared() { return 1.0; }\n#endif\n",
        ["cx_fixvs"] = "#define SHADER_TYPE 1\n#include \"inc_standard\"\nuniform float fSway;\nuniform float fScale = 2.0;\n"
                     + "uniform highp mat4 m_bones[MAX_NUM_BONES];\n"
                     + "out float fVertexHeight;\nvarying vec3 vOwnVarying;\nvarying vec3 vPosView;\n"
                     + "void main()\n{\n    ApplyStandardShader();\n    fVertexHeight = (m_m * vPos).z;\n"
                     + "    vOwnVarying = (m_bones[int(vIndex.x)] * vPos).xyz * fSway;\n}\n",
        ["cx_fixpost"] = "#define KEYHOLING 0\n#include \"inc_standard\"\nvoid main()\n{\n    FragmentColor = vec4(1.0);\n"
                       + "    SetupStandardShaderInputs();\n    float glow = 0.5, other = 1.0;\n    FragmentColor.rgb *= other;\n"
                       + "    ApplyStandardShader();\n    FragmentColor.rgb += vec3(glow) * float(numLights);\n"
                       + "    gl_FragColor = FragmentColor;\n}\n",
        ["cx_fixenv"] = "#define ENVIRONMENT_MAP 1\n#include \"inc_standard\"\nvoid main()\n{\n    FragmentColor = vec4(1.0);\n"
                      + "    SetupStandardShaderInputs();\n    ApplyStandardShader();\n}\n",
        ["cx_fixwhole"] = "void main()\n{\n    gl_FragColor = vec4(1.0, 0.0, 0.0, 1.0);\n}\n",
        // The engine-named set declared the way stock content declares it: inc_uniforms' whole block, inc_sunm's
        // lone scalar, fswater's wind loop, vs_beamvol's inline m_proj.
        ["cx_fixeng"] = "#define SHADER_TYPE 2\n#include \"inc_standard\"\nconst int MAX_WINDS = 128;\n"
                      + "uniform int moduleYear;\nuniform float moduleTimeIntoTransition;\nuniform int windPointSourcesCount;\n"
                      + "uniform vec3 windPointSourcesPosition[MAX_WINDS];\nuniform float windPointSourcesRadius[MAX_WINDS];\n"
                      + "uniform mat4 m_proj;\nvoid main ()\n{\n    FragmentColor = vec4(1.0);\n"
                      + "    SetupStandardShaderInputs();\n"
                      + "    float g = float(moduleYear) + moduleTimeIntoTransition + m_proj[3][3];\n"
                      + "    for (int i = 0; i < windPointSourcesCount; i++)\n"
                      + "        g += windPointSourcesRadius[i] * length(windPointSourcesPosition[i] - vPosView);\n"
                      + "    FragmentColor.rgb *= g;\n    ApplyStandardShader();\n    gl_FragColor = FragmentColor;\n}\n",
        ["cx_fixengv"] = "#define SHADER_TYPE 1\n#include \"inc_standard\"\nuniform mat4 m_proj;\nuniform float fWave;\n"
                       + "void main()\n{\n    ApplyStandardShader();\n    vPosView += vec3(0.0, 0.0, fWave);\n"
                       + "    gl_Position = m_proj * vec4(vPosView, 1.0);\n}\n",
        ["cx_fixown"] = "vec3 ApplyColorSpace(vec3 c) { return c * c; }\nvoid main()\n{\n    gl_FragColor = vec4(ApplyColorSpace(vec3(0.5)), 1.0);\n}\n",
        ["cx_fixmulti"] = "#include \"inc_standard\"\nuniform float rgbP0; uniform float rgbP1;\nvoid main()\n{\n"
                        + "    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n"
                        + "    gl_FragColor = vec4(FragmentColor.rgb * (rgbP0 + rgbP1), 1.0);\n}\n",
    };

    /// <summary>Off-device. Oracle: the fixtures' own text and the facts written out by hand -- which uniform takes which
    /// lane across the pair, which varying crosses it, what a declaration the environment supplies or no upload fits
    /// becomes, where main splits and what it hoists, whether the stage cuts -- never a second run of the translation.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string? Chain(string n) => Fixture.TryGetValue(n, out string? t) ? t : null;
        Result vs = Translate("cx_fixvs", false, Chain, AlloyRenderer.ContentEnvVs, "", AlloyRenderer.OwnedLitVs, 8193, 37);
        check.Expect(vs.Refused == null, $"the vertex fixture was refused: {vs.Refused}");
        Result r = Translate("cx_fixture", true, Chain, AlloyRenderer.ContentEnvFs, AlloyRenderer.VanillaEnvDefs, "", 8193, 37, vs);
        check.Expect(r.Refused == null, $"the fixture was refused: {r.Refused}");
        if (r.Refused != null || vs.Refused != null) { log.Info("ContentStage.SelfCheck: FAILED"); return false; }
        check.Expect(vs.Params.SequenceEqual(new Param[] { new("fSway", ParamKind.Float, 0), new("fScale", ParamKind.Float, 1) }),
                     $"the vertex lanes are {string.Join(", ", vs.Params)}, not fSway/Float/0, fScale/Float/1");
        check.Expect(r.Params.SequenceEqual(new Param[] { new("fSway", ParamKind.Float, 0), new("fScale", ParamKind.Float, 1),
                                                          new("iMode", ParamKind.Int, 2), new("vTint", ParamKind.Vec4, 3) }),
                     $"the pair's lanes are {string.Join(", ", r.Params)}, not the vertex pair then iMode/Int/2, vTint/Vec4/3");
        check.Expect(r.Host.Contains("#define fScale (CxCpOn(1) ? CxCp(1).x : (2.0))"),
                     "fScale does not read the lane its vertex half took, or its default is not its own initialiser");
        check.Expect(vs.Outputs.SetEquals(["fVertexHeight", "vOwnVarying"]),
                     $"the vertex half's own varyings are {string.Join(", ", vs.Outputs)}, not fVertexHeight and vOwnVarying");
        check.Expect(vs.Host.Contains("out float fVertexHeight;") && vs.Host.Contains("out vec3 vOwnVarying;")
                     && !vs.Host.Contains("out vec3 vPosView"),
                     "a vertex half's own varying is not an output, or a stock one it redeclares kept its declaration");
        check.Expect(vs.Host.Contains("CxConBoneAt(int(vIndex.x))") && !vs.Host.Contains("mat4 m_bones")
                     && !vs.Host.Contains("CxConBoneAt(MAX_NUM_BONES)")
                     && vs.Host.Contains("void CxContentVertexMain()")
                     && vs.Host.Contains("bool CxHostVertex(CxDraw d, int id, int bb)"),
                     "m_bones[i] is not the draw's bone slice, or main is not renamed and wrapped");
        check.Expect(r.Host.Contains("#undef fVertexHeight") && r.Host.Contains("in float fVertexHeight;")
                     && r.Host.Contains("in vec3 vOwnVarying;") && r.Host.Contains("vec2 vNobody = vec2(0);"),
                     "a varying the vertex half writes does not reach the fragment half as itself, or one it does not is not zeroed");
        check.Expect(r.Host.Contains("#define vNever vec3(0)"), "a vec3, which no .mtr upload fits, is not held at its default");
        check.Expect(r.Host.Contains("#define texMask uTex") && !r.Host.Contains("uniform sampler2D texUnit3"),
                     "a custom sampler is not unit 0, or the supplied texUnit3 kept its declaration");
        check.Expect(r.Cuts, "a coverage written after the stock opener does not take the part out of the prepass");
        check.Expect(r.Post && r.Host.Contains("void CxContentPost()") && r.Host.Contains("#define CX_CONTENT_POST 1")
                     && !r.Host.Contains("gl_FragColor"),
                     "an edit after ApplyStandardShader does not run on the lit colour, or gl_FragColor survives");
        check.Expect(r.Host.Contains("#define CX_CON_HAS_FixHelper") && r.Host.Contains("float FixShared()"),
                     "a function the stage defines is not guarded against the environment's, or its include did not resolve");
        check.Expect(r.Host.Contains("#define CX_CONTENT_PROGRAM 1") && r.Host.Contains("cxConRow = CxContentRow(vDraw);"),
                     "the region does not mark its program a content one, or does not resolve the row its lanes read");
        Result miss = Translate("cx_fixture", true, n => n == "cx_fixinc" ? null : Chain(n), AlloyRenderer.ContentEnvFs,
                                AlloyRenderer.VanillaEnvDefs, "", 8193, 37);
        check.Expect(miss.Refused != null && miss.Refused.Contains("cx_fixinc"),
                     "an include the chain cannot answer does not refuse the stage, as it fails it in the engine");
        Result post = Translate("cx_fixpost", true, Chain, AlloyRenderer.ContentEnvFs, AlloyRenderer.VanillaEnvDefs, "", 8193, 37);
        check.Expect(post.Refused == null && post.Post && !post.Cuts && post.NoKeyhole && post.ReadsLights,
                     "the post fixture does not edit the lit colour, cuts, keeps its keyhole, or reads no light arrays");
        check.Expect(post.Host.Contains("float cxHoist_glow;") && post.Host.Contains("cxHoist_glow = 0.5;")
                     && post.Host.Contains("float other = 1.0;") && post.Host.Contains("FragmentColor.rgb += vec3(cxHoist_glow)")
                     && post.Host.Contains("CxConFillLights();"),
                     "a local the post body reads is not hoisted and renamed, one it does not read moved, or the lights are not filled");
        Result envStage = Translate("cx_fixenv", true, Chain, AlloyRenderer.ContentEnvFs, AlloyRenderer.VanillaEnvDefs, "", 8193, 37);
        check.Expect(envStage.Refused == null && envStage.Host.Contains("#define CX_CONTENT_ENVMAP 1")
                     && !r.Host.Contains("CX_CONTENT_ENVMAP"),
                     "a stage's own ENVIRONMENT_MAP 1 does not make its diffuse alpha an env level, or one without it does");
        Result whole = Translate("cx_fixwhole", true, Chain, AlloyRenderer.ContentEnvFs, AlloyRenderer.VanillaEnvDefs, "", 8193, 37);
        check.Expect(whole.Refused == null && whole.Post && !whole.Cuts
                     && whole.Host.Contains("void CxContentMain()\n{\n}") && whole.Notes.Any(n => n.Contains("lights itself")),
                     "a stage that never calls ApplyStandardShader is not all post body, or is not noted");
        Result mine = Translate("cx_fixown", true, Chain, AlloyRenderer.ContentEnvFs, AlloyRenderer.VanillaEnvDefs,
                                "vec3 ApplyColorSpace(vec3 c) { return c; }\n", 8193, 37);
        check.Expect(mine.Refused == null && mine.Host.Contains("vec3 CxConOwn_ApplyColorSpace(vec3 c) { return c * c; }")
                     && mine.Host.Contains("vec4(CxConOwn_ApplyColorSpace(vec3(0.5)), 1.0)")
                     && !mine.Host.Contains("vec3 ApplyColorSpace(vec3 c) { return c * c; }"),
                     "a function the stage defines over one the program already has is not renamed, with its calls");
        Result multi = Translate("cx_fixmulti", true, Chain, AlloyRenderer.ContentEnvFs, AlloyRenderer.VanillaEnvDefs, "", 8193, 37);
        check.Expect(multi.Refused == null
                     && multi.Params.SequenceEqual(new Param[] { new("rgbP0", ParamKind.Float, 0), new("rgbP1", ParamKind.Float, 1) }),
                     $"two uniforms on one line do not both take a lane: {multi.Refused}");
        Result engv = Translate("cx_fixengv", false, Chain, AlloyRenderer.ContentEnvVs, "", AlloyRenderer.OwnedLitVs, 8193, 37);
        Result eng = Translate("cx_fixeng", true, Chain, AlloyRenderer.ContentEnvFs, AlloyRenderer.VanillaEnvDefs, "", 8193, 37,
                               engv.Refused == null ? engv : null);
        check.Expect(engv.Refused == null && eng.Refused == null,
                     $"an engine-named fixture was refused: {engv.Refused ?? eng.Refused}");
        check.Expect(!eng.Host.Contains("uniform int moduleYear") && !eng.Host.Contains("uniform float moduleTimeIntoTransition")
                     && !eng.Host.Contains("uniform int windPointSourcesCount") && !eng.Host.Contains("[MAX_WINDS]")
                     && !eng.Host.Contains("uniform mat4 m_proj") && !engv.Host.Contains("uniform mat4 m_proj")
                     // the environment's own MAX_WINDS, and only it
                     && eng.Host.Split("const int MAX_WINDS").Length == 2,
                     "a stage's own declaration of a name the CxEngine block carries is not stripped, so it would collide");
        check.Expect(eng.Host.Contains("windPointSourcesRadius[i]") && eng.Host.Contains("m_proj[3][3]")
                     && engv.Host.Contains("m_proj * vec4(vPosView, 1.0)"),
                     "the engine-named reads do not stand as the stage wrote them");
        check.Expect(StockShaped(Stock.Vs, false) && StockShaped(Stock.Fs, true) && !StockShaped(Fixture["cx_fixvs"], false)
                     && !StockShaped(Stock.Fs.Replace("#include \"inc_standard\"", "#include \"my_inc\""), true)
                     && !StockShaped(Stock.Fs.Replace("ApplyStandardShader();", "ApplyStandardShader();\n    FragmentColor.r = 1.0;"), true),
                     "a stage that adds nothing to stock is not read as stock, or one that adds an include or a line is");
        log.Info(check.Ok ? "ContentStage.SelfCheck: passed" : "ContentStage.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The shape of the bif's own <c>vslit_nm</c>/<c>fslit_nm</c>, hand-written. The oracle for every
    /// stock-shape verdict, <see cref="ContentPrograms.StockPair"/>'s included.</summary>
    internal static class Stock
    {
        internal const string Vs = "// header\n#define SHADER_TYPE 1\n#define NORMAL_MAP 1\n#include \"inc_standard\"\n"
                                 + "void main ()\n{\n    ApplyStandardShader();\n}\n";
        internal const string Fs = "#define SHADER_TYPE 2\n#define NORMAL_MAP 1\n#include \"inc_standard\"\nvoid main ()\n{\n"
                                 + "    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n"
                                 + "    gl_FragColor = FragmentColor;\n}\n";
    }
}
