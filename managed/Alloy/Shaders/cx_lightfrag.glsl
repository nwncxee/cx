#include "cx_pass.glsl"
layout(std430, binding = 8) readonly buffer CxLightBuf { vec4 uLights[]; };
// THREE words per cluster, two 16-bit counts each, over the list cx_lightbuild lays out as
// [0, liveCand) [.., live) [.., live + refl) [.., coldCand) [.., kept) [.., n): word 0 = n | live, word 1 =
// refl | liveCand, word 2 = coldCand | kept. live leads the cold statics; refl is R4's water-film picks (0 on
// every frame with no film); kept ends what light.relevance can leave lit at an on-screen fragment, and the
// two candidate runs are the only lights that can be a fragment's brightest. Read through the CxCluster*
// accessors, never raw.
layout(std430, binding = 9) readonly buffer CxClusterCount { uint uClCount[]; };
layout(std430, binding = 10) readonly buffer CxClusterIdx { uint uClIdx[]; };
// CasterPass.CubeDynSphere: CX_DYN_SPHERES spheres (xyz, r; r < 0 unused) per cube slot round the movers
// its dyn page holds -- a hot light owes nothing outside their shadow cones
layout(std430, binding = 11) readonly buffer CxDynMovers { vec4 uDynMovers[]; };
const int CX_DYN_SPHERES = $DYNSPH$;
// CX-43: the cluster terms (uGrid, uTile, uZ) and the curve (uFalloff, uAtten, uHot, uReach, uWrap,
// uWrapN, uSss) ride the pass block; cx_pass.glsl says what each lane is.
// Mirrored lights (light.hidden): x = on, y = the roughness ceiling a surface counts as glossy under.
uniform vec2 uLightOcc;
bool CxGlossy(float rough) { return uLightOcc.x > 0.0 && rough < uLightOcc.y; }
// 0 = no local-light shadow lands on this fragment (CX_SLOT_NOSHADOW); the lit stage clears it per record.
float cxRecvShadow = 1.0;
// /alloy viz 51: hot lights this fragment ray-tested (x) and the ones whose ray hit a mover (y)
vec2 cxHotViz = vec2(0.0);
// CX-188 budget (light.fullmodel): this light's LIGHTING LOD -- 1 = the full model, 0 = the engine's own curve
// and nothing else. It scales the three refinements that are shaped by a light you are standing in rather than
// by the fifth lamp down the street: the near-field core (#142), the sphere-sized specular lobe (#139's
// spread) and the subsurface leak (#375). The cluster walk sets it per light from the light's own share of the
// pixel's brightest; every other caller of this library -- the bake, the shadow record, cx_material's film,
// the pair debt -- leaves it 1, which is the pre-LOD body term for term.
float cxLod = 1.0;
// the share of a baked light's highlight the mirror lobes carry, the pages' one lobe the rest: 0 at the ceiling,
// 1 at half of it. A cut there swapped the baked shadow for one cube tap wherever rain crossed the ceiling.
float CxGlossW(float rough) { return uLightOcc.x > 0.0 ? 1.0 - smoothstep(0.5 * uLightOcc.y, uLightOcc.y, rough) : 0.0; }
// linear luminance under which a glossy lobe is not worth the fragment's one mirror tap
const float CX_MIRROR_FLOOR = 0.002;
const uint CX_CLUSTER_CAP = $CAP$u;
const int CX_CAP_MAX = 2;   // #336: the shadow cap's ceiling -- two NAMED candidates, no arrays
// The engine's own sRGB->linear conversion (Ghidra RenderInterface::GetColorSpaceFromSRGB: powf(c,
// 2.2) per channel, identity at 0 and 1). Textures and material colours both arrive non-linear --
// the stock shaders apply the same curve via ApplyColorSpace before lighting them.
// ponytail: unconditional. The engine gates it on GetGlobalShaderParami(0), the gamma pipeline the
// ship config runs and the shipped .shd assume (GAMMA_CORRECTION 1); plumb that param through if a
// legacy-pipeline build ever needs the Alloy path.
vec3 CxLinear(vec3 c) { return pow(max(c, vec3(0.0)), vec3($SRGB$)); }

// #143: energy-conserving wrapped diffuse. NWN's meshes are low-poly with smoothed vertex normals,
// so a punctual terminator lands mid-facet as a straight hard crease -- the artifact the community's
// GL 3.3 "light wrapping" shaders exist to hide. Widening the band by `w` and dividing the result by
// (1 + w) is the standard half-Lambert generalisation: the PEAK is unchanged, so a fully lit surface
// is bit-identical to the punctual one and only the band around the terminator moves.
// ponytail: one global wrap for every material. The modern refinement is pre-integrated skin
// (Penner 2011) -- a curvature-driven terminator LUT -- but it is per-material by nature and NWN
// carries no "this part is skin" tag; on armour and stone it reads wrong. Tag materials first.
float CxWrap(float ndl, float w)
{
    return w <= 0.0 ? clamp(ndl, 0.0, 1.0) : clamp((ndl + w) / (1.0 + w), 0.0, 1.0);
}

// #132: there is no encode here any more, and no highlight shoulder either. Every Alloy stage
// writes LINEAR into PostChain's own target and the resolve does the one encode for the whole frame
// -- one conversion per screen pixel instead of one per fragment, gated on the engine's own gamma
// parameter, and the only order in which blended compositing is correct. A stage that re-encoded
// here would be encoded twice. The pass stands down to vanilla outright on the frames PostChain
// cannot arm (see PostChain.Begin): a linear Alloy frame with no resolve is wrong by a gamma, and
// there is no display-space variant of these stages left to fall back to.

