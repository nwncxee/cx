using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;
using static CX.Alloy.UnseenFixture;

namespace CX.Alloy;

/// <summary>A creature fades into its silhouette and back out of it (approved-spec, E12, E13, E15, E16). Oracle: the
/// half second written here by hand as frame times, and the share of it each frame has used up.</summary>
internal static class UnseenFadeApprovedExamples
{
    /// <summary>The game object id the checks' creature goes by.</summary>
    private const uint Oid = 2468;

    internal static bool SelfCheck(ExtenderLog log)
        => Run(log, "UnseenFadeApprovedExamples.SelfCheck", 2, (check, o) =>
        {
            DbId body = o[0], sword = o[1];
            SceneDb.SetAttachTo(sword.Index, body);
            void GameAlpha(float a) => UnseenFixture.GameAlpha(body, a);
            float Glow() => Unseen.Alpha(sword.Index, SceneDb.ObjTint[sword.Index].W, lit: false);
            Unseen.Sight seen = new(Oid, Unseen.Heard | Unseen.Seen, body), heard = new(Oid, Unseen.Heard, body);
            GameAlpha(1f);
            Unseen.Frame([seen], Whole);

            // Given a creature in my character's sight
            // When it steps out of sight and is still heard
            // Then over half a second its colours darken evenly into the silhouette
            GameAlpha(0.5f);
            Unseen.Frame([heard], 0.125f);
            float quarter = Unseen.Amount(body.Index);
            Unseen.Frame([heard], 0.125f);
            float half = Unseen.Amount(sword.Index);
            // Given a creature with a flaming sword and glowing eyes
            // When it fades into the silhouette
            // Then the glow dims away over the same half second, and returns the same way when the creature is seen again
            float glowHalf = Glow();
            Unseen.Frame([heard], 0.25f);
            float whole = Unseen.Amount(body.Index), glowGone = Glow();
            check.Expect(quarter == 0.25f && half == 0.5f && whole == 1f,
                         $"E12 a creature darkens evenly over half a second: {quarter} at an eighth of a second, {half} at a quarter, {whole} at a half");

            // Given a creature drawn as a silhouette
            // When it walks into my character's sight
            // Then over half a second it brightens evenly back into its own colours and lighting
            GameAlpha(1f);
            Unseen.Frame([seen], 0.25f);
            float back = Unseen.Amount(body.Index), glowBack = Glow();
            Unseen.Frame([seen], 0.25f);
            check.Expect(back == 0.5f && !Marked(body.Index) && (Unseen.Slots(sword.Index, 0) & Unseen.SlotMask(1f)) == 0,
                         $"E13 a silhouette brightens evenly over half a second: {back} at a quarter of a second, then a creature again");
            check.Expect(glowHalf == 0.5f && glowGone == 0f && glowBack == 0.5f && Glow() == 1f,
                         $"E16 a glowing part dims with the fade and returns with it: {glowHalf} half way in, {glowGone} at the silhouette, {glowBack} half way back, {Glow()} seen");

            // Given a creature half way into the silhouette
            // When it comes back into my character's sight
            // Then it brightens from that half-way look, with no jump
            GameAlpha(0.5f);
            Unseen.Frame([heard], 0.25f);
            GameAlpha(1f);
            Unseen.Frame([seen], 0.125f);
            check.Expect(Unseen.Amount(body.Index) == 0.25f,
                         $"E15 a fade turned around half way carries on from there: {Unseen.Amount(body.Index)} an eighth of a second later");
        });
}

/// <summary>The fade's colour on the device (approved-spec, E12). Oracle: the two ends written here by hand, the
/// armour's own red and the silhouette's grey, whose mid-point is what half way must show.</summary>
internal static partial class GpuGolden
{
    private static void UnseenFadeApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + Unseen.LookText(null)
            + CaseBlocks(4, 3) + SlotCase, "golden silhouette fade");
        float[] cases =
        [
            0.9f, 0.05f, 0.04f, Unseen.SlotMask(0.5f),
            0.9f, 0.05f, 0.04f, Unseen.SlotMask(1f),
        ];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 2, cases, 6, "golden silhouette fade");
        vk.Destroy(pipe);
        check.Expect(got != null, "silhouette fade: the stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant(
            $"GpuGolden: silhouette fade, half way ({got[0]:F4}, {got[1]:F4}, {got[2]:F4}) whole ({got[3]:F4}, {got[4]:F4}, {got[5]:F4})"));

        // Given a creature in my character's sight
        // When it steps out of sight and is still heard
        // Then over half a second its colours darken evenly into the silhouette
        bool Mid(float at, float own) => MathF.Abs(at - (own + Grey) * 0.5f) <= MathF.Abs(own - Grey) / AlloyRenderer.SilhouetteLevels;
        check.Expect(got[3] == Grey && got[4] == Grey && got[5] == Grey && Mid(got[0], 0.9f) && Mid(got[1], 0.05f) && Mid(got[2], 0.04f),
                     FormattableString.Invariant($"E12: half way in, red armour is half way to the silhouette's grey ({got[0]:F4}, {got[1]:F4}, {got[2]:F4})"));
    }
}
