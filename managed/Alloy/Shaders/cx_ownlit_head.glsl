#version 460 core
#define CX_FS 1   // #642: gl_FragCoord is in reach for the shadow lib's dither
#define CX_LIT_MATERIAL 1   // ADR 0026: the light loop folds the material's own lobe in (CxLoopSpec)
#define CX_SUN_SOFT 1   // CX-49: the LIT surface is the one sun receiver that takes the blocker search + disc
#define CX_SKY_VIEW 1   // CX-290: ...and the one that scales the area ambient by the sky its point can see
in vec2 vUV;
in vec3 vWorld;
in vec3 vNrm;
in vec4 vTan;                  // CX-221: the supplied world tangent, handedness in w (0 = none supplied)
in float vDepth;
in vec2 vChart;                // #674: chart UV in atlas texels
flat in int vDraw;             // this fragment's CxDraw record: the material colours live there now
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_keyfogdecl.glsl"
#include "cx_chartbuf.glsl"
#include "cx_plt.glsl"
#include "cx_alphamip.glsl"
#include "cx_conbuf.glsl"
uniform sampler2D uTex;
#define CX_ARRIVE_SECONDS 0.6   // CX-52: a streamed diffuse's fade-in, see the diffuse sample
#define CX_ARRIVE_MIPS 6.0      // ...starting this many mips blurred
uniform sampler2D uFlowTex;    // #123 slot 6 (texUnit6), only ever bound on a content pair's part
// #120: the five stock .mtr slots beyond the diffuse, on units 3..7 (1 and 2 are the shadow maps).
// Sampled only where the record's slot mask says the material actually bound one.
uniform sampler2D uNrmTex;     // slot 1: tangent-space normal, xy in rg
uniform sampler2D uSpecTex;    // slot 2: specularity in r
uniform sampler2D uRoughTex;   // slot 3: roughness in r
uniform sampler2D uHeightTex;  // slot 4: height, marched for parallax and read for surface occlusion
uniform sampler2D uIllumTex;   // slot 5: self-illumination
uniform sampler2D uAoTex;      // #146 slot 7: baked ambient occlusion in r
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
#include "cx_skyview.glsl"
uniform int uViz;              // /alloy viz, see OwnedLitFsMain
// #121: the per-area IBL probe, holding LINEAR radiance (EnvProbe captures with uCapture = 1, which
// is what skips the display-space encode below). uProbeMips 0 = nothing captured yet, so no ambient
// specular at all -- the pre-#121 behaviour, never a half-written cube.
uniform samplerCube uProbe;
// uProbeMips / uProbeGain / uSceneEnv ride the pass block (cx_pass).
uniform int uCapture;          // 1 = this draw IS the probe capture: stay linear, never sample uProbe; 2 = the planar reflection's mirrored draw
uniform float uMirrorZ;        // uCapture 2: the mirror plane's world z -- what lies under it would mirror up over the reflection
uniform sampler2D uSsao;       // #379: this frame's screen-space visibility (SsaoPass), at gl_FragCoord
uniform int uSsaoOn;           // >0 when this BATCH is in the depth SsaoPass read; <0 names why not (viz 35)
uniform int uSsaoHalf;         // post.halfres: 1 = uSsao is half the frame's extent, read bilaterally
uniform sampler2D uSceneDepth; // the prepass depth SsaoPass read, to check the pixel is OURS (viz 37)
uniform float uAoDirect;       // /ao direct: how much of the occlusion the DIRECT terms take (0 = ADR 0021)
uniform float uAoCutout;       // /ao cutout: how much of it a CUTOUT receiver takes (leaf cards over leaf cards)
// #126: the engine's fAlphaDiscardValue, per batch -- 0.2 on the cutout and blended classes,
// -1.0 (no test) on opaque and additive.
uniform float uAlphaCut;
// 1 = a split wash's (AlloyRenderer.WashSplit) solid interior alone, drawn with the cutouts -- the blended pass draws
// the wash under GL_LESS against the depth that leaves
uniform int uSolid;
const float CX_SOLID_ALPHA = 0.99;
layout(location = 0) out vec4 frag;
#if defined(CX_DEPTH_ONLY) && !defined(CX_CAST)
// a content pair's depth twin doubles as its outline-highlight mask: location 0 takes the root's colour
// there, and nothing in the prepass, whose draw buffer 0 is NONE or masked off
uniform vec3 uHlColor;
#endif
#include "cx_ssr.glsl"
#include "cx_reflrec.glsl"
// CX-188 (ADR 0029): the deferred G-buffer's lanes. CX_GBUF_OUT -- which only the CX_DEFERRED twin is built
// with -- is what turns its two outputs on at locations 4..5; without it this is the reader's half alone.
#include "cx_gbuf.glsl"
// CX-188: the deferred pass's light record, read by a forward fragment lying on a deferred surface (blended only)
layout(binding = 26) uniform usampler2D uLightRec;
uniform int uLightRecOn;
// CX-273: the deferred decal layer (DecalLayer). Before the opaque pass a ground decal over-blends its albedo,
// coverage-weighted, into uDecalLayer (alpha = the share of the surface left showing); uDecalSurf keeps the prepass
// depth it was judged on (.r) and the nearest decal's view depth (.g). Every surface lying under that decal -- the
// prepassed ground, a cutout drawn over it, a wash's interior -- folds the layer into its albedo and is lit once.
uniform int uDecal;      // 1 = fold the layer into this surface; 2 = a decal batch whose on-surface fragments the layer took
uniform vec2 uDecalZ;    // the folded projection's (M33, M43): linear view depth = y / (depth + x)
layout(rgba16f, binding = 6) readonly uniform image2D uDecalLayer;
layout(rg32f, binding = 7) readonly uniform image2D uDecalSurf;
/// How high above a surface a decal may lie and still fold into it: metres, plus a share of view depth for the
/// depth buffer's own precision.
const float CX_DECAL_TOL_M = 0.1, CX_DECAL_TOL = 0.001;
/// Does a decal at linear view depth <paramref name="d"/> lie on the surface whose window depth is <paramref name="z"/>?
/// As HEIGHT off that surface (a grazing ray stretches depth without bound), one-sided: a surface over the decal (a foot
/// on it) never takes it. The writer and the blended pass ask it of the same inputs, so one of them takes each decal
/// fragment; a surface folding the layer asks it of the nearest decal.
bool CxDecalOn(float z, float d)
{
    if (!(z > 0.0 && z < 1.0)) return false;
    float s = uDecalZ.y / (z + uDecalZ.x);
    float h = (s - d) / max(d, 1e-4) * abs(dot(vWorld - uEyePos, normalize(vNrm)));
    float eps = CX_DECAL_TOL * s;
    return h > -eps && h < CX_DECAL_TOL_M + eps;
}
#include "cx_oitout.glsl"
// Slot mask bits, matching AlloyRenderer's 1 << (slot - 1).
const int CX_SLOT_NORMAL = 1, CX_SLOT_SPEC = 2, CX_SLOT_ROUGH = 4,
          CX_SLOT_HEIGHT = 8, CX_SLOT_ILLUM = 16;
