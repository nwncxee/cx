using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The reflections' read of the G-buffer (approved-spec, E1-E5): the shipped <c>cx_gbuf</c> packs a surface the way
/// its writer does, a twin's whole record or a forward draw's reflect-only header, and the reflections' read takes it
/// back against the frame's depth. Oracle: hand-written surfaces and the normal and roughness each reads back as.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>Who wrote the pixel's G0: nobody (the clear), a deferred twin, or a forward draw's reflect-only header.</summary>
    private enum ReflWriter { None, Twin, ReflectOnly }

    /// <summary>One pixel: its writer, the surface it wrote, the frame's view depth there, and what the read answers --
    /// whether it reflects, and with which normal and roughness.</summary>
    private readonly record struct ReflCase(string Name, ReflWriter By, Vector3 N, float Rough, float Coat, float CoatRough,
                                            float ViewDepth, float FrameDepth, bool Reflects, float WantRough);

    private const int ReflIn = 10, ReflOut = 5;

    // Given wet cobbles in rain, by day and by night, drawn by the stock lit surfaces
    // When reflections are on
    // Then the puddles mirror the scene as the previous build does, in a screenshot A/B of the same view
    private static readonly ReflCase[] ReflE1 =
    [
        new("E1 a stock wet cobble reflects with its own normal and roughness", ReflWriter.Twin,
            new Vector3(0f, 0f, 1f), 0.3f, 0f, 0f, 12f, 12f, true, 0.3f),
        new("E1 a tilted cobble 80 m off keeps its tilt", ReflWriter.Twin,
            Vector3.Normalize(new Vector3(0.2f, -0.1f, 1f)), 0.15f, 0f, 0f, 80f, 80f, true, 0.15f),
    ];

    // Given a glossy floor drawn by a content .mtr shader that recolours its own lit output
    // When reflections are on
    // Then the floor mirrors the scene as the previous build does
    private static readonly ReflCase[] ReflE2 =
    [
        new("E2 a forward-only content floor reflects", ReflWriter.ReflectOnly,
            new Vector3(0.6f, 0f, 0.8f), 0.2f, 0f, 0f, 9f, 9f, true, 0.2f),
    ];

    // Given grass standing on wet ground
    // When reflections are on
    // Then the blades show no reflection on themselves, and the wet ground between them still mirrors
    private static readonly ReflCase[] ReflE3 =
    [
        new("E3 a blade 1 m in front of the ground it covers does not reflect", ReflWriter.Twin,
            new Vector3(0f, 0f, 1f), 0.1f, 0f, 0f, 12f, 11f, false, 0f),
        new("E3 the wet ground between the blades reflects", ReflWriter.Twin,
            new Vector3(0f, 0f, 1f), 0.1f, 0f, 0f, 12f, 12f, true, 0.1f),
    ];

    // Given a wet floor seen through a draw that records no reflection (the sky, a hosted glass pane)
    // When reflections are on
    // Then that part of the screen shows no screen reflection, as in the previous build
    private static readonly ReflCase[] ReflE4 =
    [
        new("E4 the sky, which no surface wrote, does not reflect", ReflWriter.None,
            new Vector3(0f, 0f, 1f), 0.1f, 0f, 0f, 500f, 500f, false, 0f),
        new("E4 a pane 6 m off in front of the floor at 12 m does not reflect", ReflWriter.Twin,
            new Vector3(0f, 0f, 1f), 0.1f, 0f, 0f, 12f, 6f, false, 0f),
    ];

    // Given a rain film on flat rough stone
    // When reflections are on
    // Then the reflection in the film is as sharp as in the previous build
    private static readonly ReflCase[] ReflE5 =
    [
        // the film's roughness 0.05 reads back off the 7-bit sqrt lane as (28 / 127)^2 = 0.0486
        new("E5 a full film on rough stone reflects at the film's roughness", ReflWriter.Twin,
            new Vector3(0f, 0f, 1f), 0.8f, 1f, 0.05f, 12f, 12f, true, 0.0486f),
        // coat 0.6 reads back as 76 / 127 = 0.598: 0.8 + (0.0486 - 0.8) x 0.598 = 0.350
        new("E5 a thinning film blends the stone's roughness toward the film's", ReflWriter.Twin,
            new Vector3(0f, 0f, 1f), 0.8f, 0.6f, 0.05f, 12f, 12f, true, 0.350f),
    ];

    private static void SsrGbufSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        // uLmPages gates the lightmap SAMPLERS, which this golden never reaches: a const 0 folds them out
        string chart = ShaderSource.Get("cx_chartbuf.glsl")
            .Replace("uniform int uLmPages;", "const int uLmPages = 0;", StringComparison.Ordinal);
        string src = Version + "layout(local_size_x = 1) in;\n" + chart + ShaderSource.Get("cx_gbuf.glsl")
                   + CaseBlocks(ReflIn, ReflOut) + """
                     void main()
                     {
                         int b = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
                         vec3 N = normalize(vec3(uIn[b], uIn[b + 1], uIn[b + 2]));
                         int by = int(uIn[b + 3]);
                         uvec4 g0 = uvec4(0u);
                         if (by == 1)
                         {
                             CxGbuf g;
                             g.flags = CX_GB_DEFERRED; g.N = N; g.rough = uIn[b + 4]; g.spec = 0.5; g.fresnel = 1.0;
                             g.coat = uIn[b + 5]; g.coatRough = uIn[b + 6]; g.specC = vec3(0.04); g.slot = 0;
                             g.specT = 1.0; g.diffT = vec3(1.0); g.lmLum = 0.0; g.lmMover = vec4(0.0); g.lmShare = vec3(0.0);
                             g.surfFade = vec4(0.0); g.viewDepth = uIn[b + 7];
                             uvec4 w1; uint w2;
                             CxGbufPack(g, g0, w1, w2);
                         }
                         else if (by == 2) g0 = CxGbufReflHead(N, uIn[b + 4], uIn[b + 7]);
                         vec3 n; float rough;
                         bool ok = CxGbufReflRead(g0, uIn[b + 8], 0.0, n, rough);
                         uOut[o] = ok ? 1.0 : 0.0;
                         uOut[o + 1] = n.x; uOut[o + 2] = n.y; uOut[o + 3] = n.z; uOut[o + 4] = rough;
                     }
                     """;
        PipelineId pipe = vk.CreateCompute(src, "golden ssr gbuf");
        check.Expect(!pipe.IsNull, "the reflections' G-buffer read golden's stage builds for the device");
        if (pipe.IsNull) return;
        ReflJudge(vk, check, log, pipe, ReflE1);
        ReflJudge(vk, check, log, pipe, ReflE2);
        ReflJudge(vk, check, log, pipe, ReflE3);
        ReflJudge(vk, check, log, pipe, ReflE4);
        ReflJudge(vk, check, log, pipe, ReflE5);
        vk.Destroy(pipe);
    }

    /// <summary>Each case of one example through <paramref name="pipe"/>, judged against its hand-written answer.</summary>
    private static void ReflJudge(VkBackend vk, Checker check, ExtenderLog log, PipelineId pipe, ReflCase[] cases)
    {
        int n = cases.Length;
        var flat = new float[n * ReflIn];
        for (int i = 0; i < n; i++)
        {
            ReflCase c = cases[i];
            Span<float> row = flat.AsSpan(i * ReflIn, ReflIn);
            row[0] = c.N.X; row[1] = c.N.Y; row[2] = c.N.Z; row[3] = (float)c.By;
            row[4] = c.Rough; row[5] = c.Coat; row[6] = c.CoatRough; row[7] = c.ViewDepth; row[8] = c.FrameDepth;
        }
        float[]? got = RunCases(vk, log, pipe, n, flat, n * ReflOut, "golden ssr gbuf");
        check.Expect(got != null, $"{cases[0].Name}: the golden dispatches and reads back");
        if (got == null) return;
        for (int i = 0; i < n; i++)
        {
            ReflCase c = cases[i];
            int o = i * ReflOut;
            bool reflects = got[o] > 0.5f;
            check.Expect(reflects == c.Reflects, $"{c.Name}: reflects {reflects}, want {c.Reflects}");
            if (!reflects || !c.Reflects) continue;
            // the normal rides two 12-bit snorm lanes, the roughness an 8-bit unorm one
            Near(check, $"{c.Name}, normal x", got[o + 1], c.N.X, 2e-3);
            Near(check, $"{c.Name}, normal y", got[o + 2], c.N.Y, 2e-3);
            Near(check, $"{c.Name}, normal z", got[o + 3], c.N.Z, 2e-3);
            Near(check, $"{c.Name}, roughness", got[o + 4], c.WantRough, 5e-3);
        }
        log.Info($"GpuGolden: {cases[0].Name[..2]} ssr gbuf, {n} cases");
    }
}
