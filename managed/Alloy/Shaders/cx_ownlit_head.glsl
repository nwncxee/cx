#version 460 core
#define CX_FS 1   // #642: gl_FragCoord is in reach for the shadow lib's dither
#define CX_LIT_MATERIAL 1   // ADR 0026: the light loop folds the material's own lobe in (CxLoopSpec)
#define CX_SUN_SOFT 1   // CX-49: the LIT surface is the one sun receiver that takes the blocker search + disc
in vec2 vUV;
in vec3 vWorld;
in vec3 vNrm;
in vec4 vTan;                  // CX-221: the supplied world tangent, handedness in w (0 = none supplied)
in float vDepth;
in float vAnchorZ;             // #123: undisplaced world height, for the roof anchor discard
in vec2 vChart;                // #674: chart UV in atlas texels
flat in int vDraw;             // this fragment's CxDraw record: the material colours live there now
#include "cx_drawbuf.glsl"
#include "cx_featdecl.glsl"
#include "cx_keyfogdecl.glsl"
#include "cx_chartbuf.glsl"
#include "cx_plt.glsl"
#include "cx_alphamip.glsl"
#include "cx_tintbuf.glsl"
uniform sampler2D uTex;
#define CX_ARRIVE_SECONDS 0.6   // CX-52: a streamed diffuse's fade-in, see the diffuse sample
#define CX_ARRIVE_MIPS 6.0      // ...starting this many mips blurred
uniform sampler2D uFlowTex;    // #123 slot 6: RG flow direction, only ever bound on a flowmap part
// #120: the five stock .mtr slots beyond the diffuse, on units 3..7 (1 and 2 are the shadow maps).
// Sampled only where the record's slot mask says the material actually bound one.
uniform sampler2D uNrmTex;     // slot 1: tangent-space normal, xy in rg
uniform sampler2D uSpecTex;    // slot 2: specularity in r
uniform sampler2D uRoughTex;   // slot 3: roughness in r
uniform sampler2D uHeightTex;  // slot 4: height, read for surface occlusion
uniform sampler2D uIllumTex;   // slot 5: self-illumination
uniform sampler2D uAoTex;      // #146 slot 7: baked ambient occlusion in r
// CX-43: uAreaAmbient / uAreaDiffuse / uAreaDir ride the pass block.
#include "cx_pass.glsl"
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
// A split wash (AlloyRenderer.WashSplit): 1 = its solid interior alone, drawn with the cutouts -- the blended pass
// draws the wash under GL_LESS against the depth that leaves; 2 = all but that interior (the decal layer's writer)
uniform int uSolid;
const float CX_SOLID_ALPHA = 0.99;
layout(location = 0) out vec4 frag;
#ifdef CX_A2C_MASK
// CX-189: the coverage layer's own mask. Hardware alpha-to-coverage gives equal alpha one sample pattern,
// so a second hair card at the same alpha lands on the same samples and vanishes; rotating the pattern by a
// hash of the primitive keeps the count and moves the samples. Spatial only: it must hold with TAA off.
uniform int uA2cSamples;
int CxA2cMask(float a)
{
    int n = clamp(uA2cSamples, 1, 8);
    int k = clamp(int(a * float(n) + 0.5), 0, n);
    uint h = uint(vDraw) * 0x9E3779B9u ^ uint(gl_PrimitiveID) * 0x85EBCA6Bu;
    h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
    int r = int(h % uint(n));
    int full = (1 << n) - 1, m = (1 << k) - 1;
    return ((m << r) | (m >> (n - r))) & full;
}
#endif
#include "cx_ssr.glsl"
#include "cx_reflrec.glsl"
// CX-188 (ADR 0029): the deferred G-buffer's lanes. CX_GBUF_OUT -- which only the CX_DEFERRED twin is built
// with -- is what turns its four outputs on at locations 4..7; without it this is the reader's half alone.
#include "cx_gbuf.glsl"
// CX-188: the deferred pass's light record, read by a forward fragment lying on a deferred surface (blended only)
layout(binding = 26) uniform usampler2D uLightRec;
uniform int uLightRecOn;
// CX-273: the deferred decal layer (DecalLayer). Before the opaque pass a ground decal over-blends its albedo,
// coverage-weighted, into uDecalLayer (alpha = the share of the surface left showing) and stamps the prepass depth
// it lies on into uDecalSurf; the prepassed surface folds the layer into its own albedo and is lit once, as one.
uniform int uDecal;      // 1 = fold the layer into this surface; 2 = a decal batch whose on-surface fragments the layer took
uniform vec2 uDecalZ;    // the projection's (M33, M43): linear view depth = y / (ndc z + x)
layout(rgba16f, binding = 6) readonly uniform image2D uDecalLayer;
layout(r32f, binding = 7) readonly uniform image2D uDecalSurf;
/// How high above the prepassed surface a decal may lie and still fold into it: metres, plus a share of view
/// depth for the depth buffer's own precision.
const float CX_DECAL_TOL_M = 0.1, CX_DECAL_TOL = 0.001;
/// Does a decal fragment at linear view depth <paramref name="d"/> lie on the prepassed surface whose window depth
/// is <paramref name="z"/>? Measured as HEIGHT off that surface, not as depth along the ray, which a grazing view
/// stretches without bound, and one-sided: a surface standing over the decal (a foot on it) never takes it. The
/// layer's writer and the blended pass ask this of the same inputs, so each decal fragment is taken by one of them.
bool CxDecalOn(float z, float d)
{
    if (!(z > 0.0 && z < 1.0)) return false;
    float s = uDecalZ.y / ((z * 2.0 - 1.0) + uDecalZ.x);
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
// CX-223: bits 9..12, .mtr texture7..texture10 -- the palette tint's own maps. Set only on a material that
// names the palette tint, which is the same reading the flow slot's bit takes. CX_SLOT_PAL0 is the first,
// declared in cx_tintbuf.glsl because that library is included ahead of this block.
const int CX_SLOT_PAL_LAST = CX_SLOT_PAL0 << 3;
/// the depth encode's own rounding, nothing more: #119 makes a prepassed fragment's depth EQUAL
const float CX_AO_DEPTH_EPS = 1e-5;
/// The half-res upsample's depth tolerance, in fragment depth SLOPES: a neighbour more than this
/// many pixels' worth of the surface's own slope away is another surface, not this one.
const float CX_AO_UP_SLOPES = 8.0;
// Synthesis floors for a material that declares nothing, read straight off the OLD-generation
// inc_material.shd the installed client actually runs (#107 §11): fSpecularityMin 0.04 (the old gen's
// name -- the split-include fork calls it fGeneratedSpecularityMin), fGeneratedRoughnessMin 0.125
// unconditionally, fGeneratedRoughnessMax 0.6 at SHADER_QUALITY_MODE > 1 (0.65 below it, and the
// Alloy lobe is GGX + Schlick geometric shadowing unconditionally, which IS that setting), and the
// specular-map metallicness curve (fMetallicnessModifier 3.0 * fGeneratedMetallicnessOffset 0.2 = 0.6).
const float CX_SPEC_MIN = 0.04, CX_ROUGH_MAX = 0.6, CX_ROUGH_MIN = 0.125;
// metallicness curve, both of the old gen's branches: scale * specularity - scale * offset, where the
// offset is 0.2 with a specular map and fGeneratedSpecularityMin without one.
const float CX_METAL_SCALE_MAP = 3.0, CX_METAL_BIAS_MAP = 0.6,
            CX_METAL_SCALE_BARE = 10.0, CX_METAL_BIAS_BARE = 0.4;
// ApplyColorSpace(0.5) at the engine's 2.2 gamma -- the mid-grey inc_material.shd divides the
// albedo's peak channel by when it synthesises a specular colour.
const float CX_MID_GREY = 0.2176;
// #121: bit 5 of the same slot mask -- the material declares an env map (Material::HasEnvMap, i.e.
// mat+0x40). NO texture is bound for it: the probe replaces what it held. What survives is the
// engine's own "this is shiny" flag on 2002 content, which drives the synthesis below.
const int CX_SLOT_ENV = 64;
// CX-218's CX_SLOT_COVERNRM (bit 13, the coverage source) is declared in cx_alphamip.glsl, beside the
// cutout test it steers and where every stage that makes that test can see it.
// #123: bit 5, texture slot 6 -- the flow map, and the only slot outside the stock five. Enrolled
// only on a flowmap part, so on everything else this bit is clear and uFlowTex is never sampled.
const int CX_SLOT_FLOW = 32;
// inc_material.shd:143-144 -- an env-mapped material with no specular map takes its specularity from
// the diffuse alpha (fEnvMapLevel = 1 - alpha), and its roughness down toward the floor with it.
// TWO modifiers, not one: fLegacyEnvMapSpecularityModifier 8.0 and fLegacyEnvMapRoughnessModifier
// 3.0 (:60-62). Sharing 8.0 would saturate the roughness curve at envLevel 0.125 and render every
// legacy shiny surface at the floor. 3.0 is the OLD generation's value, which is the one this client
// runs (#107 s11); the newer split-include generation says 2.5.
const float CX_ENV_SPEC_MAX = 0.98, CX_ENV_SPEC_SCALE = 8.0, CX_ENV_ROUGH_SCALE = 3.0;
// inc_standard.shd:250-252: the albedo is desaturated toward this grey in proportion to reflectivity,
// so the reflection is not counted twice. DISPLAY space -- the engine does it before the :253 linearise.
const vec3 CX_ENV_GREY = vec3(0.666667);
// inc_lighting.shd:504: CX_ENV_GREY linearised, the env map's mean level -- vanilla's own constant where no env
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
