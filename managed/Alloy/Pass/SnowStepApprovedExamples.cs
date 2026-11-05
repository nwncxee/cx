using System.Text;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class Weather
{
    /// <summary>The stock footstepsounds.2da cells the examples read, transcribed from the game's own table:
    /// the oracle for which sound a creature's snow step names.</summary>
    private static string StockCell(int row, string column) => (row, column) switch
    {
        (HorseRow, "Snow0") => "fs_snow_horse1", (HorseRow, "Snow1") => "fs_snow_horse2", (HorseRow, "Snow2") => "fs_snow_horse3",
        (DragonRow, "Snow0") => "fs_x0snw_drg1", (DragonRow, "Snow1") => "fs_x0snw_drg2", (DragonRow, "Snow2") => "fs_x0snw_drg3",
        _ => "",
    };
    /// <summary>footstepsounds.2da rows the fixture holds; oracle: the stock table.</summary>
    private const int HorseRow = 17, DragonRow = 2;

    /// <summary>approved-spec: the examples that are false until snow steps exist. Oracle: the hand-written kinds and
    /// the stock table's cells. The creature's own row is the engine's table; only a live listen hears it.</summary>
    static partial void SnowStepApprovedExamples(Checker check) => WithStepScene(() =>
    {
        // Given snow lying on open stone ground
        // When my character walks across it
        // Then each step makes the game's snow footstep sound, not the stone one
        _snow = 1f;
        check.Expect(ChooseStep(true, StoneRow) == StepKind.Snow, "lying snow on open stone: a snow step");

        // Given a horse and a dragon walking through lying snow
        // When each takes a step
        // Then each makes its own kind's snow footstep sound
        bool ownSound = true;
        for (int pick = 0; pick < 3; pick++)
        {
            string column = Encoding.ASCII.GetString(StepColumnName(StepKind.Snow, pick));
            ownSound &= StockCell(HorseRow, column) == $"fs_snow_horse{pick + 1}" && StockCell(DragonRow, column) == $"fs_x0snw_drg{pick + 1}";
        }
        check.Expect(ownSound, "a snow step names the columns where the stock table keeps each kind's snow sounds");

        // Given snow still lying on ground that rain is now wetting
        // When my character walks across it
        // Then each step makes the snow sound, and the splash sound once the snow has melted away
        _snow = 1f; _wet = 1f;
        StepKind lying = ChooseStep(true, StoneRow);
        _snow = 0f;
        StepKind melted = ChooseStep(true, StoneRow);
        check.Expect(lying == StepKind.Snow && melted == StepKind.Puddles, $"snow over wet ground: snow, then the splash once melted ({lying}, {melted})");
    });
}