// Cluster of this fragment: screen tile from gl_FragCoord, depth slice from the interpolated view
// depth (gl_Position.w, which for a perspective projection IS the linear view depth -- no dependence
// on the projection's handedness convention). Must mirror the compute pass's slice mapping exactly.
// gl_FragCoord is WINDOW-relative, so the viewport origin comes out first -- the engine's reflection
// subframes do not all render at (0,0). A VERTEX stage composing this library (#336: particles light
// per vertex) has no gl_FragCoord and defines CX_CLUSTER_XY itself, in tile units, before the lib.
// Only a fragment stage is guaranteed to sit inside its froxel, which is what the build's bounds assume.
#ifndef CX_CLUSTER_XY
#define CX_CLUSTER_XY ((gl_FragCoord.xy - uTile.xy) / uTile.zw)
#define CX_CLUSTER_FRAG
#endif
uint CxClusterN(uint cl) { return min(uClCount[cl * 3u] & 0xffffu, CX_CLUSTER_CAP); }
uint CxClusterLive(uint cl) { return min(uClCount[cl * 3u] >> 16u, CX_CLUSTER_CAP); }
/// R4: the entries at [live, live + refl) -- the cold statics a water film mirrors. 0 = no film this frame.
uint CxClusterRefl(uint cl) { return min(uClCount[cl * 3u + 1u] & 0xffffu, CX_CLUSTER_CAP); }
/// light.relevance's floor candidates: [0, CxClusterLiveCand) and [CxClusterLive, CxClusterColdCand).
uint CxClusterLiveCand(uint cl) { return min(uClCount[cl * 3u + 1u] >> 16u, CX_CLUSTER_CAP); }
uint CxClusterColdCand(uint cl) { return min(uClCount[cl * 3u + 2u] & 0xffffu, CX_CLUSTER_CAP); }
/// How far this stage walks the whole list: the kept prefix for a fragment of the view the table was built for,
/// every entry for a vertex stage or a pass that reuses another view's table (the planar mirror).
uint CxClusterWalk(uint cl)
{
#ifdef CX_CLUSTER_FRAG
    if (uBounds > 0.5) return min(uClCount[cl * 3u + 2u] >> 16u, CX_CLUSTER_CAP);
#endif
    return CxClusterN(cl);
}
// CX-188: the walk's LIST SOURCE. Every count the walk reads and every index it fetches goes through these,
// so one body serves two sources: a fragment's own froxel from the table above, or the per-tile list the
// deferred compute pass culls for itself (cx_deferred_cs) over the same uLights. A stage that defines
// CX_LIGHT_LIST supplies the seven after this library and gets its own source; every other program leaves it
// undefined and reads the froxel table, term for term as before.
#ifdef CX_LIGHT_LIST
uint CxListN(uint cl);
uint CxListLive(uint cl);
uint CxListRefl(uint cl);
uint CxListLiveCand(uint cl);
uint CxListColdCand(uint cl);
uint CxListWalk(uint cl);
uint CxListIdx(uint cl, uint k);
#else
uint CxListN(uint cl) { return CxClusterN(cl); }
uint CxListLive(uint cl) { return CxClusterLive(cl); }
uint CxListRefl(uint cl) { return CxClusterRefl(cl); }
uint CxListLiveCand(uint cl) { return CxClusterLiveCand(cl); }
uint CxListColdCand(uint cl) { return CxClusterColdCand(cl); }
uint CxListWalk(uint cl) { return CxClusterWalk(cl); }
uint CxListIdx(uint cl, uint k) { return uClIdx[cl * CX_CLUSTER_CAP + k]; }
#endif
// CX-188 step 3: the walk's SHADOW SOURCE. A stage that has already evaluated this pixel's shadows once --
// cx_deferred_cs, off the per-pixel visibility record cx_defshadow_cs wrote -- defines CX_SHADOW_SOURCE and
// supplies this; it answers `have` false for a light the record does not hold. The visibility carries the
// BUDGET's own dissolve (cx_shrec's CxShFade: a lane near the budget's edge is already faded toward 1, which
// is what makes membership continuous) and nothing else -- not dfade, which the walk applies itself, so a
// record shared between pixels never carries one pixel's distance fade to another. The budget's fade is a
// function of position, so sharing it across a half-resolution block is the same bargain the visibility
// already is. Undefined -- every forward program -- compiles the per-fragment cap below, term for term.
#ifdef CX_SHADOW_SOURCE
float CxShadowFetch(int slot, out bool have);
/// CX-188 budget: 1 = this pixel has NO usable record, so the walk falls back to one hardware tap per shadowing
/// light -- the pre-budget path, for the slivers the record misses (counted, never silent). 0 = the record IS
/// the pixel's shadow budget and a light it does not hold is lit UNSHADOWED, which is what ends the hardware
/// tap for a pixel's fifth-and-later shadowed light. The source sets it per pixel before the walk.
float cxShMiss = 1.0;
#endif
/// CX-188 budget: how many lights may shadow one pixel, from <c>shadow.perpixel</c> (uShBudget) and capped at
/// <paramref name="ceiling"/>, the walk's own structural limit. 0 on the knob -- and <c>shadow.cap 0</c>, the
/// "every light, one hardware tap" escape -- means no budget, and the caller then reads back the ceiling.
int CxShadowBudget(int ceiling)
{
    return uShBudget > 0.0 ? int(min(uShBudget, float(ceiling))) : ceiling;
}
/// The SCORE every per-pixel shadow budget ranks by -- higher is stronger, and the forward cap, the deferred
/// record and the hot-light cap all order their candidates with this one function: a budget ranked one way and
/// filled another does not hold what it says.
/// <para>The quantity is the light's IRRADIANCE at the pixel -- the engine curve's <paramref name="att"/> times
/// the linear colour's luminance -- and it is deliberately nothing else. It is a function of POSITION alone, so
/// the contour that separates the lights keeping a shadow from the ones that do not is smooth in space and has
/// the scale of a light's falloff. N.L is NOT in it, nor the subsurface leak, nor the relevance weight: a
/// per-pixel SURFACE term would hang that contour off the normal map, and a budget whose membership follows
/// bump detail speckles. (The caller takes the score before <c>relW</c> scales the colour, for the same
/// reason.) The near-field core inside <c>att</c> carries the lighting LOD's weight when <c>light.hot</c> and
/// <c>light.fullmodel</c> are both on, and the record evaluates it with <c>cxLod</c> 1 where the walk does not;
/// the core has compact support inside <c>uHot.y</c> of the range, so the two scores can differ only for a
/// light the pixel is standing inside, which is the one both rank first anyway. That is the whole extent of
/// "one ranking" not being literally one number.</para>
/// <para>With the budget OFF the score is <c>-f2</c>, which orders candidates exactly as the pre-budget cap and
/// record did: negation is exact in IEEE, so `-f2 > -best` and `f2 < best` are the same comparison, ties
/// included. That is what makes every knob's off position bit-identical to the pre-budget frame.</para>
float CxLightRank(float att, vec3 cLin, float f2)
{
    return uShBudget > 0.0 ? att * dot(cLin, vec3(0.2126, 0.7152, 0.0722)) : -f2;
}
/// The score's own "nothing yet", below every value it can take (-f2 reaches -1 at a light's own reach).
const float CX_RANK_NONE = -1e30;
// CX-188 budget: the walk's own census -- x = lights that LIT this pixel, y = hardware shadow taps it still
// paid, z = list ENTRIES the main loop touched -- for the period line's lights-per-pixel, taps-per-pixel and
// entries-per-pixel. `x` counts lights that reached the pixel and faced it; `z` counts every entry the loop
// read, so z - x is what the reach window, the relevance cut and the facing test rejected -- the number the
// walk pays for and gets nothing back from. The relevance PRE-pass's own touches are not in z: it is a
// separate walk of the candidate runs, and the ablation ladder is what prices it. Only cx_deferred_cs defines
// CX_WALK_STATS; every other program compiles the walk with the three adds absent, not merely unexecuted.
#ifdef CX_WALK_STATS
vec3 cxWalkStats = vec3(0.0);
#define CX_WALK_LIT() cxWalkStats.x += 1.0
#define CX_WALK_TAP() cxWalkStats.y += 1.0
#define CX_WALK_ENTRY() cxWalkStats.z += 1.0
#else
#define CX_WALK_LIT()
#define CX_WALK_TAP()
#define CX_WALK_ENTRY()
#endif
// CX-188: `/alloy bench`'s ABLATION ladder. `CX_ABLATE n` is injected after the #version line by
// CX.Alloy.AblateProgram, for a bench step and nothing else, and it stops the walk after stage n while
// handing back a cheap function of what that stage produced -- so the compiler cannot strip the stages
// before it and (stage n - stage n-1) is that stage's own cost. 2 = the list source alone, 3 = + the
// relevance pre-pass, 4 = + the light bodies with no shadow of any kind, 5 = + the shadow record fetch,
// 6 = + cx_deferred_cs's transfer-weight combine, 7 = + its fill of a missed record, 8 = + the mirror tap;
// the shipped program adds the film's statics. Stage 1 never calls this function (cx_deferred_cs owns that rung). No shipped
// program defines it, so every one of them compiles this file exactly as it did before.
#ifdef CX_ABLATE
/// The sink's factor: small enough that the half-float target takes every ablated stage's contribution to
/// zero, so the ladder changes the timing and not the picture.
const float CX_ABL_EPS = 1e-30;
#endif
uint CxCluster(float viewDepth)
{
    vec2 tile = CX_CLUSTER_XY;
    int gx = clamp(int(tile.x), 0, uGrid.x - 1);
    int gy = clamp(int(tile.y), 0, uGrid.y - 1);
    float k = log(max(viewDepth, uZ.x) / uZ.x) / log(uZ.y / uZ.x);
    int gz = clamp(int(k * float(uGrid.z)), 0, uGrid.z - 1);
    return uint(gx + uGrid.x * (gy + uGrid.y * gz));
}

// fAngleMin (inc_lighting.shd:88): the floor the engine clamps N.V to before the view-side
// normalisation divides by it.
const float CX_ANGLE_MIN = 0.01;

