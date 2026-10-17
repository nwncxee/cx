using System;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The names the Alloy pass's embedded GLSL is reached by. The text itself lives in
/// <c>managed/Alloy/Shaders/*.glsl</c>; what is left here is one accessor per stage or library the C# side
/// names -- a contract predicate that greps it, a program that compiles it -- as <c>partial</c>-class
/// members of the same type, so every call site names them unqualified.</summary>
public sealed unsafe partial class AlloyRenderer
{

    /// <summary>Resrefs the Alloy colour pass looks up before falling back to the embedded stage: an author
    /// drops <c>&lt;name&gt;.shd</c> in a hak, <c>override/</c> or NWSync and gets that stage instead. The lit
    /// fragment resref replaces the whole composed stage, <see cref="AlloyLights.FragmentLib"/> included, so an
    /// override carries its own lighting helpers (sized by the <see cref="ShaderResource.Tokens"/>).</summary>
    internal const string ShdFullbrightVs = "cx_own_vs", ShdFullbrightFs = "cx_own_fs";
    internal const string ShdLitVs = "cx_ownlit_vs", ShdLitFs = "cx_ownlit_fs";
    /// <summary>The water stage. Its own pair rather than the lit one's, so an author can rewrite the water
    /// surface without forking every lit part in the world.</summary>
    internal const string ShdWaterVs = "cx_water_vs", ShdWaterFs = "cx_water_fs";

    /// <summary>The per-draw block, reached through <c>uDrawIdx[uDrawBase + gl_DrawID]</c>: everything that
    /// varies per part, so one multidraw covers a group. Records persist by ownership slot; its binding
    /// must match <see cref="BindDraw"/>, <c>rot</c> is (cos, sin) of the tile texture rotation and
    /// <c>prev</c> last frame's model matrix. ponytail: GL 4.6 requires no vertex SSBO; a refused link is loud.</summary>
    internal static string DrawBufDecl => ShaderSource.Get("cx_drawbuf.glsl");

    /// <summary>The feature ids, as GLSL: one contract with <see cref="ShaderFeature"/>, whose C# value is
    /// staged into <c>d.feat.x</c> and compared against these. <see cref="SelfCheck"/> asserts the two agree
    /// rather than trusting a comment to keep them in step.</summary>
    internal static string FeatDecl => ShaderSource.Get("cx_featdecl.glsl");

    /// <summary>The skinning half of the Alloy vertex stages, vertex-side only and shared by both stages
    /// verbatim so a robe shades identically lit or unlit. its binding must stay in step with
    /// <see cref="BindBones"/> and the attribute locations with <see cref="LocBoneIdx"/>/<see cref="LocBoneW"/>;
    /// a std430 <c>mat4</c> array's 64-byte stride IS the engine's <c>m_bones</c>, so the CPU side is a memcpy.</summary>
    internal static string BoneVs => ShaderSource.Get("cx_bone_v.glsl");

    /// <summary>The fog, split out of the keyhole so a stage can have one without the other (particles fog and
    /// never dissolve; the depth prepass dissolves and never fogs). The ramp is untouched engine --
    /// <c>GetFog</c>'s linear lerp between the area author's start and end, in view depth -- but the target
    /// colour is built per ray rather than being one constant, and the factor can be thinned by height.</summary>
    internal static string FogFs => ShaderSource.Get("cx_fog.glsl");

    /// <summary>The keyhole and the fog, in the order every fragment stage wants them. Declared after
    /// both halves: <c>check-shaders.py</c> inlines only constants it has already read.</summary>
    internal static string KeyFogFs => ShaderSource.Get("cx_keyfog.glsl");

    /// <summary>The VANILLA ENVIRONMENT -- everything a spliced stock include would have got from the
    /// <c>#include</c> lines <see cref="VanillaShader"/> drops, in ONE block ahead of every marked region:
    /// two splices both pulling <c>inc_scriptable</c> would declare one uniform twice, a compile error.
    /// <c>CX_ALLOY</c> is the content repo's contract; <c>LIGHTING 0</c> compiles stock light code out.</summary>
    internal static string VanillaEnvDefs => ShaderSource.Get("cx_vanenv_defs.glsl");

