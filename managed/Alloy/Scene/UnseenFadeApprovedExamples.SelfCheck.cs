using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>A creature fades into its silhouette and back out of it (approved-spec, E12, E13, E15, E16). Oracle: the
/// half second written here by hand as frame times, and the share of it each frame has used up.</summary>
internal static class UnseenFadeApprovedExamples
{
    /// <summary>The game object id the checks' creature goes by.</summary>
    private const uint Oid = 2468;

    /// <summary>Seconds between frames that must leave no fade running.</summary>
    private const float Whole = 10f;

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "UnseenFadeApprovedExamples.SelfCheck");
        Func<string, bool> was = ObjectFns.ResourcePresent;
        long unreadable = ObjectFns.UnreadableModels;
        var o = new DbId[2];
        try
        {
            ObjectFns.ResourcePresent = n => n == "cx_present_unreadable";
            for (int i = 0; i < o.Length; i++)
                o[i] = Facade.Object(ObjectFns.CreateForCheck("cx_present_unreadable"), SeamSlot.Unnamed);
            DbId body = o[0], sword = o[1];
            check.Expect(!body.IsNull && !sword.IsNull, "the check's two objects could not be made");
            if (!check.Ok) return false;
            SceneDb.SetAttachTo(sword.Index, body);
            nint shell = Facade.ObjectShell(body);
            void GameAlpha(float a) => ObjectSlots.Shift(body, shell, shell, Vector3.One, a, false, true, 0);
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
            check.Expect(back == 0.5f && !Unseen.Of(body.Index) && (Unseen.Slots(sword.Index, 0) & Unseen.SlotMask(1f)) == 0,
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
        }
        finally
        {
            Unseen.Reset();
            for (int i = o.Length - 1; i >= 0; i--) if (!o[i].IsNull) ObjectSlots.DestroyRecord(o[i]);
            ObjectFns.ResourcePresent = was;
            ObjectFns.UnreadableModels = unreadable;
        }
        if (check.Ok) log.Info("UnseenFadeApprovedExamples.SelfCheck: passed");
        return check.Ok;
    }
}

/// <summary>The fade's colour on the device (approved-spec, E12). Oracle: the two ends the same stage paints, the
/// armour's own red and the whole silhouette's grey, whose mid-point is what half way must show.</summary>
internal static partial class GpuGolden
{
    private static void UnseenFadeApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + ShaderSource.Get("cx_silhouette.glsl")
            + CaseBlocks(4, 3) + """
            void main()
            {
                int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
                vec3 r = CxSilhouette(vec3(uIn[i], uIn[i + 1], uIn[i + 2]), int(uIn[i + 3]));
                uOut[o] = r.x; uOut[o + 1] = r.y; uOut[o + 2] = r.z;
            }
            """, "golden silhouette fade");
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
        bool Mid(float at, float own, float grey) => MathF.Abs(at - (own + grey) * 0.5f) <= MathF.Abs(own - grey) / Unseen.SlotLevels;
        check.Expect(got[3] < 0.1f && Mid(got[0], 0.9f, got[3]) && Mid(got[1], 0.05f, got[4]) && Mid(got[2], 0.04f, got[5]),
                     FormattableString.Invariant($"E12: half way in, red armour is half way to the silhouette's grey ({got[0]:F4}, {got[1]:F4}, {got[2]:F4})"));
    }
}
