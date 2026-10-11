
// CX-188: the SYNTHESIS half, from here to CxSpecColor, reads the lit fragment stage's own slot-mask and
// floor constants (cx_ownlit_head). The deferred compute pass (cx_deferred_cs) composes this file for the
// lobes and CxFilmStatics alone -- its material came off the G-buffer, already synthesised -- so it defines
// CX_NO_SYNTH and compiles none of it. Every fragment stage leaves it undefined and gets what it always had.
#ifndef CX_NO_SYNTH
/// A material that authors nothing -- no Specularity, no spec or roughness map, i.e. typically no .mtr.
/// Its lobe is stock's, but held at F0 at every angle: legacy content was never made for a grazing Fresnel rise.
bool CxBareMaterial(vec3 declared, int slots)
{
    return (slots & (CX_SLOT_SPEC | CX_SLOT_ROUGH)) == 0 && declared.x <= 0.0;
}

/// (Specularity, Roughness, Metallicness) -- declared wins, then the map, then the generated floor.
vec3 CxSynthPbr(vec3 declared, int slots, float envLevel, float specTexel, float roughTexel)
{
    float envShiny = min(envLevel * CX_ENV_SPEC_SCALE, 1.0);
    float envRough = min(envLevel * CX_ENV_ROUGH_SCALE, 1.0);
    bool sm = (slots & CX_SLOT_SPEC) != 0;
    float spec = declared.x;
    if (spec <= 0.0)
    {
        if (sm) spec = specTexel;
        // #121: env map + no specular map = legacy "shiny" content, and this is the engine's own
        // translation of it (inc_material.shd, "mainly for the sake of enabling specularity on
        // legacy content").
        else if (envShiny > 0.0) spec = mix(CX_SPEC_MIN, CX_ENV_SPEC_MAX, envShiny);
        else spec = CX_SPEC_MIN;
    }
    float rough = declared.y;
    if (rough <= 0.0)
    {
        // A ROUGHNESS MAP IS DECLARED CONTENT: it is taken whole, and the env curve below never
        // touches it -- stock applies that curve only where the roughness was generated
        // (inc_material.shd puts the min() inside the specular-map branch, not after it).
        if ((slots & CX_SLOT_ROUGH) != 0) rough = roughTexel;
        else
        {
            // a specular map with no roughness map is pre-PBR content: translate rather than trust it
            rough = sm ? mix(CX_ROUGH_MIN, CX_ROUGH_MAX, (1.0 - spec) * (1.0 - spec))
                       : CX_ROUGH_MAX;
            // ...and the env map drives it toward the same floor, on TOP of that translation. With
            // no maps at all the min() is stock's bare assignment: the mix never exceeds the max.
            if (envLevel > 0.0)
                rough = min(rough, mix(CX_ROUGH_MAX, CX_ROUGH_MIN, envRough));
        }
    }
    float metal = declared.z;
    // the old gen keys this curve on whether a SPECULAR map is present -- 3.0/0.2 with one, 10.0/0.04
    // without (fMetallicnessModifier * fGeneratedMetallicnessOffset). Collapsing them to one branch
    // would generate visibly different metal for content that declares Specularity but ships no map.
    if (metal <= 0.0)
        metal = clamp((sm ? CX_METAL_SCALE_MAP : CX_METAL_SCALE_BARE) * spec
                      - (sm ? CX_METAL_BIAS_MAP : CX_METAL_BIAS_BARE), 0.0, 1.0);
    return vec3(spec, rough, metal);
}

/// The material's specular colour: CustomSpecularColor where declared (w = the CPU-side "not black"
/// test, which is stock's own sentinel), else generated off the albedo.
vec3 CxSpecColor(vec4 declared, vec3 base, float metal, int slots)
{
    if (declared.w > 0.0) return CxLinear(declared.rgb);
    // stock takes plain white as the non-metal base once a height map is bound -- the peak-channel
    // rescue below is for flat content, and a height-mapped material already carries its crevices
    if ((slots & CX_SLOT_HEIGHT) != 0) return mix(vec3(1.0), base, metal);
    // non-metals should be white, but dark diffuse texels stand in for crevices, so the peak channel
    // scales it -- inc_material.shd's own reasoning, in its own words
    return mix(vec3(min(1.0, max(base.r, max(base.g, base.b)) / CX_MID_GREY)), base, metal);
}
#endif

// ---- ADR 0026: the modern material model ----------------------------------------------------------
// Every roughness below is in the ENGINE's units (alpha = rough), whatever the content declared it in:
// the translation happens once, at the material, so the loop, the lightmap, the glossy tests and every
// weather splice keep reading one convention.

