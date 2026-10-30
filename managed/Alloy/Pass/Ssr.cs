using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// What the screen-space reflection's stages share: the ray budget the trace reads, the period line's clause and the
/// stages' contract check. The pass itself is <c>AlloyRenderer.VkSsr</c>.
/// </summary>
internal static class Ssr
{
    /// <summary>Cell visits per ray at each <c>light.ssrdetail</c> (full, half, quarter), whose finest hi-Z level is the
    /// same rung: a coarser confirm needs fewer. A ray grazing the ground it left never skips a coarse cell, so it pays
    /// ~2 visits a pixel; 40 ran dry short of a building at low camera pitch and left the probe's sky.</summary>
    internal static ReadOnlySpan<float> Steps => [160f, 96f, 48f];

    /// <summary>The period line's clause: whether the reflections are asked for at all.</summary>
    internal static string Stats(int frames) => $"ssr {(Lighting.Ssr.V ? "on" : "off")}";

    /// <summary>Off-device: the ray budget has a rung for every <c>light.ssrdetail</c> choice (oracle: the knob's own
    /// declaration), and the trace samples no colour, probe or mirror (oracle: the trace's GLSL text).</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string trace = ShaderSource.Get("cx_ssrtrace_f.glsl");
        check.Expect(Steps.Length == Lighting.SsrDetail.Values.Length, "every light.ssrdetail choice has its ray budget");
        check.Expect(!trace.Contains(" uColor;") && !trace.Contains("samplerCube uProbe;") && !trace.Contains(" uPlanar;"),
          "the trace fetches no colour, probe or mirror: it writes the hit and its pdf alone");
        log.Info(check.Ok ? "Ssr.SelfCheck: passed" : "Ssr.SelfCheck: FAILED");
    }
}
