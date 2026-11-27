using System;
using System.Collections.Generic;
using System.Numerics;

namespace CX.Alloy;

/// <summary>Which emitters Alloy draws as ground mist: every emitter of a stock fog model, picked by model resref, and
/// in any other model a node whose name starts with <c>cxmist</c>, any case.</summary>
internal static class MistEmitters
{
    /// <summary>The stock models whose emitters are fog banks on the ground.</summary>
    private static readonly HashSet<string> Stock = new(StringComparer.OrdinalIgnoreCase)
    {
        "tnp_gmist", "dag_fog_lrg", "dag_fog_lrg_hvy", "dag_fog_med",
        "silm_fog_50_lo", "silm_fog_50_mi", "silm_fog_50_hi", "silm_fog_50_vl", "silm_fog_50_sp",
        "silm_ifog_50_lo", "silm_ifog_50_mi", "silm_ifog_50_hi", "silm_ifog_50_vl", "silm_ifog_50_sp",
    };

    /// <summary>The node name prefix a hak marks a mist emitter with.</summary>
    private const string Mark = "cxmist";

    /// <summary>One frame of the emitter <paramref name="node"/> of <paramref name="model"/> at <paramref name="at"/>:
    /// true when the fog takes it as mist, which it then feeds, and its sprites do not draw.</summary>
    internal static bool Take(string model, string node, Vector3 at, float reach)
    {
        if (!AirScene.On() || !(Stock.Contains(model) || node.StartsWith(Mark, StringComparison.OrdinalIgnoreCase))) return false;
        GroundMist.Source(at, reach);
        return true;
    }
}
