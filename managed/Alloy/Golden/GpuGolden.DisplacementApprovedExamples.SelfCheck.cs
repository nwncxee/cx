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
        string synth = ShaderSource.Get("cx_ownlit_synth.glsl"), head = ShaderSource.Get("cx_ownlit_head.glsl");
        string march = Function(synth, "vec2 CxDisplace("), steps = Declaration(head, "CX_DISPLACE_STEPS");
        check.Expect(march.Length > 0 && steps.Length > 0, "CX-450: cx_ownlit_synth declares CxDisplace and cx_ownlit_head CX_DISPLACE_STEPS");
        if (march.Length == 0 || steps.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + steps
            + $"float cxH(vec2 p) {{ return {HeightGlsl}; }}\n"
            + "#define textureGrad(t, p, a, b) vec4(cxH(p))\n" + march + CaseBlocks(2, 2) + """
            void main()
            {
                uint i = gl_WorkGroupID.x;
                vec2 r = CxDisplace(vec2(0.3, 0.7), mat3(1.0), normalize(vec3(0.6, 0.2, 0.7)), 3.0, 1080.0, vec2(0.0), vec2(0.0),
                                    vec2(uIn[i * uint(CX_GOLD_IN)], uIn[i * uint(CX_GOLD_IN) + 1u]));
                uOut[i * uint(CX_GOLD_OUT)] = r.x; uOut[i * uint(CX_GOLD_OUT) + 1u] = r.y;
            }
            """, "golden displacement terms");
        string[] mtrs = ["", "parameter float DisplacementMultiplier 2\n", "parameter float DisplacementMultiplier 0\n",
                         "parameter float DisplacementOffset 0.5\n"];
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
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, mtrs.Length, cases, mtrs.Length * 2, "golden displacement terms");
        vk.Destroy(pipe);
        check.Expect(got != null, "CX-450: the displacement stage was refused (logged above)");
        if (got == null) return;
        var c = new DisplaceCase(0.3f, 0.7f, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
                                 Vector3.Normalize(new Vector3(0.6f, 0.2f, 0.7f)), 3f, 1080f);
        (double U, double V) plain = StockDisplace(c), twice = StockDisplace(c, 0.0, 2.0), shifted = StockDisplace(c, 0.5, 0.0);
        bool At(int k, (double U, double V) w) => Math.Abs(got[k * 2] - w.U) <= DisplaceTol && Math.Abs(got[k * 2 + 1] - w.V) <= DisplaceTol;
        string Say(int k) => FormattableString.Invariant($"({got[k * 2]:F5}, {got[k * 2 + 1]:F5})");
        log.Info($"GpuGolden: displacement terms, none {Say(0)}, multiplier 2 {Say(1)}, multiplier 0 {Say(2)}, offset 0.5 {Say(3)}");

        // Given a height-mapped wall whose .mtr sets DisplacementMultiplier 2, seen at an angle
        // When it draws
        // Then its relief shifts the texture twice as far as the same wall with no multiplier, as stock does
        check.Expect(At(1, twice) && !At(1, plain),
                     FormattableString.Invariant($"CX-450 E16: multiplier 2 lands on {Say(1)}, inc_displacement.shd says ({twice.U:F5}, {twice.V:F5})"));

        // Given the same wall with DisplacementMultiplier 0
        // When it draws
        // Then its relief is the same as the wall with no multiplier, as stock does
        check.Expect(At(2, plain), FormattableString.Invariant($"CX-450 E17: multiplier 0 lands on {Say(2)}, the unset wall's ({plain.U:F5}, {plain.V:F5})"));

        // Given a height-mapped wall whose .mtr sets DisplacementOffset 0.5, seen at an angle
        // When it draws
        // Then its texture sits half the relief's full reach further along the view than the same wall with no offset, as stock does
        check.Expect(At(3, shifted) && !At(3, plain),
                     FormattableString.Invariant($"CX-450 E18: offset 0.5 lands on {Say(3)}, inc_displacement.shd says ({shifted.U:F5}, {shifted.V:F5})"));
    }
}
