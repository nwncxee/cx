using System;
using System.Collections.Generic;

namespace CX.Graphics.Rhi;

internal static partial class VkGlsl
{
    /// <summary>One COMPUTE stage as Vulkan GLSL, by the same rules a program's two stages go through: loose
    /// uniforms into the shared std140 block (<paramref name="pushTail"/>'s into the push block instead), bindless
    /// constructions into set 1's array, each unbound sampler the unit <paramref name="samplerUnits"/> names. The
    /// text lands in <see cref="VkProgramText.Cs"/>; a stage with no varyings has no locations to assign.</summary>
    internal static VkProgramText TranslateCompute(string cs, IReadOnlyDictionary<string, uint> samplerUnits,
                                                   VkPushTail pushTail = default)
    {
        var r = new VkProgramText { Tail = pushTail };
        if (TailError(pushTail) is { } te) { r.Error = te; return r; }
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
