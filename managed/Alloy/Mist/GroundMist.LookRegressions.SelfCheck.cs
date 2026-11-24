using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>Area mist's E23: a plain regression, since an area with no fog laid no mist before the mist grew deeper. Oracle, by
/// hand: an area whose fog amount is 0 draws none.</summary>
internal static class GroundMistLookRegressions
{
    /// <summary>The bytes the fog stage is handed.</summary>
    private static readonly byte[] Sent = new byte[GroundMist.PackedBytes];

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GroundMistLookRegressions.SelfCheck");
        bool on = VolumetricFog.Enabled.V, mist = VolumetricFog.Mist.V;
        string quality = VolumetricFog.Quality.V;
        try
        {
            VolumetricFog.Quality.V = "medium";
            VolumetricFog.Enabled.V = true;
            VolumetricFog.Mist.V = true;
            NoFogNoMist(check);
        }
        finally
        {
            VolumetricFog.Enabled.V = on;
            VolumetricFog.Mist.V = mist;
            VolumetricFog.Quality.V = quality;
            GroundMist.Reset();
        }
        log.Info(check.Ok ? "GroundMistLookRegressions.SelfCheck: passed" : "GroundMistLookRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given an area whose fog amount is 0
    // When the player plays it
    // Then no mist lies on the ground.
    private static void NoFogNoMist(Checker check)
    {
        Lay(Flat(), Vector3.Zero, true, 0f);
        Run(2f, Vector2.Zero);
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        check.Expect(MemoryMarshal.Read<Vector4>(Sent).W == 0f && Near(0f, 0f, 4f) == 0f,
                     $"E23 fog amount 0 lays {Near(0f, 0f, 4f):F3} m of mist (want none, and none drawn)");
    }
}
