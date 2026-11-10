using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The caster pass's GLSL: the depth stages, their masked and layered twins, and the two override guards
/// that refuse a stage which dropped the skinning branch or stopped being inert.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Resrefs the caster pass looks up before falling back to the embedded stage.</summary>
    internal const string ShdCastVs = "cx_cast_vs", ShdCastFs = "cx_cast_fs";

    /// <summary>The caster vertex stage's half of the override contract: an override has to read Alloy's
    /// record through <c>uDraw[gl_BaseInstance]</c> (the ownership slot the gather wrote as baseInstance) and
    /// keep the skinning branch. A stage that lost the branch links perfectly, the bone attributes simply go
    /// unread, and every creature casts its BIND POSE while it animates.</summary>
    private static bool CastVsSafe(string vs)
        => vs.Contains("uDraw[gl_BaseInstance]") && vs.Contains("uBone")
           && !vs.Contains("CxCaster");

    /// <summary>The caster fragment stage must stay inert. It runs into a depth-only FBO, so the only two
    /// things it can say that matter are <c>discard</c> -- which writes no depth, i.e. the caster casts no
    /// shadow at all -- and <c>gl_FragDepth</c>, which displaces the depth the map is compared against into
    /// acne or peter-panning. Neither errors and neither shows in the log.</summary>
    private static bool CastFsSafe(string fs)
        => !fs.Contains("discard") && !fs.Contains("gl_FragDepth");

    /// <summary>Depth vertex stage. <c>gl_BaseInstance</c> IS the ownership slot, so the stage reads Alloy's
    /// own per-draw record directly -- the same numbers the colour pass draws with, not a second copy. Not
    /// gl_DrawID: compaction renumbers that. The skinning branch mirrors the colour stage's arm term for
    /// term, and the rigid arm is <see cref="AlloyRenderer.ClipExpr"/> character-for-character.</summary>
    internal static string CastVs => ShaderSource.Get("cx_cast_v.glsl");
    /// <summary>Depth-only: the fixed-function depth write is the whole output.</summary>
    internal static string CastFs => ShaderSource.Get("cx_cast_f.glsl");

    /// <summary>The masked caster's depth stage: the same clip position, plus the diffuse UVs, so the
    /// fragment stage can punch the cutout's holes into the shadow map. No skinning -- <see cref="Collect"/>
    /// keeps a posed caster out of the masked set, a bind-pose cutout being worse than a solid one. No
    /// resref: an override that drifted from the 0.2 threshold would write shadow onto a bare wall.</summary>
    private static string CastMaskVs => ShaderSource.Get("cx_castmask_v.glsl");
    private static string CastMaskFs => ShaderSource.Get("cx_castmask_f.glsl");

    /// <summary>The layered depth stage. <c>baseInstance</c> packs the view-table index above the ownership
    /// slot (<see cref="CasterGather.ViewShift"/>); the stage routes to its layer and takes its fit matrix
    /// from the view table, so no per-view uniform exists. <c>uVP</c> is deliberately spelled as a LOCAL so
    /// the clip expression stays character for character under the invariant. Embedded only.</summary>
    internal static string CastLayerVs => ShaderSource.Get("cx_castlayer_v.glsl");

    /// <summary>...and the masked twin, <see cref="CastMaskVs"/> with the same routing. No skinning, for
    /// the same bind-pose-cutout reason.</summary>
    private static string CastLayerMaskVs => ShaderSource.Get("cx_castlaymask_v.glsl");
}
