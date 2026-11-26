using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy;

public sealed unsafe partial class SceneInputs
{
    /// <summary>The engine's lights do not hang on the shadows switch. Oracle, by hand: the shipped settings (cutoff
    /// multiplier 2, intensity 0.2 at range, peak 1.5) answered by a stand-in for the engine's global params.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool LatchSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SceneInputs.LatchApprovedExamples.SelfCheck");
        RiGetGlobalShaderParamfFn saved = _getGspF;
        (Vector2 pair, bool latched) atten = AlloyLights.SaveAtten();
        try
        {
            // Given shadow.enabled=0
            // When the light feed latches the engine's lighting settings
            // Then the pair and the reach are those it latches with shadows on, and the floor does not brighten
            (float reachOn, Vector2 pairOn) = Latched(true);
            (float reachOff, Vector2 pairOff) = Latched(false);
            check.Expect(reachOff == reachOn && pairOff == pairOn,
                $"with shadows off the light feed latches reach {reachOff} and pair {pairOff}, with them on {reachOn} and {pairOn} (want the same)");
            check.Expect(Floor(pairOff) <= Floor(pairOn),
                $"with shadows off a lamp lays {Floor(pairOff):F4} on the floor halfway to its reach, with them on {Floor(pairOn):F4} (want no more)");
        }
        finally
        {
            _getGspF = saved;
            AlloyLights.RestoreAtten(atten);
        }
        log.Info(check.Ok ? "SceneInputs.LatchApprovedExamples.SelfCheck: passed" : "SceneInputs.LatchApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Arm the feed with the shadows switch at <paramref name="shadows"/> from the fallback pair and latch it.</summary>
    private static (float Reach, Vector2 Pair) Latched(bool shadows)
    {
        _getGspF = null;
        AlloyLights.RestoreAtten((new Vector2(1f, 1f), false));
        Arm(shadows, &Shipped);
        float reach = LatchAtten();
        return (reach, AlloyLights.Atten);
    }

    /// <summary>What a lamp lays at half its reach's squared share under <paramref name="pair"/>.</summary>
    private static float Floor(Vector2 pair) => 0.5f / (pair.X + pair.Y * 0.5f);

    /// <summary>The engine's global params at the shipped lighting settings.</summary>
    [UnmanagedCallersOnly]
    private static float Shipped(uint k) => k switch
    {
        0x12 => 2f,    // not-an-offset: global param, the cutoff range multiplier
        0x13 => 0.2f,  // not-an-offset: global param, the intensity at range
        0x14 => 1.5f,  // not-an-offset: global param, the maximum intensity
        _ => 0f,
    };
}
