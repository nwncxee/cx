using System;
using System.Collections.Generic;

namespace CX.Graphics.Rhi;

internal static partial class VkGlsl
{
    /// <summary>A mesh program's three stages as Vulkan GLSL, by the rules a pair goes through: one loose block all three
    /// declare alike, the mesh stage's outputs located as a vertex stage's are and the fragment stage's inputs matched to
    /// them by name. The texts land in <see cref="VkProgramText.Ts"/>, <see cref="VkProgramText.Ms"/> and
    /// <see cref="VkProgramText.Fs"/>; clip z passes through, as a world program's does.</summary>
    internal static VkProgramText TranslateMesh(string ts, string ms, string fs, IReadOnlyDictionary<string, uint> samplerUnits)
    {
        var r = new VkProgramText();
        try
        {
            string tt = Builtins(Bindless(ts, out bool bt, r), out bool dt, r);
            string tm = Builtins(Bindless(ms, out bool bm, r), out bool dm, r);
            string tf = Builtins(Bindless(fs.Replace(GlFragZ, "(1.0 - gl_FragCoord.z)", StringComparison.Ordinal), out bool bf, r), out bool df, r);
            if (r.Error != null) return r;
            List<Stmt> st = Statements(tt), sm = Statements(tm), sf = Statements(tf);
            string all = tt + "\n" + tm + "\n" + tf;
            var looseIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            Gather(st, all, r, looseIdx);
            Gather(sm, all, r, looseIdx);
            Gather(sf, all, r, looseIdx);
            if (r.Error != null) return r;
            var outLoc = new Dictionary<string, int>(StringComparer.Ordinal);
            r.Ts = Emit(tt, st, true, bt, dt, samplerUnits, new Dictionary<string, int>(StringComparer.Ordinal), r);
            r.Ms = Emit(tm, sm, true, bm, dm, samplerUnits, outLoc, r);
            r.Fs = Emit(tf, sf, false, bf, df, samplerUnits, outLoc, r);
        }
        catch (FormatException e) { r.Error = e.Message; }
        return r;
    }
}