// Schlick's Fresnel, the pow-5 form the ship quality mode takes (inc_lighting.shd GetFresnel,
// SHADER_QUALITY_MODE > 1; the lower modes use pow 4).
float CxFresnel(float c)
{
    float f = 1.0 - c;
    float sq = f * f;
    return clamp(f * sq * sq, 0.0, 1.0);
}

// light.brdf (ADR 0026): x = 1 runs the modern material model, 0 the engine's own lobe exactly. A program
// that never uploads it (water, grass, particles) reads 0 and keeps the engine's lobe.
// y = directional ambient strength, z = specular anti-aliasing on.
uniform vec3 uMaterial;

// The Schlick-Smith k. The engine takes k = alpha, which over-shadows a rough lobe at grazing angles;
// the modern model takes Karis' k = alpha / 2 (the Schlick fit to Smith-GGX). Both are separable into
// a light half and a view half, which is what keeps the per-light / once-per-fragment split below.
float CxSmithK(float rough) { return uMaterial.x > 0.5 ? rough * 0.5 : rough; }

#ifdef CX_LIT_MATERIAL
// ADR 0026: one light's full specular radiance per unit irradiance, from the engine lobe `lobe` -- the
// view-side normalisation, the specular colour and the clear-coat and sheen layers folded in. Defined
// by cx_material.glsl from the fragment's own material; only the lit stage defines CX_LIT_MATERIAL.
vec3 CxLoopSpec(float lobe, vec3 N, vec3 V, vec3 L, float spread);
#define CX_LOOP_SPEC(lobe, N, V, L, spread) CxLoopSpec(lobe, N, V, L, spread)
#else
#define CX_LOOP_SPEC(lobe, N, V, L, spread) vec3(lobe)
#endif

// Karis 2013 ("Real Shading in UE4", the sphere-light normalisation): a light is not a point. It is the
// SPHERE of radius uWrap x uFalloff.y that #139's diffuse wrap already models with the same number, and a
// specular lobe cannot be narrower than the image of the source it reflects. Alpha therefore widens by
// this at distance `dist` -- half the sine of the half-angle the sphere subtends -- and the lobe dims to
// match, so the peak is bounded by the source's real size instead of running to 1/alpha^2.
// <para>This is what made a lamp a hand's breadth off wet ground read as a blinding flare: a water film's
// alpha is 0.02-0.05, whose punctual peak is one to two thousand times the irradiance, and bloom then
// spread it over the screen. It changes dry glossy surfaces near lights too -- their highlights are
// broader, dimmer and bounded -- which is the same physical correction #139 already made to the diffuse
// terminator, and 0 on the wrap knob turns both back off together.</para>
float CxSrcSpread(float dist) { return uWrap <= 0.0 ? 0.0 : uWrap * uFalloff.y * 0.5 / max(dist, 1e-3); }

// #120, corrected by the live A/B on #121: the specular lobe every lit term goes through, as the
// ENGINE computes it (inc_lighting.shd:150-265 at the ship quality mode -- inc_config.shd's
// SPECULAR_DISTRIBUTION_MODEL 1 = GGX, SPECULAR_GEOMETRIC_SHADOWING 2 = Schlick, SPECULAR_FRESNEL 1
// = full). This replaced a textbook GGX, which differed in the one place that decides how a surface
// reads: it remapped alpha = rough^2, while the engine feeds fRoughness_sq = rough^2 as alpha^2 --
// alpha = rough. Every authored and synthesised roughness in the content is tuned for the engine's
// convention, so the remap shaded a 0.24-roughness stone as a 0.06 one: a far tighter, far brighter
// highlight on every part carrying a specular map, which is nearly every authored material.
// <para>Two halves, split the way the engine splits them: this per-LIGHT one, and the view-side
// normalisation in CxSpecBase, which is common to every light and applied once (the engine's
// fSpecularBaseModifier). fSpecularity rides in through the Fresnel term and must NOT be multiplied
// in again -- inc_lighting.shd:600-602 scales by it only when SPECULAR_FRESNEL != 1.</para>
// Returns the lobe per unit IRRADIANCE -- the caller has already folded N.L and attenuation into the
// light term it multiplies this by.
// ponytail: the low-quality branches (Blinn-Phong, no shadowing, the simplified Fresnel) are not
// reproduced. Add them if a device that cannot run the ship mode ever needs to match its own vanilla.
/// How much of the Fresnel rise every lobe takes: 1 is the engine's, 0 holds the reflectance at F0 at
/// every angle -- the lit main's choice for a bare material, whose grazing lobe otherwise outshines its diffuse.
float cxFresnel = 1.0;

/// `spread` is the sphere-light widening from <see cref="CxSrcSpread"/>; 0 is the punctual lobe, bit for
/// bit, which is what the sun and the lightmap's dominant direction pass.
float CxSpecLobe(vec3 N, vec3 V, vec3 L, float rough, float spec, float spread)
{
    vec3 H = normalize(V + L);
    float NdotH = clamp(dot(N, H), 0.0, 1.0);
    float VdotH = clamp(dot(H, V), 0.0, 1.0);
    float NdotL = clamp(dot(N, L), 0.0, 1.0);
    // GGX distribution minus its alpha^2 numerator, which lives in CxSpecBase -- and the sphere's WIDENED
    // alpha goes in the denominator only. That pair IS Karis' normalised lobe, alpha^2 over den(alpha')^2:
    // the (alpha/alpha')^2 energy factor is the numerator staying behind, with no second term to get
    // wrong. den grows with alpha, so the result can only ever go DOWN.
    float ap = min(rough + spread, 1.0);
    float den = 1.0 - NdotH * NdotH * (1.0 - ap * ap);
    float d = 1.0 / max(den * den, 1e-8);
    // ...and the microfacet shadowing keeps the SURFACE's own roughness: the source's size widens the
    // distribution, it does not make the surface rougher.
    d *= 1.0 / mix(NdotL, 1.0, CxSmithK(rough));   // geometric shadowing, light side
    return d * mix(spec, 1.0, CxFresnel(VdotH) * cxFresnel);
}
float CxSpecLobe(vec3 N, vec3 V, vec3 L, float rough, float spec) { return CxSpecLobe(N, V, L, rough, spec, 0.0); }

// The half that does not vary per light: alpha^2/4 and the view-side shadowing term.
float CxSpecBase(float NdotV, float rough)
{
    return rough * rough * 0.25 / mix(max(NdotV, CX_ANGLE_MIN), 1.0, CxSmithK(rough));
}

// CX-188 budget (light.floor): where a light stops being SEEN, as against where its authored radius ends. The
// engine's curve is RANGE-NORMALISED and reaches zero only at f2 = 1, so a lamp whose overbright surplus went
// into a 38 m radius is binned, walked and shadowed across all 38 m. uLightFloor is an ABSOLUTE display-linear
// brightness; this solves the light's own on-screen importance for the f2 at which it falls to that floor:
//     lum * (1 - f2) / (at.x + at.y * f2) = uLightFloor  ->  f2 = (lum - floor * at.x) / (lum + floor * at.y)
// with lum the master gain times the daylight dim times the LINEAR luma of the colour. Linear, not
// CxLightImportance's sRGB luma: that one is only ever read as a ratio, where the curve cancels, and an
// absolute floor has to be in the units the display shows. The near-field core (uHot) has compact support
// inside uHot.y of the range, a fraction of any cut, so it can only raise importance well inside the cut and
// never move it.
// <para>Radius-free by construction: the curve is normalised on the authored R, so the cut is a SHARE of every
// light's own reach and the world radius is R * sqrt(cut). That is also why the cut is carried as a window on
// f2 and the curve keeps the authored R -- shrinking the R the curve divides by would change the falloff's
// SHAPE, which is the look, not the budget.</para>
/// The f2 at which `cLin` (the light's LINEAR colour) drops under <c>light.floor</c>; 1e30 = no cut, which is
/// what a zero floor reads as, so the window below is then exactly the pre-floor one.
float CxReachCut(vec3 cLin)
{
    if (uLightFloor <= 0.0) return 1e30;
    vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
    float lum = uFalloff.x * uDayDim * dot(cLin, vec3(0.2126, 0.7152, 0.0722));
    return clamp((lum - uLightFloor * at.x) / max(lum + uLightFloor * at.y, 1e-8), 0.0, 1.0);
}

