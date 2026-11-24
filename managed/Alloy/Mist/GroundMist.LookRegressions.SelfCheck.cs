using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>Area mist's plain regressions: no fog lays no mist, and mist indoors or under blind lights glows with no moon.
/// Oracle, by hand: an area whose fog amount is 0 draws none; an interior's moon reaches no mist.</summary>
internal static unsafe class GroundMistLookRegressions
{
    /// <summary>The bytes the fog stage is handed.</summary>
    private static readonly byte[] Sent = new byte[GroundMist.PackedBytes];

    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GroundMistLookRegressions.SelfCheck");
        using var switches = MistFixture.Switches.Force();
        bool night = FrameInputs.Night, interior = FrameInputs.Interior, blind = FrameInputs.Blind, hasDif = FrameInputs.HasAreaDiffuse;
        Vector3 dif = FrameInputs.AreaDiffuse;
        try
        {
            NoFogNoMist(check);
            NoMoonIndoors(check);
        }
        finally
        {
            FrameInputs.Night = night; FrameInputs.Interior = interior; FrameInputs.Blind = blind;
            FrameInputs.HasAreaDiffuse = hasDif; FrameInputs.AreaDiffuse = dif;
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

    // Given an interior, or an outdoor area under blind lights, whose moon is pale blue
    // When the player looks at mist there
    // Then the mist takes no moonlight.
    private static void NoMoonIndoors(Checker check)
    {
        foreach ((bool indoors, bool blindLit, string where) in new[] { (true, false, "an interior"), (false, true, "blind lights") })
        {
            FrameInputs.Night = true; FrameInputs.Interior = indoors; FrameInputs.Blind = blindLit;
            FrameInputs.HasAreaDiffuse = true; FrameInputs.AreaDiffuse = new Vector3(0.20f, 0.22f, 0.30f);
            Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, -12f, 8f), Vector3.Zero, Vector3.UnitZ);
            Matrix4x4 vp = view * Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.5f, 200f);
            int* viewport = stackalloc int[] { 0, 0, 1280, 720 };
            FogVolume.Prepare(view, vp, viewport, 200f, true, true, 0f, out FogVolume.Terms t);
            check.Expect(t.MistSky == Vector3.Zero, $"E27 mist under {where} takes {t.MistSky} of the moon (want none)");
        }
    }
}
