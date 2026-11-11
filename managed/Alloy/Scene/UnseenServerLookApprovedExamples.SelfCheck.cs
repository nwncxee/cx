using System;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using static CX.Alloy.UnseenFixture;

namespace CX.Alloy;

/// <summary>A server's own silhouette look (approved-spec, E26, E27, E29). Oracle: the look files written here by hand
/// as a server's content author would write them, and the alphas their declaration lines ask for.</summary>
internal static class UnseenServerLookApprovedExamples
{
    private const uint Oid = 2468;

    /// <summary>A look that paints silhouettes deep blue and keeps none of the creature's lighting.</summary>
    internal const string Blue = "CxSilhouetteLook CxSilhouetteShade(CxSilhouetteSurface s) { return CxSilhouetteLook(vec3(0.0, 0.02, 0.3), 0.0); }\n";

    /// <summary>A look whose one function names a value that does not exist, under a line asking for half opacity.</summary>
    internal const string Broken = "#pragma cx_silhouette opacity 50\nCxSilhouetteLook CxSilhouetteShade(CxSilhouetteSurface s) { return nothing; }\n";

    internal static bool SelfCheck(ExtenderLog log)
        => Run(log, "UnseenServerLookApprovedExamples.SelfCheck", 2, (check, o) =>
        {
            foreach (string leaf in VulkanToolchain.Libraries)
                if (!VulkanToolchain.Load(leaf)) { check.Skip("the staged shaderc did not load"); return; }
            DbId body = o[0], sword = o[1];
            SceneDb.SetAttachTo(sword.Index, body);
            GameAlpha(body, 0.5f);

            // Given a server look that says silhouettes are half see-through
            // When a heard-but-unseen creature stands in front of a lit wall
            // Then the wall shows through it at half strength
            bool worn = Unseen.Wear("#pragma cx_silhouette opacity 50\n" + Blue, log);
            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard, body)], Whole);
            check.Expect(worn && Drawn(body) == 0.5f && Marked(body.Index),
                         $"E26 a look that asks for half opacity draws its silhouette half see-through: worn {worn}, drew at {Drawn(body)}");

            // Given a server look that says glowing parts stay
            // When I look at a heard-but-unseen creature holding a flaming sword
            // Then the flame draws as it does on a creature in sight
            worn = Unseen.Wear("#pragma cx_silhouette glow\n" + Blue, log);
            float flame = Unseen.Alpha(sword.Index, 0.5f, lit: false);
            check.Expect(worn && flame == 1f, $"E27 a look that keeps glowing parts draws the flame whole: worn {worn}, drew at {flame}");

            // Given a server cx_silhouette.shd that does not compile
            // When a player with CX hears a creature their character does not see
            // Then that creature draws the stock dark grey, and cx.log names the file and the error
            int said = 0;
            Action<string>? tap = log.Tap;
            log.Tap = line => { if (line.Contains("cx_silhouette.shd") && line.Contains("nothing")) said++; };
            try { worn = Unseen.Wear(Broken, log); }
            finally { log.Tap = tap; }
            check.Expect(!worn && said == 1 && Drawn(body) == 1f && Unseen.Worn().Contains("CX_SILHOUETTE_RGB") && !Unseen.Worn().Contains("return nothing"),
                         $"E29 a look that does not compile is refused whole, and the log names the file and the error: worn {worn}, said {said} time(s), drew at {Drawn(body)}");
        });
}

/// <summary>A server's own silhouette look on the device (approved-spec, E21, E23, E24, E25, E28, E29). Oracle: the look
/// files written here by hand, and the colours each one's own arithmetic gives for the surfaces handed to it.</summary>
internal static partial class GpuGolden
{
    /// <summary>Floats one look case reads: lit, own colour, facing, view direction, then amount and time.</summary>
    private const int LookIn = 14;