// light.relevance: how brightly light `li` could reach `worldPos` -- CxPointLight's curve and reach fade
// times the colour's luma, zero at and past the reach. Continuous in position, so a per-fragment cut on
// it has no edge to draw. Ratios only: the global gain and the rim exponent cancel out of them.
float CxLightImportance(uint li, vec3 worldPos)
{
    vec4 pr = uLights[li * 2u];
    vec3 d = pr.xyz - worldPos;
    float f2 = dot(d, d) / max(pr.w * pr.w, 1e-8);
    if (f2 >= 1.0) return 0.0;
    float fade = 1.0 - f2;
    if (uReach > 0.0)
    {
        float r2 = uReach * uReach;
        if (f2 > r2) return 0.0;
        fade *= min((r2 - f2) / (r2 * 0.3), 1.0);
    }
    vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
    return fade / (at.x + at.y * f2) * dot(uLights[li * 2u + 1u].rgb, vec3(0.299, 0.587, 0.114));
}

// #674: one light's colour, attenuation and geometry -- the per-light body, shared by the cluster loop
// below and the bake stage (LightmapBake.BakeFs), so a baked texel and a live one are ONE code path.
// False = out of reach (nothing to add). `c` is the linearised colour, `att` the engine's curve (#140,
// #142) already floored at 0, `f2` the range-normalised distance squared (the cap's ranking term),
// `ndlRaw` the geometric dot and `ndl` the wrapped one (#139 sphere wrap, #143 floor).
bool CxPointLight(vec4 pr, vec3 c0, vec3 worldPos, vec3 N,
                  out vec3 c, out vec3 L, out float att, out float f2, out float ndlRaw, out float ndl)
{
    c = vec3(0.0); L = vec3(0.0); att = 0.0; f2 = 0.0; ndlRaw = 0.0; ndl = 0.0;
    // pr.w is GetLightAdjustedRadius: the engine's own R, overbright surplus already spent
    // on it. Squared here because the engine's own cull is the same test on the same value
    // (SetLightGL @0x4c1007 uploads exactly R*R as lightQuadraticAtten).
    float rr = pr.w * pr.w;
    vec3 d = pr.xyz - worldPos;
    float dd = dot(d, d);
    if (dd > rr) return false;
    // The walk already divided an overbright colour down by its max channel (spending the
    // surplus on radius, as GetLightAdjustedColor does), so every channel is <= 1 here and the
    // plain sRGB curve is the right linearization.
    c = CxLinear(c0);
    // #140: the ENGINE's curve, range-normalised -- inc_lighting.shd's ComputePointLightSource
    // at GAMMA_CORRECTION == 1, verbatim. The authored range IS the shape here, which is the
    // whole reason a dim cave glow and a torch can share one formula: the glow's R is small and
    // its colour dim, the torch's R is large (its overbright surplus went there), and neither
    // needs a global gain to rescue it. The rim fade reaches exactly 0 at f2 = 1, so no separate
    // cutoff window is needed and a light still cannot pop against its own cull sphere.
    f2 = dd / rr;
    float fade = 1.0 - f2;
    // The walk's WINDOW on f2, whichever is tighter: light.binreach's share of every light's authored radius,
    // and light.floor's own per-light perceptual cut (CxReachCut). Both fade to exactly 0 at their edge over
    // the last 30% of the window, so neither can draw a ring -- the one mechanism, now with a per-light source
    // for the number. 1e30 from both (binreach 0 with reachfade off, floor 0) is the pre-window curve, term for
    // term: the floor's window is its own knob's, not /light reachfade's.
    float rw = min(uReach > 0.0 ? uReach * uReach : 1e30, CxReachCut(c));
    // a light whose own brightness is already under the floor has NO reach: out before the fade divides by it
    // (rw and f2 are both 0 at the light's own centre, and 0/0 is a NaN that would poison the whole pixel)
    if (rw <= 0.0) return false;
    if (rw < 1e29)
    {
        if (f2 > rw) return false;
        fade *= min((rw - f2) / (rw * 0.3), 1.0);   // to zero over the last 30% of the window (in f2)
    }
    // Uniform-valued branch, so it is coherent across the whole draw and free -- and at the
    // shipped 1.0 the pow is not merely cheap but exactly skipped, which is what makes "vanilla"
    // bit-exact rather than approximately right.
    if (uFalloff.z != 1.0) fade = pow(max(fade, 0.0), uFalloff.z);
    vec2 at = uAtten.x > 0.0 ? uAtten : vec2(1.0, 1.0);
    att = uFalloff.x * fade / (at.x + at.y * f2);
    // #142: the near-field core -- a compact-support bump reaching uHot.y of the range and
    // exactly nothing past it, so the body above owns the whole tail and the gain that was
    // calibrated against vanilla cannot drift. Peak is uHot.x / at.x, so gain + hot = 1 puts
    // the very centre at the engine's own peak.
    if (uHot.x > 0.0)
    {
        float t = max(1.0 - f2 / (uHot.y * uHot.y), 0.0);
        att += cxLod * uHot.x * fade * t * t / at.x;   // light.fullmodel: a faint light keeps the tail, not the core
    }
    att = max(att, 0.0);
    float invD = inversesqrt(max(dd, 1e-8));
    L = d * invD;
    ndlRaw = dot(N, L);
    // #139: the light is a SPHERE of radius uFalloff.y -- the same radius the attenuation
    // plateau above already models it with -- not a point. `s` is the sine of the half-angle it
    // subtends here, and wrapping N.L by it softens the terminator by exactly the amount the
    // source's own size would. The (1 + s) divide is what keeps this a redistribution rather
    // than free energy. Far away s -> 0 and the whole expression IS max(dot(N, L), 0.0) again,
    // so placed lights read exactly as they did; only a light held at arm's length differs --
    // which is the case where a punctual terminator cuts a low-poly facet in half.
    float s = uWrap <= 0.0 ? 0.0 : clamp(uWrap * uFalloff.y * invD, 0.0, 1.0);
    // max, not sum: at arm's length the sphere term is already at full strength and adding the
    // #143 floor on top of it would wrap past the point of looking lit at all.
    ndl = CxWrap(ndlRaw, max(s, uWrapN));
    return true;
}
// The unshadowed diffuse of one light: the wrapped Lambert term plus the stock subsurface leak.
vec3 CxPointDiffuse(vec3 c, float att, float ndlRaw, float ndl)
{
    // the leak is the second of light.fullmodel's three refinements (cxLod; 1 everywhere but the walk's tail)
    return c * (att * ndl) + c * (att * max(ndlRaw + uSss.x, 0.0) * uSss.y * cxLod);
}

