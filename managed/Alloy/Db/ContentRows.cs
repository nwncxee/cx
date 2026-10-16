using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>A content stage's side-table lanes (<see cref="TintTerms.Row.Cp"/>): what each uniform the stage declares
/// reads, from the material's <c>.mtr</c> <c>parameter</c> lines and the object's own <c>SetMaterialShaderUniform*</c>
/// values, under the engine's own upload rules. Engine-free, so the self-check drives it on hand-written materials.</summary>
internal static class ContentRows
{
    /// <summary>The <c>MaterialParameter</c> name field the engine matches an object's override against
    /// (<c>Material::SetMaterialParam</c> @0x4c5d70: <c>strncasecmp</c> over 0x40).</summary>
    private const int NameChars = 64;

    /// <summary>Fill <paramref name="row"/>'s content lanes for a stage laid out as <paramref name="ps"/>. A lane the
    /// material does not state -- or states in a shape its uniform cannot take -- stays off, and the stage reads the
    /// uniform's own GLSL default there, as the engine's program would with no upload behind it.</summary>
    internal static void Fill(ref TintTerms.Row row, MtrDef? mtr, ReadOnlySpan<ContentStage.Param> ps,
                              IReadOnlyList<Seam.MaterialParams.ContentOverride>? overs)
    {
        row.CpOn = 0;
        row.Cp = default;
        if (mtr == null) return;
        foreach (ContentStage.Param p in ps)
        {
            if ((uint)p.Lane >= TintTerms.ContentLanes) continue;
            // the engine binds a parameter to the uniform its .mtr name spells, case for case (glGetUniformLocation);
            // the LAST such line wins, as each one re-uploads over the one before
            int at = -1;
            for (int i = 0; i < mtr.Parameters.Count; i++)
                if (mtr.Parameters[i].Name == p.Name) at = i;
            if (at < 0) continue;
            (string name, float[] values, bool isInt) = mtr.Parameters[at];
            if (overs != null)
                foreach (Seam.MaterialParams.ContentOverride o in overs)
                    if (o.Applies(mtr.Name) && string.Compare(o.Name, 0, name, 0, NameChars, StringComparison.OrdinalIgnoreCase) == 0)
                        values = Reparse(o.Value, isInt, values.Length) ?? values;
            if (!Fits(p.Kind, isInt, values.Length)) continue;
            row.Cp[p.Lane] = new Vector4(values[0], values.Length > 1 ? values[1] : 0f,
                                         values.Length > 2 ? values[2] : 0f, values.Length > 3 ? values[3] : 0f);
            row.CpOn |= 1u << p.Lane;
        }
    }

    /// <summary>Does the engine's upload of a <c>parameter</c> line reach a uniform of this kind? An <c>int</c> line is
    /// <c>glUniform1i</c>, a one-value <c>float</c> line <c>glUniform1f</c>, and any longer one <c>glUniform4fv</c>
    /// (<c>GLRender::SetUniforms</c> @0x4ccbf0); a call that does not fit the declared type is refused by GL.</summary>
    internal static bool Fits(ContentStage.ParamKind kind, bool isInt, int count)
        => count >= 1 && (isInt ? kind is ContentStage.ParamKind.Int or ContentStage.ParamKind.Bool
                         : count == 1 ? kind is ContentStage.ParamKind.Float or ContentStage.ParamKind.Bool
                         : kind == ContentStage.ParamKind.Vec4);

    /// <summary>An object's override string, read under the entry's own type, as the engine's synthetic re-parse
    /// does: an int takes its first token, a one-value float its first token, any other float as many as scan
    /// (the count moves with them). Null when nothing scans.</summary>
    internal static float[]? Reparse(string value, bool isInt, int count)
    {
        string[] tok = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length == 0) return null;
        if (isInt)
            return int.TryParse(tok[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? [i] : null;
        if (count == 1)
            return float.TryParse(tok[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? [f] : null;
        var vals = new List<float>(4);
        foreach (string t in tok)
        {
            if (vals.Count == 4 || !float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) break;
            vals.Add(v);
        }
        return vals.Count == 0 ? null : vals.ToArray();
    }

    /// <summary>Off-device. Oracle: a hand-written <c>.mtr</c>, hand-written overrides as the engine's NWScript path
    /// formats them (<c>%f %f %f %f</c>, <c>%d</c>) and the values worked out by hand.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        MtrDef mtr = MtrDef.Parse(System.Text.Encoding.ASCII.GetBytes(
            "customshadervs vslit\ncustomshaderfs rambo\nparameter float fScale 26.0\n"
            + "parameter float vBase 0.58 0.60 0.43\nparameter int iMode 1\nparameter float vMis 1 2\n"
            + "parameter float fscale 99.0\n"), "rambo_mat");
        ContentStage.Param[] ps =
        [
            new("fScale", ContentStage.ParamKind.Float, 0), new("vBase", ContentStage.ParamKind.Vec4, 1),
            new("iMode", ContentStage.ParamKind.Int, 2), new("vMis", ContentStage.ParamKind.Float, 3),
            new("fAbsent", ContentStage.ParamKind.Float, 4),
        ];
        var row = default(TintTerms.Row);
        Fill(ref row, mtr, ps, null);
        check.Expect(row.Cp[0].X == 26f, "fScale is not the value its own-case .mtr line states (a differently cased line won)");
        check.Expect(row.Cp[1] == new Vector4(0.58f, 0.60f, 0.43f, 0f), "a three-value line does not reach a vec4 as (r, g, b, 0)");
        check.Expect(row.Cp[2].X == 1f, "an int line does not reach an int uniform");
        check.Expect(row.CpOn == 0b0111, $"lanes on are {Convert.ToString(row.CpOn, 2)}: a two-value line fed a float, "
                                         + "or a parameter the .mtr never states was stated");
        var overs = new List<Seam.MaterialParams.ContentOverride>
        {
            new("", "VBASE", "0.100000 0.200000 0.300000 1.000000"),
            new("other_mat", "fScale", "5.0"),
            new("RAMBO_MAT", "iMode", "0"),
            new("", "fAbsent", "3.0"),
        };
        Fill(ref row, mtr, ps, overs);
        check.Expect(row.Cp[1] == new Vector4(0.1f, 0.2f, 0.3f, 1f), "a Vec4 override did not replace the vec4, case-blind on the name");
        check.Expect(row.Cp[0].X == 26f, "an override filtered to another material reached this one");
        check.Expect(row.Cp[2].X == 0f, "an override filtered to this material, in another case, did not reach it");
        check.Expect((row.CpOn & (1u << 4)) == 0, "an override added a parameter the .mtr never declares");
        check.Expect(Reparse("7 8", isInt: false, count: 1) is [7f] && Reparse("x", isInt: true, count: 1) == null,
                     "a one-value float does not keep only its first token, or a non-number scanned");
        log.Info(check.Ok ? "ContentRows.SelfCheck: passed" : "ContentRows.SelfCheck: FAILED");
        return check.Ok;
    }
}
