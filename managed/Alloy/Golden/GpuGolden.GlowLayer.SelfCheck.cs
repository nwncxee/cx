using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The glow layer (found work): an additive particle draws as the stock client's 8-bit target takes it, its texel
/// undecoded and added to the display frame with a clamp at 1. Oracle: <c>fsparticle</c> writes the texel straight into
/// a display-space frame under (SrcAlpha, One), so dst + src x a, capped at 1, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    private static void GlowLayerSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string over = Function(ShaderSource.Get("cx_glow.glsl"), "vec3 CxGlowOver(");
        string albedo = Function(ShaderSource.Get("cx_part_fmain.glsl"), "vec3 CxPartAlbedo(");
        string lin = Function(ShaderSource.Get("cx_lightfrag.glsl"), "vec3 CxLinear(");
        string mesh = Function(ShaderSource.Get("cx_owned_f.glsl"), "vec3 CxOwnedAlbedo(");
        string display = Function(ShaderSource.Get("cx_lightfrag.glsl"), "vec3 CxDisplay(");
        check.Expect(over.Length > 0, "glow layer: cx_glow declares CxGlowOver");
        check.Expect(albedo.Length > 0 && lin.Length > 0, "glow layer: cx_part_fmain declares CxPartAlbedo");
        check.Expect(mesh.Length > 0, "glow layer: cx_owned_f declares CxOwnedAlbedo");
        check.Expect(display.Length > 0, "glow layer: cx_lightfrag declares CxDisplay");
        if (over.Length == 0 || albedo.Length == 0 || lin.Length == 0 || mesh.Length == 0 || display.Length == 0) return;

        // Given a one-line function followed by another function in the same library
        // When a golden splices the first one
        // Then it takes that one line alone, so the second is not defined twice beside its own splice
        check.Expect(Function("vec3 A(vec3 c) { return c; }\nvec3 B(vec3 c)\n{\n    return c;\n}\n", "vec3 A(")
                     == "vec3 A(vec3 c) { return c; }\n", "glow layer: a one-line function is spliced alone");

        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(3, 1)
            + ShaderResource.Tokens(lin) + over + albedo + ShaderResource.Tokens(mesh) + ShaderResource.Tokens(display) + """
            void main()
            {
                int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
                float r = uIn[i + 2] < -2.5 ? CxDisplay(CxLinear(vec3(uIn[i])) * uIn[i + 1]).g
                        : uIn[i + 2] < -1.5 ? CxOwnedAlbedo(vec3(uIn[i]), uIn[i + 1] > 0.5).g
                        : uIn[i + 2] < 0.0 ? CxPartAlbedo(vec3(uIn[i]), int(uIn[i + 1])).g
                                           : CxGlowOver(vec3(uIn[i]), vec3(uIn[i + 1])).g;
                uOut[o] = r;
            }
            """, "golden glow layer");
        // composites: a dim frame under a soft glow, a lit frame under a hot core, a raw sky past 1;
        // albedo: one texel as an additive sprite and as an alpha-over one, then on an unlit mesh in and out of the layer
        float[] cases = [0.2f, 0.3f, 0f, 0.6f, 0.7f, 0f, 1.2f, 0.3f, 0f, 0.5f, 1f, -1f, 0.5f, 0f, -1f, 0.5f, 1f, -2f, 0.5f, 0f, -2f,
                          0.5f, 1f, -3f, 0.5f, 2f, -3f];
        string refused = pipe.IsNull ? vk.LastFault : "";
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 9, cases, 9, "golden glow layer");
        vk.Destroy(pipe);
        check.Expect(got != null, $"glow layer: the stage was refused: \"{(refused.Length > 0 ? refused : vk.LastFault)}\"");
        if (got == null) return;

        // Given a display-space frame value of 0.2 and an additive layer of 0.3 over it
        // When the resolve lays the layer on the frame
        // Then the pixel is 0.5: the two added, as the stock client's target adds them
        Near(check, "glow G1: 0.2 under a 0.3 glow", got[0], 0.5, 1e-5);

        // Given a display-space frame value of 0.6 and a hot core of 0.7 over it
        // When the resolve lays the layer on the frame
        // Then the pixel is 1: the sum clamps, so a hot core goes white as it does in the stock client
        Near(check, "glow G2: 0.6 under a 0.7 glow", got[1], 1.0, 1e-5);

        // Given a raw sky pixel already past 1 and a glow over it
        // When the resolve lays the layer on the frame
        // Then the pixel keeps its own value: the layer never darkens what it lands on
        Near(check, "glow G3: 1.2 under a 0.3 glow", got[2], 1.2, 1e-5);

        // Given a texel of 0.5 on an additive particle
        // When the particle stage takes its colour
        // Then the colour is 0.5, undecoded: it lands in the display-space layer as the stock stage writes it
        Near(check, "glow G4: an additive texel of 0.5", got[3], 0.5, 1e-5);

        // Given the same texel on an alpha-over particle
        // When the particle stage takes its colour
        // Then the colour is decoded to linear, since that particle still draws into the linear scene
        Near(check, "glow G5: an alpha-over texel of 0.5", got[4], Math.Pow(0.5, AlloyLights.SrgbGamma), 1e-4);

        // Given a texel of 0.5 on an additive mesh, such as a flame plane
        // When the unlit mesh stage takes its colour for the glow layer
        // Then the colour is 0.5, undecoded, as the stock unlit stage writes it into its display-space target
        Near(check, "glow G7: an additive mesh texel of 0.5", got[5], 0.5, 1e-5);

        // Given the same texel on an unlit mesh that draws into the linear scene
        // When the unlit mesh stage takes its colour
        // Then the colour is decoded to linear, as before
        Near(check, "glow G8: a scene mesh texel of 0.5", got[6], Math.Pow(0.5, AlloyLights.SrgbGamma), 1e-4);

        // Given a texel of 0.5 on a lit additive mesh under a light of exactly 1
        // When the lit stage writes its colour for the glow layer
        // Then the colour is 0.5: the stage's linear result goes back to display space, as the stock target holds it
        Near(check, "glow G9: a lit additive texel of 0.5 under a light of 1", got[7], 0.5, 1e-4);

        // Given the same texel under a light of 2
        // When the lit stage writes its colour for the glow layer
        // Then the light scales it by 2 to the display power, so light still brightens the glow
        Near(check, "glow G10: a lit additive texel of 0.5 under a light of 2", got[8],
             0.5 * Math.Pow(2.0, 1.0 / AlloyLights.SrgbGamma), 1e-4);
    }
}