/// The narrowest lobe a declared roughness may ask for. GGX peaks at 1/alpha^2 times the irradiance, and
/// the colour target is RGBA16F: 0.02 keeps a sunlit mirror at 2500, far from the 65504 ceiling and from
/// the bloom fireflies of an infinite peak. In perceptual units that is ~0.14: anything declared below it
/// draws as it.
const float CX_ALPHA_MIN = 0.02;
/// glTF's clearcoat and sheen roughness defaults are 0 -- a perfect mirror coat, a knife-edge sheen -- which
/// is right for a renderer with a real prefilter and wrong for a 128^2 probe; these are the defaults an
/// .mtr that names the layer but not its roughness gets (perceptual units).
const float CX_COAT_ROUGH_DEFAULT = 0.15, CX_SHEEN_ROUGH_DEFAULT = 0.5;

/// Does the material declare the glTF vocabulary (CxMetallic, CxRoughness, or a CxOrm map that is bound)?
/// `m0` is the record's ext0 (-1 = not declared), `ormOn` 1 when texture11 carries a packed ORM map.
bool CxPhysical(vec4 m0, float ormOn) { return m0.x >= 0.0 || m0.y >= 0.0 || ormOn > 0.5; }

/// ADR 0026: the glTF metal/rough view of a physical material, returned as (Specularity, Roughness,
/// Metallicness) in the ENGINE's units and the specular colour beside it. glTF's own rules: the factor
/// multiplies the map, a factor with no map stands alone, a map with no factor is taken whole. Two
/// departures, both toward the legacy view: no metallic factor and no map is a dielectric (glTF's default
/// factor of 1 would turn "declares only CxRoughness" into chrome), and no roughness at all keeps the
/// roughness the stock parameters already gave it. Perceptual roughness squares into the engine's alpha.
vec3 CxPhysicalPbr(vec4 m0, float ormOn, vec3 orm, vec3 legacy, vec3 base, out vec3 specColor)
{
    bool orms = ormOn > 0.5;
    float metal = clamp((m0.x >= 0.0 ? m0.x : orms ? 1.0 : 0.0) * (orms ? orm.b : 1.0), 0.0, 1.0);
    float rough = legacy.y;
    if (m0.y >= 0.0 || orms)
    {
        float p = clamp((m0.y >= 0.0 ? m0.y : 1.0) * (orms ? orm.g : 1.0), 0.0, 1.0);
        rough = max(p * p, CX_ALPHA_MIN);
    }
    // the loop's Fresnel is scalar (mix(spec, 1, Fc)) with the colour outside it: F0's peak channel is the
    // scalar, its hue the colour
    vec3 f0 = mix(vec3(CX_DIELECTRIC_F0), base, metal);
    float spec = max(max(f0.r, f0.g), max(f0.b, 1e-4));
    specColor = f0 / spec;
    return vec3(spec, rough, metal);
}

// CxEnvBrdf (the split-sum DFG fit) lives in cx_ssr.glsl: the grass stages weigh their reflection with it too.

/// Multiple-scattering compensation (Fdez-Aguera 2019): a single-scatter GGX lobe loses the light that
/// bounces between microfacets, so rough metal reads dark. 1 + F0 (1/Ess - 1), Ess the lobe's directional
/// albedo at F0 = 1. Per channel: a coloured metal loses the most in the channel it reflects most.
vec3 CxMultiScatter(vec3 f0, float NoV, float perceptual)
{
    vec2 ab = CxEnvBrdf(NoV, perceptual);
    return 1.0 + f0 * (1.0 / max(ab.x + ab.y, 0.05) - 1.0);
}

/// Specular anti-aliasing (Kaplanyan & Hoffman 2016, Tokuyoshi's bounded form): the alpha^2 a
/// highlight must widen by, from the screen-space variance of the surface normal, so a lobe narrower
/// than a pixel does not crawl. The geometric normal, and taken ahead of every discard: a cutout kills
/// quad lanes, and a derivative across a killed lane is undefined.
/// CX-188: a screen derivative, so it belongs to a fragment stage -- the deferred pass reads the widened
/// roughness off the G-buffer instead (CX_NO_SYNTH).
#ifndef CX_NO_SYNTH
float CxSpecAAKernel(vec3 n)
{
    vec3 dx = dFdx(n), dy = dFdy(n);
    return min(0.5 * (dot(dx, dx) + dot(dy, dy)), 0.18);
}
#endif

/// Lagarde's specular occlusion: how much of the ambient reflection an occlusion value `ao` leaves, which
/// is less than `ao` itself for a glossy lobe looking into a crevice and more for a rough one.
float CxSpecOcclusion(float NoV, float ao, float rough)
{
    return clamp(pow(NoV + ao, exp2(-16.0 * rough - 1.0)) - 1.0 + ao, 0.0, 1.0);
}

