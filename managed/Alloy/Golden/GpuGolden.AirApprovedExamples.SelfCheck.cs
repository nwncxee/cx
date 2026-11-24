using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The air's looks on the device: the shipped <c>cx_air</c> dust profile, its light and its sky colour, run on
/// chosen inputs. Oracle, by hand: a colour's hue read off its largest channel, a lamp's tint, a knee's height.</summary>
internal static partial class GpuGolden
{
    /// <summary>The shipped air library, or empty when it is missing.</summary>
    private static string AirSource => ShaderSource.Get("cx_air.glsl");

    private static void AirApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        AirDustProfile(vk, check, log);
        AirDustAmbient(vk, check, log);
        AirDustTorch(vk, check, log);
        AirMistMoon(vk, check, log);
        AirMistLamp(vk, check, log);
        AirMotesRed(vk, check, log);
        AirWispsSun(vk, check, log);
        AirWispsMoon(vk, check, log);
        AirHazeAmbient(vk, check, log);
        AirHazeFire(vk, check, log);
        AirMistBlackFog(vk, check, log);
    }

    /// <summary>Run <paramref name="fn"/>'s body <paramref name="call"/> once per case of <paramref name="cases"/>,
    /// <paramref name="ins"/> floats in and <paramref name="outs"/> out each; null when the library or stage is missing.</summary>
    private static float[]? AirRun(VkBackend vk, Checker check, ExtenderLog log, string sig, string call, int ins, int outs, float[] cases, string label)
    {
        string fn = Function(AirSource, sig);
        check.Expect(fn.Length > 0, $"{label}: cx_air declares {sig}");
        if (fn.Length == 0) return null;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(ins, outs) + fn + """
            void main()
            {
                uint i = gl_WorkGroupID.x * uint(CX_GOLD_IN), o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
            """ + call + "\n}\n", label);
        check.Expect(!pipe.IsNull, $"{label}: the stage compiles (a refusal is logged above)");
        if (pipe.IsNull) return null;
        float[]? got = RunCases(vk, log, pipe, cases.Length / ins, cases, cases.Length / ins * outs, label);
        vk.Destroy(pipe);
        check.Expect(got != null, $"{label}: the stage was refused (logged above)");
        return got;
    }

    /// <summary>Per case of <see cref="AirLit"/>: ambient rgb, lamp rgb, distance to the lamp, the lamp's reach.</summary>
    private static float[]? AirLit(VkBackend vk, Checker check, ExtenderLog log, float[] cases, string label)
        => AirRun(vk, check, log, "vec3 CxAirLit(", """
                vec3 c = CxAirLit(vec3(uIn[i], uIn[i + 1u], uIn[i + 2u]), vec3(uIn[i + 3u], uIn[i + 4u], uIn[i + 5u]), uIn[i + 6u], uIn[i + 7u]);
                uOut[o] = c.r; uOut[o + 1u] = c.g; uOut[o + 2u] = c.b;
            """, 8, 3, cases, label);

    /// <summary>Per case of <see cref="AirSky"/>: the area's fog colour rgb, its sun or moon diffuse rgb.</summary>
    private static float[]? AirSky(VkBackend vk, Checker check, ExtenderLog log, float[] cases, string label)
        => AirRun(vk, check, log, "vec3 CxAirSky(", """
                vec3 c = CxAirSky(vec3(uIn[i], uIn[i + 1u], uIn[i + 2u]), vec3(uIn[i + 3u], uIn[i + 4u], uIn[i + 5u]));
                uOut[o] = c.r; uOut[o + 1u] = c.g; uOut[o + 2u] = c.b;
            """, 6, 3, cases, label);

    /// <summary>The colour at output case <paramref name="k"/>.</summary>
    private static (float R, float G, float B) Rgb(float[] got, int k)
    {
        ReadOnlySpan<float> c = got.AsSpan(3 * k, 3);
        return (c[0], c[1], c[2]);
    }

    /// <summary>Is <paramref name="c"/> the hue of <paramref name="want"/>: its channels in the same ratio, within 5%?</summary>
    private static bool SameHue((float R, float G, float B) c, (float R, float G, float B) want)
    {
        float sc = c.R + c.G + c.B, sw = want.R + want.G + want.B;
        if (sc <= 0f || sw <= 0f) return false;
        return MathF.Abs(c.R / sc - want.R / sw) < 0.05f && MathF.Abs(c.G / sc - want.G / sw) < 0.05f && MathF.Abs(c.B / sc - want.B / sw) < 0.05f;
    }

    // Given a crypt (an indoor area on a crypt tileset)
    // When the player stands in a room
    // Then dust lies on the floor, thickest on the floor itself and gone by a standing character's knee.
    private static void AirDustProfile(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirRun(vk, check, log, "float CxAirDust(", "    uOut[o] = CxAirDust(uIn[i], uIn[i + 1u]);", 2, 1,
            new[] { 0.02f, 0.3f, 0.2f, 0.3f, 0.5f, 0.3f }, "golden air dust");
        if (got == null) return;
        check.Expect(got[0] > got[1] && got[1] > 0f && got[2] == 0f, $"E45 the dust's density reads {got[0]:F4} on the floor, {got[1]:F4} at the shin, {got[2]:F4} at the knee (want thickest low, gone by the knee)");
    }

    // Given a crypt room lit only by its ambient light
    // When the player looks at the dust
    // Then it takes the ambient light's colour.
    private static void AirDustAmbient(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirLit(vk, check, log, new[] { 0.10f, 0.20f, 0.40f, 0f, 0f, 0f, 9f, 5f }, "golden air dust ambient");
        if (got == null) return;
        check.Expect(SameHue(Rgb(got, 0), (0.10f, 0.20f, 0.40f)), $"E48 the dust shows {Rgb(got, 0)} under a (0.10, 0.20, 0.40) ambient (want its hue)");
    }

    /// <summary>A torch 0.5 m and 6 m off against <paramref name="ambient"/>, reach 5: near it, then out of its reach.</summary>
    private static float[] TorchCases((float R, float G, float B) ambient, (float R, float G, float B) torch)
        => new[] { ambient.R, ambient.G, ambient.B, torch.R, torch.G, torch.B, 0.5f, 5f, ambient.R, ambient.G, ambient.B, torch.R, torch.G, torch.B, 6f, 5f };

    /// <summary>Is case 0 brighter than case 1 and pulled from the ambient's hue toward the torch's red?</summary>
    private static bool Tinted(float[] got)
    {
        var (nr, ng, nb) = Rgb(got, 0);
        var (fr, fg, fb) = Rgb(got, 1);
        return nr + ng + nb > fr + fg + fb && nr / (nb + 1e-6f) > fr / (fb + 1e-6f);
    }

    // Given the same room with a torch burning 3 m away
    // When the player looks at the dust
    // Then the dust near the torch is brighter than the rest and takes the torch's tint.
    private static void AirDustTorch(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirLit(vk, check, log, TorchCases((0.10f, 0.12f, 0.20f), (1f, 0.55f, 0.15f)), "golden air dust torch");
        if (got == null) return;
        check.Expect(Tinted(got), $"E49 the dust near the torch shows {Rgb(got, 0)}, the rest {Rgb(got, 1)} (want brighter, toward the torch's orange)");
    }

    // Given the night mist over the water
    // When the player looks at it away from lamps
    // Then it is lit in the moonlight's colour.
    private static void AirMistMoon(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirSky(vk, check, log, new[] { 0.30f, 0.30f, 0.30f, 0.20f, 0.22f, 0.35f }, "golden air mist moon");
        if (got == null) return;
        var (r, _, b) = Rgb(got, 0);
        check.Expect(b > r, $"E70 the mist under a blue moon and a grey fog shows {Rgb(got, 0)} (want the moon's blue)");
    }

    // Given the night mist beside a lamp on the quay
    // When the player looks at it
    // Then it is brighter there and takes the lamp's tint.
    private static void AirMistLamp(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirLit(vk, check, log, TorchCases((0.20f, 0.22f, 0.35f), (1f, 0.7f, 0.3f)), "golden air mist lamp");
        if (got == null) return;
        check.Expect(Tinted(got), $"E71 the mist by the lamp shows {Rgb(got, 0)}, away from it {Rgb(got, 1)} (want brighter, toward the lamp's tint)");
    }

    // Given a crypt room with one red-tinted lamp
    // When the player looks across the room
    // Then the motes in its light glow red, and brighter close to the lamp than at the edge of its light.
    private static void AirMotesRed(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirLit(vk, check, log, new[] { 0f, 0f, 0f, 1f, 0.1f, 0.1f, 0.5f, 5f, 0f, 0f, 0f, 1f, 0.1f, 0.1f, 4.5f, 5f }, "golden air motes red");
        if (got == null) return;
        var (r, g, b) = Rgb(got, 0);
        var (er, eg, eb) = Rgb(got, 1);
        check.Expect(r > 2f * g && r > 2f * b, $"E123 a mote by the red lamp glows {Rgb(got, 0)} (want red)");
        check.Expect(r + g + b > er + eg + eb && er > 0f, $"E123 it glows {Rgb(got, 0)} close, {Rgb(got, 1)} at the edge (want brighter close, still lit at the edge)");
    }

    // Given wisps over a meadow by day
    // When the player looks up at them
    // Then they take the colour of the area's sunlight, not a fixed white.
    private static void AirWispsSun(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirSky(vk, check, log, new[] { 0.6f, 0.6f, 0.6f, 1f, 0.85f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.8f, 1f }, "golden air wisps sun");
        if (got == null) return;
        var (wr, _, wb) = Rgb(got, 0);
        var (cr, _, cb) = Rgb(got, 1);
        check.Expect(wr > wb && cb > cr, $"E130 the wisps show {Rgb(got, 0)} under a warm sun and {Rgb(got, 1)} under a cool one (want each sun's colour)");
    }

    // Given the same meadow at night
    // When the player looks up
    // Then the wisps show pale in the moonlight's colour rather than black.
    private static void AirWispsMoon(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirSky(vk, check, log, new[] { 0f, 0f, 0f, 0.15f, 0.17f, 0.30f }, "golden air wisps moon");
        if (got == null) return;
        var (r, g, b) = Rgb(got, 0);
        check.Expect(r + g + b > 0.1f && b > r, $"E131 the wisps under the moon show {Rgb(got, 0)} (want pale, the moon's blue, not black)");
    }

    // Given a forest at night lit only by its ambient light
    // When the player looks at the haze between the trunks
    // Then it takes the ambient light's colour.
    private static void AirHazeAmbient(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirLit(vk, check, log, new[] { 0.05f, 0.12f, 0.08f, 0f, 0f, 0f, 9f, 5f }, "golden air haze ambient");
        if (got == null) return;
        check.Expect(SameHue(Rgb(got, 0), (0.05f, 0.12f, 0.08f)), $"E135 the haze shows {Rgb(got, 0)} under a (0.05, 0.12, 0.08) ambient (want its hue)");
    }

    // Given the same forest with a campfire 3 m from the trees
    // When the player looks at the haze near it
    // Then that haze is brighter than the rest and takes the fire's tint.
    private static void AirHazeFire(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirLit(vk, check, log, TorchCases((0.05f, 0.12f, 0.08f), (1f, 0.5f, 0.1f)), "golden air haze fire");
        if (got == null) return;
        check.Expect(Tinted(got), $"E136 the haze by the fire shows {Rgb(got, 0)}, the rest {Rgb(got, 1)} (want brighter, toward the fire's tint)");
    }

    // Given a city by the water at night whose fog colour is black
    // When the player looks across the water
    // Then the mist shows lighter than black, pale in the moonlight's colour.
    private static void AirMistBlackFog(VkBackend vk, Checker check, ExtenderLog log)
    {
        float[]? got = AirSky(vk, check, log, new[] { 0f, 0f, 0f, 0.20f, 0.22f, 0.30f }, "golden air mist black fog");
        if (got == null) return;
        var (r, g, b) = Rgb(got, 0);
        check.Expect(r + g + b > 0.1f && b > r, $"E146 the mist under black fog and a blue moon shows {Rgb(got, 0)} (want lighter than black, the moon's blue)");
    }
}
