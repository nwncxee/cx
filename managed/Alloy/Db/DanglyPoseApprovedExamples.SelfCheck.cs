using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Approved examples E1, E2, E5 and E7: dangly cloth whose animation moves its parent follows that
/// parent as the stock client draws it. Oracle: the stock solve run beside the part, and the rest mesh.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>How far, in model units, a drawn shape may stray from the stock solve's and still be its.</summary>
    private const float PoseSame = 1e-4f;

    static partial void DanglyPoseApprovedExamples(Checker check)
    {
        // Given a curtain door, such as ad_curtain_01
        // When a player opens it
        // Then its flaps part aside and stay parted while it stands open, as on the stock client
        PoseRun(check, "E1", false, false, s =>
        {
            for (int f = 0; f < 30; f++) PoseFrame(s, windy: false);
            PoseAt(s, PoseOpen(20f), Quaternion.Identity, Vector3.Zero);
            float worst = 0f;
            for (int f = 0; f < PoseFrames; f++)
            {
                PoseFrame(s, windy: false);
                worst = MathF.Max(worst, PoseGap(s.Drawn, s.Oracle));
            }
            check.Expect(worst <= PoseSame,
                         $"E1 an open curtain's flaps stay parted as on the stock client, strayed {worst:0.0000}");
        });

        // Given an open curtain door
        // When it closes
        // Then its flaps swing back together and hang closed
        PoseRun(check, "E2", false, false, s =>
        {
            for (int f = 0; f < 30; f++) PoseFrame(s, windy: false);
            PoseAt(s, PoseOpen(20f), Quaternion.Identity, Vector3.Zero);
            for (int f = 0; f < PoseFrames; f++) PoseFrame(s, windy: false);
            PoseAt(s, Quaternion.Identity, Quaternion.Identity, Vector3.Zero);
            for (int f = 0; f < PoseFrames; f++) PoseFrame(s, windy: false);
            float gap = PoseGap(s.Drawn, s.Mesh.Positions);
            check.Expect(gap < 0.005f, $"E2 a closed curtain's flaps hang back at rest, {gap:0.0000} off it");
        });

        // Given a flag on a pole
        // When it is destroyed and the pole falls
        // Then the cloth falls with the pole and stays on it, as on the stock client
        PoseRun(check, "E5", false, false, s =>
        {
            for (int f = 0; f < 30; f++) PoseFrame(s, windy: false);
            float worst = 0f;
            for (int f = 1; f <= 30; f++)
            {
                PoseAt(s, PoseOpen(3f * f), Quaternion.Identity, Vector3.Zero);
                PoseFrame(s, windy: false);
                worst = MathF.Max(worst, PoseGap(s.Drawn, s.Oracle));
            }
            for (int f = 0; f < PoseFrames; f++)
            {
                PoseFrame(s, windy: false);
                worst = MathF.Max(worst, PoseGap(s.Drawn, s.Oracle));
            }
            check.Expect(worst <= PoseSame,
                         $"E5 a falling flag's cloth stays on its pole as on the stock client, strayed {worst:0.0000}");
        });

        // Given a kelp placeable whose idle animation sways its stem, such as plc_kelp11
        // When it is in view
        // Then its fronds move as on the stock client
        PoseRun(check, "E7", true, false, s =>
        {
            float worst = 0f;
            for (int f = 0; f < PoseFrames; f++)
            {
                PoseAt(s, PoseOpen(12f * MathF.Sin(f * PoseDt * 2f)), Quaternion.Identity, Vector3.Zero);
                PoseFrame(s, windy: false);
                if (f > 0) worst = MathF.Max(worst, PoseGap(s.Drawn, s.Oracle));
            }
            check.Expect(worst <= PoseSame,
                         $"E7 a swaying kelp's fronds move as on the stock client, strayed {worst:0.0000}");
        });
    }
}
