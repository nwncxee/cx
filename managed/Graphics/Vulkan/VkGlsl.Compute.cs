using System;
using System.Collections.Generic;

namespace CX.Graphics.Rhi;

internal static partial class VkGlsl
{
    /// <summary>One COMPUTE stage as Vulkan GLSL, by the same rules a program's two stages go through: loose
    /// uniforms into the shared std140 block, bindless constructions into set 1's array, each unbound sampler the
    /// unit <paramref name="samplerUnits"/> names. The text lands in <see cref="VkProgramText.Cs"/>; a stage with
    /// no varyings has no locations to assign, so nothing of the pair's own numbering applies.</summary>
    internal static VkProgramText TranslateCompute(string cs, IReadOnlyDictionary<string, uint> samplerUnits)
    {
        var r = new VkProgramText();
        try
        {
            string t = Builtins(Bindless(cs, out bool bindless, r), out bool drawParams, r);
            if (r.Error != null) return r;
            List<Stmt> stmts = Statements(t);
            Gather(stmts, t, r, new Dictionary<string, int>(StringComparer.Ordinal));
            if (r.Error != null) return r;
            r.Cs = Emit(t, stmts, false, bindless, drawParams, samplerUnits, new Dictionary<string, int>(StringComparer.Ordinal), r);
        }
        catch (FormatException e) { r.Error = e.Message; }
        return r;
    }
}
