using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Light through a transparent part (approved-spec, E11, E14, E15): the deferred rig's walk for a placed light
/// and the shipped <c>CxSunLight</c> for the sun. Oracle: the same wall facing the light head-on and opaque, whose
/// pixel is the head-on brightness the stock term takes its share of (<c>inc_lighting.shd</c>:305-319), and for the
/// sun its colour, 1. A ghost draws forward; the deferred walk's <c>CxTranslucent</c> is the term both paths take.
/// </summary>
internal static partial class GpuGolden
{
    private static void TranslucenceApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        static RigPixel Lit(RigPixel p) => p with { DiffT = Vector3.One };
        if (ParityCentre(vk, check, log, ParityScene(Vector3.UnitZ, -1f, 1f, Lit), "CX-449 the wall facing its lamp") is not { } head)
            return;
        check.Expect(head.Y > 1e-3f, FormattableString.Invariant($"CX-449: the lamp lights the wall facing it ({head.Y:F4})"));

        // Given a part drawn at 50% opacity, with one placed light straight behind it
        // When it draws
        // Then its near face receives half the head-on brightness of that light, as stock does
        if (ParityCentre(vk, check, log, ParityScene(-Vector3.UnitZ, -1f, 0.5f, Lit), "CX-449 E11 a half-opaque wall, the lamp behind") is { } half)
            check.Expect(Math.Abs(half.Y - 0.5f * head.Y) <= 1e-3f * head.Y + 1e-5f,
                         FormattableString.Invariant($"CX-449 E11: the lamp behind gives {half.Y:F4}, half the head-on {head.Y:F4}"));

        // Given a ghosted creature drawn at 50% opacity, with a shadow-casting torch straight behind it
        // When it draws
        // Then the face toward the camera receives the torch through the creature, as stock does, and is not darkened by the creature's own shadow
        if (ParityCentre(vk, check, log, ParityScene(-Vector3.UnitZ, 0f, 0.5f, Lit), "CX-449 E15 a ghost, its torch's shadow on it") is { } ghost)
            check.Expect(Math.Abs(ghost.Y - 0.5f * head.Y) <= 1e-3f * head.Y + 1e-5f,
                         FormattableString.Invariant($"CX-449 E15: the shadowed torch behind gives {ghost.Y:F4}, half the head-on {head.Y:F4}"));

        SunThroughApproved(vk, check, log);
    }

    /// <summary>E14: the shipped <c>CxSunLight</c> on a surface facing straight away from a white sun, at 50% opacity.</summary>
    private static void SunThroughApproved(VkBackend vk, Checker check, ExtenderLog log)
    {
        string stage = SunStage(ShaderSource.Get("cx_lightfrag.glsl"));
        check.Expect(stage.Length > 0, "CX-449 E14: cx_lightfrag declares CxWrap, CxSunLight and CxTranslucent");
        if (stage.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 1) + stage + """
            void main()
            {
                cxTransl = 1.0 - uIn[0];
                vec3 spec;
                uOut[0] = CxSunLight(vec3(0.0, 0.0, -1.0), 1.0, 1.0, spec).g;
            }
            """, "golden sun through");
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 1, [0.5f], 1, "golden sun through");
        vk.Destroy(pipe);
        check.Expect(got != null, "CX-449 E14: the sun stage was refused (logged above)");
        if (got == null) return;

        // Given a part drawn at 50% opacity, with the sun straight behind it
        // When it draws
        // Then its near face receives half the sun's head-on brightness, as stock does
        check.Expect(Math.Abs(got[0] - 0.5f) < 1e-4f, FormattableString.Invariant($"CX-449 E14: the sun behind gives {got[0]:F4}, half its 1"));
    }
}
