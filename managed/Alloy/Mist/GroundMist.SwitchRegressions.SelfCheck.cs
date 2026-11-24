using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Ui;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The Ground mist switch's approved examples that held the moment its row was declared (E14, E15, E19, E20,
/// E21): plain regressions, since none could go red first. Oracle, by hand: the fog stage's upload, the fog terms,
/// the settings panel's rows and the composed cx.cfg.</summary>
internal static unsafe class GroundMistSwitchRegressions
{
    /// <summary>The Waterdeep docks quicksave's fog amount, read off its area's night palette.</summary>
    private const float Docks = 3f;

    /// <summary>The bytes the fog stage is handed.</summary>
    private static readonly byte[] Sent = new byte[GroundMist.PackedBytes];

    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GroundMistSwitchRegressions.SelfCheck");
        using var switches = MistFixture.Switches.Force();
        OnLays(check);
        FogOffWins(check);
        HazeStays(check);
        RowFollows(check);
        ChoiceKept(check);
        PlayerOff(check);
        log.Info(check.Ok ? "GroundMistSwitchRegressions.SelfCheck: passed" : "GroundMistSwitchRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A mist suite booting in a client whose player turned fog and mist off at Low detail runs with both on at
    /// Medium, and leaves the player's choice as it found it.</summary>
    private static void PlayerOff(Checker check)
    {
        VolumetricFog.Enabled.V = false;
        VolumetricFog.Mist.V = false;
        VolumetricFog.Quality.V = "low";
        bool on, mist;
        string quality;
        using (MistFixture.Switches.Force())
        {
            on = VolumetricFog.Enabled.V;
            mist = VolumetricFog.Mist.V;
            quality = VolumetricFog.Quality.V;
        }
        check.Expect(on && mist && quality == "medium",
            $"a suite under the player's fog off, mist off and low detail runs with fog {on}, mist {mist}, {quality} (want on, on, medium)");
        check.Expect(!VolumetricFog.Enabled.V && !VolumetricFog.Mist.V && VolumetricFog.Quality.V == "low",
            $"after the suite the player's switches read fog {VolumetricFog.Enabled.V}, mist {VolumetricFog.Mist.V}, {VolumetricFog.Quality.V} (want off, off, low)");
    }

    /// <summary>Does the fog stage draw mist, read from a first upload as a fresh fog pass takes it?</summary>
    private static bool Drawn()
    {
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        return MemoryMarshal.Read<Vector4>(Sent).W > 0f;
    }

    /// <summary>The docks as the fixture lays them: open stone ground under the area's fog, two seconds of it.</summary>
    private static void PlayDocks()
    {
        Lay(Flat(), Vector3.Zero, true, Docks);
        Run(2f, Vector2.Zero);
    }

    // Given Volumetric fog is on and Ground mist is on
    // When the player plays the Waterdeep docks
    // Then mist lies on the ground.
    private static void OnLays(Checker check)
    {
        VolumetricFog.Enabled.V = true;
        VolumetricFog.Mist.V = true;
        PlayDocks();
        check.Expect(Drawn() && Near(0f, 0f, 4f) > 0f, $"E14 both on: mist lies by the player ({Near(0f, 0f, 4f):F3} m)");
    }

    // Given Volumetric fog is off and Ground mist is on
    // When the player plays the Waterdeep docks
    // Then no mist lies on the ground.
    private static void FogOffWins(Checker check)
    {
        VolumetricFog.Enabled.V = false;
        VolumetricFog.Mist.V = true;
        PlayDocks();
        check.Expect(!Drawn(), "E15 Volumetric fog off: the fog stage draws no mist, whatever Ground mist says");
    }

    // Given Volumetric fog is on
    // When the player turns Ground mist off
    // Then the haze, light shafts and lamp glow above the ground look the same as with it on.
    private static void HazeStays(Checker check)
    {
        VolumetricFog.Enabled.V = true;
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, -12f, 8f), Vector3.Zero, Vector3.UnitZ);
        Matrix4x4 vp = view * Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.5f, 200f);
        int* viewport = stackalloc int[] { 0, 0, 1280, 720 };
        VolumetricFog.Mist.V = true;
        bool builtOn = FogVolume.Prepare(view, vp, viewport, 200f, true, true, 0f, out FogVolume.Terms with);
        VolumetricFog.Mist.V = false;
        bool builtOff = FogVolume.Prepare(view, vp, viewport, 200f, true, true, 0f, out FogVolume.Terms without);
        check.Expect(builtOn && builtOff, "E19 Given: the fog volume builds for the camera");
        check.Expect(with.M == without.M && with.Ambient == without.Ambient && with.Local == without.Local
                     && with.Height == without.Height && with.Noise == without.Noise && with.Phase == without.Phase,
                     "E19 the haze, its light and the lamp glow are the same with Ground mist off");
        check.Expect(Vector4.Distance(with.Sun, without.Sun) < 1e-3f, $"E19 ...and so are the light shafts ({with.Sun} against {without.Sun})");
    }

    // Given a fresh install
    // When the player opens the Volumetric fog settings
    // Then Ground mist is on, and its row is greyed while Volumetric fog is off and editable once it is on.
    private static void RowFollows(Checker check)
    {
        check.Expect(VolumetricFog.Mist.Default && VolumetricFog.Mist.Tier == Tier.User && VolumetricFog.Mist.Persisted,
                     "E20 Ground mist is on by default, a row everyone sees, kept in cx.cfg");
        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x180; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }
        foreach (int cp in new[] { 0x2014, 0x21BB, 0x25B8, 0x25BE }) { body.SetTestGlyph(cp, 11f); small.SetTestGlyph(cp, 8f); }
        var p = new KnobPanel();
        p.OpenAt(1280f, 720f, 24f, body, small);
        p._subs = new[] { new KnobPanel.SubMenu(VolumetricFog.G) }; p.Sub = 0;
        VolumetricFog.Enabled.V = false;
        p.LayoutRows();
        KnobPanel.Row? off = null;
        foreach (KnobPanel.Row r in p.Rows) if (ReferenceEquals(r.K, VolumetricFog.Mist)) off = r;
        bool greyed = off is { } ro && p.Greyed(ro);
        VolumetricFog.Enabled.V = true;
        p.LayoutRows();
        KnobPanel.Row? live = null;
        foreach (KnobPanel.Row r in p.Rows) if (ReferenceEquals(r.K, VolumetricFog.Mist)) live = r;
        check.Expect(off != null && greyed, "E20 with Volumetric fog off the Ground mist row shows, greyed");
        check.Expect(live is { } rl && !p.Greyed(rl), "E20 with Volumetric fog on it is editable");
    }

    // Given the player turned Ground mist off
    // When they close the game and start it again
    // Then Ground mist is still off.
    private static void ChoiceKept(Checker check)
    {
        VolumetricFog.Mist.V = false;
        string stored = VolumetricFog.Mist.FormatStored();
        string written = KnobFile.Compose();
        VolumetricFog.Mist.V = true;
        string onForm = VolumetricFog.Mist.FormatStored();
        check.Expect(Array.IndexOf(written.Split('\n'), $"{VolumetricFog.Mist.Key}={stored}") >= 0 && stored != onForm,
                     $"E21 cx.cfg is written with {VolumetricFog.Mist.Key}={stored}, which the next start reads as off");
    }
}
