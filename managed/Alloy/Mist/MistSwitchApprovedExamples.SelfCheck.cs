using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The Air switch, live the moment it flips. Oracle, by hand: what the fog stage is handed
/// (<see cref="GroundMist.Pack"/>'s head says whether the air draws) and a stock fog model's live sprites.</summary>
internal static class MistSwitchApprovedExamples
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MistSwitchApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        OffLaysNone(check);
        OffAtOnce(check);
        BackOn(check);
        ModelsFallBack(check);
        log.Info(check.Ok ? "MistSwitchApprovedExamples.SelfCheck: passed" : "MistSwitchApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A crypt room as the air fixture lays it, the player at the origin, two seconds of it.</summary>
    private static void PlayCrypt()
    {
        AirFixture.Enter(AirFixture.Crypt, AirFixture.Room(), 0f, false);
        AirFixture.Go(2f, Vector2.Zero);
    }

    // Given Air off
    // When the player plays a crypt
    // Then no dust lies on the floor.
    private static void OffLaysNone(Checker check)
    {
        Air.Enabled.V = false;
        PlayCrypt();
        check.Expect(!AirFixture.Drawn(), "E13 Air off: the fog stage draws no dust");
        check.Expect(AirFixture.Near(AirLayer.Dust, 0f, 0f, 4f) == 0f, $"E13 ...and none lies by the player ({AirFixture.Near(AirLayer.Dust, 0f, 0f, 4f):F3} m)");
        Air.Enabled.V = true;
    }

    // Given dust on a crypt's floor
    // When the player turns Air off
    // Then the dust is gone at the next frame, without leaving the area.
    private static void OffAtOnce(Checker check)
    {
        Air.Enabled.V = true;
        PlayCrypt();
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, AirFixture.Sent);
        bool lay = MemoryMarshal.Read<Vector4>(AirFixture.Sent).W > 0f;
        Air.Enabled.V = false;
        AirScene.Step(Frame, Vector2.Zero);
        int sent = GroundMist.Pack(ref gen, AirFixture.Sent);
        Air.Enabled.V = true;
        check.Expect(lay, "E16 Given: dust lies on the crypt's floor with the air on");
        check.Expect(sent > 0 && MemoryMarshal.Read<Vector4>(AirFixture.Sent).W == 0f,
                     $"E16 the next frame's upload says no dust ({sent} bytes, head {MemoryMarshal.Read<Vector4>(AirFixture.Sent).W})");
    }

    // Given Air was turned off in a crypt
    // When the player turns it on again
    // Then dust settles back onto the floor within a few seconds, without leaving the area.
    private static void BackOn(Checker check)
    {
        Air.Enabled.V = true;
        PlayCrypt();
        float before = AirFixture.Near(AirLayer.Dust, 0f, 0f, 4f);
        Air.Enabled.V = false;
        AirFixture.Go(2f, Vector2.Zero);
        bool gone = !AirFixture.Drawn();
        Air.Enabled.V = true;
        AirFixture.Go(3f, Vector2.Zero);
        float after = AirFixture.Near(AirLayer.Dust, 0f, 0f, 4f);
        check.Expect(gone, "E17 Given: with Air off the fog stage draws no dust");
        check.Expect(AirFixture.Drawn() && before > 0f && after >= 0.9f * before,
                     $"E17 three seconds after it turns back on the dust lies again ({after:F3} m against {before:F3} m)");
    }

    // Given an area with a stock fog model (tnp_gmist)
    // When the player turns Air off
    // Then its puffs draw again; and when Air turns back on, the puffs go and the air returns, whatever Volumetric fog is set to.
    private static void ModelsFallBack(Checker check)
    {
        const int frames = 300;
        Vector3 at = new(0f, 0f, 0.5f);
        Air.Enabled.V = false;
        VolumetricFog.Enabled.V = false;
        Lay(Flat(), Vector3.Zero, false, 0f);
        int vanilla = EmitterSim.MistEmit("tnp_gmist", new[] { "emitter" }, new[] { at }, frames)[0, frames - 1];
        check.Expect(vanilla > 0, $"E18 Given: with Air and Volumetric fog off the fog model's puffs draw ({vanilla} sprites)");
        foreach (bool fog in new[] { true, false })
        {
            VolumetricFog.Enabled.V = fog;
            Air.Enabled.V = false;
            Lay(Flat(), Vector3.Zero, false, 0f);
            int off = EmitterSim.MistEmit("tnp_gmist", new[] { "emitter" }, new[] { at }, frames)[0, frames - 1];
            float offMist = AirFixture.Near(AirLayer.Dust, 0f, 0f, 2f);
            Air.Enabled.V = true;
            Lay(Flat(), Vector3.Zero, false, 0f);
            int on = EmitterSim.MistEmit("tnp_gmist", new[] { "emitter" }, new[] { at }, frames)[0, frames - 1];
            float onMist = AirFixture.Near(AirLayer.Dust, 0f, 0f, 2f);
            check.Expect(off == vanilla && offMist == 0f, $"E18 Volumetric fog {(fog ? "on" : "off")}, Air off: the puffs draw ({off} against {vanilla}), no air ({offMist:F3} m)");
            check.Expect(on == 0 && onMist > 0f, $"E18 Volumetric fog {(fog ? "on" : "off")}, Air back on: the puffs go ({on}) and the air returns ({onMist:F3} m)");
        }
        VolumetricFog.Enabled.V = true;
    }
}