// #674: a static light's record carries its cube slot PLUS this when it is in the lightmap pages
// (AlloyLights.LightStatic), and CX-56 its bake-set index PLUS ONE times CX_LIGHT_SET above that
// (AlloyLights.LightSetStride; 0 = no entry). Decode: w >= 128 is static, set = (w - 128) / 512 - 1,
// slot = w - 256 - 512 * (set + 1).
const float CX_LIGHT_STATIC = 256.0;
const int CX_LIGHT_SET = 512;
int CxLightSet(int w) { return w >= 128 ? (w - 128) / CX_LIGHT_SET - 1 : -1; }
int CxLightSlot(int w) { return w >= 128 ? w - 256 - CX_LIGHT_SET * (CxLightSet(w) + 1) : w; }
// CX-56: which of the surface's four mover shares is this light's -- its place in the record's pick, 4 = none.
int CxPickChan(int set, vec4 pick)
{
    if (set < 0) return 4;
    float f = float(set);
    return pick.x == f ? 0 : pick.y == f ? 1 : pick.z == f ? 2 : pick.w == f ? 3 : 4;
}
// CX-56: what a baked receiver owes a hot static light's movers. With a channel the bake's own answer --
// the share of the texel a mover in this light's path takes back (lmSub.y, a fraction); with none (not one
// of the four the surface's pick holds) the live estimate (lmSub.x, luminance). `soft` false = one tap per map.
void CxMoverOwe(int slot, int ch, float lum, float ndlRaw, bool soft, vec3 worldPos, vec3 N, float dfade,
                vec4 lmMover, float lmTexel, inout vec3 lmSub)
{
    if (slot < 0) return;
    if (ch < 4) lmSub.y += lmMover[ch] * dfade * uLocalFade[slot] * (1.0 - CxMoverVisQ(slot, worldPos, N, ndlRaw, soft));
    else if (lum > 0.0) lmSub.x += lum * dfade * (1.0 - CxLocalDynShadowQ(slot, worldPos, N, ndlRaw, soft, lmTexel));
}
// #674: lightmap.movers -- 1 = a HOT static light (a mover under it this frame) takes its mover's
// shadow back out of the baked term; 0 = baked receivers skip static lights outright; 2 = hot lights run the ray test
// and stop there (lightmap.movertaps 0, a diagnostic). Unset reads
// 0, the off position, which is what a stale content override degrades to.
uniform int uLmMovers;
// CX-188 step 4: a stage that defines CX_NO_HOT has the whole HOT branch compiled OUT of the walk below --
// no 16-sphere ray test, no dyn-page taps for a static light on a baked receiver, no hot tap cap, and `lmSub`
// comes back zero. A hot static light then behaves exactly as a cold one here, and the debt it owes is taken
// in screen space instead (cx_pairdebt). The DEFERRED stage defines it; the forward loop does not, so the
// A/B keeps today's path for the captures, the transparents and every /alloy viz mode.