// #146: bit 6, texture slot 7 -- the AO map. It multiplies the SAME occlusion factor the height
// map's local term feeds, so a material carrying both gets one combined ambient scale.
const int CX_SLOT_AO = 128;
// bit 8: no shadow lands on this record (an untextured blended sheet: a door's transition sheen)
const int CX_SLOT_NOSHADOW = 256;
// bits 9..12, .mtr texture7..texture10 -- set only on a content pair's material, the stages that can name them, which
// is the same reading the flow slot's bit takes. CX_SLOT_PAL0 is the first, declared in cx_conbuf.glsl because that
// library is included ahead of this block.
const int CX_SLOT_PAL_LAST = CX_SLOT_PAL0 << 3;
/// the depth encode's own rounding, nothing more: #119 makes a prepassed fragment's depth EQUAL
const float CX_AO_DEPTH_EPS = 1e-5;
/// The half-res upsample's depth tolerance, in fragment depth SLOPES: a neighbour more than this
/// many pixels' worth of the surface's own slope away is another surface, not this one.
const float CX_AO_UP_SLOPES = 8.0;
// Synthesis floors for a material that declares nothing, off the CURRENT inc_material.shd (ovr.bif, the set
// the client runs): fGeneratedSpecularityMin 0.04 (:68), fGeneratedRoughnessMin 0.125 under the GGX
// distribution (:46-50, the Alloy lobe's), fGeneratedRoughnessMax 0.55 in both branches (:40-44).
const float CX_SPEC_MIN = 0.04, CX_ROUGH_MAX = 0.55, CX_ROUGH_MIN = 0.125;
// metallicness, :216-227. With a specular map: m = specularity^2, then G*m / (1 + (G-1)*m) with G 25. Without
// one: fMetallicnessModifier 10 * specularity - 10 * fGeneratedSpecularityMin.
const float CX_METAL_GAIN_MAP = 25.0, CX_METAL_SCALE_BARE = 10.0, CX_METAL_BIAS_BARE = 0.4;
// ApplyColorSpace(0.5) at the engine's 2.2 gamma -- the mid-grey inc_material.shd divides the
// albedo's peak channel by when it synthesises a specular colour.
const float CX_MID_GREY = 0.2176;
// #121: bit 5 of the same slot mask -- the material declares an env map (Material::HasEnvMap, i.e.
// mat+0x40). NO texture is bound for it: the probe replaces what it held. What survives is the
// engine's own "this is shiny" flag on 2002 content, which drives the synthesis below.
const int CX_SLOT_ENV = 64;
// #123: bit 5, texture slot 6 -- the only slot outside the stock five, which a content pair's stage reads as
// texUnit6. Enrolled only on such a part, so on everything else this bit is clear and uFlowTex is never sampled.
const int CX_SLOT_FLOW = 32;
// inc_material.shd:141-142 -- an env-mapped material with no specular map takes its specularity from
// the diffuse alpha (fEnvMapLevel = 1 - alpha), and its roughness down toward the floor with it.
// TWO modifiers, not one: fLegacyEnvMapSpecularityModifier 8.0 and fLegacyEnvMapRoughnessModifier
// 2.5 (:55-57). Sharing 8.0 would saturate the roughness curve at envLevel 0.125 and render every
// legacy shiny surface at the floor.
const float CX_ENV_SPEC_MAX = 0.98, CX_ENV_SPEC_SCALE = 8.0, CX_ENV_ROUGH_SCALE = 2.5;
// inc_material.shd:301's OCCLUSION_VICINITT_RANGE: the height-occlusion tap's mip bias at a view depth of 1.
const float CX_OCCL_VICINITY = 7.0;
// inc_displacement.shd:30's fDisplacementMaximumIterations at SHADER_QUALITY_MODE > 1: the parallax march's cap.
const float CX_DISPLACE_STEPS = 32.0;
// inc_standard.shd:250-252: the albedo is desaturated toward this grey in proportion to reflectivity,
// so the reflection is not counted twice. DISPLAY space -- the engine does it before the :253 linearise.
const vec3 CX_ENV_GREY = vec3(0.666667);
// inc_lighting.shd:586-588: CX_ENV_GREY linearised, the env map's mean level -- vanilla's own constant where no env
// texture is bound. Scales the lit legacy reflection (cx_ownlit_tail).
const vec3 CX_ENV_GREY_LIN = vec3(0.4098);
// the ceiling on the probe pattern over its mean: a torch in an otherwise black night probe is a spike, not a mirror
const float CX_ENV_PAT_MAX = 4.0;

