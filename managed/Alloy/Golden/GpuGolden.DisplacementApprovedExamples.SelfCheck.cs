using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// A material's own parallax terms (approved-spec, E16..E18): <c>.mtr</c> text through <see cref="MtrDef.Parse"/>
/// and <see cref="MtrParams.Fold"/>, the two values it lands into the shipped <c>CxDisplace</c>. Oracle:
/// <see cref="StockDisplace"/>, <c>inc_displacement.shd</c>'s own march written out, with the offset and multiplier
/// the material text names.
/// </summary>
internal static partial class GpuGolden
{
    private static void DisplacementApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        const string Pair = "customshadervs p_v\ncustomshaderfs p_f\n";
        string[] mtrs = ["", "parameter float DisplacementMultiplier 2\n", Pair + "parameter float DisplacementMultiplier 0\n",
                         "parameter float DisplacementOffset 0.5\n", Pair + "parameter float DisplacementMultiplier 2\n"];
        var cases = new float[mtrs.Length * 2];
        for (int k = 0; k < mtrs.Length; k++)
        {
            var param = new ParamRow();
            Span<float> feat = stackalloc float[4];
            feat.Clear();
            MtrParams.ResetExt(ref param.Ext);
            MtrParams.Fold(MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes("texture4 wall_h\n" + mtrs[k]), "displacement"), ref param, feat);
            cases[k * 2] = param.DisplacementOffset;
            cases[k * 2 + 1] = param.DisplacementMultiplier;
        }
        if (DisplaceRuns(vk, check, log, cases) is not { } got) return;
        DisplaceCase c = DisplaceWall;
        (double U, double V) plain = StockDisplace(c), twice = StockDisplace(c, 0.0, 2.0);
        bool At(int k, (double U, double V) w) => DisplacedTo(got, k, w);
        string Say(int k) => FormattableString.Invariant($"({got[k * 2]:F5}, {got[k * 2 + 1]:F5})");
        log.Info($"GpuGolden: displacement terms, none {Say(0)}, plain multiplier 2 {Say(1)}, pair multiplier 0 {Say(2)}, plain offset 0.5 {Say(3)}, pair multiplier 2 {Say(4)}");

        // Given a height-mapped wall on a stock shader whose .mtr sets DisplacementMultiplier 2, seen at an angle
        // When it draws
        // Then its relief is the same as the wall with no multiplier, as stock does
        check.Expect(At(1, plain) && !At(1, twice),
                     FormattableString.Invariant($"E16: a plain material's multiplier 2 lands on {Say(1)}, the unset wall's is ({plain.U:F5}, {plain.V:F5})"));

        // Given a height-mapped wall whose .mtr names its own shader pair and sets DisplacementMultiplier 0
        // When it draws
        // Then its relief is the same as the wall with no multiplier, as stock does
        check.Expect(At(2, plain), FormattableString.Invariant($"E17: a pair's multiplier 0 lands on {Say(2)}, the unset wall's is ({plain.U:F5}, {plain.V:F5})"));

        // Given a height-mapped wall on a stock shader whose .mtr sets DisplacementOffset 0.5, seen at an angle
        // When it draws
        // Then its texture sits where the same wall with no offset has it, as stock does
        check.Expect(At(3, plain), FormattableString.Invariant($"E18: a plain material's offset 0.5 lands on {Say(3)}, the unset wall's is ({plain.U:F5}, {plain.V:F5})"));

        // Given a height-mapped wall whose .mtr names its own shader pair and sets DisplacementMultiplier 2, seen at an angle
        // When it draws
        // Then its relief shifts the texture twice as far as the same wall with no multiplier, as stock does
        check.Expect(At(4, twice) && !At(4, plain),
                     FormattableString.Invariant($"E19: a pair's multiplier 2 lands on {Say(4)}, inc_displacement.shd says ({twice.U:F5}, {twice.V:F5})"));

        // Given a height-mapped wall seen at an angle
        // When the client's shader quality is Performance
        // Then its relief traces half the steps it traces at High Quality, and its crevices take no light fade, as stock does
        if (DisplaceRuns(vk, check, log, [0f, 0f], quality: 1) is not { } low || SurfaceFades(vk, check, log) is not { } fades) return;
        (double U, double V) sixteen = StockDisplace(c, maxSteps: 16.0);
        check.Expect(DisplacedTo(low, 0, sixteen) && !DisplacedTo(low, 0, plain),
                     FormattableString.Invariant($"E20: Performance's relief lands on ({low[0]:F5}, {low[1]:F5}), inc_displacement.shd says ({sixteen.U:F5}, {sixteen.V:F5})"));
        check.Expect(fades[0] == 0f && fades[1] > 0f, $"E20: the surface fade is {fades[0]} at Performance and {fades[1]} at High Quality");
    }
}