#ifdef CX_LIGHT_LITE
/// CX-188 step 5: the walk the FORWARD consumers get -- grass cards and blades, water, particles, weather.
/// Same signature and same per-light arithmetic as the full walk below; what it leaves out is everything that
/// exists for a BAKED opaque receiver, which now goes deferred (ADR 0029).
/// <para>Unreachable here, so not compiled: every one of these calls arrives through CxClusteredLight or
/// CxClusteredDiffuse, whose `baked` is 0, so `inPages` is false and with it the hot-light branch, its
/// 16-sphere mover ray test, the CxMoverOwe debt and the mirror walk of baked statics -- `lmSub` is 0 by
/// construction. The glossy mirror CANDIDATE goes too: none of these programs uploads uLightOcc, so it reads
/// (0, 0) and CxGlossy is already false at every one of them, light.hidden or not.</para>
/// <para>The shadow is ONE hardware tap per shadowing light -- no blocker search, no Vogel disc, no
/// two-candidate cap to rank them with. That is the full walk's own `shadow.cap 0` path, which the lightbench
/// measured cheaper than two PCSS candidates, and it shadows EVERY light rather than the cap's strongest two,
/// so an evicted light here is shadowed where the full walk drew it lit. /shadow cubedist, /shadow floor, the
/// per-slot fade, light.relevance and the program's own uLocalCubes (/particles shadows 0, the grass shadow
/// knob) all still apply, term for term.</para>
/// <para>CX-188 budget: <c>shadow.perpixel</c> does NOT reach this walk. What makes the budget invisible on the
/// deferred path is the record dissolving its own edge (cx_shrec's CxShFade), and that needs the best score
/// that did NOT fit -- which means ranking every candidate before any of them is tapped, i.e. named candidate
/// slots, the registers step 5 exists to take back from these nine programs. A budget here without the
/// dissolve would be the hard shadow boundary the dissolve exists to prevent. The receivers are grass, water,
/// particles and raindrops, whose shadow is already the cheapest the pipeline has, and the measured cost the
/// budget was for was the DEFERRED opaque walk's taps, not these.</para>
vec3 CxClusteredLightLm(vec3 worldPos, vec3 N, vec3 V, float viewDepth, float onMover,
                        float rough, float specularity, float baked, float lmTexel, vec4 lmMover, vec4 lmPick, out vec3 spec, out vec3 lmSub)
{
    spec = vec3(0.0); lmSub = vec3(0.0);
    uint cl = CxCluster(viewDepth);
    uint n = CxListWalk(cl);
    vec3 acc = vec3(0.0);
    // shadow.cubedist, as above: a far fragment pays no cube fetch at all, the last quarter fades the cut out
    float dfade = (uLocalPcss.w > 0.0 ? 1.0 - smoothstep(uLocalPcss.w * 0.75, uLocalPcss.w, viewDepth) : 1.0) * cxRecvShadow;
    bool farSh = dfade <= 0.0;
    // light.relevance, the same reference the full walk takes: a fragment reads the build's floor candidates
    // alone, a VERTEX stage the whole list. The vertex case stays the whole list on purpose -- the candidate
    // set changes per froxel and a vertex may sit outside its own, so a candidates-only reference there would
    // band the blade colour on the cluster grid. The main loop below is the walk that got slimmer.
    // CX-188 budget: the pre-walk also feeds light.fullmodel, whose cut is a share of the same brightest
    float relMax = 0.0;
    if (uRelevance.x > 0.0 || uFullModel > 0.0)
    {
#ifdef CX_CLUSTER_FRAG
        if (uBounds > 0.5)
        {
            for (uint k = 0u, e = CxListLiveCand(cl); k < e; k++) relMax = max(relMax, CxLightImportance(CxListIdx(cl, k), worldPos));
            for (uint k = CxListLive(cl), e = CxListColdCand(cl); k < e; k++) relMax = max(relMax, CxLightImportance(CxListIdx(cl, k), worldPos));
        }
        else
#endif
        for (uint k = 0u, nAll = CxListN(cl); k < nAll; k++) relMax = max(relMax, CxLightImportance(CxListIdx(cl, k), worldPos));
    }
    for (uint k = 0u; k < n; k++)
    {
        uint li = CxListIdx(cl, k);
        // this light's share of the pixel's brightest: light.relevance's own quantity, and with it
        // light.fullmodel's. Continuous in position and in the light set, so neither cut has an edge to draw
        // and no light can POP as the ordering changes -- the share moves, it does not step.
        float share = uRelevance.x > 0.0 || uFullModel > 0.0 ? CxLightImportance(li, worldPos) / max(relMax, 1e-8) : 1.0;
        float relW = uRelevance.x > 0.0 ? smoothstep(uRelevance.x, uRelevance.y, share) : 1.0;
        if (relW <= 0.0) continue;
        // ...and the LIGHTING LOD: the full model above uFullModel of the brightest, fading in over the octave
        // below it, so a light drifting across the cut loses its core, spread and leak gradually.
        cxLod = uFullModel > 0.0 ? smoothstep(uFullModel * 0.5, uFullModel, share) : 1.0;
        vec4 pr = uLights[li * 2u];
        vec4 cs = uLights[li * 2u + 1u];
        int slot = CxLightSlot(int(cs.w));
        vec3 c, L; float att, f2, ndlRaw, ndl;
        if (!CxPointLight(pr, cs.rgb, worldPos, N, c, L, att, f2, ndlRaw, ndl)) continue;
        c *= relW;
        if (slot == -2) { acc += c * att; continue; }   // the ambient class: c*att, no N.L, no shadow (vanilla)
        if (ndl <= 0.0) continue;
        vec3 dif = c * (att * ndl);
        float spread = CxSrcSpread(sqrt(f2) * pr.w) * cxLod;   // the third refinement (light.fullmodel)
        float lobe = CxSpecLobe(N, V, L, rough, specularity, spread);
        vec3 spc = dif * CX_LOOP_SPEC(lobe, N, V, L, spread);
        vec3 unsh = CxPointDiffuse(c, att, ndlRaw, ndl);
        if (slot < 0 || farSh) { acc += unsh; spec += spc; continue; }
        // `false` is a literal, so the blocker search and both discs fold out of the inlined body
        float sh = mix(1.0, CxLocalShadowQ(slot, worldPos, N, ndlRaw, onMover, viewDepth, false), dfade);
        acc += unsh * mix(uLocalFloor, 1.0, sh);
        spec += spc * sh;   // floor on diffuse only: a floored highlight is a wrong highlight
    }
    cxLod = 1.0;   // the walk's last light must not shade a later caller of the shared body (cx_material's film)
    spec *= uDayDim;
    return acc * uDayDim;
}
#else
// Diffuse AND specular contribution of every light binned into this fragment's cluster, in ONE walk:
// both terms want the same per-light colour, attenuation, N.L and cube-shadow fetch, and running the
// cluster twice would pay for all four again. World space throughout: the Alloy pass has world
// positions and normals in hand, so there is no eye-space round trip.
// #674: `baked` > 0.5 = this fragment reads its static light from the lightmap pages, so every static
// light is skipped here -- except a HOT one under lightmap.movers, whose mover shadow (the dyn page
// alone, never the static half: the bake already holds that) accumulates into `lmSub` as the part of the
// baked term to take back: y a fraction of it (CX-56, the light's mover channel `lmMover`), x a luminance
// (a light with no channel: the live estimate, see CxMoverOwe).
vec3 CxClusteredLightLm(vec3 worldPos, vec3 N, vec3 V, float viewDepth, float onMover,
                        float rough, float specularity, float baked, float lmTexel, vec4 lmMover, vec4 lmPick, out vec3 spec, out vec3 lmSub)
{
    spec = vec3(0.0); lmSub = vec3(0.0);
    uint cl = CxCluster(viewDepth);
    bool glossy = CxGlossy(rough);
    float glossW = CxGlossW(rough);
    // a baked receiver with no mirror to take skips every cold static outright -- they trail the list
    uint n = baked > 0.5 && !glossy ? CxListLive(cl) : CxListWalk(cl);
    vec3 acc = vec3(0.0);
#if defined(CX_ABLATE) && CX_ABLATE <= 3
    // Ablation stage 2: the LIST SOURCE alone -- every length the walk reads and every entry of the walked
    // run indexed, with no light body behind it. At stage 3 it sits BESIDE the relevance pre-pass rather
    // than inside the loop the pre-pass feeds, so (stage 4 - stage 3) understates the light bodies by
    // exactly the amount stage 2 itself reports. Said here because the summary line cannot say it.
    float cxAbl = float(n) + float(CxListN(cl)) + float(CxListLive(cl))
                + float(CxListLiveCand(cl)) + float(CxListColdCand(cl));
    for (uint k = 0u; k < n; k++) cxAbl += float(CxListIdx(cl, k));
#if CX_ABLATE == 2
    spec = vec3(cxAbl); return vec3(cxAbl);
#endif
#endif
    // #336: /shadow cap -- the cube taps a fragment may pay, chosen PER FRAGMENT by its CONTRIBUTION there
    // (CxLightRank, the unshadowed diffuse's luminance): the capN lights lighting this fragment most strongly
    // get a cube fetch, the rest draw unshadowed. Per fragment, never per froxel -- a per-froxel choice put the
    // seam on the cluster grid, squares glued to the screen wherever four cube lights overlapped. The
    // candidates are kept sorted; an evicted one is added unshadowed.
    // EXPERIMENT: the candidates used to be five local arrays indexed by a runtime variable in a
    // while loop -- what a shader compiler spills to scratch memory. Two named slots, A the strongest.
    // CX-188 budget: this walk's TWO named slots are its structural ceiling, so shadow.perpixel can only ever
    // tighten the cap here -- the deferred record is where a budget of four lives.
    int capN = min(int(min(uLocalSoft.w, float(CX_CAP_MAX))), CxShadowBudget(CX_CAP_MAX));
    // shadow.cubedist: a far fragment pays no cube fetch at all (the old pipeline's distance cut), the
    // last quarter fades so the cut has no edge; uLocalPcss.w = 0 is every fragment
    float dfade = (uLocalPcss.w > 0.0 ? 1.0 - smoothstep(uLocalPcss.w * 0.75, uLocalPcss.w, viewDepth) : 1.0) * cxRecvShadow;
    bool farSh = dfade <= 0.0;
    float aLum = CX_RANK_NONE, bLum = CX_RANK_NONE; int aSlot = -1, bSlot = -1; float aNdl = 0.0, bNdl = 0.0;
    // the MIRROR candidate (light.hidden): the strongest lobe on a glossy fragment with a cube to tap.
    // ONE extra tap per fragment, after the loop, and only when the cap did not shadow it already --
    // a tap per lobe over the floor was the frame (rain = every ground fragment x every light).
    float mLum = CX_MIRROR_FLOOR; int mSlot = -1; float mNdl = 0.0; vec3 mSpc = vec3(0.0);
    vec3 R = reflect(-V, N);
    vec3 aLit = vec3(0.0), aSpc = vec3(0.0), bLit = vec3(0.0), bSpc = vec3(0.0);
#ifndef CX_NO_HOT
    // the same cap over HOT lights: the capN strongest get the soft dyn shadow, the rest one tap per map
    // (uncapped, 15 hot lights x 8 taps was the frame). CX-188 budget: hA/hB are the shared SCORE the cap
    // orders by and hALum/hBLum the luminance the debt is actually taken out of -- one number used to do both
    // jobs, which is why a change of ranking silently changed the debt.
    float hA = 0.0, hB = 0.0; int hASlot = -1, hBSlot = -1, hACh = 4, hBCh = 4; float hANdl = 0.0, hBNdl = 0.0;
    float hALum = 0.0, hBLum = 0.0;
#endif
    // light.relevance: the brightest light reaching this fragment, over the WHOLE list -- the live prefix moves
    // per froxel, the whole list only by lights that contribute exactly nothing here, so this is continuous.
    // A fragment reads the build's floor candidates alone: no other entry can be the brightest here.
    // CX-188 budget: the pre-walk also feeds light.fullmodel, whose cut is a share of the same brightest
    float relMax = 0.0;
    if (uRelevance.x > 0.0 || uFullModel > 0.0)
    {
#ifdef CX_CLUSTER_FRAG
        if (uBounds > 0.5)
        {
            for (uint k = 0u, e = CxListLiveCand(cl); k < e; k++) relMax = max(relMax, CxLightImportance(CxListIdx(cl, k), worldPos));
            for (uint k = CxListLive(cl), e = CxListColdCand(cl); k < e; k++) relMax = max(relMax, CxLightImportance(CxListIdx(cl, k), worldPos));
        }
        else
#endif
        for (uint k = 0u, nAll = CxListN(cl); k < nAll; k++) relMax = max(relMax, CxLightImportance(CxListIdx(cl, k), worldPos));
    }
#if defined(CX_ABLATE) && CX_ABLATE == 3
    // Ablation stage 3: the list source and the relevance / full-model PRE-PASS, and nothing of the loop.
    spec = vec3(relMax); return vec3(cxAbl + relMax);
#endif
    for (uint k = 0u; k < n; k++)
    {
        uint li = CxListIdx(cl, k);
        CX_WALK_ENTRY();
        // this light's share of the pixel's brightest: light.relevance's own quantity, and with it
        // light.fullmodel's. Continuous in position and in the light set, so neither cut has an edge to draw
        // and no light can POP as the ordering changes -- the share moves, it does not step.
        float share = uRelevance.x > 0.0 || uFullModel > 0.0 ? CxLightImportance(li, worldPos) / max(relMax, 1e-8) : 1.0;
        float relW = uRelevance.x > 0.0 ? smoothstep(uRelevance.x, uRelevance.y, share) : 1.0;
        if (relW <= 0.0) continue;
        // ...and the LIGHTING LOD: the full model above uFullModel of the brightest, fading in over the octave
        // below it, so a light drifting across the cut loses its core, spread and leak gradually.
        cxLod = uFullModel > 0.0 ? smoothstep(uFullModel * 0.5, uFullModel, share) : 1.0;
        vec4 pr = uLights[li * 2u];
        vec4 cs = uLights[li * 2u + 1u];
        // the record's spare w carries this light's cube slot (-1 = it casts no shadow this frame),
        // and the #674 static bit above it
        int w = int(cs.w);
        bool isStatic = w >= 128;
        int slot = CxLightSlot(w);
        int ch = CxPickChan(CxLightSet(w), lmPick);
        // a static light on a baked receiver is in the pages already: a cold one costs nothing here,
        // a HOT one (a mover under it) owes its mover's shadow and nothing else...
        bool inPages = isStatic && baked > 0.5;
        // ...except on a GLOSSY receiver (wet ground, metal), which owes each light's own mirror image:
        // the pages hold the diffuse, and the one lobe along their blended direction is a highlight of
        // a lamp that is nowhere. Spec only, occluded by the source's visibility, never a cube tap.
        bool mirror = inPages && glossy;
        // and only while a mover stands under it (a dyn page this frame): a static light with no
        // mover is in the pages and costs this fragment nothing -- with the check on the mover knob
        // alone, 74 baked-away lights of 92 still ran the point-light body per fragment (run 8)
#ifdef CX_NO_HOT
        // CX-188 step 4: no hot branch in this stage at all -- the debt dispatch owes what it owes.
        const bool hot = false;
#else
        bool hot = inPages && !(farSh || uLmMovers == 0 || slot < 0 || uLocalDynPage[slot] < 0.5);
#endif
        if (inPages && !hot && !mirror) continue;
        // ...and only a light the reflection vector points near: the lobe of a light outside that cone
        // is under the floor, and the point body is what 74 baked-away lights cost per fragment (run 8).
        // The cone widens with roughness: 20 deg at a puddle's 0.03, the hemisphere at the 0.5 ceiling.
        if (mirror && !hot && dot(R, normalize(pr.xyz - worldPos)) < 1.0 - 2.0 * rough) continue;
#ifndef CX_NO_HOT
        if (hot)
        {   cxHotViz.x += 1.0;
            // the light-to-fragment ray misses every mover sphere: nothing on the page can shadow here,
            // and the eight taps below are the opaque pass on a plaza (run 20: 15 hot lights, 7-8 ms)
            vec3 lp = uLocalLight[slot].xyz;
            vec3 dp = worldPos - lp; float dl = max(length(dp), 1e-4);
            bool near = false;
            for (int q = 0; q < CX_DYN_SPHERES && !near; q++)
            {
                vec4 ms = uDynMovers[slot * CX_DYN_SPHERES + q];
                if (ms.w < 0.0) break;   // a slot's spheres run from 0 up, unused ones after
                vec3 dc = ms.xyz - lp; float t = dot(dc, dp) / dl;
                float rr = ms.w;   // published with the shadow read's own reach (CasterPass.HotPad)
                near = t > -rr && t < dl + rr && dot(dc, dc) - t * t <= rr * rr;
            }
            if (!near) { if (!mirror) continue; hot = false; }
            else
            {
                cxHotViz.y += 1.0;
                if (uLmMovers == 2) { if (!mirror) continue; hot = false; }   // lightmap.movertaps 0: the ray test alone
            }
        }
#endif
        vec3 c, L; float att, f2, ndlRaw, ndl;
        if (!CxPointLight(pr, cs.rgb, worldPos, N, c, L, att, f2, ndlRaw, ndl)) continue;
        // CX-188 budget: every budget's score, taken HERE -- before relW scales the colour and before any
        // surface term joins it, which is what keeps the membership contour low-frequency (see CxLightRank).
        float rank = CxLightRank(att, c, f2);
        c *= relW;
        // slot -2 = ambient class: vanilla adds c*att to the ambient term, no N.L, no shadow
        if (slot == -2) { if (!inPages) acc += c * att; continue; }
        if (ndl <= 0.0) continue;   // facing away: no light, and no reason to pay for a cube fetch
        CX_WALK_LIT();
#ifndef CX_NO_HOT
        if (hot)
        {
            // lmSub.x = the luminance a mover occludes, over the static lights with a mover under
            // them; the caller takes it off the baked term's own luminance, so a mover's shadow from
            // one light darkens only that light's share. ponytail: where the static bake already
            // shadowed that light at this texel the subtraction overshoots (clamped at the caller);
            // the exact total needs every static light evaluated live, which was the opaque pass.
            float lum = dot(CxPointDiffuse(c, att, ndlRaw, ndl), vec3(0.2126, 0.7152, 0.0722));
            // CX-188 budget: this cap ORDERS by the shared score and still CARRIES `lum`, the luminance the
            // debt is taken out of -- two different jobs that used to be one number. With the budget off the
            // score is -f2, which would re-order this cap, so off keeps its own pre-budget quantity and the
            // frame is bit-identical there.
            float hr = uShBudget > 0.0 ? rank : lum;
            int eSlot = slot, eCh = ch; float eNdl = ndlRaw, eLum = lum;   // the light this one displaces: itself by default; hard tap, as above
            if (capN > 0 && hr > hA)
            {
                if (capN >= 2) { eSlot = hBSlot; eCh = hBCh; eNdl = hBNdl; eLum = hBLum;
                                 hB = hA; hBSlot = hASlot; hBCh = hACh; hBNdl = hANdl; hBLum = hALum; }
                else { eSlot = hASlot; eCh = hACh; eNdl = hANdl; eLum = hALum; }
                hA = hr; hASlot = slot; hACh = ch; hANdl = ndlRaw; hALum = lum;
            }
            else if (capN >= 2 && hr > hB)
            {
                eSlot = hBSlot; eCh = hBCh; eNdl = hBNdl; eLum = hBLum;
                hB = hr; hBSlot = slot; hBCh = ch; hBNdl = ndlRaw; hBLum = lum;
            }
            CxMoverOwe(eSlot, eCh, eLum, eNdl, false, worldPos, N, dfade, lmMover, lmTexel, lmSub);
            if (!mirror) continue;
        }
#endif
        vec3 dif = c * (att * ndl);
        // the source's own size, from what the curve already computed: dd = f2 * R^2, so the distance is
        // sqrt(f2) * R and no second length() is paid for it
        float spread = CxSrcSpread(sqrt(f2) * pr.w) * cxLod;   // the third refinement (light.fullmodel)
        float lobe = CxSpecLobe(N, V, L, rough, specularity, spread);
        vec3 spc = dif * CX_LOOP_SPEC(lobe, N, V, L, spread);
        if (mirror) spc *= glossW;
        // a glossy surface's lobe is the mirror image of the source, and the source must be in view of
        // the FRAGMENT: a lamp on a terrace mirrors in the street from where the ground first sees over
        // the parapet, not from the wall's foot. That is the light's own shadow, so the strongest lobe
        // takes its cube tap whether or not the cap below would have given it one. A rough lobe is
        // untouched: too broad to be an image of anything. (light.hidden)
        if (glossy && slot >= 0 && !farSh)
        {
            float l = dot(dif, vec3(0.2126, 0.7152, 0.0722)) * lobe;   // engine units: the floor is tuned in them
            if (l > mLum) { mLum = l; mSlot = slot; mNdl = ndlRaw; mSpc = spc; }
        }
        if (mirror) { spec += spc; continue; }
        // ponytail: a light behind the surface (ndl <= 0, continued above) skips its subsurface leak
        // rather than being fetched unshadowed; stock has no shadow to lose there, Alloy does
        vec3 unsh = CxPointDiffuse(c, att, ndlRaw, ndl);
        if (slot < 0 || farSh) { acc += unsh; spec += spc; continue; }
#if defined(CX_ABLATE) && CX_ABLATE == 4
        // Ablation stage 4: the light BODIES alone -- curve, N.L, lobe, subsurface leak -- with no shadow of
        // any kind: no record fetch, no hardware tap, and (below) no mirror tap.
        acc += unsh; spec += spc; continue;
#endif
#ifdef CX_SHADOW_SOURCE
        {
            // CX-188 step 3: the pixel's visibility was chosen and filtered once, before this walk. Matched by
            // SLOT, so a light the record does not hold is one the record's own budget ranked out.
            // CX-188 budget: and a light past the budget lights the pixel UNSHADOWED. That is not a step: the
            // record already faded the lane holding the budget's WEAKEST light toward unshadowed as its score
            // fell toward the best score that did not fit (CxShFade), so the light leaving the record had
            // dissolved before it left. Membership is the RECORD's alone -- this walk never re-ranks, which is
            // what keeps the half-resolution record's choice and the full-resolution walk's from disagreeing.
            // The hardware tap is compiled only into CX_SH_INLOOP, the variant the "every light" escapes run
            // (shadow.cap 0, shadow.perpixel 0, or no record this frame), where dropping the lights past the
            // record would be a hole rather than a budget. The shipped program has no tap in this loop at all:
            // a pixel the record missed fills its own before the walk (cx_deferred_cs), because the tap's mere
            // presence here cost the pass 2.7 ms while it ran on ~1% of pixels.
            bool have; float v = CxShadowFetch(slot, have);
#ifdef CX_SH_INLOOP
            if (!have && (cxShMiss > 0.0 || uShBudget <= 0.0))
            {
                CX_WALK_TAP();
                v = CxLocalShadowQ(slot, worldPos, N, ndlRaw, onMover, viewDepth, false);
            }
#endif
            float sh = mix(1.0, v, dfade);
            acc += unsh * mix(uLocalFloor, 1.0, sh); spec += spc * sh;
            if (mSlot == slot) mSlot = -1;   // shadowed here: no mirror tap owed
            continue;
        }
#else
        // cap 0 = every light, at the hardware tap: a soft shadow INSIDE the loop is inlined per light
        // and its registers are the loop's (lightbench: -23% on the lit stage with only hard taps here)
        if (capN <= 0)
        {
            float sh = mix(1.0, CxLocalShadowQ(slot, worldPos, N, ndlRaw, onMover, viewDepth, false), dfade);
            acc += unsh * mix(uLocalFloor, 1.0, sh); spec += spc * sh;   // floor on diffuse only: a floored highlight is a wrong highlight
            if (mSlot == slot) mSlot = -1;   // shadowed here: no mirror tap owed
            continue;
        }
        // CX-188 budget: ranked by CxLightRank, the one score the record and the hot cap also order by -- with
        // the budget off that score IS -f2, so this chain is the pre-budget one, comparison for comparison.
        if (rank > aLum)
        {   // new strongest: A moves to B (capN 2) or is evicted unshadowed (capN 1); old B is evicted
            if (capN >= 2) { if (bSlot >= 0) { acc += bLit; spec += bSpc; }
                             bLum = aLum; bSlot = aSlot; bNdl = aNdl; bLit = aLit; bSpc = aSpc; }
            else if (aSlot >= 0) { acc += aLit; spec += aSpc; }
            aLum = rank; aSlot = slot; aNdl = ndlRaw; aLit = unsh; aSpc = spc;
        }
        else if (capN >= 2 && rank > bLum)
        {
            if (bSlot >= 0) { acc += bLit; spec += bSpc; }
            bLum = rank; bSlot = slot; bNdl = ndlRaw; bLit = unsh; bSpc = spc;
        }
        else { acc += unsh; spec += spc; }
#endif
    }
#ifndef CX_SHADOW_SOURCE
    if (aSlot >= 0)
    {
        float sh = mix(1.0, CxLocalShadow(aSlot, worldPos, N, aNdl, onMover, viewDepth), dfade);
        acc += aLit * mix(uLocalFloor, 1.0, sh); spec += aSpc * sh;
    }
    if (bSlot >= 0)
    {
        float sh = mix(1.0, CxLocalShadow(bSlot, worldPos, N, bNdl, onMover, viewDepth), dfade);
        acc += bLit * mix(uLocalFloor, 1.0, sh); spec += bSpc * sh;
    }
#endif
#ifndef CX_NO_HOT
    CxMoverOwe(hASlot, hACh, hALum, hANdl, true, worldPos, N, dfade, lmMover, lmTexel, lmSub);
    CxMoverOwe(hBSlot, hBCh, hBLum, hBNdl, true, worldPos, N, dfade, lmMover, lmTexel, lmSub);
#endif
    // the mirror tap: the strongest lobe's shadow, taken back out of the unshadowed sum it went into. The
    // hardware tap alone -- a highlight has no penumbra worth a blocker search
#if !defined(CX_ABLATE) || CX_ABLATE >= 8
    if (mSlot >= 0 && mSlot != aSlot && mSlot != bSlot)
        spec -= mSpc * (1.0 - mix(1.0, CxLocalShadowQ(mSlot, worldPos, N, mNdl, onMover, viewDepth, false), dfade));
#endif
    // a baked shadow keeps uLocalFloor of its light (cx_bake_f), so a mover's takes back only the rest
    // (the channel shares in y were baked without the floor already)
    lmSub.x *= 1.0 - uLocalFloor;
    // the daylight ratio; lmSub stays in the undimmed units the caller's baked term is compared in
    cxLod = 1.0;   // the walk's last light must not shade a later caller of the shared body (cx_material's film)
    spec *= uDayDim;
    return acc * uDayDim;
}
#endif
// The pre-#674 signature: an unbaked receiver, every light live.
vec3 CxClusteredLight(vec3 worldPos, vec3 N, vec3 V, float viewDepth, float onMover,
                        float rough, float specularity, out vec3 spec)
{
    vec3 sub;
    return CxClusteredLightLm(worldPos, N, V, viewDepth, onMover, rough, specularity, 0.0, 0.0, vec4(0.0), vec4(-1.0), spec, sub);
}

