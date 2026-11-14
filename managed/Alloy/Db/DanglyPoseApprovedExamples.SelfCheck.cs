using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Approved examples E1, E2, E5, E7 and E8: dangly cloth whose animation moves its parent follows it
/// as the stock client draws it. Oracle: the stock solve run beside the part, the rest mesh, the last frame.
/// They prove the routing to the stock solve, which <c>VanillaDanglyCheck</c> pins to the engine.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>How far, in model units, a drawn shape may stray from the stock solve's and still be its.</summary>
    private const float PoseSame = 1e-4f;

    /// <summary>A flap hangs closed within five centimetres of its rest shape: the stock solve's own wobble,
    /// against the metre a flap parted by twenty degrees stands off it.</summary>
    private const float PoseClosed = 0.05f;

    /// <summary>The share of the sway a flap's shape on its door may change by in the frame it changes hands. A
    /// flap reseeded at rest would change by all of it.</summary>
    private const float PoseCarry = 0.1f;

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
            for (int f = 0; f < PoseFrames; f++) worst = MathF.Max(worst, PoseFrame(s, windy: false));
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
            check.Expect(gap < PoseClosed, $"E2 a closed curtain's flaps hang back at rest, {gap:0.0000} off it");
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
                worst = MathF.Max(worst, PoseFrame(s, windy: false));
            }
            for (int f = 0; f < PoseFrames; f++) worst = MathF.Max(worst, PoseFrame(s, windy: false));
            check.Expect(worst <= PoseSame,
                         $"E5 a falling flag's cloth stays on its pole as on the stock client, strayed {worst:0.0000}");
        });

        // Given a closed curtain door whose flaps sway in a breeze
        // When it starts to open
        // Then the flaps carry on from where they hung, with no jump
        PoseRun(check, "E8", false, false, s =>
        {
            var before = new Vector3[s.Drawn.Length];
            for (int f = 0; f < PoseFrames; f++) PoseFrame(s, windy: true);
            float swayed = PoseGap(s.Drawn, s.Mesh.Positions);
            check.Expect(swayed > 0.005f, $"E8 the breeze sways the closed curtain {swayed:0.0000} off its rest");
            s.Drawn.CopyTo(before, 0);
            PoseAt(s, PoseOpen(2f), Quaternion.Identity, Vector3.Zero);
            PoseFrame(s, windy: true);
            float jump = PoseGap(s.Drawn, before);
            check.Expect(jump <= PoseCarry * swayed,
                         $"E8 the flaps carry on from where they hung as the door starts to open: their shape on "
                       + $"the door changed {jump:0.0000}, against {swayed:0.0000} of sway");
        });

        // Given a kelp placeable whose idle animation sways its stem, such as plc_kelp11
        // When it is in view
        // Then its fronds move as on the stock client
        PoseRun(check, "E7", true, false, s =>
        {
            float worst = 0f;
            for (int f = 0; f < PoseFrames; f++)
            {
                PoseAt(s, PoseOpen(12f * MathF.Cos(f * PoseDt * 2f)), Quaternion.Identity, Vector3.Zero);
                worst = MathF.Max(worst, PoseFrame(s, windy: false));
            }
            check.Expect(worst <= PoseSame,
                         $"E7 a swaying kelp's fronds move as on the stock client, strayed {worst:0.0000}");
        });
    }
}
