using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The ground mist's shape and its own motion: the shipped <c>CxVfMist</c> over an even field of
/// mist on a floor at 0 m, read along a 32 m street. Oracle, by hand: a standing character's waist is 1 m up.</summary>
internal static partial class GpuGolden
{
    /// <summary>The shipped functions the shape golden splices, in the order they declare.</summary>
    private static readonly string[] MistLibrary = { "float CxMistDensity(", "float CxMistTop(", "float CxVfMist(" };

    /// <summary>Floats per shape case: the point (x, y, z), the field's height, how far the wind carried it (x, y), its clock.</summary>
    private const int MistCaseFloats = 7;

    /// <summary>The points read along the street, and the heights read: the ground, half way, a standing character's waist.</summary>
    private const int StreetPoints = 64;
    private const float Ground = 0.1f, HalfWay = 0.5f, Waist = 1f;

    /// <summary>A field 1 m deep, as the docks' fog amount 3 lays it, and one 2 m deep, the deepest area fog lays.</summary>
    private const float DocksDepth = 1f, DeepDepth = 2f;

    /// <summary>Seconds the motion examples wait, and the wind they blow in, metres a second along the street.</summary>
    private const float Wait = 3f, StreetWind = 2f;

    private static void MistShapeApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string src = ShaderSource.Get("cx_vfog_cs.glsl"), lib = Declaration(src, "CX_MIST_CAP");
        foreach (string sig in MistLibrary)
        {
            string fn = Function(src, sig);
            check.Expect(fn.Length > 0, $"mist shape: cx_vfog_cs declares {sig}");
            if (fn.Length == 0) return;
            lib += fn;
        }
        var cases = new List<float>();
        int low = Street(cases, Ground, DocksDepth, 0f, 0f), waist = Street(cases, Waist, DocksDepth, 0f, 0f);
        int deepLow = Street(cases, Ground, DeepDepth, 0f, 0f), deepWaist = Street(cases, Waist, DeepDepth, 0f, 0f);
        int half = Street(cases, HalfWay, DocksDepth, 0f, 0f);
        int windy = Street(cases, HalfWay, DocksDepth, StreetWind * Wait, Wait), still = Street(cases, HalfWay, DocksDepth, 0f, Wait);
        int n = cases.Count / MistCaseFloats;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(MistCaseFloats, 1)
            + "vec4 uMistHead, uMistFlow;\nfloat gHeight;\nvec4 CxMistTap(ivec2 c, int side) { return vec4(0.0, gHeight, 0.0, 0.0); }\n"
            + ShaderSource.Get("cx_noise.glsl") + lib + """
            void main()
            {
                uint b = gl_WorkGroupID.x * uint(CX_GOLD_IN);
                gHeight = uIn[b + 3u];
                uMistHead = vec4(-128.0, -128.0, 256.0, 1.0);
                uMistFlow = vec4(uIn[b + 4u], uIn[b + 5u], uIn[b + 6u], 0.0);
                uOut[gl_WorkGroupID.x * uint(CX_GOLD_OUT)] = CxVfMist(vec3(uIn[b], uIn[b + 1u], uIn[b + 2u]));
            }
            """, "golden mist shape");
        check.Expect(!pipe.IsNull, "mist shape: the stage compiles (a refusal is logged above)");
        if (pipe.IsNull) return;
        float[]? got = RunCases(vk, log, pipe, n, cases.ToArray(), n, "golden mist shape");
        vk.Destroy(pipe);
        check.Expect(got != null, "mist shape: the stage was refused (logged above)");
        if (got == null) return;
        ReadOnlySpan<float> all = got;
        LiesLow(check, log, all.Slice(low, StreetPoints), all.Slice(waist, StreetPoints), all.Slice(deepLow, StreetPoints), all.Slice(deepWaist, StreetPoints));
        UnevenTop(check, all.Slice(half, StreetPoints));
        Moves(check, all.Slice(half, StreetPoints), all.Slice(windy, StreetPoints), all.Slice(still, StreetPoints));
    }

    /// <summary>Add the street's points at <paramref name="z"/> over a field <paramref name="height"/> deep, the wind having carried it
    /// <paramref name="carried"/> metres along the street and its clock at <paramref name="clock"/>; returns the first point's case.</summary>
    private static int Street(List<float> cases, float z, float height, float carried, float clock)
    {
        int first = cases.Count / MistCaseFloats;
        for (int i = 0; i < StreetPoints; i++)
            cases.AddRange([-15.75f + i * 0.5f, 0.3f, z, height, carried, 0f, clock]);
        return first;
    }

    private static float MeanOf(ReadOnlySpan<float> v)
    {
        float sum = 0f;
        foreach (float x in v) sum += x;
        return sum / v.Length;
    }

    private static void LiesLow(Checker check, ExtenderLog log, ReadOnlySpan<float> low, ReadOnlySpan<float> waist, ReadOnlySpan<float> deepLow, ReadOnlySpan<float> deepWaist)
    {
        float l = MeanOf(low), w = MeanOf(waist), dl = MeanOf(deepLow), dw = MeanOf(deepWaist);
        log.Info(FormattableString.Invariant($"GpuGolden: mist shape, 1 m field {l:F4} on the ground, {w:F4} at the waist; 2 m field {dl:F4}, {dw:F4}"));

        // Given the Waterdeep docks (fog amount 3) with Volumetric fog and Ground mist on
        // When the player stands on the street
        // Then the mist is thickest on the ground and gone by a standing character's waist.
        check.Expect(l > 0f && w < l * 0.05f, $"E27 over 1 m of mist the street holds {l:F4} on the ground and {w:F4} at the waist (want some, then under 5% of it)");
        check.Expect(dl > 0f && dw < dl * 0.05f, $"E27 over 2 m of mist the street holds {dl:F4} on the ground and {dw:F4} at the waist (want some, then under 5% of it)");
    }

    private static void UnevenTop(Checker check, ReadOnlySpan<float> half)
    {
        float mean = MeanOf(half), sq = 0f;
        foreach (float x in half) sq += (x - mean) * (x - mean);
        float spread = MathF.Sqrt(sq / half.Length) / MathF.Max(mean, 1e-9f);

        // Given the same docks
        // When the player looks along the street
        // Then the top of the mist rises and dips along it rather than lying flat.
        check.Expect(mean > 0f && spread > 0.2f, $"E28 half way up the mist the street's density spreads {spread:F3} of its mean {mean:F4} (want over 0.2)");
    }

    /// <summary>How many of the street's points thinned and thickened, each by over a fifth of the mean, from <paramref name="was"/> to <paramref name="now"/>.</summary>
    private static (int thinned, int thickened) Shifted(ReadOnlySpan<float> was, ReadOnlySpan<float> now)
    {
        float tell = MeanOf(was) * 0.2f;
        int thinned = 0, thickened = 0;
        for (int i = 0; i < was.Length; i++)
        {
            if (now[i] < was[i] - tell) thinned++;
            if (now[i] > was[i] + tell) thickened++;
        }
        return (thinned, thickened);
    }

    private static void Moves(Checker check, ReadOnlySpan<float> was, ReadOnlySpan<float> windy, ReadOnlySpan<float> still)
    {
        (int thinned, int thickened) = Shifted(was, windy);

        // Given the docks with the player standing still
        // When three seconds pass
        // Then the mist on the street has drifted: patches that were thick have thinned and thin ones have thickened.
        check.Expect(thinned >= StreetPoints / 4 && thickened >= StreetPoints / 4,
            $"E29 in the wind, {thinned} of {StreetPoints} points thinned and {thickened} thickened over 3 s (want a quarter each)");

        (thinned, thickened) = Shifted(was, still);

        // Given an area with no wind and mist on the ground
        // When three seconds pass with the player standing still
        // Then the mist on the ground still shifts.
        check.Expect(thinned >= StreetPoints / 4 && thickened >= StreetPoints / 4,
            $"E30 with no wind, {thinned} of {StreetPoints} points thinned and {thickened} thickened over 3 s (want a quarter each)");
    }
}
