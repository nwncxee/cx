using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// What the screen-space reflection's stages share: the ray budget the trace reads, the period line's clause and the
/// stages' contract check. The pass itself is <c>AlloyRenderer.VkSsr</c>.
/// </summary>
internal static class Ssr
{
    /// <summary>Cell visits per ray at each <c>light.ssrdetail</c> (full, half, quarter): a coarser confirm needs fewer. A ray
    /// grazing the ground it left never skips a coarse cell (the ground's near edge is in it), so it pays ~2 visits a
    /// pixel; 40 ran dry a few hundred pixels short of a building at low camera pitch and left the probe's sky.</summary>
    internal static ReadOnlySpan<float> Steps => [160f, 96f, 48f];

    /// <summary>The period line's clause: whether the reflections are asked for at all.</summary>
    internal static string Stats(int frames) => $"ssr {(Lighting.Ssr.V ? "on" : "off")}";

    /// <summary>Off-device: the three stages split the work as their contract says. Oracle: hand-written literals
    /// read against the embedded GLSL text, a second artifact.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string trace = ShaderSource.Get("cx_ssrtrace_f.glsl"), res = ShaderSource.Get("cx_ssrres_f.glsl");
        string tmp = ShaderSource.Get("cx_ssrtmp_f.glsl"), lib = ShaderSource.Get("cx_ssr.glsl");
        check.Expect(!trace.Contains(" uColor;") && !trace.Contains("samplerCube uProbe;") && !trace.Contains(" uPlanar;") && res.Contains(" uPlanar;"),
          "the trace fetches no colour, probe or mirror: it writes the hit and its pdf alone");
        check.Expect(trace.Contains("vec4(ph / size, pdf, k)") && trace.Contains("CxSsrNoise(ivec2(gl_FragCoord.xy), uFrame)"),
          "the trace's direction is drawn per frame and its pdf goes out with the hit");
        check.Expect(res.Contains("const ivec2 CX_SSR_REUSE[3]") && res.Contains("for (int j = 0; j < 4; j++)")
          && res.Contains("* NoL / t.z;"),
          "each pixel weighs its own texel's hit and three neighbours' by its own lobe over the ray's pdf");
        check.Expect(res.Contains("c / (1.0 + Luma(c)) * wt") && res.Contains("m / max(1.0 - Luma(m), 1e-3)"),
          "the resolve averages highlight-reduced and undoes it after");
        check.Expect(!trace.Contains("COAT") && !res.Contains("COAT") && res.Contains("float cone = "),
          "the mip is the lobe's cone; no coat cap stands in for it");
        check.Expect(tmp.Contains("(d + max(cur.a, 0.0)) / max(d, 1e-4)") && tmp.Contains("clamp(textureLod(uHist, huv, 0.0).rgb, mn, mx)")
          && tmp.Contains("if (uTmp.x > 0.0 && depth < 1.0)"),
          "the history reprojects by the hit's virtual point, clamps to the neighbourhood, and a reset shows the resolve alone");
        check.Expect(lib.Contains("vec2 CxSsrNoise(ivec2 p, int frame)") && lib.Contains("float CxGgxD(float NoH, float a)"),
          "the noise and the lobe live in the shared library both stages include");
        log.Info(check.Ok ? "Ssr.SelfCheck: passed" : "Ssr.SelfCheck: FAILED");
    }
}