    /// <summary>What a hosted stage gets in place of <c>inc_standard</c>: the two calls every stock fragment
    /// stage wraps its work in are Alloy's -- the normal is final before the body runs and the lighting after
    /// it is Alloy's. The setup call is NOT empty: stock's <c>SetupStandardShaderInputs</c> multiplies the
    /// texel in (<c>FragmentColor *= MainTextureColor</c>), so an empty stand-in hands the body WHITE.</summary>
    internal static string VanillaStandardFs => ShaderSource.Get("cx_vanstd_f.glsl");

    /// <summary>What a CONTENT stage gets in place of <c>inc_standard</c> (<see cref="ContentStage"/>): the hosted
    /// stand-in plus the stock stage's own coverage cut, which a content program makes instead of Alloy's.</summary>
    internal static string ContentStandardFs => ShaderSource.Get("cx_constd_f.glsl");

    /// <summary>The environment a content stage compiles in: the engine prologue, its vocabulary mapped onto Alloy's
    /// values, and the stock helpers (<see cref="ContentStage"/>).</summary>
    internal static string ContentEnvFs => ShaderSource.Get("cx_content_f.glsl");

    /// <summary>What a content VERTEX half gets in place of <c>inc_standard</c>: the stock transform and the per-vertex
    /// terms the fragment side reads, over the vertex environment's helpers.</summary>
    internal static string ContentStandardVs => ShaderSource.Get("cx_constd_v.glsl");

    /// <summary>The environment a content vertex half compiles in, inside the lit vertex stage's host region.</summary>
    internal static string ContentEnvVs => ShaderSource.Get("cx_content_v.glsl");

    /// <summary>What a stage Alloy runs WHOLE gets in place of <c>inc_standard</c> (<c>// $VANILLA-MAIN</c>).
    /// No Alloy shading surrounds it, so both calls carry their stock body: setup takes the display-space
    /// texel into working space and multiplies it in, apply reverts to display space. Its own <c>inc_noir</c>
    /// comes with it, because a stock stage calls <c>ApplyNoirDesat</c> directly.</summary>
    internal static string VanillaWholeFs => ShaderSource.Get("cx_vanwhole_f.glsl");

    /// <summary>The declarations half of the environment, after <see cref="VanillaEnvDefs"/>. The macro
    /// half is separate because a hosted stage re-issues it after scoping out its own defines.</summary>
    internal static string VanillaEnvFs => ShaderSource.Get("cx_vanenv_f.glsl");

    /// <summary>The CONTENT HOOK: a function the lit stage calls and does not implement, so that display-space
    /// shading a content repo invented lives in that repo and not in this one. The file is
    /// <c>cx_lit_hooks</c>, composed by the content out of its own includes; the signature below is the whole
    /// contract and names no feature. It runs after fog, in DISPLAY space, and no-ops where content ships none.</summary>
    internal static string HookFs => ShaderSource.Get("cx_hook_f.glsl");

    /// <summary>The SPELL-TARGETING SHAPE -- the circle, cone or rectangle a caster sees on the ground while
    /// picking a target. Vanilla draws it as arithmetic in <c>inc_standard.shd</c>'s tail, not as a draw call,
    /// so Alloy carries its own analytic SDFs (<see cref="TargetShape"/> is the same geometry in C#, where a
    /// self-check can state it). The uniform NAMES stay the engine's, because the values are.</summary>
    internal static string TargetFs => ShaderSource.Get("cx_target.glsl");

    /// <summary>The blended class's output stage, shared by every program the class draws through. <c>uOit</c>
    /// 0 = a composited colour; 1 = the weighted-blended accumulation pair (<see cref="OitPass"/>); 2 = the
    /// pair AND a (colour, depth) node appended to the pixel's linked list. The list's depth test is done HERE
    /// against the opaque depth: an SSBO append is a side effect the GL may run before its own depth test.</summary>
    internal static string OitOutFs => ShaderSource.Get("cx_oitout.glsl");

