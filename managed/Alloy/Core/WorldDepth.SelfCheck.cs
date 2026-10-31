using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

internal static partial class WorldDepth
{
    /// <summary>The folds and the content rewrite on fixtures. Oracle: a GL perspective with near 1 and far 3 worked by
    /// hand (M33 -2, M43 -3, M34 -1), its folded entries and clip depths at the two planes, and rewritten text by hand.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var gl = new Matrix4x4 { M11 = 1f, M22 = 1f, M33 = -2f, M34 = -1f, M43 = -3f };
        Matrix4x4 r = Fold(gl), s = FoldGl(gl), u = Unfold(r);
        check.Expect(r.M33 == 0.5f && r.M43 == 1.5f && r.M34 == -1f && r.M11 == 1f,
            $"the reversed fold of near 1 / far 3 is M33 0.5, M43 1.5 (got {r.M33}, {r.M43})");
        check.Expect(s.M33 == -1.5f && s.M43 == -1.5f && s.M34 == -1f, $"GL's window fold is M33 -1.5, M43 -1.5 (got {s.M33}, {s.M43})");
        check.Expect(u.M33 == -2f && u.M43 == -3f, $"unfolding the reversed fold gives GL's M33 -2, M43 -3 back (got {u.M33}, {u.M43})");
        check.Expect(r.M33 * -1f + r.M43 == 1f && r.M33 * -3f + r.M43 == 0f, "reversed: the near plane lands at depth 1, the far at 0");
        check.Expect(s.M33 * -1f + s.M43 == 0f && s.M33 * -3f + s.M43 == 3f, "GL's window fold: near at 0, far at w (depth 1)");
        string text = VanillaShader.GlDepth("a = texture2D(texFBDepth, uv).x + textureLod( texFBDepth , uv, 0.0).x"
                                          + " + texelFetch(texFBDepth, p, 0).x + gl_FragCoord.z + gl_FragCoord.zw.x + texture(uOther, uv).x;");
        check.Expect(text == "a = CxGlDepth(texFBDepth, uv).x + CxGlDepthLod(texFBDepth, uv, 0.0).x"
                          + " + CxGlDepthFetch(texFBDepth, p, 0).x + cxGlFragZ + gl_FragCoord.zw.x + texture(uOther, uv).x;",
            $"content reads GL's depth: each texFBDepth read and gl_FragCoord.z rewritten, nothing else ({text})");
        const string vs = "#version 460 core\nvoid main() { gl_Position = vec4(0.0); }\n";
        string fs = "#version 460 core\nout vec4 o;\nvoid main() { o = vec4(" + VanillaShader.GlDepth("gl_FragCoord.z") + "); }\n";
        string? world = Translate(vs, fs, new Dictionary<string, uint>()).Fs, cast = VkGlsl.Translate(vs, fs, new Dictionary<string, uint>()).Fs;
        check.Expect(world != null && world.Contains("o = vec4((1.0 - gl_FragCoord.z));"), $"a world program's fragment depth reads back as GL's ({world})");
        check.Expect(cast != null && cast.Contains("o = vec4(gl_FragCoord.z);"), $"a caster or GUI program, which keeps GL's depth, reads its own fragment depth ({cast})");
        log.Info(check.Ok ? "WorldDepth.SelfCheck: passed" : "WorldDepth.SelfCheck: FAILED");
    }
}