// CxProbeSh (the SH9 block) lives in cx_ssr.glsl beside CxProbeFetch, which reads its origin; cx_ownlit_head includes it.

/// The probe's directional shape over the area ambient: the SH irradiance toward N over the mean luminance of
/// N and -N, in [0, 2] by luminance. Normalised against its own opposite, so the authored ambient keeps its
/// level and gains a direction and a colour -- sky above, ground below, the lit wall beside. The colour is
/// capped at 2x per channel: a saturated capture must tint the ambient, not multiply one channel tenfold.
vec3 CxAmbientShape(vec3 N)
{
    vec3 even = uProbeSh[0].rgb + uProbeSh[4].rgb * (N.x * N.y) + uProbeSh[5].rgb * (N.y * N.z)
              + uProbeSh[6].rgb * (3.0 * N.z * N.z - 1.0) + uProbeSh[7].rgb * (N.x * N.z)
              + uProbeSh[8].rgb * (N.x * N.x - N.y * N.y);
    vec3 odd = uProbeSh[1].rgb * N.y + uProbeSh[2].rgb * N.z + uProbeSh[3].rgb * N.x;
    vec3 a = max(even + odd, vec3(0.0)), b = max(even - odd, vec3(0.0));
    const vec3 lum = vec3(0.2126, 0.7152, 0.0722);
    float la = dot(a, lum);
    return min(a / max(la, 1e-4), vec3(2.0)) * (2.0 * la / max(la + dot(b, lum), 1e-4));
}

/// Per-fragment material state CxLoopSpec reads, set by the lit main before the first light.
vec3 cxSpecScale;               // specColor (engine model only) x view-side normalisation x multiscatter x coat transmission
vec3 cxF0; float cxSpec;        // modern model: per-channel F0 and the scalar CxSpecLobe's Fresnel was built on
float cxCoat, cxCoatRough;      // clear-coat weight and alpha; 0 weight = no coat
vec3 cxSheen; float cxSheenRough;   // sheen colour (linear) and alpha; 0 alpha = no sheen

/// The clear coat's lobe per unit irradiance: GGX, Kelemen's visibility, a fixed 0.04 dielectric
/// (KHR_materials_clearcoat). Pi-less, like every lobe in the lit stage. <paramref name="spread"/> is the
/// sphere-light widening (<c>CxSrcSpread</c>), in the denominator alone so the numerator carries Karis'
/// energy factor -- a water film's alpha is near the CX_ALPHA_MIN floor, and this coat is where an
/// unbounded peak showed up as a flare.
/// The most a coat's highlight may exceed the light's own irradiance, as a soft (Reinhard) ceiling: a near-mirror
/// lobe peaks at 1 / alpha^2, so a lamp that barely lights the street mirrored in a puddle as a white road.
/// The ceiling keeps the lobe's shape and ties the reflection to how much the light shows on the ground.
const float CX_COAT_LOBE_MAX = 2.0;
float CxCoatLobe(vec3 N, vec3 V, vec3 L, float a, float spread)
{
    vec3 H = normalize(V + L);
    float NoH = clamp(dot(N, H), 0.0, 1.0), LoH = clamp(dot(L, H), 0.0, 1.0);
    float ap = min(a + spread, 1.0);
    float a2 = a * a, den = 1.0 - NoH * NoH * (1.0 - ap * ap);
    float lobe = a2 / max(den * den, 1e-8) * (0.25 / max(LoH * LoH, 1e-4)) * mix(CX_DIELECTRIC_F0, 1.0, CxFresnel(LoH));
    return lobe / (1.0 + lobe / CX_COAT_LOBE_MAX);
}
float CxCoatLobe(vec3 N, vec3 V, vec3 L, float a) { return CxCoatLobe(N, V, L, a, 0.0); }

/// The radiance ceiling one light's water-film highlight may reach, in the linear units the colour target
/// holds. Sunlit metal peaks well under it; a near-mirror lobe on a lamp at arm's length from the ground
/// does not, and that is the "solar flare".
const float CX_FILM_LIGHT_MAX = 8.0;

/// The sheen's lobe per unit irradiance: Estevez & Kulla's "Charlie" distribution with Neubelt's
/// visibility (KHR_materials_sheen) -- the retro-reflective rim of cloth and velvet. Pi-less.
float CxSheenLobe(vec3 N, vec3 V, vec3 L, float a)
{
    vec3 H = normalize(V + L);
    float NoH = clamp(dot(N, H), 0.0, 1.0), NoL = clamp(dot(N, L), 0.0, 1.0), NoV = clamp(dot(N, V), 1e-4, 1.0);
    float inv = 1.0 / a;
    float d = (2.0 + inv) * pow(max(1.0 - NoH * NoH, 1e-4), 0.5 * inv) * 0.5;
    return d / (4.0 * max(NoL + NoV - NoL * NoV, 1e-4));
}

