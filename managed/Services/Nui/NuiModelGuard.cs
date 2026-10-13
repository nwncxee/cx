using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The checks a model view makes on what a server's policy names before it hands the engine a number it would walk off a table with.
/// A view that fails one stays on its stand-in.
/// </summary>
internal static class NuiModelGuard
{
    /// <summary>Is <paramref name="row"/> inside a table of <paramref name="rows"/> rows? A count below zero is a table the client could not read,
    /// which refuses nothing.</summary>
    internal static bool RowInTable(int row, int rows) => rows < 0 || (row >= 0 && row < rows);

    /// <summary>Oracle: hand-written hostile rows against hand-written table sizes.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        (int Row, int Rows, bool Ok)[] table =
        {
            (0, 1, true), (0, 0, false), (4, 5, true), (5, 5, false), (65535, 65535, false), (65535, 70000, true),
            (-1, 10, false), (int.MinValue, 10, false), (int.MaxValue, 10, false), (7, -1, true), (0, int.MinValue, true),
        };
        foreach (var (row, rows, ok) in table)
            check.Expect(RowInTable(row, rows) == ok, $"row {row} in a table of {rows} rows is {(ok ? "taken" : "refused")}");
    }
}
