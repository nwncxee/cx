using System.Collections.Generic;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Which content stages draw through the G-buffer (approved-spec, E1, E2, E4). Oracle: the stages written here by hand,
/// each the shape of one a server ships, and the route a person expects of that shape.
/// </summary>
internal static class ContentStageApprovedExamples
{
    private static readonly Dictionary<string, string> Stages = new()
    {
        ["cx_fixalpha"] = "#include \"inc_standard\"\nvoid main()\n{\n    FragmentColor = vec4(1.0);\n    SetupStandardShaderInputs();\n"
                        + "    float a = texture2D(texUnit1, vTexCoords.xy).a;\n    ApplyStandardShader();\n"
                        + "    gl_FragColor = vec4(FragmentColor.rgb, a);\n}\n",
        ["cx_fixalphacut"] = "#include \"inc_standard\"\nuniform float fCut;\nvoid main()\n{\n    FragmentColor = vec4(1.0);\n"
                           + "    SetupStandardShaderInputs();\n    ApplyStandardShader();\n"
                           + "    gl_FragColor = vec4(FragmentColor.rgb, texture2D(texUnit3, vTexCoords.xy).r);\n"
                           + "    if (gl_FragColor.a < fCut)\n    {\n        discard;\n    }\n}\n",
        ["cx_fixshadowlocal"] = "#include \"inc_standard\"\nvoid main()\n{\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n    vec4 FragmentColor = vec4(0.25, 0.5, 0.75, 1.0);\n    gl_FragColor = FragmentColor;\n}\n",
        ["cx_fixshadowparam"] = "#include \"inc_standard\"\nvoid Hand(vec4 FragmentColor)\n{\n    gl_FragColor = FragmentColor;\n}\nvoid main()\n{\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n    Hand(vec4(0.25, 0.5, 0.75, 1.0));\n}\n",
        ["cx_fixshadowblock"] = "#include \"inc_standard\"\nuniform int iTint;\nvoid main()\n{\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n    if (iTint == 1)\n    {\n        vec4 FragmentColor = vec4(0.25, 0.5, 0.75, 1.0);\n        gl_FragColor = FragmentColor;\n        return;\n    }\n    gl_FragColor = FragmentColor;\n}\n",
        ["cx_fixshadowstruct"] = "#include \"inc_standard\"\nstruct Layer { vec4 FragmentColor; float w; };\nvoid main()\n{\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n    Layer l;\n    l.FragmentColor = FragmentColor;\n    l.w = 1.0;\n    gl_FragColor = l.FragmentColor * l.w;\n}\n",
        ["cx_fixshadowmulti"] = "#include \"inc_standard\"\nvoid main()\n{\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n    vec4 tint = vec4(1.0), FragmentColor = vec4(0.25, 0.5, 0.75, 1.0);\n    gl_FragColor = FragmentColor * tint;\n}\n",
        ["cx_fixshadowinit"] = "#include \"inc_standard\"\nvoid main()\n{\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n    vec4 FragmentColor = FragmentColor * vec4(1.1, 1.0, 0.9, 1.0);\n    gl_FragColor = FragmentColor;\n}\n",
        ["cx_fixshadowcall"] = "#include \"inc_standard\"\nvoid main()\n{\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n    vec4 c = mix(vec4(0.0), FragmentColor, 0.5);\n    gl_FragColor = c;\n}\n",
        ["cx_fixshadowmember"] = "#include \"inc_standard\"\nstruct Layer { vec4 FragmentColor; };\nvoid main()\n{\n    SetupStandardShaderInputs();\n    ApplyStandardShader();\n    Layer l;\n    l.FragmentColor = vec4(0.25, 0.5, 0.75, 1.0);\n    vec4 FragmentColor = l.FragmentColor;\n    gl_FragColor = FragmentColor;\n}\n",
    };

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ContentStageApprovedExamples.SelfCheck");
        (_, string litFs) = VkPrograms.LitPair(log);

        // Given a material whose fragment shader only sets its alpha after ApplyStandardShader, as fs_plt_hair does
        // When the shader is translated
        // Then cx.log names a deferred twin for it and does not call it forward only
        ContentStage.Result alpha = Translate("cx_fixalpha");
        check.Expect(alpha.Refused == null && ContentPrograms.GetsDeferredTwin(alpha, litFs) && !alpha.RouteNote.Contains("forward only"),
                     $"E1: an alpha-only stage routes as '{alpha.RouteNote}'");

        // Given a material whose fragment shader sets its alpha after ApplyStandardShader and then discards below a cutoff, as fslit_hair_nm does
        // When the shader is translated
        // Then cx.log names a deferred twin for it, and a pixel below the cutoff draws nothing, as it does today
        ContentStage.Result cut = Translate("cx_fixalphacut");
        check.Expect(cut.Refused == null && ContentPrograms.GetsDeferredTwin(cut, litFs) && !cut.RouteNote.Contains("forward only"),
                     $"E2: a stage that cuts on its alpha routes as '{cut.RouteNote}'");
        check.Expect(cut.Cuts && cut.AlphaPost && cut.Host.Contains("void CxContentPost()") && cut.Host.Contains("discard;"),
                     "E2: the twin has no post body to make the stage's cut with");

        // Given a transparent hair material on a shader with a deferred twin, with alpha 1 in its middle and soft alpha at its edge
        // When a creature wearing it stands by a lamp
        // Then the middle is lit by the deferred pass and only the edge blends
        check.Expect(Issue.SplitsSolid(wash: 0, twin: true, decal: false, Issue.AuroraSrcAlpha, Issue.AuroraOneMinusSrcAlpha),
                     "E4: a transparent part on a pair with a twin does not split");

