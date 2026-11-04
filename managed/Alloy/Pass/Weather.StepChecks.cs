using System;
using System.Numerics;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class Weather
{
    /// <summary>surfacemat.2da rows the step checks stand on; oracle: the stock table.</summary>
    private const int StoneRow = 4, WaterRow = 6, CarpetRow = 9, SnowRow = 19;

    /// <summary>Runs <paramref name="body"/> with the weather on and every knob a footfall reads at its full value,
    /// on bare dry ground, and restores all of it: the player's cx.cfg is live here.</summary>
    private static void WithStepScene(Action body)
    {
        float snow = _snow, wet = _wet, ws = WetStrength.V, ss = SnowStrength.V, fill = TrackFill.V;
        bool off = _off, en = Enabled.V, wetOn = WetSteps.V, snowOn = SnowFootsteps.V, tr = Tracks.V;
        TrackTexel[]? track = _track; FootSideState sides = _side; int prints = _prints;
        _off = false; Enabled.V = true; WetSteps.V = true; SnowFootsteps.V = true; Tracks.V = true;
        WetStrength.V = 1f; SnowStrength.V = 1f; TrackFill.V = TrackFill.Default; _snow = 0f; _wet = 0f;
        body();
        _snow = snow; _wet = wet; WetStrength.V = ws; SnowStrength.V = ss; TrackFill.V = fill;
        _off = off; Enabled.V = en; WetSteps.V = wetOn; SnowFootsteps.V = snowOn; Tracks.V = tr;
        _track = track; _side = sides; _prints = prints;
    }

    /// <summary>The approved examples that hold before snow steps exist, so they cannot go red first. Oracle: the
    /// hand-written expected kinds, and for an empty cell the stock table's own text.</summary>
    private static void StepSelfCheck(Checker check) => WithStepScene(() =>
    {
        check.Expect(!StepInWeather, "dry bare ground casts no shelter ray");
        _snow = 1f;
        check.Expect(StepInWeather, "lying snow on dry ground asks whether the sky is open");

        // Given the same ground after the snow has melted away
        // When my character walks across it
        // Then each step makes the ordinary stone sound
        _snow = 0f;
        check.Expect(ChooseStep(true, StoneRow) == StepKind.Engine, "melted snow: an ordinary step");

        // Given snow has only just started falling and the ground still looks bare
        // When my character walks across it
        // Then each step makes the ordinary sound for that ground
        _snow = 0.1f;
        check.Expect(ChooseStep(true, StoneRow) == StepKind.Engine, "a first dusting of snow: an ordinary step");

        // Given snow lying in the open around a building with a roof over its porch
        // When my character walks under the roof
        // Then each step makes the ordinary sound for that ground
        _snow = 1f;
        check.Expect(ChooseStep(false, StoneRow) == StepKind.Engine, "under a roof: an ordinary step");

        // Given a creature the game has no snow footstep sound for
        // When it walks through lying snow
        // Then it makes whatever sound it makes on that ground without the snow
        byte* res = stackalloc byte[ResRefSize];
        fixed (byte* empty = "****\0"u8, none = "\0"u8, hard = "FS_x0snw_hard1\0"u8)
            check.Expect(!CellResRef(empty, res) && !CellResRef(none, res) && !CellResRef(null, res) && CellResRef(hard, res)
              && Encoding.ASCII.GetString(res, 14) == "fs_x0snw_hard1", "an empty footstep cell plays nothing, so the engine's step goes ahead");

        // Given lying snow in an area with a stream, a carpet and a tile built as snow
        // When my character walks on each
        // Then each makes the same sound it makes with no weather
        check.Expect(ChooseStep(true, WaterRow) == StepKind.Engine && ChooseStep(true, CarpetRow) == StepKind.Engine
          && ChooseStep(true, SnowRow) == StepKind.Engine, "water, carpet and a snow tile keep their own steps under lying snow");

        // Given lying snow and Snow footstep sounds switched off
        // When my character walks across it
        // Then each step makes the ordinary sound for that ground
        SnowFootsteps.V = false;
        check.Expect(ChooseStep(true, StoneRow) == StepKind.Engine, "the setting off: an ordinary step");

        // Given lying snow and Snow footstep sounds switched off
        // When my character walks across it
        // Then footprints still press into the snow
        _track = new TrackTexel[TrackSize * TrackSize];
        _side = new FootSideState();
        Vector2 h = Vector2.UnitX;
        FootDown(new Vector3(20f, 20f, 0f), h, 7, 1f, true);
        check.Expect(MathF.Max(TrackAt(_track, new Vector2(20f + FootAhead + ToeAt, 20f - FootSide), 1f, 0.01f),
                               TrackAt(_track, new Vector2(20f + FootAhead + ToeAt, 20f + FootSide), 1f, 0.01f)) > 0.5f,
          "the setting off: footprints still press");
        SnowFootsteps.V = true;

        // Given snow falling and Snow cover set to 0, so nothing shows on the ground
        // When my character walks across it
        // Then each step makes the ordinary sound for that ground
        SnowStrength.V = 0f;
        check.Expect(ChooseStep(true, StoneRow) == StepKind.Engine, "snow cover at zero: an ordinary step");
        SnowStrength.V = 1f;
    });
}
