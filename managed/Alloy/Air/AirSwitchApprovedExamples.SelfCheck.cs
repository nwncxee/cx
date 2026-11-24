using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;

namespace CX.Alloy;

/// <summary>The Air switch: on it lays the air, off it leaves Volumetric fog's haze as it was, and it starts on and
/// stays as the player left it. Oracle, by hand: the fog stage's upload, the fog terms, the settings panel's row and
/// the composed cx.cfg.</summary>
internal static unsafe class AirSwitchApprovedExamples
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirSwitchApprovedExamples.SelfCheck");
        using var air = AirFixture.Switches.Force();
        OnLays(check);
        HazeStays(check);
        RowFollows(check);
        ChoiceKept(check);
        log.Info(check.Ok ? "AirSwitchApprovedExamples.SelfCheck: passed" : "AirSwitchApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given Air on
    // When the player plays a crypt
    // Then dust lies on the floor.
    private static void OnLays(Checker check)
    {
        Air.Enabled.V = true;
        Enter(Crypt, Room(), 0f, false);
        Go(2f, Vector2.Zero);
        check.Expect(Drawn() && Near(AirLayer.Dust, 0f, 0f, 4f) > 0f, $"E14 Air on: dust lies by the player ({Near(AirLayer.Dust, 0f, 0f, 4f):F3} m)");
    }

    // Given Volumetric fog on
    // When the player turns Air off
    // Then the haze, light shafts and lamp glow look the same as with it on.
    private static void HazeStays(Checker check)
    {
        VolumetricFog.Enabled.V = true;
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, -12f, 8f), Vector3.Zero, Vector3.UnitZ);
        Matrix4x4 vp = view * Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.5f, 200f);
        int* viewport = stackalloc int[] { 0, 0, 1280, 720 };
        Air.Enabled.V = true;
        bool builtOn = FogVolume.Prepare(view, vp, viewport, 200f, true, true, 0f, out FogVolume.Terms with);
        Air.Enabled.V = false;
        bool builtOff = FogVolume.Prepare(view, vp, viewport, 200f, true, true, 0f, out FogVolume.Terms without);
        Air.Enabled.V = true;
        check.Expect(builtOn && builtOff, "E19 Given: the fog volume builds for the camera");
        check.Expect(with.M == without.M && with.Ambient == without.Ambient && with.Local == without.Local
                     && with.Height == without.Height && with.Noise == without.Noise && with.Phase == without.Phase,
                     "E19 the haze, its light and the lamp glow are the same with Air off");
        check.Expect(Vector4.Distance(with.Sun, without.Sun) < 1e-3f, $"E19 ...and so are the light shafts ({with.Sun} against {without.Sun})");
    }

    // Given a fresh install
    // When the player opens the Air settings
    // Then the Air switch is on.
    private static void RowFollows(Checker check)
    {
        check.Expect(Air.Enabled.Default && Air.Enabled.Tier == Tier.User && Air.Enabled.Persisted,
                     "E20 the Air switch is on by default, a row everyone sees, kept in cx.cfg");
        check.Expect(Greyed(Air.G, Air.Enabled) == false, "E20 the Air settings show the switch, live");
    }

    // Given the player turned Air off
    // When they close the game and start it again
    // Then Air is still off.
    private static void ChoiceKept(Checker check)
    {
        Air.Enabled.V = false;
        string stored = Air.Enabled.FormatStored();
        string written = KnobFile.Compose();
        Air.Enabled.V = true;
        string onForm = Air.Enabled.FormatStored();
        check.Expect(Array.IndexOf(written.Split('\n'), $"{Air.Enabled.Key}={stored}") >= 0 && stored != onForm,
                     $"E21 cx.cfg is written with {Air.Enabled.Key}={stored}, which the next start reads as off");
    }
}
