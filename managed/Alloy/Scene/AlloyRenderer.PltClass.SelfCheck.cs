using System;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Off-device: a palette part with no env map blends where it wears a graded row and cuts where it does
    /// not, and the split it then takes keeps its depth. Oracle: hand-written rows -- the cloth ramp of
    /// <c>pal_cloth01</c> row 44 (alpha 255 down to 112) and row 61 (alpha 0 throughout) -- and hand-written masks.</summary>
    private static bool GradedPltSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.GradedPltSelfCheck");
        byte[] Row(params byte[] alphas)
        {
            var rgba = new byte[alphas.Length * sizeof(uint)];
            Span<uint> px = MemoryMarshal.Cast<byte, uint>(rgba.AsSpan());
            for (int i = 0; i < alphas.Length; i++) px[i] = (uint)alphas[i] << 24;
            return rgba;
        }
        check.Expect(PltPalettes.GradedRow(Row(255, 200, 112)), "the cloth ramp 255..112 is not graded");
        check.Expect(!PltPalettes.GradedRow(Row(0, 0, 0)), "row 61, alpha 0 throughout, is graded");
        check.Expect(!PltPalettes.GradedRow(Row(255, 255, 255)), "a solid row is graded");
        check.Expect(!PltPalettes.GradedRow(Row(255, 51, 0)), "a row whose shades are solid or cut at 0.2 is graded");
        check.Expect(PltPalettes.GradedRow(Row(255, 52)) && PltPalettes.GradedRow(Row(252)),
                     "an alpha the 0.2 cut keeps and the 0.99 solid test does not, 52 or 252, is not graded");
        check.Expect(!PltPalettes.GradedRow(Row(253)), "an alpha the split draws solid, 253, is graded");

        check.Expect(PltImage.LayerMask([9, 0, 9, 4, 9, 4]) == 0b10001, "pairs on layers 0 and 4 do not mask 0b10001");
        check.Expect(PltImage.LayerMask([9, 12, 9, 9]) == 1 << 9, "layer 12, past the ten the engine colours, is masked");

        ushort[] rows = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        bool[] graded = new bool[10];
        graded[4] = true;
        check.Expect(WearsGraded(1 << 4, rows, graded), "a PLT wearing a graded row on its layer 4 does not blend");
        check.Expect(!WearsGraded(0b1111, rows, graded), "a PLT whose layers 0..3 are solid blends for a layer it never uses");
        check.Expect(WearsGraded(0, rows, graded), "a PLT not decoded yet does not ask every layer");
        ushort[] far = [0, 0, 0, 0, 900, 0, 0, 0, 0, 0];
        check.Expect(!WearsGraded(1 << 4, far, graded), "a row past the graded table reads past it");

        const byte O = (byte)AlloyClass.Opaque, C = (byte)AlloyClass.Cutout, B = (byte)AlloyClass.Blended, A = (byte)AlloyClass.Additive;
        check.Expect(PltClass(O, plt: true, envMapped: false, graded: true) == B, "a cloak in a graded colour is not blended");
        check.Expect(PltClass(O, plt: true, envMapped: false, graded: false) == C, "a cloak in a solid colour is not a cutout");
        check.Expect(PltClass(O, plt: true, envMapped: true, graded: true) == O, "an env-mapped PLT, whose alpha is reflectivity, is not opaque");
        check.Expect(PltClass(O, plt: false, envMapped: false, graded: true) == O, "a plain texture is not opaque");
        check.Expect(PltClass(A, plt: true, envMapped: false, graded: true) == A, "an additive recipe lost its class");

        check.Expect(!WashPulls(WashGradedPlt) && WashSplits(WashGradedPlt), "a graded cloak is pulled toward the eye, or does not split");
        check.Expect(WashPulls(WashWhole) && WashPulls(WashSplit) && !WashPulls(0), "a wash stopped pulling, or a body started");
        check.Expect(WashSplits(WashSplit) && !WashSplits(WashWhole) && !WashSplits(0), "a split wash stopped splitting, or a whole one started");
        return check.Ok;
    }
}
