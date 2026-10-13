using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The checks a model view makes on what a server's policy names before it hands the engine a number it would walk off a table with,
/// even where the engine checks it too. A view or item that fails one stays on its stand-in (or is left out), and the client logs it once.
/// </summary>
internal static class NuiModelGuard
{
    /// <summary>The base item the engine's item builder treats as a cloak: its model number is a row of <c>cloakmodel.2da</c>, not a file.</summary>
    internal const int CloakBaseItem = 80;   // not-an-offset: BASE_ITEM_CLOAK
    /// <summary>The most distinct refusals one run writes to the log, so a server cannot grow it by naming a new bad id each time.</summary>
    private const int MaxSaid = 64;
    private static readonly HashSet<string> _said = new();

    /// <summary>Is <paramref name="row"/> inside a table of <paramref name="rows"/> rows? A count below zero is a table the client could not read,
    /// which refuses nothing.</summary>
    internal static bool RowInTable(int row, int rows) => rows < 0 || (row >= 0 && row < rows);

    /// <summary>True the first time <paramref name="key"/> is asked, so a refusal is logged once. Past <see cref="MaxSaid"/> keys it is false.</summary>
    internal static bool Once(string key) => _said.Count < MaxSaid && _said.Add(key);

    /// <summary>Forget what was said, for the self-check.</summary>
    private static void Forget() => _said.Clear();

    /// <summary>The model files the engine loads for an item, in the names <c>CNWBaseItem::GetModelResRef</c> builds: a simple or layered base item
    /// (<paramref name="modelType"/> 0 or 1) one, <c>class_nnn</c>; a composite one (2) three, <c>class_b_nnn</c>, <c>class_m_nnn</c> and
    /// <c>class_t_nnn</c>; armour (3) none, since a body is built from parts. Lower case, the number at least three digits.
    /// Returns how many names went into <paramref name="into"/>.</summary>
    internal static int ItemModels(byte modelType, string itemClass, ReadOnlySpan<ushort> models, Span<string> into)
    {
        string c = itemClass.ToLowerInvariant();
        switch (modelType)
        {
            case 0 or 1:
                if (models.Length < 1 || into.Length < 1) return 0;
                into[0] = $"{c}_{models[0]:D3}";
                return 1;
            case 2:
                if (models.Length < 3 || into.Length < 3) return 0;
                for (int i = 0; i < 3; i++) into[i] = $"{c}_{"bmt"[i]}_{models[i]:D3}";
                return 3;
            default:
                return 0;
        }
    }

    /// <summary>Oracle: hand-written hostile rows against hand-written table sizes, and model names written out from the engine's two
    /// formats (<c>%s_%03d</c> and <c>%s_%c_%03d</c> with the chunk letters B, M and T), one of them a file the game ships
    /// (<c>wswls_b_011</c>).</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        (int Row, int Rows, bool Ok)[] table =
        {
            (0, 1, true), (0, 0, false), (4, 5, true), (5, 5, false), (65535, 65535, false), (65535, 70000, true),
            (-1, 10, false), (int.MinValue, 10, false), (int.MaxValue, 10, false), (7, -1, true), (0, int.MinValue, true),
            (65000, 612, false), (611, 612, true), (612, 612, false),
        };
        foreach (var (row, rows, ok) in table)
            check.Expect(RowInTable(row, rows) == ok, $"row {row} in a table of {rows} rows is {(ok ? "taken" : "refused")}");

        var names = new string[3];
        int n = ItemModels(2, "WSWLS", new ushort[] { 11, 11, 11 }, names);
        check.Expect(n == 3 && names[0] == "wswls_b_011" && names[1] == "wswls_m_011" && names[2] == "wswls_t_011", "a composite weapon is three files, bottom, middle and top");
        n = ItemModels(2, "wswls", new ushort[] { 61, 1, 65535 }, names);
        check.Expect(n == 3 && names[0] == "wswls_b_061" && names[1] == "wswls_m_001" && names[2] == "wswls_t_65535", "numbers are padded to three digits and never cut");
        n = ItemModels(1, "helm", new ushort[] { 5, 9, 9 }, names);
        check.Expect(n == 1 && names[0] == "helm_005", "a layered item is one file named by its first model");
        n = ItemModels(0, "it_x", new ushort[] { 0 }, names);
        check.Expect(n == 1 && names[0] == "it_x_000", "model 0 is a file too");
        check.Expect(ItemModels(3, "armor", new ushort[19], names) == 0, "armour is built from body parts and names no item file");
        check.Expect(ItemModels(4, "x", new ushort[3], names) == 0 && ItemModels(255, "x", new ushort[3], names) == 0, "an unknown model type names none");
        check.Expect(ItemModels(2, "x", new ushort[2], names) == 0 && ItemModels(1, "x", ReadOnlySpan<ushort>.Empty, names) == 0, "too few models name none");
        check.Expect(ItemModels(2, "x", new ushort[3], new string[2]) == 0, "too little room names none");

        Forget();
        check.Expect(Once("vfx 65000") && !Once("vfx 65000") && Once("vfx 65001"), "a refusal is said once per value");
        for (int i = 0; i < 200; i++) Once($"hostile {i}");
        check.Expect(!Once("one more"), "past the cap nothing new is said");
        Forget();
    }
}