// #379: the visibility at this pixel. At full resolution that is one fetch. Under post.halfres the
// pass ran at half the frame's extent, and the four surrounding half-res texels are weighted by the
// BILINEAR share times how closely the depth each was computed from agrees with this fragment's --
// a plain bilinear upsample drags a wall's occlusion out over the floor in front of it. The
// tolerance is the fragment's own depth slope (a steep surface accepts what a flat one rejects), and
// where no tap agrees at all the nearest one is taken whole rather than a blend of four wrong ones.
float CxSsaoAt(ivec2 pix, float myDepth, float slope)
{
    if (uSsaoHalf == 0) return texelFetch(uSsao, pix, 0).r;
    ivec2 sz = textureSize(uSsao, 0), dsz = textureSize(uSceneDepth, 0);
    vec2 hp = (vec2(pix) + 0.5) * 0.5 - 0.5;
    ivec2 b = ivec2(floor(hp));
    vec2 f = hp - vec2(b);
    float tol = max(slope * CX_AO_UP_SLOPES, CX_AO_DEPTH_EPS);
    float sum = 0.0, wsum = 0.0, near = 1.0, best = 1e30;
    for (int i = 0; i < 4; i++)
    {
        ivec2 o = ivec2(i & 1, i >> 1);
        ivec2 p = clamp(b + o, ivec2(0), sz - 1);
        float v = texelFetch(uSsao, p, 0).r;
        // the full-res texel the half-res pass sampled its depth at: uv (p + 0.5) / (size / 2)
        float dz = abs(texelFetch(uSceneDepth, clamp(p * 2 + 1, ivec2(0), dsz - 1), 0).r - myDepth);
        if (dz < best) { best = dz; near = v; }
        float w = (o.x == 0 ? 1.0 - f.x : f.x) * (o.y == 0 ? 1.0 - f.y : f.y) * max(1.0 - dz / tol, 0.0);
        sum += v * w; wsum += w;
    }
    return wsum > 0.0 ? sum / wsum : near;
}