/// R4: the highlights a water film mirrors from the BAKED static lights the main walk no longer reaches.
/// The cluster build ranks the few brightest per froxel and permutes them to the front of the cold tail
/// (cx_lightbuild), so this is a walk of two to four lights, each on the COAT's lobe alone -- the pages
/// already hold what the base owes them -- and only the strongest pays one cube tap for its own
/// visibility. Never reached on a dry frame: the count is 0 and the caller is gated on the film.
/// Lives here, past CxCoatLobe, rather than in cx_lightfrag, which is composed before this file.
vec3 CxFilmStatics(vec3 worldPos, vec3 N, vec3 V, float viewDepth, float coat, float coatRough, float onMover)
{
    uint cl = CxCluster(viewDepth);
    uint live = CxListLive(cl);
    uint last = min(live + CxListRefl(cl), CX_CLUSTER_CAP);
    vec3 acc = vec3(0.0), bSpc = vec3(0.0);
    float bLum = 0.0, bNdl = 0.0;
    int bSlot = -1;
    for (uint k = live; k < last; k++)
    {
        uint li = CxListIdx(cl, k);
        vec4 pr = uLights[li * 2u];
        vec4 cs = uLights[li * 2u + 1u];
        vec3 c, L; float att, f2, ndlRaw, ndl;
        if (!CxPointLight(pr, cs.rgb, worldPos, N, c, L, att, f2, ndlRaw, ndl)) continue;
        if (ndl <= 0.0) continue;
        vec3 spc = c * (att * ndl) * (coat * CxCoatLobe(N, V, L, coatRough, CxSrcSpread(sqrt(f2) * pr.w)));
        // the firefly clamp: the sphere normalisation above bounds the lobe by the source's own size, and
        // this bounds what is left, per light. A lamp set into the paving of a plaza is a real value no
        // bloom or tonemap in the chain is built for, and one badly placed one must not own the frame.
        spc = min(spc, vec3(CX_FILM_LIGHT_MAX));
        acc += spc;
        float lum = dot(spc, vec3(0.2126, 0.7152, 0.0722));
        int slot = CxLightSlot(int(cs.w));
        if (slot >= 0 && lum > bLum) { bLum = lum; bSlot = slot; bNdl = ndlRaw; bSpc = spc; }
    }
    if (bSlot >= 0)
        acc -= bSpc * (1.0 - CxLocalShadowQ(bSlot, worldPos, N, bNdl, onMover, viewDepth, false));
    return acc * uDayDim;
}

/// The lobe without the coat's: what the lightmap's dominant direction owes a film, whose coat already took
/// each baked light's own image in CxFilmStatics -- a near-mirror coat lobe on a BLENDED direction points at
/// no lamp, and drew the reflection as an arc that ended wherever the baked light did.
vec3 CxLoopSpecBase(float lobe, vec3 N, vec3 V, vec3 L)
{
    vec3 s = lobe * cxSpecScale;
    // Schlick per channel, F0 + (1 - F0) Fc, so coloured specular whitens at grazing. The lobe carries the
    // engine's scalar mix(spec, 1, Fc) with the hue outside it, which never leaves the specular colour.
    if (uMaterial.x > 0.5)
    {
        float fc = CxFresnel(clamp(dot(normalize(V + L), V), 0.0, 1.0)) * cxFresnel;
        s *= mix(cxF0, vec3(1.0), fc) / max(mix(cxSpec, 1.0, fc), 1e-4);
    }
    // the sheen is a broad retro-reflective rim by construction: a source's size cannot widen it usefully
    if (cxSheenRough > 0.0) s += cxSheen * CxSheenLobe(N, V, L, cxSheenRough);
    return s;
}

vec3 CxLoopSpec(float lobe, vec3 N, vec3 V, vec3 L, float spread)
{
    vec3 s = CxLoopSpecBase(lobe, N, V, L);
    if (cxCoat > 0.0) s += vec3(cxCoat * CxCoatLobe(N, V, L, cxCoatRough, spread));
    return s;
}

/// The punctual form, for the terms that are not point lights: the sun and the lightmap's dominant
/// direction. Declared past the body it forwards to, as GLSL requires.
vec3 CxLoopSpec(float lobe, vec3 N, vec3 V, vec3 L) { return CxLoopSpec(lobe, N, V, L, 0.0); }
