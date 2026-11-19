using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// A content stage run whole: its <c>main()</c> spliced as the hosted post chain splices it, into the shipped post
/// wrapper's globals and <c>CxStage</c>, on the device. Oracle: the colour each hand-written main writes to its output.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>What every hosted main below writes to its output, by hand.</summary>
    private static readonly float[] HostedWant = [0.25f, 0.5f, 0.75f];

    /// <summary>The shapes a content main hands its colour over in, each writing <see cref="HostedWant"/>.</summary>
    private static readonly (string Name, string Text)[] HostedMains =
    [
        ("a direct write", "void main()\n{\n    gl_FragColor = vec4(0.25, 0.5, 0.75, 1.0);\n}\n"),
        ("the global working colour handed on", "void main()\n{\n    FragmentColor = vec4(0.25, 0.5, 0.75, 1.0);\n    gl_FragColor = FragmentColor;\n}\n"),
        ("a local named FragmentColor", "void main()\n{\n    vec4 FragmentColor = vec4(0.25, 0.5, 0.75, 1.0);\n    gl_FragColor = FragmentColor;\n}\n"),
        ("a parameter named FragmentColor", "void Hand(vec4 FragmentColor)\n{\n    gl_FragColor = FragmentColor;\n}\n"
                                          + "void main()\n{\n    Hand(vec4(0.25, 0.5, 0.75, 1.0));\n}\n"),
        ("the first data output", "void main()\n{\n    gl_FragData[0] = vec4(0.25, 0.5, 0.75, 1.0);\n}\n"),
    ];

    /// <summary>The shipped wrapper's own globals, one <c>vec4</c> per line: what the hosted text writes into.</summary>
    private static readonly Regex WrapperGlobal = new(@"(?m)^vec4 \w+;$");

    private static void HostedMainApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string wrapper = ShaderSource.Get("cx_fbpost_f.glsl");
        int region = wrapper.IndexOf("// $VANILLA-MAIN ", StringComparison.Ordinal);
        string stage = Function(wrapper, "vec4 CxStage(");
        check.Expect(region > 0 && stage.Length > 0, "cx_fbpost_f carries the whole-stage region and CxStage");
        if (region <= 0 || stage.Length == 0) return;
        string globals = string.Join("\n", WrapperGlobal.Matches(wrapper[..region]).Select(m => m.Value)) + "\n";
        foreach ((string name, string text) in HostedMains)
        {
            // Given a content fsfbpostpr whose main hands over (0.25, 0.5, 0.75) as <name>
            // When the post stack runs it whole inside cx_fbpost_f
            // Then the stage hands on (0.25, 0.5, 0.75)
            string? body = VanillaShader.MainText(text, "cx_goldmain", log, "golden hosted main", new List<string>(), wrapper[..region]);
            check.Expect(body != null, $"hosted main, {name}: the splice found a main() to run");
            if (body == null) continue;
            PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 4) + globals + body + stage + """
                void main()
                {
                    vec4 o = CxStage(vec4(0.0, 0.0, 0.0, 1.0));
                    for (int k = 0; k < CX_GOLD_OUT; k++) uOut[k] = o[k];
                }
                """, "golden hosted main");
            float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 1, [0f], 4, "golden hosted main");
            vk.Destroy(pipe);
            check.Expect(got != null, $"hosted main, {name}: the stage was refused (logged above)");
            if (got == null) continue;
            bool same = MathF.Abs(got[0] - HostedWant[0]) < 1e-6f && MathF.Abs(got[1] - HostedWant[1]) < 1e-6f
                     && MathF.Abs(got[2] - HostedWant[2]) < 1e-6f;
            check.Expect(same, FormattableString.Invariant(
                $"hosted main, {name}: the stage hands on ({got[0]:F3}, {got[1]:F3}, {got[2]:F3}), not the (0.25, 0.5, 0.75) the main wrote"));
        }
    }
}