    /// <summary>The one clip-space expression EVERY Alloy vertex stage computes, character for character. The
    /// prepass writes the depth the colour pass tests LEQUAL against, and <c>invariant gl_Position</c>
    /// guarantees a bit-identical result only for the same sequence of operations: a different association,
    /// or an override that drops the qualifier, punches ULP-sized holes in lit surfaces.</summary>
    internal const string ClipExpr = "gl_Position = uVP * d.model * vec4(aPos, 1.0);";

    internal static string OwnedVs => ShaderSource.Get("cx_owned_v.glsl");
    /// <summary>The unlit variant: the engine draws these parts with lighting disabled (MatStatic's flat-white
    /// branch -- cap 5 off, or a <c>mat+0x98</c> material), so flat texture IS their vanilla appearance.</summary>
    internal static string OwnedFs => ShaderSource.Get("cx_owned_f.glsl");

    /// <summary>The depth-only stage. Deliberately has NO resref: a stage that writes nothing but depth has
    /// nothing an author could restyle, and an override is one more copy of <see cref="ClipExpr"/> to keep
    /// bit-identical. The fragment stage is empty rather than absent -- colour writes are masked off, and a
    /// program with no fragment stage at all is a portability gamble for nothing.</summary>
    internal static string DepthVs => ShaderSource.Get("cx_depth_v.glsl");
    internal static string DepthFs => ShaderSource.Get("cx_depth_f.glsl");

    /// <summary>The feature prepass's fragment stage: the cutout test the colour stages make, and nothing
    /// else. Same texel (unit 0, the batch's diffuse, the lit stage's rotated <c>vUV</c>), same threshold
    /// (<c>AlloyClasses.Discard</c>), same env exemption (an env-mapped material's alpha is reflectivity, not
    /// coverage). A negative cut -- an opaque displaced batch -- tests nothing.</summary>
    internal static string DepthFeatFs => ShaderSource.Get("cx_depthfeat_f.glsl");

    /// <summary>The depth stage for a batch the camera dissolves: it runs the SAME discard the colour pass
    /// will, so the prepass writes exactly the holes the colour pass punches. (Leaving those batches out of
    /// the prepass costs most of its overdraw win -- "can dissolve" is a near-static property of a third of
    /// the set indoors.) No resref: an aperture that drifted by a constant would cull the room behind a wall.</summary>
    internal static string DepthKeyVs => ShaderSource.Get("cx_depthkey_v.glsl");
    internal static string DepthKeyFs => ShaderSource.Get("cx_depthkey_f.glsl");

    /// <summary>The lit variant's vertex stage. Beyond the fullbright one it carries world position, world
    /// normal and the view depth: <c>gl_Position.w</c> IS the linear view depth under a perspective
    /// projection, so the fragment stage finds its froxel slice without a second matrix.</summary>
    internal static string OwnedLitVs => ShaderSource.Get("cx_ownlit_v.glsl");

    /// <summary>The lit variant's fragment stage: the engine's area ambient + directional diffuse, the local
    /// lights, then the material terms -- <c>inc_lighting.shd</c>'s <c>ComputeLighting</c> combine order,
    /// multiplied onto the LINEARIZED texel. The sun is attenuated by <see cref="AlloyLights.SunShadowLib"/>'s
    /// cascade lookup; ambient deliberately is not. ponytail: no synthesis stock does not already do.</summary>
    internal static string OwnedLitFsHead => ShaderSource.Get("cx_ownlit_head.glsl");
    /// <summary>Material synthesis -- what a part gets for every stock .mtr parameter it does NOT declare. A
    /// pure function of already-sampled values, so <c>check-shaders.py</c> folds it to constants off-device.
    /// The reference is the current <c>inc_material.shd</c>'s <c>SetupSpecularity</c> (ovr.bif, the set the
    /// client runs), at the high quality setting the Alloy lobe matches.</summary>
    internal static string MaterialLib => ShaderSource.Get("cx_material.glsl");

    /// <summary>The lit main, with the weather splices folded in at their two seams: the surface rewrite
    /// between material synthesis and the lighting, and the wet reflection after the probe term.</summary>
    internal static string OwnedLitFsMain => ShaderSource.Get("cx_ownlit_main.glsl");
}
