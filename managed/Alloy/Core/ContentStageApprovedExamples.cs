using System.Collections.Generic;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Which content stages draw through the G-buffer (approved-spec, E1, E2, E4). Oracle: the stages written here by hand,
/// each the shape of one a server ships, and the route a person expects of that shape.
/// </summary>
internal static class ContentStageApprovedExamples
{
    private const int AlphaBlendSrc = 0, AlphaBlendDst = 1;

    private static readonly Dictionary<string, string> Stages = new()
    {
        ["cx_fixalpha"] = "#include \"inc_standard\"\nvoid main()\n{\n    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n"
                        + "    float a = texture2D(texUnit1, vTexCoords.xy).a;\n    ApplyStandardShader();\n"
                        + "    gl_FragColor = vec4(FragmentColor.rgb, a);\n}\n",
        ["cx_fixalphacut"] = "#include \"inc_standard\"\nuniform float fCut;\nvoid main()\n{\n    FragmentColor = vec4(1.0);\n"
                           + "    SetupStandardShaderInputs();\n    ApplyStandardShader();\n"
                           + "    gl_FragColor = vec4(FragmentColor.rgb, texture2D(texUnit3, vTexCoords.xy).r);\n"
                           + "    if (gl_FragColor.a < fCut)\n    {\n        discard;\n    }\n}\n",
    };

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ContentStageApprovedExamples.SelfCheck");

        // Given a material whose fragment shader only sets its alpha after ApplyStandardShader, as fs_plt_hair does
        // When the shader is translated
        // Then cx.log names a deferred twin for it and does not call it forward only
        ContentStage.Result alpha = Translate("cx_fixalpha");
        check.Expect(alpha.Refused == null && !alpha.ForwardOnly && !alpha.RouteNote.Contains("forward only"),
                     $"E1: an alpha-only stage routes as '{alpha.RouteNote}'");

        // Given a material whose fragment shader sets its alpha after ApplyStandardShader and then discards below a cutoff, as fslit_hair_nm does
        // When the shader is translated
        // Then cx.log names a deferred twin for it, and a pixel below the cutoff draws nothing, as it does today
        ContentStage.Result cut = Translate("cx_fixalphacut");
        check.Expect(cut.Refused == null && !cut.ForwardOnly && !cut.RouteNote.Contains("forward only"),
                     $"E2: a stage that cuts on its alpha routes as '{cut.RouteNote}'");
        check.Expect(cut.Cuts && cut.AlphaPost && cut.Host.Contains("void CxContentPost()") && cut.Host.Contains("discard;"),
                     "E2: the twin has no post body to make the stage's cut with");

        // Given a transparent hair material on a shader with a deferred twin, with alpha 1 in its middle and soft alpha at its edge
        // When a creature wearing it stands by a lamp
        // Then the middle is lit by the deferred pass and only the edge blends
        check.Expect(Issue.SplitsSolid(washSplit: false, isWash: false, content: true, twin: true, decal: false, AlphaBlendSrc, AlphaBlendDst),
                     "E4: a transparent part on a pair with a twin does not split");

        log.Info(check.Ok ? "ContentStageApprovedExamples.SelfCheck: passed" : "ContentStageApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    private static ContentStage.Result Translate(string resref)
        => ContentStage.Translate(resref, true, n => Stages.GetValueOrDefault(n), AlloyRenderer.ContentEnvFs,
                                  AlloyRenderer.VanillaEnvDefs, "", 8193, 37);
}
