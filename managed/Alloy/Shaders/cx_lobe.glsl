// The engine's specular lobe (inc_lighting.shd at the ship quality mode) as pure functions of the surface, the
// view and the light: every lit stage's lobe, and the GUI twin's. The includer declares uMaterial (light.brdf).
/// The narrowest lobe a declared roughness may ask for. GGX peaks at 1/alpha^2 times the irradiance, and
/// the colour target is RGBA16F: 0.02 keeps a sunlit mirror at 2500, far from the 65504 ceiling and from
/// the bloom fireflies of an infinite peak. In perceptual units that is ~0.14: anything declared below it
/// draws as it.
const float CX_ALPHA_MIN = 0.02;

// Schlick's Fresnel, the pow-5 form the ship quality mode takes (inc_lighting.shd:247-262 GetFresnel,
// SHADER_QUALITY_MODE > 1; the lower modes use pow 4).
float CxFresnel(float c)
{
    float f = 1.0 - c;
    float sq = f * f;
    return clamp(f * sq * sq, 0.0, 1.0);
}

// The Schlick-Smith k. The engine takes k = alpha, which over-shadows a rough lobe at grazing angles;
// the modern model takes Karis' k = alpha / 2 (the Schlick fit to Smith-GGX). Both are separable into
// a light half and a view half, which is what keeps the per-light / once-per-fragment split below.
/// 1 = the modern model's grazing forms (Karis' k, per-channel whitening); 0 = the engine's, which the lit main
/// picks for every non-glTF material: content made for the stock renderer is tuned against its grazing.
float cxModern = 1.0;
float CxSmithK(float rough) { return uMaterial.x > 0.5 && cxModern > 0.5 ? rough * 0.5 : rough; }

// #120, corrected by the live A/B on #121: the specular lobe every lit term goes through, as the
// ENGINE computes it (inc_lighting.shd:174-284 at the ship quality mode -- inc_config.shd's
// SPECULAR_DISTRIBUTION_MODEL 1 = GGX, SPECULAR_GEOMETRIC_SHADOWING 2 = Schlick, SPECULAR_FRESNEL 1
// = full). This replaced a textbook GGX, which differed in the one place that decides how a surface
// reads: it remapped alpha = rough^2, while the engine feeds fRoughness_sq = rough^2 as alpha^2 --
// alpha = rough. Every authored and synthesised roughness in the content is tuned for the engine's
// convention, so the remap shaded a 0.24-roughness stone as a 0.06 one: a far tighter, far brighter
// highlight on every part carrying a specular map, which is nearly every authored material.
// <para>Two halves, split the way the engine splits them: this per-LIGHT one, and the view-side
// normalisation in CxSpecBase, which is common to every light and applied once (the engine's
// fSpecularBaseModifier). fSpecularity rides in through the Fresnel term and must NOT be multiplied
// in again -- inc_lighting.shd:554-557 scales by fEnvironmentSpecularity only when SPECULAR_FRESNEL != 1.</para>
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

// The half that does not vary per light: alpha^2/4 and the view-side shadowing term. N.V floors at 0, as
// inc_lighting.shd:436-441 leaves it; the shadowing term's k keeps the divisor off zero.
// Stock bends a shading normal that faces away from the eye into the view plane before any light reads it;
// this floors the dot instead, which keeps the bend's own grazing spike out of the lobe.
float CxSpecBase(float NdotV, float rough)
{
    return rough * rough * 0.25 / mix(max(NdotV, 0.0), 1.0, CxSmithK(rough));
}
