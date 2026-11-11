using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;
using static CX.Alloy.UnseenFixture;

namespace CX.Alloy;

/// <summary>A creature the player character hears but does not see draws as a silhouette (approved-spec, E1, E3, E6,
/// E7, E8). Oracle: the perception marks and the alpha 0.5 <c>CNWCObject::GetFinalFadeAlpha</c> answers for them,
/// written here by hand, against the solid 1 and the fade's own halves.</summary>
internal static class UnseenSilhouetteApprovedExamples
{
    /// <summary>The game object id the checks' creature goes by.</summary>
    private const uint Oid = 1234;

    internal static bool SelfCheck(ExtenderLog log)
        => Run(log, "UnseenSilhouetteApprovedExamples.SelfCheck", 4, (check, o) =>
        {
            DbId body = o[0], cloak = o[1], sword = o[2], bystander = o[3];
            SceneDb.SetAttachTo(cloak.Index, body);
            ObjectSlots.Flag(cloak, SeamFlag.Composed, true);
            SceneDb.SetAttachTo(sword.Index, body);
            void GameAlpha(float a) => UnseenFixture.GameAlpha(body, a);

            // Given a creature my character hears but does not see, standing in front of a lit wall
            // When I look at it
            // Then it draws as a solid shape of itself and the wall does not show through it
            GameAlpha(0.5f);
            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard, body)], Whole);
            check.Expect(Drawn(body) == 1f, $"E1 a heard-but-unseen creature draws solid, drew at {Drawn(body)}");

            // Given a heard-but-unseen creature with a sword, a shield, a cloak and a helmet
            // When I look at it
            // Then all of them are the same dark grey as its body
            bool worn = Drawn(cloak) == 1f && Drawn(sword) == 1f
                     && (Unseen.Slots(cloak.Index, 0) & Unseen.SlotMask(1f)) != 0 && (Unseen.Slots(sword.Index, 0) & Unseen.SlotMask(1f)) != 0;
            check.Expect(worn && (Unseen.Slots(body.Index, 0) & Unseen.SlotMask(1f)) != 0 && !Marked(bystander.Index),
                         "E6 what a silhouette wears and holds is silhouette too, and the creature beside it is not");

            // Given a heard-but-unseen creature holding a flaming sword with glowing eyes
            // When I look at it
            // Then the blade and the head are the same dark grey as the rest, and the flame and the eye glow are not drawn
            check.Expect(Unseen.Alpha(sword.Index, 0.5f, lit: false) == 0f,
                         "E8 a silhouette's glowing part, which adds its own light, is not drawn");

            // Given a heard-but-unseen creature drawn as a silhouette
            // When my character stops hearing it
            // Then the silhouette fades away smoothly, as a see-through creature does today, with no pop
            Unseen.Frame([], Whole);
            bool held = Marked(body.Index) && Drawn(body) == 1f;
            GameAlpha(0.25f);
            Unseen.Frame([], Whole);
            bool half = Marked(body.Index) && Drawn(body) == 0.5f;
            GameAlpha(0f);
            Unseen.Frame([], Whole);
            check.Expect(held && half && Drawn(body) == 0f,
                         $"E7 a silhouette no longer heard stays one while the game fades it: held {held}, half way {half}, gone {Drawn(body)}");

            // Given a creature drawn as a silhouette
            // When it walks into my character's sight
            // Then it draws with its own colours and lighting
            GameAlpha(0.5f);
            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard, body)], Whole);
            bool before = Marked(sword.Index);
            GameAlpha(1f);
            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard | Unseen.Seen, body)], Whole);
            check.Expect(before && !Marked(body.Index) && !Marked(sword.Index) && Drawn(body) == 1f
                         && (Unseen.Slots(sword.Index, 0) & Unseen.SlotMask(1f)) == 0,
                         $"E3 a silhouette that comes into sight is a creature again: silhouette before {before}");
        });
}

/// <summary>The silhouette's colour on the device (approved-spec, E2, E8). Oracle: the lit colours written here by
/// hand -- red armour, a blade far brighter than white -- which a silhouette must not let through.</summary>
internal static partial class GpuGolden
{
    /// <summary>The silhouette's dark grey, linear, written by hand: the goldens' oracle for the shipped colour.</summary>
    private const float Grey = 0.012f;

    private static void UnseenSilhouetteApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + Unseen.LookText(null)
            + CaseBlocks(4, 3) + """
            void main()
            {
                int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
                vec3 lit = vec3(uIn[i], uIn[i + 1], uIn[i + 2]);
                float own;
                vec3 r = CxSilhouetteApply(CxSilhouetteSurface(lit, lit, vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), vec3(0.0),
                                                               CxSilhouetteAmount(int(uIn[i + 3])), 0.0), own);
                uOut[o] = r.x; uOut[o + 1] = r.y; uOut[o + 2] = r.z;
            }
            """, "golden silhouette");
        // red armour under a torch as a silhouette, the same armour seen, and a silhouette's blade far brighter than white
        float[] cases =
        [
            0.9f, 0.05f, 0.04f, Unseen.SlotMask(1f),
            0.9f, 0.05f, 0.04f, 0f,
            6f, 3f, 0.5f, Unseen.SlotMask(1f),
        ];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 3, cases, 9, "golden silhouette");
        vk.Destroy(pipe);
        check.Expect(got != null, "silhouette: the stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant(
            $"GpuGolden: silhouette ({got[0]:F4}, {got[1]:F4}, {got[2]:F4}) seen ({got[3]:F4}, {got[4]:F4}, {got[5]:F4}) blade ({got[6]:F4}, {got[7]:F4}, {got[8]:F4})"));

        // Given a heard-but-unseen creature in bright red armour under a torch
        // When I look at it
        // Then every part of it is the same dark grey, with no red and no torchlight on it
        check.Expect(got[0] == got[1] && got[1] == got[2] && got[0] == Grey,
                     FormattableString.Invariant($"E2: red armour as a silhouette is one dark grey ({got[0]:F4}, {got[1]:F4}, {got[2]:F4})"));
        check.Expect(got[3] == 0.9f && got[4] == 0.05f && got[5] == 0.04f,
                     FormattableString.Invariant($"E2: ...and the same armour seen keeps its red ({got[3]:F4}, {got[4]:F4}, {got[5]:F4})"));

        // Given a heard-but-unseen creature holding a flaming sword with glowing eyes
        // When I look at it
        // Then the blade and the head are the same dark grey as the rest, and the flame and the eye glow are not drawn
        check.Expect(got[6] == got[0] && got[7] == got[0] && got[8] == got[0],
                     FormattableString.Invariant($"E8: a silhouette's glowing blade is the body's grey ({got[6]:F4}, {got[7]:F4}, {got[8]:F4})"));
    }
}
