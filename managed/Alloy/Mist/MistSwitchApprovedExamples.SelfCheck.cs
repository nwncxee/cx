using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The Ground mist switch: its own row under Volumetric fog, live the moment it flips. Oracle, by hand: what the
/// fog stage is handed (<see cref="GroundMist.Pack"/>'s head says whether mist draws) and a stock fog model's live sprites.</summary>
internal static class MistSwitchApprovedExamples
{
    /// <summary>The Waterdeep docks quicksave's fog amount, read off its area's night palette.</summary>
    private const float Docks = 3f;

    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MistSwitchApprovedExamples.SelfCheck");
        using var switches = MistFixture.Switches.Force();
        OffLaysNone(check);
        OffAtOnce(check);
        BackOn(check);
        ModelsFallBack(check);
        log.Info(check.Ok ? "MistSwitchApprovedExamples.SelfCheck: passed" : "MistSwitchApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The bytes the fog stage is handed.</summary>
    private static readonly byte[] Sent = new byte[GroundMist.PackedBytes];

    /// <summary>Does the fog stage draw mist, read from a first upload as a fresh fog pass takes it?</summary>
    private static bool Drawn()
    {
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        return MemoryMarshal.Read<Vector4>(Sent).W > 0f;
    }

    /// <summary>The docks as the fixture lays them: open stone ground under the area's fog, the player at the origin,
    /// two seconds of it.</summary>
    private static void PlayDocks()
    {
        Lay(Flat(), Vector3.Zero, true, Docks);
        Run(2f, Vector2.Zero);
    }

    // Given Volumetric fog is on and Ground mist is off
    // When the player plays the Waterdeep docks
    // Then no mist lies on the ground.
    private static void OffLaysNone(Checker check)
    {
        VolumetricFog.Enabled.V = true;
        VolumetricFog.Mist.V = false;
        PlayDocks();
        check.Expect(!Drawn(), "E13 Ground mist off: the fog stage draws no mist");
        check.Expect(Near(0f, 0f, 4f) == 0f, $"E13 ...and none lies by the player ({Near(0f, 0f, 4f):F3} m)");
    }

    // Given mist lies on the ground
    // When the player turns Ground mist off in the settings
    // Then the mist is gone at the next frame, without leaving the area.
    private static void OffAtOnce(Checker check)
    {
        VolumetricFog.Enabled.V = true;
        VolumetricFog.Mist.V = true;
        PlayDocks();
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        bool lay = MemoryMarshal.Read<Vector4>(Sent).W > 0f;
        VolumetricFog.Mist.V = false;
        GroundMist.Step(Frame, Vector2.Zero);
        int sent = GroundMist.Pack(ref gen, Sent);
        check.Expect(lay, "E16 Given: mist lies on the ground with both switches on");
        check.Expect(sent > 0 && MemoryMarshal.Read<Vector4>(Sent).W == 0f,
                     $"E16 the next frame's upload says no mist ({sent} bytes, head {MemoryMarshal.Read<Vector4>(Sent).W})");
    }

    // Given Ground mist was turned off in an area with fog
    // When the player turns it on again
    // Then mist settles back onto the ground within a few seconds, without leaving the area.
    private static void BackOn(Checker check)
    {
        VolumetricFog.Enabled.V = true;
        VolumetricFog.Mist.V = true;
        PlayDocks();
        float before = Near(0f, 0f, 4f);
        VolumetricFog.Mist.V = false;
        Run(2f, Vector2.Zero);
        bool gone = !Drawn();
        VolumetricFog.Mist.V = true;
        Run(3f, Vector2.Zero);
        float after = Near(0f, 0f, 4f);
        check.Expect(gone, "E17 Given: with Ground mist off the fog stage draws no mist");
        check.Expect(Drawn() && before > 0f && after >= 0.9f * before,
                     $"E17 three seconds after it turns back on the mist lies again ({after:F3} m against {before:F3} m)");
    }

    // Given an area with a stock fog model (a tnp_gmist placeable)
    // When the player turns Ground mist off with Volumetric fog on
    // Then the fog model's puffs draw again as they do with Volumetric fog off; and when Ground mist turns back on, the puffs go and the mist returns.
    private static void ModelsFallBack(Checker check)
    {
        const int frames = 300;
        Vector3 at = new(0f, 0f, 0.5f);
        VolumetricFog.Enabled.V = false;
        Lay(Flat(), Vector3.Zero, false, 0f);
        int vanilla = EmitterSim.MistEmit("tnp_gmist", new[] { "emitter" }, new[] { at }, frames)[0, frames - 1];

        VolumetricFog.Enabled.V = true;
        VolumetricFog.Mist.V = false;
        Lay(Flat(), Vector3.Zero, false, 0f);
        int off = EmitterSim.MistEmit("tnp_gmist", new[] { "emitter" }, new[] { at }, frames)[0, frames - 1];
        float offMist = Near(0f, 0f, 2f);

        VolumetricFog.Mist.V = true;
        Lay(Flat(), Vector3.Zero, false, 0f);
        int on = EmitterSim.MistEmit("tnp_gmist", new[] { "emitter" }, new[] { at }, frames)[0, frames - 1];
        float onMist = Near(0f, 0f, 2f);

        check.Expect(vanilla > 0, $"E18 Given: with Volumetric fog off the fog model's puffs draw ({vanilla} sprites)");
        check.Expect(off == vanilla && offMist == 0f, $"E18 Ground mist off: the puffs draw as with Volumetric fog off ({off} against {vanilla}), no mist ({offMist:F3} m)");
        check.Expect(on == 0 && onMist > 0f, $"E18 Ground mist back on: the puffs go ({on}) and the mist returns ({onMist:F3} m)");
    }
}
