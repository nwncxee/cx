using System;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

internal static unsafe partial class Halos
{
    /// <summary>This frame's staged flare elements, in texture-group order, for the Vulkan pass.</summary>
    internal static ReadOnlySpan<HaloRec> VkRecords => _st.Rec.AsSpan(0, _st.Count);

    /// <summary>Element <paramref name="i"/>'s flare texture (an owned GL name; 0 = the untextured disc).</summary>
    internal static uint VkTex(int i) => _st.At[i].Tex;

    /// <summary>The halo SSBO's binding.</summary>
    internal const uint VkBind = BindHalos;

    /// <summary>The two stages as GL builds them: the overridable pair, or the embedded one when an override predates
    /// the element chain.</summary>
    internal static (string Vs, string Fs) VkTexts(ExtenderLog log)
    {
        GlProgram.Texts(ShdHaloVs, ShaderStages.Source(ShdHaloVs), ShdHaloFs, ShaderStages.Source(ShdHaloFs), log, "Halos",
                        out string vs, out string fs);
        return ShaderContract.FlareSafe(vs, fs) ? (vs, fs)
            : (ShaderSource.Get(ShdHaloVs + ".glsl"), ShaderSource.Get(ShdHaloFs + ".glsl"));
    }

    /// <summary>The Vulkan pass drew <paramref name="n"/> elements in <paramref name="groups"/> texture groups,
    /// <paramref name="bare"/> untextured; counted into the same period line GL's draw feeds.</summary>
    internal static void VkDrawn(int n, int groups, int bare) { _drawn += n; _groups += groups; _bare += bare; }
}