// The diffuse half alone, for <c>/alloy viz 1</c> and anything that has no material in hand.
vec3 CxClusteredDiffuse(vec3 worldPos, vec3 N, float viewDepth)
{
    vec3 spec;
    return CxClusteredLight(worldPos, N, N, viewDepth, 0.0, 1.0, 0.0, spec);
}

/// Darkest local-cube term over this fragment's cluster -- white lit, black fully occluded, and
/// exactly 1.0 where no light in reach carries a cube. <c>/alloy viz 5</c>. The composite cannot answer
/// "is the cube occluding anything?" because a lit neighbour washes the term out; this can.
float CxLocalShadowViz(vec3 worldPos, vec3 N, float viewDepth)
{
    uint cl = CxCluster(viewDepth);
    uint n = CxClusterN(cl);
    uint base = cl * CX_CLUSTER_CAP;
    float vis = 1.0;
    for (uint k = 0u; k < n; k++)
    {
        uint li = uClIdx[base + k];
        vec4 pr = uLights[li * 2u];
        int slot = CxLightSlot(int(uLights[li * 2u + 1u].w));
        if (slot < 0) continue;
        vec3 d = pr.xyz - worldPos;
        if (dot(d, d) > pr.w * pr.w) continue;
        vis = min(vis, CxLocalShadow(slot, worldPos, N, dot(N, normalize(d)), 0.0, viewDepth));
    }
    return vis;
}