        // Given a material whose fragment shader declares its own local FragmentColor and hands it to gl_FragColor
        // When the shader is translated
        // Then the colour it hands over reaches the part, and is not the local assigned to itself
        ContentStage.Result local = Translate("cx_fixshadowlocal");
        check.Expect(local.Refused == null && local.Host.Contains("vec4 " + ContentStage.ShadowName + " = vec4(0.25, 0.5, 0.75, 1.0);")
                     && local.Host.Contains("FragmentColor = " + ContentStage.ShadowName + ";") && !local.Host.Contains("FragmentColor = FragmentColor;"),
                     $"E5: a local FragmentColor's hand-off does not reach the part ({local.Refused})");

        // Given a material whose helper takes a parameter named FragmentColor and hands it to gl_FragColor
        // When the shader is translated
        // Then the parameter's colour reaches the part
        ContentStage.Result param = Translate("cx_fixshadowparam");
        check.Expect(param.Refused == null && param.Host.Contains("void Hand(vec4 " + ContentStage.ShadowName + ")")
                     && param.Host.Contains("FragmentColor = " + ContentStage.ShadowName + ";"),
                     $"E6: a parameter FragmentColor's hand-off does not reach the part ({param.Refused})");

        // Given a material that declares FragmentColor inside one branch and hands over the stock colour after it
        // When the shader is translated
        // Then the branch hands over its own colour, and the line after the branch still hands over the stock one
        ContentStage.Result block = Translate("cx_fixshadowblock");
        check.Expect(block.Refused == null && block.Host.Contains("        FragmentColor = " + ContentStage.ShadowName + ";")
                     && block.Host.Contains("    }\n    FragmentColor = FragmentColor;"),
                     $"E7: a branch's own FragmentColor is not kept to its branch ({block.Refused})");

        // Given a material that keeps a struct with a member named FragmentColor and hands that member to gl_FragColor
        // When the shader is translated
        // Then the member keeps its name, and the part draws the colour the member holds
        ContentStage.Result member = Translate("cx_fixshadowstruct");
        check.Expect(member.Refused == null && member.Host.Contains("struct Layer { vec4 FragmentColor; float w; };")
                     && member.Host.Contains("FragmentColor = l.FragmentColor * l.w;"),
                     $"E9: a struct member named FragmentColor is renamed or its hand-off lost ({member.Refused})");

        // Given a material that declares FragmentColor as the second of two locals in one declaration
        // When the shader is translated
        // Then the colour it hands over reaches the part
        ContentStage.Result multi = Translate("cx_fixshadowmulti");
        check.Expect(multi.Refused == null && multi.Host.Contains("vec4 tint = vec4(1.0), " + ContentStage.ShadowName + " = vec4(0.25, 0.5, 0.75, 1.0);")
                     && multi.Host.Contains("FragmentColor = " + ContentStage.ShadowName + " * tint;"),
                     $"E10: a FragmentColor declared second in a declaration does not reach the part ({multi.Refused})");

        // Given a material that tints the stock colour into its own local FragmentColor
        // When the shader is translated
        // Then the tint reads the stock colour, and the tinted colour reaches the part
        ContentStage.Result init = Translate("cx_fixshadowinit");
        check.Expect(init.Refused == null && init.Host.Contains("vec4 " + ContentStage.ShadowName + " = FragmentColor * vec4(1.1, 1.0, 0.9, 1.0);")
                     && init.Host.Contains("FragmentColor = " + ContentStage.ShadowName + ";"),
                     $"E11: a local FragmentColor's initialiser does not read the stock colour ({init.Refused})");

        // Given a material that passes the stock FragmentColor to a call after another argument
        // When the shader is translated
        // Then the call still reads the stock colour
        ContentStage.Result call = Translate("cx_fixshadowcall");
        check.Expect(call.Refused == null && call.Host.Contains("vec4 c = mix(vec4(0.0), FragmentColor, 0.5);")
                     && !call.Host.Contains(ContentStage.ShadowName),
                     $"E12: a call's FragmentColor argument is taken for a declaration ({call.Refused})");

        // Given a material that reads a struct member named FragmentColor into its own local FragmentColor
        // When the shader is translated
        // Then the member keeps its name, and the colour it held reaches the part
        ContentStage.Result inScope = Translate("cx_fixshadowmember");
        check.Expect(inScope.Refused == null && inScope.Host.Contains("vec4 " + ContentStage.ShadowName + " = l.FragmentColor;")
                     && inScope.Host.Contains("FragmentColor = " + ContentStage.ShadowName + ";"),
                     $"E13: a member read beside a local FragmentColor is renamed ({inScope.Refused})");

        // Given a stage whose braces do not balance
        // When it is translated
        // Then the stage reaches the translation as the server wrote it
        const string unbalanced = "void main()\n{\n    vec4 FragmentColor = vec4(1.0);\n    gl_FragColor = FragmentColor;\n}\n}\n";
        check.Expect(ContentStage.Unshadow(unbalanced) == unbalanced, "E8: a stage whose braces do not balance is rewritten");

        log.Info(check.Ok ? "ContentStageApprovedExamples.SelfCheck: passed" : "ContentStageApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    private static ContentStage.Result Translate(string resref)
        => ContentStage.Translate(resref, true, n => Stages.GetValueOrDefault(n), AlloyRenderer.ContentEnvFs,
                                  AlloyRenderer.VanillaEnvDefs, "", 8193, 37);
}