    /// <summary>Run <paramref name="file"/> as the worn look over <paramref name="cases"/>; four floats a case come back,
    /// the colour and the share of the creature's lighting kept. Null when the file or the stage was refused.</summary>
    private static float[]? LookCases(VkBackend vk, ExtenderLog log, string? file, float[] cases, string label)
    {
        try
        {
            Unseen.Wear(file, log);
            PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + Unseen.Worn() + CaseBlocks(LookIn, 4) + """
                void main()
                {
                    int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
                    CxSilhouetteSurface s = CxSilhouetteSurface(vec3(uIn[i], uIn[i + 1], uIn[i + 2]), vec3(uIn[i + 3], uIn[i + 4], uIn[i + 5]),
                        vec3(uIn[i + 6], uIn[i + 7], uIn[i + 8]), vec3(uIn[i + 9], uIn[i + 10], uIn[i + 11]), vec3(0.0), uIn[i + 12], uIn[i + 13]);
                    float own;
                    vec3 r = CxSilhouetteApply(s, own);
                    uOut[o] = r.x; uOut[o + 1] = r.y; uOut[o + 2] = r.z; uOut[o + 3] = own;
                }
                """, label);
            int groups = cases.Length / LookIn;
            float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, groups, cases, groups * 4, label);
            vk.Destroy(pipe);
            return got;
        }
        finally { Unseen.Undress(); }
    }

    private static void UnseenServerLookApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        // red armour under a torch, facing the camera, a whole silhouette at time 0; then the same half way in
        float[] armour =
        [
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f,
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 0f, 1f, 0f, 0f, 1f, 0.5f, 0f,
        ];

        // Given a server hak that carries its own cx_silhouette.shd, painting silhouettes deep blue
        // When a player with CX hears a creature their character does not see
        // Then that creature draws deep blue, not the stock dark grey
        float[]? blue = LookCases(vk, log, UnseenServerLookApprovedExamples.Blue, armour, "golden silhouette look blue");
        check.Expect(blue != null && blue[0] == 0f && blue[1] == 0.02f && blue[2] == 0.3f,
                     FormattableString.Invariant($"E21: a server's blue look draws blue ({blue?[0]:F4}, {blue?[1]:F4}, {blue?[2]:F4})"));

        // Given a server look
        // When a creature in sight walks out of my character's sight
        // Then over half a second it eases from its own look into the server's look, with no pop
        check.Expect(blue != null && Near(blue[4], 0.45f) && Near(blue[5], 0.035f) && Near(blue[6], 0.17f),
                     FormattableString.Invariant($"E28: half way in, red armour is half way to the server's blue ({blue?[4]:F4}, {blue?[5]:F4}, {blue?[6]:F4})"));

        // Given a server look that keeps a creature's own colours at a third of their brightness
        // When a heard-but-unseen creature in red armour stands under a torch
        // Then its armour still reads red, dimmer, and the torch still lights it
        float[]? third = LookCases(vk, log, "CxSilhouetteLook CxSilhouetteShade(CxSilhouetteSurface s) { return CxSilhouetteLook(s.lit / 3.0, 1.0 / 3.0); }\n",
                                   armour, "golden silhouette look third");
        check.Expect(third != null && Near(third[0], 0.3f) && Near(third[1], 0.05f / 3f) && Near(third[2], 0.04f / 3f) && Near(third[3], 1f / 3f),
                     FormattableString.Invariant($"E23: a look that keeps a third of the creature is red, dimmer, and keeps a third of its light ({third?[0]:F4}, {third?[1]:F4}, {third?[2]:F4}, light {third?[3]:F4})"));

        // Given a server look that is dark with a bright edge
        // When I look at a heard-but-unseen creature
        // Then it draws dark with a bright rim along its outline, from whichever side the camera looks
        float[] facings =
        [
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f,
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 1f, 0f, 0f, 0f, 0f, 1f, 1f, 0f,
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 1f, 0f, 0f, 1f, 0f, 1f, 0f,
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 0f, 1f, 0f, 1f, 0f, 1f, 0f,
        ];
        float[]? rim = LookCases(vk, log, """
            CxSilhouetteLook CxSilhouetteShade(CxSilhouetteSurface s)
            {
                float edge = 1.0 - max(dot(s.normal, s.view), 0.0);
                return CxSilhouetteLook(vec3(edge * edge), 0.0);
            }
            """, facings, "golden silhouette look rim");
        check.Expect(rim != null && rim[0] == 0f && rim[4] == 1f && rim[8] == 0f && rim[12] == 1f,
                     FormattableString.Invariant($"E24: a rim look is dark where the surface faces the camera and bright at its edge, from two sides ({rim?[0]:F2}, {rim?[4]:F2}, {rim?[8]:F2}, {rim?[12]:F2})"));

        // Given a server look that pulses once a second
        // When I look at a heard-but-unseen creature
        // Then it brightens and darkens once a second
        float[] clock =
        [
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0.25f,
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0.75f,
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 1.25f,
        ];
        float[]? pulse = LookCases(vk, log, "CxSilhouetteLook CxSilhouetteShade(CxSilhouetteSurface s) { return CxSilhouetteLook(vec3(0.5 + 0.5 * sin(s.time * 6.2831853)), 0.0); }\n",
                                   clock, "golden silhouette look pulse");
        check.Expect(pulse != null && Near(pulse[0], 1f) && Near(pulse[4], 0f) && Near(pulse[8], 1f),
                     FormattableString.Invariant($"E25: a pulsing look is bright, dark, and bright again a second later ({pulse?[0]:F4}, {pulse?[4]:F4}, {pulse?[8]:F4})"));

        // Given a server cx_silhouette.shd that does not compile
        // When a player with CX hears a creature their character does not see
        // Then that creature draws the stock dark grey, and cx.log names the file and the error
        float[]? broken = LookCases(vk, log, UnseenServerLookApprovedExamples.Broken, armour, "golden silhouette look broken");
        check.Expect(broken != null && broken[0] == Grey && broken[1] == Grey && broken[2] == Grey,
                     FormattableString.Invariant($"E29: a look that does not compile draws the stock grey ({broken?[0]:F4}, {broken?[1]:F4}, {broken?[2]:F4})"));
    }
}
