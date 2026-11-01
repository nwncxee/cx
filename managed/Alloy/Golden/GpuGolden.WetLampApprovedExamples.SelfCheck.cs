using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The near-lamp golden (approved-spec, E8): the shipped <c>CxFilmStatics</c> over a two-entry froxel list, a baked lamp
/// of colour 1 and a dynamic light of colour 10. Near the camera the lamp moves to the list's live head; the film must
/// mirror it the same at either end and take nothing from the dynamic light. Oracle: the lamp's colour, written by hand;
/// light, lobe and visibility are stubs at 1.
/// </summary>
internal static partial class GpuGolden
{
    private static void WetNearLamp(VkBackend vk, Checker check, ExtenderLog log)
    {
        string mat = ShaderSource.Get("cx_material.glsl");
        string fn = Function(mat, "vec3 CxFilmStatics("), cap = Declaration(mat, "CX_FILM_LIGHT_MAX");
        string isStatic = Function(ShaderSource.Get("cx_lightfrag.glsl"), "bool CxLightIsStatic(");
        bool have = fn.Length > 0 && cap.Length > 0 && isStatic.Length > 0;
        check.Expect(have, "wet E8: cx_material declares CxFilmStatics and CX_FILM_LIGHT_MAX, cx_lightfrag CxLightIsStatic");
        if (!have) return;
        string lamp = AlloyLights.LightStatic.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 1) + cap + isStatic + """
            const float uDayDim = 1.0;
            uint gLamp;
            vec4 uLights[4];
            uint CxCluster(float d) { return 0u; }
            uint CxListWalk(uint cl) { return 2u; }
            uint CxListIdx(uint cl, uint k) { return k == gLamp ? 0u : 1u; }
            int CxLightSlot(int w) { return -1; }
            float CxSrcSpread(float s) { return 0.0; }
            float CxCoatLobe(vec3 N, vec3 V, vec3 L, float a, float s) { return 1.0; }
            float CxLocalShadowQ(int s, vec3 p, vec3 n, float ndl, float m, float d, bool b) { return 1.0; }
            bool CxPointLight(vec4 pr, vec3 col, vec3 p, vec3 N, out vec3 c, out vec3 L, out float att, out float f2, out float ndlRaw, out float ndl)
            { c = col; L = vec3(0.0, 0.0, 1.0); att = 1.0; f2 = 0.0; ndlRaw = 1.0; ndl = 1.0; return true; }
            """ + fn + """
            void main()
            {
                uint i = gl_WorkGroupID.x;
                gLamp = uint(uIn[i * uint(CX_GOLD_IN)]);
                uLights[0] = vec4(0.0); uLights[1] = vec4(1.0, 1.0, 1.0, CX_LAMP_W);
                uLights[2] = vec4(0.0); uLights[3] = vec4(10.0, 10.0, 10.0, 0.0);
                uOut[i] = CxFilmStatics(vec3(0.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), 5.0, 1.0, 0.05, 0.0).g;
            }
            """.Replace("CX_LAMP_W", lamp, StringComparison.Ordinal), "golden wet lamp");
        float[] cases = [1f, 0f];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 2, cases, 2, "golden wet lamp");
        vk.Destroy(pipe);
        check.Expect(got != null, "wet E8: the lamp stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant($"GpuGolden: wet lamp, film image {got[0]:F3} at the list's tail, {got[1]:F3} at its head"));

        // Given standing water reflecting a lamp
        // When the camera moves in from 20 m to 1 m
        // Then the reflection stays, with no hard edge where it cuts off
        check.Expect(Math.Abs(got[0] - 1f) < 1e-4f && Math.Abs(got[1] - 1f) < 1e-4f,
            $"wet E8: the film mirrors the lamp {got[0]:F3} from the cold run and {got[1]:F3} from the live run; both must be its colour 1");
    }
}
